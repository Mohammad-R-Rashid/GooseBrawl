using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GooseBrawl
{
    /// <summary>
    /// Drives the post-processing volume and the key light from game state: a dark vignette that turns
    /// red with danger, lens/chromatic impact pulses, desaturation in slow motion and rage. Replaces the
    /// old red UI paint. The scene Volume (built by Setup) references an asset profile whose overrides all
    /// have non-zero baselines so URP never strips their shader variants; at runtime we animate the
    /// auto-cloned instance (volume.profile), never the asset.
    /// </summary>
    public class CinematicLookController : MonoBehaviour
    {
        public static CinematicLookController Instance { get; private set; }

        [Header("Baselines (match Assets/Settings/GoosedPostFX.asset)")]
        public float vignetteBase = 0.2f;
        public float vignetteDangerMax = 0.46f;
        public Color vignetteBaseColor = new Color(0.02f, 0.015f, 0.02f);
        public Color vignetteDangerColor = new Color(0.32f, 0.02f, 0.01f);
        public float saturationBase = 6f;
        public float contrastBase = 8f;
        public float caBase = 0.01f;
        public float lensBase = 0.01f;

        [Header("Key light")]
        public Light keyLight;
        public float keyLightElevation = 62f;

        Volume m_Volume;
        Vignette m_Vignette;
        LensDistortion m_Lens;
        ChromaticAberration m_CA;
        ColorAdjustments m_Color;
        FilmGrain m_Grain;
        Bloom m_Bloom;
        bool m_ReducedFX;
        float m_GrainBase = -1f, m_BloomBase = -1f;

        float m_Danger, m_DangerTarget;
        float m_Impact;        // 0..1 decaying pulse
        float m_SlowMo, m_SlowMoTarget;
        float m_Rage, m_RageTarget;

        void Awake()
        {
            Instance = this;
        }

        void Start()
        {
            m_Volume = FindAnyObjectByType<Volume>(FindObjectsInactive.Include);
            if (m_Volume == null)
            {
                // Editor without setup: build an in-memory profile so play mode still works (a build always has the asset).
                var go = new GameObject("PostFX Volume (runtime)");
                m_Volume = go.AddComponent<Volume>();
                m_Volume.isGlobal = true;
                var p = ScriptableObject.CreateInstance<VolumeProfile>();
                p.Add<Vignette>(true); p.Add<LensDistortion>(true); p.Add<ChromaticAberration>(true); p.Add<ColorAdjustments>(true); p.Add<Bloom>(true);
                m_Volume.sharedProfile = p;
            }
            var profile = m_Volume.profile;
            profile.TryGet(out m_Vignette);
            profile.TryGet(out m_Lens);
            profile.TryGet(out m_CA);
            profile.TryGet(out m_Color);
            profile.TryGet(out m_Grain);
            profile.TryGet(out m_Bloom);
            if (m_Grain != null) m_GrainBase = m_Grain.intensity.value;
            if (m_Bloom != null) m_BloomBase = m_Bloom.intensity.value;
            if (keyLight == null)
            {
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { keyLight = l; break; }
            }
            Apply();
        }

        public void SetDanger(float danger01) => m_DangerTarget = Mathf.Clamp01(danger01);
        public void SetSlowMotion(bool on) => m_SlowMoTarget = on ? 1f : 0f;
        public void SetRage(bool on) => m_RageTarget = on ? 1f : 0f;

        /// <summary>Adaptive quality tier 3: drop the grain, bloom and chromatic aberration (the cheap-to-lose effects).</summary>
        public void SetReducedFX(bool reduced)
        {
            m_ReducedFX = reduced;
            if (m_Grain != null && m_GrainBase >= 0f) m_Grain.intensity.value = reduced ? 0f : m_GrainBase;
            if (m_Bloom != null && m_BloomBase >= 0f) m_Bloom.intensity.value = reduced ? 0f : m_BloomBase;
        }
        public bool ReducedFX => m_ReducedFX;

        /// <summary>Short lens-distortion + chromatic pulse for impacts (lunge launch, catch, crack).</summary>
        public void Impact(float strength)
        {
            m_Impact = Mathf.Max(m_Impact, Mathf.Clamp01(strength));
        }

        /// <summary>Point the key light over the player's shoulder so shadows fall away from the phone.</summary>
        public void OrientKeyLight(Vector3 playerFlatForward)
        {
            if (keyLight == null) return;
            playerFlatForward.y = 0f;
            if (playerFlatForward.sqrMagnitude < 1e-4f) playerFlatForward = Vector3.forward;
            float yaw = Mathf.Atan2(playerFlatForward.x, playerFlatForward.z) * Mathf.Rad2Deg - 28f;
            keyLight.transform.rotation = Quaternion.Euler(keyLightElevation, yaw, 0f);
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            m_Danger = Mathf.MoveTowards(m_Danger, m_DangerTarget, dt * 1.4f);
            m_SlowMo = Mathf.MoveTowards(m_SlowMo, m_SlowMoTarget, dt * 5f);
            m_Rage = Mathf.MoveTowards(m_Rage, m_RageTarget, dt * 1.5f);
            m_Impact = Mathf.MoveTowards(m_Impact, 0f, dt * 3.4f);
            Apply();
        }

        void Apply()
        {
            float impact = m_Impact * m_Impact;
            if (m_Vignette != null)
            {
                float v = Mathf.Lerp(vignetteBase, vignetteDangerMax, m_Danger);
                v = Mathf.Max(v, Mathf.Lerp(vignetteBase, 0.42f, m_SlowMo));
                v += impact * 0.08f;
                m_Vignette.intensity.value = Mathf.Clamp(v, vignetteBase, 0.6f);
                m_Vignette.color.value = Color.Lerp(vignetteBaseColor, vignetteDangerColor, Mathf.Max(m_Danger, impact * 0.6f));
                m_Vignette.smoothness.value = 0.5f;
            }
            if (m_Lens != null)
            {
                m_Lens.intensity.value = Mathf.Min(-lensBase, -lensBase - 0.14f * impact);
                m_Lens.xMultiplier.value = 1f;
                m_Lens.yMultiplier.value = 1f;
            }
            if (m_CA != null) m_CA.intensity.value = m_ReducedFX ? 0f : Mathf.Max(caBase, 0.55f * impact + 0.12f * m_Danger * m_Danger);
            if (m_Color != null)
            {
                m_Color.saturation.value = saturationBase - 45f * m_SlowMo - 14f * m_Rage * (1f - m_SlowMo);
                m_Color.contrast.value = contrastBase + 6f * m_SlowMo;
                m_Color.postExposure.value = impact * 0.25f;
            }
        }
    }
}
