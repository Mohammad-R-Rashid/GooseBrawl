# Benchmark run, 2026-09-20 00:37 EDT, iPhone 15 Pro (iPhone16,1), Release build

Started from the Mac (`bench_device.sh 15`), 15 s per configuration, phone at thermal state 0 (nominal). The run was
interrupted after six of the ten configurations (the app was relaunched by hand), so `shadows2048`, `mesh_on`,
`render_scale_085` and `baseline_end` were not measured. Numbers are from the device console (`[Bench]` lines);
the same windows were sent to Sentry as `bench.<config>` transactions and `bench.result` logs.

| config | frames | p50 ms | p95 ms | max ms | > 33 ms | GPU p95 ms | mem MB | particles max |
|---|---|---|---|---|---|---|---|---|
| baseline | 897 | 16.7 | 17.6 | 20.0 | 0 | 10.4 | 133 | 31 |
| msaa4x | 897 | 16.7 | 17.5 | 20.1 | 0 | 10.4 | 133 | 42 |
| msaa_off | 897 | 16.7 | 16.8 | 16.9 | 0 | 11.8 | 134 | 51 |
| no_occlusion_smoothing | 867 | 16.7 | 16.8 | 383.3 | 2 | 11.0 | 134 | 38 |
| no_occlusion | 897 | 16.7 | 16.8 | 22.0 | 0 | 11.6 | 134 | 8 |
| no_postfx | 897 | 16.7 | 16.8 | 17.4 | 0 | 9.3 | 134 | 42 |

Reading: every configuration sits on the 60 Hz cap (16.7 ms) with zero frames over 33 ms except the two in
`no_occlusion_smoothing`, which are one 383 ms stall right when the occlusion mode was switched (a mode change,
not a steady-state cost). GPU time is 9-12 ms per frame, so the shipped settings keep roughly 5 ms of GPU headroom;
the post chain is worth about 1 ms of GPU, MSAA 4x costs nothing measurable, and the GPU differences between the
other rows are within the noise of what was on screen (particle counts differ per window).
