using UnityEngine;
using UnityEngine.UI;

namespace GooseBrawl
{
    /// <summary>
    /// Screen-edge locator for the goose. When the goose is off-screen an arrow, a goose silhouette and the
    /// distance sit on the screen edge in its direction; when it is behind the phone the marker drops to the
    /// bottom edge and says BEHIND YOU. Pulses on every honk. Nothing is drawn while the goose is on screen.
    /// </summary>
    public class GooseLocator : MonoBehaviour
    {
        public Transform Target { get; set; }
        public float Danger { get; set; }
        public float edgeMargin = 96f;
        [Tooltip("Keeps the marker clear of the timer / stats at the top.")]
        public float topMargin = 300f;
        [Tooltip("Keeps the marker clear of the goose-o-meter at the bottom.")]
        public float bottomMargin = 330f;

        RectTransform m_Root, m_Marker, m_Arrow;
        Image m_ArrowImage, m_IconImage, m_Pill;
        UILabel m_Distance, m_Behind;
        RectTransform m_Safe;
        float m_Pulse;
        float m_Visible;

        public static GooseLocator Create(RectTransform safeArea)
        {
            var root = UIFactory.CreateRect(safeArea, "GooseLocator");
            UIFactory.Stretch(root);
            var loc = root.gameObject.AddComponent<GooseLocator>();
            loc.Build(root, safeArea);
            return loc;
        }

        void Build(RectTransform root, RectTransform safe)
        {
            m_Root = root;
            m_Safe = safe;
            m_Marker = UIFactory.CreateRect(root, "Marker");
            UIFactory.Place(m_Marker, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(150f, 150f));

            m_Pill = UIFactory.CreateGlassPill(m_Marker, "Pill", new Vector2(150f, 150f), strong: true);
            m_Pill.sprite = UIFactory.Circle;
            m_Pill.type = Image.Type.Simple;
            UIFactory.Place(m_Pill.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(150f, 150f));
            var edge = m_Pill.transform.Find("Edge");
            if (edge != null) edge.gameObject.SetActive(false);

            m_IconImage = UIFactory.CreateImage(m_Marker, "Icon", UIFactory.GooseIcon, UITheme.Ink);
            UIFactory.Place(m_IconImage.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 14f), new Vector2(84f, 84f));
            m_IconImage.preserveAspect = true;

            m_Distance = UIFactory.CreateLabel(m_Marker, "Distance", "3.2 m", 30f, UITheme.Ink, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(m_Distance.Rect, new Vector2(0.5f, 0.5f), new Vector2(0f, -40f), new Vector2(150f, 40f));

            m_Arrow = UIFactory.CreateRect(m_Marker, "ArrowPivot");
            UIFactory.Place(m_Arrow, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(10f, 10f));
            m_ArrowImage = UIFactory.CreateImage(m_Arrow, "Arrow", UIFactory.Triangle, UITheme.Ink);
            // Triangle points up by default; we rotate the pivot so "up" becomes the direction.
            UIFactory.Place(m_ArrowImage.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 96f), new Vector2(46f, 40f));

            m_Behind = UIFactory.CreateLabel(m_Marker, "Behind", "BEHIND YOU", 34f, UITheme.Ink, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(m_Behind.Rect, new Vector2(0.5f, 0.5f), new Vector2(0f, 108f), new Vector2(360f, 44f));
            UIFactory.AddShadow(m_Behind, new Color(0f, 0f, 0f, 0.6f), new Vector2(0f, -2f));
            m_Behind.SetActive(false);

            m_Marker.gameObject.SetActive(false);
        }

        public void Pulse() => m_Pulse = 1f;

        void Update()
        {
            var cam = Camera.main;
            float dt = Time.unscaledDeltaTime;
            m_Pulse = Mathf.MoveTowards(m_Pulse, 0f, dt * 3f);
            if (Target == null || cam == null || m_Safe == null)
            {
                Show(false);
                return;
            }

            Vector3 world = Target.position + Vector3.up * 0.5f;
            Vector3 vp = cam.WorldToViewportPoint(world);
            bool behind = vp.z < 0f;
            bool onScreen = !behind && vp.x > 0.04f && vp.x < 0.96f && vp.y > 0.06f && vp.y < 0.94f;
            if (onScreen)
            {
                Show(false);
                return;
            }
            Show(true);

            Vector2 size = m_Safe.rect.size;
            Vector2 dir;
            if (behind)
            {
                // Behind the camera: the projection flips; use it only for left/right, and point down.
                float side = Mathf.Clamp(-(vp.x - 0.5f) * 2f, -1f, 1f);
                dir = new Vector2(side * 0.35f, -1f).normalized;
            }
            else
            {
                dir = new Vector2((vp.x - 0.5f) * size.x, (vp.y - 0.5f) * size.y);
                if (dir.sqrMagnitude < 1e-3f) dir = Vector2.up;
                dir.Normalize();
            }

            // Slide the marker along dir until it touches the usable rect (HUD bands at the top and bottom excluded).
            float right = size.x * 0.5f - edgeMargin, left = -right;
            float top = size.y * 0.5f - topMargin, bottom = -size.y * 0.5f + bottomMargin;
            float tx = dir.x > 1e-4f ? right / dir.x : (dir.x < -1e-4f ? left / dir.x : float.MaxValue);
            float ty = dir.y > 1e-4f ? top / dir.y : (dir.y < -1e-4f ? bottom / dir.y : float.MaxValue);
            float t = Mathf.Min(tx, ty);
            Vector2 pos = dir * t;
            if (behind) pos.y = bottom; // sit just above the goose-o-meter
            m_Marker.anchoredPosition = Vector2.Lerp(m_Marker.anchoredPosition, pos, 1f - Mathf.Exp(-dt * 14f));

            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
            m_Arrow.localRotation = Quaternion.Euler(0f, 0f, angle);

            var mgr = GooseGameManager.Instance;
            float dist = mgr != null ? mgr.Player.FlatDistanceTo(Target.position) : 0f;
            m_Distance.Text = dist.ToString("0.0") + " m";
            m_Behind.SetActive(behind);

            Color c = Color.Lerp(UITheme.Ink, UITheme.Danger, Mathf.Clamp01((Danger - 0.35f) / 0.5f));
            m_IconImage.color = c;
            m_ArrowImage.color = c;
            m_Distance.Color = c;
            m_Behind.Color = c;

            float s = 1f + 0.22f * Mathf.Sin(m_Pulse * Mathf.PI) + (behind ? 0.06f * Mathf.Sin(Time.unscaledTime * 6f) : 0f);
            m_Marker.localScale = Vector3.one * s;
        }

        void Show(bool on)
        {
            m_Visible = Mathf.MoveTowards(m_Visible, on ? 1f : 0f, Time.unscaledDeltaTime * 8f);
            bool active = m_Visible > 0.01f;
            if (m_Marker.gameObject.activeSelf != active) m_Marker.gameObject.SetActive(active);
            if (!active) return;
            var pc = m_Pill.color; pc.a = UITheme.GlassStrong.a * m_Visible; m_Pill.color = pc;
        }
    }
}
