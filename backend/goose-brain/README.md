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
| POST | `/agents/goose-brain/<deviceId>/session` | `{ roundsThisSession, gamesPlayed, bestTime }` | `{ name, title, grudge, rounds, returning, intro: { text, audioUrl, mood } }` |
| POST | `/agents/goose-brain/<deviceId>/event` | `{ kind, payload }` or multipart `json` + `audio` (WAV) for `yell` | `{ text, audioUrl, mood, source, transcript }` |
| GET | `/agents/goose-brain/<deviceId>/memory` | | the memory + last 20 events |
| POST | `/agents/goose-brain/<deviceId>/reset` | | wipes the memory |
| GET | `/audio/<sha256>` | | cached WAV |
| POST | `/warm` | | voices every bank line for both voices into the R2 cache (call until `done: true`); run once after a deploy so no line ever waits on TTS |

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

`scripts/lines.json` is exported from Unity (`Goose Brawl > Export Voice Lines`); it is the offline bank and the
model's fallback.
