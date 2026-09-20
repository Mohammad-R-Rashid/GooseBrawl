# GOOSED. sponsor integrations

One architecture serves four sponsor tracks. The goose in the AR game has a **brain** (a Cloudflare Agent), a **voice**
(ElevenLabs), a **writer** (OpenAI, optional), and the whole thing is **benchmarked, profiled and monitored with Sentry**.

```
 iPhone (Unity 6, AR Foundation)            Cloudflare Worker "goose-brain"                    Third parties
 ───────────────────────────────            ─────────────────────────────────                  ─────────────
 GooseBrainClient ── POST /session ──►      GooseBrain extends Agent (Durable Object + SQLite)  OpenAI Responses API (optional)
   sentry-trace + baggage headers             memory: name, title, voice, grudge, rounds,         writes the line, names the goose
                  ◄── {name, voice, intro}    best/last time, last shout, breads, dodges,      OpenAI Transcriptions (optional)
                  ── POST /event ──►          per-beat counts, event log                          what did the player shout?
 GooseVoice (3D voice source + goose EQ)    workflow: remember -> write -> voice -> cache      ElevenLabs TTS (live)
 YellDetector (mic, local flinch)           GET /audio/<hash>   R2 cache, content-addressed      Callum / Laura, v3 conversational
 PerfProbe + PerfBenchmark                  GET /health         Sentry Uptime target
 Sentry Unity SDK: errors, tracing with     @sentry/cloudflare: errors, tracing continued from the phone,
   frame measurements, logs, metrics          logs, agent + tool spans (gen_ai.*), Uptime
```

| Track | Document | Status in the demo build |
|---|---|---|
| Cloudflare: Best Agent with a Brain | [CLOUDFLARE.md](CLOUDFLARE.md) | live: `https://goose-brain.mohammad-rashid7337.workers.dev` |
| MLH: Best Use of ElevenLabs | [ELEVENLABS.md](ELEVENLABS.md) | live: two stock voices, 168-line pre-voiced bank + live cache |
| Sentry: Best Use of Sentry | [SENTRY.md](SENTRY.md) | live: errors, tracing, logs, metrics on phone and Worker; Uptime monitor to create in the UI |
| OpenAI: API Prizes | [OPENAI.md](OPENAI.md) | the writer (Responses API, structured output) and the ears (transcription); the script bank is the offline fallback |

Everything gameplay-relevant is deterministic and local. The cloud supplies words and voice, and every spoken beat has an
offline fallback, so the one demo run never depends on the venue wifi.
