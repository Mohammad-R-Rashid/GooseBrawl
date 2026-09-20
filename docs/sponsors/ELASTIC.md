# Elastic: Find the Signal (Best Use of Elasticsearch)

**Claim:** Elasticsearch is the goose's **context layer**. GOOSED. is an AR game whose antagonist holds a grudge, and the
grudge is now built from genuinely messy, real-world data: what people **shout** at the goose (speech, any language),
a **5 Hz sensor stream** from the phone (where the player and the goose are, how fast they move, frame time, thermal
state), and every line and round. An agent reads that data, decides what to retrieve, and **acts**: the goose's words
change, an Agent Builder agent writes the case file, and an Elastic Workflow files perf regressions and flags players
on its own, every five minutes, with nobody watching.

Nothing gameplay-relevant depends on it, and nothing on the phone ever waits for it.

## Where it lives

| File | Role |
|---|---|
| `backend/goose-brain/src/tools/elastic.ts` | the client: `es()` (every call a Sentry span), `bulk`, `esql` (named params), `similarShouts` (hybrid retriever), `converse` (Agent Builder), document shapes |
| `backend/goose-brain/src/intel.ts` | the case file: `fastIntel` (7 ES|QL queries in parallel), `findEcho` (hybrid search at yell time), `deepIntel` (the agent), `intelForPrompt` |
| `backend/goose-brain/src/telemetry.ts` | `POST /telemetry`: compact rows from the phone -> documents -> `_bulk` in `waitUntil` |
| `backend/goose-brain/src/alerts.ts` | `POST /elastic/alert`: the Workflow's findings become Sentry issues |
| `backend/goose-brain/src/tools/workersai.ts`, `src/writer.ts` | no OpenAI key on this account: Whisper hears the shout (multilingual), Llama 3.3 70B writes the line from the case file |
| `backend/goose-brain/src/agent.ts` | session: case file in parallel with naming; yell: transcript -> echo -> index; caught/outlasted: run document; every line indexed |
| `backend/goose-brain/scripts/elastic-setup.ts` | indices, `semantic_text` on the project's Jina endpoint, the time-series data stream (falls back to a plain data stream) |
| `backend/goose-brain/scripts/elastic-seed.ts` | a seeded hackathon day (60 players, 44 shouts in 14 languages, telemetry with a few laggy rounds), all `seed: true` |
| `backend/goose-brain/scripts/elastic-agent.ts` | Agent Builder: six tools + the agent `goosed-intel` |
| `backend/goose-brain/scripts/elastic-workflow.ts` | the Workflow `goosed-watch` |
| `Assets/Scripts/Core/GooseTelemetryStream.cs` | the sensor stream on the phone (self-bootstrapping, no scene change) |
| `Assets/Scripts/Goose/GooseVoice.cs` | a line the brain wrote but could not voice is shown as a subtitle with a honk (`subtitleBrainText`), so the case file reaches the player even when TTS is out of quota |

## The data (all of it messy on purpose)

| Index | What lands there | Written by | Mapping highlights |
|---|---|---|---|
| `goosed-shouts` | the transcript of every 2 s shout, with the detected language | the Worker after Whisper / OpenAI | `transcript` (text + keyword), `transcript_semantic` (`semantic_text`, Jina embeddings), `lang`, `device_id`, `goose`, `survival_at`, `tier` |
| `goosed-telemetry` | 5 Hz samples: player and goose position (xyz and cartesian `point`), distance, speeds, tier, anger, fps, frame ms, GPU ms, thermal state, goose state | the phone, batched every 3 s | time-series data stream: dimensions `device_id`, `round_id`; gauges for every measurement |
| `goosed-runs` | one document per finished round: outcome, survival, honks, dodges, breads, max tier, grudge, last shout | the Worker on `caught` / `outlasted` | keyword + numeric |
| `goosed-lines` | everything the goose said, per beat, with mood and source (openai / workersai / bank) | the Worker | `line` + `line_semantic` |
| `goosed-players` | flags an agent or the Workflow decided on (`mercy`, reason, count) | the Workflow | keyword + boolean |

Timestamps for telemetry are assigned by the Worker from the batch's own clock (`t` seconds since the batch started), so a
phone with a wrong clock cannot write outside the data stream's time window.

## The read path (what "agentic" means here)

**Session start, fast tier** (`fastIntel`, in parallel with naming the goose, 900 ms budget): seven ES|QL queries at once,
each optional:

```esql
FROM goosed-runs | WHERE @timestamp > NOW() - 24 hours
| STATS runs = COUNT(*), players = COUNT_DISTINCT(device_id), avg = AVG(survival), best = MAX(survival), goosed = SUM(CASE(outcome == "GOOSED", 1, 0))

FROM goosed-runs | WHERE @timestamp > NOW() - 24 hours | STATS best = MAX(survival) BY device_id | WHERE best > ?mine | STATS ahead = COUNT(*)

FROM goosed-telemetry | WHERE device_id == ?device AND @timestamp > NOW() - 7 days
| STATS avgSpeed = AVG(player_speed), maxSpeed = MAX(player_speed), still = SUM(CASE(player_speed < 0.15, 1, 0)), n = COUNT(*), avgDist = AVG(dist)

FROM goosed-shouts | WHERE @timestamp > NOW() - 24 hours AND device_id != ?device | SORT @timestamp DESC | KEEP transcript, lang | LIMIT 3
FROM goosed-players | WHERE device_id == ?device | KEEP mercy | LIMIT 1
```

The result is the case file in the goose's memory (`intel`): how many humans it faced today and how long they lasted,
where this player ranks, how fast they run and how much of the time they stand still, what they and others shouted,
whether the Workflow asked for mercy. The writer's character sheet says: use one concrete detail as ammunition, never
read numbers out.

**Yell, live** (`findEcho`, inside the beat's 4.2 s budget): one hybrid query, BM25 on the transcript fused with Jina
dense vectors on the `semantic_text` field by reciprocal rank fusion, optionally through Elastic's reranker, excluding
the player's own shouts. This is why a shout in French or Punjabi finds "go away" from another player an hour earlier:

```json
{ "retriever": { "rrf": { "retrievers": [
    { "standard": { "query": { "match": { "transcript": { "query": "va-t'en" } } }, "filter": [ { "bool": { "must_not": { "term": { "device_id": "…" } } } } ] } },
    { "standard": { "query": { "semantic": { "field": "transcript_semantic", "query": "va-t'en" } }, "filter": [ … ] } }
  ], "rank_window_size": 20, "rank_constant": 60 } } }
```

**Deep tier, background** (`deepIntel`): the Agent Builder agent `goosed-intel` is asked to "build the case file for
device X, use the tools, do not guess". It picks its own tools (ES|QL with parameters for the player's history, movement
profile, the crowd and the leaderboard; index search over the shouts and the lines), and its notes land in memory for
the next beat. It takes seconds, which is why it runs after the response.

**The Workflow** (`goosed-watch`, every 5 minutes, also runnable by hand) closes the loop with no human in it:

1. ES|QL over the sensor stream: rounds in the last 15 minutes whose median frame rate fell under 30 while the goose was
   within 1.2 m -> for each, an HTTP step posts to the Worker's `/elastic/alert`, which files a Sentry issue with the
   numbers attached (that is the second sponsor track getting data it could not otherwise have).
2. ES|QL over the runs: players goosed three or more times in under 8 s in the last two hours -> for each, an
   `elasticsearch.request` step writes `{ mercy: true }` into `goosed-players`; the goose's next case file reads it and
   the writer is told "be smug, not cruel".

## Performance budget (the phone never waits)

| Where | Cost | Why it does not hurt |
|---|---|---|
| phone, sampling | 5 Hz reads of positions already in memory, a preallocated `float[64 x 17]` ring, no allocations per frame | ~0.01 ms |
| phone, flush | one `UnityWebRequest` every 3 s (about 1.5 KB), from a coroutine, at most one in flight, dropped when the brain is offline or the thermal state is above "fair" | never blocks the main thread; `Sent` / `Dropped` counters |
| Worker, ingest | `202` first, `_bulk` inside `ctx.waitUntil` | the phone sees ~30 ms |
| Worker, indexing shouts / lines / runs | `waitUntil` | never awaited |
| session start | fast tier in parallel with naming, `Promise.race` against 900 ms | typically 100-250 ms hidden behind work the intro already waits on; a late answer is stored when it arrives |
| yell | one hybrid query (1.8 s timeout) | the yell beat already tolerates transcription latency; the deadline on the phone is 3.5 s |
| other beats | read the cached case file | 0 ms |
| deep tier | seconds | background only, consumed by later beats |

## Setup (once)

```bash
cd backend/goose-brain
# .dev.vars: ELASTIC_API_KEY=<Kibana > Stack Management > API keys>; endpoints are in wrangler.jsonc
npx wrangler secret put ELASTIC_API_KEY
npm run elastic:setup                      # indices, Jina semantic_text, the goosed-telemetry data stream (prints the rerank id)
npm run elastic:seed                       # the seeded day (idempotent with --clean first)
npm run elastic:agent                      # Agent Builder tools + agent; `-- --ask <device_id>` for one round-trip
npm run elastic:workflow -- --run          # the Workflow, run once now
npx wrangler deploy
```

`/health` reports `elastic: true` and `writer: "workersai" | "openai" | "bank"`.

## Judges' FAQ

- **Is this RAG with a chatbot?** No. There is no chat. The goose's memory is queried with aggregations (ES|QL over a
  time-series stream), hybrid search happens once per shout, and the decisions come out as words, as a Sentry issue and
  as a flag another agent reads. The Agent Builder agent chooses its own tools.
- **Where is the messy data?** Shouted speech from a hackathon hall (any language, Whisper transcripts, noise), a raw
  sensor stream from an AR session (jitter, tracking loss, thermal throttling), and logs of what the goose said.
- **Why Jina?** `semantic_text` on the project's Jina endpoint gives multilingual dense vectors at ingest, so the Worker
  never calls an embedding model and a Mandarin shout lands next to an English one. BM25 covers the exact quotes.
- **What happens when Elastic is down?** `elasticOn()` is false or a query fails: the case file is empty, the writer
  works from the Durable Object memory alone, telemetry is answered `204`. The game does not notice.
- **What did it change in the demo?** Watch the subtitle on the intro of a returning player ("Forty humans today. You
  are the slow one.") and the reply to a shout in another language. On the laptop: Kibana Discover on `goosed-telemetry`
  and the agent's conversation in Agent Builder.
