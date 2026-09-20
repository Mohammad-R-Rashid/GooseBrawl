using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARKit;
using UnityEngine.XR.Management;
using UnityEditor.XR.Management;

namespace GooseBrawl.Editor
{
    /// <summary>Goose Brawl > Validate Project: prints a clear pass/fail report to the Console.</summary>
    public static class GooseBrawlValidator
    {
        public static bool Validate()
        {
            var sb = new StringBuilder();
            int failures = 0, warnings = 0;
            sb.AppendLine("=============== GOOSED. VALIDATION ===============");

            void Check(bool ok, string label, string hint = null, bool warnOnly = false)
            {
                string mark = ok ? "PASS" : (warnOnly ? "WARN" : "FAIL");
                sb.Append("[").Append(mark).Append("] ").Append(label);
                if (!ok && !string.IsNullOrEmpty(hint)) sb.Append("  ->  ").Append(hint);
                sb.AppendLine();
                if (!ok) { if (warnOnly) warnings++; else failures++; }
            }

            // Packages
            Check(PackagePresent("com.unity.xr.arfoundation"), "AR Foundation installed", "Add com.unity.xr.arfoundation in Package Manager");
            var arkit = UnityEditor.XR.ARKit.ARKitSettings.currentSettings;
            Check(arkit != null && arkit.faceTracking, "ARKit front-camera support included", "Enable Face Tracking in ARKit settings");
            Check(PackagePresent("com.unity.xr.arkit"), "ARKit XR Plugin installed", "Add com.unity.xr.arkit in Package Manager");
            Check(PackagePresent("com.unity.xr.management"), "XR Plugin Management installed");
            Check(PackagePresent("com.unity.render-pipelines.universal"), "URP installed");

            // Goose asset
            string model = GoosePrefabBuilder.FindGooseModel(GooseBrawlSetup.GooseFolder);
            Check(model != null, "Goose model found under " + GooseBrawlSetup.GooseFolder + (model != null ? " (" + Path.GetFileName(model) + ")" : ""), "Copy the goose FBX into Assets/Art/Goose (placeholder goose is used otherwise)", warnOnly: true);
            if (model != null)
            {
                var clips = AssetDatabase.LoadAllAssetRepresentationsAtPath(model).OfType<AnimationClip>().Count(c => !c.name.StartsWith("__preview__"));
                Check(clips > 0, "Goose model has animation clips (" + clips + ")", "Procedural animation will be used", warnOnly: true);
                var importer = AssetImporter.GetAtPath(model) as ModelImporter;
                Check(importer != null && importer.animationType == ModelImporterAnimationType.Generic, "Goose rig imported as Generic", "Run Setup Project", warnOnly: true);
            }

            // Prefabs & scene
            var goosePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GooseBrawlSetup.GoosePrefabPath);
            Check(goosePrefab != null, "Goose prefab exists (" + GooseBrawlSetup.GoosePrefabPath + ")", "Run Goose Brawl > Setup Project");
            if (goosePrefab != null)
            {
                Check(goosePrefab.GetComponent<GooseChaseController>() != null, "Goose prefab has GooseChaseController");
                var resolver = goosePrefab.GetComponent<GooseAnimationResolver>();
                Check(resolver != null && resolver.clips != null && resolver.clips.Length > 0, "Goose prefab has clips assigned", "Procedural fallback in use", warnOnly: true);
                var vis = goosePrefab.GetComponent<GooseVisualController>();
                Check(vis != null && vis.modelRoot != null, "Goose prefab has a model");
                var smr = goosePrefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
                Check(smr == null || smr.shadowCastingMode == ShadowCastingMode.On, "Goose casts shadows", "Run Setup Project");
            }
            Check(File.Exists(GooseBrawlSetup.ScenePath), "Scene exists (" + GooseBrawlSetup.ScenePath + ")", "Run Goose Brawl > Setup Project");
            Check(EditorBuildSettings.scenes.Any(s => s.enabled && s.path == GooseBrawlSetup.ScenePath), "Scene is in Build Settings", "Run Setup Project");

            // Scene contents
            if (File.Exists(GooseBrawlSetup.ScenePath))
            {
                var active = EditorSceneManager.GetActiveScene();
                bool sceneOpen = active.path == GooseBrawlSetup.ScenePath;
                if (!sceneOpen && !EditorApplication.isPlaying && active.isDirty == false)
                {
                    EditorSceneManager.OpenScene(GooseBrawlSetup.ScenePath);
                    sceneOpen = true;
                }
                if (sceneOpen)
                {
                    Check(Object.FindAnyObjectByType<ARSession>(FindObjectsInactive.Include) != null, "ARSession in scene");
                    Check(Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>(FindObjectsInactive.Include) != null, "XROrigin in scene");
                    Check(Object.FindAnyObjectByType<ARPlaneManager>(FindObjectsInactive.Include) != null, "ARPlaneManager in scene");
                    Check(Object.FindAnyObjectByType<ARRaycastManager>(FindObjectsInactive.Include) != null, "ARRaycastManager in scene");
                    Check(Object.FindAnyObjectByType<ARCameraManager>(FindObjectsInactive.Include) != null, "ARCameraManager in scene");
                    Check(Object.FindAnyObjectByType<ARCameraBackground>(FindObjectsInactive.Include) != null, "ARCameraBackground in scene");
                    Check(Object.FindAnyObjectByType<AROcclusionManager>(FindObjectsInactive.Include) != null, "AROcclusionManager in scene", warnOnly: true);
                    Check(Object.FindAnyObjectByType<ARMeshManager>(FindObjectsInactive.Include) != null, "ARMeshManager in scene", warnOnly: true);
                    Check(Object.FindAnyObjectByType<AREnvironmentProbeManager>(FindObjectsInactive.Include) != null, "AREnvironmentProbeManager in scene (real reflections)", "Run Setup Project", warnOnly: true);
                    var mgr = Object.FindAnyObjectByType<GooseGameManager>(FindObjectsInactive.Include);
                    Check(mgr != null, "GooseGameManager in scene");
                    Check(mgr != null && mgr.goosePrefab != null, "GooseGameManager has goose prefab reference");
                    Check(Object.FindAnyObjectByType<GameUIController>(FindObjectsInactive.Include) != null, "GameUIController in scene");
                    Check(Object.FindAnyObjectByType<AudioManager>(FindObjectsInactive.Include) != null, "AudioManager in scene");
                    Check(Object.FindAnyObjectByType<HapticsService>(FindObjectsInactive.Include) != null, "HapticsService in scene");
                    Check(Object.FindAnyObjectByType<CinematicLookController>(FindObjectsInactive.Include) != null, "CinematicLookController in scene", "Run Setup Project");
                    Check(Object.FindAnyObjectByType<ARLightRig>(FindObjectsInactive.Include) != null, "ARLightRig in scene", "Run Setup Project");
                    var lib = Object.FindAnyObjectByType<MaterialLibrary>(FindObjectsInactive.Include);
                    Check(lib != null && lib.gooseMaterial != null && lib.shadowMaterial != null, "MaterialLibrary references materials", "Run Setup Project", warnOnly: true);
                    Check(lib != null && lib.shadowCatcherMaterial != null && lib.shadowCatcherMaterial.shader != null && lib.shadowCatcherMaterial.shader.name == "GooseBrawl/ShadowCatcher", "Shadow catcher material uses GooseBrawl/ShadowCatcher", "Run Setup Project");
                    var planeMgr = Object.FindAnyObjectByType<ARPlaneManager>(FindObjectsInactive.Include);
                    Check(planeMgr == null || planeMgr.planePrefab != null, "ARPlaneManager has plane prefab", warnOnly: true);
                    Check(planeMgr == null || planeMgr.planePrefab == null || planeMgr.planePrefab.GetComponentInChildren<Renderer>() == null,
                        "Scan plane prefab is invisible (colliders retained)");
                    var meshMgr = Object.FindAnyObjectByType<ARMeshManager>(FindObjectsInactive.Include);
                    Check(meshMgr == null || meshMgr.meshPrefab != null, "ARMeshManager has mesh prefab", warnOnly: true);
                    var cam = Camera.main;
                    var camData = cam != null ? cam.GetUniversalAdditionalCameraData() : null;
                    Check(camData != null && camData.renderPostProcessing, "Post-processing enabled on the AR camera", "Run Setup Project");
                    var volume = Object.FindAnyObjectByType<Volume>(FindObjectsInactive.Include);
                    Check(volume != null && volume.sharedProfile != null && AssetDatabase.Contains(volume.sharedProfile), "Scene Volume references the post-FX profile asset", "Run Setup Project");
                }
                else
                {
                    Check(false, "Scene checks skipped (another scene is open with unsaved changes)", null, warnOnly: true);
                }
            }

            // Post-FX profile: every override must be active or URP strips its shader variants from the build.
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(GooseBrawlSetup.PostFXProfilePath);
            Check(profile != null, "Post-FX profile asset exists (" + GooseBrawlSetup.PostFXProfilePath + ")", "Run Setup Project");
            if (profile != null)
            {
                Check(profile.TryGet<Bloom>(out var bloom) && bloom.IsActive(), "Bloom active in profile (keeps variant)");
                Check(profile.TryGet<LensDistortion>(out var lens) && lens.IsActive(), "LensDistortion active in profile (keeps variant)");
                Check(profile.TryGet<ChromaticAberration>(out var ca) && ca.IsActive(), "ChromaticAberration active in profile (keeps variant)");
                Check(profile.TryGet<Vignette>(out var vig) && vig.IsActive(), "Vignette active in profile");
                Check(!profile.TryGet<Tonemapping>(out _), "No tonemapping (camera feed is already display-referred)");
            }
            Check(!File.Exists("Assets/Settings/SampleSceneProfile.asset"), "URP template profile removed", "Run Setup Project", warnOnly: true);

            // Pipeline assets
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/")) continue;
                var rp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (rp == null) continue;
                string n = Path.GetFileNameWithoutExtension(path);
                Check(rp.msaaSampleCount == 2, n + ": MSAA 2x (phone budget)", "Run Setup Project");
                Check(Mathf.Approximately(rp.renderScale, 1f), n + ": render scale 1.0", "Run Setup Project");
                Check(rp.supportsMainLightShadows && rp.supportsSoftShadows, n + ": soft main light shadows", "Run Setup Project");
                Check(rp.supportsCameraDepthTexture, n + ": depth texture", "Run Setup Project");
            }
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/")) continue;
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (data == null) continue;
                Check(data.shadowTransparentReceive, Path.GetFileNameWithoutExtension(path) + ": transparent objects receive shadows (shadow catcher)", "Run Setup Project");
            }
            Check(Shader.Find("GooseBrawl/ShadowCatcher") != null, "ShadowCatcher shader compiles", "See Assets/Shaders/ShadowCatcher.shader");
            Check(Shader.Find("GooseBrawl/ContactShadow") != null, "ContactShadow shader compiles", "See Assets/Shaders/ContactShadow.shader");

            // Layers
            Check(LayerMask.NameToLayer(AREnvironmentMeshController.EnvironmentLayerName) >= 0, "Layer 'AREnvironment' exists", "Run Setup Project");
            Check(LayerMask.NameToLayer("Interactable") >= 0, "Layer 'Interactable' exists", "Run Setup Project", warnOnly: true);

            // iOS settings
            Check(PlayerSettings.productName == GooseBrawlSetup.ProductName, "Product name is " + GooseBrawlSetup.ProductName, "Run Setup Project");
            Check(!string.IsNullOrEmpty(PlayerSettings.iOS.cameraUsageDescription), "Camera usage description set", "Run Setup Project");
            Check(EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS, "Active build target is iOS", "Goose Brawl > Switch Build Target To iOS", warnOnly: true);
            Check(PlayerSettings.defaultInterfaceOrientation == UIOrientation.Portrait, "Portrait orientation", warnOnly: true);
            Check(PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.iOS) == ScriptingImplementation.IL2CPP, "IL2CPP scripting backend (iOS)", warnOnly: true);
            Check(File.Exists(GooseBrawlSetup.AppIconPath), "App icon generated", "Run Setup Project", warnOnly: true);

            // XR management
            bool arkitLoader = false;
            if (EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget perTarget) && perTarget != null)
            {
                var manager = perTarget.ManagerSettingsForBuildTarget(BuildTargetGroup.iOS);
                arkitLoader = manager != null && manager.activeLoaders.Any(l => l is ARKitLoader);
            }
            Check(arkitLoader, "ARKit loader enabled for iOS in XR Plug-in Management", "Run Setup Project");

            // URP AR background feature
            bool feature = AssetDatabase.FindAssets("t:UniversalRendererData")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.StartsWith("Assets/"))
                .Select(p => AssetDatabase.LoadAssetAtPath<UniversalRendererData>(p))
                .Where(d => d != null)
                .All(d => d.rendererFeatures.Any(f => f is ARBackgroundRendererFeature));
            Check(feature, "ARBackgroundRendererFeature on every URP renderer", "Run Setup Project");

            Check(GooseBrawlSetup.TMPResourcesPresent, "TextMeshPro essential resources imported", "Window > TextMeshPro > Import TMP Essential Resources (legacy Text fallback otherwise)", warnOnly: true);
            Check(File.Exists("Assets/Plugins/iOS/GooseHaptics.mm"), "Native haptics plugin present", "Handheld.Vibrate fallback in use", warnOnly: true);
            var pluginImporter = AssetImporter.GetAtPath("Assets/Plugins/iOS/GooseHaptics.mm") as PluginImporter;
            Check(pluginImporter != null && pluginImporter.GetCompatibleWithPlatform(BuildTarget.iOS), "Haptics plugin enabled for iOS", "Select the .mm file and enable iOS in its import settings", warnOnly: true);
            int honkClips = AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/Resources/GooseAudio/honks" }).Length;
            Check(honkClips > 0, "Real goose honk recordings present (" + honkClips + ")", "Procedural honks will be used", warnOnly: true);
            string audioSrc = File.Exists("Assets/Scripts/Audio/ProceduralAudio.cs") ? File.ReadAllText("Assets/Scripts/Audio/ProceduralAudio.cs") : "";
            Check(!audioSrc.Contains("ChaseLoop"), "No music loop in the audio code (design rule: diegetic only)", "Remove ChaseLoop");

            // Voice, brain, yell, Sentry
            Check(!string.IsNullOrEmpty(PlayerSettings.iOS.microphoneUsageDescription), "Microphone usage description set (yell at the goose)", "Run Setup Project / settings");
            var ps = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            var forceSpk = ps.FindProperty("Force IOS Speakers When Recording");
            Check(forceSpk != null && forceSpk.boolValue, "Force iOS speakers when recording (goose stays loud while the mic listens)", "Run Setup Project / settings", warnOnly: true);
            int voiceClips = AssetDatabase.IsValidFolder("Assets/Resources/GooseVoice") ? AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/Resources/GooseVoice" }).Length : 0;
            int voiceLines = 0; foreach (var kv in GooseLines.Lines) voiceLines += kv.Value.Length;
            Check(voiceClips >= voiceLines * 2, "Offline voice bank complete for both voices (" + voiceClips + "/" + voiceLines * 2 + " clips)", "cd backend/goose-brain && npm run pregen", warnOnly: true);
            var urlAsset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Resources/GooseBrainUrl.txt");
            Check(urlAsset != null && !urlAsset.text.Contains("YOUR-ACCOUNT") && urlAsset.text.Trim().StartsWith("https://"), "Goose brain Worker URL set (Assets/Resources/GooseBrainUrl.txt)", "wrangler deploy, then paste the workers.dev URL", warnOnly: true);
            Check(PackagePresent("io.sentry.unity"), "Sentry Unity SDK installed", "Packages/manifest.json: io.sentry.unity", warnOnly: true);
            var sentryOptions = AssetDatabase.LoadAssetAtPath<ScriptableObject>(GooseBrawlSetup.SentryOptionsPath);
            bool sentryEnabled = false;
            if (sentryOptions != null) { var so = new SerializedObject(sentryOptions); var en = so.FindProperty("<Enabled>k__BackingField"); var dsn = so.FindProperty("<Dsn>k__BackingField"); sentryEnabled = en != null && en.boolValue && dsn != null && !string.IsNullOrEmpty(dsn.stringValue); }
            Check(sentryEnabled, "Sentry enabled with a DSN (tracing, logs, perf harness)", "Put the DSN in ./sentry.dsn and run Goose Brawl > Configure Sentry", warnOnly: true);
            Check(PlayerSettings.enableFrameTimingStats, "Frame timing stats enabled (PerfProbe GPU/CPU timings in release builds)", "Run Setup Project / settings", warnOnly: true);

            sb.AppendLine("------------------------------------------------------");
            sb.Append(failures == 0 ? "RESULT: READY" : "RESULT: " + failures + " problem(s)").Append("  (").Append(warnings).AppendLine(" warning(s))");
            sb.AppendLine("======================================================");
            if (failures > 0) Debug.LogError(sb.ToString());
            else if (warnings > 0) Debug.LogWarning(sb.ToString());
            else Debug.Log(sb.ToString());
            return failures == 0;
        }

        static bool PackagePresent(string id)
        {
            var manifest = File.ReadAllText("Packages/manifest.json");
            return manifest.Contains("\"" + id + "\"");
        }
    }
}
