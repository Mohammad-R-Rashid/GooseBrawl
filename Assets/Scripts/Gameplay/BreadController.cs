using System.Collections;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// A bread roll the player throws: flies from the hand on a short arc, bounces once on the real floor and rolls to a
    /// stop, then the goose eats it (it shrinks as the beak works). Procedural lathe mesh with a generated crust, same
    /// fidelity bar as the egg and the nest. Never uses physics: the arc, bounce and roll are scripted so it always
    /// lands where the goose can reach it.
    /// </summary>
    public class BreadController : MonoBehaviour
    {
        /// <summary>A slice of toast: width and height of the face, Height is the thickness (it lies face-up on the floor).</summary>
        public const float Width = 0.11f;
        public const float SliceHeight = 0.105f;
        public const float Height = 0.014f;

        public bool Landed { get; private set; }
        public bool Eaten { get; private set; }
        public Vector3 LandingPosition { get; private set; }

        Transform m_Model;
        float m_Floor;
        Vector3 m_BaseScale = Vector3.one;

        public static BreadController Create(MaterialLibrary mats, Vector3 at)
        {
            var go = new GameObject("BreadSlice");
            go.transform.position = at;
            var ctrl = go.AddComponent<BreadController>();
            var model = new GameObject("Model");
            model.transform.SetParent(go.transform, false);
            var mf = model.AddComponent<MeshFilter>();
            mf.sharedMesh = ProceduralAssets.ToastSliceMesh("BreadSlice", Width, SliceHeight, Height);
            var mr = model.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mats != null ? mats.Bread : ProceduralAssets.LitMaterial("Bread_Runtime", new Color(0.83f, 0.6f, 0.33f), 0.15f);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            mr.receiveShadows = true;
            ctrl.m_Model = model.transform;
            return ctrl;
        }

        /// <summary>Arc from the hand to a floor point, one bounce, a short roll. Calls back when it has stopped.</summary>
        public IEnumerator Throw(Vector3 from, Vector3 to, float floorY, GooseGameManager mgr)
        {
            m_Floor = floorY;
            LandingPosition = new Vector3(to.x, floorY, to.z);
            transform.position = from;
            Vector3 spinAxis = Random.onUnitSphere;
            float flat = Vector3.Distance(new Vector3(from.x, 0f, from.z), new Vector3(to.x, 0f, to.z));
            float duration = Mathf.Clamp(0.55f + flat * 0.12f, 0.6f, 1.3f);
            float peak = Mathf.Max(from.y, floorY) + Mathf.Clamp(0.25f + flat * 0.08f, 0.3f, 0.75f);
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / duration);
                Vector3 p = Vector3.Lerp(from, LandingPosition, k);
                // Parabola through the peak.
                float h = Mathf.Lerp(from.y, floorY, k) + (peak - Mathf.Lerp(from.y, floorY, 0.5f)) * 4f * k * (1f - k);
                p.y = Mathf.Max(floorY + Height * 0.5f, h);
                transform.position = p;
                m_Model.rotation = Quaternion.AngleAxis(t * 540f, spinAxis) * m_Model.rotation;
                yield return null;
            }
            // Bounce and roll.
            if (mgr != null)
            {
                mgr.Audio.PlayBreadThud(LandingPosition);
                mgr.Haptics.Light();
                var dust = ProceduralAssets.CreateDustPuff(transform, mgr.Materials != null ? mgr.Materials.Dust : null, 6);
                dust.transform.position = LandingPosition + Vector3.up * 0.02f;
                dust.Play();
            }
            Vector3 rollDir = (LandingPosition - from); rollDir.y = 0f; rollDir = rollDir.sqrMagnitude > 1e-4f ? rollDir.normalized : Vector3.forward;
            float bounceT = 0f;
            const float bounceDur = 0.32f;
            Vector3 start = LandingPosition;
            Vector3 end = LandingPosition + rollDir * 0.2f;
            while (bounceT < bounceDur)
            {
                bounceT += Time.deltaTime;
                float k = Mathf.Clamp01(bounceT / bounceDur);
                Vector3 p = Vector3.Lerp(start, end, k);
                p.y = floorY + Height * 0.5f + 0.12f * 4f * k * (1f - k);
                transform.position = p;
                m_Model.rotation = Quaternion.AngleAxis(Time.deltaTime * 360f, Vector3.Cross(Vector3.up, rollDir)) * m_Model.rotation;
                yield return null;
            }
            LandingPosition = new Vector3(end.x, floorY, end.z);
            transform.position = LandingPosition + Vector3.up * Height * 0.5f;
            // Lands face-up (the face normal is +Z on the mesh), any heading, a little tilt.
            m_Model.rotation = Quaternion.AngleAxis(Random.Range(0f, 360f), Vector3.up) * Quaternion.Euler(-90f + Random.Range(-6f, 6f), 0f, Random.Range(-6f, 6f));
            m_BaseScale = m_Model.localScale;
            Landed = true;
        }

        /// <summary>The goose is eating: shrink over the last part, then vanish.</summary>
        public IEnumerator Eat(float seconds)
        {
            float t = 0f;
            while (t < seconds)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01((t - (seconds - 0.45f)) / 0.45f);
                float s = Mathf.Lerp(1f, 0.05f, k);
                // Bites: a little squash each peck rhythm.
                float bite = 1f - 0.08f * Mathf.Abs(Mathf.Sin(t * Mathf.PI * 2f));
                m_Model.localScale = new Vector3(m_BaseScale.x * s * bite, m_BaseScale.y * s, m_BaseScale.z * s * bite);
                yield return null;
            }
            Eaten = true;
            Destroy(gameObject);
        }
    }
}
