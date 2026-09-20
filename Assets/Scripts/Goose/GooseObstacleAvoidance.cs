using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Local steering against the scanned environment. Samples a fan of horizontal directions with
    /// capsule casts (the goose body, not a point), checks for floor under each candidate and
    /// scores them. Deliberately simple: convincing avoidance of walls and furniture, not pathfinding.
    /// Also owns the goose's physical body: a kinematic trigger capsule that nothing collides with, used to
    /// push the goose out of geometry it ends up inside (a LiDAR chunk of the chair it landed in arriving late).
    /// </summary>
    public class GooseObstacleAvoidance : MonoBehaviour
    {
        [Header("Body")]
        [Tooltip("Approximate goose body radius in meters (0.25 - 0.4).")]
        public float bodyRadius = 0.32f;
        [Tooltip("Top of the body capsule above the feet (m). The goose is 1.05 m tall: the head has to clear a seat or a table top too.")]
        public float bodyHeight = 0.95f;
        [Tooltip("Bottom of the body capsule above the feet (m): LiDAR floor noise below this never counts as an overlap.")]
        public float bodyBottom = 0.1f;

        [Header("Overlap recovery")]
        [Tooltip("Furthest the body is nudged out of geometry per pass (m). Deeper overlaps move to the nearest free spot instead.")]
        public float maxPushPerPass = 0.3f;
        [Tooltip("Vertical intrusions up to this deep are LiDAR floor noise and ignored (m); deeper ones (a seat through the body) relocate.")]
        public float floorNoiseTolerance = 0.12f;

        [Header("Probing")]
        public float lookAhead = 1.0f;
        [Tooltip("If the best direction has less clearance than this the goose stops.")]
        public float emergencyStopDistance = 0.28f;
        public float floorProbeHeight = 0.5f;
        public float floorDropTolerance = 0.4f;
        public LayerMask environmentMask;

        [Header("Scoring")]
        public float clearWeight = 3f;
        public float deviationWeight = 1.5f;
        public float headingWeight = 0.6f;
        public float floorWeight = 0.8f;
        public float progressWeight = 1.2f;

        /// <summary>True when any environment colliders exist. Without them steering is conservative.</summary>
        public bool HasEnvironmentData { get; set; }
        public float FloorY { get; set; }

        public bool LastDirectBlocked { get; private set; }
        public float LastChosenAngle { get; private set; }
        public float LastClearance { get; private set; }
        /// <summary>Times the body was nudged out of geometry / moved to a free spot (diagnostics, smoke test).</summary>
        public int OverlapPushCount { get; private set; }
        public int RelocationCount { get; private set; }
        public Collider BodyCollider => m_Body;

        static readonly float[] k_Angles = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f };
        static readonly float[] k_RelocationRadii = { 0.35f, 0.6f, 0.9f, 1.25f, 1.7f, 2.2f };
        static readonly Collider[] s_Overlaps = new Collider[8];
        const int k_BodyLayer = 2; // Ignore Raycast: no query in the game looks at this layer

        CapsuleCollider m_Body;
        float m_BodyRadiusSynced = -1f, m_BodyHeightSynced = -1f, m_BodyBottomSynced = -1f;
        int m_StuckPasses;
        bool m_LayerPairIgnored;

        public enum OverlapResult { Clear, Pushed, Relocated, Stuck }

        void Start()
        {
            EnsureBody();
        }

        /// <summary>
        /// The body: a kinematic trigger capsule on the Ignore Raycast layer. It never collides with anything (contacts with
        /// the environment layer are switched off, every environment query ignores triggers); it exists so that
        /// Physics.ComputePenetration can tell how far the goose is inside a scanned chair or wall.
        /// </summary>
        void EnsureBody()
        {
            if (m_Body != null) return;
            gameObject.layer = k_BodyLayer;
            if (!TryGetComponent(out Rigidbody rb)) rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            if (!TryGetComponent(out m_Body)) m_Body = gameObject.AddComponent<CapsuleCollider>();
            m_Body.isTrigger = true;
            m_Body.direction = 1;
            m_BodyRadiusSynced = -1f;
            SyncBodyIfChanged();
        }

        void SyncBodyIfChanged()
        {
            if (m_Body == null) return;
            if (!m_LayerPairIgnored && environmentMask.value != 0)
            {
                for (int layer = 0; layer < 32; layer++)
                    if ((environmentMask.value & (1 << layer)) != 0) Physics.IgnoreLayerCollision(k_BodyLayer, layer, true);
                m_LayerPairIgnored = true;
            }
            if (bodyRadius == m_BodyRadiusSynced && bodyHeight == m_BodyHeightSynced && bodyBottom == m_BodyBottomSynced) return;
            m_BodyRadiusSynced = bodyRadius; m_BodyHeightSynced = bodyHeight; m_BodyBottomSynced = bodyBottom;
            GetCapsule(Vector3.zero, out var p1, out var p2, out var r);
            m_Body.radius = r;
            m_Body.height = (p2.y - p1.y) + 2f * r;
            m_Body.center = new Vector3(0f, (p1.y + p2.y) * 0.5f, 0f);
        }

        public struct Decision
        {
            public Vector3 direction;
            public float speedScale;
            public bool blocked;
            public bool noEscape;
        }

        public Decision Choose(Vector3 pos, Vector3 toPlayer, Vector3 heading, float distToPlayer, bool widerSearch)
        {
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 1e-4f) toPlayer = heading;
            toPlayer.Normalize();
            heading.y = 0f;
            if (heading.sqrMagnitude < 1e-4f) heading = toPlayer;
            heading.Normalize();

            float probe = Mathf.Clamp(distToPlayer - 0.15f, 0.35f, lookAhead);
            int count = widerSearch ? k_Angles.Length : 7;

            float bestScore = float.MinValue;
            Vector3 bestDir = toPlayer;
            float bestClearance = 0f;
            bool bestClear = false;
            float bestAngle = 0f;

            for (int i = 0; i < count; i++)
            {
                float angle = k_Angles[i];
                Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * toPlayer;
                float clearance = Clearance(pos, dir, probe);
                bool clear = clearance >= probe - 0.001f;
                if (i == 0) LastDirectBlocked = !clear;

                float floorScore = FloorOk(pos + dir * Mathf.Min(0.5f, probe)) ? 1f : 0f;
                float score = 0f;
                score += clear ? clearWeight : clearWeight * Mathf.Clamp01(clearance / probe) * 0.5f;
                score += deviationWeight * (1f - Mathf.Abs(angle) / 180f);
                score += headingWeight * Vector3.Dot(dir, heading);
                score += floorWeight * floorScore;
                score += progressWeight * Mathf.Clamp01(Vector3.Dot(dir, toPlayer));
                if (!clear && clearance < emergencyStopDistance) score -= 2.5f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDir = dir;
                    bestClearance = clearance;
                    bestClear = clear;
                    bestAngle = angle;
                }
            }

            LastChosenAngle = bestAngle;
            LastClearance = bestClearance;

            var d = new Decision { direction = bestDir, blocked = !bestClear };
            if (bestClear)
            {
                d.speedScale = 1f;
            }
            else
            {
                float usable = Mathf.Clamp01((bestClearance - emergencyStopDistance) / Mathf.Max(0.01f, probe - emergencyStopDistance));
                d.speedScale = usable * 0.6f;
                d.noEscape = bestClearance < emergencyStopDistance;
            }
            if (!HasEnvironmentData) d.speedScale *= 0.85f; // nothing mapped yet: be careful
            return d;
        }

        /// <summary>Distance the goose body can travel along dir before hitting environment geometry.</summary>
        public float Clearance(Vector3 pos, Vector3 dir, float distance)
        {
            if (environmentMask.value == 0) return distance;
            GetCapsule(pos, out var p1, out var p2, out var r);
            if (Physics.CapsuleCast(p1, p2, r, dir, out var hit, distance, environmentMask, QueryTriggerInteraction.Ignore))
                return hit.distance;
            return distance;
        }

        public bool FloorOk(Vector3 p)
        {
            if (!HasEnvironmentData || environmentMask.value == 0) return true;
            return Physics.Raycast(new Vector3(p.x, FloorY + floorProbeHeight, p.z), Vector3.down, floorProbeHeight + floorDropTolerance, environmentMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>The whole body (feet to head) is clear of environment geometry at this floor position.</summary>
        public bool IsPositionFree(Vector3 pos)
        {
            if (environmentMask.value == 0) return true;
            GetCapsule(pos, out var p1, out var p2, out var r);
            return !Physics.CheckCapsule(p1, p2, r, environmentMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>A place the goose can stand: something scanned under it and the whole body clear of geometry.</summary>
        public bool IsSpotFree(Vector3 pos) => FloorOk(pos) && IsPositionFree(pos);

        void GetCapsule(Vector3 pos, out Vector3 p1, out Vector3 p2, out float radius)
        {
            radius = bodyRadius * 0.9f;
            float bottom = pos.y + Mathf.Max(0.02f, bodyBottom);
            float top = pos.y + Mathf.Max(bodyHeight, bodyBottom + 2f * radius + 0.02f);
            p1 = new Vector3(pos.x, bottom + radius, pos.z);
            p2 = new Vector3(pos.x, top - radius, pos.z);
        }

        /// <summary>
        /// Find a free spawn point near the desired spot (behind the player), rotating around the player if blocked.
        /// Spots with scanned floor under them come first; if the scan is thin it settles for a spot that is merely
        /// clear (the overlap recovery sorts out whatever the LiDAR finds there later).
        /// </summary>
        public Vector3 FindFreeSpawn(Vector3 desired, Vector3 playerFlat, float distance, float floorY)
        {
            FloorY = floorY;
            Vector3 offset = desired - playerFlat;
            offset.y = 0f;
            if (offset.sqrMagnitude < 1e-4f) offset = Vector3.back * distance;
            float[] angles = { 0f, 25f, -25f, 50f, -50f, 75f, -75f, 100f, -100f, 130f, -130f, 180f };
            float[] scales = { 1f, 0.75f, 0.55f };
            for (int pass = 0; pass < 2; pass++)
            {
                bool needFloor = pass == 0;
                foreach (var scale in scales)
                {
                    foreach (var a in angles)
                    {
                        Vector3 p = playerFlat + Quaternion.AngleAxis(a, Vector3.up) * offset * scale;
                        p.y = floorY;
                        if (!IsPositionFree(p)) continue;
                        if (needFloor && !FloorOk(p)) continue;
                        Vector3 toPlayer = playerFlat - p;
                        float dist = toPlayer.magnitude;
                        if (dist < 0.01f) continue;
                        if (Clearance(p, toPlayer / dist, Mathf.Min(dist, 1.2f)) < Mathf.Min(dist, 1.2f) - 0.01f) continue;
                        return p;
                    }
                }
            }
            Debug.LogWarning("[GooseBrawl] No free spawn point found, using the default spot behind the player.");
            return new Vector3(desired.x, floorY, desired.z);
        }

        // ------------------------------------------------------------------ overlap recovery

        /// <summary>
        /// Keep a standing goose out of geometry that overlaps its body: a LiDAR chunk that arrived after it landed, a hop
        /// that ended against a couch. Sideways nudges only (capped per pass); when the shortest way out is vertical (a seat
        /// or a table top through the body) or too deep, or a corner keeps pushing both ways, it moves to the nearest free
        /// spot instead. One capsule overlap query per pass when clear, so it is cheap enough to run at 20 Hz.
        /// </summary>
        public OverlapResult ResolveOverlap(ref Vector3 pos, Vector3 playerFlat, float minPlayerDistance)
        {
            if (environmentMask.value == 0) return OverlapResult.Clear;
            if (m_Body == null) EnsureBody();
            SyncBodyIfChanged();

            var result = OverlapResult.Clear;
            bool relocate = false;
            for (int pass = 0; pass < 3; pass++)
            {
                if (!Penetration(pos, out var push, out bool vertical)) { m_StuckPasses = 0; return result; }
                if (vertical || push.magnitude > maxPushPerPass * 2f) { relocate = true; break; }
                pos += Vector3.ClampMagnitude(push, maxPushPerPass);
                OverlapPushCount++;
                result = OverlapResult.Pushed;
            }
            if (!relocate)
            {
                // Still touching after the nudges: a couple more passes of that (a corner pushing both ways) and it relocates.
                if (Penetration(pos, out _, out _) == false) { m_StuckPasses = 0; return result; }
                if (++m_StuckPasses < 6) return result;
            }
            m_StuckPasses = 0;
            if (TryFindNearestFreeSpot(pos, playerFlat, minPlayerDistance, out var spot))
            {
                pos = spot;
                RelocationCount++;
                return OverlapResult.Relocated;
            }
            return OverlapResult.Stuck;
        }

        /// <summary>Sum of the sideways separations the overlapping environment colliders ask for. False when clear.</summary>
        bool Penetration(Vector3 pos, out Vector3 push, out bool vertical)
        {
            push = Vector3.zero;
            vertical = false;
            GetCapsule(pos, out var p1, out var p2, out var r);
            int n = Physics.OverlapCapsuleNonAlloc(p1, p2, r, s_Overlaps, environmentMask, QueryTriggerInteraction.Ignore);
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                var c = s_Overlaps[i];
                s_Overlaps[i] = null;
                if (c == null || c == m_Body) continue;
                var ct = c.transform;
                if (!Physics.ComputePenetration(m_Body, pos, Quaternion.identity, c, ct.position, ct.rotation, out var dir, out var dist) || dist <= 1e-4f) continue;
                Vector3 flat = new Vector3(dir.x, 0f, dir.z);
                float flatLen = flat.magnitude;
                if (flatLen < 0.5f)
                {
                    // Mostly vertical: a bump in the LiDAR floor is ignored, a seat through the body is not.
                    if (dist <= floorNoiseTolerance) continue;
                    vertical = true;
                    any = true;
                    continue;
                }
                // Sideways distance that achieves the same separation along the contact direction.
                push += flat / flatLen * (dist / flatLen);
                any = true;
            }
            return any;
        }

        /// <summary>
        /// Nearest standing spot around pos (free body, scanned floor, outside the phone's bubble), searched on growing
        /// rings starting on the side away from the player so it steps back rather than into the phone.
        /// </summary>
        public bool TryFindNearestFreeSpot(Vector3 pos, Vector3 playerFlat, float minPlayerDistance, out Vector3 spot)
        {
            Vector3 away = pos - playerFlat; away.y = 0f;
            float startAngle = away.sqrMagnitude > 1e-4f ? Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg : 0f;
            float minSq = minPlayerDistance * minPlayerDistance;
            foreach (float radius in k_RelocationRadii)
            {
                int steps = radius < 0.7f ? 8 : 12;
                float step = 360f / steps;
                for (int i = 0; i < steps; i++)
                {
                    float a = startAngle + ((i + 1) / 2) * step * (i % 2 == 0 ? 1f : -1f);
                    Vector3 p = pos + Quaternion.AngleAxis(a, Vector3.up) * Vector3.forward * radius;
                    p.y = FloorY;
                    Vector3 toPlayer = p - playerFlat; toPlayer.y = 0f;
                    if (toPlayer.sqrMagnitude < minSq) continue;
                    if (!IsSpotFree(p)) continue;
                    spot = p;
                    return true;
                }
            }
            spot = pos;
            return false;
        }
    }
}
