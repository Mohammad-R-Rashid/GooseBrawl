using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GooseBrawl
{
    /// <summary>
    /// The goose nest: a lumpy mud bowl, one combined mesh of woven twig strands (normal mapped), a few down
    /// feathers, the egg, and a warm point light that only burns while the egg is home. Everything casts a
    /// real shadow onto the shadow catcher. No decals, no glow sprites.
    /// </summary>
    public class NestController : MonoBehaviour
    {
        public EggController Egg { get; private set; }

        Light m_Light;
        float m_LightBase = 0f;
        float m_Seed;

        public static NestController Create(MaterialLibrary mats)
        {
            var go = new GameObject("GooseNest");
            var nest = go.AddComponent<NestController>();
            nest.Build(mats);
            go.SetActive(false);
            return nest;
        }

        void Build(MaterialLibrary mats)
        {
            m_Seed = Random.value * 10f;
            var twigMat = TwigMaterial(mats);

            if (mats.nestModel != null)
            {
                BuildFromModel(mats.nestModel);
            }
            else
            {
                // Mud bowl: darker, rougher clone of the twig material.
                var bowlMat = new Material(twigMat) { name = "Nest_Bowl" };
                bowlMat.SetColor("_BaseColor", new Color(0.62f, 0.52f, 0.42f));
                bowlMat.SetFloat("_Smoothness", 0.06f);
                bowlMat.SetTextureScale("_BaseMap", new Vector2(2f, 0.5f));
                bowlMat.SetTextureScale("_BumpMap", new Vector2(2f, 0.5f));
                MeshPart("Bowl", ProceduralAssets.NestBowlMesh("NestBowl", 0.27f, 0.16f, 1234), bowlMat, Vector3.zero);

                // All woven strands as one draw call. They do not cast shadows: dozens of thin twig shadows
                // on the egg read as noise, and the bowl already grounds the nest on the floor.
                var strands = ProceduralAssets.NestStrandsMesh("NestStrands", 70, 1234, out _);
                MeshPart("Strands", strands, twigMat, Vector3.zero, castShadows: false);

                // A few down feathers inside the bowl.
                var featherMat = ProceduralAssets.UnlitTransparent("NestDown_Runtime", new Color(1f, 1f, 0.98f, 0.92f), ProceduralAssets.FeatherTexture());
                Random.State state = Random.state;
                Random.InitState(4321);
                for (int i = 0; i < 7; i++)
                {
                    var q = ProceduralAssets.CreateQuad("Down" + i, 0.055f, featherMat);
                    q.transform.SetParent(transform, false);
                    float a = Random.Range(0f, Mathf.PI * 2f);
                    float r = Random.Range(0.02f, 0.13f);
                    q.transform.localPosition = new Vector3(Mathf.Cos(a) * r, 0.105f + Random.Range(0f, 0.012f), Mathf.Sin(a) * r);
                    q.transform.localRotation = Quaternion.Euler(Random.Range(-14f, 14f), Random.Range(0f, 360f), Random.Range(-14f, 14f));
                    var qr = q.GetComponent<Renderer>();
                    qr.shadowCastingMode = ShadowCastingMode.Off;
                }
                Random.state = state;
            }

            Egg = EggController.Create(mats, transform);

            // Optional warm light on the egg. Off by default: a virtual hot spot on a real-lit egg looked wrong on the phone.
            if (m_LightBase <= 0f) return;
            var lightGo = new GameObject("NestLight");
            lightGo.transform.SetParent(transform, false);
            lightGo.transform.localPosition = new Vector3(0.05f, 0.42f, -0.06f);
            m_Light = lightGo.AddComponent<Light>();
            m_Light.type = LightType.Point;
            m_Light.range = 0.8f;
            m_Light.intensity = m_LightBase;
            m_Light.color = new Color(1f, 0.93f, 0.82f);
            m_Light.shadows = LightShadows.None;
        }

        static Material TwigMaterial(MaterialLibrary mats)
        {
            var m = mats.Nest;
            if (m != null && m.GetTexture("_BaseMap") != null) return m;
            // Runtime fallback when Setup has not created the textured asset yet.
            var clone = new Material(m) { name = "Nest_Twigs_Runtime" };
            clone.SetTexture("_BaseMap", ProceduralAssets.TwigStripTexture());
            clone.SetColor("_BaseColor", Color.white);
            clone.SetFloat("_Smoothness", 0.14f);
            clone.SetTexture("_BumpMap", ProceduralAssets.TwigNormalTexture());
            clone.EnableKeyword("_NORMALMAP");
            return clone;
        }

        void MeshPart(string name, Mesh mesh, Material mat, Vector3 localPos, bool castShadows = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            mr.receiveShadows = true;
        }

        /// <summary>Use a real nest model: instantiate, scale to ~0.5 m across and sit it on the floor.</summary>
        void BuildFromModel(GameObject model)
        {
            var inst = Instantiate(model, transform);
            inst.name = "NestModel";
            foreach (var c in inst.GetComponentsInChildren<Collider>()) Destroy(c);
            var renderers = inst.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            var b = renderers[0].bounds;
            foreach (var r in renderers) { b.Encapsulate(r.bounds); r.shadowCastingMode = ShadowCastingMode.On; }
            float size = Mathf.Max(b.size.x, b.size.z);
            if (size > 0.001f) inst.transform.localScale = inst.transform.localScale * (0.5f / size);
            b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            inst.transform.position += transform.position - new Vector3(b.center.x, b.min.y, b.center.z);
        }

        public void PlaceAt(Vector3 floorPosition, Vector3 playerPosition)
        {
            transform.position = floorPosition;
            var d = playerPosition - floorPosition;
            d.y = 0f;
            if (d.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
            gameObject.SetActive(true);
            Egg.ResetEgg();
        }

        public IEnumerator StealEgg(Camera cam)
        {
            yield return Egg.FlyToCamera(cam);
        }

        /// <summary>The fumble: the egg reappears in your hand, drops and cracks on the floor.</summary>
        public IEnumerator DropEgg(Camera cam, float floorY, MaterialLibrary mats)
        {
            yield return Egg.DropAndCrack(cam, floorY, mats);
        }

        public IEnumerator DropEgg(Camera cam, float floorY, MaterialLibrary mats, Vector3 initialWorldVelocity)
        {
            yield return Egg.DropAndCrack(cam, floorY, mats, initialWorldVelocity);
        }

        public void ResetEgg()
        {
            gameObject.SetActive(true);
            Egg.ResetEgg();
        }

        void Update()
        {
            if (m_Light == null) return;
            bool eggPresent = Egg != null && Egg.gameObject.activeSelf && Egg.Tappable;
            float target = eggPresent ? m_LightBase * (0.92f + 0.08f * Mathf.Sin((Time.time + m_Seed) * 1.7f)) : 0f;
            m_Light.intensity = Mathf.MoveTowards(m_Light.intensity, target, Time.deltaTime * 2.5f);
            if (m_Light.enabled != m_Light.intensity > 0.01f) m_Light.enabled = m_Light.intensity > 0.01f;
        }
    }
}
