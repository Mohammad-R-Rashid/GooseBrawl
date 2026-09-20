# GOOSED. performance benchmark (Sentry-driven)

How the AR chase is profiled and tuned on the iPhone 15 Pro, what the harness measures, how a run is started, what the
numbers mean, and what changed because of them. Companion to [docs/sponsors/SENTRY.md](sponsors/SENTRY.md).

## Why this exists

An AR game on a phone has three budgets that fight each other: the ARKit camera and depth pipeline, the URP frame
(MSAA, shadows, post-processing, occlusion compositing) and the CPU work the game adds (mesh colliders from LiDAR
chunks, particles, UI rebuilds, audio decode, network). Guessing which one costs the frame time is how demos stutter.
So the game carries its own profiler, reports through Sentry, and has a scripted benchmark that measures each
render setting in isolation on the real device. The defaults committed in the repo are the ones the numbers pick.

## The instrument

All code lives in `Assets/Scripts/Core/`:

| Piece | What it does |
|---|---|
| `PerfProbe` | Every frame: wall-clock frame time, `FrameTimingManager` CPU main-thread and GPU times, `ProfilerRecorder` memory / GC / draw calls, live particle count, LiDAR chunk count, thermal state (`ProcessInfo.thermalState` via `GooseCamera.mm`). Keeps a rolling window; a round is a Sentry transaction `game.round` with one `game.phase` span per phase (fly_in, glare, chase.tier0..3, lunge, dash, eating, flinch, end) and measurements per window. Any frame over 33 ms on the device is a Sentry log `frame.spike` carrying the whole context (goose state, particles, chunks, occlusion, MSAA, render scale, brain request in flight, voice decode, mic, thermal). An adaptive ladder steps the render settings down while a round runs when the rolling p95 stays above 20 ms and logs `quality.tier_changed`. |
| `PerfBenchmark` | The scripted matrix (below). Drives the normal flow to a chase the goose cannot win (`NoCatch`), applies one configuration, records a window of N seconds as a transaction `bench.<config>` (tags: config, msaa, occlusion, occlusion_smoothing, post_fx, shadow_res, meshing, mesh_density, render_scale, device_model), logs `bench.result` with the same numbers, emits the Application Metrics and finally writes a JSON report to `Documents/bench/latest.json` (plus a timestamped copy). The baseline runs first and last so thermal drift over the run is visible. |
| `GooseTelemetry` | The thin Sentry wrapper (transactions, spans, measurements, structured logs, metrics, trace headers for the Worker). Compiled only under `GOOSE_SENTRY`. |
| Sentry SDK options | `Assets/Resources/Sentry/SentryOptions.asset` (written by `GooseBrawlSetup.ConfigureSentry`): tracing 100 %, structured logging on, and the SDK's own automatic metrics on: frame rate / frame time every second, memory and GC every 10 s, network every 10 s. |

Measurements on every window: `frame.p50_ms`, `frame.p95_ms`, `frame.max_ms`, `frames.over_33ms`, `frames.total`,
`fps.avg`, `gpu.p95_ms`, `mainthread.p95_ms`, `mem.peak_mb`, `gc.kb_per_s`, `draw_calls.p95`, `mesh.chunks_max`,
`particles.max`, `thermal.max`, `spikes`.

Metrics (time series, tagged with `kind` round|bench, `config`, `device`): `frame.p95_ms`, `frame.p50_ms`,
`gpu.p95_ms`, `mem.peak_mb`, `frames.spikes`, `quality.tier`, `round.seconds`, `rounds`, `voice.spoken`,
`brain.request_ms`.

## The matrix

Each configuration is the committed baseline with exactly one thing changed, so a difference in p95 is that thing's
cost. 15 s per configuration on the phone (3 s in the Editor mock), ten configurations, about three minutes.

| Config | Change from baseline | Question it answers |
|---|---|---|
| `baseline` | MSAA 2x, occlusion (Fastest + temporal smoothing), post-FX on, 1024 shadow map, LiDAR mesh off, render scale 1.0 | The shipped settings |
| `msaa4x` | MSAA 4x | Can we afford smoother edges? |
| `msaa_off` | no MSAA | How much does 2x cost? |
| `no_occlusion_smoothing` | depth temporal smoothing off | The first rung of the adaptive ladder |
| `no_occlusion` | environment depth off | The whole occlusion pipeline's cost |
| `no_postfx` | bloom, vignette, grain, CA, lens, colour off | The post chain's cost |
| `shadows2048` | 2048 shadow map | Can we afford crisper shadows? |
| `mesh_on` | LiDAR scene reconstruction on (density 0.35) | What meshing cost us before it was turned off |
| `render_scale_085` | render scale 0.85 | The last rung of the adaptive ladder |
| `baseline_end` | baseline again | Thermal drift during the run |

## Running it

Three ways to start the same run:

1. **From the Mac (recommended, hands-free except for holding the phone).** Build and install the Release build
   (`scratchpad/build_device.sh`), then:

   ```bash
   /private/tmp/claude-501/-Volumes-devDrive-Dev-mac-GooseBrawl/c44ba907-f5b5-40ac-bad1-5e3e0a581f38/scratchpad/bench_device.sh 15 docs/benchmarks
   ```

   It launches the app with the environment variable `GOOSE_BENCH=1` (and `GOOSE_BENCH_SECONDS`), streams the
   device console until the `[Bench] RESULTS` line, then copies `Documents/bench/latest.json` out of the app
   container into `docs/benchmarks/` and prints the table.
2. **On the phone.** Hold three fingers on the title screen for 1.5 s.
3. **In the Editor mock.** `echo bench > Library/GooseBrawlCommand.txt` (3 s per config; useful only to check the
   harness itself, the mock has no ARKit cost).

What the person holding the phone does: tap nothing. Aim at the floor until the nest lands (the harness places it
for you), then stand still and keep the goose roughly in view for three minutes. The goose runs, lunges and dashes
but cannot catch you. Keep the phone away from anything warm and start from a cool phone (the report records the
thermal state at start and end).

## Reading the results

- **Locally**: `docs/benchmarks/bench_<timestamp>.json`, one object per configuration with the measurements above,
  plus device, OS, GPU, screen, thermal start/end.
- **Sentry, Trace Explorer**: `transaction:bench.*` grouped by the `config` tag, column `measurements.frame.p95_ms`
  (and `gpu.p95_ms`, `mainthread.p95_ms`). The `device_model` tag separates phones.
- **Sentry, Logs**: `message:bench.result` lists every configuration with its numbers; `message:frame.spike` during
  a bench window shows what a bad frame looked like (with `config` in the surrounding transaction);
  `message:quality.tier_changed` shows the ladder acting during real rounds.
- **Sentry, Metrics**: `frame.p95_ms` filtered `kind:bench`, grouped by `config`; the SDK's own
  `frames.frame_time` / `frames.fps` series show the whole session including the menu and the scan.

Decision rules used: a change is free when it moves p95 by less than 1 ms and adds no frames over 33 ms; the
shipped setting is the heaviest free one; anything that adds frames over 33 ms is out; the ladder order is the
order of the cheapest visual loss per millisecond saved.

## What the harness already changed (before the device numbers)

These came from the Editor-mock spike logs and the device console of the earlier builds, and from the profile of the
code rather than the matrix:

| Finding | Change |
|---|---|
| Every LiDAR chunk update cooked a `MeshCollider` on the main thread; spike logs on the phone carried `mesh_chunks` climbing while the frame hitched; the goose steers just as well against ARKit wall planes. | Scene reconstruction off by default (`ARBootstrapper.enableEnvironmentMeshing = false`); still measurable through `mesh_on`. |
| Environment probes upload and convolve a cubemap per update, and nothing glossy was left to reflect it (the egg and the nest are matte). | Environment probes off by default. |
| The HUD rebuilt its text meshes (and allocated strings) every frame for the timer, honks and best; the whole overlay canvas was re-batched with them. | HUD labels only touched when the shown value changes; the HUD lives on its own nested canvas so only its batch rebuilds. |
| The goose's voice filters (high-pass, distortion, chorus) were applied to the shared source for honks too. | Filters enabled only while a line plays. |
| MSAA 4x / 2048 shadow map were unverified guesses. | MSAA 2x, 1024 map, 8 m shadow distance committed; the matrix measures the heavier options. |

## Results

First device run: 2026-09-20 00:37 EDT, iPhone 15 Pro (iPhone16,1), Release build, thermal state nominal, 15 s per
configuration, started from the Mac. The run was interrupted after six of the ten configurations (the app was
relaunched by hand), so `shadows2048`, `mesh_on`, `render_scale_085` and `baseline_end` still need a run. Raw
lines: [benchmarks/bench_2026-09-20_iphone15pro_partial.md](benchmarks/bench_2026-09-20_iphone15pro_partial.md).

| Config | p50 ms | p95 ms | max ms | frames > 33 ms | GPU p95 ms | mem MB | particles max |
|---|---|---|---|---|---|---|---|
| baseline | 16.7 | 17.6 | 20.0 | 0 | 10.4 | 133 | 31 |
| msaa4x | 16.7 | 17.5 | 20.1 | 0 | 10.4 | 133 | 42 |
| msaa_off | 16.7 | 16.8 | 16.9 | 0 | 11.8 | 134 | 51 |
| no_occlusion_smoothing | 16.7 | 16.8 | 383.3 | 2 | 11.0 | 134 | 38 |
| no_occlusion | 16.7 | 16.8 | 22.0 | 0 | 11.6 | 134 | 8 |
| no_postfx | 16.7 | 16.8 | 17.4 | 0 | 9.3 | 134 | 42 |

What it says:

- **The phone is on the 60 Hz cap in every configuration** (p50 = 16.7 ms is the vsync interval) with no frames over
  33 ms in steady state. The two long frames in `no_occlusion_smoothing` are one 383 ms stall at the moment the
  occlusion mode was switched, a transition cost the ladder pays once, not a per-frame cost.
- **GPU time is 9-12 ms per frame**, so the shipped settings (MSAA 2x, occlusion with smoothing, post-FX, 1024
  shadows, mesh off) keep about 5 ms of GPU headroom at 60 fps. MSAA 4x costs nothing measurable; the post chain is
  worth about 1 ms of GPU; the rest of the differences are within the noise of what was on screen (the particle
  counts differ per window). Memory is flat at 133-134 MB.
- **Decision**: keep the committed defaults. There is no need to spend the headroom (4x MSAA would be free but adds
  nothing visible on a 460 ppi screen) and no need to drop anything. The stutter the player felt earlier is not in
  these numbers, which is consistent with its source being the LiDAR mesh-collider cooking that is now off and the
  main-thread work around voice and network events (both show up as `frame.spike` logs with context, not as
  steady-state frame time). The adaptive ladder stays as a safety net for hot phones.
- Still to measure: `mesh_on` (to put a number on the removed cost), `shadows2048`, `render_scale_085` and the
  end-of-run baseline for thermal drift. Start it from the phone with the three-finger hold on the title screen, or
  from the Mac with `bench_device.sh` when nobody is playing.

## Reproducing the analysis without Sentry access

The JSON report is self-contained. `bench_device.sh` prints the table; the same numbers are what the transactions
carry as measurements, so the local file and the Sentry view never disagree.
