using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.ARKit;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARKit;
using UnityEngine.XR.Management;

namespace GooseBrawl.Editor
{
    /// <summary>Goose Brawl > Setup Project: one click (or automatic on first compile) project configuration.</summary>
    public static class GooseBrawlSetup
    {
        public const string ProductName = "GOOSED";      // bundle / Xcode product name (no trailing dot in paths)
        public const string DisplayName = "GOOSED.";     // what the home screen shows (set by GooseBrawlPostBuild)
        public const string ScenePath = "Assets/Scenes/GooseBrawl.unity";
        public const string GooseFolder = "Assets/Art/Goose";
        public const string PrefabFolder = "Assets/Prefabs";
        public const string GoosePrefabPath = PrefabFolder + "/Goose.prefab";
        public const string EnvMeshPrefabPath = PrefabFolder + "/AREnvironmentMesh.prefab";
        public const string PlanePrefabPath = PrefabFolder + "/ARPlaneVisual.prefab";
        public const string PostFXProfilePath = "Assets/Settings/GoosedPostFX.asset";
        public const string AppIconPath = "Assets/Art/Generated/AppIcon.png";
        public const string CameraUsageDescription = "GOOSED. uses the camera to put the goose in your room.";
        public const string MicrophoneUsageDescription = "GOOSED. listens for you yelling at the goose.";
        public const string BundleId = "com.goosebrawl.eggsnatcher"; // unchanged on purpose: keeps saved best times on installed devices

        [MenuItem("Goose Brawl/Setup Project", false, 1)]
        public static void SetupProject()
        {
            RunSetup(force: true);
        }

        [MenuItem("Goose Brawl/Validate Project", false, 2)]
        public static void ValidateProject()
        {
            GooseBrawlValidator.Validate();
        }

        [MenuItem("Goose Brawl/Open GooseBrawl Scene", false, 20)]
        public static void OpenScene()
        {
            if (File.Exists(ScenePath)) EditorSceneManager.OpenScene(ScenePath);
            else Debug.LogWarning("[Goose Brawl] Scene missing. Run Goose Brawl > Setup Project first.");
        }

        [MenuItem("Goose Brawl/Switch Build Target To iOS", false, 21)]
        public static void SwitchToIOS()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS)
            {
                Debug.Log("[Goose Brawl] Build target is already iOS.");
                return;
            }
            Debug.Log("[Goose Brawl] Switching build target to iOS (this re-imports assets, please wait)...");
            EditorUserBuildSettings.SwitchActiveBuildTargetAsync(BuildTargetGroup.iOS, BuildTarget.iOS);
        }

        public const string BuildFolder = "Builds/iOS";

        [MenuItem("Goose Brawl/Build iOS Xcode Project (Release)", false, 22)]
        public static void BuildIOS()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.iOS)
            {
                Debug.LogError("[Goose Brawl] Switch the build target to iOS first (Goose Brawl > Switch Build Target To iOS).");
                return;
            }
            string team = Environment.GetEnvironmentVariable("GOOSEBRAWL_TEAM_ID");
            if (!string.IsNullOrEmpty(team))
            {
                PlayerSettings.iOS.appleDeveloperTeamID = team;
                PlayerSettings.iOS.appleEnableAutomaticSigning = true;
            }
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = BuildFolder,
                target = BuildTarget.iOS,
                options = BuildOptions.None // release: no development console overlay, no profiler hooks, optimized IL2CPP
            };
            Debug.Log("[Goose Brawl] Building iOS Xcode project to " + BuildFolder + " ...");
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log("[Goose Brawl] iOS build result: " + report.summary.result + " (" + report.summary.totalErrors + " errors, " + report.summary.totalWarnings + " warnings) -> " + report.summary.outputPath);
        }

        public static void RunSetup(bool force)
        {
            try
            {
                Debug.Log("[Goose Brawl] ===== Setup Project started =====");
                EnsureFolder(PrefabFolder);
                EnsureFolder("Assets/Scenes");
                EnsureFolder("Assets/XR");
                EnsureFolder("Assets/Settings");

                EnsureLayers();
                ConfigurePlayerSettings();
                ConfigureSentry();
                ConfigureXRManagement();
                ConfigureURPRendererFeature();
                var postProfile = ConfigureRenderPipeline();
                ConfigureAudioImports();
                EnsureTMPResources();

                int envLayer = LayerMask.NameToLayer(AREnvironmentMeshController.EnvironmentLayerName);
                if (envLayer < 0) envLayer = 0;

                // Materials need a MaterialLibrary instance to fill; use a temporary one.
                var tempLibGo = new GameObject("TempMaterialLibrary");
                var tempLib = tempLibGo.AddComponent<MaterialLibrary>();
                GooseBrawlMaterials.Apply(tempLib, GooseFolder);
                GenerateAppIcon();

                GameObject goose = AssetDatabase.LoadAssetAtPath<GameObject>(GoosePrefabPath);
                if (force || goose == null)
                {
                    if (goose != null) AssetDatabase.DeleteAsset(GoosePrefabPath);
                    goose = GoosePrefabBuilder.Build(tempLib, GooseFolder, GoosePrefabPath);
                }

                var envPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(EnvMeshPrefabPath);
                if (force || envPrefab == null) envPrefab = EditorSceneSetup.CreateEnvironmentMeshPrefab(EnvMeshPrefabPath, envLayer);
                var planePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlanePrefabPath);
                if (force || planePrefab == null) planePrefab = EditorSceneSetup.CreatePlanePrefab(PlanePrefabPath, envLayer, tempLib.planeMaterial);

                var mats = CaptureMaterials(tempLib);
                UnityEngine.Object.DestroyImmediate(tempLibGo);

                if (force || !File.Exists(ScenePath))
                {
                    EditorSceneSetup.BuildScene(ScenePath, goose, envPrefab, planePrefab, lib => ApplyMaterials(lib, mats), postProfile);
                }
                AddSceneToBuildSettings();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log("[Goose Brawl] ===== Setup Project finished. Press Play to test in the Editor mock, or build for iOS. =====");
                GooseBrawlValidator.Validate();
                SwitchToIOS();
            }
            catch (Exception e)
            {
                Debug.LogError("[Goose Brawl] Setup failed: " + e);
            }
        }

        // ------------------------------------------------------------------ materials plumbing
        struct MaterialSet
        {
            public Material goose, gooseGrey, placeholder, accent, nest, egg, glow, shadow, shadowCatcher, reticle, plane, dust, mockFloor, mockWall;
            public GameObject nestModel;
        }

        static MaterialSet CaptureMaterials(MaterialLibrary lib)
        {
            return new MaterialSet
            {
                goose = lib.gooseMaterial, gooseGrey = lib.gooseMaterialGrey, placeholder = lib.placeholderGooseMaterial, accent = lib.accentMaterial, nest = lib.nestMaterial,
                egg = lib.eggMaterial, glow = lib.glowMaterial, shadow = lib.shadowMaterial, shadowCatcher = lib.shadowCatcherMaterial, reticle = lib.reticleMaterial, plane = lib.planeMaterial,
                dust = lib.dustMaterial, mockFloor = lib.mockFloorMaterial, mockWall = lib.mockWallMaterial, nestModel = lib.nestModel
            };
        }

        static void ApplyMaterials(MaterialLibrary lib, MaterialSet m)
        {
            lib.gooseMaterial = m.goose;
            lib.gooseMaterialGrey = m.gooseGrey;
            lib.placeholderGooseMaterial = m.placeholder;
            lib.accentMaterial = m.accent;
            lib.nestMaterial = m.nest;
            lib.eggMaterial = m.egg;
            lib.glowMaterial = m.glow;
            lib.shadowMaterial = m.shadow;
            lib.shadowCatcherMaterial = m.shadowCatcher;
            lib.reticleMaterial = m.reticle;
            lib.planeMaterial = m.plane;
            lib.dustMaterial = m.dust;
            lib.mockFloorMaterial = m.mockFloor;
            lib.mockWallMaterial = m.mockWall;
            lib.nestModel = m.nestModel;
        }

        // ------------------------------------------------------------------ steps
        public static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        public static void EnsureLayers()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0) return;
            var tagManager = new SerializedObject(assets[0]);
            var layers = tagManager.FindProperty("layers");
            AddLayer(layers, AREnvironmentMeshController.EnvironmentLayerName);
            AddLayer(layers, "Interactable");
            tagManager.ApplyModifiedProperties();
        }

        static void AddLayer(SerializedProperty layers, string name)
        {
            for (int i = 0; i < layers.arraySize; i++)
                if (layers.GetArrayElementAtIndex(i).stringValue == name) return;
            for (int i = 8; i < layers.arraySize; i++)
            {
                var p = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(p.stringValue))
                {
                    p.stringValue = name;
                    Debug.Log("[Goose Brawl] Added layer '" + name + "' at index " + i);
                    return;
                }
            }
            Debug.LogWarning("[Goose Brawl] No free layer slot for '" + name + "'.");
        }

        public static void ConfigurePlayerSettings()
        {
            PlayerSettings.productName = ProductName;
            PlayerSettings.companyName = "Goose Brawl";
            PlayerSettings.runInBackground = true; // keeps Editor play mode (and the smoke test) running when unfocused
            var ios = NamedBuildTarget.iOS;
            PlayerSettings.iOS.cameraUsageDescription = CameraUsageDescription;
            PlayerSettings.iOS.microphoneUsageDescription = MicrophoneUsageDescription;
            PlayerSettings.enableFrameTimingStats = true; // PerfProbe reads FrameTimingManager in release builds too
            PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly; // the Editor / dev build may talk to wrangler dev on http://localhost
            ConfigureIOSRecordingFlags();
            PlayerSettings.iOS.targetOSVersionString = "16.0";
            PlayerSettings.iOS.targetDevice = iOSTargetDevice.iPhoneAndiPad;
            PlayerSettings.iOS.requiresPersistentWiFi = false;
            PlayerSettings.SetScriptingBackend(ios, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetArchitecture(ios, 1); // ARM64
            var id = PlayerSettings.GetApplicationIdentifier(ios);
            if (string.IsNullOrEmpty(id) || id.Contains("DefaultCompany") || id.Contains("unity.template") || id.Contains("Unity-Technologies"))
                PlayerSettings.SetApplicationIdentifier(ios, BundleId);
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
            PlayerSettings.SetApiCompatibilityLevel(ios, ApiCompatibilityLevel.NET_Standard);
            Debug.Log("[Goose Brawl] Player settings configured (" + ProductName + ", portrait, camera usage description, IL2CPP/ARM64, iOS 16+).");
        }

        /// <summary>
        /// Not public API: 'Force IOS Speakers When Recording' keeps the goose on the loudspeaker while the yell detector
        /// records; 'Prepare IOS For Recording' stays off so the title screen is not routed through the play-and-record session.
        /// </summary>
        public static void ConfigureIOSRecordingFlags()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (assets == null || assets.Length == 0) return;
            var so = new SerializedObject(assets[0]);
            var force = so.FindProperty("Force IOS Speakers When Recording");
            var prepare = so.FindProperty("Prepare IOS For Recording");
            if (force != null) force.boolValue = true;
            if (prepare != null) prepare.boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("[Goose Brawl] iOS recording flags: force speakers=" + (force != null) + ", prepare=off");
        }

        /// <summary>GOOSE_SENTRY is defined only while the Sentry package is actually resolved, so the project always compiles.</summary>
        public static void EnsureSentryDefine()
        {
            bool present = false;
            try { foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()) if (p.name == "io.sentry.unity") { present = true; break; } }
            catch (Exception e) { Debug.LogWarning("[Goose Brawl] Package query failed: " + e.Message); return; }
            foreach (var target in new[] { NamedBuildTarget.iOS, NamedBuildTarget.Standalone, NamedBuildTarget.Android })
            {
                string defines = PlayerSettings.GetScriptingDefineSymbols(target);
                var list = defines.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                bool has = list.Contains("GOOSE_SENTRY");
                if (present == has) continue;
                if (present) list.Add("GOOSE_SENTRY"); else list.Remove("GOOSE_SENTRY");
                PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", list));
                Debug.Log("[Goose Brawl] GOOSE_SENTRY define " + (present ? "added" : "removed") + " for " + target.TargetName);
            }
        }

        public const string SentryOptionsPath = "Assets/Resources/Sentry/SentryOptions.asset";
        public const string SentryConfigPath = "Assets/Resources/Sentry/GooseSentryOptions.asset";
        public const string SentryDsnFile = "sentry.dsn";

        /// <summary>
        /// Writes the Sentry SDK options asset from the DSN in ./sentry.dsn (or GOOSED_SENTRY_DSN). Equivalent to filling in
        /// Tools > Sentry by hand: enabled, capture in Editor, traces 100%, iOS native on, our options-configuration script.
        /// No symbol upload (no SentryCliOptions asset), so the Xcode build never needs network.
        /// </summary>
        [MenuItem("Goose Brawl/Configure Sentry", false, 3)]
        public static void ConfigureSentry()
        {
#if GOOSE_SENTRY
            string dsn = Environment.GetEnvironmentVariable("GOOSED_SENTRY_DSN");
            if (string.IsNullOrEmpty(dsn) && File.Exists(SentryDsnFile)) dsn = File.ReadAllText(SentryDsnFile).Trim();
            dsn = dsn ?? "";
            EnsureFolder("Assets/Resources/Sentry");

            var config = AssetDatabase.LoadAssetAtPath<GooseSentryOptions>(SentryConfigPath);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<GooseSentryOptions>();
                AssetDatabase.CreateAsset(config, SentryConfigPath);
            }
            var options = AssetDatabase.LoadAssetAtPath<Sentry.Unity.ScriptableSentryUnityOptions>(SentryOptionsPath);
            if (options == null)
            {
                options = ScriptableObject.CreateInstance<Sentry.Unity.ScriptableSentryUnityOptions>();
                AssetDatabase.CreateAsset(options, SentryOptionsPath);
            }
            var so = new SerializedObject(options);
            void SetBool(string name, bool v) { var p = so.FindProperty("<" + name + ">k__BackingField"); if (p != null) p.boolValue = v; else Debug.LogWarning("[Goose Brawl] Sentry option missing: " + name); }
            void SetString(string name, string v) { var p = so.FindProperty("<" + name + ">k__BackingField"); if (p != null) p.stringValue = v; else Debug.LogWarning("[Goose Brawl] Sentry option missing: " + name); }
            void SetFloat(string name, float v) { var p = so.FindProperty("<" + name + ">k__BackingField"); if (p != null) { if (p.propertyType == SerializedPropertyType.Float) p.floatValue = v; } else Debug.LogWarning("[Goose Brawl] Sentry option missing: " + name); }
            SetBool("Enabled", !string.IsNullOrEmpty(dsn));
            SetString("Dsn", dsn);
            SetBool("CaptureInEditor", true);
            SetFloat("TracesSampleRate", 1f);
            SetBool("Debug", true);
            SetBool("DebugOnlyInEditor", true);
            SetString("ReleaseOverride", "goosed@1.0.0");
            SetBool("IosNativeSupportEnabled", true);
            SetBool("AutoStartupTraces", true);
            SetBool("AutoSceneLoadTraces", true);
            var cfg = so.FindProperty("<OptionsConfiguration>k__BackingField");
            if (cfg != null) cfg.objectReferenceValue = config;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(options);
            AssetDatabase.SaveAssets();
            Debug.Log("[Goose Brawl] Sentry options written: " + (string.IsNullOrEmpty(dsn) ? "DISABLED (no DSN in " + SentryDsnFile + ")" : "enabled, DSN set") + ".");
#else
            Debug.LogWarning("[Goose Brawl] Sentry package not resolved (GOOSE_SENTRY undefined); nothing to configure.");
#endif
        }

        public static void ConfigureXRManagement()
        {
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
            {
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, perTarget, true);
            }
            if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.iOS))
                perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.iOS);
            var general = perTarget.SettingsForBuildTarget(BuildTargetGroup.iOS);
            if (general != null) general.InitManagerOnStart = true;
            var manager = perTarget.ManagerSettingsForBuildTarget(BuildTargetGroup.iOS);
            if (manager != null && !manager.activeLoaders.Any(l => l is ARKitLoader))
            {
                bool ok = XRPackageMetadataStore.AssignLoader(manager, typeof(ARKitLoader).FullName, BuildTargetGroup.iOS);
                Debug.Log("[Goose Brawl] ARKit loader assigned for iOS: " + ok);
            }
            EditorUtility.SetDirty(perTarget);

            var arkit = ARKitSettings.currentSettings;
            if (arkit == null)
            {
                arkit = ScriptableObject.CreateInstance<ARKitSettings>();
                AssetDatabase.CreateAsset(arkit, "Assets/XR/ARKitSettings.asset");
                ARKitSettings.currentSettings = arkit;
            }
            arkit.requirement = ARKitSettings.Requirement.Required;
            EditorUtility.SetDirty(arkit);
            AssetDatabase.SaveAssets();
        }

        public static void ConfigureURPRendererFeature()
        {
            int added = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/")) continue; // package assets are immutable
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (data == null) continue;
                if (data.rendererFeatures.Any(f => f is ARBackgroundRendererFeature)) continue;

                var feature = ScriptableObject.CreateInstance<ARBackgroundRendererFeature>();
                feature.name = "ARBackgroundRendererFeature";
                AssetDatabase.AddObjectToAsset(feature, data);
                AssetDatabase.SaveAssets();
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

                var so = new SerializedObject(data);
                var list = so.FindProperty("m_RendererFeatures");
                var map = so.FindProperty("m_RendererFeatureMap");
                list.arraySize++;
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = feature;
                map.arraySize++;
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(data);
                added++;
                Debug.Log("[Goose Brawl] Added ARBackgroundRendererFeature to " + path);
            }
            if (added > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// Pipeline quality for a mature AR look on a phone: full render scale, MSAA 2x (alpha-to-coverage on the feather cards),
        /// soft shadows at 2048, depth texture, and the post-processing profile asset. Every URP asset under Assets/ gets it,
        /// so the Editor (PC) and the phone (Mobile) look the same.
        /// </summary>
        public static VolumeProfile ConfigureRenderPipeline()
        {
            var profile = CreatePostFXProfile();
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/")) continue;
                var rp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (rp == null) continue;
                rp.renderScale = 1f;
                rp.msaaSampleCount = 2;
                rp.supportsCameraDepthTexture = true;
                rp.supportsHDR = true;
                rp.mainLightShadowmapResolution = 1024;
                rp.shadowDistance = 8f;
                rp.shadowCascadeCount = 1;
                rp.shadowDepthBias = 1f;
                rp.shadowNormalBias = 0.8f;
                rp.colorGradingMode = ColorGradingMode.LowDynamicRange;
                var so = new SerializedObject(rp);
                var soft = so.FindProperty("m_SoftShadowsSupported");
                if (soft != null) soft.boolValue = true;
                var mainShadows = so.FindProperty("m_MainLightShadowsSupported");
                if (mainShadows != null) mainShadows.boolValue = true;
                var vol = so.FindProperty("m_VolumeProfile");
                if (vol != null) vol.objectReferenceValue = profile;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(rp);
                Debug.Log("[Goose Brawl] Render pipeline configured: " + path);
            }
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/")) continue;
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (data == null) continue;
                // Without this the transparent shadow catcher receives an empty shadow map.
                data.shadowTransparentReceive = true;
                EditorUtility.SetDirty(data);
            }
            // The URP template profile has Tonemapping on; it would grade the camera feed. It is replaced by GoosedPostFX.
            if (File.Exists("Assets/Settings/SampleSceneProfile.asset")) AssetDatabase.DeleteAsset("Assets/Settings/SampleSceneProfile.asset");
            AssetDatabase.SaveAssets();
            return profile;
        }

        /// <summary>
        /// The post-processing profile must be an asset with every override active (non-zero) or URP strips the
        /// Uber shader variants from the iOS build. Runtime code animates the cloned instance, never this asset.
        /// </summary>
        public static VolumeProfile CreatePostFXProfile()
        {
            var p = AssetDatabase.LoadAssetAtPath<VolumeProfile>(PostFXProfilePath);
            if (p == null)
            {
                p = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(p, PostFXProfilePath);
            }
            T Ensure<T>() where T : VolumeComponent
            {
                if (!p.TryGet<T>(out var c))
                {
                    c = p.Add<T>(true);
                    c.name = typeof(T).Name;
                    AssetDatabase.AddObjectToAsset(c, p);
                }
                c.active = true;
                return c;
            }
            var bloom = Ensure<Bloom>();
            bloom.intensity.Override(0.3f);
            bloom.threshold.Override(1.3f);
            bloom.scatter.Override(0.6f);
            bloom.highQualityFiltering.Override(false);
            var vignette = Ensure<Vignette>();
            vignette.intensity.Override(0.2f);
            vignette.smoothness.Override(0.5f);
            vignette.color.Override(new Color(0.02f, 0.015f, 0.02f));
            vignette.rounded.Override(false);
            var color = Ensure<ColorAdjustments>();
            color.contrast.Override(8f);
            color.saturation.Override(6f);
            color.postExposure.Override(0f);
            var ca = Ensure<ChromaticAberration>();
            ca.intensity.Override(0.01f);
            var lens = Ensure<LensDistortion>();
            lens.intensity.Override(-0.01f);
            lens.xMultiplier.Override(1f);
            lens.yMultiplier.Override(1f);
            lens.scale.Override(1f);
            var grain = Ensure<FilmGrain>();
            grain.type.Override(FilmGrainLookup.Thin1);
            grain.intensity.Override(0.06f);
            grain.response.Override(0.8f);
            if (p.TryGet<Tonemapping>(out var tm))
            {
                p.Remove<Tonemapping>();
                UnityEngine.Object.DestroyImmediate(tm, true);
            }
            EditorUtility.SetDirty(p);
            AssetDatabase.SaveAssets();
            return p;
        }

        /// <summary>Honks: PCM (tiny, no decode cost). Flock recording: streamed (was 11 MB of PCM decompressed at startup).</summary>
        public static void ConfigureAudioImports()
        {
            var audioFolders = new System.Collections.Generic.List<string> { "Assets/Resources/GooseAudio" };
            if (AssetDatabase.IsValidFolder("Assets/Resources/GooseVoice")) audioFolders.Add("Assets/Resources/GooseVoice");
            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", audioFolders.ToArray()))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as AudioImporter;
                if (importer == null) continue;
                var settings = importer.defaultSampleSettings;
                bool longClip = path.EndsWith(".mp3") || path.Contains("yellowstone");
                bool voice = path.Contains("/GooseVoice/"); // 168 spoken lines: Vorbis keeps the bank small, decompressed on load
                var wantLoad = longClip ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
                var wantFormat = longClip || voice ? AudioCompressionFormat.Vorbis : AudioCompressionFormat.PCM;
                float wantQuality = longClip ? 0.6f : (voice ? 0.7f : 1f);
                bool changed = settings.loadType != wantLoad || settings.compressionFormat != wantFormat || Mathf.Abs(settings.quality - wantQuality) > 0.01f;
                if (importer.forceToMono != !longClip) { importer.forceToMono = !longClip; changed = true; }
                if (importer.loadInBackground != longClip) { importer.loadInBackground = longClip; changed = true; }
                if (!changed) continue;
                settings.loadType = wantLoad;
                settings.compressionFormat = wantFormat;
                settings.quality = wantQuality;
                importer.defaultSampleSettings = settings;
                importer.SaveAndReimport();
                Debug.Log("[Goose Brawl] Audio import configured: " + path + " -> " + wantLoad + "/" + wantFormat);
            }
        }

        /// <summary>App icon and a matching launch screen, generated in code (yolk background, cream egg with a crack).</summary>
        public static void GenerateAppIcon()
        {
            try
            {
                if (!File.Exists(AppIconPath))
                {
                    EnsureFolder(Path.GetDirectoryName(AppIconPath).Replace('\\', '/'));
                    var tex = ProceduralAssets.AppIconTexture(1024);
                    File.WriteAllBytes(AppIconPath, tex.EncodeToPNG());
                    AssetDatabase.ImportAsset(AppIconPath);
                    var importer = AssetImporter.GetAtPath(AppIconPath) as TextureImporter;
                    if (importer != null)
                    {
                        importer.textureType = TextureImporterType.Default;
                        importer.alphaIsTransparency = false;
                        importer.mipmapEnabled = false;
                        importer.maxTextureSize = 1024;
                        importer.textureCompression = TextureImporterCompression.Uncompressed;
                        importer.isReadable = true;
                        importer.SaveAndReimport();
                    }
                }
                var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(AppIconPath);
                if (icon == null) return;

                // Default icon for every platform.
                PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { icon }, IconKind.Application);
                // iOS: every kind and size gets the same source; Unity scales at build time.
                foreach (var kind in PlayerSettings.GetSupportedIconKinds(NamedBuildTarget.iOS))
                {
                    var icons = PlayerSettings.GetPlatformIcons(NamedBuildTarget.iOS, kind);
                    foreach (var i in icons) i.SetTexture(icon);
                    PlayerSettings.SetPlatformIcons(NamedBuildTarget.iOS, kind, icons);
                }
                // Launch screen: the icon on a warm cream background instead of the default black flash.
                PlayerSettings.iOS.SetiPhoneLaunchScreenType(iOSLaunchScreenType.ImageAndBackgroundRelative);
                PlayerSettings.iOS.SetLaunchScreenImage(icon, iOSLaunchScreenImageType.iPhonePortraitImage);
                var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
                if (assets != null && assets.Length > 0)
                {
                    var so = new SerializedObject(assets[0]);
                    var bg = so.FindProperty("iOSLaunchScreenBackgroundColor");
                    if (bg != null) bg.colorValue = new Color(1f, 0.965f, 0.87f, 1f);
                    var fill = so.FindProperty("iOSLaunchScreenFillPct");
                    if (fill != null)
                    {
                        if (fill.propertyType == SerializedPropertyType.Float) fill.floatValue = 32f;
                        else if (fill.propertyType == SerializedPropertyType.Integer) fill.intValue = 32;
                    }
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                Debug.Log("[Goose Brawl] App icon and launch screen assigned.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Goose Brawl] App icon setup skipped: " + e.Message);
            }
        }

        public static bool TMPResourcesPresent => File.Exists("Assets/TextMesh Pro/Resources/TMP Settings.asset");

        public static void EnsureTMPResources()
        {
            if (TMPResourcesPresent) return;
            try
            {
                TMP_PackageResourceImporter.ImportResources(true, false, false);
                Debug.Log("[Goose Brawl] Imported TextMeshPro essential resources.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Goose Brawl] Could not import TMP resources automatically (UI falls back to legacy Text): " + e.Message);
            }
        }

        public static void AddSceneToBuildSettings()
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath && s.path != "Assets/Scenes/SampleScene.unity").ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }

    /// <summary>Runs the setup automatically the first time the scripts compile so the project is playable without clicking anything.</summary>
    [InitializeOnLoad]
    public static class GooseBrawlAutoSetup
    {
        /// <summary>Bump to force the automatic setup to run again after a code change.</summary>
        public const int SetupVersion = 4;
        const string MarkerPath = "Library/GooseBrawlSetupVersion.txt";

        static GooseBrawlAutoSetup()
        {
            EditorApplication.delayCall += TryRun;
            EditorApplication.delayCall += GooseBrawlSetup.EnsureSentryDefine;
        }

        static void TryRun()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryRun;
                return;
            }
            int done = -1;
            if (File.Exists(MarkerPath)) int.TryParse(File.ReadAllText(MarkerPath).Trim(), out done);
            if (done == SetupVersion && File.Exists(GooseBrawlSetup.ScenePath)) return;

            Debug.Log("[Goose Brawl] Setup version " + SetupVersion + ": running Goose Brawl > Setup Project automatically.");
            File.WriteAllText(MarkerPath, SetupVersion.ToString());
            GooseBrawlSetup.RunSetup(force: true);
        }
    }
}
