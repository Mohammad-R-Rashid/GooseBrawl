# Sentry: Best Use of Sentry

**Claim:** Sentry is the instrument we benchmark, profile and tune the AR game with, not an error inbox. The phone sends
per-round transactions with frame-level measurements, structured spike logs with game and AR context, and metrics; the
Worker continues the same trace; and the numbers decide render settings.

Products in use: **Errors**, **Tracing** (distributed phone -> Worker -> tools), **Logs** (both sides), **Metrics**,
**Agent/tool spans** on the Worker (`instrumentAgentWithSentry`, `gen_ai.execute_tool` for the voice, `gen_ai.chat` when
OpenAI is on), **Uptime** (monitor on `/health`, created in the UI). Sentry Profiling and Session Replay do not exist for
Unity, so the frame-level depth comes from our harness through Tracing and Logs.

Project: one Sentry project receives both sides (DSN in `Assets/Resources/Sentry/SentryOptions.asset` and
`backend/goose-brain/wrangler.jsonc`). Environments: `editor-mock`, `device`, `hackathon` (Worker).

## Where it lives

| File | Role |
|---|---|
| `Assets/Scripts/Core/GooseTelemetry.cs` | the only Sentry surface in the game: transactions, spans, measurements, logs, metrics, trace headers, exceptions (no-op without the package) |
| `Assets/Scripts/Core/PerfProbe.cs` | the profiler: per-frame sampling, round/phase windows, spike logs, adaptive quality ladder, render-setting controls |
| `Assets/Scripts/Core/PerfBenchmark.cs` | the benchmark: six render configurations, 15 s each, one `bench.<config>` transaction per config |
| `Assets/Scripts/Core/GooseSentryOptions.cs` | options script: tracing 100%, logs on, environment, release, default tags |
| `Assets/Scripts/Editor/GooseBrawlSetup.cs` (`ConfigureSentry`) | writes the SDK options asset from `./sentry.dsn` |
| `backend/goose-brain/src/server.ts`, `src/agent.ts`, `src/tools/*.ts` | `withSentry`, `instrumentAgentWithSentry`, `instrumentOpenAiClient`, tool spans, `Sentry.logger` |

## The phone-side harness

**Per frame** (`PerfProbe.Update`): wall-clock frame time; `FrameTimingManager` CPU main / render thread and GPU time;
`ProfilerRecorder` counters (Total Used Memory, GC Allocated In Frame, Draw Calls, SetPass, Triangles); and the context
that explains a bad frame: game state, goose state / tier / distance, live particles, LiDAR mesh chunks, planes, tracking,
occlusion mode and temporal smoothing, MSAA, render scale, shadow resolution, post-FX on/off, a brain request or WAV
decode in flight, the microphone, the iOS thermal state (native `GooseCamera_ThermalState`), the quality tier.

**Round transaction** `game.round` (op `game.round`, started in `BeginChase`, finished in `OnPlayerCaught` /
`OnGooseGaveUp`):
- measurements: `frame.p50_ms`, `frame.p95_ms`, `frame.max_ms`, `frames.over_33ms`, `frames.total`, `fps.avg`,
  `gpu.p95_ms`, `mainthread.p95_ms`, `mem.peak_mb`, `gc.kb_per_s`, `draw_calls.p95`, `mesh.chunks_max`,
  `particles.max`, `thermal.max`, `spikes`;
- data: outcome, survival, honks, dodges, dashes, lunges, best, goose name, lines from the brain, voice fallbacks;
- child spans per phase (`fly_in`, `glare`, `chase.tier0..3`, `dash`, `lunge`, `eating`, `flinch`, `end`, `paused`), each
  carrying the same stats for its window;
- child spans per operation: `brain.session`, `brain.event.<beat>` (http.client), `voice.fetch`, `voice.decode`,
  `voice.play`, `yell.detect`, `bread.spawn`.

**Distributed tracing**: every `UnityWebRequest` to the Worker carries `sentry-trace` and `baggage`
(`GooseTelemetry.AddTraceHeaders`); `withSentry` continues the trace, so one trace shows phone -> Worker -> ElevenLabs
(and -> OpenAI when enabled).

**Spike logs**: any frame over 33 ms on the device (50 ms in the Editor) is a structured log `frame.spike` with all the
context above, rate-limited to 5/s. Other structured logs: `yell.trigger` / `yell.gated reason=honk|goose_speaking|slowmo`
with dB levels, `bread.thrown/eating/eaten`, `dodge`, `voice.fallback reason=deadline|offline|unconfigured`,
`voice.deadline`, `brain.request_failed`, `quality.tier_changed`, `round.stats`.

**Metrics** (`SentrySdk.Metrics`): per round `frame.p95_ms`, `frame.p50_ms`, `gpu.p95_ms`, `mem.peak_mb` (distributions
tagged by outcome / config / device), `frames.spikes`, `rounds` (counters), `quality.tier` (gauge), `round.seconds`;
per request `brain.request_ms`; `voice.spoken`, `voice.fallback`, `yell.trigger`, `yell.gated`.

**Adaptive quality**: when the rolling 3 s p95 frame time exceeds 20 ms on the device the ladder steps down (MSAA 4x ->
2x, then occlusion temporal smoothing off, then grain / bloom / chromatic aberration off), never touching the goose, the
shadows or the shadow catcher, and logs `quality.tier_changed` with the thermal state.

**Benchmark**: three fingers held on the title for 1.5 s (or `bench` in the Editor) drives the game to a chase the goose
cannot win and runs `baseline`, `msaa2x`, `no_occlusion_smoothing`, `no_postfx`, `shadows1024`, `mesh_density_025` for
15 s each; each is a `bench.<config>` transaction with the same measurements and tags, so p95 per config can be compared
in Trace Explorer or a dashboard. The winners are written back into `GooseBrawlSetup.ConfigureRenderPipeline`.

## The Worker side

`withSentry` on the handler (`tracesSampleRate: 1.0`, `enableLogs: true`, `dataCollection.genAI` inputs/outputs),
`instrumentAgentWithSentry` on the `GooseBrain` class (every request and callable becomes a span, conversation id =
device), `instrumentOpenAiClient` (gen_ai.chat spans with token usage, when OpenAI is on), manual
`gen_ai.execute_tool` spans for `speak` (ElevenLabs) and `transcribe`, `Sentry.logger` for every line written, guard
rejection, cache hit / miss, budget overrun and fallback.

## What the data changed (the stories)

1. **The yell detector was firing on its own.** The first Editor run with the Mac microphone logged two `yell.trigger`
   lines in one smoke run (level -18 dBFS against a -58 dBFS floor) that were never a shout; the goose flinched twice and two
   unrelated checks failed. Fixes: the Editor no longer listens by default, the absolute floor rose to -20 dBFS, and the
   detector is gated for the goose's full honk length plus 0.5 s and while it speaks (`yell.gated reason=honk`). Every gate
   decision is a log line with its levels, so the phone run next to the speaker will show whether the gate holds.
2. **Bread made the goose too deadly.** Round transactions after the bread beat ended in `caught` inside the head start;
   the fix split escalation into a time-based tier (dashes, lunges) and an anger level (animation, honks); bread now buys
   distance instead of a faster lunge schedule.
3. **First-time voice lines missed the deadline.** `brain.event.*` spans at 1.4 s (`voice.deadline` logs) showed the TTS
   cost on the first play of each line; the fix is a pre-voiced bank uploaded into the Worker's cache and a longer deadline
   for non-urgent beats, proven by the same spans dropping to ~70 ms.
4. **Frame spikes in the Editor were the smoke test's screenshots**, not the game (`frame.spike` with `main_ms` around 1 ms
   and wall-clock 120 ms); the harness now logs Editor spikes at info level and the device threshold stays at 33 ms.
5. **The device benchmark** (to run on the iPhone 15 Pro before the demo) decides MSAA / occlusion smoothing / shadow
   resolution / mesh density from `bench.*` p95 per config; the README's "What Sentry told us" section holds the numbers.

## Verify it yourself

Run a round (or `Library/GooseBrawlCommand.txt` <- `smoke` in the Editor) and open the project in Sentry: Performance ->
`game.round` (measurements tab, phase spans), Explore -> Traces (a `smokeonline` run shows the Worker spans in the same
trace), Logs (filter `frame.spike`, `yell.`, `voice.`), Metrics (`frame.p95_ms` by `outcome`), Insights -> AI Agents
(`goose-brain` conversations; OpenAI spans appear once the key is set).

## FAQ (questions we expect from the Sentry judges)

**Which products beyond errors, concretely?**
Tracing (distributed, with custom measurements and phase spans), Logs (structured, both sides), Metrics (distributions,
counters, gauges), Agent/tool monitoring on the Worker, and an Uptime monitor on `/health`. Profiling and Replay are not
available for Unity; the harness fills that gap with tracing + logs.

**How did observability shape what you built?**
See "What the data changed": the honk gate, the tier/anger split, the pre-voiced cache, the Editor spike threshold, and
the render settings from the benchmark. Each is a log or span you can find.

**Isn't a per-frame probe expensive?**
It samples what Unity already computes (`FrameTimingManager`, `ProfilerRecorder`), keeps fixed-size lists, and sends one
transaction per round plus rate-limited logs. Particle counts are refreshed every ten frames, the particle list every two
seconds.

**Why one Sentry project for the phone and the Worker?**
So a single trace shows the phone's span tree continuing into the Worker and the third-party calls; environments and
tags (`device_model`, `mock_ar`, `config`) separate the views.

**What is the adaptive quality doing in production?**
Only stepping down, only on the device, only from the rolling p95, and every step is logged with the thermal state; the
render benchmark is how the baseline was chosen in the first place.

**Can we trigger something during the demo?**
Yes: a shout produces `yell.trigger` (or `yell.gated`) within a second; a bread throw produces `bread.thrown` -> `bread.eaten`
and a `brain.event.bread` span; the results card ends the `game.round` transaction with its measurements.
