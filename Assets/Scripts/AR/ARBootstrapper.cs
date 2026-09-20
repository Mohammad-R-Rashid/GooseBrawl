using System;
using System.Collections;
using Unity.Collections;
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
        [Tooltip("ARKit scene reconstruction (LiDAR): the only way furniture (a chair, a table) exists for the goose, so it never spawns or walks inside it. Kept lean: low density, no normals, a short generation queue; the mesh prefab carries the MeshCollider so AR Foundation bakes the collision data off the main thread. The quality ladder freezes it (chunks stay, updates stop) when frames run long.")]
        public bool enableEnvironmentMeshing = true;
        [Tooltip("Mesh density 0-1 (0.3: coarse chunks are enough for a chair-sized obstacle and cheap to bake and query).")]
        [Range(0.1f, 1f)] public float meshDensity = 0.3f;
        [Tooltip("Mesh chunks generated concurrently. Small keeps the per-frame work flat: updates for a chunk that is already queued coalesce instead of piling up.")]
        [Range(1, 8)] public int meshQueueSize = 2;
        [Tooltip("Environment depth occlusion so the goose can hide behind real furniture.")]
        public bool enableOcclusion = true;
        [Tooltip("ARKit environment texturing (reflection cubemaps). Off by default: the egg and the nest are matte now and each probe update uploads and convolves a cubemap.")]
        public bool enableEnvironmentProbes = false;

        [Header("Selfie (photo mode)")]
        [Tooltip("PHOTO WITH THE GOOSE uses the front camera: the session switches to ARKit's face-tracking configuration while the world-only managers (planes, meshing, occlusion, probes) are off, and back afterwards.")]
        public bool selfiePhotoMode = true;
        [Tooltip("In the selfie, ask ARKit for people occlusion (person segmentation) so the goose peeks out from behind your shoulder instead of floating in front of it. Dropped silently where unsupported.")]
        public bool selfieHumanOcclusion = true;

        public bool SelfieActive { get; private set; }
        /// <summary>The selfie camera feed is actually live (the switch takes a few frames).</summary>
        public bool SelfieLive => SelfieActive && PhotoCameraReady;
        public bool PhotoCameraReady => IsMock || (cameraManager != null && m_CameraFrames >= 2 &&
            cameraManager.currentFacingDirection == (SelfieActive ? CameraFacingDirection.User : CameraFacingDirection.World));
        int m_CameraFrames;

        GooseConfigurationChooser m_Chooser;
        bool m_SelfieRestorePlanes, m_SelfieRestoreMesh, m_SelfieRestoreOcclusion, m_SelfieRestoreProbes;

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
                if (on) { ConfigureMeshManager(); meshManager.enabled = true; }
                else meshManager.enabled = false; // freezes: existing chunks and their colliders stay, updates stop
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
                cameraManager.frameReceived -= OnCameraFrame;
                cameraManager.frameReceived += OnCameraFrame;
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
                try { ConfigureMeshManager(); meshManager.enabled = true; }
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

        /// <summary>
        /// Switch between the world camera and the selfie camera. AR Foundation's default chooser picks the ARKit
        /// configuration that satisfies the most requested features, so with planes, meshing and occlusion requested the
        /// single user-facing request loses and the rear camera stays. Our chooser puts the camera facing first, and the
        /// world-only managers are switched off around the selfie (their state is restored afterwards; mesh chunks stay).
        /// Returns false when there is no AR session to switch (mock mode reports true and does nothing).
        /// </summary>
        public bool SetSelfieCamera(bool on)
        {
            if (IsMock) { SelfieActive = on; return true; }
            if (cameraManager == null || session == null || !ScanningStarted) return false;
            if (on == SelfieActive) return true;
            try
            {
                m_CameraFrames = 0;
                if (m_Chooser == null) m_Chooser = new GooseConfigurationChooser();
                var sub = session.subsystem;
                Feature selfieFeatures = Feature.None;
                if (on)
                {
                    if (sub == null) return false;
                    using (var descriptors = sub.GetConfigurationDescriptors(Allocator.Temp))
                        foreach (var descriptor in descriptors)
                            if ((descriptor.capabilities & Feature.UserFacingCamera) != 0) selfieFeatures |= descriptor.capabilities;
                    if ((selfieFeatures & Feature.UserFacingCamera) == 0) return false;
                }
                if (sub != null && sub.configurationChooser != m_Chooser) sub.configurationChooser = m_Chooser;
                m_Chooser.PreferUserFacing = on;
                if (on)
                {
                    m_SelfieRestorePlanes = planeManager != null && planeManager.enabled;
                    m_SelfieRestoreMesh = meshManager != null && meshManager.enabled;
                    m_SelfieRestoreOcclusion = occlusionManager != null && occlusionManager.enabled;
                    m_SelfieRestoreProbes = environmentProbeManager != null && environmentProbeManager.enabled;
                    if (planeManager != null) planeManager.enabled = false;
                    if (meshManager != null) meshManager.enabled = false; // chunks and their colliders stay
                    if (environmentProbeManager != null) environmentProbeManager.enabled = false;
                    if (occlusionManager != null)
                    {
                        occlusionManager.enabled = false;
                        occlusionManager.requestedEnvironmentDepthMode = EnvironmentDepthMode.Disabled;
                        // Front-camera configurations commonly lack person segmentation. Never request a
                        // world-camera feature that could interfere with switching or leave stale depth behind.
                        occlusionManager.requestedHumanStencilMode = HumanSegmentationStencilMode.Disabled;
                        occlusionManager.requestedHumanDepthMode = HumanSegmentationDepthMode.Disabled;
                        if (selfieHumanOcclusion && (selfieFeatures & Feature.PeopleOcclusionStencil) != 0)
                        {
                            try
                            {
                                occlusionManager.requestedHumanStencilMode = HumanSegmentationStencilMode.Fastest;
                                occlusionManager.requestedHumanDepthMode = (selfieFeatures & Feature.PeopleOcclusionDepth) != 0
                                    ? HumanSegmentationDepthMode.Fastest : HumanSegmentationDepthMode.Disabled;
                                occlusionManager.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.PreferHumanOcclusion;
                                occlusionManager.enabled = true;
                            }
                            catch (Exception e) { GooseLog.Warn("People occlusion for the selfie unavailable: " + e.Message); }
                        }
                    }
                    cameraManager.requestedFacingDirection = CameraFacingDirection.User;
                }
                else
                {
                    cameraManager.requestedFacingDirection = CameraFacingDirection.World;
                    if (occlusionManager != null)
                    {
                        occlusionManager.enabled = false;
                        try
                        {
                            occlusionManager.requestedHumanStencilMode = HumanSegmentationStencilMode.Disabled;
                            occlusionManager.requestedHumanDepthMode = HumanSegmentationDepthMode.Disabled;
                            occlusionManager.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.PreferEnvironmentOcclusion;
                        }
                        catch (Exception e) { GooseLog.Warn("Occlusion restore: " + e.Message); }
                        if (m_SelfieRestoreOcclusion)
                        {
                            occlusionManager.requestedEnvironmentDepthMode = EnvironmentDepthMode.Fastest;
                            occlusionManager.enabled = true;
                        }
                    }
                    if (planeManager != null && m_SelfieRestorePlanes) planeManager.enabled = true;
                    if (meshManager != null && m_SelfieRestoreMesh) { ConfigureMeshManager(); meshManager.enabled = true; }
                    if (environmentProbeManager != null && m_SelfieRestoreProbes) environmentProbeManager.enabled = true;
                    // The video format is per camera: pick the wide one for the world camera again.
                    var selector = GetComponent<ARCameraSelector>();
                    if (selector == null) selector = FindAnyObjectByType<ARCameraSelector>();
                    if (selector != null) StartCoroutine(selector.SelectBestConfiguration(cameraManager));
                }
                SelfieActive = on;
                GooseLog.Info(on ? "Selfie camera requested." : "World camera requested.");
                return true;
            }
            catch (Exception e)
            {
                GooseLog.Warn("Camera facing switch failed: " + e.Message);
                return false;
            }
        }

        void OnCameraFrame(ARCameraFrameEventArgs args)
        {
            var wanted = SelfieActive ? CameraFacingDirection.User : CameraFacingDirection.World;
            m_CameraFrames = cameraManager.currentFacingDirection == wanted ? Mathf.Min(2, m_CameraFrames + 1) : 0;
        }

        void OnDestroy()
        {
            if (cameraManager != null) cameraManager.frameReceived -= OnCameraFrame;
        }

        void ConfigureMeshManager()
        {
            meshManager.density = meshDensity;
            meshManager.normals = false;
            meshManager.tangents = false;
            meshManager.textureCoordinates = false;
            meshManager.colors = false;
            meshManager.concurrentQueueSize = Mathf.Max(1, meshQueueSize);
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

    /// <summary>
    /// AR Foundation's default scoring (most requested features, then rank), except that while a selfie is wanted only
    /// configurations that offer the user-facing camera are considered: the one camera request must beat the pile of
    /// world-tracking requests, or the phone never leaves the rear camera.
    /// </summary>
    public class GooseConfigurationChooser : ConfigurationChooser
    {
        public bool PreferUserFacing;
        readonly DefaultConfigurationChooser m_Default = new DefaultConfigurationChooser();

        public override Configuration ChooseConfiguration(NativeSlice<ConfigurationDescriptor> descriptors, Feature requestedFeatures)
        {
            if (!PreferUserFacing || descriptors.Length == 0) return m_Default.ChooseConfiguration(descriptors, requestedFeatures);
            int best = -1, bestCount = -1, bestRank = int.MinValue;
            for (int i = 0; i < descriptors.Length; i++)
            {
                var d = descriptors[i];
                if ((d.capabilities & Feature.UserFacingCamera) == 0) continue;
                int count = requestedFeatures.Intersection(d.capabilities).Count();
                if (count > bestCount || (count == bestCount && d.rank > bestRank)) { best = i; bestCount = count; bestRank = d.rank; }
            }
            if (best < 0) return m_Default.ChooseConfiguration(descriptors, requestedFeatures);
            var chosen = descriptors[best];
            return new Configuration(chosen, requestedFeatures.Intersection(chosen.capabilities));
        }
    }
}
