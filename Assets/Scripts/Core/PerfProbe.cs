using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace GooseBrawl
{
    /// <summary>
    /// The game's own profiler, reporting through Sentry Tracing and Logs (Sentry has no Profiling product for Unity).
    /// Every frame it samples the wall-clock frame time, the CPU/GPU frame timings, memory, GC and render stats, and
    /// the game / AR context that explains a bad frame (goose state, particles, LiDAR mesh chunks, occlusion, MSAA,
    /// network or audio work in flight, thermal state). A round becomes a transaction with frame measurements and one
    /// span per phase; any frame over the spike threshold becomes a structured log with that context; and when the
    /// p95 frame time stays high the adaptive quality ladder steps the render settings down and logs the change.
    /// The benchmark uses the same windows to compare render configurations on the phone.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public class PerfProbe : MonoBehaviour
    {
        public static PerfProbe Instance { get; private set; }

        [Header("Spikes")]
        [Tooltip("A frame slower than this on the device is logged with its context.")]
        public float spikeThresholdMsDevice = 33f;
        public float spikeThresholdMsEditor = 50f;
        public float spikeLogsPerSecond = 5f;

        [Header("Adaptive quality (steps down, never up, while a round runs)")]
        public bool adaptive = true;
        [Tooltip("p95 frame time over the window that triggers a step down (ms).")]
        public float adaptiveP95Ms = 20f;
        public float adaptiveWindowSeconds = 3f;
        public float adaptiveMinIntervalSeconds = 6f;
        public int maxQualityTier = 3;

        /// <summary>Everything sampled over one window (a round, a phase or a benchmark config).</summary>
        public class Window
        {
            public string Name;
            public float StartedAt;
            public int Frames, Over33, Spikes;
            public float MaxMs, SumMs;
            public long MemPeak;
            public double GcBytes;
            public int ParticlesMax, MeshChunksMax, ThermalMax;
            public readonly List<float> FrameMs = new List<float>(4096);
            public readonly List<float> GpuMs = new List<float>(4096);
            public readonly List<float> MainMs = new List<float>(4096);
            public readonly List<float> DrawCalls = new List<float>(1024);
            public float Seconds => Time.realtimeSinceStartup - StartedAt;

            public void Add(float ms, float gpu, float main, long mem, long gc, int draws, int particles, int chunks, int thermal, float spikeMs)
            {
                Frames++;
                SumMs += ms;
                if (ms > MaxMs) MaxMs = ms;
                if (ms > 33.4f) Over33++;
                if (ms > spikeMs) Spikes++;
                if (FrameMs.Count < 6000 || (Frames & 1) == 0)
                {
                    FrameMs.Add(ms);
                    GpuMs.Add(gpu);
                    MainMs.Add(main);
                }
                if (draws > 0 && (Frames % 6) == 0) DrawCalls.Add(draws);
                if (mem > MemPeak) MemPeak = mem;
                if (gc > 0) GcBytes += gc;
                if (particles > ParticlesMax) ParticlesMax = particles;
                if (chunks > MeshChunksMax) MeshChunksMax = chunks;
                if (thermal > ThermalMax) ThermalMax = thermal;
            }

            public static float Percentile(List<float> xs, float p)
            {
                if (xs == null || xs.Count == 0) return 0f;
                var copy = new List<float>(xs);
                copy.Sort();
                int i = Mathf.Clamp(Mathf.RoundToInt((copy.Count - 1) * p), 0, copy.Count - 1);
                return copy[i];
            }

            public float P50 => Percentile(FrameMs, 0.5f);
            public float P95 => Percentile(FrameMs, 0.95f);
            public float Gpu95 => Percentile(GpuMs, 0.95f);
            public float Main95 => Percentile(MainMs, 0.95f);
            public float Draws95 => Percentile(DrawCalls, 0.95f);
            public float AvgFps => Frames > 0 && SumMs > 0f ? Frames / (SumMs / 1000f) : 0f;
            public float GcKbPerSecond => Seconds > 0.5f ? (float)(GcBytes / 1024.0 / Seconds) : 0f;

            public Dictionary<string, double> Measurements()
            {
                return new Dictionary<string, double>
                {
                    { "frame.p50_ms", P50 }, { "frame.p95_ms", P95 }, { "frame.max_ms", MaxMs }, { "frames.over_33ms", Over33 },
                    { "frames.total", Frames }, { "fps.avg", AvgFps }, { "gpu.p95_ms", Gpu95 }, { "mainthread.p95_ms", Main95 },
                    { "mem.peak_mb", MemPeak / (1024.0 * 1024.0) }, { "gc.kb_per_s", GcKbPerSecond }, { "draw_calls.p95", Draws95 },
                    { "mesh.chunks_max", MeshChunksMax }, { "particles.max", ParticlesMax }, { "thermal.max", ThermalMax }, { "spikes", Spikes }
                };
            }

            public string Summary()
            {
                return Name + ": " + Seconds.ToString("F1") + " s, " + Frames + " frames, p50 " + P50.ToString("F1") + " ms, p95 " + P95.ToString("F1") +
                       " ms, max " + MaxMs.ToString("F1") + " ms, >33ms " + Over33 + ", gpu95 " + Gpu95.ToString("F1") + " ms, mem " +
                       (MemPeak / (1024f * 1024f)).ToString("F0") + " MB, draws95 " + Draws95.ToString("F0") + ", particles " + ParticlesMax +
                       ", chunks " + MeshChunksMax + ", thermal " + ThermalMax;
            }
        }

        // Context flags other systems raise while they work, so a spike log can name the culprit.
        public bool BrainRequestInFlight;
        public bool VoiceDecodeInFlight;
        public bool MicListening;

        public int QualityTier { get; private set; }
        public Window Round { get; private set; }
        public Window Phase { get; private set; }
        public Window LastRoundStats { get; private set; }
        public int SpikeCount { get; private set; }
        public float LastFrameMs { get; private set; }
        public float RollingP95 { get; private set; }
        public int ThermalState { get; private set; }
        public int LiveParticles { get; private set; }
        public bool RoundOpen => Round != null;

        GooseTelemetry.Span m_Tx, m_PhaseSpan;
        string m_PhaseName;
        readonly FrameTiming[] m_Timings = new FrameTiming[1];
        ProfilerRecorder m_MemRec, m_GcRec, m_DrawRec, m_SetPassRec, m_TriRec;
        readonly List<float> m_Ring = new List<float>(256);
        float m_NextAdaptiveCheck, m_LastTierChange, m_NextSpikeSlot;
        int m_SpikeBudget;
        int m_FramesSinceRound;
        float m_NextParticleScan, m_NextThermalPoll;
        ParticleSystem[] m_Particles = Array.Empty<ParticleSystem>();

        // Render settings the ladder / benchmark can touch, with the originals for restore.
        int m_MsaaOriginal = -1, m_ShadowResOriginal = -1;
        float m_MeshDensityOriginal = -1f, m_RenderScaleOriginal = -1f;
        bool m_Touched;

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int GooseCamera_ThermalState();
#endif

        void Awake()
        {
            Instance = this;
        }

        void OnEnable()
        {
            try
            {
                m_MemRec = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Used Memory");
                m_GcRec = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
                m_DrawRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
                m_SetPassRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
                m_TriRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            }
            catch (Exception e) { GooseLog.Warn("Profiler recorders unavailable: " + e.Message); }
        }

        void OnDisable()
        {
            m_MemRec.Dispose(); m_GcRec.Dispose(); m_DrawRec.Dispose(); m_SetPassRec.Dispose(); m_TriRec.Dispose();
        }

        void OnDestroy()
        {
            RestoreRenderSettings();
            if (Instance == this) Instance = null;
        }

        void OnApplicationQuit()
        {
            RestoreRenderSettings();
        }

        // ------------------------------------------------------------------ windows
        public void BeginRound(string name, string op, IDictionary<string, string> tags = null)
        {
            if (Round != null) EndRound("abandoned", null);
            m_Tx = GooseTelemetry.StartTransaction(name, op, tags);
            Round = new Window { Name = name, StartedAt = Time.realtimeSinceStartup };
            m_FramesSinceRound = 0;
            m_PhaseName = null;
            if (tags != null) foreach (var kv in tags) m_Tx.SetTag(kv.Key, kv.Value);
            m_Tx.SetData("quality.tier_start", QualityTier);
        }

        public void BeginPhase(string name)
        {
            if (Round == null || name == m_PhaseName) return;
            EndPhase();
            m_PhaseName = name;
            Phase = new Window { Name = name, StartedAt = Time.realtimeSinceStartup };
            m_PhaseSpan = GooseTelemetry.StartSpan("game.phase", name);
        }

        public void EndPhase()
        {
            if (Phase == null) return;
            if (m_PhaseSpan != null)
            {
                foreach (var kv in Phase.Measurements()) m_PhaseSpan.SetData(kv.Key, kv.Value);
                m_PhaseSpan.Finish();
            }
            m_PhaseSpan = null;
            Phase = null;
            m_PhaseName = null;
        }

        public Window EndRound(string outcome, IDictionary<string, object> data)
        {
            if (Round == null) return null;
            EndPhase();
            var w = Round;
            if (m_Tx != null)
            {
                foreach (var kv in w.Measurements())
                {
                    string unit = kv.Key.EndsWith("_ms") ? "ms" : (kv.Key.EndsWith("_mb") ? "mb" : "");
                    m_Tx.SetMeasurement(kv.Key, kv.Value, unit);
                }
                m_Tx.SetData("outcome", outcome);
                m_Tx.SetData("quality.tier_end", QualityTier);
                m_Tx.SetData("seconds", w.Seconds);
                if (data != null) foreach (var kv in data) m_Tx.SetData(kv.Key, kv.Value);
                m_Tx.SetTag("outcome", outcome);
                m_Tx.Finish();
            }
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "round.stats " + w.Summary(), ("outcome", outcome), ("p95_ms", w.P95), ("over33", w.Over33), ("spikes", w.Spikes), ("quality_tier", QualityTier));
            // Application Metrics: the same numbers as time series, so a demo day of rounds can be charted by outcome / device.
            string kind = w.Name.StartsWith("bench.") ? "bench" : "round";
            string cfg = w.Name.StartsWith("bench.") ? w.Name.Substring(6) : outcome;
            GooseTelemetry.Distribution("frame.p95_ms", w.P95, "ms", ("kind", kind), ("config", cfg), ("device", SystemInfo.deviceModel));
            GooseTelemetry.Distribution("frame.p50_ms", w.P50, "ms", ("kind", kind), ("config", cfg), ("device", SystemInfo.deviceModel));
            GooseTelemetry.Distribution("gpu.p95_ms", w.Gpu95, "ms", ("kind", kind), ("config", cfg), ("device", SystemInfo.deviceModel));
            GooseTelemetry.Distribution("mem.peak_mb", w.MemPeak / (1024.0 * 1024.0), "mb", ("kind", kind), ("device", SystemInfo.deviceModel));
            GooseTelemetry.Increment("frames.spikes", w.Spikes, ("kind", kind), ("config", cfg));
            GooseTelemetry.Gauge("quality.tier", QualityTier, "", ("device", SystemInfo.deviceModel));
            if (kind == "round") { GooseTelemetry.Distribution("round.seconds", w.Seconds, "s", ("outcome", outcome)); GooseTelemetry.Increment("rounds", 1, ("outcome", outcome)); }
            LastRoundStats = w;
            Round = null;
            m_Tx = null;
            return w;
        }

        // ------------------------------------------------------------------ per frame
        void Update()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            LastFrameMs = ms;
            m_FramesSinceRound++;

            float gpu = 0f, main = 0f;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, m_Timings) > 0)
                {
                    gpu = (float)m_Timings[0].gpuFrameTime;
                    main = (float)m_Timings[0].cpuMainThreadFrameTime;
                }
            }
            catch { }

            long mem = m_MemRec.Valid ? m_MemRec.LastValue : 0;
            long gc = m_GcRec.Valid ? m_GcRec.LastValue : 0;
            int draws = m_DrawRec.Valid ? (int)m_DrawRec.LastValue : 0;

            float now = Time.realtimeSinceStartup;
            if (now >= m_NextParticleScan)
            {
                m_NextParticleScan = now + 2f;
                m_Particles = FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
            }
            if ((m_FramesSinceRound % 10) == 0)
            {
                int live = 0;
                foreach (var ps in m_Particles) if (ps != null && ps.isPlaying) live += ps.particleCount;
                LiveParticles = live;
            }
            if (now >= m_NextThermalPoll)
            {
                m_NextThermalPoll = now + 1f;
                ThermalState = ReadThermal();
            }

            var mgr = GooseGameManager.Instance;
            int chunks = mgr != null && mgr.Environment != null ? mgr.Environment.MeshCount : 0;
            float spikeMs = Application.isEditor ? spikeThresholdMsEditor : spikeThresholdMsDevice;

            if (Round != null)
            {
                UpdatePhase(mgr);
                if (m_FramesSinceRound > 3)
                {
                    Round.Add(ms, gpu, main, mem, gc, draws, LiveParticles, chunks, ThermalState, spikeMs);
                    Phase?.Add(ms, gpu, main, mem, gc, draws, LiveParticles, chunks, ThermalState, spikeMs);
                }
            }

            // Spike logs (with the context), rate limited so a stall never floods the log stream.
            if (ms > spikeMs && m_FramesSinceRound > 3 && Time.timeScale > 0f)
            {
                SpikeCount++;
                if (now >= m_NextSpikeSlot) { m_NextSpikeSlot = now + 1f; m_SpikeBudget = Mathf.Max(1, Mathf.RoundToInt(spikeLogsPerSecond)); }
                if (m_SpikeBudget > 0)
                {
                    m_SpikeBudget--;
                    // Editor spikes are mostly the smoke test's screenshots: keep them at info level there.
                    GooseTelemetry.Log(Application.isEditor ? GooseTelemetry.Level.Info : GooseTelemetry.Level.Warning, "frame.spike", ContextAttributes(mgr, ms, gpu, main, mem, gc, draws, chunks));
                }
            }

            // Rolling p95 for the adaptive ladder.
            m_Ring.Add(ms);
            int keep = Mathf.Max(60, Mathf.RoundToInt(adaptiveWindowSeconds * 60f));
            if (m_Ring.Count > keep) m_Ring.RemoveRange(0, m_Ring.Count - keep);
            if (now >= m_NextAdaptiveCheck)
            {
                m_NextAdaptiveCheck = now + 0.5f;
                RollingP95 = Window.Percentile(m_Ring, 0.95f);
                bool inRound = Round != null && mgr != null && mgr.ChaseActive && !mgr.IsPaused;
                bool benching = mgr != null && mgr.Bench != null && mgr.Bench.Running;
                if (adaptive && inRound && !benching && !Application.isEditor && RollingP95 > adaptiveP95Ms && QualityTier < maxQualityTier &&
                    now - m_LastTierChange > adaptiveMinIntervalSeconds && m_Ring.Count >= keep)
                {
                    StepDown(RollingP95);
                }
            }
        }

        void UpdatePhase(GooseGameManager mgr)
        {
            if (mgr == null) return;
            string phase;
            var goose = mgr.Goose;
            switch (mgr.State)
            {
                case GooseGameState.EggStolen:
                    phase = goose == null ? "steal" : (goose.FlyingIn ? "fly_in" : (goose.Glaring ? "glare" : "steal"));
                    break;
                case GooseGameState.Chasing:
                case GooseGameState.Danger:
                case GooseGameState.JumpAttack:
                    if (goose == null) phase = "chase";
                    else
                    {
                        switch (goose.State)
                        {
                            case GooseState.JumpAttack: phase = "lunge"; break;
                            case GooseState.Dash: phase = "dash"; break;
                            case GooseState.Eating: phase = "eating"; break;
                            case GooseState.Distracted: phase = "distracted"; break;
                            case GooseState.Flinched: phase = "flinch"; break;
                            case GooseState.NameCalled: phase = "name_called"; break;
                            default: phase = "chase.tier" + goose.Tier; break;
                        }
                    }
                    break;
                case GooseGameState.GameOver: phase = "end"; break;
                default: phase = mgr.State.ToString().ToLowerInvariant(); break;
            }
            if (mgr.IsPaused) phase = "paused";
            BeginPhase(phase);
        }

        (string, object)[] ContextAttributes(GooseGameManager mgr, float ms, float gpu, float main, long mem, long gc, int draws, int chunks)
        {
            var goose = mgr != null ? mgr.Goose : null;
            var urp = Urp;
            bool occlusion = mgr != null && mgr.AR != null && mgr.AR.OcclusionActive;
            bool smoothing = mgr != null && mgr.AR != null && mgr.AR.occlusionManager != null && mgr.AR.occlusionManager.environmentDepthTemporalSmoothingRequested;
            return new (string, object)[]
            {
                ("frame_ms", ms), ("gpu_ms", gpu), ("main_ms", main), ("mem_mb", mem / (1024f * 1024f)), ("gc_kb", gc / 1024f), ("draw_calls", draws),
                ("game_state", mgr != null ? mgr.State.ToString() : "none"), ("goose_state", goose != null ? goose.State.ToString() : "none"),
                ("goose_tier", goose != null ? goose.Tier : -1), ("goose_distance", goose != null ? goose.DistanceToPlayer : -1f),
                ("particles", LiveParticles), ("mesh_chunks", chunks), ("meshing", mgr != null && mgr.AR != null && mgr.AR.MeshingActive), ("planes", mgr != null && mgr.Environment != null ? mgr.Environment.PlaneCount : 0),
                ("tracking_good", mgr != null && mgr.Player != null && mgr.Player.TrackingGood), ("occlusion", occlusion), ("occlusion_smoothing", smoothing),
                ("msaa", urp != null ? urp.msaaSampleCount : 0), ("render_scale", urp != null ? urp.renderScale : 0f), ("shadow_res", urp != null ? urp.mainLightShadowmapResolution : 0),
                ("post_fx", PostFxEnabled), ("brain_in_flight", BrainRequestInFlight), ("voice_decode", VoiceDecodeInFlight), ("mic", MicListening),
                ("thermal", ThermalState), ("quality_tier", QualityTier), ("time_scale", Time.timeScale), ("phase", m_PhaseName ?? "none")
            };
        }

        static int ReadThermal()
        {
#if UNITY_IOS && !UNITY_EDITOR
            try { return GooseCamera_ThermalState(); } catch { return 0; }
#else
            return 0;
#endif
        }

        // ------------------------------------------------------------------ quality ladder + render controls
        public static UniversalRenderPipelineAsset Urp => (QualitySettings.renderPipeline as UniversalRenderPipelineAsset) ?? GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;

        public static bool PostFxEnabled
        {
            get
            {
                var cam = Camera.main;
                if (cam == null) return false;
                var data = cam.GetUniversalAdditionalCameraData();
                return data != null && data.renderPostProcessing;
            }
        }

        void Remember()
        {
            if (m_Touched) return;
            m_Touched = true;
            var urp = Urp;
            if (urp != null) { m_MsaaOriginal = urp.msaaSampleCount; m_ShadowResOriginal = urp.mainLightShadowmapResolution; m_RenderScaleOriginal = urp.renderScale; }
            var mgr = GooseGameManager.Instance;
            if (mgr != null && mgr.AR != null && mgr.AR.meshManager != null) m_MeshDensityOriginal = mgr.AR.meshManager.density;
        }

        /// <summary>Restore whatever the ladder or the benchmark changed (assets keep runtime edits in the Editor).</summary>
        public void RestoreRenderSettings()
        {
            if (!m_Touched) return;
            var urp = Urp;
            if (urp != null)
            {
                if (m_MsaaOriginal > 0) urp.msaaSampleCount = m_MsaaOriginal;
                if (m_ShadowResOriginal > 0) urp.mainLightShadowmapResolution = m_ShadowResOriginal;
                if (m_RenderScaleOriginal > 0f) urp.renderScale = m_RenderScaleOriginal;
            }
            SetOcclusionSmoothing(true);
            SetPostFx(true);
            if (CinematicLookController.Instance != null) CinematicLookController.Instance.SetReducedFX(false);
            var mgr = GooseGameManager.Instance;
            if (mgr != null && mgr.AR != null)
            {
                if (mgr.AR.meshManager != null && m_MeshDensityOriginal >= 0f) mgr.AR.meshManager.density = m_MeshDensityOriginal;
                mgr.AR.SetMeshing(mgr.AR.enableEnvironmentMeshing);
                mgr.AR.SetOcclusion(mgr.AR.enableOcclusion);
            }
            QualityTier = 0;
        }

        public void SetMsaa(int samples)
        {
            Remember();
            var urp = Urp;
            if (urp != null) urp.msaaSampleCount = Mathf.Clamp(samples, 1, 8);
        }

        public void SetShadowResolution(int res)
        {
            Remember();
            var urp = Urp;
            if (urp != null) urp.mainLightShadowmapResolution = res;
        }

        public void SetOcclusionSmoothing(bool on)
        {
            Remember();
            var mgr = GooseGameManager.Instance;
            if (mgr == null || mgr.AR == null || mgr.AR.IsMock || mgr.AR.occlusionManager == null) return;
            try { mgr.AR.occlusionManager.environmentDepthTemporalSmoothingRequested = on; } catch { }
        }

        public void SetPostFx(bool on)
        {
            Remember();
            var cam = Camera.main;
            if (cam == null) return;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data != null) data.renderPostProcessing = on;
        }

        public void SetMeshDensity(float density)
        {
            Remember();
            var mgr = GooseGameManager.Instance;
            if (mgr == null || mgr.AR == null || mgr.AR.IsMock || mgr.AR.meshManager == null) return;
            try { mgr.AR.meshManager.density = Mathf.Clamp01(density); } catch { }
        }

        public void SetMeshing(bool on)
        {
            Remember();
            var mgr = GooseGameManager.Instance;
            if (mgr == null || mgr.AR == null) return;
            mgr.AR.SetMeshing(on);
        }

        public void SetOcclusion(bool on)
        {
            Remember();
            var mgr = GooseGameManager.Instance;
            if (mgr == null || mgr.AR == null) return;
            mgr.AR.SetOcclusion(on);
        }

        public void SetRenderScale(float scale)
        {
            Remember();
            var urp = Urp;
            if (urp != null) urp.renderScale = Mathf.Clamp(scale, 0.5f, 1f);
        }

        /// <summary>Ladder from the MSAA 2x / 1024 baseline: 1 = occlusion smoothing off, 2 = grain/bloom/CA off, 3 = render scale 0.85.</summary>
        public void ApplyQualityTier(int tier)
        {
            tier = Mathf.Clamp(tier, 0, maxQualityTier);
            Remember();
            SetOcclusionSmoothing(tier < 1);
            if (CinematicLookController.Instance != null) CinematicLookController.Instance.SetReducedFX(tier >= 2);
            var urp = Urp;
            if (urp != null && m_RenderScaleOriginal > 0f) urp.renderScale = tier >= 3 ? Mathf.Min(m_RenderScaleOriginal, 0.85f) : m_RenderScaleOriginal;
            QualityTier = tier;
        }

        void StepDown(float p95)
        {
            int from = QualityTier;
            ApplyQualityTier(QualityTier + 1);
            m_LastTierChange = Time.realtimeSinceStartup;
            m_Ring.Clear();
            GooseTelemetry.Log(GooseTelemetry.Level.Warning, "quality.tier_changed", ("from", from), ("to", QualityTier), ("p95_ms", p95), ("thermal", ThermalState), ("mesh_chunks", GooseGameManager.Instance != null && GooseGameManager.Instance.Environment != null ? GooseGameManager.Instance.Environment.MeshCount : 0));
            if (m_Tx != null) m_Tx.SetData("quality.tier_changed_at_s", Round != null ? Round.Seconds : 0f);
        }

        public void ResetQuality()
        {
            RestoreRenderSettings();
            m_Ring.Clear();
        }
    }
}
