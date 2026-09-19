using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace GooseBrawl
{
    /// <summary>
    /// Publishes the ARKit environment depth texture and the display transform as shader globals so the
    /// shadow catcher can hide shadows behind real objects without z-fighting against the noisy LiDAR
    /// depth in the depth buffer. Off (keyword disabled) in mock mode or without a depth texture.
    /// </summary>
    public class AREnvDepthPublisher : MonoBehaviour
    {
        const string Keyword = "_GB_ENVDEPTH";
        static readonly int k_Tex = Shader.PropertyToID("_GB_EnvDepth");
        static readonly int k_Valid = Shader.PropertyToID("_GB_EnvDepthValid");
        static readonly int k_Transform = Shader.PropertyToID("_GB_DisplayTransform");

        public AROcclusionManager occlusionManager;
        public ARCameraManager cameraManager;
        public bool Active { get; private set; }

        bool m_Subscribed;

        void OnEnable()
        {
            Shader.DisableKeyword(Keyword);
            Shader.SetGlobalFloat(k_Valid, 0f);
        }

        void Update()
        {
            if (GooseGameManager.UseMockAR) return;
            if (occlusionManager == null) occlusionManager = FindAnyObjectByType<AROcclusionManager>();
            if (cameraManager == null) cameraManager = FindAnyObjectByType<ARCameraManager>();
            if (!m_Subscribed && cameraManager != null)
            {
                cameraManager.frameReceived += OnFrame;
                m_Subscribed = true;
            }
            Texture2D tex = null;
            if (occlusionManager != null && occlusionManager.enabled)
            {
                try { tex = occlusionManager.environmentDepthTexture; } catch { tex = null; }
            }
            bool active = tex != null;
            if (active)
            {
                Shader.SetGlobalTexture(k_Tex, tex);
                Shader.SetGlobalFloat(k_Valid, 1f);
                if (!Active) Shader.EnableKeyword(Keyword);
            }
            else if (Active)
            {
                Shader.SetGlobalFloat(k_Valid, 0f);
                Shader.DisableKeyword(Keyword);
            }
            Active = active;
        }

        void OnFrame(ARCameraFrameEventArgs args)
        {
            if (args.displayMatrix.HasValue) Shader.SetGlobalMatrix(k_Transform, args.displayMatrix.Value);
        }

        void OnDisable()
        {
            if (m_Subscribed && cameraManager != null) cameraManager.frameReceived -= OnFrame;
            m_Subscribed = false;
            Shader.DisableKeyword(Keyword);
            Shader.SetGlobalFloat(k_Valid, 0f);
            Active = false;
        }
    }
}
