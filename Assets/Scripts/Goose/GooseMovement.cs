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
        public float acceleration = 5f;
        public float deceleration = 9f;

        [Header("Floor")]
        [Tooltip("Max height difference from the nest floor the goose may follow (keeps it off tables).")]
        public float floorSnapTolerance = 0.25f;
        public LayerMask environmentMask;

        [Header("Player space")]
        [Tooltip("The goose body never comes closer than this to the camera, so you can never see inside it.")]
        public float minCameraDistance = 0.6f;

        [Header("Stuck detection")]
        public float stuckCheckInterval = 1f;
        public float stuckDistance = 0.18f;

        public float FloorY { get; set; }
        /// <summary>While true another system (the lunge) drives the transform.</summary>
        public bool ExternalControl { get; set; }
        public float CurrentSpeed { get; private set; }
        public float DesiredSpeed { get; private set; }
        public Vector3 DesiredDir { get; private set; } = Vector3.forward;
        public Vector3 Heading { get; private set; } = Vector3.forward;
        public bool Stuck { get; private set; }

        Vector3 m_StuckAnchor;
        float m_StuckTimer;

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
