using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace GooseBrawl
{
    /// <summary>
    /// Owns the round flow: Boot -> Scan -> PlaceNest -> EggReady -> EggStolen -> Chasing/Danger/JumpAttack -> GameOver.
    /// All other systems are small services this manager wires together.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class GooseGameManager : MonoBehaviour
    {
        public static GooseGameManager Instance { get; private set; }

        [Header("Mode")]
        [Tooltip("USE_MOCK_AR: when running inside the Unity Editor use a mock camera + floor instead of AR Foundation.")]
        public bool useMockARInEditor = true;
        [Tooltip("Force the mock world in a device build (no AR).")]
        public bool forceMockARInBuild = false;

        [Header("Prefabs")]
        public GameObject goosePrefab;

        [Header("Flow tuning")]
        [Tooltip("Legacy: how far behind the player the goose appears when the fly-in entrance is disabled (meters).")]
        public float spawnDistance = 3f;
        [Tooltip("Cinematic entrance: the goose flies in along the player's line of sight, lands and glares until seen.")]
        public bool flyInEntrance = true;
        [Tooltip("Minimum distance from the player at which the goose lands (meters).")]
        public float minLandingDistance = 2.4f;
        public float maxLandingDistance = 3.2f;
        [Tooltip("Below this distance the HUD switches to the Danger state.")]
        public float dangerDistance = 1.8f;
        [Tooltip("Minimum seconds the scan screen stays visible so the player actually scans.")]
        public float minimumScanTime = 1.5f;
        [Tooltip("Repeat rounds play the steal beat this much faster (and it can be tapped through).")]
        public float repeatBeatSpeed = 0.65f;

        [Header("Carry (the egg has inertia in your hand: move or tilt the phone and it slides off)")]
        [Tooltip("How far (m) the egg can slide in the hand before it rolls off.")]
        public float palmRadius = 0.07f;
        [Tooltip("Spring pulling the egg back to the palm centre (1/s^2) and its damping (1/s).")]
        public float palmSpring = 40f;
        public float palmDamping = 7f;
        [Tooltip("How much phone acceleration (m/s^2) pushes the egg.")]
        public float accelerationGain = 0.85f;
        [Tooltip("Phone acceleration below this (m/s^2) is tracking noise (gated: nothing below, the full push above).")]
        public float accelerationDeadZone = 0.6f;
        [Tooltip("How much tilting the phone lets gravity pull the egg.")]
        public float tiltGain = 1f;
        [Tooltip("Sideways gravity (rolling the phone) below this (m/s^2, about 10 degrees) is free (gated).")]
        public float tiltDeadZoneRoll = 1.8f;
        [Tooltip("Forward gravity (pitching the phone down to look at the floor) below this (m/s^2, about 24 degrees) is free; only the excess pulls.")]
        public float tiltDeadZonePitch = 4f;
        [Tooltip("Pushes do not count for this long after the egg lands in the hand (the drop is the beat, not a balancing game; this just stops it going on the first frame).")]
        public float carryGraceSeconds = 1f;
        [Tooltip("Repeat rounds multiply the pushes by this (they also skip the steal beat).")]
        public float repeatCarryDifficulty = 1.4f;
        [Tooltip("Standing perfectly still: after this many seconds the egg starts creeping off anyway.")]
        public float stillCreepAfter = 10f;

        [Header("Rounds")]
        [Tooltip("Survive this long and the goose gives up (the win). Demo-tunable.")]
        public float outlastSeconds = 45f;
        [Tooltip("A missed lunge counts as a DODGE when the player moved at least this far during it (meters).")]
        public float dodgeDistance = 0.35f;
        [Tooltip("Bread throws per round (one: it is the save-me, not a weapon).")]
        public int breadPerRound = 1;

        public GooseGameState State { get; private set; } = GooseGameState.Boot;
        public event Action<GooseGameState> StateChanged;

        /// <summary>USE_MOCK_AR resolved for this run.</summary>
        public static bool UseMockAR { get; private set; }

        // Services (auto resolved in Awake)
        public ARBootstrapper AR { get; private set; }
        public ARSurfaceScanner Scanner { get; private set; }
        public ARPlacementController Placement { get; private set; }
        public AREnvironmentMeshController Environment { get; private set; }
        public PlayerTracker Player { get; private set; }
        public ScoreManager Score { get; private set; }
        public AudioManager Audio { get; private set; }
        public HapticsService Haptics { get; private set; }
        public DangerFeedbackController Danger { get; private set; }
        public GameUIController UI { get; private set; }
        public MockARController Mock { get; private set; }
        public MaterialLibrary Materials { get; private set; }
        public ChaosAudioController Chaos { get; private set; }
        public CinematicLookController Look { get; private set; }
        public PerfProbe Perf { get; private set; }
        public PerfBenchmark Bench { get; private set; }
        public GooseBrainClient Brain { get; private set; }
        public GoosePersona Persona { get; private set; }
        public YellDetector Yell { get; private set; }
        public GooseShareService Share { get; private set; }

        public NestController Nest { get; private set; }
        public GooseChaseController Goose { get; private set; }
        public float FloorY { get; private set; }
        public bool ChaseActive => State == GooseGameState.Chasing || State == GooseGameState.Danger || State == GooseGameState.JumpAttack;
        public bool IsPaused { get; private set; }
        /// <summary>The goose exists and is on its way (fly-in / glare) or chasing: the locator should be live.</summary>
        public bool GooseLive => Goose != null && (State == GooseGameState.EggStolen || ChaseActive);
        public int RoundsThisSession { get; private set; }
        /// <summary>Automation: skip the first-run coaching card once (benchmark / smoke).</summary>
        public bool SkipCoachingOnce { get; set; }
        public int DodgeCount { get; private set; }
        /// <summary>Lunges this round that did not catch you (dodged or plain missed).</summary>
        public int LungesSurvived { get; private set; }
        /// <summary>The last round ended with the goose giving up.</summary>
        public bool LastRoundWon { get; private set; }
        float m_BenchHoldSince = -1f;
        bool m_Taunted;
        Vector3 m_LungeStartPos;
        GooseBrainClient.LineResult m_IntroLine;
        BreadController m_Bread;
        BreadIconRenderer m_BreadIcon;
        public int BreadsThrown { get; private set; }
        static readonly Vector3 k_ThrowHandLocal = new Vector3(0.06f, -0.14f, 0.44f);
        class RunSnapshot { public float Seconds; public bool Won; public string Goose; public int Honks, Dodges; }
        RunSnapshot m_LastRun;
        byte[] m_PendingYellWav;
        bool m_Shutter;

        Coroutine m_FlowRoutine;
        ShadowCatcher m_GooseCatcher, m_NestCatcher;
        GameObject m_NestProbe;
        bool m_SkipRequested;
        bool m_CoachingShown;

        static readonly string[] k_FirstLines = { "OOPS.\nTHERE GOES BREAKFAST.", "BUTTERFINGERS.", "WHOOPS.\nNO BREAKFAST.", "OH NO." };
        static readonly string[] k_SecondLines = { "THE GOOSE HEARD THAT.", "IT HEARD THAT.", "SOMETHING IS HONKING.", "THE GOOSE HAS NOTICED." };
        static readonly string[] k_LossTitles = { "{0}\nGOT YOU", "GOOSED\nBY {0}", "{0} 1\nYOU 0", "HONKED\nTO DEATH", "PECKED\nBY {0}.", "NO BREAKFAST\nFOR YOU" };
        static readonly string[] k_WinTitles = { "YOU OUTLASTED\n{0}", "{0}\nGAVE UP", "BREAKFAST\nIS SAFE." };

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            UseMockAR = Application.isEditor ? useMockARInEditor : forceMockARInBuild;

            Application.targetFrameRate = 60;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            AR = Resolve<ARBootstrapper>();
            Scanner = Resolve<ARSurfaceScanner>();
            Placement = Resolve<ARPlacementController>();
            Environment = Resolve<AREnvironmentMeshController>();
            Player = Resolve<PlayerTracker>();
            Score = Resolve<ScoreManager>();
            Audio = Resolve<AudioManager>();
            Haptics = Resolve<HapticsService>();
            Danger = Resolve<DangerFeedbackController>();
            UI = Resolve<GameUIController>();
            Materials = Resolve<MaterialLibrary>();
            Chaos = Resolve<ChaosAudioController>();
            Look = Resolve<CinematicLookController>();
            Perf = Resolve<PerfProbe>();
            Bench = Resolve<PerfBenchmark>();
            Brain = Resolve<GooseBrainClient>();
            Persona = Resolve<GoosePersona>();
            Yell = Resolve<YellDetector>();
            Share = Resolve<GooseShareService>();
            Mock = Resolve<MockARController>(optional: true);
            if (UseMockAR && Mock == null) Mock = gameObject.AddComponent<MockARController>();
        }

        T Resolve<T>(bool optional = false) where T : Component
        {
            var c = GetComponentInChildren<T>(true);
            if (c == null) c = FindAnyObjectByType<T>(FindObjectsInactive.Include);
            if (c == null && !optional) c = gameObject.AddComponent<T>();
            return c;
        }

        void Start()
        {
            AR.Initialize(UseMockAR);
            if (UseMockAR && Mock != null) Mock.Build(Environment.EnvironmentLayer);
            Placement.Active = false;
            Placement.Placed += OnNestPlaced;
            State = GooseGameState.Boot;
            StateChanged?.Invoke(State);
            UI.ShowStart(Score.BestScore, Score.BestTime, UseMockAR);
            m_BreadIcon = BreadIconRenderer.Create(Materials);
            UI.SetBreadTexture(m_BreadIcon != null ? m_BreadIcon.Texture : null);
            StartCoroutine(PrewarmGameplayAssets());
            if (Yell != null)
            {
                Yell.Yelled += OnPlayerYelled;
                Yell.YellAudioReady += OnYellAudio;
                Yell.TranscriptReady += OnTranscript;
            }
        }

        void OnDestroy()
        {
            Time.timeScale = 1f;
            if (Placement != null) Placement.Placed -= OnNestPlaced;
            if (Instance == this) Instance = null;
        }

        public bool GameplayAssetsWarm { get; private set; }

        IEnumerator PrewarmGameplayAssets()
        {
            // Keep title/scan frames responsive: generate one cached asset per frame, before it is needed.
            yield return null;
            ProceduralAssets.SpeckleTexture(512);
            yield return null;
            ProceduralAssets.FeatherTexture();
            yield return null;
            ProceduralAssets.EggMesh("EggMesh", EggController.EggWidth, EggController.EggHeight);
            yield return null;
            if (Materials != null)
            {
                // Asset-backed materials are already loaded; only exercise the procedural fallbacks when necessary.
                _ = Materials.Bread;
                _ = Materials.Crumbs;
                _ = Materials.Dust;
                if (Materials.Nest.GetTexture("_BaseMap") == null)
                {
                    ProceduralAssets.TwigStripTexture();
                    yield return null;
                    ProceduralAssets.TwigNormalTexture();
                }
            }
            if (Share != null) Share.PrepareCapture();
            yield return null;
            if (Nest == null) Nest = NestController.Create(Materials);
            yield return Nest.Egg.PrepareCrack(Materials);
            GameplayAssetsWarm = true;
        }

        void Update()
        {
            if (Player != null && AR != null) Player.TrackingGood = AR.IsTrackingGood();

            if (!IsPaused && (State == GooseGameState.Chasing || State == GooseGameState.Danger))
            {
                if (Goose != null)
                    SetState(Goose.DistanceToPlayer < dangerDistance ? GooseGameState.Danger : GooseGameState.Chasing);
            }
            if (ChaseActive)
            {
                UI.UpdateHUD(Score.SurvivalTime, Goose != null ? Goose.HonkCount : 0, DodgeCount, Score.BestTime);
                if (!IsPaused && Goose != null)
                {
                    if (!m_Taunted && Score.SurvivalTime >= 10f && Goose.Voice != null)
                    {
                        m_Taunted = true;
                        Goose.Voice.SayBeat(GooseLines.Beat.Taunt10, EventPayload());
                    }
                    if (Score.SurvivalTime >= outlastSeconds && !(Bench != null && Bench.Running)) OnGooseGaveUp();
                }
                bool breadVisible = Goose != null && !Goose.FlyingIn && !Goose.Glaring && m_Bread == null && !IsPaused && BreadsThrown < breadPerRound;
                UI.SetBread(breadVisible, Goose != null ? Goose.Danger01 : 0f);
            }

            bool showTracking = !Player.TrackingGood && State != GooseGameState.Boot && State != GooseGameState.GameOver && !IsPaused;
            UI.SetTrackingWarning(showTracking, showTracking ? AR.TrackingHint() : null);

            if (State == GooseGameState.EggStolen && RoundsThisSession > 0 && GameInput.TryGetTap(out var tap) && !GameInput.IsPointerOverUI(tap))
                m_SkipRequested = true;

            // Hidden benchmark trigger: three fingers held on the title screen for 1.5 s.
            if (State == GooseGameState.Boot && Bench != null && !Bench.Running)
            {
                if (GameInput.TouchCount() >= 3)
                {
                    if (m_BenchHoldSince < 0f) m_BenchHoldSince = Time.unscaledTime;
                    else if (Time.unscaledTime - m_BenchHoldSince > 1.5f) { m_BenchHoldSince = -1f; Bench.Begin(); }
                }
                else m_BenchHoldSince = -1f;
            }

            if (UseMockAR && !UI.TextEntryFocused)
            {
                if (GameInput.RestartPressed() && State == GooseGameState.GameOver) OnRunAgainPressed();
                if (GameInput.BreadPressed()) OnBreadPressed();
                if (GameInput.YellPressed() && Yell != null && ChaseActive) Yell.SimulateYell();
                if (GameInput.NamePressed() && Yell != null && ChaseActive) Yell.SimulateYell(Persona.Name);
                if (GameInput.SorryPressed() && Yell != null && ChaseActive) Yell.SimulateYell("sorry please stop");
                if (GameInput.PhotoPressed() && State == GooseGameState.GameOver) { if (UI.PhotoModeActive) OnShutterPressed(); else OnPhotoPressed(); }
                if (GameInput.SpacePressed())
                {
                    if (State == GooseGameState.Boot) OnStartPressed();
                    else if (State == GooseGameState.EggReady) OnStealEggPressed();
                    else if (IsPaused) ResumeGame();
                }
            }
        }

        public void SetState(GooseGameState s)
        {
            if (State == s) return;
            State = s;
            StateChanged?.Invoke(s);
        }

        // ------------------------------------------------------------------ UI hooks

        public void OnStartPressed()
        {
            if (State != GooseGameState.Boot) return;
            Audio.PlayStart();
            Haptics.Light();
            Persona.EnsureFallback(Score.GamesPlayed);
            BeginBrainSession();
            if (Yell != null) Yell.WarmUpPermission();
            // No coaching card: the first run is the surprise. The scan and place pills say what to do; the goose says the rest.
            SkipCoachingOnce = false;
            StartFlow(ScanRoutine());
        }

        public void OnHowToPlayPressed()
        {
            if (State != GooseGameState.Boot) return;
            m_CoachingShown = true;
            UI.ShowCoaching(() => UI.ShowStart(Score.BestScore, Score.BestTime, UseMockAR));
        }

        IEnumerator ScanRoutine()
        {
            SetState(GooseGameState.ScanEnvironment);
            UI.ShowScan();
            AR.StartScanning();
            Scanner.BeginScan();
            float t = 0f;
            while (!Scanner.HasFloor || t < minimumScanTime)
            {
                t += Time.deltaTime;
                yield return null;
            }
            Scanner.EndScan();
            Haptics.Transient(0.45f, 0.4f);
            BeginPlacement();
        }

        public void BeginPlacement()
        {
            SetState(GooseGameState.PlaceNest);
            UI.ShowPlace();
            Environment.SetPlaneVisualization(true);
            Placement.Active = true;
            if (Yell != null) Yell.WarmUpPermission();
            StartCoroutine(PrepareGoose());
        }

        /// <summary>From the scan screen when the floor never shows up: back to the title.</summary>
        public void ReturnToTitle()
        {
            if (m_FlowRoutine != null) StopCoroutine(m_FlowRoutine);
            m_FlowRoutine = null;
            Scanner.EndScan();
            Placement.Active = false;
            Environment.SetPlaneVisualization(false);
            SetState(GooseGameState.Boot);
            if (Yell != null) Yell.ReleaseMicrophone();
            UI.ShowStart(Score.BestScore, Score.BestTime, UseMockAR);
        }

        void OnNestPlaced(Pose pose)
        {
            if (State != GooseGameState.PlaceNest) return;
            Placement.Active = false;
            FloorY = pose.position.y;
            Player.FloorY = FloorY;

            if (Nest == null) Nest = NestController.Create(Materials);
            Nest.PlaceAt(pose.position, Player.Position);
            Environment.SetPlaneVisualization(false);

            // Ground the scene: key light over the player's shoulder, a shadow catcher under the nest, room reverb, a reflection probe.
            if (Look != null) Look.OrientKeyLight(Player.FlatForward);
            EnsureNestGrounding(pose.position);

            Audio.PlayPlace(pose.position);
            Haptics.Play(HapticsService.Pattern.Landing, 0.7f);
            SetState(GooseGameState.EggReady);
            UI.ShowEgg();
        }

        void EnsureNestGrounding(Vector3 nestPos)
        {
            var catcherMat = Materials != null ? Materials.ShadowCatcher : null;
            if (catcherMat != null)
            {
                if (m_NestCatcher == null) m_NestCatcher = ShadowCatcher.Create("NestShadowCatcher", 3f, catcherMat, FloorY, null);
                m_NestCatcher.floorY = FloorY;
                m_NestCatcher.transform.position = nestPos;
                m_NestCatcher.Snap();
            }
            Audio.CreateRoomReverb(nestPos);
            if (!UseMockAR && AR.enableEnvironmentProbes && AR.environmentProbeManager != null && AR.environmentProbeManager.enabled)
            {
                try
                {
                    if (m_NestProbe == null)
                    {
                        m_NestProbe = new GameObject("NestEnvironmentProbe");
                        var rp = m_NestProbe.AddComponent<ReflectionProbe>();
                        rp.size = new Vector3(3f, 2.5f, 3f);
                        rp.boxProjection = true;
                        m_NestProbe.AddComponent<AREnvironmentProbe>();
                    }
                    m_NestProbe.transform.position = nestPos + Vector3.up * 0.6f;
                }
                catch (Exception e) { GooseLog.Warn("Nest environment probe skipped: " + e.Message); }
            }
        }

        public void OnEggTapped()
        {
            if (State == GooseGameState.EggReady) OnStealEggPressed();
        }

        public void OnStealEggPressed()
        {
            if (State != GooseGameState.EggReady) return;
            StartFlow(StealRoutine());
        }

        /// <summary>Automation: skip ahead through the steal beat (benchmark / smoke).</summary>
        public void RequestSkip() => m_SkipRequested = true;

        /// <summary>Wait, but in repeat rounds a tap anywhere skips ahead.</summary>
        IEnumerator Beat(float seconds)
        {
            float speed = RoundsThisSession > 0 ? repeatBeatSpeed : 1f;
            float t = 0f;
            while (t < seconds * speed)
            {
                if (m_SkipRequested) yield break;
                t += Time.deltaTime;
                yield return null;
            }
        }

        IEnumerator StealRoutine()
        {
            m_SkipRequested = false;
            float speed = RoundsThisSession > 0 ? repeatBeatSpeed : 1f;
            SetState(GooseGameState.EggStolen);
            UI.ShowStealing();
            Audio.PlayEggPickup();
            Haptics.Transient(0.5f, 0.7f);
            UI.ShowMessage("BREAKFAST TIME!", 1.0f * speed, UITheme.Yolk);
            yield return Nest.StealEgg(Player.Cam);

            // Carry it: the grip drains with time and with how much the phone moves. The slip is on you.
            UI.ShowCarry();
            UI.ShowMessage("GOT IT.\nDON'T DROP IT.", 1.3f * speed, UITheme.Yolk);
            yield return CarryRoutine();
            UI.HideCarry();

            // The fumble: the egg slips, falls (in slow motion so you can see it) and cracks. That noise wakes the goose.
            UI.ShowMessage("NO NO NO", 1.6f * speed);
            Time.timeScale = 0.55f;
            if (Look != null) Look.SetSlowMotion(true);
            yield return Nest.DropEgg(Player.Cam, FloorY, Materials, m_LastCarryVelocity);
            Time.timeScale = 1f;
            if (Look != null) { Look.SetSlowMotion(false); Look.Impact(0.3f); }
            Audio.PlayEggCrack(Nest.Egg.CrackPosition);
            Haptics.Play(HapticsService.Pattern.EggCrack);
            UI.Flash(new Color(1f, 0.95f, 0.8f, 0.18f), 0.2f);
            UI.ShowMessage(k_FirstLines[UnityEngine.Random.Range(0, k_FirstLines.Length)], 1.3f * speed);
            yield return Beat(1.4f);

            Vector3 farAhead = Player.FlatPosition + Player.FlatForward * 6f;
            Audio.PlayHonkAt(flyInEntrance ? farAhead : Player.FlatPosition - Player.FlatForward * spawnDistance, AudioManager.HonkKind.Dramatic);
            Haptics.Transient(0.6f, 0.4f);
            UI.ShowMessage(k_SecondLines[UnityEngine.Random.Range(0, k_SecondLines.Length)], 1.4f * speed);
            yield return Beat(1.3f);
            SpawnGoose();
        }

        /// <summary>
        /// The egg is a mass on a springy palm. Phone acceleration (walking, stops, turns) and tilt push it
        /// around; past the palm radius it rolls off with the velocity it has. Standing perfectly still keeps
        /// it safe until the creep kicks in. A hold vibration follows how close it is to slipping.
        /// </summary>
        IEnumerator CarryRoutine()
        {
            var cam = Player.Cam;
            var egg = Nest != null ? Nest.Egg : null;
            Vector3 offset = Vector3.zero, offVel = Vector3.zero;   // camera-local, metres
            int warnLevel = 0;
            Vector3 prevPos = cam != null ? cam.transform.position : Vector3.zero;
            Vector3 velEma = Vector3.zero, prevVelEma = Vector3.zero, accEma = Vector3.zero;
            float still = 0f, elapsed = 0f;
            bool hinted = false;
            Vector3 creepDir = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) * Vector3.forward;
            float repeat = RoundsThisSession > 0 ? repeatCarryDifficulty : 1f;
            m_LastCarryVelocity = Vector3.zero;
            Haptics.HoldStart(0.14f, 0.25f);

            while (true)
            {
                if (m_SkipRequested) break;
                float dt = Time.deltaTime;
                if (dt <= 0f || cam == null) { yield return null; continue; }
                elapsed += dt;

                // Phone motion: velocity (smoothed against pose jitter) and its change.
                Vector3 pos = cam.transform.position;
                Vector3 vel = (pos - prevPos) / dt;
                prevPos = pos;
                velEma = Vector3.Lerp(velEma, vel, 1f - Mathf.Exp(-dt / 0.08f));
                Vector3 acc = (velEma - prevVelEma) / dt;
                prevVelEma = velEma;
                accEma = Vector3.Lerp(accEma, acc, 1f - Mathf.Exp(-dt / 0.06f));
                Vector3 accLocal = cam.transform.InverseTransformDirection(accEma);
                accLocal.y = 0f;
                if (accLocal.magnitude < accelerationDeadZone) accLocal = Vector3.zero; // tracking noise

                // Tilt: rolling the phone spills quickly; pitching it down is more generous because looking at the floor is normal AR posture.
                Vector3 downLocal = cam.transform.InverseTransformDirection(Vector3.down);
                float roll = downLocal.x * 9.81f, pitch = downLocal.z * 9.81f;
                Vector3 tiltPull = new Vector3(Mathf.Abs(roll) < tiltDeadZoneRoll ? 0f : roll, 0f, SoftDeadZone(pitch, tiltDeadZonePitch));

                // Creep when the player refuses to move.
                if (velEma.magnitude < 0.06f) still += dt; else still = 0f;
                Vector3 creep = elapsed > stillCreepAfter / repeat ? creepDir * (0.5f + 0.35f * (elapsed - stillCreepAfter / repeat)) : Vector3.zero;

                // The first moments are free (the egg has just landed in the hand); after that every push counts.
                Vector3 push = elapsed < carryGraceSeconds ? Vector3.zero : -accLocal * accelerationGain * repeat + tiltPull * tiltGain * repeat + creep;
                Vector3 force = push - offset * palmSpring - offVel * palmDamping;
                offVel += force * dt;
                offset += offVel * dt;
                float w = Mathf.Clamp01(offset.magnitude / palmRadius);

                if (egg != null)
                {
                    egg.HandOffset = offset;
                    egg.HandShake = w;
                }
                UI.SetGrip(1f - w);
                Haptics.HoldUpdate(0.12f + 0.7f * w * w, 0.2f + 0.6f * w);

                if (!hinted && still > 3f && elapsed > 3f)
                {
                    hinted = true;
                    UI.ShowMessage("GO ON. WALK.", 1.1f, UITheme.Yolk);
                }
                // Stepped warnings on the way out (28 / 45 / 65 / 85 %), each firing once, re-armed with hysteresis.
                while (warnLevel < k_GripWarn.Length && w >= k_GripWarn[warnLevel]) { warnLevel++; Haptics.Transient(0.3f + 0.15f * warnLevel, 0.35f + 0.15f * warnLevel); }
                while (warnLevel > 0 && w < k_GripWarn[warnLevel - 1] - 0.1f) warnLevel--;

                if (offset.magnitude > palmRadius)
                {
                    // It rolls off with the velocity it had in the hand plus the hand's own motion.
                    m_LastCarryVelocity = velEma + cam.transform.TransformDirection(offVel) + Vector3.down * 0.15f;
                    break;
                }
                yield return null;
            }
            Haptics.HoldStop();
            Haptics.Transient(0.8f, 0.9f); // the slip
            if (egg != null) { egg.HandShake = 1f; }
        }

        Vector3 m_LastCarryVelocity;

        static readonly float[] k_GripWarn = { 0.28f, 0.45f, 0.65f, 0.85f };

        static float SoftDeadZone(float v, float deadZone) => Mathf.Abs(v) <= deadZone ? 0f : v - Mathf.Sign(v) * deadZone;

        GooseChaseController m_PreparedGoose;
        Renderer[] m_PreparedRenderers;
        bool m_PreparingGoose;
        public bool GoosePrepared => m_PreparedGoose != null && m_PreparedGoose.Visual.EffectsReady;

        IEnumerator PrepareGoose()
        {
            if (m_PreparingGoose || m_PreparedGoose != null) yield break;
            m_PreparingGoose = true;
            yield return null;
            GameObject prepared = goosePrefab != null ? Instantiate(goosePrefab) : GoosePlaceholderFactory.CreateRuntimeGoose(Materials);
            prepared.name = "Goose";
            prepared.transform.position = Vector3.down * 100f;
            m_PreparedGoose = prepared.GetComponent<GooseChaseController>();
            if (m_PreparedGoose == null) m_PreparedGoose = prepared.AddComponent<GooseChaseController>();
            m_PreparedGoose.Initialize(this);
            m_PreparedGoose.Movement.Airborne = true;
            m_PreparedRenderers = prepared.GetComponentsInChildren<Renderer>();
            foreach (var r in m_PreparedRenderers) r.forceRenderingOff = true;
            m_PreparingGoose = false;
        }

        void SpawnGoose()
        {
            if (Goose != null) Destroy(Goose.gameObject);
            if (m_GooseCatcher != null) Destroy(m_GooseCatcher.gameObject);
            GameObject go;
            if (m_PreparedGoose != null)
            {
                Goose = m_PreparedGoose;
                go = Goose.gameObject;
                foreach (var r in m_PreparedRenderers) if (r != null) r.forceRenderingOff = false;
                m_PreparedGoose = null;
                m_PreparedRenderers = null;
            }
            else
            {
                go = goosePrefab != null ? Instantiate(goosePrefab) : GoosePlaceholderFactory.CreateRuntimeGoose(Materials);
                go.name = "Goose";
                Goose = go.GetComponent<GooseChaseController>();
                if (Goose == null) Goose = go.AddComponent<GooseChaseController>();
                Goose.Initialize(this);
            }
            Goose.Movement.Airborne = false;
            var catcherMat = Materials != null ? Materials.ShadowCatcher : null;
            if (catcherMat != null) m_GooseCatcher = ShadowCatcher.Create("GooseShadowCatcher", 6f, catcherMat, FloorY, go.transform);
            UI.SetLocatorTarget(Goose.transform);

            // Always relative to where the phone is pointing NOW, so the goose is in the view cone from the first frame.
            Vector3 fwd = Player.FlatForward;
            Vector3 playerFlat = Player.FlatPosition;

            if (!flyInEntrance)
            {
                Vector3 desired = playerFlat - fwd * spawnDistance;
                Vector3 spawn = Goose.Avoidance.FindFreeSpawn(desired, playerFlat, spawnDistance, FloorY);
                Goose.Spawn(spawn, fwd, FloorY);
                BeginChase();
                return;
            }

            // Landing spot: in the view cone, on free floor, with a clear line of sight from the phone (never inside a wall or a couch).
            Vector3 dir = fwd;
            float landDist = Mathf.Lerp(minLandingDistance, maxLandingDistance, 0.5f);
            if (Nest != null)
            {
                Vector3 toNest = Nest.transform.position - playerFlat;
                toNest.y = 0f;
                float nestDist = toNest.magnitude;
                if (nestDist > 0.3f && Vector3.Angle(fwd, toNest) < 30f)
                {
                    dir = toNest / nestDist;
                    landDist = nestDist + 0.6f;
                }
            }
            landDist = Mathf.Clamp(landDist, minLandingDistance, maxLandingDistance);
            Vector3 landing = FindLandingSpot(playerFlat, dir, landDist);

            // Fly-in start: far along the same line, but never behind a wall or inside furniture; lower the flight if the room is tight.
            Vector3 flyDir = landing - playerFlat; flyDir.y = 0f; flyDir.Normalize();
            float flyHeight = Goose.flyHeight;
            Vector3 start = FindFlyInStart(landing, flyDir, ref flyHeight);

            UI.ShowMessage(Persona.Returning ? "THIS MEANS\nWAR." : "HERE COMES\n" + Persona.Name + ".", 2f, UITheme.Yolk);
            Danger.SpawnEffect();
            Goose.FlyIn(start, landing, FloorY, flyHeight, BeginChase);
        }

        bool LandingOk(Vector3 p)
        {
            if (!Goose.Avoidance.IsPositionFree(p)) return false;
            if (!Goose.Avoidance.FloorOk(p)) return false;
            if (Environment.EnvironmentMask.value != 0)
            {
                Vector3 eye = Player.Position;
                Vector3 chest = p + Vector3.up * 0.45f;
                if (Physics.Linecast(eye, chest, Environment.EnvironmentMask, QueryTriggerInteraction.Ignore)) return false;
            }
            return true;
        }

        /// <summary>Closest-to-ideal spot in front of the player that is free, on the floor and visible from the phone.</summary>
        Vector3 FindLandingSpot(Vector3 playerFlat, Vector3 dir, float ideal)
        {
            float[] angles = { 0f, -15f, 15f, -30f, 30f, -45f, 45f };
            foreach (float a in angles)
            {
                Vector3 d = Quaternion.AngleAxis(a, Vector3.up) * dir;
                for (float dist = ideal; dist >= minLandingDistance - 0.6f; dist -= 0.3f)
                {
                    Vector3 p = playerFlat + d * dist;
                    p.y = FloorY;
                    if (LandingOk(p)) return p;
                }
            }
            // Nothing in the cone: let the wide search pick a free spot anywhere around the player.
            Vector3 fallback = Goose.Avoidance.FindFreeSpawn(playerFlat + dir * ideal, playerFlat, ideal, FloorY);
            fallback.y = FloorY;
            return fallback;
        }

        /// <summary>Start of the fly-in: as far as the room allows along the line of sight, at a height whose path is clear.</summary>
        Vector3 FindFlyInStart(Vector3 landing, Vector3 flyDir, ref float height)
        {
            float[] heights = { height, 1.15f, 0.75f };
            var mask = Environment.EnvironmentMask;
            foreach (float h in heights)
            {
                float dist = Goose.flyInDistance;
                Vector3 origin = new Vector3(landing.x, FloorY + h, landing.z);
                if (mask.value != 0 && Physics.SphereCast(origin, 0.45f, flyDir, out var hit, dist, mask, QueryTriggerInteraction.Ignore))
                    dist = hit.distance - 1.0f;
                if (dist >= 2f)
                {
                    Vector3 start = landing + flyDir * dist;
                    start.y = FloorY + h;
                    if (mask.value == 0 || !Physics.CheckSphere(start, 0.45f, mask, QueryTriggerInteraction.Ignore))
                    {
                        height = h;
                        return new Vector3(start.x, FloorY, start.z);
                    }
                }
            }
            // Tight room: a short low hop in from just beyond the landing spot.
            height = 0.6f;
            return landing + flyDir * 1.4f;
        }

        void BeginChase()
        {
            if (State == GooseGameState.GameOver) return;
            Score.StartRun();
            RoundsThisSession++;
            DodgeCount = 0;
            LungesSurvived = 0;
            m_Taunted = false;
            LastRoundWon = false;
            BreadsThrown = 0;
            if (m_Bread != null) { Destroy(m_Bread.gameObject); m_Bread = null; }
            if (Yell != null) Yell.BeginListening();
            if (Perf != null && !(Bench != null && Bench.Running))
            {
                Perf.BeginRound("game.round", "game.round", new System.Collections.Generic.Dictionary<string, string>
                {
                    { "round", RoundsThisSession.ToString() }, { "mock_ar", UseMockAR.ToString() }, { "device_model", SystemInfo.deviceModel }
                });
            }
            SetState(GooseGameState.Chasing);
            UI.ShowHUD();
            UI.ShowMessage("TURN AROUND\nAND RUN!", 2.4f, UITheme.Danger);
            Haptics.Heavy();
        }

        public void NotifyLungeStarted()
        {
            m_LungeStartPos = Player.FlatPosition;
            if (ChaseActive) SetState(GooseGameState.JumpAttack);
        }

        public void NotifyLungeEnded(bool caught)
        {
            if (caught) OnPlayerCaught();
            else if (State == GooseGameState.JumpAttack)
            {
                SetState(GooseGameState.Chasing);
                LungesSurvived++;
                float moved = Vector3.Distance(Player.FlatPosition, m_LungeStartPos);
                if (moved >= dodgeDistance) StartCoroutine(DodgeMoment(moved));
                else
                {
                    // The player's win moment: a near miss.
                    UI.ShowMessage("MISSED!", 0.8f, UITheme.Yolk);
                    Haptics.Transient(0.5f, 0.8f);
                }
            }
        }

        /// <summary>A sidestepped lunge: hit-stop, DODGED!, feathers, a sore-loser line. Counted on the HUD and the results card.</summary>
        IEnumerator DodgeMoment(float moved)
        {
            DodgeCount++;
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "dodge", ("moved_m", moved), ("dodges", DodgeCount), ("survival_s", Score.SurvivalTime));
            if (Time.timeScale > 0.99f)
            {
                Time.timeScale = 0.05f;
                yield return new WaitForSecondsRealtime(0.08f);
                if (State != GooseGameState.GameOver) Time.timeScale = 1f;
            }
            UI.ShowMessage("DODGED!", 0.9f, UITheme.Yolk);
            Danger.Flash(new Color(1f, 0.95f, 0.8f, 0.15f), 0.15f);
            if (Look != null) Look.Impact(0.4f);
            if (Goose != null) Goose.Visual.FeatherBurst(10);
            Haptics.Transient(0.7f, 0.9f);
            if (Goose != null && Goose.Voice != null) Goose.Voice.SayBeat(GooseLines.Beat.Dodge, EventPayload());
        }

        public void OnPlayerCaught()
        {
            if (State == GooseGameState.GameOver) return;
            Score.EndRun();
            SetState(GooseGameState.GameOver);
            LastRoundWon = false;
            m_LastRun = Snapshot(false);
            Persona.RecordLoss(Score.SurvivalTime);
            UI.SetBread(false, 0f);
            if (Yell != null) Yell.EndListening();
            EndRoundTelemetry("caught");
            UI.SetLocatorTarget(null);
            if (Goose != null && Goose.Voice != null) Goose.Voice.RoundEnded(); // finish the audible sentence; discard unheard chase reactions
            Audio.PlayCaught(Goose != null ? Goose.transform.position : Player.FlatPosition);
            Haptics.Play(HapticsService.Pattern.Catch);
            Danger.CaughtEffect();
            if (Goose != null) Goose.OnCaughtPlayer();
            StartFlow(GameOverRoutine());
        }

        void EndRoundTelemetry(string outcome)
        {
            if (Perf == null || !Perf.RoundOpen) return;
            var data = new System.Collections.Generic.Dictionary<string, object>
            {
                { "survival_s", Score.SurvivalTime }, { "honks", Goose != null ? Goose.HonkCount : 0 }, { "dashes", Goose != null ? Goose.DashCount : 0 },
                { "lunges", Goose != null && Goose.Attack != null ? Goose.Attack.LungeCount : 0 }, { "best_s", Score.BestTime }, { "new_best", Score.LastRunWasBest },
                { "dodges", DodgeCount }, { "goose", Persona != null ? Persona.Name : "" }, { "lines_from_brain", Brain != null ? Brain.LinesFromBrain : 0 }, { "voice_fallbacks", Goose != null && Goose.Voice != null ? Goose.Voice.FallbackCount : 0 }
            };
            Perf.EndRound(outcome, data);
        }

        IEnumerator GameOverRoutine()
        {
            // Dramatic slow motion on the catch, then the sad goose honks and the results.
            Time.timeScale = 0.25f;
            if (Look != null) Look.SetSlowMotion(true);
            UI.ShowMessage("HONK.", 0.9f, UITheme.Danger);
            // The take-home frame: a third of a second into the slow motion the goose is squashed and flapping in your face.
            yield return new WaitForSecondsRealtime(0.35f);
            if (Share != null && GooseOnScreen()) Share.CaptureResult(StampLine1(), StampLine2(false));
            yield return new WaitForSecondsRealtime(0.55f); // the Catch haptic's rumble plays through the slow motion untouched
            Time.timeScale = 1f;
            if (Look != null) Look.SetSlowMotion(false);
            yield return new WaitForSecondsRealtime(0.35f);
            Audio.PlayGameOver();
            Haptics.Notify(false);
            string title = string.Format(k_LossTitles[Mathf.Max(0, RoundsThisSession - 1) % k_LossTitles.Length], Persona.Name);
            UI.ShowGameOver(title, Score.SurvivalTime, Goose != null ? Goose.HonkCount : 0, DodgeCount, Score.BestTime, Score.BestScore, Score.LastRunWasBest, false, Persona.Grudge > 1 ? Persona.GrudgeLine() : "", BuildRecap(false));
            if (Goose != null && Goose.Voice != null) Goose.Voice.SayBeat(GooseLines.Beat.Caught, EventPayload(), null, 2.5f);
            if (Score.LastRunWasBest)
            {
                yield return new WaitForSecondsRealtime(1.9f);
                Audio.PlayNewBest();
                Haptics.Play(HapticsService.Pattern.Success);
            }
        }

        /// <summary>The win: the goose gives up. Same terminal state, a different card.</summary>
        public void OnGooseGaveUp()
        {
            if (State == GooseGameState.GameOver || !ChaseActive) return;
            Score.EndRun();
            SetState(GooseGameState.GameOver);
            LastRoundWon = true;
            m_LastRun = Snapshot(true);
            Persona.RecordWin(Score.SurvivalTime);
            if (Yell != null) Yell.EndListening();
            EndRoundTelemetry("outlasted");
            UI.SetLocatorTarget(null);
            UI.SetBread(false, 0f);
            Danger.ResetEffects();
            if (Goose != null && Goose.Voice != null) Goose.Voice.RoundEnded();
            if (Goose != null) Goose.GiveUp();
            StartFlow(WinRoutine());
        }

        IEnumerator WinRoutine()
        {
            UI.ShowMessage("THE GOOSE\nHAS GIVEN UP.", 2.4f, UITheme.Yolk);
            Haptics.Play(HapticsService.Pattern.Success);
            if (Look != null) Look.SetRage(false);
            yield return new WaitForSecondsRealtime(1.2f);
            Audio.PlayNewBest();
            yield return new WaitForSecondsRealtime(1.4f);
            if (Share != null && GooseOnScreen()) Share.CaptureResult(StampLine1(), StampLine2(true));
            string title = string.Format(k_WinTitles[Mathf.Max(0, RoundsThisSession - 1) % k_WinTitles.Length], Persona.Name);
            UI.ShowGameOver(title, Score.SurvivalTime, Goose != null ? Goose.HonkCount : 0, DodgeCount, Score.BestTime, Score.BestScore, Score.LastRunWasBest, true, "", BuildRecap(true));
            if (Goose != null && Goose.Voice != null) Goose.Voice.SayBeat(GooseLines.Beat.Outlasted, EventPayload(), null, 2.5f);
            if (Score.LastRunWasBest)
            {
                yield return new WaitForSecondsRealtime(1.9f);
                Haptics.Play(HapticsService.Pattern.Success);
            }
        }

        // ------------------------------------------------------------------ goose hooks (voice beats)
        /// <summary>The goose landed and started glaring: first words (pre-voiced by the brain during the steal beat).</summary>
        public void OnGooseGlareStarted()
        {
            if (Goose != null && Goose.Voice != null) Goose.Voice.SayPrepared(m_IntroLine, GooseLines.Beat.Intro);
            m_IntroLine = null;
        }

        public void OnGooseRage()
        {
            if (Goose != null && Goose.Voice != null) Goose.Voice.SayBeat(GooseLines.Beat.Rage, EventPayload());
        }

        /// <summary>Warm the brain, get the goose's name and the pre-voiced intro. Fire-and-forget; offline is fine.</summary>
        void BeginBrainSession()
        {
            m_IntroLine = null;
            if (Brain == null) return;
            StartCoroutine(Brain.StartSession(RoundsThisSession, Score.GamesPlayed, Score.BestTime, r =>
            {
                if (r == null) return;
                Persona.ApplyFromBrain(r.Name, r.Title, r.Grudge, r.Female);
                m_IntroLine = r.Intro;
            }));
        }

        /// <summary>What the brain hears about the round with every beat.</summary>
        public System.Collections.Generic.Dictionary<string, object> EventPayload()
        {
            return new System.Collections.Generic.Dictionary<string, object>
            {
                { "survival", Score.SurvivalTime }, { "honks", Goose != null ? Goose.HonkCount : 0 }, { "dodges", DodgeCount },
                { "breads", Goose != null ? Goose.BreadsEaten : 0 }, { "tier", Goose != null ? Goose.Tier : 0 }, { "roundsThisSession", RoundsThisSession }
            };
        }

        // ------------------------------------------------------------------ bread
        public void OnBreadPressed()
        {
            if (!ChaseActive || IsPaused || Goose == null || m_Bread != null || BreadsThrown >= breadPerRound) return;
            if (Goose.FlyingIn || Goose.Glaring || Goose.State == GooseState.JumpAttack || Goose.State == GooseState.Dash || Goose.State == GooseState.Eating || Goose.State == GooseState.NameCalled) return;
            var cam = Player.Cam;
            Vector3 from = cam != null ? cam.transform.TransformPoint(k_ThrowHandLocal) : Player.Position;
            Vector3 landing = FindBreadLanding();
            landing.y = Goose.Movement.SampleFloor(landing);
            m_Bread = BreadController.Create(Materials, from);
            BreadsThrown++;
            Audio.PlayThrowWhoosh(from);
            Haptics.Transient(0.35f, 0.7f);
            UI.ShowMessage("BREAD!", 0.7f, UITheme.Yolk);
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "bread.thrown", ("distance_m", Vector3.Distance(Player.FlatPosition, landing)), ("goose_distance", Goose.DistanceToPlayer), ("tier", Goose.Tier));
            StartCoroutine(BreadRoutine(from, landing));
        }

        IEnumerator BreadRoutine(Vector3 from, Vector3 landing)
        {
            var bread = m_Bread;
            using (var span = GooseTelemetry.StartSpan("bread.spawn", "throw"))
            {
                yield return null;
            }
            if (Goose != null) Goose.Distract(bread);
            yield return bread.Throw(from, landing, FloorY, this);
            if (Goose != null && Goose.State != GooseState.Distracted && Goose.State != GooseState.Eating) Goose.Distract(bread);
            // Wait until eaten (or the round ends); the goose reports the eating itself.
            float t = 0f;
            while (bread != null && !bread.Eaten && t < 30f && ChaseActive) { t += Time.deltaTime; yield return null; }
            if (bread != null && !bread.Eaten) Destroy(bread.gameObject);
            if (m_Bread == bread) m_Bread = null;
        }

        /// <summary>Called by the goose when the roll is gone: a line, and the HUD knows it is angrier.</summary>
        public void OnBreadEaten()
        {
            UI.ShowMessage("ANGRIER.", 0.9f, UITheme.Danger);
            Haptics.Transient(0.5f, 0.6f);
            if (Look != null) Look.Impact(0.25f);
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "bread.eaten", ("tier", Goose != null ? Goose.Tier : 0), ("breads", Goose != null ? Goose.BreadsEaten : 0));
            if (Goose != null && Goose.Voice != null) Goose.Voice.SayBeat(GooseLines.Beat.Bread, EventPayload());
        }

        /// <summary>
        /// Bread lands on the far side of the goose (away from the player): eating it buys distance and time, and the
        /// goose has to come back. Falls back to beside the goose, never next to the player.
        /// </summary>
        Vector3 FindBreadLanding()
        {
            Vector3 player = Player.FlatPosition;
            Vector3 g = Goose.transform.position; g.y = FloorY;
            Vector3 away = g - player; away.y = 0f;
            away = away.sqrMagnitude > 1e-3f ? away.normalized : Player.FlatForward;
            Vector3[] offsets =
            {
                away * 1.6f, away * 1.2f,
                Quaternion.AngleAxis(45f, Vector3.up) * away * 1.4f, Quaternion.AngleAxis(-45f, Vector3.up) * away * 1.4f,
                Quaternion.AngleAxis(90f, Vector3.up) * away * 1.2f, Quaternion.AngleAxis(-90f, Vector3.up) * away * 1.2f,
                away * 0.8f
            };
            foreach (var o in offsets)
            {
                Vector3 p = g + o; p.y = FloorY;
                if (Vector3.Distance(new Vector3(p.x, 0f, p.z), new Vector3(player.x, 0f, player.z)) < 1.3f) continue;
                if (Goose.Avoidance.FloorOk(p) && Goose.Avoidance.IsPositionFree(p)) return p;
            }
            Vector3 fb = g + away * 0.8f; fb.y = FloorY;
            return fb;
        }

        // ------------------------------------------------------------------ yell
        void OnPlayerYelled(float db)
        {
            if (!ChaseActive || IsPaused || Goose == null) return;
            using (var span = GooseTelemetry.StartSpan("yell.detect", "flinch"))
            {
                span.SetData("level_db", db);
                if (Goose.Flinch())
                {
                    UI.ShowMessage("IT HEARD YOU.", 0.9f);
                    Danger.Shake(0.3f);
                }
            }
        }

        void OnYellAudio(byte[] wav)
        {
            if (Goose == null || Goose.Voice == null || State == GooseGameState.GameOver) return;
            // On-device speech is reading the clip: the reply waits for the transcript (OnTranscript).
            if (Yell != null && Yell.RecognitionPending) { m_PendingYellWav = wav; return; }
            Goose.Voice.SayBeat(GooseLines.Beat.Yell, EventPayload(), wav, 3.5f);
        }

        /// <summary>What the player actually shouted (on-device recognition; "" = nothing understood).</summary>
        void OnTranscript(string transcript)
        {
            var wav = m_PendingYellWav;
            m_PendingYellWav = null;
            var intent = ShoutClassifier.Classify(transcript, Persona.Name);
            string tag = intent == ShoutIntent.Name ? "name" : (intent == ShoutIntent.Apology ? "apology" : "none");
            GooseTelemetry.Increment("speech.recognized", 1, ("matched", tag));
            GooseTelemetry.Log(GooseTelemetry.Level.Info, "speech.recognized", ("transcript", transcript ?? ""), ("matched", tag), ("name", Persona.Name), ("goose_state", Goose != null ? Goose.State.ToString() : "none"));
            if (!string.IsNullOrEmpty(transcript)) Persona.RecordShout(transcript);
            if (!ChaseActive || IsPaused || Goose == null || Goose.Voice == null) return;
            if (intent == ShoutIntent.Name && Goose.NameCalled()) { Danger.Shake(0.4f); return; }   // "...WHAT." is the whole reply
            if (intent == ShoutIntent.Apology) { Goose.RefuseApology(); return; }
            Goose.Voice.SayBeat(GooseLines.Beat.Yell, EventPayload(), wav, 3.5f);
        }

        // ------------------------------------------------------------------ recap, board, photo
        RoundRecap BuildRecap(bool won)
        {
            return new RoundRecap
            {
                Dashes = Goose != null ? Goose.DashCount : 0, LungesSurvived = LungesSurvived, Breads = BreadsThrown,
                Honks = Goose != null ? Goose.HonkCount : 0, Dodges = DodgeCount, Rage = Goose != null && Goose.Rage, Won = won,
                Survival = Score.SurvivalTime, GooseName = Persona != null ? Persona.Name : "", GooseTitle = Persona != null ? Persona.Title : "",
                HasShot = Share != null && Share.HasShot
            };
        }

        RunSnapshot Snapshot(bool won) => new RunSnapshot
        {
            Seconds = Score.SurvivalTime, Won = won, Goose = Persona != null && !string.IsNullOrEmpty(Persona.Name) ? Persona.Name : "GOOSE",
            Honks = Goose != null ? Goose.HonkCount : 0, Dodges = DodgeCount
        };

        /// <summary>The Goose Board (Worker) is reachable and there is a finished round to post.</summary>
        public bool BoardAvailable => Brain != null && Brain.Available && m_LastRun != null;

        public void PostRunToBoard(string name, Action<int> onRank)
        {
            var run = m_LastRun;
            if (!BoardAvailable || run == null) { onRank?.Invoke(-1); return; }
            StartCoroutine(Brain.PostBoardRun(name, run.Seconds, run.Won, run.Goose, run.Honks, run.Dodges, dto =>
            {
                int rank = dto != null ? dto.rank : -1;
                GooseTelemetry.Log(GooseTelemetry.Level.Info, "board.posted", ("name", name), ("rank", rank), ("seconds", run.Seconds), ("won", run.Won));
                onRank?.Invoke(rank);
            }));
        }

        public void FetchBoardTop(int n, Action<GooseBrainClient.BoardRowDto[]> done)
        {
            if (Brain == null || !Brain.Available) { done?.Invoke(null); return; }
            StartCoroutine(Brain.FetchBoardTop(n, done));
        }

        /// <summary>The automatic shot is only worth keeping when the goose is actually in the frame (caught from behind: use PHOTO WITH instead).</summary>
        bool GooseOnScreen() => Goose != null && Player.IsInView(Goose.transform.position + Vector3.up * 0.35f, 0.02f);

        string StampLine1() => Persona == null || string.IsNullOrEmpty(Persona.Name) ? "THE GOOSE" : (string.IsNullOrEmpty(Persona.Title) ? Persona.Name : Persona.Name + ", " + Persona.Title);
        string StampLine2(bool won) => (won ? "OUTLASTED IN " : "GOT YOU AT ") + ScoreManager.FormatTime(Score.SurvivalTime);

        string ShareText()
        {
            string name = Persona != null && !string.IsNullOrEmpty(Persona.Name) ? Persona.Name : "THE GOOSE";
            string t = ScoreManager.FormatTime(Score.SurvivalTime);
            return LastRoundWon ? "I outlasted " + name + " for " + t + ". GOOSED. #hackthenorth" : name + " got me at " + t + ". GOOSED. #hackthenorth";
        }

        public void OnSharePressed()
        {
            if (State != GooseGameState.GameOver || Share == null || !Share.HasShot) return;
            Haptics.Light();
            Audio.PlayShutter();
            Share.ShareLast(ShareText());
            GooseTelemetry.Increment("share.opened", 1, ("won", LastRoundWon.ToString()));
        }

        /// <summary>Photo mode is using the front camera with the goose over your shoulder (FLIP goes back to the room).</summary>
        public bool SelfieMode { get; private set; }
        public bool PhotoCameraReady { get; private set; }
        Coroutine m_PhotoCameraRoutine;

        void PreparePhotoCamera()
        {
            if (m_PhotoCameraRoutine != null) StopCoroutine(m_PhotoCameraRoutine);
            PhotoCameraReady = false;
            UI.SetPhotoCameraReady(false);
            if (Goose.Voice != null) Goose.Voice.Interrupt();
            Goose.EndPhotoPose();
            m_PhotoCameraRoutine = StartCoroutine(WaitForPhotoCamera());
        }

        IEnumerator WaitForPhotoCamera()
        {
            yield return null;
            float until = Time.unscaledTime + 5f;
            while (AR != null && !AR.PhotoCameraReady && Time.unscaledTime < until) yield return null;
            if (AR != null && !AR.PhotoCameraReady && SelfieMode)
            {
                AR.SetSelfieCamera(false);
                SelfieMode = false;
                UI.ShowPhotoMode(false);
                UI.SetPhotoCameraReady(false);
                UI.ShowMessage("Selfie camera unavailable. Using rear camera.", 3f);
                until = Time.unscaledTime + 5f;
                while (!AR.PhotoCameraReady && Time.unscaledTime < until) yield return null;
            }
            PhotoCameraReady = AR == null || AR.PhotoCameraReady;
            if (PhotoCameraReady && Goose != null) Goose.BeginPhotoPose(SelfieMode);
            UI.SetPhotoCameraReady(PhotoCameraReady);
            if (!PhotoCameraReady) UI.ShowMessage("Camera unavailable. Tap BACK to try again.", 3f);
            m_PhotoCameraRoutine = null;
        }

        /// <summary>PHOTO WITH KEVIN: the card slides away, the front camera comes on and the goose photobombs beside you.</summary>
        public void OnPhotoPressed()
        {
            if (State != GooseGameState.GameOver || Goose == null || UI.PhotoModeActive) return;
            Haptics.Light();
            SelfieMode = AR != null && AR.selfiePhotoMode && AR.SetSelfieCamera(true);
            UI.ShowPhotoMode(SelfieMode);
            PreparePhotoCamera();
            GooseTelemetry.Increment("photo.opened", 1, ("selfie", SelfieMode.ToString()));
        }

        /// <summary>FLIP in photo mode: selfie with the goose, or the goose posing in the room through the world camera.</summary>
        public void OnPhotoFlipPressed()
        {
            if (State != GooseGameState.GameOver || Goose == null || !UI.PhotoModeActive || m_Shutter || m_PhotoCameraRoutine != null) return;
            bool selfie = !SelfieMode;
            if (AR == null || !AR.SetSelfieCamera(selfie)) return;
            SelfieMode = selfie;
            Haptics.Light();
            UI.ShowPhotoMode(SelfieMode);
            PreparePhotoCamera();
        }

        public void OnPhotoBackPressed()
        {
            if (State != GooseGameState.GameOver) return;
            LeavePhotoMode();
            UI.ShowResultsAgain();
        }

        /// <summary>World camera back on and the goose back where it stood. Safe to call when photo mode is not open.</summary>
        void LeavePhotoMode()
        {
            if (m_PhotoCameraRoutine != null) StopCoroutine(m_PhotoCameraRoutine);
            m_PhotoCameraRoutine = null;
            PhotoCameraReady = false;
            if (SelfieMode && AR != null) AR.SetSelfieCamera(false);
            SelfieMode = false;
            if (Goose != null) Goose.EndPhotoPose();
        }

        public void OnShutterPressed()
        {
            if (State != GooseGameState.GameOver || Share == null || m_Shutter || !UI.PhotoModeActive || !PhotoCameraReady || (AR != null && !AR.PhotoCameraReady)) return;
            StartCoroutine(ShutterRoutine());
        }

        IEnumerator ShutterRoutine()
        {
            m_Shutter = true;
            Haptics.Transient(0.6f, 0.9f);
            Audio.PlayShutter();
            Texture2D tex = null;
            yield return Share.Capture(StampLine1(), StampLine2(LastRoundWon), t => tex = t);
            if (tex != null)
            {
                UI.Flash(new Color(1f, 1f, 1f, 0.5f), 0.25f); // after the capture, or the flash ends up in the shot
                Share.SetShot(tex);
                Share.ShareLast(ShareText());
                GooseTelemetry.Increment("photo.taken");
            }
            else UI.ShowMessage("NO PHOTO.", 1f);
            m_Shutter = false;
        }

        /// <summary>Game over -> a new round always starts by placing the nest again (the old spot is never reused).</summary>
        public void OnRunAgainPressed()
        {
            if (State != GooseGameState.GameOver) return;
            Time.timeScale = 1f;
            LeavePhotoMode();
            if (Share != null) Share.ClearShot();
            ClearGoose();
            Danger.ResetEffects();
            Audio.PlayStart();
            Haptics.Light();
            BeginBrainSession();
            if (Nest != null)
            {
                Nest.Egg.HideCrackedEgg();
                Nest.gameObject.SetActive(false);
            }
            BeginPlacement();
        }

        /// <summary>Game over -> pick a new spot for the nest.</summary>
        public void OnMoveNestPressed()
        {
            if (State != GooseGameState.GameOver) return;
            Time.timeScale = 1f;
            LeavePhotoMode();
            if (Share != null) Share.ClearShot();
            ClearGoose();
            Danger.ResetEffects();
            Haptics.Light();
            BeginBrainSession();
            if (Nest != null)
            {
                Nest.Egg.HideCrackedEgg();
                Nest.gameObject.SetActive(false);
            }
            BeginPlacement();
        }

        void ClearGoose()
        {
            if (Goose != null)
            {
                Destroy(Goose.gameObject);
                Goose = null;
            }
            if (m_GooseCatcher != null)
            {
                Destroy(m_GooseCatcher.gameObject);
                m_GooseCatcher = null;
            }
            UI.SetLocatorTarget(null);
        }

        // ------------------------------------------------------------------ pause
        public void TogglePause()
        {
            if (IsPaused) ResumeGame();
            else PauseGame();
        }

        public void PauseGame()
        {
            if (!ChaseActive || IsPaused) return;
            IsPaused = true;
            if (Goose != null) Goose.Paused = true;
            Score.Pause();
            if (Yell != null) Yell.EndListening();
            UI.ShowPaused();
        }

        public void ResumeGame()
        {
            if (!IsPaused) return;
            IsPaused = false;
            if (Goose != null) Goose.Paused = false;
            Score.Resume();
            if (Yell != null && ChaseActive) Yell.BeginListening();
            UI.HidePaused();
            Haptics.Light();
        }

        void OnApplicationPause(bool paused)
        {
            if (paused && ChaseActive) PauseGame();
        }

        void StartFlow(IEnumerator routine)
        {
            if (m_FlowRoutine != null) StopCoroutine(m_FlowRoutine);
            m_FlowRoutine = StartCoroutine(routine);
        }
    }
}
