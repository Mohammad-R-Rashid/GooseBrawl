using System;
using System.Collections;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GooseBrawl
{
    /// <summary>
    /// Thin wrapper around the AR Foundation managers. Starts the session, toggles features when the
    /// scan begins and reports tracking quality. In mock mode it simply disables the AR objects.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class ARBootstrapper : MonoBehaviour
    {
        [Header("AR references (auto-found when empty)")]
        public ARSession session;
        public XROrigin origin;
        public ARPlaneManager planeManager;
        public ARRaycastManager raycastManager;
        public ARCameraManager cameraManager;
        public AROcclusionManager occlusionManager;
        public ARMeshManager meshManager;
        public AREnvironmentProbeManager environmentProbeManager;

        [Header("Features")]
        public bool enableVerticalPlanes = true;
        [Tooltip("ARKit scene reconstruction (LiDAR). Off by default: every chunk update cooked a MeshCollider on the main thread (frame hitches) and the goose steers just as well against the detected wall planes. The benchmark config 'mesh_on' measures it.")]
        public bool enableEnvironmentMeshing = false;
        [Tooltip("Mesh density 0-1 (0.35: fewer collider rebuilds per second, same walls for the goose).")]
        [Range(0.1f, 1f)] public float meshDensity = 0.35f;
        [Tooltip("Environment depth occlusion so the goose can hide behind real furniture.")]
        public bool enableOcclusion = true;
        [Tooltip("ARKit environment texturing (reflection cubemaps). Off by default: the egg and the nest are matte now and each probe update uploads and convolves a cubemap.")]
        public bool enableEnvironmentProbes = false;

        public bool IsMock { get; private set; }
        public bool Initialized { get; private set; }
        public bool ScanningStarted { get; private set; }

        public bool MeshingActive
        {
            get
            {
                if (IsMock || meshManager == null || !meshManager.enabled) return false;
                var sub = meshManager.subsystem;
                return sub != null && sub.running;
            }
        }

        /// <summary>Runtime switch for the benchmark / quality ladder (scene reconstruction on or off after the scan started).</summary>
        public void SetMeshing(bool on)
        {
            if (IsMock || meshManager == null || !ScanningStarted) return;
            try
            {
                if (on) { meshManager.density = meshDensity; meshManager.enabled = true; }
                else meshManager.enabled = false;
            }
            catch (Exception e) { GooseLog.Warn("Environment meshing toggle failed: " + e.Message); }
        }

        /// <summary>Runtime switch for environment-depth occlusion (benchmark / quality ladder).</summary>
        public void SetOcclusion(bool on)
        {
            if (IsMock || occlusionManager == null || !ScanningStarted) return;
            try
            {
                occlusionManager.requestedEnvironmentDepthMode = on ? EnvironmentDepthMode.Fastest : EnvironmentDepthMode.Disabled;
                occlusionManager.enabled = on;
            }
            catch (Exception e) { GooseLog.Warn("Occlusion toggle failed: " + e.Message); }
        }

        public bool OcclusionActive
        {
            get
            {
                if (IsMock || occlusionManager == null || !occlusionManager.enabled) return false;
                return occlusionManager.currentEnvironmentDepthMode != EnvironmentDepthMode.Disabled;
            }
        }

        void Awake()
        {
            // Decide mock mode before the AR components get a chance to start their subsystems.
            var mgr = FindAnyObjectByType<GooseGameManager>(FindObjectsInactive.Include);
            bool mock = mgr != null && (Application.isEditor ? mgr.useMockARInEditor : mgr.forceMockARInBuild);
            if (!mock) return;
            FindReferences();
            if (session != null) session.gameObject.SetActive(false);
            if (origin != null) origin.gameObject.SetActive(false);
        }

        public void Initialize(bool mock)
        {
            IsMock = mock;
            FindReferences();
            Initialized = true;

            if (mock)
            {
                if (session != null) session.gameObject.SetActive(false);
                if (origin != null) origin.gameObject.SetActive(false);
                GooseLog.Info("Mock AR mode active (USE_MOCK_AR).");
                return;
            }

            if (planeManager != null) planeManager.enabled = false;
            if (meshManager != null) meshManager.enabled = false;
            if (occlusionManager != null) occlusionManager.enabled = false;
            if (environmentProbeManager != null) environmentProbeManager.enabled = false;

            if (cameraManager != null)
            {
                cameraManager.requestedFacingDirection = CameraFacingDirection.World;
                // ARKit world tracking provides ambient lumens + colour temperature (used by ARLightRig).
                cameraManager.requestedLightEstimation = LightEstimation.AmbientIntensity | LightEstimation.AmbientColor;
                cameraManager.autoFocusRequested = true;
            }

            if (session != null)
            {
                session.gameObject.SetActive(true);
                session.attemptUpdate = true;
                session.matchFrameRateRequested = true;
                session.enabled = true;
            }
            if (origin != null) origin.gameObject.SetActive(true);

            StartCoroutine(CheckSupport());
        }

        IEnumerator CheckSupport()
        {
            if (ARSession.state == ARSessionState.None || ARSession.state == ARSessionState.CheckingAvailability)
                yield return ARSession.CheckAvailability();
            if (ARSession.state == ARSessionState.Unsupported)
                GooseLog.Warn("AR is not supported on this device.");
            else
                GooseLog.Info("AR session state: " + ARSession.state);
        }

        public void StartScanning()
        {
            if (IsMock || ScanningStarted) return;
            ScanningStarted = true;

            if (planeManager != null)
            {
                planeManager.requestedDetectionMode = enableVerticalPlanes
                    ? PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical
                    : PlaneDetectionMode.Horizontal;
                planeManager.enabled = true;
            }

            if (enableEnvironmentMeshing && meshManager != null)
            {
                try { meshManager.density = meshDensity; meshManager.enabled = true; }
                catch (Exception e) { GooseLog.Warn("Environment meshing could not be enabled: " + e.Message); }
            }

            if (enableOcclusion && occlusionManager != null)
            {
                try
                {
                    occlusionManager.requestedEnvironmentDepthMode = EnvironmentDepthMode.Fastest;
                    occlusionManager.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.PreferEnvironmentOcclusion;
                    occlusionManager.environmentDepthTemporalSmoothingRequested = true;
                    occlusionManager.enabled = true;
                }
                catch (Exception e) { GooseLog.Warn("Occlusion could not be enabled: " + e.Message); }
            }

            if (enableEnvironmentProbes && environmentProbeManager != null)
            {
                try
                {
                    environmentProbeManager.automaticPlacementRequested = true;
                    environmentProbeManager.environmentTextureHDRRequested = true;
                    environmentProbeManager.enabled = true;
                }
                catch (Exception e) { GooseLog.Warn("Environment probes could not be enabled: " + e.Message); }
            }

            var selector = GetComponent<ARCameraSelector>();
            if (selector == null) selector = FindAnyObjectByType<ARCameraSelector>();
            if (selector == null) selector = gameObject.AddComponent<ARCameraSelector>();
            if (cameraManager != null) StartCoroutine(selector.SelectBestConfiguration(cameraManager));
        }

        public bool IsTrackingGood()
        {
            if (IsMock) return true;
            return ARSession.state == ARSessionState.SessionTracking && ARSession.notTrackingReason == NotTrackingReason.None;
        }

        public bool SessionRunning => IsMock || ARSession.state >= ARSessionState.SessionInitializing;

        public string TrackingHint()
        {
            if (IsMock) return null;
            switch (ARSession.notTrackingReason)
            {
                case NotTrackingReason.InsufficientLight: return "Too dark. Find more light.";
                case NotTrackingReason.ExcessiveMotion: return "Slow down a little.";
                case NotTrackingReason.InsufficientFeatures: return "Point at textured surfaces.";
                case NotTrackingReason.Relocalizing: return "Hold still...";
                case NotTrackingReason.CameraUnavailable: return "Camera unavailable.";
                default: return "Move phone slowly.";
            }
        }

        void FindReferences()
        {
            if (session == null) session = FindAnyObjectByType<ARSession>(FindObjectsInactive.Include);
            if (origin == null) origin = FindAnyObjectByType<XROrigin>(FindObjectsInactive.Include);
            if (planeManager == null) planeManager = FindAnyObjectByType<ARPlaneManager>(FindObjectsInactive.Include);
            if (raycastManager == null) raycastManager = FindAnyObjectByType<ARRaycastManager>(FindObjectsInactive.Include);
            if (cameraManager == null) cameraManager = FindAnyObjectByType<ARCameraManager>(FindObjectsInactive.Include);
            if (occlusionManager == null) occlusionManager = FindAnyObjectByType<AROcclusionManager>(FindObjectsInactive.Include);
            if (meshManager == null) meshManager = FindAnyObjectByType<ARMeshManager>(FindObjectsInactive.Include);
            if (environmentProbeManager == null) environmentProbeManager = FindAnyObjectByType<AREnvironmentProbeManager>(FindObjectsInactive.Include);
        }
    }
}
