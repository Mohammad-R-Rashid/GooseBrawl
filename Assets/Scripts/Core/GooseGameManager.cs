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

        public NestController Nest { get; private set; }
        public GooseChaseController Goose { get; private set; }
        public float FloorY { get; private set; }
        public bool ChaseActive => State == GooseGameState.Chasing || State == GooseGameState.Danger || State == GooseGameState.JumpAttack;
        public bool IsPaused { get; private set; }
        /// <summary>The goose exists and is on its way (fly-in / glare) or chasing: the locator should be live.</summary>
        public bool GooseLive => Goose != null && (State == GooseGameState.EggStolen || ChaseActive);
        public int RoundsThisSession { get; private set; }

        Coroutine m_FlowRoutine;
        ShadowCatcher m_GooseCatcher, m_NestCatcher;
        GameObject m_NestProbe;
        bool m_SkipRequested;
        bool m_CoachingShown;

        static readonly string[] k_FirstLines = { "OOPS.\nTHERE GOES BREAKFAST.", "BUTTERFINGERS.", "WHOOPS.\nNO BREAKFAST.", "OH NO." };
        static readonly string[] k_SecondLines = { "THE GOOSE HEARD THAT.", "IT HEARD THAT.", "SOMETHING IS HONKING.", "THE GOOSE HAS NOTICED." };
        static readonly string[] k_GameOverTitles = { "THE GOOSE\nGOT YOU", "PECKED.", "HONKED\nTO DEATH", "GOOSE 1\nYOU 0", "YOU GOT\nGOOSED", "NO BREAKFAST\nFOR YOU" };

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
        }

        void OnDestroy()
        {
            Time.timeScale = 1f;
            if (Placement != null) Placement.Placed -= OnNestPlaced;
            if (Instance == this) Instance = null;
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
                UI.UpdateHUD(Score.SurvivalTime, Goose != null ? Goose.HonkCount : 0, Score.BestTime);

            bool showTracking = !Player.TrackingGood && State != GooseGameState.Boot && State != GooseGameState.GameOver && !IsPaused;
            UI.SetTrackingWarning(showTracking, showTracking ? AR.TrackingHint() : null);

            if (State == GooseGameState.EggStolen && RoundsThisSession > 0 && GameInput.TryGetTap(out var tap) && !GameInput.IsPointerOverUI(tap))
                m_SkipRequested = true;

            if (UseMockAR)
            {
                if (GameInput.RestartPressed() && State == GooseGameState.GameOver) OnRunAgainPressed();
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
            if (Score.GamesPlayed == 0 && !m_CoachingShown)
            {
                m_CoachingShown = true;
                UI.ShowCoaching(() => StartFlow(ScanRoutine()));
                return;
            }
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
            UI.ShowMessage("BREAKFAST TIME!", 1.2f * speed, UITheme.Yolk);
            yield return Nest.StealEgg(Player.Cam);
            yield return Beat(0.6f);
            if (!m_SkipRequested) UI.ShowMessage("GOT IT.\nLET'S GO COOK.", 1.5f * speed, UITheme.Yolk);
            yield return Beat(1.6f);

            // The fumble: the egg slips, falls (in slow motion so you can see it) and cracks. That noise wakes the goose.
            UI.ShowMessage("NO NO NO", 2.2f * speed);
            Time.timeScale = 0.4f;
            if (Look != null) Look.SetSlowMotion(true);
            yield return Nest.DropEgg(Player.Cam, FloorY, Materials);
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

        void SpawnGoose()
        {
            if (Goose != null) Destroy(Goose.gameObject);
            if (m_GooseCatcher != null) Destroy(m_GooseCatcher.gameObject);

            GameObject go = goosePrefab != null ? Instantiate(goosePrefab) : GoosePlaceholderFactory.CreateRuntimeGoose(Materials);
            go.name = "Goose";
            Goose = go.GetComponent<GooseChaseController>();
            if (Goose == null) Goose = go.AddComponent<GooseChaseController>();
            Goose.Initialize(this);
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

            // Landing spot: beside the nest when the nest is roughly where the player is looking, otherwise straight ahead.
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
            Vector3 landing = playerFlat + dir * landDist;
            landing = Goose.Avoidance.FindFreeSpawn(landing, playerFlat, landDist, FloorY);
            // Keep the landing inside the view cone even after FindFreeSpawn rotated it.
            Vector3 toLanding = landing - playerFlat; toLanding.y = 0f;
            if (toLanding.sqrMagnitude > 1e-3f && Vector3.Angle(fwd, toLanding) > 40f)
            {
                landing = playerFlat + fwd * landDist;
                landing.y = FloorY;
            }

            // Fly-in start: far along the same direction, shortened if a scanned wall is in the way.
            float flyDist = Goose.flyInDistance;
            Vector3 flyDir = (landing - playerFlat); flyDir.y = 0f; flyDir.Normalize();
            Vector3 probeOrigin = new Vector3(landing.x, FloorY + Goose.flyHeight, landing.z);
            if (Environment.EnvironmentMask.value != 0 &&
                Physics.SphereCast(probeOrigin, 0.3f, flyDir, out var hit, flyDist, Environment.EnvironmentMask, QueryTriggerInteraction.Ignore))
                flyDist = Mathf.Max(1.5f, hit.distance - 0.4f);
            Vector3 start = landing + flyDir * flyDist;

            UI.ShowMessage("HERE IT COMES.", 2f, UITheme.Yolk);
            Danger.SpawnEffect();
            Goose.FlyIn(start, landing, FloorY, BeginChase);
        }

        void BeginChase()
        {
            if (State == GooseGameState.GameOver) return;
            Score.StartRun();
            RoundsThisSession++;
            SetState(GooseGameState.Chasing);
            UI.ShowHUD();
            UI.ShowMessage("TURN AROUND\nAND RUN!", 2.4f, UITheme.Danger);
            Haptics.Heavy();
        }

        public void NotifyLungeStarted()
        {
            if (ChaseActive) SetState(GooseGameState.JumpAttack);
        }

        public void NotifyLungeEnded(bool caught)
        {
            if (caught) OnPlayerCaught();
            else if (State == GooseGameState.JumpAttack)
            {
                SetState(GooseGameState.Chasing);
                // The player's win moment: a near miss.
                UI.ShowMessage("MISSED!", 0.8f, UITheme.Yolk);
                Haptics.Transient(0.5f, 0.8f);
            }
        }

        public void OnPlayerCaught()
        {
            if (State == GooseGameState.GameOver) return;
            Score.EndRun();
            SetState(GooseGameState.GameOver);
            UI.SetLocatorTarget(null);
            Audio.PlayCaught(Goose != null ? Goose.transform.position : Player.FlatPosition);
            Haptics.Play(HapticsService.Pattern.Catch);
            Danger.CaughtEffect();
            if (Goose != null) Goose.OnCaughtPlayer();
            StartFlow(GameOverRoutine());
        }

        IEnumerator GameOverRoutine()
        {
            // Dramatic slow motion on the catch, then the sad goose honks and the results.
            Time.timeScale = 0.25f;
            if (Look != null) Look.SetSlowMotion(true);
            UI.ShowMessage("HONK.", 0.9f, UITheme.Danger);
            yield return new WaitForSecondsRealtime(0.3f);
            Haptics.Heavy();
            yield return new WaitForSecondsRealtime(0.6f);
            Time.timeScale = 1f;
            if (Look != null) Look.SetSlowMotion(false);
            yield return new WaitForSecondsRealtime(0.35f);
            Audio.PlayGameOver();
            Haptics.Notify(false);
            string title = k_GameOverTitles[UnityEngine.Random.Range(0, k_GameOverTitles.Length)];
            UI.ShowGameOver(title, Score.SurvivalTime, Goose != null ? Goose.HonkCount : 0, Score.BestTime, Score.BestScore, Score.LastRunWasBest);
            if (Score.LastRunWasBest)
            {
                yield return new WaitForSecondsRealtime(1.9f);
                Audio.PlayNewBest();
                Haptics.Play(HapticsService.Pattern.Success);
            }
        }

        public void OnRunAgainPressed()
        {
            if (State != GooseGameState.GameOver) return;
            Time.timeScale = 1f;
            ClearGoose();
            Danger.ResetEffects();
            Audio.PlayStart();
            Haptics.Light();
            if (Nest != null)
            {
                Nest.ResetEgg();
                SetState(GooseGameState.EggReady);
                UI.ShowEgg();
            }
            else
            {
                BeginPlacement();
            }
        }

        /// <summary>Game over -> pick a new spot for the nest.</summary>
        public void OnMoveNestPressed()
        {
            if (State != GooseGameState.GameOver) return;
            Time.timeScale = 1f;
            ClearGoose();
            Danger.ResetEffects();
            Haptics.Light();
            if (Nest != null)
            {
                Nest.Egg.ClearCrackedEgg();
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
            UI.ShowPaused();
        }

        public void ResumeGame()
        {
            if (!IsPaused) return;
            IsPaused = false;
            if (Goose != null) Goose.Paused = false;
            Score.Resume();
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
