using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace GooseBrawl
{
    /// <summary>
    /// Every screen of the game plus the overlay effects. Title and results keep the warm egg-and-goose brand
    /// (cream, yolk, brown); everything drawn over the live camera during play is dark glass with light type so
    /// the AR scene stays the star. Built entirely from code in Awake.
    /// </summary>
    public class GameUIController : MonoBehaviour
    {
        class Bubble
        {
            public RectTransform Rect;
            public Image Image;
            public UILabel Label;
            public float Born, Until;
            public float Tilt;
        }

        Canvas m_Canvas;
        RectTransform m_ShakeRoot, m_Safe;
        RectTransform m_Start, m_Scan, m_Place, m_Egg, m_HUD, m_GameOver, m_Coaching, m_Paused, m_Carry;
        Image m_GripFill;
        UILabel m_CarryTitle;
        Image m_Flash, m_DangerFill, m_ScanEgg, m_TitleEgg, m_NewBestStamp;
        RectTransform[] m_ScanDots;
        RectTransform m_TitleGroup, m_MeterPill, m_ScanBack;
        UILabel m_StartBest, m_Message, m_MockHint, m_Tracking;
        UILabel m_Time, m_Honks, m_Best, m_DangerLabel, m_TurnAround;
        UILabel m_GoTitle, m_GoTime, m_GoScore, m_GoBest, m_GoGrudge;
        Button m_PhotoShutter;
        UILabel m_GoRecap, m_PhotoLabel, m_PostLabel, m_PhotoHint;
        RectTransform m_Photo, m_Card, m_BoardRow, m_ActionRow, m_ShareButton, m_PhotoButton, m_Ticker;
        readonly UILabel[] m_TickerName = new UILabel[3];
        readonly UILabel[] m_TickerGoose = new UILabel[3];
        readonly UILabel[] m_TickerTime = new UILabel[3];
        UIInput m_NameInput;
        Button m_PostButton;
        int m_BoardToken;
        CanvasGroup m_OverlayGroup, m_StampGroup;
        UILabel m_StampLine1, m_StampLine2;
        const string PlayerNameKey = "GooseBrawl.PlayerName";
        UILabel m_Subtitle;
        RectTransform m_SubtitlePill, m_BreadButton;
        Image m_BreadRing, m_BreadFace;
        RawImage m_BreadRoll;
        Image m_BreadFlat;
        float m_BreadDanger;
        static Sprite s_Ring;
        float m_SubtitleUntil;
        UILabel m_SoundToggle, m_HapticsToggle;
        RectTransform m_TrackingPill;
        UILabel m_ScanSubtitle, m_PlaceSubtitle;
        GooseLocator m_Locator;
        Action m_CoachingDone;
        float m_ScanShownAt;
        readonly Bubble[] m_Bubbles = new Bubble[4];

        float m_Shake;
        float m_FlashUntil, m_FlashDuration;
        Color m_FlashColor;
        float m_MessageUntil;
        float m_DangerLevel;
        bool m_GooseBehind, m_GooseVisible;
        float m_GooseAngle;
        bool m_HudActive;
        Vector2 m_ShakeBase;

        void Awake()
        {
            Build();
            HideAll();
        }

        // ------------------------------------------------------------------ build
        void Build()
        {
            m_Canvas = UIFactory.CreateCanvas("GooseBrawl Canvas");
            m_Canvas.transform.SetParent(transform, false);
            // One group over everything so the photo capture can hide the whole overlay for a frame (the HUD is a nested canvas).
            m_OverlayGroup = m_Canvas.gameObject.AddComponent<CanvasGroup>();

            m_ShakeRoot = UIFactory.Stretch(UIFactory.CreateRect(m_Canvas.transform, "ShakeRoot"));

            m_Safe = UIFactory.Stretch(UIFactory.CreateRect(m_ShakeRoot, "SafeArea"));
            m_Safe.gameObject.AddComponent<SafeAreaFitter>();

            BuildStart();
            BuildScan();
            BuildPlace();
            BuildEgg();
            BuildHUD();
            BuildGameOver();
            BuildCoaching();
            BuildPaused();
            BuildCarry();
            BuildPhoto();
            BuildStamp();

            // Locator: live from the fly-in through the chase, above the screens.
            m_Locator = GooseLocator.Create(m_Safe);

            // Honk bubbles (pooled, above everything but the flash)
            for (int i = 0; i < m_Bubbles.Length; i++)
            {
                var pill = UIFactory.CreatePill(m_Safe, "HonkBubble" + i, UITheme.CreamPanel, new Vector2(300f, 110f), false);
                UIFactory.Place(pill.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(300f, 110f));
                var label = UIFactory.CreateLabel(pill.transform, "Text", "HONK!", 58f, UITheme.Brown);
                UIFactory.Stretch(label.Rect, 6f);
                pill.gameObject.SetActive(false);
                m_Bubbles[i] = new Bubble { Rect = pill.rectTransform, Image = pill, Label = label };
            }

            // Centre message ("OH NO.")
            m_Message = UIFactory.CreateLabel(m_Safe, "Message", "", 112f, UITheme.White, true, TextAnchor.MiddleCenter, UITheme.Brown, 0.26f, UIFont.Display, autoSize: true);
            UIFactory.Place(m_Message.Rect, new Vector2(0.5f, 0.5f), new Vector2(0f, 130f), new Vector2(940f, 270f));
            m_Message.SetActive(false);

            // Subtitle pill: what the goose just said, for the audience (lives across screens, like the message).
            var subtitlePill = UIFactory.CreateGlassPill(m_Safe, "SubtitlePill", new Vector2(560f, 92f), strong: true);
            UIFactory.Place(subtitlePill.rectTransform, new Vector2(0.5f, 0f), new Vector2(-70f, 196f), new Vector2(560f, 92f));
            m_Subtitle = UIFactory.CreateLabel(subtitlePill.transform, "Text", "", 32f, UITheme.Ink, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud, autoSize: true);
            UIFactory.Stretch(m_Subtitle.Rect, 12f);
            m_SubtitlePill = subtitlePill.rectTransform;
            m_SubtitlePill.gameObject.SetActive(false);

            // Tracking hint pill
            var trackingPill = UIFactory.CreateGlassPill(m_Safe, "TrackingPill", new Vector2(760f, 88f));
            UIFactory.Place(trackingPill.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 560f), new Vector2(760f, 88f));
            m_Tracking = UIFactory.CreateLabel(trackingPill.transform, "Text", "", 34f, UITheme.Ink, false, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Stretch(m_Tracking.Rect, 10f);
            m_TrackingPill = trackingPill.rectTransform;
            m_TrackingPill.gameObject.SetActive(false);

#if UNITY_EDITOR
            m_MockHint = UIFactory.CreateLabel(m_Safe, "MockHint", "editor mock  ·  WASD move  ·  Q/E turn  ·  right-drag look  ·  SPACE start/steal/resume  ·  R restart  ·  B bread  ·  Y yell  ·  N name  ·  M sorry  ·  P photo", 19f, UITheme.Ink, false, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(m_MockHint.Rect, new Vector2(0.5f, 0f), new Vector2(0f, 8f), new Vector2(1040f, 36f));
            m_MockHint.SetActive(false);
#endif

            m_Flash = UIFactory.CreateImage(m_ShakeRoot, "Flash", UIFactory.Vignette, new Color(1f, 1f, 1f, 0f));
            UIFactory.Stretch(m_Flash.rectTransform, -260f);
            m_Flash.gameObject.SetActive(false);
        }

        RectTransform MakeScreen(string name)
        {
            return UIFactory.Stretch(UIFactory.CreateRect(m_Safe, name));
        }

        /// <summary>Glass pill at the top with a title and optional smaller line.</summary>
        RectTransform TopPill(Transform parent, string title, string subtitle, out UILabel subtitleLabel) => TopPill(parent, title, subtitle, out subtitleLabel, out _);

        RectTransform TopPill(Transform parent, string title, string subtitle, out UILabel subtitleLabel, out UILabel titleLabel)
        {
            subtitleLabel = null;
            float h = subtitle != null ? 164f : 108f;
            var pill = UIFactory.CreateGlassPill(parent, "TopPill", new Vector2(960f, h));
            UIFactory.Place(pill.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -36f), new Vector2(960f, h));
            var t = UIFactory.CreateLabel(pill.transform, "Title", title, 42f, UITheme.Ink, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            titleLabel = t;
            UIFactory.Place(t.Rect, new Vector2(0.5f, 1f), new Vector2(0f, subtitle != null ? -24f : -14f), new Vector2(900f, 66f));
            if (subtitle != null)
            {
                subtitleLabel = UIFactory.CreateLabel(pill.transform, "Subtitle", subtitle, 30f, UITheme.InkMuted, false, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
                UIFactory.Place(subtitleLabel.Rect, new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(900f, 56f));
            }
            return pill.rectTransform;
        }

        RectTransform BottomButton(Transform parent, string name, string text, Action onClick, float y = 120f)
        {
            var size = new Vector2(640f, 150f);
            var button = UIFactory.CreateButton(parent, name, text, size, 60f, out _);
            var holder = button.transform.parent.GetComponent<RectTransform>();
            UIFactory.Place(holder, new Vector2(0.5f, 0f), new Vector2(0f, y), size);
            button.onClick.AddListener(() => onClick());
            return holder;
        }

        RectTransform SmallGlassButton(Transform parent, string name, string text, Vector2 anchor, Vector2 pos, Vector2 size, Action onClick, out UILabel label)
        {
            var button = UIFactory.CreateGlassButton(parent, name, text, size, 30f, out label);
            var holder = button.transform.parent.GetComponent<RectTransform>();
            UIFactory.Place(holder, anchor, pos, size);
            button.onClick.AddListener(() => onClick());
            return holder;
        }

        void BuildStart()
        {
            m_Start = MakeScreen("StartScreen");
            var dim = UIFactory.CreateImage(m_Start, "Dim", null, new Color(0.16f, 0.09f, 0.03f, 0.3f));
            UIFactory.Stretch(dim.rectTransform);

            // Egg logo with the title inside.
            m_TitleGroup = UIFactory.CreateRect(m_Start, "TitleGroup");
            UIFactory.Place(m_TitleGroup, new Vector2(0.5f, 1f), new Vector2(0f, -150f), new Vector2(760f, 900f));
            var eggShadow = UIFactory.CreateImage(m_TitleGroup, "EggShadow", UIFactory.Egg, new Color(0.16f, 0.09f, 0.03f, 0.35f));
            UIFactory.Place(eggShadow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(14f, -18f), new Vector2(700f, 860f));
            m_TitleEgg = UIFactory.CreateImage(m_TitleGroup, "Egg", UIFactory.Egg, UITheme.Cream);
            UIFactory.Place(m_TitleEgg.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(700f, 860f));
            var title = UIFactory.CreateLabel(m_TitleGroup, "Title", "GOOSED.", 150f, UITheme.Brown, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Display, autoSize: true);
            UIFactory.Place(title.Rect, new Vector2(0.5f, 0.5f), new Vector2(0f, -10f), new Vector2(620f, 260f));
            var honk = UIFactory.CreateLabel(m_TitleGroup, "Honk", "honk.", 46f, UITheme.Yolk, true, TextAnchor.MiddleCenter, UITheme.Brown, 0.2f);
            UIFactory.Place(honk.Rect, new Vector2(0.5f, 0.5f), new Vector2(210f, -290f), new Vector2(300f, 80f));
            honk.Rect.localRotation = Quaternion.Euler(0f, 0f, -12f);
            m_TitleGroup.localRotation = Quaternion.Euler(0f, 0f, 4f);

            var sub = UIFactory.CreateLabel(m_Start, "Subtitle", "Steal the egg.\nGet goosed.", 50f, UITheme.White, true, TextAnchor.MiddleCenter, UITheme.Brown, 0.25f);
            UIFactory.Place(sub.Rect, new Vector2(0.5f, 0f), new Vector2(0f, 560f), new Vector2(980f, 150f));

            var bestPill = UIFactory.CreatePill(m_Start, "BestPill", UITheme.CreamPanel, new Vector2(760f, 74f), false);
            UIFactory.Place(bestPill.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 440f), new Vector2(760f, 74f));
            m_StartBest = UIFactory.CreateLabel(bestPill.transform, "Best", "", 32f, UITheme.Brown, false);
            UIFactory.Stretch(m_StartBest.Rect, 8f);

            BottomButton(m_Start, "StartButton", "START", () => GooseGameManager.Instance.OnStartPressed(), 260f);
            // No HOW TO PLAY: the card spelled out that a goose was coming.

            // Booth ticker: today's top three from the Goose Board (filled by ShowStart when the Worker answers).
            var ticker = UIFactory.CreateGlassPill(m_Start, "Ticker", new Vector2(760f, 176f), strong: true);
            UIFactory.Place(ticker.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 64f), new Vector2(760f, 176f));
            m_Ticker = ticker.rectTransform;
            var tickerTitle = UIFactory.CreateLabel(ticker.transform, "Title", "TOP GEESE TODAY", 22f, UITheme.Yolk, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(tickerTitle.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -12f), new Vector2(700f, 32f));
            for (int i = 0; i < 3; i++)
            {
                float y = -52f - i * 40f;
                m_TickerName[i] = UIFactory.CreateLabel(ticker.transform, "Name" + i, "", 28f, UITheme.Ink, true, TextAnchor.MiddleLeft, default, 0f, UIFont.Hud, autoSize: true);
                UIFactory.Place(m_TickerName[i].Rect, new Vector2(0f, 1f), new Vector2(40f, y), new Vector2(320f, 38f));
                m_TickerGoose[i] = UIFactory.CreateLabel(ticker.transform, "Goose" + i, "", 24f, UITheme.InkMuted, false, TextAnchor.MiddleLeft, default, 0f, UIFont.Hud, autoSize: true);
                UIFactory.Place(m_TickerGoose[i].Rect, new Vector2(0f, 1f), new Vector2(372f, y), new Vector2(200f, 38f));
                m_TickerTime[i] = UIFactory.CreateLabel(ticker.transform, "Time" + i, "", 28f, UITheme.Yolk, true, TextAnchor.MiddleRight, default, 0f, UIFont.Hud);
                UIFactory.Place(m_TickerTime[i].Rect, new Vector2(1f, 1f), new Vector2(-40f, y), new Vector2(140f, 38f));
            }
            m_Ticker.gameObject.SetActive(false);

            // Settings toggles, top right.
            SmallGlassButton(m_Start, "SoundToggle", "SOUND ON", new Vector2(1f, 1f), new Vector2(-24f, -24f), new Vector2(250f, 78f), ToggleSound, out m_SoundToggle);
            SmallGlassButton(m_Start, "HapticsToggle", "HAPTICS ON", new Vector2(1f, 1f), new Vector2(-24f, -114f), new Vector2(250f, 78f), ToggleHaptics, out m_HapticsToggle);
        }

        void BuildScan()
        {
            m_Scan = MakeScreen("ScanScreen");
            TopPill(m_Scan, "Move your phone slowly to scan.", "Point it at the floor in front of you.", out m_ScanSubtitle);

            m_ScanEgg = UIFactory.CreateImage(m_Scan, "ScanEgg", UIFactory.Egg, new Color(1f, 0.965f, 0.87f, 0.28f));
            UIFactory.Place(m_ScanEgg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 40f), new Vector2(300f, 380f));
            m_ScanDots = new RectTransform[3];
            for (int i = 0; i < 3; i++)
            {
                var dot = UIFactory.CreateImage(m_Scan, "ScanDot" + i, UIFactory.Circle, UITheme.Yolk);
                UIFactory.Place(dot.rectTransform, new Vector2(0.5f, 0.5f), new Vector2((i - 1) * 64f, 40f), new Vector2(30f, 30f));
                m_ScanDots[i] = dot.rectTransform;
            }
            m_ScanBack = SmallGlassButton(m_Scan, "ScanBack", "BACK", new Vector2(0.5f, 0f), new Vector2(0f, 120f), new Vector2(300f, 84f),
                () => GooseGameManager.Instance.ReturnToTitle(), out _);
            m_ScanBack.gameObject.SetActive(false);
        }

        void BuildPlace()
        {
            m_Place = MakeScreen("PlaceScreen");
            TopPill(m_Place, "Tap the floor to place the nest.", "Aim the ring at a clear spot.", out m_PlaceSubtitle);
        }

        void BuildEgg()
        {
            m_Egg = MakeScreen("EggScreen");
            TopPill(m_Egg, "Grab the egg.", "Breakfast is not going to make itself.", out _);
            BottomButton(m_Egg, "StealButton", "TAKE EGG", () => GooseGameManager.Instance.OnStealEggPressed());
        }

        void BuildHUD()
        {
            m_HUD = MakeScreen("HUD");
            // Own nested canvas: the timer, meter and bread button change every frame, so only this batch is rebuilt,
            // not the whole overlay. The raycaster keeps the bread button tappable inside the nested canvas.
            m_HUD.gameObject.AddComponent<Canvas>();
            m_HUD.gameObject.AddComponent<GraphicRaycaster>();

            m_Time = UIFactory.CreateLabel(m_HUD, "Time", "0:00.0", 100f, UITheme.Ink, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(m_Time.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -26f), new Vector2(700f, 120f));
            UIFactory.AddShadow(m_Time, new Color(0f, 0f, 0f, 0.55f), new Vector2(0f, -4f));

            var honksPill = UIFactory.CreateGlassPill(m_HUD, "HonksPill", new Vector2(420f, 64f));
            UIFactory.Place(honksPill.rectTransform, new Vector2(0.5f, 1f), new Vector2(-190f, -150f), new Vector2(420f, 64f));
            m_Honks = UIFactory.CreateLabel(honksPill.transform, "Honks", "HONKS 0", 30f, UITheme.Ink, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Stretch(m_Honks.Rect, 8f);
            var bestPill = UIFactory.CreateGlassPill(m_HUD, "BestPill", new Vector2(300f, 64f));
            UIFactory.Place(bestPill.rectTransform, new Vector2(0.5f, 1f), new Vector2(190f, -150f), new Vector2(300f, 64f));
            m_Best = UIFactory.CreateLabel(bestPill.transform, "Best", "BEST 0:00.0", 30f, UITheme.InkMuted, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Stretch(m_Best.Rect, 8f);

            // BREAD: one throw per round. A round glass button with the real roll inside, bottom-right above the meter.
            // Quiet while the goose is far; it grows and its ring pulses as the goose closes in (a "save me", not a nag).
            m_BreadButton = UIFactory.CreateRect(m_HUD, "Bread");
            UIFactory.Place(m_BreadButton, new Vector2(1f, 0f), new Vector2(-40f, 180f), new Vector2(150f, 150f));
            m_BreadButton.pivot = new Vector2(0.5f, 0.5f);
            m_BreadButton.anchoredPosition = new Vector2(-115f, 255f);
            m_BreadRing = UIFactory.CreateImage(m_BreadButton, "Ring", RingSprite, new Color(UITheme.Yolk.r, UITheme.Yolk.g, UITheme.Yolk.b, 0f));
            UIFactory.Place(m_BreadRing.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(214f, 214f));
            m_BreadFace = UIFactory.CreateImage(m_BreadButton, "Face", UIFactory.Circle, UITheme.GlassStrong, true);
            UIFactory.Place(m_BreadFace.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(150f, 150f));
            var breadButton = m_BreadFace.gameObject.AddComponent<Button>();
            breadButton.targetGraphic = m_BreadFace;
            var bc = breadButton.colors; bc.normalColor = Color.white; bc.highlightedColor = Color.white; bc.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f); bc.fadeDuration = 0.05f; breadButton.colors = bc;
            breadButton.onClick.AddListener(() => GooseGameManager.Instance.OnBreadPressed());
            var breadPress = m_BreadFace.gameObject.AddComponent<UIButtonPress>();
            breadPress.target = m_BreadButton;
            var rollGo = new GameObject("Roll", typeof(RectTransform));
            rollGo.transform.SetParent(m_BreadFace.transform, false);
            m_BreadRoll = rollGo.AddComponent<RawImage>();
            m_BreadRoll.raycastTarget = false;
            m_BreadRoll.color = Color.white;
            UIFactory.Place(m_BreadRoll.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 2f), new Vector2(124f, 124f));
            m_BreadRoll.enabled = false;
            // Flat fallback (a warm disc) only if the icon camera cannot be created.
            m_BreadFlat = UIFactory.CreateImage(m_BreadFace.transform, "Flat", UIFactory.Circle, new Color(0.83f, 0.6f, 0.33f, 1f));
            UIFactory.Place(m_BreadFlat.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(84f, 84f));
            m_BreadButton.gameObject.SetActive(false);

            SmallGlassButton(m_HUD, "Pause", "II", new Vector2(1f, 1f), new Vector2(-24f, -24f), new Vector2(96f, 96f),
                () => GooseGameManager.Instance.TogglePause(), out var pauseLabel);
            pauseLabel.FontSize = 40f;

            // Goose-o-meter: slim glass pill, state word, 6 px bar.
            var meter = UIFactory.CreateGlassPill(m_HUD, "Meter", new Vector2(880f, 118f));
            UIFactory.Place(meter.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 48f), new Vector2(880f, 118f));
            m_MeterPill = meter.rectTransform;
            // Hidden: the locator (distance + arrow) already says where the goose is; a filling bar read as gimmicky.
            m_MeterPill.gameObject.SetActive(false);
            m_DangerLabel = UIFactory.CreateLabel(meter.transform, "DangerLabel", "GOOSE INCOMING", 34f, UITheme.Ink, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(m_DangerLabel.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -18f), new Vector2(820f, 50f));
            var track = UIFactory.CreatePill(meter.transform, "Track", new Color(1f, 1f, 1f, 0.12f), new Vector2(760f, 8f), false);
            UIFactory.Place(track.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(760f, 8f));
            var mask = track.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = true;
            m_DangerFill = UIFactory.CreateImage(track.transform, "Fill", null, UITheme.Yolk);
            m_DangerFill.rectTransform.anchorMin = new Vector2(0f, 0f);
            m_DangerFill.rectTransform.anchorMax = new Vector2(0f, 1f);
            m_DangerFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            m_DangerFill.rectTransform.anchoredPosition = Vector2.zero;
            m_DangerFill.rectTransform.sizeDelta = new Vector2(0f, 0f);

            m_TurnAround = UIFactory.CreateLabel(m_HUD, "TurnAround", "TURN AROUND!", 96f, UITheme.Danger, true, TextAnchor.MiddleCenter, UITheme.White, 0.22f, UIFont.Display, autoSize: true);
            UIFactory.Place(m_TurnAround.Rect, new Vector2(0.5f, 0.5f), new Vector2(0f, 400f), new Vector2(940f, 130f));
            m_TurnAround.SetActive(false);
        }

        void BuildGameOver()
        {
            m_GameOver = MakeScreen("GameOverScreen");
            var dim = UIFactory.CreateImage(m_GameOver, "Dim", null, UITheme.Dim);
            UIFactory.Stretch(dim.rectTransform);

            m_GoTitle = UIFactory.CreateLabel(m_GameOver, "Title", "THE GOOSE\nGOT YOU", 112f, UITheme.Yolk, true, TextAnchor.MiddleCenter, UITheme.Brown, 0.28f, UIFont.Display, autoSize: true);
            UIFactory.Place(m_GoTitle.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -210f), new Vector2(920f, 300f));
            m_GoTitle.Rect.localRotation = Quaternion.Euler(0f, 0f, -3f);
            m_GoGrudge = UIFactory.CreateLabel(m_GameOver, "Grudge", "", 34f, UITheme.White, true, TextAnchor.MiddleCenter, UITheme.Brown, 0.22f);
            UIFactory.Place(m_GoGrudge.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -530f), new Vector2(900f, 56f));
            m_GoGrudge.SetActive(false);

            // The card hangs from its top edge (a fixed distance above the buttons) so it can shrink from the bottom when the board row is hidden.
            m_Card = UIFactory.CreatePanel(m_GameOver, "ResultCard", UITheme.CreamPanel);
            UIFactory.Place(m_Card, new Vector2(0.5f, 0.5f), new Vector2(0f, 320f), new Vector2(820f, 600f));
            m_Card.pivot = new Vector2(0.5f, 1f);
            m_Card.anchoredPosition = new Vector2(0f, 320f);
            var card = m_Card;
            var cardImg = card.GetComponent<Image>();
            cardImg.pixelsPerUnitMultiplier = 0.55f;
            m_GoTime = UIFactory.CreateLabel(card, "Time", "You survived", 40f, UITheme.Brown, false);
            UIFactory.Place(m_GoTime.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -34f), new Vector2(780f, 60f));
            m_GoScore = UIFactory.CreateLabel(card, "Score", "0:00.0", 118f, UITheme.Brown, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(m_GoScore.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -96f), new Vector2(780f, 150f));

            // Stack under the big time: NEW BEST band, the run recap, the stats line, the board row.
            m_NewBestStamp = UIFactory.CreatePill(card, "NewBest", UITheme.Danger, new Vector2(300f, 70f), false);
            UIFactory.Place(m_NewBestStamp.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -252f), new Vector2(300f, 70f));
            m_NewBestStamp.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -2f);
            var stampLabel = UIFactory.CreateLabel(m_NewBestStamp.transform, "Text", "NEW BEST!", 36f, UITheme.White);
            UIFactory.Stretch(stampLabel.Rect, 6f);
            m_GoRecap = UIFactory.CreateLabel(card, "Recap", "", 28f, UITheme.Brown, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud, autoSize: true);
            UIFactory.Place(m_GoRecap.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -334f), new Vector2(760f, 44f));
            m_GoBest = UIFactory.CreateLabel(card, "Best", "HONKS 0   ·   BEST 0:00.0", 32f, UITheme.Muted, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(m_GoBest.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -386f), new Vector2(780f, 50f));

            // Goose Board row: your name + POST TO BOARD. Hidden (and the card shortened) when the Worker is unreachable.
            m_BoardRow = UIFactory.CreateRect(card, "BoardRow");
            UIFactory.Place(m_BoardRow, new Vector2(0.5f, 1f), new Vector2(0f, -456f), new Vector2(760f, 84f));
            m_NameInput = UIFactory.CreateInputField(m_BoardRow, "Name", "YOUR NAME FOR THE BOARD", new Vector2(470f, 84f), 30f, 12, v => CleanName(v, false));
            UIFactory.Place(m_NameInput.Rect, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(470f, 84f));
            m_PostButton = UIFactory.CreateButton(m_BoardRow, "PostBoard", "POST TO BOARD", new Vector2(270f, 84f), 26f, out m_PostLabel);
            UIFactory.Place(m_PostButton.transform.parent.GetComponent<RectTransform>(), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(270f, 84f));
            m_PostButton.onClick.AddListener(OnPostToBoardPressed);

            BottomButton(m_GameOver, "RunAgainButton", "RUN AGAIN", () => GooseGameManager.Instance.OnRunAgainPressed(), 260f);
            // Secondary row: SHARE · PHOTO WITH <goose>. LayoutActionRow centres them (SHARE only when a shot exists).
            // No MOVE NEST: RUN AGAIN always places the nest again, the old spot is never reused.
            m_ActionRow = UIFactory.CreateRect(m_GameOver, "ActionRow");
            UIFactory.Place(m_ActionRow, new Vector2(0.5f, 0f), new Vector2(0f, 110f), new Vector2(960f, 84f));
            m_ShareButton = SmallGlassButton(m_ActionRow, "Share", "SHARE", new Vector2(0.5f, 0f), Vector2.zero, new Vector2(200f, 84f),
                () => GooseGameManager.Instance.OnSharePressed(), out _);
            m_PhotoButton = SmallGlassButton(m_ActionRow, "Photo", "PHOTO WITH THE GOOSE", new Vector2(0.5f, 0f), Vector2.zero, new Vector2(380f, 84f),
                () => GooseGameManager.Instance.OnPhotoPressed(), out m_PhotoLabel);
            m_PhotoLabel.AutoSize(30f, 18f);
            LayoutActionRow(false);
        }

        /// <summary>Photo mode: the card slides away; a shutter, BACK and FLIP remain. Selfie by default (the goose over your shoulder), FLIP for the goose posing in the room.</summary>
        void BuildPhoto()
        {
            m_Photo = MakeScreen("PhotoScreen");
            TopPill(m_Photo, "Hold it like a selfie. Tap the shutter.", null, out _, out m_PhotoHint);
            var shutter = UIFactory.CreateImage(m_Photo, "Shutter", UIFactory.Circle, UITheme.GlassStrong, true);
            UIFactory.Place(shutter.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 230f), new Vector2(164f, 164f));
            shutter.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            shutter.rectTransform.anchoredPosition = new Vector2(0f, 230f);
            var inner = UIFactory.CreateImage(shutter.transform, "Inner", UIFactory.Circle, UITheme.Yolk);
            UIFactory.Place(inner.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(112f, 112f));
            var btn = shutter.gameObject.AddComponent<Button>();
            btn.targetGraphic = shutter;
            var bc = btn.colors; bc.normalColor = Color.white; bc.highlightedColor = Color.white; bc.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f); bc.fadeDuration = 0.05f; btn.colors = bc;
            m_PhotoShutter = btn;
            btn.onClick.AddListener(() => GooseGameManager.Instance.OnShutterPressed());
            var press = shutter.gameObject.AddComponent<UIButtonPress>();
            press.target = shutter.rectTransform;
            SmallGlassButton(m_Photo, "PhotoBack", "BACK", new Vector2(0f, 0f), new Vector2(40f, 190f), new Vector2(200f, 84f),
                () => GooseGameManager.Instance.OnPhotoBackPressed(), out _);
            SmallGlassButton(m_Photo, "PhotoFlip", "FLIP", new Vector2(1f, 0f), new Vector2(-40f, 190f), new Vector2(200f, 84f),
                () => GooseGameManager.Instance.OnPhotoFlipPressed(), out _);
        }

        /// <summary>
        /// The brand stamp on every photo: its own canvas above the overlay, always active at alpha 0 (so the glyphs exist
        /// before the capture frame) and shown only for the one frame that is captured.
        /// </summary>
        void BuildStamp()
        {
            var canvas = UIFactory.CreateCanvas("Stamp Canvas");
            canvas.transform.SetParent(transform, false);
            canvas.sortingOrder = 20;
            var raycaster = canvas.GetComponent<GraphicRaycaster>();
            if (raycaster != null) raycaster.enabled = false;
            m_StampGroup = canvas.gameObject.AddComponent<CanvasGroup>();
            m_StampGroup.alpha = 0f;
            m_StampGroup.blocksRaycasts = false;
            m_StampGroup.interactable = false;
            var safe = UIFactory.Stretch(UIFactory.CreateRect(canvas.transform, "SafeArea"));
            safe.gameObject.AddComponent<SafeAreaFitter>();

            var panel = UIFactory.CreatePanel(safe, "StampPanel", UITheme.CreamPanel);
            UIFactory.Place(panel, new Vector2(0.5f, 0f), new Vector2(0f, 64f), new Vector2(900f, 260f));
            panel.GetComponent<Image>().pixelsPerUnitMultiplier = 0.55f;
            var egg = UIFactory.CreateImage(panel, "Egg", UIFactory.Egg, UITheme.Yolk);
            UIFactory.Place(egg.rectTransform, new Vector2(0f, 0.5f), new Vector2(44f, 0f), new Vector2(116f, 146f));
            egg.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 8f);
            var word = UIFactory.CreateLabel(panel, "Wordmark", "GOOSED.", 84f, UITheme.Brown, true, TextAnchor.MiddleLeft, default, 0f, UIFont.Display);
            UIFactory.Place(word.Rect, new Vector2(0f, 1f), new Vector2(190f, -22f), new Vector2(480f, 100f));
            var footer = UIFactory.CreateLabel(panel, "Footer", "HACK THE NORTH 2026", 22f, UITheme.Muted, true, TextAnchor.MiddleRight, default, 0f, UIFont.Hud);
            UIFactory.Place(footer.Rect, new Vector2(1f, 1f), new Vector2(-44f, -44f), new Vector2(360f, 32f));
            m_StampLine1 = UIFactory.CreateLabel(panel, "Line1", "", 28f, UITheme.Muted, true, TextAnchor.MiddleLeft, default, 0f, UIFont.Hud, autoSize: true);
            UIFactory.Place(m_StampLine1.Rect, new Vector2(0f, 0f), new Vector2(190f, 98f), new Vector2(670f, 40f));
            m_StampLine2 = UIFactory.CreateLabel(panel, "Line2", "", 48f, UITheme.Brown, true, TextAnchor.MiddleLeft, default, 0f, UIFont.Hud, autoSize: true);
            UIFactory.Place(m_StampLine2.Rect, new Vector2(0f, 0f), new Vector2(190f, 34f), new Vector2(670f, 64f));
        }

        /// <summary>The carry phase: a glass pill with the grip bar. Your movement drains it.</summary>
        void BuildCarry()
        {
            m_Carry = MakeScreen("CarryScreen");
            var pill = UIFactory.CreateGlassPill(m_Carry, "CarryPill", new Vector2(960f, 178f));
            UIFactory.Place(pill.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -36f), new Vector2(960f, 178f));
            m_CarryTitle = UIFactory.CreateLabel(pill.transform, "Title", "DON'T DROP IT.", 42f, UITheme.Ink, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(m_CarryTitle.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(900f, 60f));
            var sub = UIFactory.CreateLabel(pill.transform, "Subtitle", "Carry it away from the nest. Slowly.", 30f, UITheme.InkMuted, false, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(sub.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -84f), new Vector2(900f, 50f));
            var gripLabel = UIFactory.CreateLabel(pill.transform, "GripLabel", "GRIP", 24f, UITheme.InkMuted, true, TextAnchor.MiddleLeft, default, 0f, UIFont.Hud);
            UIFactory.Place(gripLabel.Rect, new Vector2(0f, 0f), new Vector2(70f, 30f), new Vector2(120f, 30f));
            var track = UIFactory.CreatePill(pill.transform, "GripTrack", new Color(1f, 1f, 1f, 0.12f), new Vector2(700f, 10f), false);
            UIFactory.Place(track.rectTransform, new Vector2(1f, 0f), new Vector2(-70f, 40f), new Vector2(700f, 10f));
            var mask = track.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = true;
            m_GripFill = UIFactory.CreateImage(track.transform, "Fill", null, UITheme.Yolk);
            m_GripFill.rectTransform.anchorMin = new Vector2(0f, 0f);
            m_GripFill.rectTransform.anchorMax = new Vector2(0f, 1f);
            m_GripFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            m_GripFill.rectTransform.anchoredPosition = Vector2.zero;
            m_GripFill.rectTransform.sizeDelta = new Vector2(700f, 0f);
        }

        public void ShowCarry()
        {
            HideAll();
            m_Carry.gameObject.SetActive(true);
            SetGrip(1f);
        }

        public void HideCarry()
        {
            m_Carry.gameObject.SetActive(false);
        }

        public void SetGrip(float grip01)
        {
            if (m_GripFill == null) return;
            float g = Mathf.Clamp01(grip01);
            m_GripFill.rectTransform.sizeDelta = new Vector2(700f * g, 0f);
            m_GripFill.color = Color.Lerp(UITheme.Danger, UITheme.Yolk, Mathf.Clamp01((g - 0.2f) / 0.5f));
            if (m_CarryTitle != null) m_CarryTitle.Text = g < 0.35f ? "IT'S SLIPPING." : "DON'T DROP IT.";
        }

        void BuildCoaching()
        {
            m_Coaching = MakeScreen("Coaching");
            var dim = UIFactory.CreateImage(m_Coaching, "Dim", null, new Color(0.02f, 0.02f, 0.02f, 0.72f), true);
            UIFactory.Stretch(dim.rectTransform);
            var card = UIFactory.CreateGlassCard(m_Coaching, "Card", new Vector2(920f, 900f));
            UIFactory.Place(card, new Vector2(0.5f, 0.5f), new Vector2(0f, 80f), new Vector2(920f, 900f));
            var title = UIFactory.CreateLabel(card, "Title", "HOW IT WORKS", 56f, UITheme.Yolk, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(title.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(860f, 80f));
            string[] steps =
            {
                "1   Scan your floor. Walls too, if you want the goose to dodge them.",
                "2   Place the nest and grab the egg. Breakfast!",
                "3   The goose is a real thing in your room. It lands in front of you.",
                "4   TURN AROUND AND RUN. Keep the phone facing forward. Survive.",
            };
            for (int i = 0; i < steps.Length; i++)
            {
                var l = UIFactory.CreateLabel(card, "Step" + i, steps[i], 34f, UITheme.Ink, false, TextAnchor.MiddleLeft, default, 0f, UIFont.Hud);
                UIFactory.Place(l.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -150f - i * 130f), new Vector2(820f, 110f));
            }
            var safety = UIFactory.CreateLabel(card, "Safety", "Clear about 3 metres. Watch where you walk, not only the goose.", 30f, UITheme.Yolk, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(safety.Rect, new Vector2(0.5f, 0f), new Vector2(0f, 150f), new Vector2(820f, 90f));
            var btn = UIFactory.CreateButton(card, "GotIt", "GOT IT", new Vector2(420f, 116f), 48f, out _);
            var holder = btn.transform.parent.GetComponent<RectTransform>();
            UIFactory.Place(holder, new Vector2(0.5f, 0f), new Vector2(0f, 30f), new Vector2(420f, 116f));
            btn.onClick.AddListener(() =>
            {
                m_Coaching.gameObject.SetActive(false);
                var done = m_CoachingDone;
                m_CoachingDone = null;
                done?.Invoke();
            });
        }

        void BuildPaused()
        {
            m_Paused = MakeScreen("Paused");
            var dim = UIFactory.CreateImage(m_Paused, "Dim", null, new Color(0.02f, 0.02f, 0.02f, 0.55f), true);
            UIFactory.Stretch(dim.rectTransform);
            var card = UIFactory.CreateGlassCard(m_Paused, "Card", new Vector2(700f, 420f));
            UIFactory.Place(card, new Vector2(0.5f, 0.5f), new Vector2(0f, 60f), new Vector2(700f, 420f));
            var title = UIFactory.CreateLabel(card, "Title", "PAUSED", 72f, UITheme.Ink, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(title.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -50f), new Vector2(640f, 90f));
            var sub = UIFactory.CreateLabel(card, "Sub", "The goose is waiting. It is not patient.", 30f, UITheme.InkMuted, false, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(sub.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -150f), new Vector2(640f, 60f));
            var btn = UIFactory.CreateButton(card, "Continue", "CONTINUE", new Vector2(420f, 116f), 48f, out _);
            var holder = btn.transform.parent.GetComponent<RectTransform>();
            UIFactory.Place(holder, new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(420f, 116f));
            btn.onClick.AddListener(() => GooseGameManager.Instance.ResumeGame());
        }

        // ------------------------------------------------------------------ screens
        void HideAll()
        {
            m_Start.gameObject.SetActive(false);
            m_Scan.gameObject.SetActive(false);
            m_Place.gameObject.SetActive(false);
            m_Egg.gameObject.SetActive(false);
            m_HUD.gameObject.SetActive(false);
            m_GameOver.gameObject.SetActive(false);
            m_Coaching.gameObject.SetActive(false);
            m_Paused.gameObject.SetActive(false);
            m_Carry.gameObject.SetActive(false);
            if (m_Photo != null) m_Photo.gameObject.SetActive(false);
            m_HudActive = false;
        }

        public void ShowStart(int bestScore, float bestTime, bool mockMode)
        {
            HideAll();
            m_Start.gameObject.SetActive(true);
            m_StartBest.Text = bestTime > 0f ? "best survival  " + ScoreManager.FormatTime(bestTime) : "No one has survived the goose yet.";
#if UNITY_EDITOR
            if (m_MockHint != null) m_MockHint.SetActive(mockMode);
#endif
            RefreshToggles();
            StartCoroutine(PopIn(m_TitleGroup, 0.45f));
            // Fire-and-forget: the board ticker fills in when (if) the Worker answers; START never waits.
            if (m_Ticker != null) m_Ticker.gameObject.SetActive(false);
            var mgr = GooseGameManager.Instance;
            if (mgr != null) mgr.FetchBoardTop(3, rows => { if (m_Start != null && m_Start.gameObject.activeSelf) FillTicker(rows); });
        }

        void FillTicker(GooseBrainClient.BoardRowDto[] rows)
        {
            if (m_Ticker == null) return;
            for (int i = 0; i < 3; i++)
            {
                bool has = rows != null && i < rows.Length && rows[i] != null;
                m_TickerName[i].SetActive(has); m_TickerGoose[i].SetActive(has); m_TickerTime[i].SetActive(has);
                if (!has) continue;
                var r = rows[i];
                m_TickerName[i].Text = (i + 1) + "   " + (string.IsNullOrEmpty(r.name) ? "SOMEONE" : r.name);
                m_TickerGoose[i].Text = string.IsNullOrEmpty(r.goose) ? "" : (r.outcome == "OUTLASTED" ? "outlasted " : "vs ") + r.goose;
                m_TickerTime[i].Text = ScoreManager.FormatTime(r.seconds);
            }
            m_Ticker.gameObject.SetActive(rows != null && rows.Length > 0);
        }

        void RefreshToggles()
        {
            var mgr = GooseGameManager.Instance;
            bool sound = mgr == null || mgr.Audio == null || !mgr.Audio.Muted;
            bool haptics = mgr == null || mgr.Haptics == null || mgr.Haptics.hapticsEnabled;
            if (m_SoundToggle != null) { m_SoundToggle.Text = sound ? "SOUND ON" : "SOUND OFF"; m_SoundToggle.Color = sound ? UITheme.Yolk : UITheme.InkMuted; }
            if (m_HapticsToggle != null) { m_HapticsToggle.Text = haptics ? "HAPTICS ON" : "HAPTICS OFF"; m_HapticsToggle.Color = haptics ? UITheme.Yolk : UITheme.InkMuted; }
        }

        void ToggleSound()
        {
            var mgr = GooseGameManager.Instance;
            if (mgr != null && mgr.Audio != null) mgr.Audio.SetMuted(!mgr.Audio.Muted);
            RefreshToggles();
        }

        void ToggleHaptics()
        {
            var mgr = GooseGameManager.Instance;
            if (mgr != null && mgr.Haptics != null) mgr.Haptics.SetEnabled(!mgr.Haptics.hapticsEnabled);
            RefreshToggles();
        }

        public void ShowCoaching(Action onDone)
        {
            HideAll();
            m_CoachingDone = onDone;
            m_Coaching.gameObject.SetActive(true);
            StartCoroutine(PopIn(m_Coaching.Find("Card") as RectTransform, 0.3f));
        }

        public void ShowScan()
        {
            HideAll();
            m_Scan.gameObject.SetActive(true);
            m_ScanBack.gameObject.SetActive(false);
            m_ScanShownAt = Time.unscaledTime;
        }

        public void ShowPlace()
        {
            HideAll();
            m_Place.gameObject.SetActive(true);
        }

        public void ShowEgg()
        {
            HideAll();
            m_Egg.gameObject.SetActive(true);
        }

        public void ShowStealing()
        {
            HideAll();
        }

        public void ShowHUD()
        {
            HideAll();
            m_HUD.gameObject.SetActive(true);
            m_HudActive = true;
            m_HudDeciseconds = -1; m_HudHonks = -1; m_HudDodges = -1; m_HudBest = -1f;
            if (m_MeterPill.gameObject.activeSelf) StartCoroutine(PopIn(m_MeterPill, 0.3f));
        }

        public void ShowPaused()
        {
            m_Paused.gameObject.SetActive(true);
            m_Paused.SetAsLastSibling();
        }

        public void HidePaused()
        {
            m_Paused.gameObject.SetActive(false);
        }

        public void ShowGameOver(string title, float time, int score, float bestTime, int bestScore, bool newBest)
        {
            ShowGameOver(title, time, score, 0, bestTime, bestScore, newBest, false, "", default);
        }

        public void ShowGameOver(string title, float time, int honks, int dodges, float bestTime, int bestScore, bool newBest, bool won, string grudgeLine, RoundRecap recap)
        {
            HideAll();
            m_GameOver.gameObject.SetActive(true);
            m_GoTitle.Text = title;
            m_GoTime.Text = won ? "You outlasted the goose in" : "You survived";
            m_GoScore.Text = ScoreManager.FormatTime(time);
            m_GoBest.Text = "HONKS " + honks + "   ·   DODGES " + dodges + "   ·   BEST " + ScoreManager.FormatTime(bestTime);
            m_GoGrudge.Text = grudgeLine ?? "";
            m_GoGrudge.SetActive(!string.IsNullOrEmpty(grudgeLine));
            m_NewBestStamp.gameObject.SetActive(newBest);
            string strip = RecapLine(recap);
            m_GoRecap.Text = strip;
            m_GoRecap.SetActive(strip.Length > 0);

            // Board row only when the Worker is reachable; the card shortens without it.
            var mgr = GooseGameManager.Instance;
            bool canPost = mgr != null && mgr.BoardAvailable;
            m_BoardToken++;
            m_BoardRow.gameObject.SetActive(canPost);
            m_Card.sizeDelta = new Vector2(820f, canPost ? 600f : 500f);
            if (canPost)
            {
                m_NameInput.Text = PlayerPrefs.GetString(PlayerNameKey, "");
                m_NameInput.Interactable = true;
                m_PostLabel.Text = "POST TO BOARD";
                m_PostButton.interactable = true;
            }
            m_PhotoLabel.Text = "PHOTO WITH " + (string.IsNullOrEmpty(recap.GooseName) ? "THE GOOSE" : recap.GooseName);
            LayoutActionRow(recap.HasShot);
            StartCoroutine(PopIn(m_GoTitle.Rect, 0.4f));
            if (newBest) StartCoroutine(PopIn(m_NewBestStamp.rectTransform, 0.5f));
        }

        static string RecapLine(in RoundRecap r)
        {
            var parts = new System.Collections.Generic.List<string>(4);
            if (r.Dashes > 0) parts.Add("DASHES SURVIVED " + r.Dashes);
            if (r.LungesSurvived > 0) parts.Add("LUNGES SURVIVED " + r.LungesSurvived);
            if (r.Breads > 0) parts.Add("BREAD " + r.Breads);
            if (r.Rage) parts.Add("RAGE REACHED");
            return string.Join("   ·   ", parts);
        }

        /// <summary>SHARE · PHOTO, centred as a group; SHARE drops out when there is nothing to share.</summary>
        void LayoutActionRow(bool share)
        {
            if (m_ShareButton.gameObject.activeSelf != share) m_ShareButton.gameObject.SetActive(share);
            const float gap = 24f;
            float wShare = 200f, wPhoto = 380f;
            float total = wPhoto + (share ? wShare + gap : 0f);
            float x = -total * 0.5f;
            if (share) { m_ShareButton.anchoredPosition = new Vector2(x + wShare * 0.5f, 0f); x += wShare + gap; }
            m_PhotoButton.anchoredPosition = new Vector2(x + wPhoto * 0.5f, 0f);
        }

        void OnPostToBoardPressed()
        {
            var mgr = GooseGameManager.Instance;
            if (mgr == null || m_NameInput == null) return;
            string name = CleanName(m_NameInput.Text, true);
            if (string.IsNullOrEmpty(name)) name = "SOMEONE";
            m_NameInput.Text = name;
            PlayerPrefs.SetString(PlayerNameKey, name);
            PlayerPrefs.Save();
            m_NameInput.Interactable = false;
            m_PostButton.interactable = false;
            m_PostLabel.Text = "POSTING...";
            int token = m_BoardToken;
            mgr.PostRunToBoard(name, rank =>
            {
                if (token != m_BoardToken || m_PostLabel == null) return; // a later round's card is up
                if (rank > 0) m_PostLabel.Text = "#" + rank + " ON THE BOARD";
                else { m_PostLabel.Text = "RETRY POST"; m_PostButton.interactable = true; m_NameInput.Interactable = true; }
            });
        }

        /// <summary>Board names: A-Z, 0-9, single spaces, 12 characters. While typing a trailing space is allowed.</summary>
        static string CleanName(string s, bool final)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s.ToUpperInvariant())
            {
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (c == ' ' && sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
            }
            string r = sb.ToString();
            if (final) r = r.Trim();
            return r.Length > 12 ? r.Substring(0, 12) : r;
        }

        public bool TextEntryFocused => m_NameInput != null && m_NameInput.IsFocused;
        public bool PhotoModeActive => m_Photo != null && m_Photo.gameObject.activeSelf;

        /// <summary>Hide every overlay for the captured frame. A CanvasGroup, not Canvas.enabled: the HUD is a nested canvas and would keep drawing.</summary>
        public void SetOverlayVisible(bool visible)
        {
            if (m_OverlayGroup != null) m_OverlayGroup.alpha = visible ? 1f : 0f;
        }

        public void ShowStamp(string line1, string line2)
        {
            if (m_StampGroup == null) return;
            m_StampLine1.Text = line1 ?? "";
            m_StampLine2.Text = line2 ?? "";
            m_StampGroup.alpha = 1f;
        }

        public void HideStamp()
        {
            if (m_StampGroup != null) m_StampGroup.alpha = 0f;
        }

        public void SetPhotoCameraReady(bool ready)
        {
            if (m_PhotoShutter != null) m_PhotoShutter.interactable = ready;
            if (m_PhotoHint != null) m_PhotoHint.Text = !ready ? "Opening camera…" :
                (GooseGameManager.Instance.SelfieMode ? "Hold it like a selfie. Tap the shutter." : "Walk around it. Tap the shutter.");
        }

        public void ShowPhotoMode(bool selfie = true)
        {
            HideAll();
            if (m_PhotoHint != null) m_PhotoHint.Text = selfie ? "Hold it like a selfie. Tap the shutter." : "Walk around it. Tap the shutter.";
            m_Photo.gameObject.SetActive(true);
        }

        /// <summary>Back from photo mode: the card returns with its numbers untouched.</summary>
        public void ShowResultsAgain()
        {
            m_Photo.gameObject.SetActive(false);
            m_GameOver.gameObject.SetActive(true);
        }

        public void UpdateHUD(float time, int honks, float bestTime)
        {
            UpdateHUD(time, honks, 0, bestTime);
        }

        int m_HudDeciseconds = -1, m_HudHonks = -1, m_HudDodges = -1;
        float m_HudBest = -1f;

        public void UpdateHUD(float time, int honks, int dodges, float bestTime)
        {
            if (!m_HudActive) return;
            // Text is only rebuilt when a shown value changes (the timer shows tenths): no per-frame strings, no per-frame mesh rebuilds.
            int ds = Mathf.FloorToInt(time * 10f);
            if (ds != m_HudDeciseconds) { m_HudDeciseconds = ds; m_Time.Text = ScoreManager.FormatTime(time); }
            if (honks != m_HudHonks || dodges != m_HudDodges)
            {
                m_HudHonks = honks; m_HudDodges = dodges;
                m_Honks.Text = dodges > 0 ? "HONKS " + honks + "  ·  DODGES " + dodges : "HONKS " + honks;
            }
            if (!Mathf.Approximately(bestTime, m_HudBest)) { m_HudBest = bestTime; m_Best.Text = "BEST " + ScoreManager.FormatTime(bestTime); }
        }

        static Sprite RingSprite
        {
            get
            {
                if (s_Ring != null) return s_Ring;
                var tex = ProceduralAssets.SoftRingTexture(256, 0.80f, 0.93f, 0.05f);
                s_Ring = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                return s_Ring;
            }
        }

        /// <summary>The bread button: shown only while a throw is available; scale, ring and opacity follow the goose's closeness.</summary>
        public bool BreadVisible => m_BreadButton != null && m_BreadButton.gameObject.activeInHierarchy && (m_OverlayGroup == null || m_OverlayGroup.alpha > 0f);

        public void SetBread(bool visible, float danger)
        {
            if (m_BreadButton == null) return;
            if (m_BreadButton.gameObject.activeSelf != visible) m_BreadButton.gameObject.SetActive(visible);
            if (!visible) return;
            m_BreadDanger = Mathf.Clamp01(danger);
        }

        public void SetBreadTexture(Texture texture)
        {
            if (m_BreadRoll == null) return;
            m_BreadRoll.texture = texture;
            m_BreadRoll.enabled = texture != null;
            if (m_BreadFlat != null) m_BreadFlat.enabled = texture == null;
        }

        /// <summary>What the goose just said, in the audience's reading position.</summary>
        public void ShowSubtitle(string text, float seconds)
        {
            if (m_SubtitlePill == null || string.IsNullOrEmpty(text)) return;
            bool results = (m_GameOver != null && m_GameOver.gameObject.activeSelf) || (m_Photo != null && m_Photo.gameObject.activeSelf);
            m_SubtitlePill.anchoredPosition = results ? new Vector2(0f, 470f) : new Vector2(-70f, 196f);
            m_Subtitle.Text = text;
            m_SubtitleUntil = Time.unscaledTime + Mathf.Max(0.8f, seconds);
            m_SubtitlePill.gameObject.SetActive(true);
            m_SubtitlePill.SetAsLastSibling();
            StartCoroutine(PopIn(m_SubtitlePill, 0.18f));
        }

        /// <summary>The line stopped early (interrupted, round over): the pill goes with it, so it never shows words nobody hears.</summary>
        public void HideSubtitle()
        {
            m_SubtitleUntil = 0f;
            if (m_SubtitlePill != null && m_SubtitlePill.gameObject.activeSelf) m_SubtitlePill.gameObject.SetActive(false);
        }

        public void SetDanger(float danger, bool gooseBehind, float angle, bool gooseVisible)
        {
            m_DangerLevel = danger;
            m_GooseBehind = gooseBehind;
            m_GooseAngle = angle;
            m_GooseVisible = gooseVisible;
            if (m_Locator != null) m_Locator.Danger = danger;
        }

        public void SetLocatorTarget(Transform target)
        {
            if (m_Locator != null) m_Locator.Target = target;
        }

        public void PulseLocator()
        {
            if (m_Locator != null) m_Locator.Pulse();
        }

        public void SetTrackingWarning(bool show, string text)
        {
            if (m_TrackingPill.gameObject.activeSelf != show) m_TrackingPill.gameObject.SetActive(show);
            if (show && !string.IsNullOrEmpty(text)) m_Tracking.Text = text;
        }

        public void ShowMessage(string text, float duration)
        {
            ShowMessage(text, duration, UITheme.White);
        }

        public void ShowMessage(string text, float duration, Color color)
        {
            foreach (var b in m_Bubbles) if (b.Rect.gameObject.activeSelf) b.Rect.gameObject.SetActive(false);
            m_Message.Text = text;
            m_Message.Color = color;
            m_Message.SetActive(true);
            m_MessageUntil = Time.unscaledTime + duration;
            m_Message.Rect.localRotation = Quaternion.identity;
            StartCoroutine(PopIn(m_Message.Rect, 0.2f));
        }

        /// <summary>Comic honk bubble near the goose (only while it is on screen).</summary>
        public void ShowHonk(Vector3 worldPos, string text, float duration, float scale = 1f)
        {
            var cam = Camera.main;
            if (cam == null || m_Safe == null) return;
            Vector2 size = m_Safe.rect.size;
            Vector3 vp = cam.WorldToViewportPoint(worldPos + Vector3.up * 0.55f);
            // Only when the goose is actually on screen; off-screen honks are carried by the locator pulse and the sound.
            if (!(vp.z > 0f && vp.x > 0.1f && vp.x < 0.9f && vp.y > 0.15f && vp.y < 0.85f)) return;
            // Never two texts at once: a centre message wins over the bubble.
            if (m_Message.GameObject != null && m_Message.GameObject.activeSelf) return;
            Vector2 pos = new Vector2((vp.x - 0.5f) * size.x, (vp.y - 0.5f) * size.y + 110f);

            // One bubble at a time: a fading previous honk would ghost behind the new one.
            Bubble b = m_Bubbles[0];
            foreach (var candidate in m_Bubbles)
            {
                if (candidate.Rect.gameObject.activeSelf) candidate.Rect.gameObject.SetActive(false);
            }

            b.Label.Text = text;
            b.Born = Time.unscaledTime;
            b.Until = b.Born + Mathf.Max(0.35f, duration);
            b.Tilt = UnityEngine.Random.Range(-6f, 6f);
            float w = Mathf.Clamp(120f + text.Length * 34f, 200f, 640f) * scale;
            b.Rect.sizeDelta = new Vector2(w, 110f * scale);
            b.Image.pixelsPerUnitMultiplier = 62f / (110f * scale);
            b.Label.FontSize = 58f * scale;
            b.Rect.anchoredPosition = pos;
            b.Rect.localRotation = Quaternion.Euler(0f, 0f, b.Tilt);
            b.Rect.gameObject.SetActive(true);
            b.Rect.SetAsLastSibling();
        }

        public void Flash(Color color, float duration)
        {
            m_FlashColor = color;
            m_FlashDuration = Mathf.Max(0.05f, duration);
            m_FlashUntil = Time.unscaledTime + m_FlashDuration;
            m_Flash.gameObject.SetActive(true);
        }

        public void Shake(float intensity)
        {
            m_Shake = Mathf.Max(m_Shake, Mathf.Clamp01(intensity));
        }

        IEnumerator PopIn(RectTransform rt, float duration)
        {
            if (rt == null) yield break;
            Quaternion baseRot = rt.localRotation;
            float t = 0f;
            while (t < duration && rt != null)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / duration);
                float s = Mathf.Lerp(0.92f, 1f, Mathf.Sin(k * Mathf.PI * 0.5f));
                rt.localScale = Vector3.one * s;
                yield return null;
            }
            if (rt != null)
            {
                rt.localScale = Vector3.one;
                rt.localRotation = baseRot;
            }
        }

        // ------------------------------------------------------------------ per-frame polish
        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            float t = Time.unscaledTime;

            var mgr = GooseGameManager.Instance;

            if (m_Scan.gameObject.activeSelf && m_ScanDots != null)
            {
                for (int i = 0; i < m_ScanDots.Length; i++)
                {
                    float p = 0.5f + 0.5f * Mathf.Sin(t * 3f - i * 0.8f);
                    m_ScanDots[i].localScale = Vector3.one * Mathf.Lerp(0.75f, 1.15f, p);
                }
                var c = m_ScanEgg.color;
                c.a = 0.22f + 0.08f * Mathf.Sin(t * 2f);
                m_ScanEgg.color = c;
                if (m_ScanSubtitle != null && mgr != null)
                {
                    float waited = t - m_ScanShownAt;
                    string hint = waited < 6f ? "Point it at the floor in front of you."
                        : (waited < 14f ? "Sweep slowly across the floor and nearby walls."
                        : (waited < 25f ? "Need more light or texture. Try a different patch of floor."
                        : "Still nothing? Find a brighter spot with a patterned floor, or go back."));
                    if (mgr.Scanner != null && mgr.Scanner.HasFloor) hint = "Floor found!";
                    if (m_ScanSubtitle.Text != hint) m_ScanSubtitle.Text = hint;
                    bool showBack = waited >= 25f && !(mgr.Scanner != null && mgr.Scanner.HasFloor);
                    if (m_ScanBack.gameObject.activeSelf != showBack) m_ScanBack.gameObject.SetActive(showBack);
                }
            }

            if (m_Place.gameObject.activeSelf && m_PlaceSubtitle != null && mgr != null && mgr.Placement != null)
            {
                string hint = mgr.Placement.HasReticle ? "Tap anywhere to place it here." : "Point the phone at the floor.";
                if (m_PlaceSubtitle.Text != hint) m_PlaceSubtitle.Text = hint;
            }

            if (m_Message.GameObject != null && m_Message.GameObject.activeSelf && t > m_MessageUntil)
                m_Message.SetActive(false);
            if (m_SubtitlePill != null && m_SubtitlePill.gameObject.activeSelf && t > m_SubtitleUntil)
                m_SubtitlePill.gameObject.SetActive(false);

            // Honk bubbles
            foreach (var b in m_Bubbles)
            {
                if (!b.Rect.gameObject.activeSelf) continue;
                float age = t - b.Born;
                if (t > b.Until) { b.Rect.gameObject.SetActive(false); continue; }
                float s = age < 0.12f ? Mathf.Lerp(0.6f, 1.06f, age / 0.12f) : (age < 0.22f ? Mathf.Lerp(1.06f, 1f, (age - 0.12f) / 0.1f) : 1f);
                b.Rect.localScale = Vector3.one * s;
                float fade = Mathf.Clamp01((b.Until - t) / 0.2f);
                var c = b.Image.color; c.a = UITheme.CreamPanel.a * fade; b.Image.color = c;
                b.Label.Alpha = fade;
                b.Rect.localRotation = Quaternion.Euler(0f, 0f, b.Tilt);
            }

            // Flash (radial, short)
            if (m_Flash.gameObject.activeSelf)
            {
                float remaining = m_FlashUntil - t;
                if (remaining <= 0f) m_Flash.gameObject.SetActive(false);
                else m_Flash.color = new Color(m_FlashColor.r, m_FlashColor.g, m_FlashColor.b, m_FlashColor.a * Mathf.Clamp01(remaining / m_FlashDuration));
            }

            // Shake (subtle: the camera feed never moves, so keep the HUD jitter small)
            if (m_Shake > 0f)
            {
                m_Shake = Mathf.MoveTowards(m_Shake, 0f, dt * 1.8f);
                m_ShakeRoot.anchoredPosition = m_ShakeBase + UnityEngine.Random.insideUnitCircle * (m_Shake * m_Shake * 9f);
            }
            else if (m_ShakeRoot.anchoredPosition != m_ShakeBase)
            {
                m_ShakeRoot.anchoredPosition = m_ShakeBase;
            }

            // Bread button: calm at a distance, the focus when the goose closes in.
            if (m_BreadButton != null && m_BreadButton.gameObject.activeSelf)
            {
                float focus = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((m_BreadDanger - 0.35f) / 0.5f));
                float pulse = 0.5f + 0.5f * Mathf.Sin(t * 5.5f);
                float scale = Mathf.Lerp(1f, 1.38f, focus) * (1f + 0.03f * focus * pulse);
                m_BreadButton.localScale = Vector3.Lerp(m_BreadButton.localScale, Vector3.one * scale, dt * 6f);
                var ring = m_BreadRing.color; ring.a = focus * Mathf.Lerp(0.25f, 0.85f, pulse); m_BreadRing.color = ring;
                var face = m_BreadFace.color; face.a = Mathf.Lerp(UITheme.GlassStrong.a * 0.75f, UITheme.GlassStrong.a, focus); m_BreadFace.color = face;
                if (m_BreadRoll != null && m_BreadRoll.enabled) { var rc = m_BreadRoll.color; rc.a = Mathf.Lerp(0.8f, 1f, focus); m_BreadRoll.color = rc; }
            }

            // Goose-o-meter
            if (m_HudActive)
            {
                float width = 760f * Mathf.Clamp01(m_DangerLevel);
                m_DangerFill.rectTransform.sizeDelta = new Vector2(width, 0f);
                m_DangerFill.color = Color.Lerp(UITheme.Yolk, UITheme.Danger, Mathf.Clamp01((m_DangerLevel - 0.4f) / 0.6f));

                // State word only; direction is the locator's job.
                string where;
                bool rage = mgr != null && mgr.Goose != null && mgr.Goose.Rage;
                if (m_DangerLevel < 0.05f) where = "GOOSE INCOMING";
                else if (rage && m_DangerLevel > 0.5f) where = "RAGE MODE";
                else if (m_DangerLevel >= 0.7f) where = m_GooseVisible ? "RIGHT THERE" : "RIGHT BEHIND YOU";
                else if (m_DangerLevel >= 0.4f) where = "CLOSING IN";
                else where = m_GooseVisible ? "IN SIGHT" : "HUNTING";
                m_DangerLabel.Text = where;
                m_DangerLabel.Color = m_DangerLevel > 0.7f ? UITheme.Danger : UITheme.Ink;

                bool messageUp = m_Message.GameObject != null && m_Message.GameObject.activeSelf;
                bool turnAround = m_DangerLevel > 0.5f && m_GooseBehind && !m_GooseVisible && !messageUp && (mgr == null || !mgr.IsPaused);
                if (m_TurnAround.GameObject.activeSelf != turnAround) m_TurnAround.SetActive(turnAround);
                if (turnAround) m_TurnAround.Alpha = 0.8f + 0.2f * Mathf.Sin(t * 8f);
            }
        }
    }
}
