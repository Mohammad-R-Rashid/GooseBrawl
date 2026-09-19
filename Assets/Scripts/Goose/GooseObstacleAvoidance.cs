using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Local steering against the scanned environment. Samples a fan of horizontal directions with
    /// capsule casts (the goose body, not a point), checks for floor under each candidate and
    /// scores them. Deliberately simple: convincing avoidance of walls and furniture, not pathfinding.
    /// </summary>
    public class GooseObstacleAvoidance : MonoBehaviour
    {
        [Header("Body")]
        [Tooltip("Approximate goose body radius in meters (0.25 - 0.4).")]
        public float bodyRadius = 0.32f;
        public float bodyHeight = 0.7f;

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

        static readonly float[] k_Angles = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f };

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

        public bool IsPositionFree(Vector3 pos)
        {
            if (environmentMask.value == 0) return true;
            GetCapsule(pos, out var p1, out var p2, out var r);
            return !Physics.CheckCapsule(p1, p2, r, environmentMask, QueryTriggerInteraction.Ignore);
        }

        void GetCapsule(Vector3 pos, out Vector3 p1, out Vector3 p2, out float radius)
        {
            radius = bodyRadius * 0.9f;
            p1 = pos + Vector3.up * (bodyRadius + 0.06f);
            p2 = pos + Vector3.up * Mathf.Max(bodyHeight - bodyRadius, bodyRadius + 0.08f);
        }

        /// <summary>Find a free spawn point near the desired spot (behind the player), rotating around the player if blocked.</summary>
        public Vector3 FindFreeSpawn(Vector3 desired, Vector3 playerFlat, float distance, float floorY)
        {
            FloorY = floorY;
            Vector3 offset = desired - playerFlat;
            offset.y = 0f;
            if (offset.sqrMagnitude < 1e-4f) offset = Vector3.back * distance;
            float[] angles = { 0f, 25f, -25f, 50f, -50f, 75f, -75f, 100f, -100f, 130f, -130f, 180f };
            float[] scales = { 1f, 0.75f, 0.55f };
            foreach (var scale in scales)
            {
                foreach (var a in angles)
                {
                    Vector3 p = playerFlat + Quaternion.AngleAxis(a, Vector3.up) * offset * scale;
                    p.y = floorY;
                    if (!IsPositionFree(p)) continue;
                    Vector3 toPlayer = playerFlat - p;
                    float dist = toPlayer.magnitude;
                    if (dist < 0.01f) continue;
                    if (Clearance(p, toPlayer / dist, Mathf.Min(dist, 1.2f)) < Mathf.Min(dist, 1.2f) - 0.01f) continue;
                    return p;
                }
            }
            Debug.LogWarning("[GooseBrawl] No free spawn point found, using the default spot behind the player.");
            return new Vector3(desired.x, floorY, desired.z);
        }
    }
}
