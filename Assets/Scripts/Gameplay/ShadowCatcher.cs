using UnityEngine;
using UnityEngine.Rendering;

namespace GooseBrawl
{
    /// <summary>
    /// An invisible floor quad that only renders the shadows cast on it (GooseBrawl/ShadowCatcher shader).
    /// One follows the goose on the floor plane, one sits under the nest.
    /// </summary>
    public class ShadowCatcher : MonoBehaviour
    {
        public Transform follow;
        public float floorY;
        public float lift = 0.01f;

        public static ShadowCatcher Create(string name, float size, Material material, float floorY, Transform follow)
        {
            var go = new GameObject(name);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = true;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mf.sharedMesh = QuadMesh(size);
            var sc = go.AddComponent<ShadowCatcher>();
            sc.follow = follow;
            sc.floorY = floorY;
            sc.Snap();
            return sc;
        }

        static Mesh QuadMesh(float size)
        {
            float h = size * 0.5f;
            var m = new Mesh { name = "ShadowCatcherQuad" };
            m.vertices = new[] { new Vector3(-h, 0f, -h), new Vector3(-h, 0f, h), new Vector3(h, 0f, h), new Vector3(h, 0f, -h) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            m.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            return m;
        }

        public void Snap()
        {
            if (follow != null)
            {
                var p = follow.position;
                transform.position = new Vector3(p.x, floorY + lift, p.z);
            }
            else
            {
                var p = transform.position;
                transform.position = new Vector3(p.x, floorY + lift, p.z);
            }
        }

        void LateUpdate()
        {
            if (follow == null) return;
            var p = follow.position;
            transform.position = new Vector3(p.x, floorY + lift, p.z);
        }
    }
}
