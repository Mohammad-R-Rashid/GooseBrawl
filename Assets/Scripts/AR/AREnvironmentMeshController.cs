using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GooseBrawl
{
    /// <summary>
    /// Turns scanned geometry into invisible physics colliders on the AREnvironment layer.
    /// - ARKit scene reconstruction meshes (LiDAR) get MeshColliders and no renderer.
    /// - Detected planes (floor and walls) also carry MeshColliders as the fallback.
    /// The goose steering only ever queries this layer. Scan geometry is never rendered.
    /// </summary>
    public class AREnvironmentMeshController : MonoBehaviour
    {
        public const string EnvironmentLayerName = "AREnvironment";
        public float planePollInterval = 0.5f;

        public int EnvironmentLayer { get; private set; }
        public LayerMask EnvironmentMask { get; private set; }
        public int MeshCount { get; private set; }
        public int VerticalPlaneCount { get; private set; }
        public int PlaneCount { get; private set; }

        /// <summary>True when there is any collider the goose can steer against.</summary>
        public bool HasEnvironmentColliders => GooseGameManager.UseMockAR || MeshCount > 0 || VerticalPlaneCount > 0;
        public bool HasMeshData => MeshCount > 0;

        float m_NextPlanePoll;
        ARMeshManager m_MeshManager;
        void Awake()
        {
            EnvironmentLayer = LayerMask.NameToLayer(EnvironmentLayerName);
            if (EnvironmentLayer < 0)
            {
                GooseLog.Warn("Layer '" + EnvironmentLayerName + "' is missing. Run Goose Brawl > Setup Project. Falling back to Default.");
                EnvironmentLayer = 0;
            }
            EnvironmentMask = 1 << EnvironmentLayer;
        }

        void Start()
        {
            var mgr = GooseGameManager.Instance;
            if (mgr != null && mgr.AR != null && mgr.AR.meshManager != null)
            {
                m_MeshManager = mgr.AR.meshManager;
                m_MeshManager.meshesChanged += OnMeshesChanged;
            }
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
            if (Time.time < m_NextPlanePoll) return;
            m_NextPlanePoll = Time.time + planePollInterval;
            var mgr = GooseGameManager.Instance;
            if (mgr == null || mgr.AR == null || mgr.AR.IsMock) return;
            var pm = mgr.AR.planeManager;
            if (pm == null) return;

            int walls = 0, count = 0;
            foreach (var plane in pm.trackables)
            {
                count++;
                var go = plane.gameObject;
                if (go.layer != EnvironmentLayer) go.layer = EnvironmentLayer;
                if (plane.alignment == PlaneAlignment.Vertical && plane.trackingState == TrackingState.Tracking) walls++;

                // The prefab's ARPlaneMeshVisualizer maintains the collider as boundaries change.
                // forceRenderingOff also protects old/custom prefabs from AR Foundation re-enabling their renderer.
                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null) renderer.forceRenderingOff = true;
                var line = go.GetComponent<LineRenderer>();
                if (line != null) line.forceRenderingOff = true;
            }
            PlaneCount = count;
            VerticalPlaneCount = walls;
        }

        public void SetPlaneVisualization(bool visible)
        {
            // Scan progress is shown by the UI; geometry stays available only to physics.
            m_NextPlanePoll = 0f;
        }
    }
}
