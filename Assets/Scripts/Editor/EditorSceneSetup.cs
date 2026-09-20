using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GooseBrawl.Editor
{
    /// <summary>Builds Assets/Scenes/GooseBrawl.unity from scratch: AR Session, XR Origin, managers, game services, UI, light, post-processing volume.</summary>
    public static class EditorSceneSetup
    {
        public static void BuildScene(string scenePath, GameObject goosePrefab, GameObject envMeshPrefab, GameObject planePrefab,
            System.Action<MaterialLibrary> configureMaterials, VolumeProfile postProfile)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- Lighting -------------------------------------------------------------------------
            var lightGo = new GameObject("Key Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.color = new Color(1f, 0.96f, 0.9f);
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.85f;
            light.shadowBias = 0.02f;
            light.shadowNormalBias = 0.4f;
            lightGo.transform.rotation = Quaternion.Euler(62f, -35f, 0f);
            var lightData = light.GetUniversalAdditionalLightData();
            if (lightData != null) lightData.usePipelineSettings = true;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.6f, 0.58f);
            RenderSettings.ambientEquatorColor = new Color(0.46f, 0.45f, 0.45f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.2f, 0.19f);
            RenderSettings.skybox = null;
            RenderSettings.fog = false;

            // ---- Post-processing volume ------------------------------------------------------------
            var volumeGo = new GameObject("PostFX Volume");
            var volume = volumeGo.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 0f;
            volume.weight = 1f;
            volume.sharedProfile = postProfile;

            // ---- AR Session -----------------------------------------------------------------------
            var sessionGo = new GameObject("AR Session");
            var session = sessionGo.AddComponent<ARSession>();
            sessionGo.AddComponent<ARInputManager>();
            session.attemptUpdate = true;
            session.matchFrameRateRequested = true;

            // ---- XR Origin + AR camera ----------------------------------------------------------------
            var originGo = new GameObject("XR Origin");
            var origin = originGo.AddComponent<XROrigin>();
            var offsetGo = new GameObject("Camera Offset");
            offsetGo.transform.SetParent(originGo.transform, false);

            var camGo = new GameObject("Main Camera");
            camGo.transform.SetParent(offsetGo.transform, false);
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 30f;
            cam.allowHDR = true;
            cam.allowMSAA = true;
            camGo.AddComponent<AudioListener>();
            var cameraManager = camGo.AddComponent<ARCameraManager>();
            camGo.AddComponent<ARCameraBackground>();
            var occlusion = camGo.AddComponent<AROcclusionManager>();
            occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Fastest;
            occlusion.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.PreferEnvironmentOcclusion;
            occlusion.environmentDepthTemporalSmoothingRequested = true;
            occlusion.enabled = false;

            var tpd = camGo.AddComponent<TrackedPoseDriver>();
            var positionAction = new InputAction("Position", binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3");
            positionAction.AddBinding("<HandheldARInputDevice>/devicePosition");
            var rotationAction = new InputAction("Rotation", binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion");
            rotationAction.AddBinding("<HandheldARInputDevice>/deviceRotation");
            tpd.positionInput = new InputActionProperty(positionAction);
            tpd.rotationInput = new InputActionProperty(rotationAction);

            var camData = cam.GetUniversalAdditionalCameraData();
            if (camData != null)
            {
                camData.renderPostProcessing = true;
                camData.dithering = true;
                camData.antialiasing = AntialiasingMode.None; // MSAA from the pipeline asset
                camData.renderShadows = true;
                camData.stopNaN = false;
            }

            origin.Camera = cam;
            origin.CameraFloorOffsetObject = offsetGo;
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
            origin.CameraYOffset = 0f;

            var planeManager = originGo.AddComponent<ARPlaneManager>();
            planeManager.planePrefab = planePrefab;
            planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
            planeManager.enabled = false;
            var raycastManager = originGo.AddComponent<ARRaycastManager>();
            var probeManager = originGo.AddComponent<AREnvironmentProbeManager>();
            probeManager.automaticPlacementRequested = true;
            probeManager.environmentTextureHDRRequested = true;
            probeManager.debugPrefab = null;
            probeManager.enabled = false;

            var meshGo = new GameObject("AR Mesh Manager");
            meshGo.transform.SetParent(originGo.transform, false);
            var meshManager = meshGo.AddComponent<ARMeshManager>();
            meshManager.meshPrefab = envMeshPrefab != null ? envMeshPrefab.GetComponent<MeshFilter>() : null;
            meshManager.density = 0.3f;
            meshManager.normals = false;
            meshManager.tangents = false;
            meshManager.textureCoordinates = false;
            meshManager.colors = false;
            meshManager.concurrentQueueSize = 2;
            meshManager.enabled = false;

            // ---- Game services --------------------------------------------------------------------
            var gameGo = new GameObject("GooseBrawl Game");
            var mgr = gameGo.AddComponent<GooseGameManager>();
            mgr.goosePrefab = goosePrefab;
            var ar = gameGo.AddComponent<ARBootstrapper>();
            ar.session = session;
            ar.origin = origin;
            ar.planeManager = planeManager;
            ar.raycastManager = raycastManager;
            ar.cameraManager = cameraManager;
            ar.occlusionManager = occlusion;
            ar.meshManager = meshManager;
            ar.environmentProbeManager = probeManager;
            gameGo.AddComponent<ARCameraSelector>();
            gameGo.AddComponent<ARSurfaceScanner>();
            gameGo.AddComponent<ARPlacementController>();
            gameGo.AddComponent<AREnvironmentMeshController>();
            gameGo.AddComponent<PlayerTracker>();
            gameGo.AddComponent<ScoreManager>();
            gameGo.AddComponent<AudioManager>();
            gameGo.AddComponent<ChaosAudioController>();
            gameGo.AddComponent<HapticsService>();
            gameGo.AddComponent<DangerFeedbackController>();
            gameGo.AddComponent<MockARController>();
            var look = gameGo.AddComponent<CinematicLookController>();
            look.keyLight = light;
            var rig = gameGo.AddComponent<ARLightRig>();
            rig.keyLight = light;
            var depth = gameGo.AddComponent<AREnvDepthPublisher>();
            depth.occlusionManager = occlusion;
            depth.cameraManager = cameraManager;
            var lib = gameGo.AddComponent<MaterialLibrary>();
            configureMaterials?.Invoke(lib);

            var uiGo = new GameObject("Game UI");
            uiGo.AddComponent<GameUIController>();

            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<EventSystem>();
            esGo.AddComponent<InputSystemUIInputModule>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, scenePath);
            Debug.Log("[Goose Brawl] Saved scene: " + scenePath);
        }

        /// <summary>Prefab instantiated by ARMeshManager for every scanned mesh chunk: collider only, no renderer.</summary>
        public static GameObject CreateEnvironmentMeshPrefab(string path, int layer)
        {
            var go = new GameObject("AREnvironmentMesh");
            go.layer = layer;
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshCollider>();
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }

        /// <summary>Detected planes keep their boundary mesh and collider, with no visible scan geometry.</summary>
        public static GameObject CreatePlanePrefab(string path, int layer, Material planeMaterial)
        {
            var go = new GameObject("ARPlane");
            go.layer = layer;
            go.AddComponent<ARPlane>();
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshCollider>();
            go.AddComponent<ARPlaneMeshVisualizer>();
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }
    }
}
