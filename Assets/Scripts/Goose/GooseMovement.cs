using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Moves the goose root through code (no root motion). Smooth turning, acceleration, floor
    /// snapping against the AREnvironment layer and simple stuck detection.
    /// </summary>
    public class GooseMovement : MonoBehaviour
    {
        [Header("Turning / speed")]
        public float turnSpeedDeg = 420f;
        public float acceleration = 8f;
        public float deceleration = 10f;

        [Header("Floor")]
        [Tooltip("Max height difference from the nest floor the goose may follow (keeps it off tables).")]
        public float floorSnapTolerance = 0.25f;
        public LayerMask environmentMask;

        [Header("Player space")]
        [Tooltip("The goose body never comes closer than this to the camera (root to lens, flat), so you can never see inside it. Must stay below the chase controller's catchDistance.")]
        public float minCameraDistance = 0.75f;

        [Header("Body")]
        [Tooltip("How often a standing goose checks that its body is clear of scanned geometry (Hz). One overlap query per check when clear.")]
        public float overlapCheckRate = 20f;

        [Header("Stuck detection")]
        public float stuckCheckInterval = 1f;
        public float stuckDistance = 0.18f;

        public float FloorY { get; set; }
        /// <summary>While true another system (the lunge) drives the transform.</summary>
        public bool ExternalControl { get; set; }
        /// <summary>Set by the fly-in and the hops while the goose is off the floor: no overlap recovery in the air.</summary>
        public bool Airborne { get; set; }
        /// <summary>The body was moved to a free spot because geometry (a late LiDAR chunk) had it inside (from, to).</summary>
        public event System.Action<Vector3, Vector3> Relocated;
        public GooseObstacleAvoidance.OverlapResult LastOverlap { get; private set; }
        public float CurrentSpeed { get; private set; }
        public float DesiredSpeed { get; private set; }
        public Vector3 DesiredDir { get; private set; } = Vector3.forward;
        public Vector3 Heading { get; private set; } = Vector3.forward;
        public bool Stuck { get; private set; }

        Vector3 m_StuckAnchor;
        float m_StuckTimer;
        GooseObstacleAvoidance m_Avoidance;
        float m_NextOverlapCheck;

        void Awake()
        {
            m_Avoidance = GetComponent<GooseObstacleAvoidance>();
        }

        public void SetDesired(Vector3 dir, float speed)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude > 1e-4f) DesiredDir = dir.normalized;
            DesiredSpeed = Mathf.Max(0f, speed);
        }

        public void Stop()
        {
            DesiredSpeed = 0f;
        }

        public void Teleport(Vector3 position, Vector3 facing)
        {
            transform.position = position;
            SetHeading(facing);
            DesiredDir = Heading;
            CurrentSpeed = 0f;
            ClearStuck();
        }

        public void SetHeading(Vector3 facing)
        {
            facing.y = 0f;
            if (facing.sqrMagnitude > 1e-4f)
            {
                Heading = facing.normalized;
                transform.rotation = Quaternion.LookRotation(Heading, Vector3.up);
            }
        }

        /// <summary>Rotate toward a direction without moving (used while crouching before a lunge).</summary>
        public void FaceTowards(Vector3 dir, float dt)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) return;
            var h = Vector3.RotateTowards(Heading, dir.normalized, turnSpeedDeg * Mathf.Deg2Rad * dt, 0f);
            h.y = 0f;
            if (h.sqrMagnitude < 1e-6f) h = dir;
            Heading = h.normalized;
            transform.rotation = Quaternion.LookRotation(Heading, Vector3.up);
        }

        public void ClearStuck()
        {
            Stuck = false;
            m_StuckTimer = 0f;
            m_StuckAnchor = transform.position;
        }

        void Update()
        {
            if (ExternalControl) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // Turn smoothly toward the desired direction.
            var h = Vector3.RotateTowards(Heading, DesiredDir, turnSpeedDeg * Mathf.Deg2Rad * dt, 0f);
            h.y = 0f;
            if (h.sqrMagnitude < 1e-6f) h = DesiredDir;
            Heading = h.normalized;
            transform.rotation = Quaternion.LookRotation(Heading, Vector3.up);

            // Slow down while turning hard so it never slides sideways into a wall.
            float alignment = Mathf.Clamp01(Vector3.Dot(Heading, DesiredDir));
            float target = DesiredSpeed * Mathf.Lerp(0.3f, 1f, alignment);
            CurrentSpeed = target > CurrentSpeed
                ? Mathf.MoveTowards(CurrentSpeed, target, acceleration * dt)
                : Mathf.MoveTowards(CurrentSpeed, target, deceleration * dt);

            Vector3 pos = transform.position + Heading * CurrentSpeed * dt;
            pos = KeepOutOfCamera(pos);
            pos.y = SampleFloor(pos);
            transform.position = pos;

            // Stuck detection: asked to move but barely moved for a second.
            if (DesiredSpeed > 0.2f)
            {
                m_StuckTimer += dt;
                if (m_StuckTimer >= stuckCheckInterval)
                {
                    float moved = Vector3.Distance(pos, m_StuckAnchor);
                    Stuck = moved < stuckDistance;
                    m_StuckAnchor = pos;
                    m_StuckTimer = 0f;
                }
            }
            else
            {
                m_StuckTimer = 0f;
                m_StuckAnchor = pos;
                Stuck = false;
            }
        }

        /// <summary>
        /// Every state in which the goose stands on the floor (chase, glare, stunned, results): keep the body out of
        /// geometry that overlaps it and out of the phone's bubble. Runs after the state logic and the hop coroutines
        /// have placed the goose for this frame, at overlapCheckRate while clear and every frame while inside something.
        /// </summary>
        void LateUpdate()
        {
            if (Airborne || m_Avoidance == null) return;
            bool inside = LastOverlap == GooseObstacleAvoidance.OverlapResult.Pushed || LastOverlap == GooseObstacleAvoidance.OverlapResult.Stuck;
            if (!inside && Time.time < m_NextOverlapCheck) return;
            m_NextOverlapCheck = Time.time + 1f / Mathf.Max(1f, overlapCheckRate);

            Vector3 before = transform.position;
            Vector3 pos = before;
            var cam = Camera.main;
            Vector3 playerFlat = cam != null ? new Vector3(cam.transform.position.x, pos.y, cam.transform.position.z) : pos + Vector3.forward * 99f;
            var result = m_Avoidance.ResolveOverlap(ref pos, playerFlat, minCameraDistance);
            LastOverlap = result;
            // Update() already keeps a self-driven goose out of the bubble every frame; here it covers the driven states.
            if (result != GooseObstacleAvoidance.OverlapResult.Clear || ExternalControl) pos = KeepOutOfCamera(pos);
            if ((pos - before).sqrMagnitude < 1e-8f) return;

            pos.y = SampleFloor(pos);
            transform.position = pos;
            if (result == GooseObstacleAvoidance.OverlapResult.Relocated)
            {
                ClearStuck();
                Relocated?.Invoke(before, pos);
            }
        }

        /// <summary>Push a position out of the camera's personal bubble (horizontal only).</summary>
        public Vector3 KeepOutOfCamera(Vector3 pos)
        {
            var cam = Camera.main;
            if (cam == null) return pos;
            Vector3 c = cam.transform.position;
            Vector3 d = pos - c;
            d.y = 0f;
            float dist = d.magnitude;
            if (dist >= minCameraDistance) return pos;
            Vector3 dir = dist > 1e-4f ? d / dist : -Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.back;
            return new Vector3(c.x + dir.x * minCameraDistance, pos.y, c.z + dir.z * minCameraDistance);
        }

        /// <summary>Floor height under a point: scanned geometry when it is close to the nest floor, else the nest floor.</summary>
        public float SampleFloor(Vector3 pos)
        {
            if (environmentMask.value != 0 &&
                Physics.Raycast(new Vector3(pos.x, FloorY + 0.6f, pos.z), Vector3.down, out var hit, 1.2f, environmentMask, QueryTriggerInteraction.Ignore))
            {
                if (Mathf.Abs(hit.point.y - FloorY) <= floorSnapTolerance) return hit.point.y;
            }
            return FloorY;
        }
    }
}
