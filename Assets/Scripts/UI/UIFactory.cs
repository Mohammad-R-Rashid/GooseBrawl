using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GooseBrawl
{
    /// <summary>Palette: warm egg-and-goose brand for the title and results, dark glass for everything drawn over the camera.</summary>
    public static class UITheme
    {
        public static readonly Color Cream = new Color(1f, 0.965f, 0.87f, 1f);
        public static readonly Color CreamPanel = new Color(1f, 0.965f, 0.87f, 0.93f);
        public static readonly Color Yolk = new Color(1f, 0.72f, 0.15f, 1f);
        public static readonly Color Brown = new Color(0.30f, 0.18f, 0.07f, 1f);
        public static readonly Color Muted = new Color(0.30f, 0.18f, 0.07f, 0.6f);
        public static readonly Color Danger = new Color(0.98f, 0.25f, 0.18f, 1f);
        public static readonly Color White = Color.white;
        public static readonly Color Dim = new Color(0.16f, 0.09f, 0.03f, 0.62f);
        // Dark glass (in-chase HUD)
        public static readonly Color Glass = new Color(0.05f, 0.04f, 0.03f, 0.58f);
        public static readonly Color GlassStrong = new Color(0.05f, 0.04f, 0.03f, 0.78f);
        public static readonly Color GlassLine = new Color(1f, 1f, 1f, 0.14f);
        public static readonly Color Ink = new Color(0.97f, 0.95f, 0.9f, 1f);
        public static readonly Color InkMuted = new Color(0.97f, 0.95f, 0.9f, 0.62f);
    }

    public enum UIFont { Display, Hud }

    /// <summary>Wrapper so the UI works with TextMeshPro when its resources are imported and legacy Text otherwise.</summary>
    public class UILabel
    {
        public RectTransform Rect;
        public TMP_Text Tmp;
        public Text Legacy;

        public string Text
        {
            get => Tmp != null ? Tmp.text : (Legacy != null ? Legacy.text : string.Empty);
            set
            {
                if (Tmp != null) Tmp.text = value;
                else if (Legacy != null) Legacy.text = value;
            }
        }

        public Color Color
        {
            get => Tmp != null ? Tmp.color : (Legacy != null ? Legacy.color : Color.white);
            set
            {
                if (Tmp != null) Tmp.color = value;
                else if (Legacy != null) Legacy.color = value;
            }
        }

        public float Alpha
        {
            get => Color.a;
            set { var c = Color; c.a = value; Color = c; }
        }

        public float FontSize
        {
            set
            {
                if (Tmp != null) { Tmp.fontSize = value; if (Tmp.enableAutoSizing) { Tmp.fontSizeMax = value; Tmp.fontSizeMin = value * 0.55f; } }
                else if (Legacy != null) Legacy.fontSize = Mathf.RoundToInt(value);
            }
        }

        public GameObject GameObject => Rect != null ? Rect.gameObject : null;
        public void SetActive(bool active) { if (Rect != null) Rect.gameObject.SetActive(active); }
    }

    /// <summary>Scales a button's holder down while pressed and plays the tap sound + haptic.</summary>
    public class UIButtonPress : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public RectTransform target;
        public float pressedScale = 0.96f;
        bool m_Down;

        public void OnPointerDown(PointerEventData e)
        {
            m_Down = true;
            if (target != null) target.localScale = Vector3.one * pressedScale;
            var mgr = GooseGameManager.Instance;
            if (mgr != null)
            {
                if (mgr.Audio != null) mgr.Audio.PlayButtonTap();
                if (mgr.Haptics != null) mgr.Haptics.Transient(0.35f, 0.5f);
            }
        }

        public void OnPointerUp(PointerEventData e) => Release();
        public void OnPointerExit(PointerEventData e) => Release();

        void Release()
        {
            if (!m_Down) return;
            m_Down = false;
            if (target != null) target.localScale = Vector3.one;
        }

        void OnDisable()
        {
            m_Down = false;
            if (target != null) target.localScale = Vector3.one;
        }
    }

    /// <summary>Builds the whole UI from code: pills, glass, egg shapes, big outlined labels, chunky buttons, two OS fonts.</summary>
    public static class UIFactory
    {
        static Sprite s_Rounded, s_Pill, s_Circle, s_Vignette, s_Triangle, s_Egg, s_GooseIcon;
        static Font s_LegacyFont, s_OSFont;
        static TMP_FontAsset s_DisplayFont, s_HudFont;
        static bool? s_UseTmp;
        static bool s_FontsResolved;

        // (family, style) pairs that exist on iOS and macOS. Display = playful, Hud = clean condensed.
        static readonly string[][] k_DisplayCandidates =
        {
            new[] { "Marker Felt", "Wide" }, new[] { "Marker Felt", "Thin" },
            new[] { "Chalkboard SE", "Bold" }, new[] { "Chalkboard SE", "Regular" },
            new[] { "Arial Rounded MT Bold", "Regular" },
            new[] { "Avenir Next Condensed", "Heavy" }, new[] { "Avenir Next Condensed", "Bold" },
            new[] { "Helvetica Neue", "Bold" }
        };
        static readonly string[][] k_HudCandidates =
        {
            new[] { "Avenir Next Condensed", "Heavy" }, new[] { "Avenir Next Condensed", "Bold" }, new[] { "Avenir Next Condensed", "DemiBold" },
            new[] { "Avenir Next", "Heavy" }, new[] { "Avenir Next", "Bold" },
            new[] { "Helvetica Neue", "Bold" }, new[] { "Helvetica Neue", "Medium" }, new[] { "Helvetica", "Bold" }
        };

        public static bool UseTMP
        {
            get
            {
                if (s_UseTmp.HasValue) return s_UseTmp.Value;
                try
                {
                    var inst = TMP_Settings.instance;
                    s_UseTmp = inst != null && TMP_Settings.defaultFontAsset != null;
                }
                catch { s_UseTmp = false; }
                if (!s_UseTmp.Value) GooseLog.Warn("TextMeshPro resources not imported; using legacy UI Text.");
                return s_UseTmp.Value;
            }
        }

        static void ResolveFonts()
        {
            if (s_FontsResolved) return;
            s_FontsResolved = true;
            s_LegacyFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            HashSet<string> installed = null;
            try { installed = new HashSet<string>(Font.GetOSInstalledFontNames()); }
            catch (Exception e) { GooseLog.Warn("OS font lookup failed: " + e.Message); }
            if (installed == null) return;

            s_DisplayFont = CreateTmpFont(installed, k_DisplayCandidates, out var displayFamily);
            s_HudFont = CreateTmpFont(installed, k_HudCandidates, out _);
            if (s_HudFont == null) s_HudFont = s_DisplayFont;
            if (s_OSFont == null && displayFamily != null)
            {
                try { s_OSFont = Font.CreateDynamicFontFromOSFont(displayFamily, 96); } catch { s_OSFont = null; }
            }
            if (UseTMP && s_DisplayFont == null) GooseLog.Info("No OS font could be loaded by TextMeshPro; using the default TMP font.");
        }

        static TMP_FontAsset CreateTmpFont(HashSet<string> installed, string[][] candidates, out string familyUsed)
        {
            familyUsed = null;
            if (!UseTMP) { foreach (var c in candidates) if (installed.Contains(c[0])) { familyUsed = c[0]; break; } return null; }
            foreach (var candidate in candidates)
            {
                string family = candidate[0], style = candidate[1];
                if (!installed.Contains(family)) continue;
                try
                {
                    // Family-name creation goes through the font engine, which can read OS fonts (unlike Font objects).
                    var f = TMP_FontAsset.CreateFontAsset(family, style, 90);
                    if (f != null)
                    {
                        f.name = "GooseUIFont (" + family + " " + style + ")";
                        familyUsed = family;
                        GooseLog.Info("UI font: " + family + " " + style);
                        return f;
                    }
                }
                catch (Exception e)
                {
                    GooseLog.Warn("TMP font creation failed for " + family + ": " + e.Message);
                }
            }
            return null;
        }

        public static Sprite Rounded => s_Rounded != null ? s_Rounded : (s_Rounded = MakeRounded(64, 20));
        public static Sprite Pill => s_Pill != null ? s_Pill : (s_Pill = MakeRounded(64, 31));
        public static Sprite Circle => s_Circle != null ? s_Circle : (s_Circle = MakeCircle(64));
        public static Sprite Vignette => s_Vignette != null ? s_Vignette : (s_Vignette = MakeVignette(128));
        public static Sprite Triangle => s_Triangle != null ? s_Triangle : (s_Triangle = MakeTriangle(64));
        public static Sprite Egg => s_Egg != null ? s_Egg : (s_Egg = MakeEgg(384, 480));
        public static Sprite GooseIcon => s_GooseIcon != null ? s_GooseIcon : (s_GooseIcon = ProceduralAssets.GooseIconSprite());

        // ------------------------------------------------------------------ sprites
        static Sprite MakeRounded(int size, int radius)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Rounded" + radius, filterMode = FilterMode.Bilinear };
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
                    float cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(radius - d + 0.5f));
                }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        }

        static Sprite MakeCircle(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Circle" };
            var px = new Color[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(half, half));
                    px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(half - d + 0.5f));
                }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        static Sprite MakeEgg(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "Egg" };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float nx = (x + 0.5f) / w * 2f - 1f;
                    float ny = (y + 0.5f) / h * 2f - 1f;
                    // Egg: an ellipse that is narrower toward the top.
                    float widen = 1f - 0.22f * Mathf.Clamp01(ny);
                    float d = (nx / widen) * (nx / widen) + ny * ny;
                    float a = Mathf.Clamp01((1f - d) * w * 0.4f);
                    px[y * w + x] = new Color(1f, 1f, 1f, a);
                }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
        }

        static Sprite MakeVignette(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Vignette", wrapMode = TextureWrapMode.Clamp };
            var px = new Color[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01((d - 0.45f) / 0.65f);
                    px[y * size + x] = new Color(1f, 1f, 1f, a * a);
                }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        static Sprite MakeTriangle(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Triangle" };
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float fy = y / (float)(size - 1);
                    float halfWidth = (1f - fy) * 0.5f;
                    float fx = x / (float)(size - 1) - 0.5f;
                    float a = Mathf.Clamp01((halfWidth - Mathf.Abs(fx)) * size);
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        // ------------------------------------------------------------------ building blocks
        public static Canvas CreateCanvas(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        public static RectTransform CreateRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        public static RectTransform Stretch(RectTransform rt, float margin = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(margin, margin);
            rt.offsetMax = new Vector2(-margin, -margin);
            return rt;
        }

        public static RectTransform Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        public static Image CreateImage(Transform parent, string name, Sprite sprite, Color color, bool raycast = false)
        {
            var rt = CreateRect(parent, name);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = raycast;
            if (sprite != null && sprite.border.sqrMagnitude > 0f) img.type = Image.Type.Sliced;
            return img;
        }

        /// <summary>Rounded rectangle panel.</summary>
        public static RectTransform CreatePanel(Transform parent, string name, Color color)
        {
            return CreateImage(parent, name, Rounded, color, true).rectTransform;
        }

        /// <summary>Fully rounded pill of the given height.</summary>
        public static Image CreatePill(Transform parent, string name, Color color, Vector2 size, bool raycast = true)
        {
            var img = CreateImage(parent, name, Pill, color, raycast);
            img.rectTransform.sizeDelta = size;
            img.pixelsPerUnitMultiplier = Mathf.Max(0.05f, 62f / Mathf.Max(1f, size.y));
            return img;
        }

        /// <summary>Dark glass pill: near-black translucent body (no highlight line; it read as a stray bar on the phone).</summary>
        public static Image CreateGlassPill(Transform parent, string name, Vector2 size, bool strong = false, bool raycast = false)
        {
            return CreatePill(parent, name, strong ? UITheme.GlassStrong : UITheme.Glass, size, raycast);
        }

        /// <summary>Glass rounded card (for pause / coaching).</summary>
        public static RectTransform CreateGlassCard(Transform parent, string name, Vector2 size)
        {
            var img = CreateImage(parent, name, Rounded, UITheme.GlassStrong, true);
            img.rectTransform.sizeDelta = size;
            img.pixelsPerUnitMultiplier = 0.55f;
            return img.rectTransform;
        }

        public static UILabel CreateLabel(Transform parent, string name, string text, float size, Color color,
            bool bold = true, TextAnchor anchor = TextAnchor.MiddleCenter, Color outline = default, float outlineWidth = 0f,
            UIFont font = UIFont.Display, bool autoSize = false)
        {
            ResolveFonts();
            var rt = CreateRect(parent, name);
            var label = new UILabel { Rect = rt };
            if (UseTMP)
            {
                var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
                var asset = font == UIFont.Hud ? s_HudFont : s_DisplayFont;
                if (asset != null) tmp.font = asset;
                tmp.text = text;
                tmp.fontSize = size;
                tmp.color = color;
                tmp.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
                tmp.alignment = ToTmpAlignment(anchor);
                tmp.raycastTarget = false;
                tmp.textWrappingMode = TextWrappingModes.Normal;
                tmp.overflowMode = autoSize ? TextOverflowModes.Truncate : TextOverflowModes.Overflow;
                if (autoSize)
                {
                    tmp.enableAutoSizing = true;
                    tmp.fontSizeMax = size;
                    tmp.fontSizeMin = size * 0.55f;
                }
                if (outlineWidth > 0f)
                {
                    tmp.outlineWidth = outlineWidth;
                    tmp.outlineColor = outline;
                }
                label.Tmp = tmp;
            }
            else
            {
                var txt = rt.gameObject.AddComponent<Text>();
                txt.font = s_OSFont != null ? s_OSFont : s_LegacyFont;
                txt.text = text;
                txt.fontSize = Mathf.RoundToInt(size);
                txt.color = color;
                txt.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
                txt.alignment = anchor;
                txt.raycastTarget = false;
                txt.horizontalOverflow = HorizontalWrapMode.Wrap;
                txt.verticalOverflow = VerticalWrapMode.Overflow;
                if (autoSize) { txt.resizeTextForBestFit = true; txt.resizeTextMaxSize = Mathf.RoundToInt(size); txt.resizeTextMinSize = Mathf.RoundToInt(size * 0.55f); }
                if (outlineWidth > 0f)
                {
                    var o = rt.gameObject.AddComponent<Outline>();
                    o.effectColor = outline;
                    o.effectDistance = new Vector2(3f, -3f);
                }
                label.Legacy = txt;
            }
            return label;
        }

        /// <summary>Soft drop shadow under a label (HUD numbers over the camera feed).</summary>
        public static void AddShadow(UILabel label, Color color, Vector2 distance)
        {
            if (label == null || label.Rect == null) return;
            var s = label.Rect.gameObject.AddComponent<Shadow>();
            s.effectColor = color;
            s.effectDistance = distance;
            s.useGraphicAlpha = true;
        }

        static TextAlignmentOptions ToTmpAlignment(TextAnchor anchor)
        {
            switch (anchor)
            {
                case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
                case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
                case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
                case TextAnchor.MiddleLeft: return TextAlignmentOptions.Left;
                case TextAnchor.MiddleRight: return TextAlignmentOptions.Right;
                case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
                case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
                case TextAnchor.LowerRight: return TextAlignmentOptions.BottomRight;
                default: return TextAlignmentOptions.Center;
            }
        }

        /// <summary>Chunky pill button: yolk background, brown text, darker brown shadow pill underneath. Scales on press, taps and buzzes.</summary>
        public static Button CreateButton(Transform parent, string name, string text, Vector2 size, float fontSize, out UILabel label)
        {
            var holder = CreateRect(parent, name);
            holder.sizeDelta = size;
            var shadow = CreatePill(holder, "Shadow", UITheme.Brown, size, false);
            Place(shadow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -10f), size);
            var face = CreatePill(holder, "Face", UITheme.Yolk, size, true);
            Place(face.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            var button = face.gameObject.AddComponent<Button>();
            button.targetGraphic = face;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 1f, 1f, 1f);
            colors.pressedColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            colors.fadeDuration = 0.05f;
            button.colors = colors;
            var press = face.gameObject.AddComponent<UIButtonPress>();
            press.target = holder;
            label = CreateLabel(face.transform, "Label", text, fontSize, UITheme.Brown);
            Stretch(label.Rect, 8f);
            return button;
        }

        /// <summary>Small glass button (secondary actions, toggles, pause).</summary>
        public static Button CreateGlassButton(Transform parent, string name, string text, Vector2 size, float fontSize, out UILabel label)
        {
            var holder = CreateRect(parent, name);
            holder.sizeDelta = size;
            var face = CreateGlassPill(holder, "Face", size, strong: true, raycast: true);
            Place(face.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            var button = face.gameObject.AddComponent<Button>();
            button.targetGraphic = face;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = Color.white;
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.fadeDuration = 0.05f;
            button.colors = colors;
            var press = face.gameObject.AddComponent<UIButtonPress>();
            press.target = holder;
            label = CreateLabel(face.transform, "Label", text, fontSize, UITheme.Ink, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            Stretch(label.Rect, 6f);
            return button;
        }
    }
}
