using UnityEngine;

namespace GooseBrawl
{
    /// <summary>Keeps a full-screen RectTransform inside the device safe area (notch / home indicator).</summary>
    [RequireComponent(typeof(RectTransform))]
    public class SafeAreaFitter : MonoBehaviour
    {
        RectTransform m_Rect;
        Rect m_LastSafeArea;
        Vector2Int m_LastScreen;

        void Awake()
        {
            m_Rect = GetComponent<RectTransform>();
            Apply();
        }

        void Update()
        {
            if (Screen.safeArea != m_LastSafeArea || Screen.width != m_LastScreen.x || Screen.height != m_LastScreen.y)
                Apply();
        }

        void Apply()
        {
            m_LastSafeArea = Screen.safeArea;
            m_LastScreen = new Vector2Int(Screen.width, Screen.height);
            if (Screen.width <= 0 || Screen.height <= 0) return;
            var sa = m_LastSafeArea;
            var min = sa.position;
            var max = sa.position + sa.size;
            min.x /= Screen.width;
            min.y /= Screen.height;
            max.x /= Screen.width;
            max.y /= Screen.height;
            m_Rect.anchorMin = min;
            m_Rect.anchorMax = max;
            m_Rect.offsetMin = Vector2.zero;
            m_Rect.offsetMax = Vector2.zero;
        }
    }
}
