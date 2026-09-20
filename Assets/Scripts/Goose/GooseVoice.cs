using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// The goose talks. Lines come from the brain (Cloudflare Agent -> OpenAI -> ElevenLabs) with a deadline; when the
    /// brain is slow or offline the pre-generated bank in Resources/GooseVoice plays, and when even that is missing a
    /// synthesized goose-babble clip carries the subtitle. Single slot: one line at a time, honks are suppressed while
    /// it speaks, the beak bobs, and the audience reads the line in the subtitle pill. All through the goose's own 3D
    /// voice source (behind-you low-pass, slow-motion pitch), so it stays diegetic.
    /// </summary>
    public class GooseVoice : MonoBehaviour
    {
        public int SpokenCount { get; private set; }
        public int FallbackCount { get; private set; }
        public bool Speaking => m_Source != null && m_Source.isPlaying && m_Current != null;
        public string LastText { get; private set; } = "";
        public string LastSource { get; private set; } = "";
        [Tooltip("When the brain wrote a line but could not voice it (TTS quota, deadline), the subtitle carries the words and the goose honks, instead of a bank line that says something else. Off = always the voiced bank line.")]
        public bool subtitleBrainText = true;
        public int TextOnlyCount { get; private set; }

        GooseChaseController m_Goose;
        GooseGameManager m_Mgr;
        AudioSource m_Source;
        AudioClip m_Current;
        readonly Queue<(AudioClip clip, string text, string source)> m_Queue = new Queue<(AudioClip, string, string)>();
        Coroutine m_Player;
        readonly Dictionary<GooseLines.Beat, int> m_Repeats = new Dictionary<GooseLines.Beat, int>();
        static readonly Dictionary<string, AudioClip> s_Babble = new Dictionary<string, AudioClip>();

        AudioHighPassFilter m_HighPass;
        AudioDistortionFilter m_Distortion;
        AudioChorusFilter m_Chorus;

        public void Bind(GooseChaseController goose, GooseGameManager mgr)
        {
            m_Goose = goose;
            m_Mgr = mgr;
            m_Source = goose.voiceSource;
            try { SetCharacter(true); SetCharacter(false); } catch (Exception e) { GooseTelemetry.CaptureException(e, "voice.character"); }
        }

        /// <summary>
        /// The runtime voice character (AudioManager.voice*): the recordings are a calm human narrator, the goose is
        /// a squeaky cartoon bird. Pitch up (in PlayQueue), thin it with a high-pass, cap it with the behind-you
        /// low-pass (GooseChaseController), a chorus warble and a raspy distortion. Only on while a line plays: the
        /// honk recordings on the same source stay as recorded. Re-applied per line so it can be tuned live.
        /// </summary>
        void ApplyCharacter() => SetCharacter(true);

        void SetCharacter(bool on)
        {
            if (m_Source == null || m_Mgr == null || m_Mgr.Audio == null) return;
            var a = m_Mgr.Audio;
            bool hp = on && a.voiceHighPassHz > 0f;
            if (hp)
            {
                if (m_HighPass == null && !m_Source.TryGetComponent(out m_HighPass)) m_HighPass = m_Source.gameObject.AddComponent<AudioHighPassFilter>();
                m_HighPass.cutoffFrequency = a.voiceHighPassHz;
                m_HighPass.highpassResonanceQ = 1.3f;
            }
            if (m_HighPass != null) m_HighPass.enabled = hp;

            bool warble = on && a.voiceWarbleDepth > 0f;
            if (warble)
            {
                if (m_Chorus == null && !m_Source.TryGetComponent(out m_Chorus)) m_Chorus = m_Source.gameObject.AddComponent<AudioChorusFilter>();
                m_Chorus.delay = 12f;
                m_Chorus.rate = Mathf.Max(0.1f, a.voiceWarbleRateHz);
                m_Chorus.depth = Mathf.Clamp01(a.voiceWarbleDepth);
                m_Chorus.dryMix = 1f - 0.5f * a.voiceWarbleMix;
                m_Chorus.wetMix1 = a.voiceWarbleMix;
                m_Chorus.wetMix2 = 0f;
                m_Chorus.wetMix3 = 0f;
            }
            if (m_Chorus != null) m_Chorus.enabled = warble;

            bool dist = on && a.voiceDistortion > 0f;
            if (dist)
            {
                if (m_Distortion == null && !m_Source.TryGetComponent(out m_Distortion)) m_Distortion = m_Source.gameObject.AddComponent<AudioDistortionFilter>();
                m_Distortion.distortionLevel = a.voiceDistortion;
            }
            if (m_Distortion != null) m_Distortion.enabled = dist;
        }

        /// <summary>Speak a beat: ask the brain (if available) with a deadline, else play the bank line.</summary>
        public Coroutine SayBeat(GooseLines.Beat beat, Dictionary<string, object> payload = null, byte[] wav = null, float deadlineSeconds = 2.5f, Action<string> onText = null)
        {
            return StartCoroutine(SayBeatRoutine(beat, payload, wav, deadlineSeconds, onText));
        }

        /// <summary>A line the brain produced earlier (the intro is pre-voiced during the steal beat).</summary>
        public void SayPrepared(GooseBrainClient.LineResult line, GooseLines.Beat fallbackBeat)
        {
            if (line != null && line.Clip != null) Say(line.Clip, line.Text, line.Source ?? "brain");
            else if (line != null && subtitleBrainText && !string.IsNullOrEmpty(line.Text) && line.Source != "bank") SayTextOnly(line.Text, line.Source ?? "brain");
            else SayFallback(fallbackBeat == GooseLines.Beat.Intro && m_Mgr != null && m_Mgr.Persona != null && m_Mgr.Persona.Returning ? GooseLines.Beat.IntroAgain : fallbackBeat);
        }

        public void SayFallback(GooseLines.Beat beat)
        {
            int round = m_Mgr != null ? Mathf.Max(0, m_Mgr.RoundsThisSession - 1) : 0;
            m_Repeats.TryGetValue(beat, out int repeat);
            m_Repeats[beat] = repeat + 1;
            int index = GooseLines.Index(beat, round, repeat);
            string text = GooseLines.Lines[beat][index];
            string folder = m_Mgr != null && m_Mgr.Persona != null ? m_Mgr.Persona.VoiceFolder : GooseLines.MaleVoiceFolder;
            var clip = Resources.Load<AudioClip>("GooseVoice/" + folder + "/" + GooseLines.Key(beat) + "_" + index);
            if (clip == null) clip = Resources.Load<AudioClip>("GooseVoice/" + GooseLines.Key(beat) + "_" + index);
            string source = "bank";
            FallbackCount++;
            if (clip == null)
            {
                // Never a synthesized voice: the subtitle carries the line and the log says which clip is missing.
                GooseTelemetry.Log(GooseTelemetry.Level.Warning, "voice.missing_clip", ("beat", GooseLines.Key(beat)), ("index", index), ("folder", folder));
                if (m_Mgr != null && m_Mgr.UI != null) m_Mgr.UI.ShowSubtitle(GooseLines.Display(text), 2.5f);
                LastText = text;
                return;
            }
            GooseTelemetry.Increment("voice.fallback", 1, ("beat", GooseLines.Key(beat)), ("source", source));
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "voice.fallback", ("beat", GooseLines.Key(beat)), ("source", source), ("reason", m_Mgr != null && m_Mgr.Brain != null ? (m_Mgr.Brain.Available ? "deadline" : (m_Mgr.Brain.Configured ? "offline" : "unconfigured")) : "no-brain"));
            Say(clip, text, source);
        }

        IEnumerator SayBeatRoutine(GooseLines.Beat beat, Dictionary<string, object> payload, byte[] wav, float deadline, Action<string> onText)
        {
            var brain = m_Mgr != null ? m_Mgr.Brain : null;
            if (brain == null || !brain.Available)
            {
                SayFallback(beat);
                onText?.Invoke(LastText);
                yield break;
            }
            GooseBrainClient.LineResult result = null;
            bool done = false;
            StartCoroutine(brain.SendEvent(beat, payload, wav, r => { result = r; done = true; }));
            float t = 0f;
            while (!done && t < deadline) { t += Time.unscaledDeltaTime; yield return null; }
            if (done && result != null && result.Clip != null)
            {
                Say(result.Clip, result.Text, result.Source ?? "brain");
                if (!string.IsNullOrEmpty(result.Transcript) && m_Mgr.Persona != null) m_Mgr.Persona.RecordShout(result.Transcript);
            }
            else if (done && result != null && subtitleBrainText && !string.IsNullOrEmpty(result.Text) && result.Source != "bank")
            {
                // The brain wrote the line (with its Elastic case file) but could not voice it: the words still reach the player.
                SayTextOnly(result.Text, result.Source ?? "brain");
                if (!string.IsNullOrEmpty(result.Transcript) && m_Mgr.Persona != null) m_Mgr.Persona.RecordShout(result.Transcript);
            }
            else
            {
                if (!done) GooseTelemetry.Log(GooseTelemetry.Level.Warning, "voice.deadline", ("beat", GooseLines.Key(beat)), ("deadline_s", deadline));
                SayFallback(beat);
            }
            onText?.Invoke(LastText);
        }

        /// <summary>A line the brain wrote but could not voice: the subtitle carries the words, the goose honks. Never a synthesized voice.</summary>
        void SayTextOnly(string text, string source)
        {
            LastText = text;
            LastSource = source;
            SpokenCount++;
            TextOnlyCount++;
            if (m_Mgr != null && m_Mgr.UI != null) m_Mgr.UI.ShowSubtitle(GooseLines.Display(text), Mathf.Clamp(1.6f + text.Length * 0.06f, 2.2f, 4.5f));
            if (m_Mgr != null && m_Mgr.Audio != null && m_Source != null && !m_Source.isPlaying) m_Mgr.Audio.PlayGooseHonk(AudioManager.HonkKind.Angry, m_Source, 0.5f);
            GooseTelemetry.Increment("voice.spoken", 1, ("source", source + "-text"));
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "voice.text_only", ("source", source), ("text", text));
        }

        public void Say(AudioClip clip, string text, string source)
        {
            if (clip == null || m_Source == null) return;
            m_Queue.Enqueue((clip, text ?? "", source ?? ""));
            if (m_Player == null) m_Player = StartCoroutine(PlayQueue());
        }

        IEnumerator PlayQueue()
        {
            while (m_Queue.Count > 0)
            {
                var (clip, text, source) = m_Queue.Dequeue();
                // Never talk over the tackle slow-mo honk or a honk burst already playing.
                float wait = 0f;
                while (m_Source.isPlaying && wait < 1.2f) { wait += Time.unscaledDeltaTime; yield return null; }
                m_Current = clip;
                LastText = text;
                LastSource = source;
                SpokenCount++;
                GooseTelemetry.Increment("voice.spoken", 1, ("source", source));
                float length = clip.length / Mathf.Max(0.3f, m_Mgr != null && m_Mgr.Audio != null ? m_Mgr.Audio.SlowMoPitch : 1f);
                ApplyCharacter();
                float voicePitch = m_Mgr != null && m_Mgr.Audio != null ? m_Mgr.Audio.voicePitch : 1f;
                length /= Mathf.Max(0.5f, voicePitch);
                m_Goose.SuppressHonks(length + 0.3f);
                m_Source.pitch = (m_Mgr != null && m_Mgr.Audio != null ? m_Mgr.Audio.SlowMoPitch : 1f) * voicePitch;
                using (var span = GooseTelemetry.StartSpan("voice.play", source))
                {
                    span.SetData("seconds", clip.length);
                    span.SetData("text", text);
                    m_Source.clip = clip;
                    m_Source.Play();
                    if (m_Mgr != null && m_Mgr.UI != null) m_Mgr.UI.ShowSubtitle(GooseLines.Display(text), length + 0.6f);
                    if (m_Mgr != null && m_Mgr.Haptics != null) m_Mgr.Haptics.Transient(0.25f, 0.4f, ambient: true);
                    float t = 0f, nextBob = 0f;
                    while (m_Source.isPlaying && m_Source.clip == clip && t < length + 0.5f)
                    {
                        t += Time.unscaledDeltaTime;
                        if (t >= nextBob)
                        {
                            nextBob = t + 0.32f;
                            if (m_Goose.Visual != null && m_Goose.Visual.Procedural != null) m_Goose.Visual.Procedural.TriggerHonkGesture();
                        }
                        yield return null;
                    }
                }
                m_Current = null;
                SetCharacter(false);
                yield return new WaitForSecondsRealtime(0.25f);
            }
            m_Player = null;
        }

        /// <summary>Last-resort voice: synthesized goose babble, one syllable per word, 22.05 kHz mono.</summary>
        public static AudioClip Babble(string text)
        {
            if (s_Babble.TryGetValue(text, out var cached) && cached != null) return cached;
            const int sr = 22050;
            string plain = GooseLines.Display(text);
            int syllables = 0;
            foreach (var w in plain.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) syllables += Mathf.Max(1, Mathf.RoundToInt(w.Length / 3f));
            syllables = Mathf.Clamp(syllables, 2, 14);
            int sylLen = Mathf.RoundToInt(sr * 0.13f), gap = Mathf.RoundToInt(sr * 0.05f);
            int total = syllables * (sylLen + gap) + Mathf.RoundToInt(sr * 0.1f);
            var data = new float[total];
            var rnd = new System.Random(text.GetHashCode());
            int pos = 0;
            for (int s = 0; s < syllables; s++)
            {
                float f0 = 330f + (float)rnd.NextDouble() * 110f;
                float rasp = 0.35f + (float)rnd.NextDouble() * 0.3f;
                for (int i = 0; i < sylLen; i++)
                {
                    float t = i / (float)sr;
                    float k = i / (float)sylLen;
                    float env = Mathf.Sin(Mathf.PI * Mathf.Min(1f, k * 1.15f)) * (1f - 0.3f * k);
                    float pitch = f0 * (1f + 0.08f * Mathf.Sin(k * Mathf.PI));
                    float saw = 2f * ((t * pitch) % 1f) - 1f;
                    float h2 = 0.5f * Mathf.Sin(2f * Mathf.PI * pitch * 2f * t);
                    float noise = ((float)rnd.NextDouble() * 2f - 1f) * rasp * 0.4f;
                    data[pos + i] = Mathf.Clamp((saw * 0.5f + h2 * 0.3f + noise) * env * 0.36f, -1f, 1f);
                }
                pos += sylLen + gap;
            }
            var clip = AudioClip.Create("Babble_" + Mathf.Abs(text.GetHashCode()), total, 1, sr, false);
            clip.SetData(data, 0);
            s_Babble[text] = clip;
            return clip;
        }
    }
}
