using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Scripted render-settings benchmark on the phone (or the Editor mock): drives the normal flow to a chase the
    /// goose cannot win, then runs a matrix of configurations for a fixed number of seconds each. Every configuration
    /// is a Sentry transaction ("bench.<config>") with the PerfProbe frame measurements, a "bench.result" log with the
    /// same numbers, and Application Metrics (frame.p95_ms etc. tagged config=...), so the p95 frame time per
    /// configuration can be compared in Sentry and the winners written back into GooseBrawlSetup / ARBootstrapper.
    /// The run also writes a JSON report next to the app (persistentDataPath/bench/latest.json) for offline reading.
    /// Start: 3-finger hold on the title screen, the launch environment variable GOOSE_BENCH=1 (devicectl), or
    /// Library/GooseBrawlCommand.txt <- bench in the Editor. The baseline runs first and last so thermal drift shows.
    /// </summary>
    public class PerfBenchmark : MonoBehaviour
    {
        public struct Config
        {
            public string name;
            public int msaa;
            public bool occlusion;
            public bool occlusionSmoothing;
            public bool postFx;
            public int shadowRes;
            public bool meshing;
            public float meshDensity;
            public float renderScale;

            public static Config Baseline(string name) => new Config
            {
                name = name, msaa = 2, occlusion = true, occlusionSmoothing = true, postFx = true, shadowRes = 1024,
                meshing = false, meshDensity = 0.35f, renderScale = 1f
            };
        }

        public static readonly Config[] Matrix = BuildMatrix();

        static Config[] BuildMatrix()
        {
            var baseline = Config.Baseline("baseline");
            var msaa4 = Config.Baseline("msaa4x"); msaa4.msaa = 4;
            var msaaOff = Config.Baseline("msaa_off"); msaaOff.msaa = 1;
            var noSmooth = Config.Baseline("no_occlusion_smoothing"); noSmooth.occlusionSmoothing = false;
            var noOccl = Config.Baseline("no_occlusion"); noOccl.occlusion = false; noOccl.occlusionSmoothing = false;
            var noPost = Config.Baseline("no_postfx"); noPost.postFx = false;
            var shadows = Config.Baseline("shadows2048"); shadows.shadowRes = 2048;
            var mesh = Config.Baseline("mesh_on"); mesh.meshing = true;
            var scale = Config.Baseline("render_scale_085"); scale.renderScale = 0.85f;
            var end = Config.Baseline("baseline_end");
            return new[] { baseline, msaa4, msaaOff, noSmooth, noOccl, noPost, shadows, mesh, scale, end };
        }

        [Tooltip("Seconds per configuration.")]
        public float secondsPerConfig = 15f;
        [Tooltip("Editor smoke runs use a shorter matrix step.")]
        public float secondsPerConfigEditor = 3f;
        [Tooltip("Environment variable that starts the benchmark right after launch (xcrun devicectl ... -e '{\"GOOSE_BENCH\":\"1\"}').")]
        public string autoStartEnvVar = "GOOSE_BENCH";

        public bool Running { get; private set; }
        public bool Completed { get; private set; }
        public string LastReportPath { get; private set; }
        public readonly List<(string name, PerfProbe.Window stats)> Results = new List<(string, PerfProbe.Window)>();

        public static string ReportFolder => Path.Combine(Application.persistentDataPath, "bench");

        IEnumerator Start()
        {
            string flag = null;
            try { flag = Environment.GetEnvironmentVariable(autoStartEnvVar); } catch { }
            if (string.IsNullOrEmpty(flag) || flag == "0") yield break;
            string secs = null;
            try { secs = Environment.GetEnvironmentVariable(autoStartEnvVar + "_SECONDS"); } catch { }
            if (!string.IsNullOrEmpty(secs) && float.TryParse(secs, NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s > 1f) secondsPerConfig = s;
            // Give the AR session and the title screen a moment, then go.
            float t = 0f;
            while (t < 3f || GooseGameManager.Instance == null || GooseGameManager.Instance.State != GooseGameState.Boot)
            {
                t += Time.deltaTime;
                if (t > 20f) yield break;
                yield return null;
            }
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "bench.autostart", ("env", autoStartEnvVar), ("seconds", secondsPerConfig));
            Begin();
        }

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
            float seconds = Application.isEditor ? secondsPerConfigEditor : secondsPerConfig;
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "bench.start", ("configs", Matrix.Length), ("seconds", seconds));

            // Drive the normal flow to a chase: title -> scan -> place -> steal (skipped) -> chase.
            if (mgr.State == GooseGameState.Boot)
            {
                mgr.SkipCoachingOnce = true;
                mgr.OnStartPressed();
            }
            float t = 0f;
            while (mgr.State != GooseGameState.PlaceNest && mgr.State != GooseGameState.EggReady && !mgr.ChaseActive && t < 60f) { t += Time.deltaTime; yield return null; }
            if (mgr.State == GooseGameState.PlaceNest)
            {
                t = 0f;
                while (mgr.State == GooseGameState.PlaceNest && t < 45f)
                {
                    t += 0.5f;
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
                Debug.Log("[Bench] ABORT in state " + mgr.State);
                Running = false;
                yield break;
            }

            mgr.Goose.NoCatch = true;
            mgr.UI.ShowMessage("BENCHMARK", 1.6f, UITheme.Yolk);
            var device = SystemInfo.deviceModel;
            int thermalStart = perf.ThermalState;
            foreach (var cfg in Matrix)
            {
                Apply(perf, cfg);
                yield return new WaitForSeconds(0.8f);
                var tags = Tags(cfg, device);
                perf.BeginRound("bench." + cfg.name, "bench", tags);
                mgr.UI.ShowMessage(cfg.name.ToUpperInvariant().Replace('_', ' '), 1.2f, UITheme.Yolk);
                yield return new WaitForSeconds(seconds);
                var stats = perf.EndRound("bench", null);
                if (stats != null)
                {
                    Results.Add((cfg.name, stats));
                    Debug.Log("[Bench] " + stats.Summary());
                    GooseTelemetry.Log(GooseTelemetry.Level.Info, "bench.result", ("config", cfg.name), ("device", device),
                        ("p50_ms", stats.P50), ("p95_ms", stats.P95), ("max_ms", stats.MaxMs), ("over33", stats.Over33), ("spikes", stats.Spikes),
                        ("gpu95_ms", stats.Gpu95), ("main95_ms", stats.Main95), ("draws95", stats.Draws95), ("mem_mb", stats.MemPeak / (1024f * 1024f)),
                        ("gc_kb_s", stats.GcKbPerSecond), ("thermal_max", stats.ThermalMax), ("frames", stats.Frames), ("seconds", stats.Seconds));
                }
            }
            Apply(perf, Matrix[0]);
            perf.RestoreRenderSettings();
            mgr.Goose.NoCatch = false;
            mgr.UI.ShowMessage("BENCH DONE", 2f, UITheme.Yolk);
            var sb = new StringBuilder("[Bench] RESULTS (" + device + ", " + SystemInfo.operatingSystem + ")\n");
            foreach (var r in Results)
                sb.Append("  ").Append(r.name.PadRight(24)).Append(" p50 ").Append(r.stats.P50.ToString("F1")).Append("  p95 ").Append(r.stats.P95.ToString("F1"))
                  .Append("  max ").Append(r.stats.MaxMs.ToString("F1")).Append("  >33 ").Append(r.stats.Over33).Append("  gpu95 ").Append(r.stats.Gpu95.ToString("F1"))
                  .Append("  main95 ").Append(r.stats.Main95.ToString("F1")).Append("  draws ").Append(r.stats.Draws95.ToString("F0")).Append("  thermal ").Append(r.stats.ThermalMax).Append('\n');
            Debug.Log(sb.ToString());
            LastReportPath = WriteReport(device, seconds, thermalStart, perf.ThermalState);
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "bench.complete", ("configs", Results.Count), ("device", device), ("report", LastReportPath ?? ""));
            Completed = true;
            Running = false;
        }

        static Dictionary<string, string> Tags(Config cfg, string device)
        {
            return new Dictionary<string, string>
            {
                { "config", cfg.name }, { "device_model", device }, { "msaa", cfg.msaa.ToString() }, { "occlusion", cfg.occlusion.ToString() },
                { "occlusion_smoothing", cfg.occlusionSmoothing.ToString() }, { "post_fx", cfg.postFx.ToString() }, { "shadow_res", cfg.shadowRes.ToString() },
                { "meshing", cfg.meshing.ToString() }, { "mesh_density", cfg.meshDensity.ToString("F2", CultureInfo.InvariantCulture) },
                { "render_scale", cfg.renderScale.ToString("F2", CultureInfo.InvariantCulture) }
            };
        }

        static void Apply(PerfProbe perf, Config cfg)
        {
            perf.SetMsaa(cfg.msaa);
            perf.SetOcclusion(cfg.occlusion);
            perf.SetOcclusionSmoothing(cfg.occlusion && cfg.occlusionSmoothing);
            perf.SetPostFx(cfg.postFx);
            perf.SetShadowResolution(cfg.shadowRes);
            perf.SetMeshDensity(cfg.meshDensity);
            perf.SetMeshing(cfg.meshing);
            perf.SetRenderScale(cfg.renderScale);
        }

        /// <summary>JSON report (hand-written: no dictionary support in JsonUtility). Returns the path or null.</summary>
        string WriteReport(string device, float seconds, int thermalStart, int thermalEnd)
        {
            try
            {
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder(8192);
                sb.Append("{\n");
                sb.Append("  \"device\": \"").Append(Escape(device)).Append("\",\n");
                sb.Append("  \"os\": \"").Append(Escape(SystemInfo.operatingSystem)).Append("\",\n");
                sb.Append("  \"gpu\": \"").Append(Escape(SystemInfo.graphicsDeviceName)).Append("\",\n");
                sb.Append("  \"app\": \"").Append(Escape(Application.version)).Append("\",\n");
                sb.Append("  \"unity\": \"").Append(Escape(Application.unityVersion)).Append("\",\n");
                sb.Append("  \"editor\": ").Append(Application.isEditor ? "true" : "false").Append(",\n");
                sb.Append("  \"screen\": \"").Append(Screen.width).Append('x').Append(Screen.height).Append("\",\n");
                sb.Append("  \"target_fps\": ").Append(Application.targetFrameRate).Append(",\n");
                sb.Append("  \"seconds_per_config\": ").Append(seconds.ToString("F1", inv)).Append(",\n");
                sb.Append("  \"thermal_start\": ").Append(thermalStart).Append(", \"thermal_end\": ").Append(thermalEnd).Append(",\n");
                sb.Append("  \"started_utc\": \"").Append(DateTime.UtcNow.ToString("o", inv)).Append("\",\n");
                sb.Append("  \"configs\": [\n");
                for (int i = 0; i < Results.Count; i++)
                {
                    var (name, w) = Results[i];
                    Config cfg = Matrix[0];
                    foreach (var c in Matrix) if (c.name == name) cfg = c;
                    sb.Append("    { \"name\": \"").Append(name).Append("\", \"msaa\": ").Append(cfg.msaa).Append(", \"occlusion\": ").Append(cfg.occlusion ? "true" : "false")
                      .Append(", \"occlusion_smoothing\": ").Append(cfg.occlusionSmoothing ? "true" : "false").Append(", \"post_fx\": ").Append(cfg.postFx ? "true" : "false")
                      .Append(", \"shadow_res\": ").Append(cfg.shadowRes).Append(", \"meshing\": ").Append(cfg.meshing ? "true" : "false")
                      .Append(", \"render_scale\": ").Append(cfg.renderScale.ToString("F2", inv)).Append(",\n      ");
                    bool first = true;
                    foreach (var kv in w.Measurements())
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        sb.Append('"').Append(kv.Key).Append("\": ").Append(double.IsNaN(kv.Value) || double.IsInfinity(kv.Value) ? "0" : kv.Value.ToString("F2", inv));
                    }
                    sb.Append(", \"seconds\": ").Append(w.Seconds.ToString("F1", inv)).Append(", \"main95_ms\": ").Append(w.Main95.ToString("F2", inv))
                      .Append(", \"gc_kb_per_s\": ").Append(w.GcKbPerSecond.ToString("F1", inv)).Append(" }").Append(i < Results.Count - 1 ? ",\n" : "\n");
                }
                sb.Append("  ]\n}\n");
                Directory.CreateDirectory(ReportFolder);
                string stamped = Path.Combine(ReportFolder, "bench_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", inv) + ".json");
                File.WriteAllText(stamped, sb.ToString());
                File.WriteAllText(Path.Combine(ReportFolder, "latest.json"), sb.ToString());
                Debug.Log("[Bench] report written: " + stamped);
                return stamped;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Bench] report not written: " + e.Message);
                return null;
            }
        }

        static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
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
