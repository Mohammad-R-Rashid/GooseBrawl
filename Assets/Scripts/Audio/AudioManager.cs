using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// All sound events in one place. Design rule: no music, only sounds that exist in the room.
    /// The honks are real goose recordings; every other sound is synthesized at startup (ProceduralAudio) in
    /// several round-robin variants. Goose sounds play through 3D sources on the goose; a small pool of world
    /// sources handles one-shots at arbitrary positions (egg crack, nest); UI feedback stays 2D.
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        public enum HonkKind { Far, Mid, Near, Angry, Rage, Dramatic }

        public const string SoundPrefsKey = "GooseBrawl.Sound";

        [Header("Real recordings (loaded from Resources/GooseAudio, procedural fallback)")]
        [Tooltip("Folder under Resources holding sliced single-honk clips.")]
        public string honkResourceFolder = "GooseAudio/honks";
        public bool preferRealHonks = true;

        [Header("Mix")]
        [Range(0f, 1f)] public float masterVolume = 1f;
        [Tooltip("Goose sources: full volume inside this distance.")]
        public float gooseMinDistance = 0.8f;
        [Tooltip("Goose sources: silent beyond this distance.")]
        public float gooseMaxDistance = 10f;

        // Synth clips (generated in Awake)
        public AudioClip honkClip, honkClip2, honkClip3, angryHonkClip, rageHonkClip, dramaticHonkClip;
        public AudioClip whooshClip => m_Whooshes != null && m_Whooshes.Length > 0 ? m_Whooshes[0] : null;
        public AudioClip BreathClip { get; private set; }
        public AudioClip HeartbeatClip { get; private set; }

        public bool UsingRealHonks => preferRealHonks && m_RealHonks.Length > 0;
        public int RealHonkCount => m_RealHonks.Length;
        public bool Muted { get; private set; }
        /// <summary>Low-pass cutoff every goose source should respect right now (slow motion muffles the world).</summary>
        public float SlowMotionCutoff => Time.timeScale < 0.99f ? 900f : 22000f;
        /// <summary>Pitch multiplier for one-shots started during slow motion.</summary>
        public float SlowMoPitch => Time.timeScale < 0.99f ? 0.72f : 1f;

        AudioSource m_UI, m_Voice2D, m_Distant;
        readonly List<AudioSource> m_WorldPool = new List<AudioSource>();
        int m_NextWorld;
        AudioClip[] m_RealHonks = new AudioClip[0];
        AudioClip m_LongestRealHonk;
        AudioClip[] m_Footsteps, m_Flaps, m_Whooshes, m_Dashes, m_EggCracks;
        AudioClip m_WingBeatLoop, m_CatchImpact, m_EggPickup, m_NestPlace, m_ButtonTap;
        readonly List<int> m_HonkBag = new List<int>();
        int m_FootstepIdx, m_FlapIdx, m_WhooshIdx, m_DashIdx;
        AnimationCurve m_Rolloff;
        AudioReverbZone m_Reverb;
        Coroutine m_GameOverHonks;
        bool m_Suspended;
        readonly HashSet<AudioSource> m_SpeakingSources = new HashSet<AudioSource>();
        readonly Dictionary<AudioSource, Coroutine> m_HonkBursts = new Dictionary<AudioSource, Coroutine>();

        void Awake()
        {
            ConfigureDsp();
            LoadRealHonks();
            GenerateClips();
            // x = distance / maxDistance. Full volume to 1.2 m, still 70% at 3 m: the goose stays the loudest thing in the room.
            m_Rolloff = new AnimationCurve(
                new Keyframe(0f, 1f, 0f, 0f), new Keyframe(0.12f, 1f, 0f, -0.9f), new Keyframe(0.3f, 0.72f, -0.9f, -0.9f),
                new Keyframe(0.6f, 0.36f, -0.6f, -0.6f), new Keyframe(1f, 0f, -0.1f, 0f));

            m_UI = MakeSource("UI", 0f);
            m_Voice2D = MakeSource("Voice2D", 0f);
            m_Distant = MakeSource("Distant", 0f);
            var lp = m_Distant.gameObject.AddComponent<AudioLowPassFilter>();
            lp.cutoffFrequency = 2400f;
            for (int i = 0; i < 3; i++)
            {
                var s = MakeSource("World" + i, 1f);
                ConfigureGooseSource(s);
                m_WorldPool.Add(s);
            }
            SetMuted(PlayerPrefs.GetInt(SoundPrefsKey, 1) == 0, save: false);
        }

        static void ConfigureDsp()
        {
            try
            {
                var cfg = AudioSettings.GetConfiguration();
                if (cfg.dspBufferSize > 512)
                {
                    cfg.dspBufferSize = 512; // ~11 ms at 48 kHz: keeps haptics and hits in sync
                    AudioSettings.Reset(cfg);
                }
            }
            catch (System.Exception e) { GooseLog.Warn("DSP buffer config skipped: " + e.Message); }
        }

        AudioSource MakeSource(string name, float spatial)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = spatial;
            return s;
        }

        void LoadRealHonks()
        {
            try
            {
                var clips = Resources.LoadAll<AudioClip>(honkResourceFolder);
                if (clips != null && clips.Length > 0)
                {
                    m_RealHonks = clips;
                    foreach (var c in clips) if (m_LongestRealHonk == null || c.length > m_LongestRealHonk.length) m_LongestRealHonk = c;
                    GooseLog.Info("Loaded " + clips.Length + " real goose honk recordings.");
                }
            }
            catch (System.Exception e)
            {
                GooseLog.Warn("Could not load honk recordings: " + e.Message);
            }
        }

        void GenerateClips()
        {
            honkClip = ProceduralAudio.Honk("Honk", 1f, 0.42f, 1);
            honkClip2 = ProceduralAudio.Honk("Honk2", 1.12f, 0.3f, 2);
            honkClip3 = ProceduralAudio.Honk("Honk3", 0.92f, 0.55f, 3);
            angryHonkClip = ProceduralAudio.AngryHonk("AngryHonk", 2, 1f, 7);
            rageHonkClip = ProceduralAudio.AngryHonk("RageHonk", 3, 1.1f, 11);
            dramaticHonkClip = ProceduralAudio.DramaticHonk("DramaticHonk");
            m_Footsteps = new AudioClip[6];
            for (int i = 0; i < m_Footsteps.Length; i++) m_Footsteps[i] = ProceduralAudio.Footstep("Footstep" + i, 100 + i * 13);
            m_Flaps = new AudioClip[4];
            for (int i = 0; i < m_Flaps.Length; i++) m_Flaps[i] = ProceduralAudio.Flap("Flap" + i, 200 + i * 17);
            m_Whooshes = new AudioClip[3];
            for (int i = 0; i < m_Whooshes.Length; i++) m_Whooshes[i] = ProceduralAudio.Whoosh("Whoosh" + i, 300 + i * 19);
            m_Dashes = new AudioClip[2];
            for (int i = 0; i < m_Dashes.Length; i++) m_Dashes[i] = ProceduralAudio.DashLaunch("Dash" + i, 400 + i * 23);
            m_EggCracks = new AudioClip[3];
            for (int i = 0; i < m_EggCracks.Length; i++) m_EggCracks[i] = ProceduralAudio.EggCrack("EggCrack" + i, 500 + i * 29);
            m_WingBeatLoop = ProceduralAudio.WingBeatLoop("WingBeatLoop");
            m_CatchImpact = ProceduralAudio.CatchImpact("CatchImpact");
            m_EggPickup = ProceduralAudio.EggPickup("EggPickup");
            m_NestPlace = ProceduralAudio.NestPlace("NestPlace");
            m_ButtonTap = ProceduralAudio.ButtonTap("ButtonTap");
            HeartbeatClip = ProceduralAudio.HeartbeatOneShot("Heartbeat");
            BreathClip = ProceduralAudio.BreathLoop("Breathing");
        }

        /// <summary>Configure a 3D source for the goose: custom rolloff so the last two metres are dramatic, a little spread.</summary>
        public void ConfigureGooseSource(AudioSource s)
        {
            if (s == null) return;
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.minDistance = gooseMinDistance;
            s.maxDistance = gooseMaxDistance;
            s.rolloffMode = AudioRolloffMode.Custom;
            if (m_Rolloff != null) s.SetCustomCurve(AudioSourceCurveType.CustomRolloff, m_Rolloff);
            s.spread = 30f;
        }

        /// <summary>Mute persists between launches (public demo spaces).</summary>
        public void SetMuted(bool muted, bool save = true)
        {
            Muted = muted;
            AudioListener.volume = muted ? 0f : 1f;
            if (save)
            {
                PlayerPrefs.SetInt(SoundPrefsKey, muted ? 0 : 1);
                PlayerPrefs.Save();
            }
        }

        /// <summary>Mild room reverb around the play area so the goose sounds like it is in the room, not in your head.</summary>
        public void CreateRoomReverb(Vector3 center)
        {
            if (m_Reverb == null)
            {
                var go = new GameObject("RoomReverb");
                go.transform.SetParent(transform, true);
                m_Reverb = go.AddComponent<AudioReverbZone>();
                m_Reverb.reverbPreset = AudioReverbPreset.Room;
                m_Reverb.minDistance = 5f;
                m_Reverb.maxDistance = 11f;
                m_Reverb.room = -1400;
                m_Reverb.reverbDelay = 0.02f;
                m_Reverb.decayTime = 0.55f;
            }
            m_Reverb.transform.position = center;
        }

        AudioSource NextWorld(Vector3 position)
        {
            var s = m_WorldPool[m_NextWorld];
            m_NextWorld = (m_NextWorld + 1) % m_WorldPool.Count;
            s.transform.position = position;
            return s;
        }

        static AudioClip Pick(AudioClip[] set, ref int idx)
        {
            if (set == null || set.Length == 0) return null;
            var c = set[idx % set.Length];
            idx = (idx + 1 + (set.Length > 2 ? Random.Range(0, 2) : 0)) % set.Length;
            return c;
        }

        // ---- UI / 2D ---------------------------------------------------------------------------
        /// <summary>Title screen: a single goose somewhere far away, muffled.</summary>
        public void PlayStart()
        {
            StopGameOverHonks();
            var clip = UsingRealHonks ? PickRealHonk() : honkClip;
            if (clip == null) return;
            m_Distant.pitch = Random.Range(0.86f, 0.95f);
            m_Distant.PlayOneShot(clip, 0.35f * masterVolume);
        }

        public void PlayButtonTap() => PlayUI(m_ButtonTap, 0.6f, Random.Range(0.95f, 1.05f));
        /// <summary>Camera shutter for the photo with the goose: the tap, brighter and sharper.</summary>
        public void PlayShutter() => PlayUI(m_ButtonTap, 0.9f, 1.6f);
        public void PlayEggPickup() => PlayUI(m_EggPickup, 0.9f, Random.Range(0.97f, 1.03f));

        /// <summary>Twigs settling, played where the nest is.</summary>
        public void PlayPlace(Vector3 position)
        {
            var s = NextWorld(position);
            s.pitch = Random.Range(0.95f, 1.05f);
            s.PlayOneShot(m_NestPlace, 0.8f * masterVolume);
        }

        /// <summary>Being tackled by a goose, played at the goose.</summary>
        public void PlayCaught(Vector3 position)
        {
            var s = NextWorld(position);
            s.pitch = SlowMoPitch;
            s.PlayOneShot(m_CatchImpact, 1f * masterVolume);
        }

        /// <summary>Egg hitting the floor, played in 3D where it landed.</summary>
        public void PlayEggCrack(Vector3 position)
        {
            var s = NextWorld(position);
            s.pitch = Random.Range(0.95f, 1.05f) * SlowMoPitch;
            s.PlayOneShot(m_EggCracks[Random.Range(0, m_EggCracks.Length)], 1f * masterVolume);
        }

        /// <summary>Game over: four real honks, each lower than the last. Made of goose, not of music.</summary>
        public void PlayGameOver()
        {
            StopGameOverHonks();
            m_GameOverHonks = StartCoroutine(GameOverHonks());
        }

        void StopGameOverHonks()
        {
            if (m_GameOverHonks == null) return;
            StopCoroutine(m_GameOverHonks);
            m_GameOverHonks = null;
        }

        IEnumerator GameOverHonks()
        {
            float[] pitches = { 1f, 0.93f, 0.85f, 0.72f };
            for (int i = 0; i < pitches.Length; i++)
            {
                var clip = UsingRealHonks ? PickRealHonk() : honkClip;
                if (clip == null) yield break;
                m_Voice2D.pitch = pitches[i];
                m_Voice2D.PlayOneShot(clip, (i == pitches.Length - 1 ? 0.9f : 0.7f) * masterVolume);
                yield return new WaitForSecondsRealtime(i == pitches.Length - 2 ? 0.55f : 0.42f);
            }
            m_GameOverHonks = null;
        }

        /// <summary>New best time: one short goose call, without an unrelated field recording.</summary>
        public void PlayNewBest()
        {
            if (m_Suspended || m_SpeakingSources.Count > 0) return;
            var clip = UsingRealHonks ? PickRealHonk() : honkClip;
            if (clip == null) return;
            m_Distant.pitch = 1.05f;
            m_Distant.PlayOneShot(clip, 0.7f * masterVolume);
        }

        public void PlayHeartbeat(float volume, float pitch)
        {
            if (HeartbeatClip == null) return;
            m_UI.pitch = pitch;
            m_UI.PlayOneShot(HeartbeatClip, volume);
        }

        void PlayUI(AudioClip clip, float volume, float pitch)
        {
            if (clip == null || m_UI == null) return;
            m_UI.pitch = pitch;
            m_UI.PlayOneShot(clip, volume * masterVolume);
        }

        // ---- World / 3D ------------------------------------------------------------------------
        /// <summary>Play a honk at a world position (e.g. from far away before the goose exists).</summary>
        public void PlayHonkAt(Vector3 position, HonkKind kind)
        {
            var s = NextWorld(position);
            PlayGooseHonk(kind, s, kind == HonkKind.Far ? 0f : 0.6f);
        }

        /// <summary>Honk through a goose source. Real recordings when available; danger nudges pitch and volume.</summary>
#if UNITY_IOS && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern int GooseAudio_ApplyPlayback();
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern int GooseAudio_Release();
#endif
        float m_NextSessionCheck;
        public int PlaybackSessionApplied { get; private set; }

        /// <summary>
        /// iOS: the game must be audible with the ringer switch off. Unity's default session category (Ambient) obeys the switch,
        /// so we move to Playback whenever the microphone is not recording (recording needs PlayAndRecord, which also ignores the switch).
        /// </summary>
        public void EnsurePlaybackSession(string reason)
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                if (Microphone.IsRecording(null)) return;
                int r = GooseAudio_ApplyPlayback();
                if (r == 1) { PlaybackSessionApplied++; GooseTelemetry.Log(GooseTelemetry.Level.Info, "audio.session_playback", ("reason", reason), ("applied", PlaybackSessionApplied)); }
                else if (r == 0) GooseTelemetry.Log(GooseTelemetry.Level.Warning, "audio.session_playback_failed", ("reason", reason));
            }
            catch (System.Exception e) { GooseLog.Warn("Audio session: " + e.Message); }
#endif
        }

        void Start()
        {
            EnsurePlaybackSession("start");
            m_NextSessionCheck = Time.unscaledTime + 8f;
        }

        void Update()
        {
            if (Time.unscaledTime >= m_NextSessionCheck)
            {
                m_NextSessionCheck = Time.unscaledTime + 8f;
                EnsurePlaybackSession("periodic");
            }
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) Suspend("pause");
            else Resume("resume");
        }

        void OnApplicationFocus(bool focus)
        {
            if (focus) Resume("focus");
            else Suspend("focus_lost");
        }

        /// <summary>
        /// Leaving the app (home, app switcher, a system sheet): nothing of the game may keep sounding. Unity pauses the
        /// player loop but not the mixer, so a line, a honk burst or the game-over honks would run on for a few seconds; the
        /// Playback session we hold would also keep other apps' audio silenced. Everything stops, the listener pauses and the
        /// audio hardware is handed back.
        /// </summary>
        void Suspend(string reason)
        {
            if (m_Suspended || Application.isEditor) return; // the Editor loses window focus all the time; only a player leaves the app
            m_Suspended = true;
            foreach (var burst in m_HonkBursts.Values) if (burst != null) StopCoroutine(burst);
            m_HonkBursts.Clear();
            var goose = GooseGameManager.Instance != null ? GooseGameManager.Instance.Goose : null;
            if (goose != null)
            {
                if (goose.Voice != null) goose.Voice.Interrupt();
                if (goose.voiceSource != null) goose.voiceSource.Stop();
                if (goose.bodySource != null) goose.bodySource.Stop();
            }
            StopGameOverHonks();
            if (m_UI != null) m_UI.Stop();
            if (m_Voice2D != null) m_Voice2D.Stop();
            if (m_Distant != null) m_Distant.Stop();
            foreach (var s in m_WorldPool) if (s != null) s.Stop();
            AudioListener.pause = true;
#if UNITY_IOS && !UNITY_EDITOR
            try { GooseAudio_Release(); } catch (System.Exception e) { GooseLog.Warn("Audio session release: " + e.Message); }
#endif
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "audio.suspended", ("reason", reason));
        }

        void Resume(string reason)
        {
            if (!m_Suspended) return;
            m_Suspended = false;
            AudioListener.pause = false;
            EnsurePlaybackSession(reason);
        }

        [Header("Goose voice character (spoken lines only; tune in play mode)")]
        [Tooltip("Pitch multiplier on spoken lines (1 = as recorded; 1.3 = squeaky cartoon bird, lines play faster too).")]
        [Range(0.7f, 1.6f)] public float voicePitch = 1.1f;
        [Tooltip("High-pass cutoff on spoken lines in Hz (0 = off; 380 thins it toward a beak).")]
        public float voiceHighPassHz = 220f;
        [Tooltip("Distortion on spoken lines (0 = off; 0.25 = a raspy quack).")]
        [Range(0f, 0.6f)] public float voiceDistortion = 0.1f;
        [Tooltip("Low-pass cap on spoken lines in Hz (band-limits toward a quack; 22000 = off).")]
        public float voiceLowPassHz = 22000f;
        [Tooltip("Chorus warble on spoken lines: depth 0-1 (0 = off), rate in Hz, wet mix.")]
        [Range(0f, 1f)] public float voiceWarbleDepth = 0f;
        public float voiceWarbleRateHz = 5.5f;
        [Range(0f, 1f)] public float voiceWarbleMix = 0.45f;

        /// <summary>When the goose last honked (the yell detector ignores the mic for a moment after it).</summary>
        public float LastHonkTime { get; private set; } = -99f;
        public float LastHonkLength { get; private set; }

        public void SetSpeechActive(AudioSource honkSource, bool active)
        {
            if (honkSource == null) return;
            if (!active) { m_SpeakingSources.Remove(honkSource); return; }
            m_SpeakingSources.Add(honkSource);
            StopGameOverHonks();
            if (m_Voice2D != null) m_Voice2D.Stop();
            if (m_Distant != null) m_Distant.Stop();
            CancelHonkBurst(honkSource);
            honkSource.Stop();
        }

        void CancelHonkBurst(AudioSource source)
        {
            if (!m_HonkBursts.TryGetValue(source, out var burst)) return;
            if (burst != null) StopCoroutine(burst);
            m_HonkBursts.Remove(source);
        }

        public void PlayGooseHonk(HonkKind kind, AudioSource source, float danger, float pitchOffset = 0f)
        {
            if (m_Suspended || (source != null && m_SpeakingSources.Contains(source))) return;
            if (source != null) CancelHonkBurst(source);
            LastHonkTime = Time.time;
            LastHonkLength = HonkLength(kind);
            if (source == null)
            {
                PlayUI(kind == HonkKind.Angry ? angryHonkClip : honkClip, 0.8f, 1f);
                return;
            }
            float volume = kind == HonkKind.Far ? 0.7f : (kind == HonkKind.Mid ? 0.85f : 1f);
            float pitch = (1f + danger * 0.1f + pitchOffset + Random.Range(-0.04f, 0.04f)) * SlowMoPitch;
            if (UsingRealHonks)
            {
                switch (kind)
                {
                    case HonkKind.Angry:
                        m_HonkBursts[source] = StartCoroutine(HonkBurst(source, 2, pitch + 0.04f, volume));
                        return;
                    case HonkKind.Rage:
                        m_HonkBursts[source] = StartCoroutine(HonkBurst(source, 3, pitch + 0.08f, volume));
                        return;
                    case HonkKind.Dramatic:
                        source.pitch = 0.72f * SlowMoPitch;
                        source.PlayOneShot(m_LongestRealHonk != null ? m_LongestRealHonk : PickRealHonk(), volume * masterVolume);
                        return;
                    default:
                        source.pitch = pitch;
                        source.PlayOneShot(PickRealHonk(), volume * masterVolume);
                        return;
                }
            }

            AudioClip clip;
            switch (kind)
            {
                case HonkKind.Angry: clip = angryHonkClip; break;
                case HonkKind.Rage: clip = rageHonkClip != null ? rageHonkClip : angryHonkClip; pitch += 0.05f; break;
                case HonkKind.Dramatic: clip = dramaticHonkClip; pitch = 0.95f; break;
                default: clip = PickHonk(); break;
            }
            if (clip == null) return;
            source.pitch = pitch;
            source.PlayOneShot(clip, volume * masterVolume);
        }

        IEnumerator HonkBurst(AudioSource source, int count, float pitch, float volume)
        {
            for (int i = 0; i < count; i++)
            {
                if (source == null || m_Suspended || m_SpeakingSources.Contains(source)) break;
                var clip = PickRealHonk();
                if (clip == null) yield break;
                source.pitch = pitch + i * 0.05f;
                source.PlayOneShot(clip, volume * masterVolume);
                yield return new WaitForSecondsRealtime(Mathf.Min(clip.length / Mathf.Max(0.1f, source.pitch) * 0.85f, 0.45f) * Random.Range(0.85f, 1.15f));
            }
            if (source != null) m_HonkBursts.Remove(source);
        }

        /// <summary>Shuffle bag: every honk plays once before any repeats.</summary>
        AudioClip PickRealHonk()
        {
            if (m_RealHonks.Length == 0) return honkClip;
            if (m_HonkBag.Count == 0)
            {
                for (int i = 0; i < m_RealHonks.Length; i++) m_HonkBag.Add(i);
                for (int i = m_HonkBag.Count - 1; i > 0; i--)
                {
                    int j = Random.Range(0, i + 1);
                    (m_HonkBag[i], m_HonkBag[j]) = (m_HonkBag[j], m_HonkBag[i]);
                }
            }
            int idx = m_HonkBag[m_HonkBag.Count - 1];
            m_HonkBag.RemoveAt(m_HonkBag.Count - 1);
            return m_RealHonks[idx];
        }

        AudioClip PickHonk()
        {
            float r = Random.value;
            if (r < 0.55f || honkClip2 == null) return honkClip;
            if (r < 0.8f || honkClip3 == null) return honkClip2;
            return honkClip3;
        }

        /// <summary>Length of the clip a honk kind would play (for syncing UI bubbles).</summary>
        public float HonkLength(HonkKind kind)
        {
            if (UsingRealHonks)
            {
                switch (kind)
                {
                    case HonkKind.Angry: return 0.9f;
                    case HonkKind.Rage: return 1.3f;
                    case HonkKind.Dramatic: return m_LongestRealHonk != null ? m_LongestRealHonk.length / 0.72f : 1.2f;
                    default: return 0.45f;
                }
            }
            switch (kind)
            {
                case HonkKind.Angry: return angryHonkClip != null ? angryHonkClip.length : 0.6f;
                case HonkKind.Rage: return rageHonkClip != null ? rageHonkClip.length : 0.9f;
                case HonkKind.Dramatic: return dramaticHonkClip != null ? dramaticHonkClip.length : 1f;
                default: return honkClip != null ? honkClip.length : 0.4f;
            }
        }

        public void PlayFlap(AudioSource source)
        {
            var clip = Pick(m_Flaps, ref m_FlapIdx);
            if (source == null || clip == null) return;
            source.pitch = Random.Range(0.9f, 1.1f) * SlowMoPitch;
            source.PlayOneShot(clip, 0.8f * masterVolume);
        }

        /// <summary>A throw leaving the hand: whoosh from the hand position.</summary>
        public void PlayThrowWhoosh(Vector3 position)
        {
            var s = NextWorld(position);
            var clip = Pick(m_Whooshes, ref m_WhooshIdx);
            if (clip == null) return;
            s.pitch = 1.15f * SlowMoPitch;
            s.PlayOneShot(clip, 0.7f * masterVolume);
        }

        public void PlayWhoosh(AudioSource source)
        {
            var clip = Pick(m_Whooshes, ref m_WhooshIdx);
            if (source == null || clip == null) return;
            source.pitch = Random.Range(0.95f, 1.1f) * SlowMoPitch;
            source.PlayOneShot(clip, 0.9f * masterVolume);
        }

        public void PlayFootstep(AudioSource source, float intensity)
        {
            var clip = Pick(m_Footsteps, ref m_FootstepIdx);
            if (source == null || clip == null) return;
            source.pitch = Random.Range(0.9f, 1.12f) * SlowMoPitch;
            source.PlayOneShot(clip, Mathf.Lerp(0.22f, 0.5f, intensity) * masterVolume);
        }

        /// <summary>A bread roll landing on the floor: a soft, low footstep.</summary>
        public void PlayBreadThud(Vector3 position)
        {
            var s = NextWorld(position);
            var clip = Pick(m_Footsteps, ref m_FootstepIdx);
            if (clip == null) return;
            s.pitch = 0.78f * SlowMoPitch;
            s.PlayOneShot(clip, 0.45f * masterVolume);
        }

        /// <summary>Flap-dash launch: push-off, wing and air, layered.</summary>
        public void PlayDash(AudioSource source)
        {
            var clip = Pick(m_Dashes, ref m_DashIdx);
            if (source == null || clip == null) return;
            source.pitch = Random.Range(0.95f, 1.08f) * SlowMoPitch;
            source.PlayOneShot(clip, 0.85f * masterVolume);
        }

        /// <summary>The lunge launch: the dash launch plus a bigger whoosh.</summary>
        public void PlayLungeLaunch(AudioSource source)
        {
            PlayDash(source);
            var w = Pick(m_Whooshes, ref m_WhooshIdx);
            if (source != null && w != null) source.PlayOneShot(w, 1f * masterVolume);
        }

        /// <summary>Continuous wing beats while airborne (fly-in): a loop on the body source.</summary>
        public void StartWingBeat(AudioSource source)
        {
            if (source == null || m_WingBeatLoop == null) return;
            source.clip = m_WingBeatLoop;
            source.loop = true;
            source.volume = 0.6f * masterVolume;
            source.pitch = 1f;
            if (!source.isPlaying) source.Play();
        }

        public void StopWingBeat(AudioSource source)
        {
            if (source == null || source.clip != m_WingBeatLoop) return;
            source.Stop();
            source.clip = null;
            source.loop = false;
            source.volume = 1f;
        }
    }
}
