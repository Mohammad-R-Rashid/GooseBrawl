using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GooseBrawl
{
    /// <summary>
    /// Tap-to-place for the nest. Shows a soft ring plus a translucent ghost of the nest on the floor under
    /// the screen centre, and places the nest at the tap point (or at the reticle when the tap misses the floor).
    /// </summary>
    public class ARPlacementController : MonoBehaviour
    {
        public float reticleRadius = 0.22f;
        public event Action<Pose> Placed;

        public bool Active
        {
            get => m_Active;
            set
            {
                m_Active = value;
                if (m_Reticle != null) m_Reticle.SetActive(false);
            }
        }

        public bool HasReticle { get; private set; }
        public Pose ReticlePose { get; private set; }

        static readonly List<ARRaycastHit> s_Hits = new List<ARRaycastHit>();
        GameObject m_Reticle;
        Transform m_Ring;
        bool m_Active;
        bool m_HadReticle;

        void Update()
        {
            if (!m_Active) return;
            var mgr = GooseGameManager.Instance;
            if (mgr == null || mgr.Player.Cam == null) return;

            Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            HasReticle = RaycastFloor(center, out var pose);
            if (HasReticle)
            {
                ReticlePose = pose;
                EnsureReticle(mgr);
                m_Reticle.SetActive(true);
                m_Reticle.transform.position = Vector3.Lerp(m_Reticle.transform.position, pose.position + Vector3.up * 0.006f, m_HadReticle ? 1f - Mathf.Exp(-Time.deltaTime * 18f) : 1f);
                // Face the player so the ghost nest previews the real orientation.
                Vector3 toPlayer = mgr.Player.Position - pose.position; toPlayer.y = 0f;
                if (toPlayer.sqrMagnitude > 1e-4f) m_Reticle.transform.rotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up);
                float pulse = 1f + 0.04f * Mathf.Sin(Time.time * 4f);
                if (m_Ring != null)
                {
                    m_Ring.localScale = Vector3.one * pulse;
                    m_Ring.localRotation = Quaternion.Euler(0f, Time.time * 20f, 0f);
                }
                if (!m_HadReticle) mgr.Haptics.Transient(0.3f, 0.6f);
            }
            else if (m_Reticle != null)
            {
                m_Reticle.SetActive(false);
            }
            m_HadReticle = HasReticle;

            if (GameInput.TryGetTap(out var tap) && !GameInput.IsPointerOverUI(tap))
            {
                if (RaycastFloor(tap, out var tapPose)) Place(tapPose);
                else if (HasReticle) Place(ReticlePose);
            }
        }

        /// <summary>Place the nest as if the player tapped this screen point (used by automation).</summary>
        public bool TryPlaceAtScreenPoint(Vector2 screenPos)
        {
            if (!m_Active) return false;
            if (RaycastFloor(screenPos, out var pose)) { Place(pose); return true; }
            if (HasReticle) { Place(ReticlePose); return true; }
            return false;
        }

        void Place(Pose pose)
        {
            if (m_Reticle != null) m_Reticle.SetActive(false);
            m_HadReticle = false;
            Placed?.Invoke(pose);
        }

        /// <summary>Raycast a screen point against detected horizontal-up floor. Mock mode uses physics.</summary>
        public bool RaycastFloor(Vector2 screenPos, out Pose pose)
        {
            pose = default;
            var mgr = GooseGameManager.Instance;
            var cam = mgr.Player.Cam;
            if (cam == null) return false;

            if (mgr.AR.IsMock)
            {
                var ray = cam.ScreenPointToRay(screenPos);
                if (Physics.Raycast(ray, out var hit, 20f, mgr.Environment.EnvironmentMask, QueryTriggerInteraction.Ignore) && hit.normal.y > 0.7f)
                {
                    pose = new Pose(hit.point, Quaternion.identity);
                    return true;
                }
                return false;
            }

            var rm = mgr.AR.raycastManager;
            var pm = mgr.AR.planeManager;
            if (rm == null) return false;
            s_Hits.Clear();
            if (!rm.Raycast(screenPos, s_Hits, TrackableType.PlaneWithinPolygon)) return false;
            foreach (var h in s_Hits)
            {
                var plane = pm != null ? pm.GetPlane(h.trackableId) : null;
                if (plane != null && plane.alignment != PlaneAlignment.HorizontalUp) continue;
                pose = new Pose(h.pose.position, Quaternion.identity);
                return true;
            }
            return false;
        }

        void EnsureReticle(GooseGameManager mgr)
        {
            if (m_Reticle != null) return;
            m_Reticle = new GameObject("PlacementReticle");
            var ring = ProceduralAssets.CreateQuad("Ring", reticleRadius * 2f, mgr.Materials.Reticle);
            ring.transform.SetParent(m_Reticle.transform, false);
            m_Ring = ring.transform;

            // Ghost nest: the real nest meshes, translucent.
            var ghostMat = new Material(mgr.Materials.Reticle) { name = "NestGhost_Runtime" };
            ghostMat.SetTexture("_BaseMap", null);
            ghostMat.SetColor("_BaseColor", new Color(1f, 0.965f, 0.87f, 0.32f));
            var ghost = new GameObject("GhostNest");
            ghost.transform.SetParent(m_Reticle.transform, false);
            AddGhostPart(ghost.transform, ProceduralAssets.NestBowlMesh("GhostBowl", 0.27f, 0.16f, 1234), ghostMat);
            AddGhostPart(ghost.transform, ProceduralAssets.NestStrandsMesh("GhostStrands", 70, 1234, out _), ghostMat);
            AddGhostPart(ghost.transform, ProceduralAssets.EggMesh("GhostEgg", EggController.EggWidth, EggController.EggHeight), ghostMat, new Vector3(0f, 0.075f, 0f));
        }

        static void AddGhostPart(Transform parent, Mesh mesh, Material mat, Vector3 localPos = default)
        {
            var go = new GameObject(mesh.name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }
    }
}
