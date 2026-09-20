using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GooseBrawl.Editor
{
    /// <summary>
    /// The code defaults are the source of truth for the gameplay tunables, but the scene and the goose prefab keep
    /// their own serialized copies (a field edited in C# after the scene was built never reaches the build). This
    /// copies the listed fields from a fresh component (the C# defaults) into the scene objects and the goose prefab,
    /// leaving every other serialized value (references, mock flags, prefab links) untouched.
    /// Menu: Goose Brawl > Sync Tunables To Code Defaults. Remote: Library/GooseBrawlCommand.txt <- tune.
    /// </summary>
    public static class GooseBrawlTune
    {
        static readonly (Type type, string[] fields)[] k_SceneTunables =
        {
            (typeof(GooseGameManager), new[]
            {
                "palmRadius", "palmSpring", "palmDamping", "accelerationGain", "accelerationDeadZone", "tiltGain", "tiltDeadZoneRoll", "tiltDeadZonePitch",
                "carryGraceSeconds", "repeatCarryDifficulty", "stillCreepAfter", "outlastSeconds", "dodgeDistance", "breadPerRound"
            }),
            (typeof(ARBootstrapper), new[] { "enableVerticalPlanes", "enableEnvironmentMeshing", "meshDensity", "meshQueueSize", "enableOcclusion", "enableEnvironmentProbes", "selfiePhotoMode", "selfieHumanOcclusion" }),
            (typeof(AudioManager), new[] { "voicePitch", "voiceHighPassHz", "voiceDistortion", "voiceLowPassHz", "voiceWarbleDepth", "voiceWarbleRateHz", "voiceWarbleMix" }),
            (typeof(PerfProbe), new[] { "adaptive", "adaptiveP95Ms", "adaptiveWindowSeconds", "adaptiveMinIntervalSeconds", "maxQualityTier", "spikeThresholdMsDevice" }),
            (typeof(PerfBenchmark), new[] { "secondsPerConfig", "secondsPerConfigEditor", "autoStartEnvVar" }),
        };

        static readonly (Type type, string[] fields)[] k_GoosePrefabTunables =
        {
            (typeof(GooseChaseController), new[]
            {
                "walkSpeed", "runSpeed", "maxChaseSpeed", "timeToMaxSpeed", "walkPhaseDuration", "nearCapSpeed", "tierStartTimes", "dashCooldownByTier",
                "selfieDistance", "selfieSide", "selfieDrop"
            }),
            (typeof(GooseMovement), new[] { "acceleration", "deceleration", "minCameraDistance", "overlapCheckRate", "floorSnapTolerance" }),
            (typeof(GooseObstacleAvoidance), new[] { "bodyRadius", "bodyHeight", "bodyBottom", "maxPushPerPass", "floorNoiseTolerance", "emergencyStopDistance" }),
        };

        [MenuItem("Goose Brawl/Sync Tunables To Code Defaults")]
        public static void Apply()
        {
            GooseBrawlSetup.OpenScene();
            var scene = SceneManager.GetActiveScene();
            int sceneChanged = 0, prefabChanged = 0;

            var temp = new GameObject("TuneDefaults") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                foreach (var (type, fields) in k_SceneTunables)
                {
                    var target = UnityEngine.Object.FindAnyObjectByType(type, FindObjectsInactive.Include) as Component;
                    if (target == null) { Debug.LogWarning("[Goose Brawl] Tune: no " + type.Name + " in the scene."); continue; }
                    sceneChanged += Sync(target, temp, fields);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(temp); }
            if (sceneChanged > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            if (System.IO.File.Exists(GooseBrawlSetup.GoosePrefabPath))
            {
                var root = PrefabUtility.LoadPrefabContents(GooseBrawlSetup.GoosePrefabPath);
                var temp2 = new GameObject("TuneDefaults2") { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    foreach (var (type, fields) in k_GoosePrefabTunables)
                    {
                        var target = root.GetComponentInChildren(type, true);
                        if (target == null) { Debug.LogWarning("[Goose Brawl] Tune: no " + type.Name + " in the goose prefab."); continue; }
                        prefabChanged += Sync(target, temp2, fields);
                    }
                    if (prefabChanged > 0) PrefabUtility.SaveAsPrefabAsset(root, GooseBrawlSetup.GoosePrefabPath);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(temp2);
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[Goose Brawl] Tunables synced to code defaults: " + sceneChanged + " scene field(s), " + prefabChanged + " goose prefab field(s) changed.");
        }

        static int Sync(Component target, GameObject temp, string[] fields)
        {
            var fresh = temp.GetComponent(target.GetType());
            if (fresh == null) fresh = temp.AddComponent(target.GetType());
            var so = new SerializedObject(target);
            var fo = new SerializedObject(fresh);
            int n = 0;
            foreach (var f in fields)
            {
                var p = so.FindProperty(f);
                var q = fo.FindProperty(f);
                if (p == null || q == null) { Debug.LogWarning("[Goose Brawl] Tune: field '" + f + "' missing on " + target.GetType().Name); continue; }
                if (SerializedProperty.DataEquals(p, q)) continue;
                Debug.Log("[Goose Brawl] Tune " + target.GetType().Name + "." + f + ": " + Describe(p) + " -> " + Describe(q));
                so.CopyFromSerializedProperty(q);
                n++;
            }
            if (n > 0) so.ApplyModifiedPropertiesWithoutUndo();
            return n;
        }

        static string Describe(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Float: return p.floatValue.ToString("0.###");
                case SerializedPropertyType.Integer: return p.intValue.ToString();
                case SerializedPropertyType.Boolean: return p.boolValue.ToString();
                case SerializedPropertyType.String: return p.stringValue;
                default:
                    if (p.isArray)
                    {
                        var parts = new string[p.arraySize];
                        for (int i = 0; i < p.arraySize; i++) parts[i] = Describe(p.GetArrayElementAtIndex(i));
                        return "[" + string.Join(", ", parts) + "]";
                    }
                    return p.propertyType.ToString();
            }
        }
    }
}
