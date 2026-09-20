# goose-brain — the goose's brain on Cloudflare

A Cloudflare **Agent** (Durable Object with SQLite) per player device. It is the memory, the writer and the voice of the
goose in GOOSED.:

- **Memory** (`this.state`, persisted): name, title, grudge (rounds the goose won), rounds, best/last survival time, the
  last thing the player shouted, breads thrown, lunges dodged, last line. Survives app launches: round 2 the goose says
  "you again" and quotes what you yelled last time.
- **Tools**: OpenAI Responses (`gpt-5.6-luna`, structured JSON) writes every line in character; OpenAI transcription
  (`gpt-transcribe`) turns the 2-second shout into text; ElevenLabs (`eleven_v3_conversational`, WAV 22.05 kHz) voices it;
  R2 caches the audio by content hash.
- **Workflow** per event: remember → write → voice → cache → answer `{ text, audioUrl, mood }` inside a 4.2 s budget
  (text always comes back; audio is dropped if the budget runs out; the phone then plays its offline bank).
- **Observability**: `@sentry/cloudflare` — the phone's `sentry-trace` header continues into the Worker, OpenAI calls are
  `gen_ai.*` spans (AI Agent monitoring), ElevenLabs and transcription are `gen_ai.execute_tool` spans, structured logs
  carry every line, guard rejection and fallback. `/health` is the Sentry Uptime target.

## Routes

| Method | Path | Body | Returns |
|---|---|---|---|
| GET | `/health` | | `{ ok, version, mock, voice }` |
| POST | `/agents/goose-brain/<deviceId>/session` | `{ roundsThisSession, gamesPlayed, bestTime, voice }` (`voice: false` = the phone plays its local bank, the intro is written but not voiced) | `{ name, title, grudge, rounds, returning, intro: { text, audioUrl, mood } }` |
| POST | `/agents/goose-brain/<deviceId>/event` | `{ kind, payload }` or multipart `json` + `audio` (WAV) for `yell`; `payload.spoken` = the bank line the phone already said (mid-chase reactions do not wait), recorded as the line, nothing written or voiced | `{ text, audioUrl, mood, source, transcript }` |
| GET | `/agents/goose-brain/<deviceId>/memory` | | the memory + last 20 events |
| POST | `/agents/goose-brain/<deviceId>/reset` | | wipes the memory |
| GET | `/audio/<sha256>` | | cached WAV |
| POST | `/warm` | | voices every bank line for both voices into the R2 cache (call until `done: true`); run once after a deploy so no line ever waits on TTS |
| POST | `/telemetry` | `{ device, goose, round, dur, samples: [[t, px, py, pz, gx, gy, gz, dist, player_speed, goose_speed, tier, anger, fps, frame_ms, gpu_ms, thermal, state], ...] }` | `202 { accepted }` before it is bulk-indexed into Elasticsearch (`goosed-telemetry`); `204` when Elastic is off |
| POST | `/elastic/alert` | `{ kind, message, device_id, data }` + header `x-warm-key` | the Elastic Workflow's findings -> a Sentry issue + log |
| GET | `/board` | | the booth page: today's leaderboard + the live brain of whoever is playing (polls `/board/data` every 1.5 s) |
| POST | `/board/run` | `{ deviceId, name, seconds, outcome, gooseName, honks, dodges }` | `{ rank, total, name }` (names sanitised to A-Z0-9, 12 chars; rolling 24 h window) |
| GET | `/board/top?n=3` | | `{ runs[], total }` for the title-screen ticker |
| GET | `/board/data?device=` | | `{ runs, total, latestDevice, brain: { memory, events } }` for the page (`device` overrides "who is playing") |

The board is a second Agent class, `GooseBoard` (one global Durable Object named `global`, migration `v2`); every `/session`
call touches it so the page follows the phone that just started a round.

Beats (`kind`): `intro`, `taunt10`, `yell`, `bread`, `dodge`, `rage`, `caught`, `outlasted`.

## Run

```bash
npm install
cp .dev.vars.example .dev.vars      # add OPENAI_API_KEY and ELEVENLABS_API_KEY (without them MOCK_AI is implied)
npm run dev                          # http://localhost:8787
curl -s -X POST localhost:8787/agents/goose-brain/me/session -H 'content-type: application/json' -d '{"gamesPlayed":0}'
```

## Deploy (once, needs `wrangler login`)

```bash
npx wrangler r2 bucket create goose-audio
npx wrangler secret put OPENAI_API_KEY
npx wrangler secret put ELEVENLABS_API_KEY
npm run design-voice                 # designs + saves the goose voice, prints ELEVENLABS_VOICE_ID -> wrangler.jsonc vars
npm run pregen                       # writes the offline bank to Assets/Resources/GooseVoice (npm run pregen -- --mock without a key)
npx wrangler deploy                  # -> https://goose-brain.<account>.workers.dev  (paste into GooseBrainClient.brainBaseUrl)
```

Set `SENTRY_DSN` in `wrangler.jsonc` vars to the Sentry "goose-brain" project DSN, then create an Uptime monitor on
`https://goose-brain.<account>.workers.dev/health` (Sentry UI → Monitors → New, 1 minute).

## Elastic (the context layer)

With `ELASTIC_URL` / `ELASTIC_KIBANA_URL` in `wrangler.jsonc` and the secret `ELASTIC_API_KEY`, every shout, line and
finished round is indexed (`src/tools/elastic.ts`, always in `waitUntil`, never awaited by the request), the phone's 5 Hz
sensor stream lands in the `goosed-telemetry` data stream, and the brain reads it back: at session start `src/intel.ts`
runs seven ES|QL queries in parallel (900 ms budget, else the intro goes out without them and the answer is stored when it
arrives), on a `yell` it runs one hybrid search (BM25 + Jina `semantic_text`, RRF, optional rerank via `ELASTIC_RERANK_ID`)
for who else shouted something similar, and the Agent Builder agent `ELASTIC_AGENT_ID` is asked for its notes in the
background. Without an OpenAI key the words come from Llama on Workers AI (`src/tools/workersai.ts`, binding `AI`) and the
shout is heard by Whisper (multilingual, language detected); with `MOCK_AI=1` everything is the bank. Full write-up:
[docs/sponsors/ELASTIC.md](../../docs/sponsors/ELASTIC.md).

```bash
# .dev.vars: ELASTIC_API_KEY=<Kibana > API keys>   then:
npx wrangler secret put ELASTIC_API_KEY
npm run elastic:setup                  # indices (+ Jina semantic_text), the telemetry data stream; prints the rerank id for wrangler.jsonc
npm run elastic:seed                   # a seeded day: 60 players, multilingual shouts, telemetry (all seed: true; --clean removes them)
npm run elastic:agent                  # Agent Builder: 6 tools + agent goosed-intel   (-- --ask seed0000... to try it)
npm run elastic:workflow -- --run      # the Workflow goosed-watch (every 5 min: perf collapses -> Sentry, fast losers -> mercy)
npx wrangler deploy
```

`scripts/lines.json` is exported from Unity (`Goose Brawl > Export Voice Lines`); it is the offline bank and the
model's fallback.
