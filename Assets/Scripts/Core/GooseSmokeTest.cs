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
            mgr.Mock.AutoMove = new Vector2(1f, 0f); // sidestep into open floor
            float t = 0f;
            while (mgr.Nest.Egg.CrackedEgg == null && t < 12f) { t += Time.deltaTime; yield return null; }
            mgr.Mock.AutoMove = Vector2.zero;
            Check(t < 6f, "Grip drained from movement within " + t.ToString("F1") + " s");
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
            while (t < 6f && mgr.ChaseActive)
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

            // Turn back to look at it.
            mgr.Mock.AutoTurn = 225f;
            yield return new WaitForSeconds(0.8f);
            mgr.Mock.AutoTurn = 0f;
            yield return null;
            bool visible = mgr.Player.IsInView(goose.transform.position + Vector3.up * 0.3f);
            Debug.Log("[Smoke] after turning back: goose in view=" + visible + " dist=" + goose.DistanceToPlayer.ToString("F2"));
            Check(visible || goose.DistanceToPlayer < 1.2f, "Goose visible after turning back");
            yield return Shot("09_chase_front");

            // Escalation window: put the player on open floor 5 m from the goose, facing it, and wait for a flap-dash.
            Vector3 gpos = goose.transform.position;
            Vector3 openDir = Vector3.left; // the mock room is open toward -x
            Vector3 spot = new Vector3(gpos.x, mgr.FloorY, gpos.z) + openDir * 5.2f;
            mgr.Mock.TeleportTo(spot, -openDir);
            t = 0f;
            int dashesBefore = goose.DashCount;
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

            t = 0f;
            while (mgr.State != GooseGameState.GameOver && t < 40f)
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

            yield return new WaitForSecondsRealtime(2.5f);
            yield return Shot("12_gameover");
            mgr.OnRunAgainPressed();
            yield return null;
            Check(mgr.State == GooseGameState.EggReady, "Run again -> EggReady");
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
