#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Editor-only automated play-through of the mock game: start, scan, place, steal, fly-in, glare until seen,
    /// run, turn around, get caught, restart. Logs a [Smoke] report, saves screenshots of every beat to
    /// Library/SmokeShots and exits play mode. Excluded from device builds.
    /// </summary>
    public class GooseSmokeTest : MonoBehaviour
    {
        const string ShotFolder = "Library/SmokeShots";

        readonly StringBuilder m_Report = new StringBuilder();
        int m_Failures;
        int m_PenetrationFrames;
        int m_ChaseFrames;
        float m_MinDistance = 99f;
        float m_MaxSpeed;
        int m_LungeCount;
        int m_StuckCount;
        float m_MaxPenetrationDepth;

        void Start()
        {
            // Render with the pipeline asset that ships to the phone.
            var names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++) if (names[i] == "Mobile") { QualitySettings.SetQualityLevel(i, true); break; }
            Directory.CreateDirectory(ShotFolder);
            StartCoroutine(Run());
        }

        void Check(bool ok, string what)
        {
            m_Report.Append(ok ? "  [OK]   " : "  [FAIL] ").AppendLine(what);
            if (!ok) m_Failures++;
        }

        IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(ShotFolder, name + ".png"));
            yield return null;
        }

        IEnumerator WaitForState(GooseGameManager mgr, GooseGameState state, float timeout)
        {
            float t = 0f;
            while (mgr.State != state && t < timeout)
            {
                t += Time.deltaTime;
                yield return null;
            }
        }

        IEnumerator Run()
        {
            yield return null;
            var mgr = GooseGameManager.Instance;
            Check(mgr != null, "GooseGameManager present");
            if (mgr == null) { Finish(); yield break; }
            Check(GooseGameManager.UseMockAR, "Mock AR mode active");
            Check(mgr.Mock != null && mgr.Mock.MockCamera != null, "Mock camera built");
            Check(mgr.Player.HasCamera, "PlayerTracker sees a camera");
            Check(mgr.Environment.EnvironmentLayer > 0, "AREnvironment layer resolved (" + mgr.Environment.EnvironmentLayer + ")");
            Check(mgr.goosePrefab != null, "Goose prefab assigned");
            Check(mgr.Audio.honkClip != null && mgr.Audio.whooshClip != null && mgr.Audio.BreathClip != null && mgr.Audio.HeartbeatClip != null, "Procedural audio clips generated");
            Check(mgr.Look != null, "CinematicLookController present");
            Check(mgr.Materials != null && mgr.Materials.ShadowCatcher != null, "Shadow catcher material available");
            Check(mgr.State == GooseGameState.Boot, "Starts in Boot");
            bool online = UnityEditor.SessionState.GetBool("GooseBrawl.SmokeOnline", false);
            if (mgr.Brain != null) mgr.Brain.forceOffline = !online; // deterministic by default: bank voice, no network ('smokeonline' uses wrangler dev)
            yield return Shot("01_title");

            // First launch shows the coaching card; dismiss it through the manager path.
            bool firstRun = mgr.Score.GamesPlayed == 0;
            mgr.OnStartPressed();
            yield return null;
            if (firstRun)
            {
                yield return Shot("02_coaching");
                var gotIt = GameObject.Find("GotIt");
                Check(gotIt != null, "Coaching card shown on first run");
                var btn = gotIt != null ? gotIt.GetComponentInChildren<UnityEngine.UI.Button>() : null;
                if (btn != null) btn.onClick.Invoke();
            }
            yield return WaitForState(mgr, GooseGameState.PlaceNest, 12f);
            Check(mgr.State == GooseGameState.PlaceNest, "Scan -> PlaceNest (floor found)");
            yield return new WaitForSeconds(0.3f);
            yield return Shot("03_place");

            bool placed = mgr.Placement.TryPlaceAtScreenPoint(new Vector2(Screen.width * 0.5f, Screen.height * 0.35f));
            Check(placed, "Nest placed by tap raycast");
            yield return WaitForState(mgr, GooseGameState.EggReady, 3f);
            Check(mgr.State == GooseGameState.EggReady, "PlaceNest -> EggReady");
            Check(mgr.Nest != null && mgr.Nest.Egg != null && mgr.Nest.Egg.gameObject.activeSelf, "Nest + egg visible");
            Check(GameObject.Find("NestShadowCatcher") != null, "Shadow catcher under the nest");
            yield return new WaitForSeconds(0.4f);
            yield return Shot("04_egg");
            // Close-up of the nest for the visual pass, then back to where we were.
            Vector3 nestPos = mgr.Nest.transform.position;
            Vector3 playerBefore = mgr.Player.FlatPosition;
            Vector3 fwdBefore = mgr.Player.FlatForward;
            Vector3 back = (playerBefore - nestPos); back.y = 0f; back = back.sqrMagnitude > 1e-3f ? back.normalized : Vector3.back;
            mgr.Mock.TeleportTo(nestPos + back * 0.9f, -back);
            mgr.Mock.LookAt(nestPos + Vector3.up * 0.12f);
            yield return new WaitForSeconds(0.3f);
            yield return Shot("04b_nest_closeup");
            mgr.Mock.TeleportTo(playerBefore, fwdBefore);
            yield return null;

            mgr.OnStealEggPressed();
            // Carry phase: walk backwards so the grip drains from movement (the slip is player-caused).
            yield return new WaitForSeconds(0.9f);
            yield return Shot("04c_carry");
            // A few small brisk sidesteps back and forth: each reversal is a jerk the palm remembers; a few of them spill it.
            float mockSpeed = mgr.Mock.moveSpeed;
            mgr.Mock.moveSpeed = 0.6f; // brisk: a reversal is ~1.2 m/s of jerk, two or three of them spill it
            float t = 0f, flip = 0f;
            int dir = -1;
            while (mgr.Nest.Egg.CrackedEgg == null && t < 12f)
            {
                t += Time.deltaTime;
                if (t >= flip) { flip = t + 0.5f; dir = -dir; mgr.Mock.AutoMove = new Vector2(dir, 0f); }
                yield return null;
            }
            mgr.Mock.AutoMove = Vector2.zero;
            mgr.Mock.moveSpeed = mockSpeed;
            Check(t < 6f, "Egg slipped from the player's movement within " + t.ToString("F1") + " s");
            // Back to the original spot and facing so the entrance and the head-start run are reproducible.
            mgr.Mock.TeleportTo(playerBefore, fwdBefore);
            // Wait for the goose to exist and start its fly-in, then look away to prove the Glare waits for us.
            t = 0f;
            while (mgr.Goose == null && t < 20f) { t += Time.deltaTime; yield return null; }
            Check(mgr.Goose != null, "Goose spawned for the fly-in");
            if (mgr.Goose == null) { Finish(); yield break; }
            var goose = mgr.Goose;
            yield return new WaitForSeconds(0.8f);
            yield return Shot("05_flyin");
            Check(mgr.Nest != null && mgr.Nest.Egg != null && mgr.Nest.Egg.CrackedEgg != null, "Cracked egg left on the floor");
            if (mgr.Nest.Egg.CrackedEgg != null)
            {
                mgr.Mock.LookAt(mgr.Nest.Egg.CrackedEgg.transform.position);
                yield return new WaitForSeconds(0.2f);
                yield return Shot("05b_cracked_egg");
                mgr.Mock.LookAt(goose.transform.position + Vector3.up * 0.5f);
            }

            // Turn away so the goose is behind the camera while it lands.
            mgr.Mock.AutoTurn = 225f;
            yield return new WaitForSeconds(0.8f);
            mgr.Mock.AutoTurn = 0f;
            t = 0f;
            while (!goose.Glaring && goose.FlyingIn && t < 10f) { t += Time.deltaTime; yield return null; }
            Check(goose.Glaring, "Goose glares after landing");
            Vector3 glarePos = goose.transform.position;
            yield return new WaitForSeconds(1.2f);
            Check(mgr.State == GooseGameState.EggStolen, "Chase has not started while the goose is unseen");
            Check(Vector3.Distance(glarePos, goose.transform.position) < 0.05f, "Goose holds position during the glare");
            yield return Shot("06_glare_behind");

            // Turn back: the locator should have pointed us here; once seen the chase begins.
            mgr.Mock.AutoTurn = 225f;
            yield return new WaitForSeconds(0.8f);
            mgr.Mock.AutoTurn = 0f;
            yield return WaitForState(mgr, GooseGameState.Chasing, 8f);
            Check(mgr.State == GooseGameState.Chasing, "Glare -> Chasing once the goose was seen");
            Check(!goose.FlyingIn && !goose.Glaring, "Fly-in and glare finished");
            float spawnDist = mgr.Player.FlatDistanceTo(goose.transform.position);
            Check(spawnDist > 1.6f && spawnDist < 5f, "Landed " + spawnDist.ToString("F2") + " m from the player");
            Check(mgr.Player.IsInView(goose.transform.position + Vector3.up * 0.3f, 0.1f), "Goose visible when the chase starts");
            Check(goose.Visual.HasAnimation, "Playables animation graph active (" + goose.Visual.resolver.DescribeAll().Count + " clips)");
            Check(goose.Visual.headBone != null && goose.Visual.leftWingBone != null && goose.Visual.rightWingBone != null, "Head and wing bones found");
            Check(!goose.Visual.Procedural.AllowProceduralWings, "Procedural wing flapping disabled (clips drive the wings)");
            Check(goose.Movement.FloorY == mgr.FloorY, "Goose uses nest floor height");
            Check(GameObject.Find("GooseShadowCatcher") != null, "Shadow catcher follows the goose");
            yield return Shot("07_chase_start");


            // Turn around and run away, sidestepping so the wide crate ends up between goose and player.
            mgr.Mock.AutoTurn = 225f;
            yield return new WaitForSeconds(0.8f);
            mgr.Mock.AutoTurn = 0f;
            var crate = GameObject.Find("MockCrate");
            Collider crateCol = crate != null ? crate.GetComponent<Collider>() : null;
            mgr.Mock.AutoMove = new Vector2(-1f, 0f);
            yield return new WaitForSeconds(1.0f);
            mgr.Mock.AutoMove = new Vector2(0f, 1f);
            t = 0f;
            float lastLog = -1f;
            bool locatorSeen = false;
            bool runFlapShot = false;
            var locator = FindAnyObjectByType<GooseLocator>();
            while (t < 4.2f && mgr.ChaseActive) // shorter than the 6 s tier-1 start: the head start is walk + grace only
            {
                t += Time.deltaTime;
                Sample(mgr, goose, crateCol);
                if (!runFlapShot && goose.Visual.WingLayerWeight > 0.5f && (goose.State == GooseState.Run || goose.State == GooseState.Walk))
                {
                    runFlapShot = true;
                    Vector3 keepFwd = mgr.Player.FlatForward;
                    Vector2 keepMove = mgr.Mock.AutoMove;
                    mgr.Mock.AutoMove = Vector2.zero;
                    mgr.Mock.LookAt(goose.transform.position + Vector3.up * 0.5f);
                    yield return Shot("08b_run_flap");
                    mgr.Mock.TeleportTo(mgr.Player.FlatPosition, keepFwd);
                    mgr.Mock.AutoMove = keepMove;
                }
                if (locator != null && locator.transform.childCount > 0 && locator.transform.GetChild(0).gameObject.activeSelf) locatorSeen = true;
                if (t - lastLog >= 1f)
                {
                    lastLog = t;
                    Debug.Log("[Smoke] t=" + t.ToString("F1") + " state=" + goose.State + " tier=" + goose.Tier + " dist=" + goose.DistanceToPlayer.ToString("F2") +
                              " speed=" + goose.Movement.CurrentSpeed.ToString("F2") + " blocked=" + goose.Avoidance.LastDirectBlocked +
                              " angle=" + goose.Avoidance.LastChosenAngle + " slot=" + goose.Visual.CurrentSlot + " danger=" + goose.Danger01.ToString("F2"));
                }
                yield return null;
            }
            yield return Shot("08_chase_behind");
            mgr.Mock.AutoMove = Vector2.zero;
            Check(m_MaxSpeed > 0.5f, "Goose moved (max speed " + m_MaxSpeed.ToString("F2") + " m/s)");
            Check(locatorSeen, "Locator shown while the goose was off-screen");
            Check(goose.State != GooseState.GameOver, "Player not caught during the head start");

            // The scripted beats (dash, bread, shout) must all happen: no catch until they have, whatever the tuning.
            goose.NoCatch = true;
            // Step onto open floor 5 m from the goose first (tier 1 and its first dash begin at 6 s: turning round next
            // to it would end the round), then turn back to look at it.
            Vector3 gpos = goose.transform.position;
            Vector3 openDir = Vector3.left; // the mock room is open toward -x
            Vector3 spot = new Vector3(gpos.x, mgr.FloorY, gpos.z) + openDir * 5.2f;
            mgr.Mock.TeleportTo(spot, openDir); // still facing away from it
            mgr.Mock.AutoTurn = 225f;
            yield return new WaitForSeconds(0.8f);
            mgr.Mock.AutoTurn = 0f;
            mgr.Mock.LookAt(goose.transform.position + Vector3.up * 0.3f);
            yield return null;
            bool visible = mgr.Player.IsInView(goose.transform.position + Vector3.up * 0.3f);
            Debug.Log("[Smoke] after turning back: goose in view=" + visible + " dist=" + goose.DistanceToPlayer.ToString("F2"));
            Check(visible || goose.DistanceToPlayer < 1.2f, "Goose visible after turning back");
            yield return Shot("09_chase_front");

            // Escalation window: wait here for a flap-dash.
            t = 0f;
            int dashesBefore = goose.DashCount - (goose.DashCount > 0 ? 1 : 0); // a dash during the head start counts
            while (t < 14f && mgr.ChaseActive && goose.DashCount == dashesBefore)
            {
                t += Time.deltaTime;
                Sample(mgr, goose, crateCol);
                if (!runFlapShot && goose.Visual.WingLayerWeight > 0.5f && (goose.State == GooseState.Run || goose.State == GooseState.Walk))
                {
                    runFlapShot = true;
                    yield return Shot("09b_run_flap");
                }
                yield return null;
            }
            Check(goose.Visual.HasWingLayer, "Wing flap layer built (mask over the wing bones)");
            Check(goose.RunFlapCount > 0, "Goose flapped while moving (bursts=" + goose.RunFlapCount + ")");
            if (goose.DashCount > dashesBefore)
            {
                yield return new WaitForSeconds(0.3f);
                yield return Shot("10_dash");
            }
            Check(goose.DashCount > dashesBefore, "Flap-dash happened once tier 1 began (dashes=" + goose.DashCount + ", tier=" + goose.Tier + ", waited " + t.ToString("F1") + " s)");
            Check(m_MaxSpeed <= 1.8f, "Real speed stayed gentle (max " + m_MaxSpeed.ToString("F2") + " m/s)");

            // Bread (thrown when the goose is close): it lands beyond the goose, the goose detours and eats, then comes back angrier.
            t = 0f;
            while (t < 6f && mgr.ChaseActive && (goose.State == GooseState.Dash || goose.State == GooseState.JumpAttack)) { t += Time.deltaTime; yield return null; }
            if (mgr.ChaseActive)
            {
                // Throw from a safe distance so the detour, the meal and the shout all happen before it can reach us.
                Vector3 g2 = goose.transform.position;
                mgr.Mock.TeleportTo(new Vector3(g2.x, mgr.FloorY, g2.z) + openDir * 4.5f, -openDir);
                yield return null;
            }
            mgr.OnBreadPressed();
            Check(mgr.BreadsThrown == 1, "Bread thrown (HUD button / B key)");
            t = 0f;
            bool breadEating = false, breadEaten = false;
            while (t < 14f && mgr.ChaseActive)
            {
                t += Time.deltaTime;
                if (!breadEating && goose.State == GooseState.Eating)
                {
                    breadEating = true;
                    yield return new WaitForSeconds(0.7f);
                    mgr.Mock.LookAt(goose.transform.position + Vector3.up * 0.3f);
                    yield return Shot("07b_bread");
                }
                if (goose.BreadsEaten > 0) { breadEaten = true; break; }
                yield return null;
            }
            Check(breadEating, "Goose went for the bread (Eating observed after " + t.ToString("F1") + " s)");
            Check(breadEaten && goose.Anger >= 1, "Goose finished the bread and got angrier (eaten=" + goose.BreadsEaten + ", anger=" + goose.Anger + ", tier=" + goose.Tier + ")");

            // Yell: the goose flinches, then the reply line plays (bank line when the brain is offline).
            t = 0f;
            while (t < 4f && goose.State != GooseState.Walk && goose.State != GooseState.Run && goose.State != GooseState.Idle) { t += Time.deltaTime; yield return null; }
            int spokenBefore = goose.Voice != null ? goose.Voice.SpokenCount : 0;
            mgr.Yell.SimulateYell();
            yield return null;
            Check(goose.State == GooseState.Flinched, "Goose flinched at the shout (state=" + goose.State + ")");
            yield return new WaitForSeconds(0.5f);
            yield return Shot("08c_yell");
            t = 0f;
            while (t < 6f && goose.Voice != null && goose.Voice.SpokenCount == spokenBefore) { t += Time.deltaTime; yield return null; }
            Check(goose.Voice != null && goose.Voice.SpokenCount > spokenBefore, "Goose answered the shout (spoken=" + (goose.Voice != null ? goose.Voice.SpokenCount : 0) + ")");

            goose.NoCatch = false;
            t = 0f;
            while (mgr.State != GooseGameState.GameOver && t < 40f && goose != null)
            {
                t += Time.deltaTime;
                Sample(mgr, goose, crateCol);
                yield return null;
            }
            Check(mgr.State == GooseGameState.GameOver, "Goose caught the player (after " + t.ToString("F1") + " s, lunges=" + m_LungeCount + ")");
            yield return Shot("11_caught");
            Check(m_PenetrationFrames == 0 || m_PenetrationFrames < m_ChaseFrames * 0.02f,
                "Goose did not pass through the crate (penetration frames " + m_PenetrationFrames + "/" + m_ChaseFrames + ", max depth " + m_MaxPenetrationDepth.ToString("F2") + ")");
            Check(mgr.Haptics.PulseCount > 0, "Haptic pulses requested (" + mgr.Haptics.PulseCount + ")");
            Check(mgr.Chaos != null && mgr.Chaos.HeartbeatCount > 0, "Heartbeat beats fired (" + (mgr.Chaos != null ? mgr.Chaos.HeartbeatCount : 0) + ")");
            Check(mgr.Score.Score >= 1, "Score counted (" + mgr.Score.Score + ")");
            Check(mgr.Score.BestScore >= mgr.Score.Score, "Best score saved (" + mgr.Score.BestScore + ")");
            Check(PlayerPrefs.GetInt("GooseBrawl.BestScore", -1) == mgr.Score.BestScore, "PlayerPrefs persisted");
            bool music = false;
            foreach (var s in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
                if (s.isPlaying && s.clip != null && (s.clip.name.Contains("Chase") || s.clip.name.Contains("Music") || s.clip.name.Contains("Loop") && !s.clip.name.Contains("WingBeat") && !s.clip.name.Contains("Breath"))) music = true;
            Check(!music, "No music playing (design rule)");
            Check(!string.IsNullOrEmpty(mgr.Persona.Name), "Goose has a name (" + mgr.Persona.Name + ", " + mgr.Persona.Title + ", from " + mgr.Persona.Source + ")");
            Check(goose.Voice != null && goose.Voice.SpokenCount >= 3, "Goose spoke its beats (spoken=" + (goose.Voice != null ? goose.Voice.SpokenCount : 0) + ", fallbacks=" + (goose.Voice != null ? goose.Voice.FallbackCount : 0) + ")");
            Check(mgr.Perf != null && mgr.Perf.LastRoundStats != null && mgr.Perf.LastRoundStats.Frames > 0, "PerfProbe captured the round (" + (mgr.Perf != null && mgr.Perf.LastRoundStats != null ? mgr.Perf.LastRoundStats.Summary() : "none") + ")");
            Check(GooseTelemetry.Compiled, "Sentry SDK compiled in (GOOSE_SENTRY)");
            Check(GooseTelemetry.Enabled, "Sentry SDK initialised with the DSN (round transaction, logs and metrics are being sent)");
            Check(mgr.Brain != null && mgr.Brain.Configured, "Goose brain URL configured (" + (mgr.Brain != null ? mgr.Brain.brainBaseUrl : "none") + ")");

            yield return new WaitForSecondsRealtime(2.5f);
            yield return Shot("12_gameover");
            if (online)
            {
                // Goose Board: the results card's POST TO BOARD path end to end (the Worker answers with a rank).
                Check(mgr.BoardAvailable, "Goose Board row available (Worker reachable, run snapshot taken)");
                int rank = 0; bool answered = false;
                mgr.PostRunToBoard("SMOKE", r => { rank = r; answered = true; });
                t = 0f;
                while (!answered && t < 8f) { t += Time.unscaledDeltaTime; yield return null; }
                Check(answered && rank > 0, "Goose Board accepted the run (rank " + rank + ")");
            }
            mgr.OnRunAgainPressed();
            yield return null;
            Check(mgr.State == GooseGameState.EggReady, "Run again -> EggReady");

            // Round 2: outlast the goose (short timer) -> the win card with the sore-loser line.
            mgr.outlastSeconds = 6f;
            mgr.OnStealEggPressed();
            yield return null;
            mgr.RequestSkip();
            mgr.Mock.AutoMove = new Vector2(1f, 0f);
            t = 0f;
            while (mgr.Goose == null && t < 20f) { t += Time.deltaTime; if (mgr.State == GooseGameState.EggStolen) mgr.RequestSkip(); yield return null; }
            mgr.Mock.AutoMove = Vector2.zero;
            var goose2 = mgr.Goose;
            Check(goose2 != null && goose2 != goose, "Second goose spawned for round 2");
            t = 0f;
            while (!mgr.ChaseActive && t < 25f)
            {
                t += Time.deltaTime;
                if (goose2 != null) mgr.Mock.LookAt(goose2.transform.position + Vector3.up * 0.45f);
                yield return null;
            }
            Check(mgr.ChaseActive, "Round 2 chase started");
            if (goose2 != null) goose2.NoCatch = true;
            t = 0f;
            while (mgr.State != GooseGameState.GameOver && t < 25f) { t += Time.deltaTime; yield return null; }
            Check(mgr.State == GooseGameState.GameOver && mgr.LastRoundWon, "Outlasted the goose -> win (after " + t.ToString("F1") + " s)");
            yield return new WaitForSecondsRealtime(3.2f);
            yield return Shot("13_outlasted");
            Check(goose2 != null && goose2.State == GooseState.GameOver, "Goose gave up (terminal state)");
            mgr.OnRunAgainPressed();
            yield return null;
            Check(mgr.State == GooseGameState.EggReady, "Run again after the win -> EggReady");
            Finish();
        }

        void Sample(GooseGameManager mgr, GooseChaseController goose, Collider crate)
        {
            m_ChaseFrames++;
            m_MinDistance = Mathf.Min(m_MinDistance, goose.DistanceToPlayer);
            if (goose.State != GooseState.Dash && goose.State != GooseState.JumpAttack) m_MaxSpeed = Mathf.Max(m_MaxSpeed, goose.Movement.CurrentSpeed);
            if (goose.State == GooseState.JumpAttack && goose.Attack.LungeCount > m_LungeCount) m_LungeCount = goose.Attack.LungeCount;
            if (goose.State == GooseState.Stunned) m_StuckCount++;
            if (crate != null)
            {
                Vector3 p = goose.transform.position;
                Vector3 c = crate.ClosestPoint(p + Vector3.up * 0.3f);
                float d = Vector3.Distance(c, p + Vector3.up * 0.3f);
                if (d < 0.12f)
                {
                    m_PenetrationFrames++;
                    m_MaxPenetrationDepth = Mathf.Max(m_MaxPenetrationDepth, 0.12f - d);
                }
            }
        }

        void Finish()
        {
            var header = m_Failures == 0 ? "[Smoke] PASSED" : "[Smoke] FAILED (" + m_Failures + " checks)";
            Debug.Log(header + "\n" + m_Report + "  stats: minDist=" + m_MinDistance.ToString("F2") + " maxSpeed=" + m_MaxSpeed.ToString("F2") +
                      " lunges=" + m_LungeCount + " stunnedFrames=" + m_StuckCount + "\n[Smoke] END");
            StartCoroutine(ExitSoon());
        }

        IEnumerator ExitSoon()
        {
            yield return new WaitForSeconds(1f);
            UnityEditor.EditorApplication.ExitPlaymode();
        }
    }
}
#endif
