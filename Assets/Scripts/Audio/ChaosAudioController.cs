using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// The hype layer. During the chase: your own heartbeat (a beat-driven one-shot so the haptic thump lands on
    /// the same frame as the sound), panting that builds with time on the run, a distant flock of angry geese in
    /// rage mode, and random goose wing-flaps / feathers so the goose never goes quiet.
    /// Design rule: NO background music, ever; only sounds that exist in the world.
    /// </summary>
    public class ChaosAudioController : MonoBehaviour
    {
        [Header("Heartbeat (one-shot per beat, haptic in sync)")]
        [Range(0f, 1f)] public float heartbeatVolume = 0.55f;
        [Tooltip("Beats per second at danger 0 and 1.")]
        public float heartRateCalm = 0.9f;
        public float heartRatePanic = 2.2f;
        [Tooltip("Danger level at which the heartbeat becomes audible.")]
        public float heartbeatStart = 0.25f;

        [Header("Breathing")]
        [Range(0f, 1f)] public float breathVolume = 0.3f;
        public float breathPitchCalm = 0.9f;
        public float breathPitchPanic = 1.5f;
        [Tooltip("Seconds of chase before you start audibly puffing.")]
        public float breathStartTime = 6f;

        [Header("Flock ambience (rage mode)")]
        [Range(0f, 1f)] public float flockVolume = 0.15f;

        [Header("Random goose flaps")]
        public float flapIntervalMin = 4f;
        public float flapIntervalMax = 9f;

        public int HeartbeatCount { get; private set; }

        AudioSource m_Breath, m_Flock;
        AudioClip m_FlockClip;
        float m_NextFlap;
        float m_NextBeat;
        float m_Danger;
        bool m_Active;

        void Start()
        {
            var mgr = GooseGameManager.Instance;
            var audio = mgr != null ? mgr.Audio : FindAnyObjectByType<AudioManager>();
            m_Breath = MakeSource("Breathing", audio != null ? audio.BreathClip : null);
            m_FlockClip = Resources.Load<AudioClip>("GooseAudio/yellowstone_canada_geese");
            if (m_FlockClip == null) m_FlockClip = Resources.Load<AudioClip>("GooseAudio/geese_honking_distant");
            m_Flock = MakeSource("Flock", m_FlockClip);
            // The flock is somewhere outside: dull it.
            var lp = m_Flock.gameObject.AddComponent<AudioLowPassFilter>();
            lp.cutoffFrequency = 2600f;
        }

        AudioSource MakeSource(string name, AudioClip clip)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = true;
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            s.volume = 0f;
            return s;
        }

        void Update()
        {
            var mgr = GooseGameManager.Instance;
            if (mgr == null) return;
            bool chase = mgr.ChaseActive && mgr.Goose != null && !mgr.IsPaused;
            float dt = Time.unscaledDeltaTime;

            if (chase && !m_Active) Begin();
            if (!chase && m_Active) End();
            if (!m_Active)
            {
                Fade(m_Breath, 0f, dt * 2f);
                Fade(m_Flock, 0f, dt * 1.5f);
                return;
            }

            var goose = mgr.Goose;
            m_Danger = Mathf.Lerp(m_Danger, goose.Danger01, dt * 3f);
            float chaseTime = goose.ChaseTime;
            float master = mgr.Audio != null ? mgr.Audio.masterVolume : 1f;
            bool slowMo = Time.timeScale < 0.99f;
            float scale = slowMo ? 0.6f : 1f; // duck a little in the slow-motion catch

            // Heartbeat: from medium danger upward, rate and intensity rise with danger; haptic on the same tick.
            float heartLevel = Mathf.Clamp01((m_Danger - heartbeatStart) / (1f - heartbeatStart));
            if (heartLevel > 0.01f && Time.unscaledTime >= m_NextBeat)
            {
                float rate = Mathf.Lerp(heartRateCalm, heartRatePanic, heartLevel);
                m_NextBeat = Time.unscaledTime + 1f / rate;
                HeartbeatCount++;
                if (mgr.Audio != null) mgr.Audio.PlayHeartbeat(heartbeatVolume * Mathf.Lerp(0.35f, 1f, heartLevel) * master * scale, Mathf.Lerp(0.95f, 1.15f, heartLevel));
                if (mgr.Haptics != null) mgr.Haptics.Play(HapticsService.Pattern.Heartbeat, Mathf.Lerp(0.3f, 1f, heartLevel));
            }

            // Breathing: builds with time on the run, panics with danger.
            float stamina = Mathf.Clamp01((chaseTime - breathStartTime) / 20f);
            float breathLevel = Mathf.Clamp01(stamina * 0.6f + m_Danger * 0.6f);
            Fade(m_Breath, breathVolume * breathLevel * master * scale, dt * 1f);
            m_Breath.pitch = Mathf.Lerp(breathPitchCalm, breathPitchPanic, breathLevel) * (slowMo ? 0.75f : 1f);

            // Distant flock: rage mode only.
            Fade(m_Flock, goose.Rage ? flockVolume * master : 0f, dt * 0.6f);

            // Random flaps / feathers so the goose never goes quiet.
            if (Time.time >= m_NextFlap && goose.State != GooseState.GameOver)
            {
                m_NextFlap = Time.time + Random.Range(flapIntervalMin, flapIntervalMax) * (goose.Rage ? 0.5f : 1f);
                if (mgr.Audio != null) mgr.Audio.PlayFlap(goose.bodySource);
                goose.Visual.FeatherBurst(8);
                goose.Visual.Procedural.TriggerHonkGesture();
            }
        }

        void Begin()
        {
            m_Active = true;
            m_Danger = 0f;
            m_NextFlap = Time.time + Random.Range(2f, 4f);
            m_NextBeat = Time.unscaledTime + 0.3f;
            Play(m_Breath);
            Play(m_Flock);
        }

        void End()
        {
            m_Active = false;
        }

        static void Play(AudioSource s)
        {
            if (s == null || s.clip == null) return;
            if (!s.isPlaying) s.Play();
        }

        static void Fade(AudioSource s, float target, float step)
        {
            if (s == null) return;
            s.volume = Mathf.MoveTowards(s.volume, target, step);
            if (s.volume <= 0.001f && s.isPlaying && target <= 0f) s.Stop();
            else if (s.volume > 0.001f && !s.isPlaying && s.clip != null) s.Play();
        }
    }
}
