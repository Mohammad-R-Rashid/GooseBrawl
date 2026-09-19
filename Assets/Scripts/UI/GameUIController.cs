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
        UILabel m_GoTitle, m_GoTime, m_GoScore, m_GoBest;
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
            UIFactory.Place(m_Message.Rect, new Vector2(0.5f, 0.5f), new Vector2(0f, 140f), new Vector2(980f, 320f));
            m_Message.SetActive(false);

            // Tracking hint pill
            var trackingPill = UIFactory.CreateGlassPill(m_Safe, "TrackingPill", new Vector2(760f, 88f));
            UIFactory.Place(trackingPill.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 300f), new Vector2(760f, 88f));
            m_Tracking = UIFactory.CreateLabel(trackingPill.transform, "Text", "", 34f, UITheme.Ink, false, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Stretch(m_Tracking.Rect, 10f);
            m_TrackingPill = trackingPill.rectTransform;
            m_TrackingPill.gameObject.SetActive(false);

#if UNITY_EDITOR
            m_MockHint = UIFactory.CreateLabel(m_Safe, "MockHint", "editor mock  ·  WASD move  ·  Q/E turn  ·  right-drag look  ·  SPACE start/steal/resume  ·  R restart", 22f, UITheme.Ink, false, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
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
        RectTransform TopPill(Transform parent, string title, string subtitle, out UILabel subtitleLabel)
        {
            subtitleLabel = null;
            float h = subtitle != null ? 164f : 108f;
            var pill = UIFactory.CreateGlassPill(parent, "TopPill", new Vector2(960f, h));
            UIFactory.Place(pill.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -36f), new Vector2(960f, h));
            var t = UIFactory.CreateLabel(pill.transform, "Title", title, 42f, UITheme.Ink, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
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

            BottomButton(m_Start, "StartButton", "START", () => GooseGameManager.Instance.OnStartPressed(), 240f);
            SmallGlassButton(m_Start, "HowToPlay", "HOW TO PLAY", new Vector2(0.5f, 0f), new Vector2(0f, 120f), new Vector2(420f, 84f),
                () => GooseGameManager.Instance.OnHowToPlayPressed(), out _);

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

            m_Time = UIFactory.CreateLabel(m_HUD, "Time", "0:00.0", 100f, UITheme.Ink, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(m_Time.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -26f), new Vector2(700f, 120f));
            UIFactory.AddShadow(m_Time, new Color(0f, 0f, 0f, 0.55f), new Vector2(0f, -4f));

            var honksPill = UIFactory.CreateGlassPill(m_HUD, "HonksPill", new Vector2(300f, 64f));
            UIFactory.Place(honksPill.rectTransform, new Vector2(0.5f, 1f), new Vector2(-160f, -150f), new Vector2(300f, 64f));
            m_Honks = UIFactory.CreateLabel(honksPill.transform, "Honks", "HONKS 0", 30f, UITheme.Ink, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Stretch(m_Honks.Rect, 8f);
            var bestPill = UIFactory.CreateGlassPill(m_HUD, "BestPill", new Vector2(300f, 64f));
            UIFactory.Place(bestPill.rectTransform, new Vector2(0.5f, 1f), new Vector2(160f, -150f), new Vector2(300f, 64f));
            m_Best = UIFactory.CreateLabel(bestPill.transform, "Best", "BEST 0:00.0", 30f, UITheme.InkMuted, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Stretch(m_Best.Rect, 8f);

            SmallGlassButton(m_HUD, "Pause", "II", new Vector2(1f, 1f), new Vector2(-24f, -24f), new Vector2(96f, 96f),
                () => GooseGameManager.Instance.TogglePause(), out var pauseLabel);
            pauseLabel.FontSize = 40f;

            // Goose-o-meter: slim glass pill, state word, 6 px bar.
            var meter = UIFactory.CreateGlassPill(m_HUD, "Meter", new Vector2(880f, 118f));
            UIFactory.Place(meter.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 48f), new Vector2(880f, 118f));
            m_MeterPill = meter.rectTransform;
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
            UIFactory.Place(m_TurnAround.Rect, new Vector2(0.5f, 0.5f), new Vector2(0f, 330f), new Vector2(980f, 140f));
            m_TurnAround.SetActive(false);
        }

        void BuildGameOver()
        {
            m_GameOver = MakeScreen("GameOverScreen");
            var dim = UIFactory.CreateImage(m_GameOver, "Dim", null, UITheme.Dim);
            UIFactory.Stretch(dim.rectTransform);

            m_GoTitle = UIFactory.CreateLabel(m_GameOver, "Title", "THE GOOSE\nGOT YOU", 118f, UITheme.Yolk, true, TextAnchor.MiddleCenter, UITheme.Brown, 0.28f, UIFont.Display, autoSize: true);
            UIFactory.Place(m_GoTitle.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -230f), new Vector2(980f, 340f));
            m_GoTitle.Rect.localRotation = Quaternion.Euler(0f, 0f, -3f);

            var card = UIFactory.CreatePanel(m_GameOver, "ResultCard", UITheme.CreamPanel);
            UIFactory.Place(card, new Vector2(0.5f, 0.5f), new Vector2(0f, -40f), new Vector2(820f, 440f));
            var cardImg = card.GetComponent<Image>();
            cardImg.pixelsPerUnitMultiplier = 0.55f;
            m_GoTime = UIFactory.CreateLabel(card, "Time", "You survived", 40f, UITheme.Brown, false);
            UIFactory.Place(m_GoTime.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -34f), new Vector2(780f, 60f));
            m_GoScore = UIFactory.CreateLabel(card, "Score", "0:00.0", 118f, UITheme.Brown, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(m_GoScore.Rect, new Vector2(0.5f, 1f), new Vector2(0f, -96f), new Vector2(780f, 150f));
            m_GoBest = UIFactory.CreateLabel(card, "Best", "HONKS 0   ·   BEST 0:00.0", 32f, UITheme.Muted, true, TextAnchor.MiddleCenter, default, 0f, UIFont.Hud);
            UIFactory.Place(m_GoBest.Rect, new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(780f, 60f));

            // Straddles the card's top-right corner: clear of the text, inside the screen on narrow phones.
            m_NewBestStamp = UIFactory.CreatePill(card, "NewBest", UITheme.Danger, new Vector2(300f, 78f), false);
            UIFactory.Place(m_NewBestStamp.rectTransform, new Vector2(1f, 1f), new Vector2(-40f, 36f), new Vector2(300f, 78f));
            m_NewBestStamp.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 7f);
            var stampLabel = UIFactory.CreateLabel(m_NewBestStamp.transform, "Text", "NEW BEST!", 40f, UITheme.White);
            UIFactory.Stretch(stampLabel.Rect, 6f);

            BottomButton(m_GameOver, "RunAgainButton", "RUN AGAIN", () => GooseGameManager.Instance.OnRunAgainPressed(), 240f);
            SmallGlassButton(m_GameOver, "MoveNest", "MOVE NEST", new Vector2(0.5f, 0f), new Vector2(0f, 120f), new Vector2(420f, 84f),
                () => GooseGameManager.Instance.OnMoveNestPressed(), out _);
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
            StartCoroutine(PopIn(m_MeterPill, 0.3f));
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
            HideAll();
            m_GameOver.gameObject.SetActive(true);
            m_GoTitle.Text = title;
            m_GoTime.Text = "You survived";
            m_GoScore.Text = ScoreManager.FormatTime(time);
            m_GoBest.Text = "HONKS " + score + "   ·   BEST " + ScoreManager.FormatTime(bestTime);
            m_NewBestStamp.gameObject.SetActive(newBest);
            StartCoroutine(PopIn(m_GoTitle.Rect, 0.4f));
            if (newBest) StartCoroutine(PopIn(m_NewBestStamp.rectTransform, 0.5f));
        }

        public void UpdateHUD(float time, int honks, float bestTime)
        {
            if (!m_HudActive) return;
            m_Time.Text = ScoreManager.FormatTime(time);
            m_Honks.Text = "HONKS " + honks;
            m_Best.Text = "BEST " + ScoreManager.FormatTime(bestTime);
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
