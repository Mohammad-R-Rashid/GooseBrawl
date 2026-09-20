using System;
using System.Collections;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Listens on the microphone during the chase and detects a shout: a loud burst well above the adaptive noise
    /// floor. The reaction is local and instant (the goose flinches); the 2-second window around the shout is encoded
    /// as WAV and sent to the brain so the goose can answer what was actually said. Gated for a moment after the
    /// goose's own honks (the phone speaker would otherwise trigger it) and while it speaks. Everything it decides is
    /// logged with the levels, which is how the honk gate was tuned.
    /// </summary>
    public class YellDetector : MonoBehaviour
    {
        [Tooltip("Master switch (turn off if the play-and-record session makes the phone too quiet).")]
        public bool yellEnabled = true;
        [Tooltip("How far above the noise floor (dB) a shout must be.")]
        public float thresholdDb = 15f;
        [Tooltip("Absolute minimum level (dBFS) regardless of the floor.")]
        public float absoluteFloorDb = -20f;
        [Tooltip("Listen on the Editor's microphone too (off: the Editor mock and the smoke test use SimulateYell / the Y key).")]
        public bool listenInEditor = false;
        public float minLoudSeconds = 0.12f;
        public float cooldownSeconds = 6f;
        [Tooltip("Ignore the mic this long after a goose honk plays (the speaker is right next to the mic).")]
        public float honkGateSeconds = 0.5f;
        public float floorSeconds = 3f;
        public int sampleRate = 16000;
        public float captureBefore = 0.3f;
        public float captureAfter = 1.7f;
        [Tooltip("Speech results arriving later than this after the shout are dropped (the goose has moved on).")]
        public float speechDeadlineAfterYell = 2.8f;

        public event Action<float> Yelled;
        public event Action<byte[]> YellAudioReady;
        /// <summary>The transcript of the last shout ("" when nothing was understood or it came too late).</summary>
        public event Action<string> TranscriptReady;
        /// <summary>A transcript of the last shout is on its way (on-device speech recognition).</summary>
        public bool RecognitionPending { get; private set; }
        public bool SpeechReady => m_SpeechAvailable && GooseSpeech.AuthorizationStatus == 3;
        bool m_SpeechAvailable;
        string m_FakeTranscript;

        public bool Listening { get; private set; }
        public bool Available { get; private set; }
        public float LevelDb { get; private set; } = -80f;
        public float FloorDb { get; private set; } = -60f;
        public int YellCount { get; private set; }
        public int GatedCount { get; private set; }
        public float LastYellTime { get; private set; } = -99f;

        AudioClip m_Clip;
        int m_LastPos;
        float m_LoudSince = -1f;
        float[] m_Buffer = new float[4096];
        const int ClipSeconds = 10;
        bool m_Permitted;
        int m_CaptureGeneration;
        public bool MicrophoneWarm => m_Clip != null;
        public int MicrophoneStarts { get; private set; }

        // Keep the hardware running between active-game beats. Listening only arms detection;
        // toggling Microphone.Start/End at chase/catch rebuilds iOS audio and cuts off speech.
        bool PrepareMicrophone()
        {
            if (m_Clip != null) return true;
            if (!yellEnabled || (Application.isEditor && !listenInEditor)) return false;
            if (Microphone.devices == null || Microphone.devices.Length == 0) { Available = false; return false; }
            m_Clip = Microphone.Start(null, true, ClipSeconds, sampleRate);
            Available = m_Clip != null;
            if (Available) MicrophoneStarts++;
            return Available;
        }

        /// <summary>Trigger the OS microphone prompt on the title screen so it never interrupts the chase.</summary>
        public void WarmUpPermission()
        {
            if (!yellEnabled || m_Permitted) return;
            m_Permitted = true;
            try
            {
                PrepareMicrophone();
                // On-device speech (does it know its name?): probe and ask once, right behind the microphone prompt.
                m_SpeechAvailable = GooseSpeech.Available;
                if (m_SpeechAvailable && GooseSpeech.AuthorizationStatus == 0) GooseSpeech.RequestAuthorization();
                GooseTelemetry.Log(GooseTelemetry.Level.Info, "speech.warmup", ("available", m_SpeechAvailable), ("auth", GooseSpeech.AuthorizationStatus));
            }
            catch (Exception e)
            {
                Available = false;
                GooseTelemetry.Log(GooseTelemetry.Level.Warning, "yell.mic_unavailable", ("error", e.Message));
            }
        }

        public void BeginListening()
        {
            if (!yellEnabled || Listening) return;
            if (Application.isEditor && !listenInEditor) return;
            try
            {
                if (!PrepareMicrophone()) return;
                Listening = true;
                m_LastPos = Mathf.Max(0, Microphone.GetPosition(null));
                m_LoudSince = -1f;
                FloorDb = -50f;
                if (PerfProbe.Instance != null) PerfProbe.Instance.MicListening = true;
                GooseTelemetry.Log(GooseTelemetry.Level.Info, "yell.listening", ("rate", sampleRate), ("device", Microphone.devices[0]));
            }
            catch (Exception e)
            {
                Available = false;
                GooseTelemetry.Log(GooseTelemetry.Level.Warning, "yell.start_failed", ("error", e.Message));
            }
        }

        public void EndListening()
        {
            Listening = false;
            m_CaptureGeneration++;
            RecognitionPending = false;
            m_FakeTranscript = null;
            m_LoudSince = -1f;
            if (PerfProbe.Instance != null) PerfProbe.Instance.MicListening = false;
        }

        public void ReleaseMicrophone(bool restorePlayback = true)
        {
            EndListening();
            if (m_Clip == null) return;
            try { Microphone.End(null); } catch { }
            Destroy(m_Clip);
            m_Clip = null;
            m_Permitted = false;
            var mgr = GooseGameManager.Instance;
            if (restorePlayback && mgr != null && mgr.Audio != null) mgr.Audio.EnsurePlaybackSession("mic_release");
        }

        void OnDisable() => ReleaseMicrophone(false);
        void OnApplicationPause(bool paused) { if (paused) ReleaseMicrophone(false); }
        void OnApplicationFocus(bool focused) { if (!focused && !Application.isEditor) ReleaseMicrophone(false); }

        void Update()
        {
            if (!Listening || m_Clip == null) return;
            int pos;
            try { pos = Microphone.GetPosition(null); } catch { return; }
            int total = m_Clip.samples;
            int n = pos - m_LastPos;
            if (n < 0) n += total;
            if (n <= 0 || n > total) return;
            // Read the newest samples (two chunks across the wrap).
            if (m_Buffer.Length < n) m_Buffer = new float[Mathf.NextPowerOfTwo(n)];
            double sum = 0; int count = 0;
            int first = Mathf.Min(n, total - m_LastPos);
            if (first > 0 && m_Clip.GetData(m_Buffer, m_LastPos))
            {
                for (int i = 0; i < first; i++) { float v = m_Buffer[i]; sum += v * v; }
                count += first;
            }
            int rest = n - first;
            if (rest > 0 && m_Clip.GetData(m_Buffer, 0))
            {
                for (int i = 0; i < rest; i++) { float v = m_Buffer[i]; sum += v * v; }
                count += rest;
            }
            m_LastPos = pos;
            if (count == 0) return;
            float rms = Mathf.Sqrt((float)(sum / count));
            LevelDb = 20f * Mathf.Log10(rms + 1e-6f);

            float dt = Time.unscaledDeltaTime;
            bool loud = LevelDb > FloorDb + thresholdDb && LevelDb > absoluteFloorDb;
            if (!loud) FloorDb = Mathf.Lerp(FloorDb, LevelDb, Mathf.Clamp01(dt / floorSeconds));
            else FloorDb = Mathf.Lerp(FloorDb, LevelDb, Mathf.Clamp01(dt / (floorSeconds * 12f)));

            if (!loud) { m_LoudSince = -1f; return; }
            if (m_LoudSince < 0f) m_LoudSince = Time.unscaledTime;
            if (Time.unscaledTime - m_LoudSince < minLoudSeconds) return;
            if (Time.unscaledTime - LastYellTime < cooldownSeconds) return;

            var mgr = GooseGameManager.Instance;
            string gate = null;
            if (Time.timeScale < 0.99f) gate = "slowmo";
            else if (mgr != null && mgr.Audio != null && Time.time - mgr.Audio.LastHonkTime < mgr.Audio.LastHonkLength + honkGateSeconds) gate = "honk";
            else if (mgr != null && mgr.Goose != null && mgr.Goose.Voice != null && mgr.Goose.Voice.Speaking) gate = "goose_speaking";
            if (gate != null)
            {
                GatedCount++;
                m_LoudSince = -1f;
                GooseTelemetry.Increment("yell.gated", 1, ("reason", gate));
                GooseTelemetry.Log(GooseTelemetry.Level.Info, "yell.gated", ("reason", gate), ("level_db", LevelDb), ("floor_db", FloorDb), ("since_honk_s", mgr != null && mgr.Audio != null ? Time.time - mgr.Audio.LastHonkTime : -1f));
                return;
            }
            Trigger(false);
        }

        void Trigger(bool simulated)
        {
            LastYellTime = Time.unscaledTime;
            YellCount++;
            m_LoudSince = -1f;
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "yell.trigger", ("level_db", LevelDb), ("floor_db", FloorDb), ("simulated", simulated), ("count", YellCount));
            GooseTelemetry.Increment("yell.trigger", 1, ("simulated", simulated.ToString()));
            RecognitionPending = m_FakeTranscript != null || (!simulated && SpeechReady);
            Yelled?.Invoke(LevelDb);
            StartCoroutine(CaptureWindow(simulated));
        }

        /// <summary>Editor / smoke test: a synthetic shout through the same path (optionally with a fake transcript, e.g. the goose's name).</summary>
        public void SimulateYell(string fakeTranscript = null)
        {
            m_FakeTranscript = fakeTranscript;
            LevelDb = -8f;
            Trigger(true);
        }

        IEnumerator CaptureWindow(bool simulated)
        {
            int generation = m_CaptureGeneration;
            yield return new WaitForSecondsRealtime(captureAfter);
            if (generation != m_CaptureGeneration) yield break;
            byte[] wav = null;
            try
            {
                int samples = Mathf.RoundToInt((captureBefore + captureAfter) * sampleRate);
                var data = new float[samples];
                if (simulated || m_Clip == null || !Listening)
                {
                    var rnd = new System.Random(42);
                    for (int i = 0; i < samples; i++)
                    {
                        float env = Mathf.Sin(Mathf.PI * i / (float)samples);
                        data[i] = ((float)rnd.NextDouble() * 2f - 1f) * 0.5f * env * (0.6f + 0.4f * Mathf.Sin(i * 0.02f));
                    }
                }
                else
                {
                    int pos = Microphone.GetPosition(null);
                    int total = m_Clip.samples;
                    int start = pos - samples;
                    if (start < 0) start += total;
                    int first = Mathf.Min(samples, total - start);
                    var tmp = new float[Mathf.Max(first, samples - first)];
                    if (first > 0 && m_Clip.GetData(tmp, start)) Array.Copy(tmp, 0, data, 0, first);
                    int rest = samples - first;
                    if (rest > 0 && m_Clip.GetData(tmp, 0)) Array.Copy(tmp, 0, data, first, rest);
                }
                wav = EncodeWav(data, sampleRate);
            }
            catch (Exception e)
            {
                GooseTelemetry.CaptureException(e, "yell.capture");
            }
            if (wav != null) YellAudioReady?.Invoke(wav);
            if (RecognitionPending) StartCoroutine(RecognizeRoutine(wav, LastYellTime));
        }

        /// <summary>Feed the clip to the on-device recogniser and publish the transcript (or "" on timeout / failure).</summary>
        IEnumerator RecognizeRoutine(byte[] wav, float yellTime)
        {
            int generation = m_CaptureGeneration;
            string text = "";
            int status = -1;
            float t0 = Time.unscaledTime;
            if (m_FakeTranscript != null)
            {
                text = m_FakeTranscript; m_FakeTranscript = null; status = 1;
                yield return new WaitForSecondsRealtime(0.4f);
            }
            else if (wav != null)
            {
                string path = null;
                try { path = System.IO.Path.Combine(Application.temporaryCachePath, "yell.wav"); System.IO.File.WriteAllBytes(path, wav); }
                catch (Exception e) { GooseTelemetry.CaptureException(e, "speech.write"); path = null; }
                float budget = Mathf.Max(0.6f, yellTime + speechDeadlineAfterYell - Time.unscaledTime);
                var mgr = GooseGameManager.Instance;
                string context = ShoutClassifier.ContextCsv(mgr != null && mgr.Persona != null ? mgr.Persona.Name : "");
                int id = path != null ? GooseSpeech.Recognize(path, context, budget) : -1;
                if (id >= 0)
                {
                    while (Time.unscaledTime - t0 < budget + 0.3f)
                    {
                        status = GooseSpeech.Poll(id, out text);
                        if (status != 0) break;
                        yield return null;
                    }
                }
            }
            if (generation != m_CaptureGeneration) yield break;
            RecognitionPending = false;
            bool late = Time.unscaledTime > yellTime + speechDeadlineAfterYell + 0.3f;
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "speech.result", ("ms", (Time.unscaledTime - t0) * 1000f), ("status", status), ("late", late), ("text", text ?? ""));
            TranscriptReady?.Invoke(!late && status == 1 ? (text ?? "") : "");
        }

        public static byte[] EncodeWav(float[] samples, int rate)
        {
            int bytes = samples.Length * 2;
            var buf = new byte[44 + bytes];
            void Str(int o, string s) { for (int i = 0; i < s.Length; i++) buf[o + i] = (byte)s[i]; }
            void U32(int o, uint v) { buf[o] = (byte)v; buf[o + 1] = (byte)(v >> 8); buf[o + 2] = (byte)(v >> 16); buf[o + 3] = (byte)(v >> 24); }
            void U16(int o, ushort v) { buf[o] = (byte)v; buf[o + 1] = (byte)(v >> 8); }
            Str(0, "RIFF"); U32(4, (uint)(36 + bytes)); Str(8, "WAVE"); Str(12, "fmt "); U32(16, 16); U16(20, 1); U16(22, 1);
            U32(24, (uint)rate); U32(28, (uint)(rate * 2)); U16(32, 2); U16(34, 16); Str(36, "data"); U32(40, (uint)bytes);
            int p = 44;
            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)Mathf.Clamp(Mathf.RoundToInt(samples[i] * 32767f), -32768, 32767);
                buf[p++] = (byte)s; buf[p++] = (byte)(s >> 8);
            }
            return buf;
        }
    }
}
