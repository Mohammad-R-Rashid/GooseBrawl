using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GooseBrawl
{
    /// <summary>
    /// Turns scanned geometry into invisible physics colliders on the AREnvironment layer.
    /// - ARKit scene reconstruction meshes (LiDAR) get MeshColliders and no renderer.
    /// - Detected planes (floor and walls) also carry MeshColliders as the fallback.
    /// The goose steering only ever queries this layer. Plane visuals (a soft dot grid) fade in while
    /// scanning and fade out once the nest is placed.
    /// </summary>
    public class AREnvironmentMeshController : MonoBehaviour
    {
        public const string EnvironmentLayerName = "AREnvironment";
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");

        [Tooltip("Draw the detected planes (the dot grid) while scanning / placing. Off: the scan pill is enough and the plane renderers never draw.")]
        public bool showPlaneDots = false;
        [Tooltip("Runtime state: whether the plane visuals are currently wanted (SetPlaneVisualization); gated by showPlaneDots.")]
        public bool visualizePlanes = false;
        public float planePollInterval = 0.5f;
        public float planeFadeSpeed = 1.8f;

        public int EnvironmentLayer { get; private set; }
        public LayerMask EnvironmentMask { get; private set; }
        public int MeshCount { get; private set; }
        public int VerticalPlaneCount { get; private set; }
        public int PlaneCount { get; private set; }
        public float PlaneAlpha { get; private set; }

        /// <summary>True when there is any collider the goose can steer against.</summary>
        public bool HasEnvironmentColliders => GooseGameManager.UseMockAR || MeshCount > 0 || VerticalPlaneCount > 0;
        public bool HasMeshData => MeshCount > 0;

        float m_NextPlanePoll;
        ARMeshManager m_MeshManager;
        readonly List<MeshRenderer> m_PlaneRenderers = new List<MeshRenderer>();
        MaterialPropertyBlock m_Block;
        Color m_PlaneColor = new Color(1f, 0.965f, 0.87f, 0.3f);

        void Awake()
        {
            EnvironmentLayer = LayerMask.NameToLayer(EnvironmentLayerName);
            if (EnvironmentLayer < 0)
            {
                GooseLog.Warn("Layer '" + EnvironmentLayerName + "' is missing. Run Goose Brawl > Setup Project. Falling back to Default.");
                EnvironmentLayer = 0;
            }
            EnvironmentMask = 1 << EnvironmentLayer;
            m_Block = new MaterialPropertyBlock();
        }

        void Start()
        {
            var mgr = GooseGameManager.Instance;
            if (mgr != null && mgr.AR != null && mgr.AR.meshManager != null)
            {
                m_MeshManager = mgr.AR.meshManager;
                m_MeshManager.meshesChanged += OnMeshesChanged;
            }
            if (mgr != null && mgr.Materials != null && mgr.Materials.planeMaterial != null && mgr.Materials.planeMaterial.HasProperty(k_BaseColor))
                m_PlaneColor = mgr.Materials.planeMaterial.GetColor(k_BaseColor);
        }

        void OnDestroy()
        {
            if (m_MeshManager != null) m_MeshManager.meshesChanged -= OnMeshesChanged;
        }

        void OnMeshesChanged(ARMeshesChangedEventArgs args)
        {
            if (args.added != null) foreach (var mf in args.added) ConfigureMesh(mf);
            if (args.updated != null) foreach (var mf in args.updated) ConfigureMesh(mf);
            if (m_MeshManager != null) MeshCount = m_MeshManager.meshes.Count;
        }

        void ConfigureMesh(MeshFilter mf)
        {
            if (mf == null) return;
            var go = mf.gameObject;
            if (go.layer != EnvironmentLayer) go.layer = EnvironmentLayer;
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.enabled) renderer.enabled = false;
            var collider = go.GetComponent<MeshCollider>();
            if (collider == null)
            {
                collider = go.AddComponent<MeshCollider>();
                collider.sharedMesh = mf.sharedMesh;
            }
        }

        void Update()
        {
            // Smooth fade of the plane visuals.
            float target = visualizePlanes ? 1f : 0f;
            if (!Mathf.Approximately(PlaneAlpha, target))
            {
                PlaneAlpha = Mathf.MoveTowards(PlaneAlpha, target, Time.deltaTime * planeFadeSpeed);
                ApplyPlaneAlpha();
            }

            if (Time.time < m_NextPlanePoll) return;
            m_NextPlanePoll = Time.time + planePollInterval;
            var mgr = GooseGameManager.Instance;
            if (mgr == null || mgr.AR == null || mgr.AR.IsMock) return;
            var pm = mgr.AR.planeManager;
            if (pm == null) return;

            int walls = 0, count = 0;
            m_PlaneRenderers.Clear();
            foreach (var plane in pm.trackables)
            {
                count++;
                var go = plane.gameObject;
                if (go.layer != EnvironmentLayer) go.layer = EnvironmentLayer;
                if (plane.alignment == PlaneAlignment.Vertical && plane.trackingState == TrackingState.Tracking) walls++;

                var collider = go.GetComponent<MeshCollider>();
                var mf = go.GetComponent<MeshFilter>();
                if (collider == null && mf != null)
                {
                    collider = go.AddComponent<MeshCollider>();
                }
                if (collider != null && mf != null && collider.sharedMesh != mf.sharedMesh)
                    collider.sharedMesh = mf.sharedMesh;

                bool visible = plane.subsumedBy == null && PlaneAlpha > 0.005f;
                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    if (renderer.enabled != visible) renderer.enabled = visible;
                    if (visible) m_PlaneRenderers.Add(renderer);
                }
                var line = go.GetComponent<LineRenderer>();
                if (line != null && line.enabled) line.enabled = false;
            }
            PlaneCount = count;
            VerticalPlaneCount = walls;
            ApplyPlaneAlpha();
        }

        void ApplyPlaneAlpha()
        {
            var c = m_PlaneColor;
            c.a *= PlaneAlpha;
            for (int i = 0; i < m_PlaneRenderers.Count; i++)
            {
                var r = m_PlaneRenderers[i];
                if (r == null) continue;
                r.GetPropertyBlock(m_Block);
                m_Block.SetColor(k_BaseColor, c);
                r.SetPropertyBlock(m_Block);
                if (PlaneAlpha <= 0.005f && r.enabled) r.enabled = false;
            }
        }

        public void SetPlaneVisualization(bool visible)
        {
            visualizePlanes = visible && showPlaneDots;
            m_NextPlanePoll = 0f;
        }
    }
}
