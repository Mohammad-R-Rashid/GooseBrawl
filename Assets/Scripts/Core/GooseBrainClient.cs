using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace GooseBrawl
{
    /// <summary>
    /// Talks to the goose-brain Worker (backend/goose-brain): one Cloudflare Agent per device holds the memory, writes the
    /// line with OpenAI and voices it with ElevenLabs. Every call is a Sentry span and carries the trace headers so the
    /// phone's round transaction continues into the Worker. Goes Offline on the first failure (retried every 20 s); the
    /// callers always have an offline fallback, so nothing in the game ever waits on this.
    /// </summary>
    public class GooseBrainClient : MonoBehaviour
    {
        public const string UrlResource = "GooseBrainUrl"; // Assets/Resources/GooseBrainUrl.txt: the deployed Worker URL
        public const string LocalDevUrl = "http://localhost:8787";
        [Tooltip("Deployed Worker, e.g. https://goose-brain.<account>.workers.dev (no trailing slash). Empty = read Resources/GooseBrainUrl.txt; the Editor prefers a running `wrangler dev` on localhost:8787.")]
        public string brainBaseUrl = "";
        [Tooltip("Editor smoke runs force the offline path so they are deterministic.")]
        public bool forceOffline;
        public float requestTimeoutSeconds = 6f;
        public float retryOfflineAfterSeconds = 20f;

        [Serializable] public class LineDto { public string text; public string mood; public string source; public string audioUrl; public string transcript; public int ms; }
        [Serializable] public class SessionDto { public string deviceId; public string name; public string title; public string voice; public int grudge; public int rounds; public bool returning; public LineDto intro; public int ms; }

        public class LineResult
        {
            public string Text, Mood, Source, AudioUrl, Transcript;
            public AudioClip Clip;
            public bool FromBrain => Source == "openai";
        }

        public class SessionResult
        {
            public string Name, Title;
            public int Grudge, Rounds;
            public bool Returning, Female;
            public LineResult Intro;
        }

        public bool Offline { get; private set; }
        public string LastError { get; private set; }
        public int Requests { get; private set; }
        public int Failures { get; private set; }
        public int LinesFromBrain { get; private set; }
        public bool Configured => !string.IsNullOrEmpty(brainBaseUrl) && !brainBaseUrl.Contains("YOUR-ACCOUNT") && brainBaseUrl.StartsWith("http");
        public bool Available => Configured && !forceOffline && !(Offline && Time.realtimeSinceStartup < m_RetryAt);
        public string DeviceId
        {
            get
            {
                if (string.IsNullOrEmpty(m_DeviceId))
                {
                    m_DeviceId = PlayerPrefs.GetString("GooseBrawl.DeviceId", "");
                    if (string.IsNullOrEmpty(m_DeviceId))
                    {
                        m_DeviceId = Guid.NewGuid().ToString("N").Substring(0, 16);
                        PlayerPrefs.SetString("GooseBrawl.DeviceId", m_DeviceId);
                        PlayerPrefs.Save();
                    }
                }
                return m_DeviceId;
            }
        }

        string m_DeviceId;
        float m_RetryAt;

        void Awake()
        {
            if (string.IsNullOrEmpty(brainBaseUrl))
            {
                var ta = Resources.Load<TextAsset>(UrlResource);
                if (ta != null) brainBaseUrl = ta.text.Trim();
            }
            GooseLog.Info("Goose brain URL: '" + brainBaseUrl + "' configured=" + Configured);
            if (Application.isEditor) StartCoroutine(ProbeLocalDev());
        }

        /// <summary>Editor convenience: if `wrangler dev` is up on localhost:8787, use it (the whole pipeline without deploying).</summary>
        IEnumerator ProbeLocalDev()
        {
            using (var req = UnityWebRequest.Get(LocalDevUrl + "/health"))
            {
                req.timeout = 2;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success && req.downloadHandler.text.Contains("\"ok\":true"))
                {
                    brainBaseUrl = LocalDevUrl;
                    Offline = false;
                    GooseLog.Info("Goose brain: using local wrangler dev at " + LocalDevUrl + " (" + req.downloadHandler.text + ")");
                }
            }
        }
        int m_InFlight;
        readonly Dictionary<string, AudioClip> m_Clips = new Dictionary<string, AudioClip>();

        string AgentUrl(string suffix) => brainBaseUrl.TrimEnd('/') + "/agents/goose-brain/" + DeviceId + suffix;
        string AbsoluteUrl(string maybeRelative) => string.IsNullOrEmpty(maybeRelative) ? null : (maybeRelative.StartsWith("http") ? maybeRelative : brainBaseUrl.TrimEnd('/') + maybeRelative);

        /// <summary>Warm the agent, get the goose's name and the pre-voiced intro line. Callback receives null when offline.</summary>
        public IEnumerator StartSession(int roundsThisSession, int gamesPlayed, float bestTime, Action<SessionResult> done)
        {
            if (!Available) { done?.Invoke(null); yield break; }
            string body = "{\"roundsThisSession\":" + roundsThisSession + ",\"gamesPlayed\":" + gamesPlayed + ",\"bestTime\":" + bestTime.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
            SessionDto dto = null;
            yield return Post(AgentUrl("/session"), "brain.session", null, body, null, json => dto = JsonUtility.FromJson<SessionDto>(json));
            if (dto == null || string.IsNullOrEmpty(dto.name)) { done?.Invoke(null); yield break; }
            var result = new SessionResult { Name = dto.name, Title = dto.title, Grudge = dto.grudge, Rounds = dto.rounds, Returning = dto.returning, Female = dto.voice == "female", Intro = ToLine(dto.intro) };
            if (result.Intro != null && !string.IsNullOrEmpty(result.Intro.AudioUrl))
                yield return FetchClip(result.Intro.AudioUrl, clip => result.Intro.Clip = clip);
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "brain.session", ("name", dto.name), ("grudge", dto.grudge), ("returning", dto.returning), ("intro_audio", result.Intro != null && result.Intro.Clip != null), ("ms", dto.ms));
            done?.Invoke(result);
        }

        /// <summary>A game beat -> the goose's line (+ clip). Callback receives null when offline or on failure.</summary>
        public IEnumerator SendEvent(GooseLines.Beat beat, Dictionary<string, object> payload, byte[] wav, Action<LineResult> done)
        {
            if (!Available) { done?.Invoke(null); yield break; }
            string kind = GooseLines.Key(beat);
            string json = "{\"kind\":\"" + kind + "\",\"payload\":" + ToJson(payload) + "}";
            LineDto dto = null;
            yield return Post(AgentUrl("/event"), "brain.event." + kind, kind, json, wav, s => dto = JsonUtility.FromJson<LineDto>(s));
            if (dto == null || string.IsNullOrEmpty(dto.text)) { done?.Invoke(null); yield break; }
            var line = ToLine(dto);
            if (!string.IsNullOrEmpty(line.AudioUrl)) yield return FetchClip(line.AudioUrl, clip => line.Clip = clip);
            if (line.FromBrain) LinesFromBrain++;
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "brain.line", ("beat", kind), ("source", dto.source), ("audio", line.Clip != null), ("transcript", dto.transcript ?? ""), ("ms", dto.ms));
            done?.Invoke(line);
        }

        LineResult ToLine(LineDto d)
        {
            if (d == null || string.IsNullOrEmpty(d.text)) return null;
            return new LineResult { Text = d.text, Mood = d.mood, Source = d.source, AudioUrl = AbsoluteUrl(d.audioUrl), Transcript = d.transcript };
        }

        IEnumerator Post(string url, string spanName, string kind, string json, byte[] wav, Action<string> onJson)
        {
            Requests++;
            m_InFlight++;
            if (PerfProbe.Instance != null) PerfProbe.Instance.BrainRequestInFlight = true;
            UnityWebRequest req;
            if (wav != null)
            {
                var form = new List<IMultipartFormSection>
                {
                    new MultipartFormDataSection("json", json, "application/json"),
                    new MultipartFormFileSection("audio", wav, "yell.wav", "audio/wav")
                };
                req = UnityWebRequest.Post(url, form);
            }
            else
            {
                req = new UnityWebRequest(url, "POST") { uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)), downloadHandler = new DownloadHandlerBuffer() };
                req.SetRequestHeader("Content-Type", "application/json");
            }
            req.timeout = Mathf.CeilToInt(requestTimeoutSeconds);
            GooseTelemetry.AddTraceHeaders(req);
            using (var span = GooseTelemetry.StartSpan("http.client", spanName))
            {
                span.SetData("url", url);
                span.SetData("bytes_up", json.Length + (wav != null ? wav.Length : 0));
                float t0 = Time.realtimeSinceStartup;
                yield return req.SendWebRequest();
                span.SetData("status", (int)req.responseCode);
                float ms = (Time.realtimeSinceStartup - t0) * 1000f;
                span.SetData("ms", ms);
                GooseTelemetry.Distribution("brain.request_ms", ms, "ms", ("kind", kind ?? "session"), ("ok", (req.result == UnityWebRequest.Result.Success && req.responseCode < 400).ToString()));
                m_InFlight--;
                if (PerfProbe.Instance != null) PerfProbe.Instance.BrainRequestInFlight = m_InFlight > 0;
                if (req.result != UnityWebRequest.Result.Success || req.responseCode >= 400)
                {
                    Failures++;
                    LastError = req.error + " (" + req.responseCode + ")";
                    bool network = req.result == UnityWebRequest.Result.ConnectionError || req.responseCode == 0;
                    if (network) { Offline = true; m_RetryAt = Time.realtimeSinceStartup + retryOfflineAfterSeconds; }
                    span.Finish(false);
                    GooseTelemetry.Log(GooseTelemetry.Level.Warning, "brain.request_failed", ("span", spanName), ("error", LastError), ("offline", Offline));
                    req.Dispose();
                    yield break;
                }
                Offline = false;
                string text = req.downloadHandler.text;
                req.Dispose();
                try { onJson(text); }
                catch (Exception e) { GooseTelemetry.CaptureException(e, "brain.parse"); }
            }
        }

        /// <summary>Fetch (and cache) a WAV as an AudioClip. Decoding is the frame hitch the spike logs will show.</summary>
        public IEnumerator FetchClip(string url, Action<AudioClip> done)
        {
            if (string.IsNullOrEmpty(url)) { done?.Invoke(null); yield break; }
            if (m_Clips.TryGetValue(url, out var cached) && cached != null) { done?.Invoke(cached); yield break; }
            using (var req = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.WAV))
            {
                req.timeout = Mathf.CeilToInt(requestTimeoutSeconds);
                GooseTelemetry.AddTraceHeaders(req);
                if (PerfProbe.Instance != null) PerfProbe.Instance.VoiceDecodeInFlight = true;
                using (var span = GooseTelemetry.StartSpan("voice.fetch", "GET audio"))
                {
                    yield return req.SendWebRequest();
                    span.SetData("status", (int)req.responseCode);
                    span.SetData("bytes", (long)req.downloadedBytes);
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        span.Finish(false);
                        if (PerfProbe.Instance != null) PerfProbe.Instance.VoiceDecodeInFlight = false;
                        GooseTelemetry.Log(GooseTelemetry.Level.Warning, "voice.fetch_failed", ("error", req.error), ("url", url));
                        done?.Invoke(null);
                        yield break;
                    }
                    AudioClip clip = null;
                    using (var decode = GooseTelemetry.StartSpan("voice.decode", "wav -> AudioClip"))
                    {
                        try { clip = DownloadHandlerAudioClip.GetContent(req); }
                        catch (Exception e) { GooseTelemetry.CaptureException(e, "voice.decode"); }
                        decode.SetData("seconds", clip != null ? clip.length : 0f);
                    }
                    if (PerfProbe.Instance != null) PerfProbe.Instance.VoiceDecodeInFlight = false;
                    if (clip != null) { clip.name = "Voice_" + url.Substring(Mathf.Max(0, url.Length - 12)); m_Clips[url] = clip; }
                    done?.Invoke(clip);
                }
            }
        }

        static string ToJson(Dictionary<string, object> payload)
        {
            if (payload == null || payload.Count == 0) return "{}";
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var kv in payload)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kv.Key).Append("\":");
                switch (kv.Value)
                {
                    case null: sb.Append("null"); break;
                    case bool b: sb.Append(b ? "true" : "false"); break;
                    case int i: sb.Append(i); break;
                    case float f: sb.Append(f.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); break;
                    case double d: sb.Append(d.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); break;
                    default: sb.Append('"').Append(kv.Value.ToString().Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); break;
                }
            }
            return sb.Append('}').ToString();
        }
    }
}
