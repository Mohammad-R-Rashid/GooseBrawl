using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GooseBrawl
{
    /// <summary>The big egg in the nest (a real egg shape). Tappable; flies to the camera when stolen; cracks into shell pieces, white and yolk.</summary>
    public class EggController : MonoBehaviour
    {
        public const float EggWidth = 0.17f;
        public const float EggHeight = 0.225f;

        public float tapMaxDistance = 6f;
        public float flyDuration = 0.55f;
        public bool Tappable { get; set; }
        public GameObject CrackedEgg { get; private set; }
        public Vector3 CrackPosition { get; private set; }

        SphereCollider m_Collider;
        Transform m_Nest;
        Vector3 m_LocalPos;
        Vector3 m_BaseScale;
        float m_Seed;
        Material m_Shell;

        public static EggController Create(MaterialLibrary mats, Transform parent)
        {
            var go = new GameObject("Egg");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0.075f, 0f);
            int layer = LayerMask.NameToLayer("Interactable");
            if (layer >= 0) go.layer = layer;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = ProceduralAssets.EggMesh("EggMesh", EggWidth, EggHeight);
            var r = go.AddComponent<MeshRenderer>();
            var shell = new Material(mats.Egg) { name = "Egg_Speckled" };
            shell.SetTexture("_BaseMap", ProceduralAssets.SpeckleTexture(512));
            shell.SetColor("_BaseColor", Color.white);
            shell.SetFloat("_Smoothness", 0.62f);
            shell.DisableKeyword("_EMISSION");
            r.sharedMaterial = shell;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
            var col = go.AddComponent<SphereCollider>();
            col.center = new Vector3(0f, EggHeight * 0.5f, 0f);
            col.radius = 0.28f; // generous tap target
            var egg = go.AddComponent<EggController>();
            egg.m_Collider = col;
            egg.m_Shell = shell;
            egg.m_Nest = parent;
            egg.m_LocalPos = go.transform.localPosition;
            egg.m_BaseScale = go.transform.localScale;
            egg.m_Seed = Random.value * 10f;
            return egg;
        }

        void Update()
        {
            if (!Tappable) return;
            float t = Time.time + m_Seed;
            transform.localRotation = Quaternion.Euler(Mathf.Sin(t * 1.1f) * 3f, t * 12f, Mathf.Sin(t * 1.5f) * 3f);
            transform.localPosition = m_LocalPos + Vector3.up * (0.006f * Mathf.Sin(t * 1.6f));

            if (GameInput.TryGetTap(out var screenPos) && !GameInput.IsPointerOverUI(screenPos))
            {
                var cam = Camera.main;
                if (cam == null) return;
                if (Physics.Raycast(cam.ScreenPointToRay(screenPos), out var hit, tapMaxDistance, ~0, QueryTriggerInteraction.Collide) && hit.collider == m_Collider)
                {
                    var mgr = GooseGameManager.Instance;
                    if (mgr != null) mgr.OnEggTapped();
                }
            }
        }

        public void ResetEgg()
        {
            ClearCrackedEgg();
            m_Held = false;
            m_HeldTime = 0f;
            if (transform.parent != m_Nest) transform.SetParent(m_Nest, false);
            gameObject.SetActive(true);
            transform.localPosition = m_LocalPos;
            transform.localRotation = Quaternion.identity;
            transform.localScale = m_BaseScale;
            Tappable = true;
        }

        /// <summary>The egg slips out of your hand, falls to the floor and cracks. Returns when it has landed.</summary>
        public IEnumerator DropAndCrack(Camera cam, float floorY, MaterialLibrary mats)
        {
            Tappable = false;
            m_Held = false;
            gameObject.SetActive(true);
            Vector3 fwd = cam != null ? cam.transform.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-3f) fwd = Vector3.forward;
            fwd.Normalize();

            // The slip: wobble in the hand, then it rolls off the fingers.
            float t = 0f;
            while (t < 0.45f && cam != null)
            {
                t += Time.deltaTime;
                float k = t / 0.45f;
                transform.localPosition = k_HandLocal + new Vector3(0.05f * k, -0.03f * k, 0.02f * k);
                transform.localRotation = Quaternion.Euler(Mathf.Sin(t * 30f) * 12f * k, t * 60f, 35f * k);
                yield return null;
            }
            transform.SetParent(null, true);
            transform.localScale = m_BaseScale;

            // Free fall with a little forward drift and tumbling.
            Vector3 pos = transform.position;
            Vector3 vel = fwd * 0.35f + Vector3.up * 0.15f;
            float landY = floorY + EggHeight * 0.35f;
            float spin = Random.Range(360f, 720f);
            float safety = 0f;
            while (pos.y > landY && safety < 3f)
            {
                float dt = Time.deltaTime;
                safety += dt;
                vel += Vector3.down * 9.81f * dt;
                pos += vel * dt;
                transform.position = pos;
                transform.Rotate(spin * dt, 0f, spin * 0.6f * dt, Space.World);
                yield return null;
            }
            pos.y = floorY;
            CrackPosition = pos;
            gameObject.SetActive(false);
            SpawnCrackedEgg(pos, fwd, mats);
            var splash = ProceduralAssets.CreateSplash(null, new Color(1f, 0.78f, 0.15f, 0.95f));
            splash.transform.position = pos + Vector3.up * 0.02f;
            splash.Play();
            Destroy(splash.gameObject, 3f);
        }

        void SpawnCrackedEgg(Vector3 pos, Vector3 fwd, MaterialLibrary mats)
        {
            if (CrackedEgg != null) Destroy(CrackedEgg);
            CrackedEgg = new GameObject("CrackedEgg");
            CrackedEgg.transform.position = pos;
            CrackedEgg.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(0f, Random.Range(-40f, 40f), 0f);

            var white = TransparentLit(mats.Egg, "EggWhite", new Color(0.97f, 0.96f, 0.9f, 0.86f), 0.92f);
            var yolk = new Material(mats.Egg) { name = "EggYolk" };
            yolk.SetColor("_BaseColor", new Color(1f, 0.70f, 0.08f, 1f));
            yolk.SetFloat("_Smoothness", 0.9f);
            yolk.DisableKeyword("_EMISSION");
            var shell = m_Shell != null ? m_Shell : mats.Egg;

            // Egg white: a flat organic puddle that spreads out.
            var whiteGo = new GameObject("White");
            whiteGo.transform.SetParent(CrackedEgg.transform, false);
            whiteGo.transform.localPosition = new Vector3(0.01f, 0.003f, 0.02f);
            whiteGo.transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            var wmf = whiteGo.AddComponent<MeshFilter>();
            wmf.sharedMesh = ProceduralAssets.DiscMesh("EggWhite", 0.15f, 0.28f, Random.Range(1, 9999));
            var wmr = whiteGo.AddComponent<MeshRenderer>();
            wmr.sharedMaterial = white;
            wmr.shadowCastingMode = ShadowCastingMode.Off;
            wmr.receiveShadows = true;
            StartCoroutine(Spread(whiteGo.transform, 0.35f));

            // Yolk dome.
            Piece(CrackedEgg.transform, "Yolk", PrimitiveType.Sphere, new Vector3(0.01f, 0.014f, 0.01f), new Vector3(0.078f, 0.034f, 0.078f), yolk, Quaternion.identity, castShadow: true);

            // Shell: bottom half on its side, top cap flipped, and small shards, all with jagged edges.
            ShellPiece("ShellBottom", shell, 0f, 0.48f, 0f, 360f, 0.09f, 11, new Vector3(-0.09f, 0f, 0.06f), Quaternion.Euler(105f, Random.Range(0f, 360f), 20f));
            ShellPiece("ShellTop", shell, 0.58f, 1f, 0f, 360f, 0.08f, 17, new Vector3(0.1f, 0f, -0.05f), Quaternion.Euler(-160f, Random.Range(0f, 360f), 15f));
            for (int i = 0; i < 6; i++)
            {
                float a = Random.value * Mathf.PI * 2f;
                float r = Random.Range(0.07f, 0.2f);
                float a0 = Random.Range(0f, 360f);
                ShellPiece("Shard" + i, shell, Random.Range(0.35f, 0.5f), Random.Range(0.55f, 0.7f), a0, a0 + Random.Range(28f, 55f), 0.12f, 100 + i,
                    new Vector3(Mathf.Cos(a) * r, 0.002f, Mathf.Sin(a) * r), Quaternion.Euler(Random.Range(70f, 110f), Random.value * 360f, Random.Range(-20f, 20f)));
            }
        }

        void ShellPiece(string name, Material mat, float vFrom, float vTo, float angleFrom, float angleTo, float jagged, int seed, Vector3 localPos, Quaternion rot)
        {
            var go = new GameObject(name);
            go.transform.SetParent(CrackedEgg.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = rot;
            var mf = go.AddComponent<MeshFilter>();
            var outer = ProceduralAssets.EggShellPieceMesh(name, EggWidth, EggHeight, vFrom, vTo, angleFrom, angleTo, jagged, seed);
            mf.sharedMesh = outer;
            var mr = go.AddComponent<MeshRenderer>();
            var m = new Material(mat) { name = mat.name + "_Shell" };
            mr.sharedMaterial = m;
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
            // Inside of the shell: the same surface with flipped normals so it is lit correctly (URP Lit does not flip back-face normals).
            var inner = new GameObject(name + "_Inner");
            inner.transform.SetParent(go.transform, false);
            var imf = inner.AddComponent<MeshFilter>();
            imf.sharedMesh = FlippedCopy(outer);
            var imr = inner.AddComponent<MeshRenderer>();
            var innerMat = new Material(m) { name = m.name + "_Inner" };
            innerMat.SetColor("_BaseColor", new Color(0.93f, 0.92f, 0.88f, 1f));
            innerMat.SetFloat("_Smoothness", 0.35f);
            imr.sharedMaterial = innerMat;
            imr.shadowCastingMode = ShadowCastingMode.Off;
            imr.receiveShadows = true;
            // Shell pieces rest on the floor: raise by the mesh's lowest point so nothing pokes through.
            var b = mr.bounds;
            float floorY = CrackedEgg.transform.position.y;
            if (b.min.y < floorY) go.transform.position += Vector3.up * (floorY - b.min.y + 0.002f);
        }

        /// <summary>Same geometry, reversed winding and normals: the visible inside of a thin shell.</summary>
        static Mesh FlippedCopy(Mesh src)
        {
            var m = new Mesh { name = src.name + "_Inner" };
            var verts = src.vertices;
            var normals = src.normals;
            for (int i = 0; i < normals.Length; i++) normals[i] = -normals[i];
            var tris = src.triangles;
            for (int i = 0; i < tris.Length; i += 3) { int t = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = t; }
            m.vertices = verts;
            m.normals = normals;
            m.uv = src.uv;
            m.triangles = tris;
            if (src.tangents != null && src.tangents.Length == verts.Length) m.tangents = src.tangents;
            m.RecalculateBounds();
            return m;
        }

        static Material TransparentLit(Material source, string name, Color color, float smoothness)
        {
            var m = new Material(source) { name = name };
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_EMISSION");
            m.renderQueue = (int)RenderQueue.Transparent;
            return m;
        }

        IEnumerator Spread(Transform t, float duration)
        {
            float e = 0f;
            while (e < duration && t != null)
            {
                e += Time.deltaTime;
                float k = Mathf.SmoothStep(0f, 1f, e / duration);
                t.localScale = Vector3.one * Mathf.Lerp(0.4f, 1f, k);
                yield return null;
            }
            if (t != null) t.localScale = Vector3.one;
        }

        static void Piece(Transform parent, string name, PrimitiveType type, Vector3 localPos, Vector3 scale, Material mat, Quaternion rot, bool castShadow = false)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.transform.localRotation = rot;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = castShadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        public void ClearCrackedEgg()
        {
            if (CrackedEgg != null) Destroy(CrackedEgg);
            CrackedEgg = null;
        }

        /// <summary>Held position in camera space: low right, like an egg in your hand in front of the phone.</summary>
        static readonly Vector3 k_HandLocal = new Vector3(0.06f, -0.14f, 0.44f);

        /// <summary>Fly from the nest into your hand and stay there (parented to the camera) until dropped. A quick, clean swoop, no spinning.</summary>
        public IEnumerator FlyToCamera(Camera cam)
        {
            Tappable = false;
            float t = 0f;
            Vector3 startPos = transform.position;
            Quaternion startRot = transform.rotation;
            Vector3 startScale = transform.localScale;
            transform.SetParent(null, true);
            while (t < flyDuration)
            {
                float dt = Time.deltaTime;
                t += dt;
                float k = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / flyDuration), 3f); // ease out
                Vector3 target = cam != null ? cam.transform.TransformPoint(k_HandLocal) : startPos + Vector3.up;
                Quaternion targetRot = cam != null ? cam.transform.rotation * Quaternion.Euler(k_HeldTilt) : startRot;
                transform.position = Vector3.Lerp(startPos, target, k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * 0.12f);
                transform.rotation = Quaternion.Slerp(startRot, targetRot, k);
                transform.localScale = startScale * Mathf.Lerp(1f, 0.9f, k);
                yield return null;
            }
            if (cam != null)
            {
                transform.SetParent(cam.transform, true);
                transform.localPosition = k_HandLocal;
                transform.localRotation = Quaternion.Euler(k_HeldTilt);
            }
            m_Held = true;
            m_HeldTime = 0f;
            HandShake = 0f;
        }

        static readonly Vector3 k_HeldTilt = new Vector3(18f, 0f, -12f);

        bool m_Held;
        float m_HeldTime;
        /// <summary>0 = steady hand, 1 = about to slip. Drives the wobble of the held egg (set by the carry phase).</summary>
        public float HandShake { get; set; }

        void LateUpdate()
        {
            if (!m_Held) return;
            m_HeldTime += Time.deltaTime;
            float shake = Mathf.Clamp01(HandShake);
            float f = 3f + 9f * shake;
            // Gentle bob in the hand so it reads as "held", plus a growing wobble the closer it is to slipping.
            Vector3 jitter = new Vector3(Mathf.Sin(m_HeldTime * f * 1.3f), Mathf.Sin(m_HeldTime * f), Mathf.Cos(m_HeldTime * f * 0.7f)) * (0.004f + 0.02f * shake);
            transform.localPosition = k_HandLocal + new Vector3(0f, 0.008f * Mathf.Sin(m_HeldTime * 3f), 0f) + jitter;
            float wobble = 4f + 22f * shake;
            transform.localRotation = Quaternion.Euler(k_HeldTilt + new Vector3(Mathf.Sin(m_HeldTime * f) * wobble, Mathf.Sin(m_HeldTime * f * 0.6f) * wobble * 0.6f, Mathf.Cos(m_HeldTime * f * 1.1f) * wobble));
        }
    }
}
