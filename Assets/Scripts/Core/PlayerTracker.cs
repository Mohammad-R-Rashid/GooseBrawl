using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Where is the player? In AR the player *is* the phone, so we track the main camera.
    /// The goose only ever cares about the horizontal (XZ) position; the floor height is fixed
    /// when the nest is placed so the goose never tries to reach the phone's altitude.
    /// </summary>
    public class PlayerTracker : MonoBehaviour
    {
        [Tooltip("World-space floor height. Set when the nest is placed.")]
        public float FloorY;

        [Tooltip("Set by the game manager every frame from the AR session / mock.")]
        public bool TrackingGood = true;

        Camera m_Camera;
        Vector3 m_LastFlatForward = Vector3.forward;

        public Camera Cam
        {
            get
            {
                if (m_Camera == null || !m_Camera.isActiveAndEnabled)
                    m_Camera = Camera.main;
                return m_Camera;
            }
        }

        public bool HasCamera => Cam != null;

        /// <summary>Raw camera position (includes the phone's height).</summary>
        public Vector3 Position => Cam != null ? Cam.transform.position : Vector3.zero;

        /// <summary>Camera X/Z projected down onto the floor. This is the goose's chase target.</summary>
        public Vector3 FlatPosition
        {
            get
            {
                var p = Position;
                return new Vector3(p.x, FloorY, p.z);
            }
        }

        /// <summary>Camera forward flattened onto the XZ plane. Falls back gracefully when looking straight down.</summary>
        public Vector3 FlatForward
        {
            get
            {
                var c = Cam;
                if (c == null) return m_LastFlatForward;
                var f = c.transform.forward;
                f.y = 0f;
                if (f.sqrMagnitude < 0.01f)
                {
                    // Phone pointed at the floor/ceiling: use the top edge of the phone instead.
                    f = c.transform.up;
                    f.y = 0f;
                }
                if (f.sqrMagnitude < 0.0001f) return m_LastFlatForward;
                m_LastFlatForward = f.normalized;
                return m_LastFlatForward;
            }
        }

        public float FlatDistanceTo(Vector3 worldPos)
        {
            var p = Position;
            float dx = p.x - worldPos.x;
            float dz = p.z - worldPos.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Signed angle (degrees) from the camera's flat forward to a world point. 0 = straight ahead, +/-180 = behind.</summary>
        public float SignedAngleTo(Vector3 worldPos)
        {
            var to = worldPos - Position;
            to.y = 0f;
            if (to.sqrMagnitude < 1e-4f) return 0f;
            return Vector3.SignedAngle(FlatForward, to.normalized, Vector3.up);
        }

        /// <summary>Is the point inside the camera frustum (with an optional viewport margin)?</summary>
        public bool IsInView(Vector3 worldPos, float viewportMargin = 0f)
        {
            var c = Cam;
            if (c == null) return false;
            var vp = c.WorldToViewportPoint(worldPos);
            if (vp.z <= 0f) return false;
            return vp.x > -viewportMargin && vp.x < 1f + viewportMargin && vp.y > -viewportMargin && vp.y < 1f + viewportMargin;
        }
    }
}
