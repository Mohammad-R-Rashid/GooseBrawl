using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Scripted render-settings benchmark on the phone (or the Editor mock): drives the normal flow to a chase the
    /// goose cannot win, then runs a matrix of configurations for a fixed number of seconds each. Every configuration
    /// is a Sentry transaction ("bench.<config>") with the PerfProbe frame measurements, so the p95 frame time per
    /// configuration can be compared in Sentry and the winners written back into GooseBrawlSetup.
    /// Start: 3-finger hold on the title screen, or Library/GooseBrawlCommand.txt <- bench in the Editor.
    /// </summary>
    public class PerfBenchmark : MonoBehaviour
    {
        public struct Config
        {
            public string name;
            public int msaa;
            public bool occlusionSmoothing;
            public bool postFx;
            public int shadowRes;
            public float meshDensity;
        }

        public static readonly Config[] Matrix =
        {
            new Config { name = "baseline", msaa = 2, occlusionSmoothing = true, postFx = true, shadowRes = 1024, meshDensity = 0.35f },
            new Config { name = "msaa4x", msaa = 4, occlusionSmoothing = true, postFx = true, shadowRes = 1024, meshDensity = 0.35f },
            new Config { name = "no_occlusion_smoothing", msaa = 2, occlusionSmoothing = false, postFx = true, shadowRes = 1024, meshDensity = 0.35f },
            new Config { name = "no_postfx", msaa = 2, occlusionSmoothing = true, postFx = false, shadowRes = 1024, meshDensity = 0.35f },
            new Config { name = "shadows2048", msaa = 2, occlusionSmoothing = true, postFx = true, shadowRes = 2048, meshDensity = 0.35f },
            new Config { name = "mesh_density_060", msaa = 2, occlusionSmoothing = true, postFx = true, shadowRes = 1024, meshDensity = 0.6f },
        };

        [Tooltip("Seconds per configuration.")]
        public float secondsPerConfig = 15f;
        [Tooltip("Editor smoke runs use a shorter matrix step.")]
        public float secondsPerConfigEditor = 3f;

        public bool Running { get; private set; }
        public bool Completed { get; private set; }
        public readonly List<(string name, PerfProbe.Window stats)> Results = new List<(string, PerfProbe.Window)>();

        public void Begin()
        {
            if (Running) return;
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            Running = true;
            Completed = false;
            Results.Clear();
            var mgr = GooseGameManager.Instance;
            var perf = PerfProbe.Instance;
            if (mgr == null || perf == null) { Running = false; yield break; }
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "bench.start", ("configs", Matrix.Length), ("seconds", Application.isEditor ? secondsPerConfigEditor : secondsPerConfig));

            // Drive the normal flow to a chase: title -> scan -> place -> steal (skipped) -> chase.
            if (mgr.State == GooseGameState.Boot)
            {
                mgr.SkipCoachingOnce = true;
                mgr.OnStartPressed();
            }
            float t = 0f;
            while (mgr.State != GooseGameState.PlaceNest && mgr.State != GooseGameState.EggReady && !mgr.ChaseActive && t < 40f) { t += Time.deltaTime; yield return null; }
            if (mgr.State == GooseGameState.PlaceNest)
            {
                t = 0f;
                while (mgr.State == GooseGameState.PlaceNest && t < 30f)
                {
                    t += Time.deltaTime;
                    if (mgr.Placement.HasReticle || GooseGameManager.UseMockAR)
                        mgr.Placement.TryPlaceAtScreenPoint(new Vector2(Screen.width * 0.5f, Screen.height * 0.4f));
                    yield return new WaitForSeconds(0.5f);
                }
            }
            if (mgr.State == GooseGameState.EggReady)
            {
                mgr.OnStealEggPressed();
                yield return null;
                mgr.RequestSkip();
            }
            t = 0f;
            while (!mgr.ChaseActive && t < 45f) { t += Time.deltaTime; if (mgr.State == GooseGameState.EggStolen) mgr.RequestSkip(); yield return null; }
            if (!mgr.ChaseActive || mgr.Goose == null)
            {
                GooseTelemetry.Log(GooseTelemetry.Level.Error, "bench.abort", ("state", mgr.State.ToString()));
                Running = false;
                yield break;
            }

            mgr.Goose.NoCatch = true;
            mgr.UI.ShowMessage("BENCHMARK", 1.6f, UITheme.Yolk);
            float seconds = Application.isEditor ? secondsPerConfigEditor : secondsPerConfig;
            var device = SystemInfo.deviceModel;
            foreach (var cfg in Matrix)
            {
                Apply(perf, cfg);
                yield return new WaitForSeconds(0.6f);
                var tags = new Dictionary<string, string>
                {
                    { "config", cfg.name }, { "device_model", device }, { "msaa", cfg.msaa.ToString() }, { "occlusion_smoothing", cfg.occlusionSmoothing.ToString() },
                    { "post_fx", cfg.postFx.ToString() }, { "shadow_res", cfg.shadowRes.ToString() }, { "mesh_density", cfg.meshDensity.ToString("F2") }
                };
                perf.BeginRound("bench." + cfg.name, "bench", tags);
                mgr.UI.ShowMessage(cfg.name.ToUpperInvariant().Replace('_', ' '), 1.2f, UITheme.Yolk);
                yield return new WaitForSeconds(seconds);
                var stats = perf.EndRound("bench", null);
                if (stats != null)
                {
                    Results.Add((cfg.name, stats));
                    Debug.Log("[Bench] " + stats.Summary());
                }
            }
            Apply(perf, Matrix[0]);
            perf.RestoreRenderSettings();
            mgr.Goose.NoCatch = false;
            mgr.UI.ShowMessage("BENCH DONE", 2f, UITheme.Yolk);
            var sb = new System.Text.StringBuilder("[Bench] RESULTS (" + device + ")\n");
            foreach (var r in Results) sb.Append("  ").Append(r.name.PadRight(24)).Append(" p50 ").Append(r.stats.P50.ToString("F1")).Append("  p95 ").Append(r.stats.P95.ToString("F1")).Append("  max ").Append(r.stats.MaxMs.ToString("F1")).Append("  >33 ").Append(r.stats.Over33).Append("  gpu95 ").Append(r.stats.Gpu95.ToString("F1")).Append('\n');
            Debug.Log(sb.ToString());
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "bench.complete", ("configs", Results.Count), ("device", device));
            Completed = true;
            Running = false;
        }

        static void Apply(PerfProbe perf, Config cfg)
        {
            perf.SetMsaa(cfg.msaa);
            perf.SetOcclusionSmoothing(cfg.occlusionSmoothing);
            perf.SetPostFx(cfg.postFx);
            perf.SetShadowResolution(cfg.shadowRes);
            perf.SetMeshDensity(cfg.meshDensity);
        }
    }

#if UNITY_EDITOR
    /// <summary>Spawned by the Editor remote 'bench' command: starts the benchmark once the scene is up.</summary>
    public class GooseBenchStarter : MonoBehaviour
    {
        IEnumerator Start()
        {
            yield return null;
            yield return null;
            var mgr = GooseGameManager.Instance;
            if (mgr != null && mgr.Bench != null) mgr.Bench.Begin();
        }
    }
#endif
}
