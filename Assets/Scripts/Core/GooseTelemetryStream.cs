using System.Collections;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace GooseBrawl
{
    /// <summary>
    /// The AR sensor stream for Elasticsearch: 5 Hz samples of where the player and the goose are, how fast they move,
    /// the goose's state and the frame time, batched every 3 s into one POST /telemetry on the goose-brain Worker
    /// (which answers 202 before it indexes). Self-bootstrapping (no scene or prefab change), a preallocated ring,
    /// no per-frame allocations, one request in flight at most, silent when the brain is offline or unconfigured,
    /// and it stops sampling when the phone runs hot. Nothing here touches gameplay.
    /// Columns: t, px, py, pz, gx, gy, gz, dist, player_speed, goose_speed, tier, anger, fps, frame_ms, gpu_ms, thermal, state.
    /// </summary>
    public class GooseTelemetryStream : MonoBehaviour
    {
        public const string Route = "/telemetry";
        [Tooltip("Seconds between samples (5 Hz).")] public float sampleInterval = 0.2f;
        [Tooltip("Seconds between posts.")] public float flushInterval = 3f;
        [Tooltip("iOS thermal state above which sampling pauses (0 nominal, 1 fair, 2 serious, 3 critical).")] public int maxThermalState = 1;
        public bool verbose;

        const int Capacity = 64;
        const int Columns = 17;

        public static GooseTelemetryStream Instance { get; private set; }
        public int Sent { get; private set; }
        public int Dropped { get; private set; }
        public int Batches { get; private set; }
        public int Failures { get; private set; }
        public bool InFlight => m_InFlight;

        readonly float[] m_Buf = new float[Capacity * Columns];
        readonly FrameTiming[] m_Timings = new FrameTiming[1];
        readonly StringBuilder m_Sb = new StringBuilder(8192);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        int m_Count;
        float m_NextSample, m_NextFlush, m_BatchStart, m_LastSampleTime;
        Vector3 m_LastPlayer, m_LastGoose;
        bool m_InFlight, m_WasLive;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("GooseTelemetryStream");
            DontDestroyOnLoad(go);
            go.AddComponent<GooseTelemetryStream>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            var mgr = GooseGameManager.Instance;
            float now = Time.unscaledTime;
            bool live = mgr != null && mgr.Goose != null && mgr.Player != null && (mgr.GooseLive || mgr.State == GooseGameState.GameOver);
            if (live && now >= m_NextSample)
            {
                m_NextSample = now + sampleInterval;
                Sample(mgr, now);
            }
            bool roundEnded = m_WasLive && !live;
            if (m_Count > 0 && (now >= m_NextFlush || m_Count >= Capacity - 1 || roundEnded)) Flush(mgr);
            m_WasLive = live;
        }

        void Sample(GooseGameManager mgr, float now)
        {
            var perf = PerfProbe.Instance;
            int thermal = perf != null ? perf.ThermalState : 0;
            if (thermal > maxThermalState) return;
            if (m_Count >= Capacity) { Dropped++; return; } // a post is late: drop rather than grow
            if (m_Count == 0) { m_BatchStart = now; m_NextFlush = now + flushInterval; }

            Vector3 p = mgr.Player.Position;
            Vector3 g = mgr.Goose.transform.position;
            float dt = m_LastSampleTime > 0f ? now - m_LastSampleTime : 0f;
            float playerSpeed = 0f, gooseSpeed = 0f;
            if (dt > 0.01f && dt < 1f)
            {
                Vector3 dp = p - m_LastPlayer; dp.y = 0f; playerSpeed = dp.magnitude / dt;
                Vector3 dg = g - m_LastGoose; dg.y = 0f; gooseSpeed = dg.magnitude / dt;
            }
            m_LastPlayer = p; m_LastGoose = g; m_LastSampleTime = now;
            Vector3 flat = p - g; flat.y = 0f;

            float frameMs = perf != null && perf.LastFrameMs > 0f ? perf.LastFrameMs : Time.unscaledDeltaTime * 1000f;
            float gpuMs = 0f;
            try { if (FrameTimingManager.GetLatestTimings(1, m_Timings) > 0) gpuMs = (float)m_Timings[0].gpuFrameTime; } catch { }

            int i = m_Count * Columns;
            m_Buf[i + 0] = now - m_BatchStart;
            m_Buf[i + 1] = p.x; m_Buf[i + 2] = p.y; m_Buf[i + 3] = p.z;
            m_Buf[i + 4] = g.x; m_Buf[i + 5] = g.y; m_Buf[i + 6] = g.z;
            m_Buf[i + 7] = flat.magnitude;
            m_Buf[i + 8] = playerSpeed; m_Buf[i + 9] = gooseSpeed;
            m_Buf[i + 10] = mgr.Goose.Tier; m_Buf[i + 11] = mgr.Goose.Anger;
            m_Buf[i + 12] = frameMs > 0.01f ? 1000f / frameMs : 0f;
            m_Buf[i + 13] = frameMs;
            m_Buf[i + 14] = gpuMs;
            m_Buf[i + 15] = thermal;
            m_Buf[i + 16] = (int)mgr.Goose.State;
            m_Count++;
        }

        void Flush(GooseGameManager mgr)
        {
            var brain = mgr != null ? mgr.Brain : null;
            if (brain == null || !brain.Available)
            {
                Dropped += m_Count; m_Count = 0;
                return;
            }
            if (m_InFlight)
            {
                // Keep buffering until the previous post returns; the ring caps the memory.
                if (m_Count >= Capacity - 1) { Dropped += m_Count; m_Count = 0; }
                else m_NextFlush = Time.unscaledTime + 0.5f;
                return;
            }
            int n = m_Count;
            var sb = m_Sb;
            sb.Length = 0;
            sb.Append("{\"device\":\"").Append(brain.DeviceId).Append("\",\"goose\":\"").Append(Escape(mgr.Persona != null ? mgr.Persona.Name : "")).Append("\",\"round\":").Append(mgr.RoundsThisSession);
            sb.Append(",\"dur\":").Append(m_Buf[(n - 1) * Columns].ToString("F2", Inv)).Append(",\"samples\":[");
            for (int s = 0; s < n; s++)
            {
                int i = s * Columns;
                if (s > 0) sb.Append(',');
                sb.Append('[');
                for (int c = 0; c < Columns; c++)
                {
                    if (c > 0) sb.Append(',');
                    float v = m_Buf[i + c];
                    if (c >= 10 && c != 12 && c != 13 && c != 14) sb.Append((int)v); // tier, anger, thermal, state
                    else sb.Append(v.ToString(c == 12 || c == 13 || c == 14 ? "F1" : "F3", Inv));
                }
                sb.Append(']');
            }
            sb.Append("]}");
            m_Count = 0;
            StartCoroutine(Post(brain.brainBaseUrl.TrimEnd('/') + Route, sb.ToString(), n));
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        IEnumerator Post(string url, string json, int samples)
        {
            m_InFlight = true;
            using (var req = new UnityWebRequest(url, "POST") { uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)), downloadHandler = new DownloadHandlerBuffer() })
            {
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 4;
                GooseTelemetry.AddTraceHeaders(req);
                yield return req.SendWebRequest();
                Batches++;
                bool ok = req.result == UnityWebRequest.Result.Success && req.responseCode < 400;
                if (ok)
                {
                    Sent += samples;
                    GooseTelemetry.Increment("elastic.samples", samples);
                    if (verbose || Batches == 1 || Batches % 20 == 0)
                        GooseTelemetry.Log(GooseTelemetry.Level.Info, "elastic.flush", ("samples", samples), ("bytes", json.Length), ("status", (int)req.responseCode), ("batches", Batches), ("dropped", Dropped));
                }
                else
                {
                    Failures++;
                    if (Failures <= 3 || Failures % 20 == 0)
                        GooseTelemetry.Log(GooseTelemetry.Level.Warning, "elastic.flush_failed", ("error", req.error ?? ""), ("status", (int)req.responseCode), ("failures", Failures));
                }
            }
            m_InFlight = false;
        }
    }
}
