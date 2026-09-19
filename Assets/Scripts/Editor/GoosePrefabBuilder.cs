using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GooseBrawl.Editor
{
    /// <summary>Imports the goose model, resolves its clips and assembles the Goose prefab (or a placeholder).</summary>
    public static class GoosePrefabBuilder
    {
        public const float TargetHeightMeters = 1.05f;

        public static string FindGooseModel(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return null;
            var guids = AssetDatabase.FindAssets("t:Model", new[] { folder });
            var paths = guids.Select(AssetDatabase.GUIDToAssetPath).Where(p => !p.EndsWith(".blend") && !p.EndsWith(".blend1")).ToList();
            if (paths.Count == 0) return null;
            // Prefer a file that contains animations, then anything named goose.
            string best = paths.FirstOrDefault(p => Path.GetFileName(p).ToLowerInvariant().Contains("anim"))
                          ?? paths.FirstOrDefault(p => Path.GetFileName(p).ToLowerInvariant().Contains("goose"))
                          ?? paths[0];
            return best;
        }

        public static GameObject Build(MaterialLibrary lib, string gooseFolder, string prefabPath)
        {
            string fbx = FindGooseModel(gooseFolder);
            var root = new GameObject("Goose");
            GameObject model = null;
            AnimationClip[] clips = null;

            if (fbx != null)
            {
                ConfigureModelImporter(fbx);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
                if (asset != null)
                {
                    model = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                    model.name = "GooseModel";
                    model.transform.SetParent(root.transform, false);
                    clips = AssetDatabase.LoadAllAssetRepresentationsAtPath(fbx)
                        .OfType<AnimationClip>()
                        .Where(c => !c.name.StartsWith("__preview__"))
                        .ToArray();
                    ApplyMaterialsAndScale(model, lib.gooseMaterial);
                    Debug.Log("[Goose Brawl] Goose model: " + fbx + " (" + clips.Length + " clips, " + CountBones(model) + " transforms)");
                }
            }

            if (model == null)
            {
                Debug.LogWarning("[Goose Brawl] No goose model found under " + gooseFolder + ". Building a PLACEHOLDER goose.");
                model = GoosePlaceholderFactory.CreateModel(lib.placeholderGooseMaterial, lib.accentMaterial);
                model.transform.SetParent(root.transform, false);
            }

            var animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            // Contact shadow (multiplicative darkening under the body); the real shadow comes from the shadow catcher.
            var shadow = GameObject.CreatePrimitive(PrimitiveType.Quad);
            shadow.name = "BlobShadow";
            Object.DestroyImmediate(shadow.GetComponent<Collider>());
            shadow.transform.SetParent(root.transform, false);
            shadow.transform.localPosition = new Vector3(0f, 0.012f, 0.02f);
            shadow.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            shadow.transform.localScale = new Vector3(0.8f, 1.1f, 1f);
            var sr = shadow.GetComponent<MeshRenderer>();
            sr.sharedMaterial = lib.shadowMaterial;
            sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sr.receiveShadows = false;

            // Audio sources (3D, with a low-pass so the goose sounds muffled when it is behind you)
            var voice = new GameObject("Voice").AddComponent<AudioSource>();
            voice.transform.SetParent(root.transform, false);
            voice.transform.localPosition = new Vector3(0f, 0.55f, 0.25f);
            var body = new GameObject("Body").AddComponent<AudioSource>();
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.25f, 0f);
            foreach (var s in new[] { voice, body })
            {
                s.playOnAwake = false;
                s.spatialBlend = 1f;
                s.rolloffMode = AudioRolloffMode.Linear;
                s.minDistance = 0.8f;
                s.maxDistance = 10f;
                s.dopplerLevel = s == body ? 0.3f : 0f;
                var lp = s.gameObject.AddComponent<AudioLowPassFilter>();
                lp.cutoffFrequency = 22000f;
                lp.lowpassResonanceQ = 1f;
            }

            // Components
            root.AddComponent<GooseMovement>();
            root.AddComponent<GooseObstacleAvoidance>();
            root.AddComponent<GooseAttackController>();
            root.AddComponent<GooseProceduralAnimation>();
            var resolver = root.AddComponent<GooseAnimationResolver>();
            resolver.clips = clips ?? new AnimationClip[0];
            var vis = root.AddComponent<GooseVisualController>();
            vis.modelRoot = model.transform;
            vis.animator = animator;
            vis.resolver = resolver;
            vis.blobShadow = shadow.transform;
            var chase = root.AddComponent<GooseChaseController>();
            chase.voiceSource = voice;
            chase.bodySource = body;

            GooseBrawlSetup.EnsureFolder(Path.GetDirectoryName(prefabPath).Replace('\\', '/'));
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
            Debug.Log("[Goose Brawl] Saved goose prefab: " + prefabPath);
            return prefab;
        }

        static int CountBones(GameObject go) => go.GetComponentsInChildren<Transform>(true).Length;

        public static void ConfigureModelImporter(string path)
        {
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null) return;
            bool changed = false;
            if (mi.animationType != ModelImporterAnimationType.Generic) { mi.animationType = ModelImporterAnimationType.Generic; changed = true; }
            if (!mi.importAnimation) { mi.importAnimation = true; changed = true; }
            if (mi.materialImportMode != ModelImporterMaterialImportMode.None) { mi.materialImportMode = ModelImporterMaterialImportMode.None; changed = true; }
            if (mi.importBlendShapes) { mi.importBlendShapes = false; changed = true; }
            if (mi.importCameras) { mi.importCameras = false; changed = true; }
            if (mi.importLights) { mi.importLights = false; changed = true; }
            if (mi.optimizeGameObjects) { mi.optimizeGameObjects = false; changed = true; }
            if (mi.animationCompression != ModelImporterAnimationCompression.Optimal) { mi.animationCompression = ModelImporterAnimationCompression.Optimal; changed = true; }

            // Only keep the clips we can use and mark the cyclic ones as looping.
            var defaults = mi.defaultClipAnimations;
            if (defaults != null && defaults.Length > 0)
            {
                var keep = new List<ModelImporterClipAnimation>();
                foreach (var c in defaults)
                {
                    var n = GooseAnimationResolver.Normalize(c.name);
                    if (n.EndsWith("_rm") || n.Contains("sit") || n.Contains("swim") || n.Contains("sleep") || n.Contains("eat") || n.Contains("drink") || n.Contains("turn_"))
                        continue;
                    c.loopTime = n.Contains("idle") || n.Contains("walk") || n.Contains("run_f") || n.Contains("fly_f") || n.Contains("hissing") || n.Contains("swim");
                    c.loopPose = false;
                    c.keepOriginalOrientation = true;
                    c.keepOriginalPositionXZ = true;
                    c.keepOriginalPositionY = true;
                    keep.Add(c);
                }
                if (keep.Count > 0 && (mi.clipAnimations == null || mi.clipAnimations.Length != keep.Count))
                {
                    mi.clipAnimations = keep.ToArray();
                    changed = true;
                }
            }
            if (changed)
            {
                mi.SaveAndReimport();
                Debug.Log("[Goose Brawl] Re-imported goose model with Generic rig and looping clips: " + path);
            }
        }

        static void ApplyMaterialsAndScale(GameObject model, Material material)
        {
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (material != null)
                {
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++) mats[i] = material;
                    r.sharedMaterials = mats;
                }
                // The goose casts a real shadow on the floor and onto itself.
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) smr.updateWhenOffscreen = true; // accurate bounds while we measure the model below
            }

            // Scale to a believable goose height and put the feet on y = 0.
            var b = ComputeBounds(model);
            if (b.size.y > 0.0001f)
            {
                float s = TargetHeightMeters / b.size.y;
                model.transform.localScale = Vector3.one * s;
            }
            b = ComputeBounds(model);
            var center = model.transform.parent != null ? model.transform.parent.InverseTransformPoint(b.center) : b.center;
            float minY = model.transform.parent != null ? model.transform.parent.InverseTransformPoint(new Vector3(b.center.x, b.min.y, b.center.z)).y : b.min.y;
            model.transform.localPosition -= new Vector3(center.x, minY, center.z);

            // Face +Z: find the head and rotate the model so the head points forward.
            var head = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.ToLowerInvariant() == "head")
                       ?? model.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.ToLowerInvariant().Contains("head"));
            var beak = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.ToLowerInvariant().Contains("beak"));
            var tail = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.ToLowerInvariant().StartsWith("tail"));
            b = ComputeBounds(model);
            Vector3 front = beak != null ? beak.position : (head != null ? head.position : Vector3.zero);
            Vector3 back = tail != null ? tail.position : b.center;
            if (front != Vector3.zero)
            {
                Vector3 dir = front - back;
                dir.y = 0f;
                if (dir.sqrMagnitude > 1e-4f)
                {
                    float angle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                    if (Mathf.Abs(angle) > 5f)
                    {
                        model.transform.localRotation = Quaternion.Euler(0f, -angle, 0f) * model.transform.localRotation;
                        Debug.Log("[Goose Brawl] Rotated goose model by " + (-angle).ToString("F0") + " degrees so it faces +Z.");
                    }
                }
            }
            b = ComputeBounds(model);
            center = model.transform.parent != null ? model.transform.parent.InverseTransformPoint(b.center) : b.center;
            model.transform.localPosition -= new Vector3(center.x, 0f, center.z);
            Debug.Log("[Goose Brawl] Goose bounds after scaling: " + ComputeBounds(model).size);

            // Generous fixed bounds instead of per-frame bounds recomputation: the goose is only culled when far off-screen.
            foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.updateWhenOffscreen = false;
                smr.localBounds = new Bounds(Vector3.zero, Vector3.one * 3.5f / Mathf.Max(0.001f, model.transform.localScale.x));
            }
        }

        static Bounds ComputeBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            var b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return b;
        }
    }
}
