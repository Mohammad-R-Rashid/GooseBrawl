using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

namespace GooseBrawl
{
    /// <summary>
    /// Makes the virtual light match the room. ARKit world tracking reports the ambient intensity (lumens)
    /// and colour temperature of the camera image; we map those onto the key light and the ambient
    /// tri-light every frame with heavy smoothing (the estimate follows auto-exposure and jumps).
    /// In the Editor mock there is no camera, so fixed values are used.
    /// </summary>
    public class ARLightRig : MonoBehaviour
    {
        public Light keyLight;
        public float baseIntensity = 1.15f;
        [Tooltip("Lumens reported by ARKit that count as a normally lit room.")]
        public float neutralLumens = 1000f;
        public float minScale = 0.35f, maxScale = 1.3f;
        public float smoothing = 4f;

        public Color skyAmbient = new Color(0.62f, 0.6f, 0.58f);
        public Color equatorAmbient = new Color(0.46f, 0.45f, 0.45f);
        public Color groundAmbient = new Color(0.22f, 0.2f, 0.19f);

        public float CurrentScale { get; private set; } = 1f;
        public float CurrentKelvin { get; private set; } = 5200f;
        public bool ReceivingEstimates { get; private set; }

        ARCameraManager m_CameraManager;
        float m_TargetScale = 1f;
        Color m_TargetColor = Color.white;
        Color m_CurrentColor = Color.white;
        bool m_Subscribed;

        void OnEnable()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            ApplyAmbient(1f);
        }

        void Start()
        {
            if (keyLight == null)
            {
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { keyLight = l; break; }
            }
            if (keyLight != null)
            {
                keyLight.intensity = baseIntensity;
                m_CurrentColor = m_TargetColor = new Color(1f, 0.96f, 0.9f);
                keyLight.color = m_CurrentColor;
            }
        }

        void Update()
        {
            if (!m_Subscribed && !GooseGameManager.UseMockAR)
            {
                if (m_CameraManager == null) m_CameraManager = FindAnyObjectByType<ARCameraManager>();
                if (m_CameraManager != null)
                {
                    m_CameraManager.frameReceived += OnFrame;
                    m_Subscribed = true;
                }
            }
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * smoothing);
            CurrentScale = Mathf.Lerp(CurrentScale, m_TargetScale, k);
            m_CurrentColor = Color.Lerp(m_CurrentColor, m_TargetColor, k * 0.5f);
            if (keyLight != null)
            {
                keyLight.intensity = baseIntensity * CurrentScale;
                keyLight.color = m_CurrentColor;
            }
            ApplyAmbient(CurrentScale);
        }

        void OnDisable()
        {
            if (m_Subscribed && m_CameraManager != null) m_CameraManager.frameReceived -= OnFrame;
            m_Subscribed = false;
        }

        void OnFrame(ARCameraFrameEventArgs args)
        {
            var le = args.lightEstimation;
            if (le.averageIntensityInLumens.HasValue)
            {
                ReceivingEstimates = true;
                m_TargetScale = Mathf.Clamp(le.averageIntensityInLumens.Value / neutralLumens, minScale, maxScale);
            }
            else if (le.averageBrightness.HasValue)
            {
                ReceivingEstimates = true;
                m_TargetScale = Mathf.Clamp(le.averageBrightness.Value * 2f, minScale, maxScale);
            }
            if (le.averageColorTemperature.HasValue)
            {
                CurrentKelvin = Mathf.Clamp(le.averageColorTemperature.Value, 3200f, 7000f);
                Color c = Mathf.CorrelatedColorTemperatureToRGB(CurrentKelvin);
                // Only a hint of the room's colour: the camera feed is already white-balanced, so a strong tint reads as a coloured gel.
                m_TargetColor = Color.Lerp(Color.white, c, 0.28f);
            }
        }

        void ApplyAmbient(float scale)
        {
            RenderSettings.ambientSkyColor = skyAmbient * scale;
            RenderSettings.ambientEquatorColor = equatorAmbient * scale;
            RenderSettings.ambientGroundColor = groundAmbient * scale;
        }
    }
}
