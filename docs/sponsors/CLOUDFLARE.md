# Cloudflare: Best Agent with a Brain

**Claim:** the goose's brain is a Cloudflare **Agent**. One Durable Object per player device holds the goose's memory,
owns its tools (a writer, a transcriber, a voice, a cache) and runs a workflow for every game event. Workers is the
runtime and the orchestration layer; there is no other server anywhere in the project.

## Where it lives

`backend/goose-brain/` (TypeScript, Wrangler 4, `agents` 0.24, `@sentry/cloudflare` 10.75)

| File | Role |
|---|---|
| `src/server.ts` | Worker entry: `/health`, `/audio/<hash>`, `/warm`, `/cache/<hash>`, everything else -> `routeAgentRequest` |
| `src/agent.ts` | `GooseBrainBase extends Agent<Env, GooseMemory>`: memory, event table, `onRequest`, the workflow |
| `src/tools/openai.ts` | tools `writeLine`, `namePersona`, `transcribe` (OpenAI Responses + Transcriptions; bank fallback) |
| `src/tools/elevenlabs.ts` | tool `speak` (ElevenLabs TTS, delivery settings, model fallback) |
| `src/tools/r2.ts` | content-addressed WAV cache in R2, served through the Worker |
| `src/bank.ts`, `scripts/lines.json` | the offline script (84 lines, 12 personas) exported from Unity |
| `src/guard.ts` | content guard for model output |
| `wrangler.jsonc` | Durable Object binding + SQLite migration, R2 bucket, vars |

Unity side: `Assets/Scripts/Core/GooseBrainClient.cs` (the HTTP client), `GoosePersona.cs` (the on-device mirror of the
memory), `Goose/GooseVoice.cs` (plays what the brain returns, with a deadline and a fallback).

## The Agent

```ts
export class GooseBrainBase extends Agent<Env, GooseMemory> {
  initialState: GooseMemory = { name: "", title: "", voice: "male", grudge: 0, rounds: 0, outlasted: 0, bestTime: 0,
    lastTime: 0, lastShout: "", breads: 0, dodges: 0, lastLine: "", counts: {}, created, lastSeen };
  async onStart() { this.sql`CREATE TABLE IF NOT EXISTS events (...)`; }
  async onRequest(request) { /* /session | /event | /memory | /reset */ }
}
export const GooseBrain = Sentry.instrumentAgentWithSentry(sentryOptions, GooseBrainBase);
```

- **Instance = player device.** The URL is `/agents/goose-brain/<deviceId>/...`; `routeAgentRequest` maps the name to a
  Durable Object id, so the same phone always reaches the same goose.
- **Memory** is `this.state` (persisted automatically in the DO's SQLite, survives deploys and app reinstalls): the goose's
  name, title and **voice** (fixed the moment it is named: a goose never changes voice), the **grudge** (rounds it won),
  rounds, best and last survival time, the last thing the player shouted, breads thrown, lunges dodged, the last line, and
  per-beat counters for the current round (so a second dodge gets a different line).
- **Event log**: an `events` table through `this.sql` (kind, payload, line, source, timestamp), returned by `GET .../memory`.
- **Tools**: `namePersona`, `writeLine`, `transcribe` (OpenAI, each with a deterministic bank fallback), `speak`
  (ElevenLabs), the R2 cache. Every tool is a span in the trace (see SENTRY.md).

## The workflow (one game event)

```
POST /agents/goose-brain/<device>/event   { kind: "yell" | "bread" | "dodge" | "rage" | "caught" | "outlasted" | "taunt10", payload }
   (or multipart: json + audio.wav for "yell")
 1. transcribe(audio)            -> transcript (OpenAI; "" in mock-text mode)
 2. remember(kind, payload)      -> state: breads++, dodges++, grudge++ on caught, outlasted++, lastShout, per-beat counts
 3. writeLine(kind, memory)      -> { line, mood }  (OpenAI structured JSON through the content guard; bank line otherwise)
 4. speak(line, voiceFor(memory)) -> WAV  (ElevenLabs; skipped when the R2 key exists; skipped when the 4.2 s budget is gone)
 5. cache                        -> R2 key sha256(voiceId | model | deliverySignature | text)
 6. record(events table), setState
 <- { text, mood, source, audioUrl, transcript, ms }
```

`POST .../session` runs at the title screen: it names the goose if needed, bumps `rounds`, resets the per-beat counters
and **pre-produces the intro line and audio** (`intro` or `intro_again` when the goose has met this player before) so the
first words at landing have zero latency.

Time budget: 4.2 s total per event; text always comes back, audio is dropped when the budget runs out and the phone
plays its offline copy of the same script.

## Cloudflare products used

| Product | Use |
|---|---|
| Workers | runtime + orchestration (`src/server.ts`), routes, CORS, cache serving |
| Agents SDK (`agents`) | `Agent` base class, `routeAgentRequest`, state persistence, `this.sql` |
| Durable Objects (SQLite) | one goose per device, memory + event table |
| R2 (`goose-audio`) | content-addressed WAV cache (168 lines x 2 voices pre-uploaded; `/warm` voices anything missing) |
| Wrangler secrets / vars | `ELEVENLABS_API_KEY` (secret), voice ids, delivery settings, `SENTRY_DSN` |
| Observability | `observability.enabled` in `wrangler.jsonc` plus Sentry on every request |

## The phone side

`GooseBrainClient` (Unity) reads the Worker URL from `Assets/Resources/GooseBrainUrl.txt`, keeps a device GUID in
PlayerPrefs, sends every request with Sentry trace headers, fetches the WAV with `UnityWebRequestMultimedia` and caches
the `AudioClip`. It goes `Offline` on the first network failure (retry after 20 s); `GooseVoice.SayBeat` waits at most
2.5 s (3.5 s for the shout reply) before playing the offline bank. `GoosePersona` mirrors name / title / voice / grudge in
PlayerPrefs so an offline phone still has a consistent goose.

## Verify it yourself

```bash
U=https://goose-brain.mohammad-rashid7337.workers.dev
curl -s $U/health
curl -s -X POST $U/agents/goose-brain/judge1/session -H 'content-type: application/json' -d '{"gamesPlayed":0}'
curl -s -X POST $U/agents/goose-brain/judge1/event   -H 'content-type: application/json' -d '{"kind":"dodge","payload":{"survival":12}}'
curl -s -X POST $U/agents/goose-brain/judge1/event   -H 'content-type: application/json' -d '{"kind":"caught","payload":{"survival":30}}'
curl -s -X POST $U/agents/goose-brain/judge1/session -H 'content-type: application/json' -d '{"gamesPlayed":1}'   # returning: true, grudge: 1, intro_again
curl -s $U/agents/goose-brain/judge1/memory
```

Local: `cd backend/goose-brain && npm install && npm run dev` (mock mode without keys), `npm test`.

## FAQ (questions we expect from the Cloudflare judges)

**Is this really an agent, or an API wrapper?**
It has memory (Durable Object state that outlives the app), tools (writer, transcriber, voice, cache), a workflow per
event (remember -> write -> voice -> cache) and it acts on its own state (the grudge changes what it says on the next
round). The chat-shaped part is optional (OpenAI); the agent's memory and workflow run regardless.

**Where is the state, exactly?**
`this.state` of the `GooseBrain` Durable Object, persisted by the Agents SDK in the object's SQLite, plus an `events` table
written with `this.sql`. `GET /agents/goose-brain/<device>/memory` dumps both.

**Why Durable Objects rather than KV or D1?**
One goose per player is exactly one object: single-writer, strongly consistent, co-located with its own SQLite, and the
Agents SDK gives state persistence and routing for free. R2 holds the audio because the objects are large and immutable.

**Does Workers do real work or just proxy?**
It runs the workflow, enforces the time budget, applies the content guard, computes the cache keys, shapes the text for
the voice model, serves the cached audio with immutable caching, and is the only place the secrets live.

**What happens when the venue wifi dies?**
Nothing visible: the phone plays the same script from its offline bank; the client marks itself offline and retries in
20 s. The next time it reaches the Worker, the memory is still there.

**Can we see it fail gracefully?**
`POST .../event` with an unknown `kind` returns 400; a TTS timeout returns text with `audioUrl: null`; the phone then plays
its bank copy. Sentry has every case as a log line.

**How much does it cost to run?**
Workers free tier + one small R2 bucket; ElevenLabs is the only metered dependency and the bank is cached, so a demo day
is effectively free.
