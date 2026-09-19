using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Builds a clearly-labelled placeholder goose out of primitives when the real model is missing.
    /// Bone names match the real asset (Neck_1, Head, Wing_1.L ...) so the procedural animation works.
    /// </summary>
    public static class GoosePlaceholderFactory
    {
        public static GameObject CreateModel(Material body, Material accent)
        {
            var root = new GameObject("PLACEHOLDER_GooseModel");

            var torso = Part(root.transform, "Body", PrimitiveType.Sphere, new Vector3(0f, 0.36f, 0f), new Vector3(0.36f, 0.30f, 0.52f), body);
            Part(root.transform, "Tail", PrimitiveType.Cube, new Vector3(0f, 0.42f, -0.28f), new Vector3(0.14f, 0.06f, 0.16f), body).transform.localRotation = Quaternion.Euler(-25f, 0f, 0f);

            var neck1 = new GameObject("Neck_1").transform;
            neck1.SetParent(root.transform, false);
            neck1.localPosition = new Vector3(0f, 0.48f, 0.18f);
            Part(neck1, "NeckMesh", PrimitiveType.Capsule, new Vector3(0f, 0.14f, 0.03f), new Vector3(0.11f, 0.16f, 0.11f), body);

            var head = new GameObject("Head").transform;
            head.SetParent(neck1, false);
            head.localPosition = new Vector3(0f, 0.3f, 0.05f);
            Part(head, "HeadMesh", PrimitiveType.Sphere, Vector3.zero, new Vector3(0.17f, 0.15f, 0.2f), body);
            Part(head, "Beak", PrimitiveType.Cube, new Vector3(0f, -0.02f, 0.14f), new Vector3(0.06f, 0.05f, 0.13f), accent);
            Part(head, "Eye.L", PrimitiveType.Sphere, new Vector3(-0.06f, 0.03f, 0.06f), Vector3.one * 0.035f, null);
            Part(head, "Eye.R", PrimitiveType.Sphere, new Vector3(0.06f, 0.03f, 0.06f), Vector3.one * 0.035f, null);

            Wing(root.transform, "Wing_1.L", -1f, body);
            Wing(root.transform, "Wing_1.R", 1f, body);
            Leg(root.transform, "Leg_1.L", -0.08f, accent, body);
            Leg(root.transform, "Leg_1.R", 0.08f, accent, body);

            foreach (var c in root.GetComponentsInChildren<Collider>()) Object.Destroy(c);
            return root;
        }

        static void Wing(Transform parent, string name, float side, Material mat)
        {
            var wing = new GameObject(name).transform;
            wing.SetParent(parent, false);
            wing.localPosition = new Vector3(side * 0.17f, 0.45f, 0.02f);
            var mesh = Part(wing, "WingMesh", PrimitiveType.Cube, new Vector3(side * 0.03f, -0.08f, -0.04f), new Vector3(0.05f, 0.16f, 0.34f), mat);
            mesh.transform.localRotation = Quaternion.Euler(0f, 0f, side * -8f);
        }

        static void Leg(Transform parent, string name, float x, Material accent, Material body)
        {
            var leg = new GameObject(name).transform;
            leg.SetParent(parent, false);
            leg.localPosition = new Vector3(x, 0.22f, 0f);
            Part(leg, "LegMesh", PrimitiveType.Cylinder, new Vector3(0f, -0.11f, 0f), new Vector3(0.035f, 0.11f, 0.035f), accent);
            Part(leg, "Foot", PrimitiveType.Cube, new Vector3(0f, -0.215f, 0.04f), new Vector3(0.09f, 0.02f, 0.14f), accent);
        }

        static GameObject Part(Transform parent, string name, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            if (mat != null) r.sharedMaterial = mat;
            else r.sharedMaterial = ProceduralAssets.LitMaterial("Eye_Runtime", Color.black, 0.8f);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            var col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            return go;
        }

        /// <summary>Runtime fallback when no goose prefab is assigned: a full goose GameObject with all components.</summary>
        public static GameObject CreateRuntimeGoose(MaterialLibrary mats)
        {
            Debug.LogWarning("[GooseBrawl] Goose prefab missing; spawning a placeholder goose. Run Goose Brawl > Setup Project.");
            var root = new GameObject("Goose (Placeholder)");
            var model = CreateModel(mats != null ? mats.PlaceholderGoose : null, mats != null ? mats.Accent : null);
            model.transform.SetParent(root.transform, false);

            var shadow = GameObject.CreatePrimitive(PrimitiveType.Quad);
            shadow.name = "BlobShadow";
            Object.Destroy(shadow.GetComponent<Collider>());
            shadow.transform.SetParent(root.transform, false);
            shadow.transform.localPosition = new Vector3(0f, 0.01f, 0f);
            shadow.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            shadow.transform.localScale = new Vector3(0.7f, 0.9f, 1f);
            var sr = shadow.GetComponent<Renderer>();
            if (mats != null) sr.sharedMaterial = mats.Shadow;
            sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            root.AddComponent<GooseMovement>();
            root.AddComponent<GooseObstacleAvoidance>();
            root.AddComponent<GooseAttackController>();
            root.AddComponent<GooseProceduralAnimation>();
            root.AddComponent<GooseAnimationResolver>();
            var vis = root.AddComponent<GooseVisualController>();
            vis.modelRoot = model.transform;
            vis.blobShadow = shadow.transform;
            root.AddComponent<GooseChaseController>();
            return root;
        }
    }
}
