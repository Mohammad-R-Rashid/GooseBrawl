using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// The goose talks. Lines come from the brain (Cloudflare Agent -> OpenAI -> ElevenLabs) with a deadline; when the
    /// brain is slow or offline the pre-generated bank in Resources/GooseVoice plays.
    /// Single slot: one line at a time, honks are suppressed while
    /// it speaks, the beak bobs, and the audience reads the line in the subtitle pill. All through a dedicated 3D
    /// speech source (behind-you low-pass, slow-motion pitch), so it stays diegetic.
    /// Timing rule: a reaction is only funny on the beat. Mid-chase reactions never wait for the network and go stale
    /// after a couple of seconds in the queue; the round-end line and the reply to a shout may wait for the brain.
    /// Coherence rule: the subtitle pill only ever shows what the voice is saying. A brain line that arrives without audio
    /// is not shown with a honk in its place; the voiced bank line plays instead. Honks never play over a line.
    /// Shipped setting: the voice is the local bank only (GooseBrainClient.fetchVoiceAudio off), nothing is generated or
    /// fetched live; the brain names the goose, keeps the memory and hears the shouts.
    /// </summary>
    public class GooseVoice : MonoBehaviour
    {
        public int SpokenCount { get; private set; }
        public int FallbackCount { get; private set; }
        public bool Speaking => m_Source != null && m_Source.isPlaying && m_Current != null;
        public string LastText { get; private set; } = "";
        public string LastSource { get; private set; } = "";

        GooseChaseController m_Goose;
        GooseGameManager m_Mgr;
        AudioSource m_Source;
        AudioClip m_Current;
        struct Line
        {
            public AudioClip Clip;
            public string Text, Source;
            /// <summary>Unscaled time after which the line is not worth saying any more (0 = never stale).</summary>
            public float SayBy;
            /// <summary>How long it may wait for a honk already playing on the source before it starts.</summary>
            public float MaxHonkWait;
        }
        readonly Queue<Line> m_Queue = new Queue<Line>();
        Coroutine m_Player;
        /// <summary>Bumped when the round ends; brain waits started before it drop their result.</summary>
        int m_Generation;
        AudioLowPassFilter m_LowPass, m_HonkLowPass;
        readonly Dictionary<string, AudioClip> m_Bank = new Dictionary<string, AudioClip>();
        /// <summary>Lines dropped because their moment had passed (queue backlog, round over).</summary>
        public int StaleCount { get; private set; }
        readonly Dictionary<GooseLines.Beat, int> m_Repeats = new Dictionary<GooseLines.Beat, int>();

        AudioHighPassFilter m_HighPass;
        AudioDistortionFilter m_Distortion;
        AudioChorusFilter m_Chorus;

        public void Bind(GooseChaseController goose, GooseGameManager mgr)
        {
            m_Goose = goose;
            m_Mgr = mgr;
            var speech = new GameObject("Speech");
            speech.transform.SetParent(goose.voiceSource.transform, false);
            m_Source = speech.AddComponent<AudioSource>();
            mgr.Audio.ConfigureGooseSource(m_Source);
            m_Source.priority = 32;
            m_Source.dopplerLevel = 0f; // camera movement must not stretch spoken words
            m_LowPass = speech.AddComponent<AudioLowPassFilter>();
            m_HonkLowPass = goose.voiceSource.GetComponent<AudioLowPassFilter>();
            StartCoroutine(WarmBank());
            try { SetCharacter(true); SetCharacter(false); } catch (Exception e) { GooseTelemetry.CaptureException(e, "voice.character"); }
        }

        /// <summary>
        /// The runtime voice character (AudioManager.voice*): the recordings are a calm human narrator, the goose is
        /// a squeaky cartoon bird. Pitch up (in PlayQueue), thin it with a high-pass, cap it with the behind-you
        /// low-pass (GooseChaseController), a chorus warble and a raspy distortion on the
        /// speech source only; honk recordings use their own source. Re-applied per line so it can be tuned live.
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

        /// <summary>
        /// How long a beat may wait for the brain before the bank line plays. Mid-chase reactions (the ten-second taunt,
        /// bread, a dodged lunge, rage) do not wait at all: a sore-loser line two seconds after the dodge is a non sequitur,
        /// and with the brain slow the wait itself was the delay. A pause reads as intent only where the goose is visibly
        /// thinking (the reply to a shout) or the round is over (the line under the results card). The intro is pre-voiced.
        /// </summary>
        public static float BrainDeadline(GooseLines.Beat beat)
        {
            switch (beat)
            {
                case GooseLines.Beat.Yell: return 3.5f;
                case GooseLines.Beat.Caught:
                case GooseLines.Beat.Outlasted:
                case GooseLines.Beat.Intro:
                case GooseLines.Beat.IntroAgain: return 2.5f;
                default: return 0f;
            }
        }

        /// <summary>Seconds a line stays worth saying once queued; 0 = never stale (the intro, the results line).</summary>
        public static float Freshness(GooseLines.Beat beat)
        {
            switch (beat)
            {
                case GooseLines.Beat.Taunt10:
                case GooseLines.Beat.Bread:
                case GooseLines.Beat.Dodge:
                case GooseLines.Beat.Rage: return 2f;
                case GooseLines.Beat.Yell: return 4f;
                default: return 0f;
            }
        }

        /// <summary>The brain can hand over audio at all (GooseBrainClient.fetchVoiceAudio); otherwise every beat is the local bank, on the beat.</summary>
        bool BrainVoiced => m_Mgr != null && m_Mgr.Brain != null && m_Mgr.Brain.fetchVoiceAudio;

        /// <summary>Speak a beat: the local bank line on the beat, or (brain audio enabled) the brain's line within the beat's deadline. deadlineSeconds &lt; 0 = BrainDeadline(beat).</summary>
        public Coroutine SayBeat(GooseLines.Beat beat, Dictionary<string, object> payload = null, byte[] wav = null, float deadlineSeconds = -1f, Action<string> onText = null)
        {
            float deadline = !BrainVoiced ? 0f : (deadlineSeconds < 0f ? BrainDeadline(beat) : deadlineSeconds);
            return StartCoroutine(SayBeatRoutine(beat, payload, wav, deadline, onText));
        }

        /// <summary>
        /// The round is over (tackled or outlasted): queued mid-chase reactions are dropped, a reaction still playing is cut
        /// (the tackle is what you hear now) and brain replies still in flight for the chase are ignored. The round-end line
        /// queued after this plays normally.
        /// </summary>
        public void RoundEnded() => Interrupt();

        string BankPath(GooseLines.Beat beat, int repeat)
        {
            int round = m_Mgr != null ? Mathf.Max(0, m_Mgr.RoundsThisSession - 1) : 0;
            string folder = m_Mgr != null && m_Mgr.Persona != null ? m_Mgr.Persona.VoiceFolder : GooseLines.MaleVoiceFolder;
            return "GooseVoice/" + folder + "/" + GooseLines.Key(beat) + "_" + GooseLines.Index(beat, round, repeat);
        }

        // Warm only the next line for each beat during the entrance, then one line ahead when used.
        // Async resource loading avoids first-use asset I/O on the chase reaction frame.
        IEnumerator WarmBank()
        {
            foreach (var beat in GooseLines.Lines.Keys) yield return WarmClip(BankPath(beat, 0));
        }

        IEnumerator WarmClip(string path)
        {
            if (m_Bank.ContainsKey(path)) yield break;
            var request = Resources.LoadAsync<AudioClip>(path);
            yield return request;
            var clip = request.asset as AudioClip;
            if (clip == null) yield break;
            m_Bank[path] = clip;
            if (clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
        }

        /// <summary>A line the brain produced earlier (the intro is pre-voiced during the steal beat).</summary>
        public void SayPrepared(GooseBrainClient.LineResult line, GooseLines.Beat fallbackBeat)
        {
            if (line != null && line.Clip != null) Say(line.Clip, line.Text, line.Source ?? "brain");
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
            string path = BankPath(beat, repeat);
            if (!m_Bank.TryGetValue(path, out var clip)) clip = Resources.Load<AudioClip>(path);
            StartCoroutine(WarmClip(BankPath(beat, repeat + 1)));
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
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "voice.fallback", ("beat", GooseLines.Key(beat)), ("source", source), ("reason", m_Mgr != null && m_Mgr.Brain != null ? (!BrainVoiced ? "local" : m_Mgr.Brain.Available ? (BrainDeadline(beat) > 0f ? "deadline" : "reaction") : (m_Mgr.Brain.Configured ? "offline" : "unconfigured")) : "no-brain"));
            Say(clip, text, source, beat);
        }

        IEnumerator SayBeatRoutine(GooseLines.Beat beat, Dictionary<string, object> payload, byte[] wav, float deadline, Action<string> onText)
        {
            float requestedAt = Time.unscaledTime;
            var brain = m_Mgr != null ? m_Mgr.Brain : null;
            bool brainUp = brain != null && brain.Available;
            if (!brainUp || deadline <= 0f)
            {
                SayFallback(beat);
                onText?.Invoke(LastText);
                // The brain still hears the beat (memory, the Elastic case file) and exactly what was said; nothing waits on it.
                if (brainUp)
                {
                    payload = payload ?? new Dictionary<string, object>();
                    payload["spoken"] = LastText ?? "";
                    StartCoroutine(brain.SendEvent(beat, payload, wav, null));
                }
                yield break;
            }
            int generation = m_Generation;
            GooseBrainClient.LineResult result = null;
            bool done = false;
            StartCoroutine(brain.SendEvent(beat, payload, wav, r => { result = r; done = true; }));
            float t = 0f;
            while (!done && t < deadline && generation == m_Generation) { t += Time.unscaledDeltaTime; yield return null; }
            if (generation != m_Generation)
            {
                // The round ended while the brain was thinking: a reply nobody is waiting for any more.
                StaleCount++;
                GooseTelemetry.Log(GooseTelemetry.Level.Info, "voice.dropped", ("reason", "round_over"), ("beat", GooseLines.Key(beat)));
                yield break;
            }
            if (done && result != null && result.Clip != null)
            {
                Say(result.Clip, result.Text, result.Source ?? "brain", beat, requestedAt);
                if (!string.IsNullOrEmpty(result.Transcript) && m_Mgr.Persona != null) m_Mgr.Persona.RecordShout(result.Transcript);
            }
            else
            {
                // Late, failed, or written without audio (the words alone would not match what the goose sounds like it says): the voiced bank line.
                if (!done) GooseTelemetry.Log(GooseTelemetry.Level.Warning, "voice.deadline", ("beat", GooseLines.Key(beat)), ("deadline_s", deadline));
                else if (result != null && !string.IsNullOrEmpty(result.Text)) GooseTelemetry.Log(GooseTelemetry.Level.Info, "voice.unvoiced_line", ("beat", GooseLines.Key(beat)), ("source", result.Source ?? ""), ("text", result.Text));
                if (done && result != null && !string.IsNullOrEmpty(result.Transcript) && m_Mgr.Persona != null) m_Mgr.Persona.RecordShout(result.Transcript);
                SayFallback(beat);
            }
            onText?.Invoke(LastText);
        }

        /// <summary>Something louder takes the stage (the name-called honk): the line playing stops and pending reactions go.</summary>
        public void Interrupt()
        {
            m_Generation++;
            StaleCount += m_Queue.Count;
            m_Queue.Clear();
            // Also cancel a line already dequeued but still waiting to start.
            if (m_Player != null) StopCoroutine(m_Player);
            m_Player = null;
            if (m_Source != null) m_Source.Stop();
            FinishLine();
        }

        void FinishLine()
        {
            m_Current = null;
            SetCharacter(false);
            if (m_Source != null) { m_Source.clip = null; m_Source.pitch = 1f; }
            if (m_Mgr != null && m_Mgr.Audio != null && m_Goose != null)
                m_Mgr.Audio.SetSpeechActive(m_Goose.voiceSource, false);
            if (m_Mgr != null && m_Mgr.UI != null) m_Mgr.UI.HideSubtitle();
        }

        void OnDisable() => Interrupt();
        void OnApplicationPause(bool paused) { if (paused) Interrupt(); }
        void OnApplicationFocus(bool focused) { if (!focused && !Application.isEditor) Interrupt(); }

        /// <summary>Queue a line. With a beat, the beat's freshness applies (a stale reaction is dropped instead of played late).</summary>
        public void Say(AudioClip clip, string text, string source, GooseLines.Beat? beat = null, float requestedAt = -1f)
        {
            if (clip == null || m_Source == null) return;
            float fresh = beat.HasValue ? Freshness(beat.Value) : 0f;
            // One pending reaction: a burst of events must not become a backlog of dialogue.
            StaleCount += m_Queue.Count;
            m_Queue.Clear();
            LastText = text ?? "";
            LastSource = source ?? "";
            m_Queue.Enqueue(new Line
            {
                Clip = clip, Text = text ?? "", Source = source ?? "",
                SayBy = fresh > 0f ? (requestedAt >= 0f ? requestedAt : Time.unscaledTime) + fresh : 0f,
                MaxHonkWait = fresh > 0f ? 0.12f : 0.35f,
            });
            if (m_Player == null) m_Player = StartCoroutine(PlayQueue());
        }

        IEnumerator PlayQueue()
        {
            // Always yield once so StartCoroutine cannot finish before m_Player receives its handle.
            yield return null;
            while (m_Queue.Count > 0)
            {
                var line = m_Queue.Dequeue();
                var clip = line.Clip;
                string text = line.Text, source = line.Source;
                // Let an existing honk finish briefly, then speech owns the voice channel.
                float waitUntil = Time.unscaledTime + line.MaxHonkWait;
                while (m_Goose.voiceSource.isPlaying && Time.unscaledTime < waitUntil) yield return null;
                if (clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
                float loadUntil = Time.unscaledTime + 0.5f;
                while (clip.loadState == AudioDataLoadState.Loading && Time.unscaledTime < loadUntil) yield return null;
                if (clip.loadState != AudioDataLoadState.Loaded) { StaleCount++; continue; }
                if (line.SayBy > 0f && Time.unscaledTime > line.SayBy)
                {
                    // Its moment passed while another line played: late, it would read as a non sequitur.
                    StaleCount++;
                    GooseTelemetry.Increment("voice.stale", 1, ("source", source));
                    GooseTelemetry.Log(GooseTelemetry.Level.Info, "voice.stale", ("text", text), ("late_s", Time.unscaledTime - line.SayBy));
                    continue;
                }
                m_Current = clip;
                LastText = text;
                LastSource = source;
                SpokenCount++;
                GooseTelemetry.Increment("voice.spoken", 1, ("source", source));
                float length = clip.length / Mathf.Max(0.3f, m_Mgr != null && m_Mgr.Audio != null ? m_Mgr.Audio.SlowMoPitch : 1f);
                ApplyCharacter();
                float voicePitch = m_Mgr != null && m_Mgr.Audio != null ? m_Mgr.Audio.voicePitch : 1f;
                length /= Mathf.Max(0.5f, voicePitch);
                m_Mgr.Audio.SetSpeechActive(m_Goose.voiceSource, true);
                m_Source.volume = m_Mgr.Audio.masterVolume;
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
                        m_LowPass.cutoffFrequency = Mathf.Min(m_HonkLowPass != null ? m_HonkLowPass.cutoffFrequency : 22000f, m_Mgr.Audio.voiceLowPassHz);
                        if (t >= nextBob)
                        {
                            nextBob = t + 0.32f;
                            if (m_Goose.Visual != null && m_Goose.Visual.Procedural != null) m_Goose.Visual.Procedural.TriggerHonkGesture();
                        }
                        yield return null;
                    }
                }
                m_Source.Stop();
                FinishLine();
            }
            m_Player = null;
        }

    }
}
