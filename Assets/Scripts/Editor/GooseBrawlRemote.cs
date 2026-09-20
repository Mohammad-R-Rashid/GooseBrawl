using System.IO;
using UnityEditor;
using UnityEngine;

namespace GooseBrawl.Editor
{
    /// <summary>
    /// Tiny file-based remote control for automation: write a command into Library/GooseBrawlCommand.txt
    /// (play, stop, smoke, setup, validate) and the editor executes it on its next update tick.
    /// Used to drive the mock smoke test without touching the editor UI.
    /// </summary>
    [InitializeOnLoad]
    public static class GooseBrawlRemote
    {
        const string CommandPath = "Library/GooseBrawlCommand.txt";
        public const string SmokeFlag = "GooseBrawl.SmokeTest";
        public const string BenchFlag = "GooseBrawl.Bench";
        public const string SmokeOnlineFlag = "GooseBrawl.SmokeOnline";
        public const string IconFlag = "GooseBrawl.IconShot";
        static double s_NextPoll;

        static GooseBrawlRemote()
        {
            EditorApplication.update += Poll;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < s_NextPoll) return;
            s_NextPoll = EditorApplication.timeSinceStartup + 0.5;
            if (!File.Exists(CommandPath)) return;
            string cmd;
            try
            {
                cmd = File.ReadAllText(CommandPath).Trim().ToLowerInvariant();
                File.Delete(CommandPath);
            }
            catch
            {
                return;
            }
            if (string.IsNullOrEmpty(cmd)) return;
            // Commands that need edit mode: leave play mode first and re-queue.
            if ((cmd == "play" || cmd == "smoke" || cmd == "smokeonline" || cmd == "bench" || cmd == "icon" || cmd == "setup" || cmd == "build" || cmd == "tune") && EditorApplication.isPlaying)
            {
                EditorApplication.ExitPlaymode();
                File.WriteAllText(CommandPath, cmd);
                s_NextPoll = EditorApplication.timeSinceStartup + 2.0;
                return;
            }
            Debug.Log("[Goose Brawl Remote] command: " + cmd);
            switch (cmd)
            {
                case "play":
                    SessionState.SetBool(SmokeFlag, false);
                    GooseBrawlSetup.OpenScene();
                    UsePhoneResolution();
                    EditorApplication.EnterPlaymode();
                    break;
                case "smokeonline":
                    SessionState.SetBool(SmokeFlag, true);
                    SessionState.SetBool(SmokeOnlineFlag, true);
                    GooseBrawlSetup.OpenScene();
                    UsePhoneResolution();
                    EditorApplication.EnterPlaymode();
                    break;
                case "smoke":
                    SessionState.SetBool(SmokeFlag, true);
                    SessionState.SetBool(SmokeOnlineFlag, false);
                    GooseBrawlSetup.OpenScene();
                    UsePhoneResolution();
                    EditorApplication.EnterPlaymode();
                    break;
                case "icon":
                    SessionState.SetBool(SmokeFlag, false);
                    SessionState.SetBool(IconFlag, true);
                    GooseBrawlSetup.OpenScene();
                    try { PlayModeWindow.SetViewType(PlayModeWindow.PlayModeViewTypes.GameView); PlayModeWindow.SetCustomRenderingResolution(1024, 1024, "Icon 1024"); } catch (System.Exception e) { Debug.LogWarning(e.Message); }
                    EditorApplication.EnterPlaymode();
                    break;
                case "bench":
                    SessionState.SetBool(SmokeFlag, false);
                    SessionState.SetBool(BenchFlag, true);
                    GooseBrawlSetup.OpenScene();
                    UsePhoneResolution();
                    EditorApplication.EnterPlaymode();
                    break;
                case "stop":
                    EditorApplication.ExitPlaymode();
                    break;
                case "setup":
                    GooseBrawlSetup.RunSetup(force: true);
                    break;
                case "validate":
                    GooseBrawlValidator.Validate();
                    break;
                case "save":
                    AssetDatabase.SaveAssets();
                    break;
                case "build":
                    GooseBrawlSetup.BuildIOS();
                    break;
                case "sentry":
                    GooseBrawlSetup.ConfigureSentry();
                    break;
                case "layers":
                    GooseBrawlSetup.EnsureLayers();
                    AssetDatabase.SaveAssets();
                    break;
                case "pipeline":
                    GooseBrawlSetup.ConfigureRenderPipeline();
                    AssetDatabase.SaveAssets();
                    break;
                case "audio":
                    GooseBrawlSetup.ConfigureAudioImports();
                    AssetDatabase.SaveAssets();
                    break;
                case "export":
                    GooseVoiceExport.Export();
                    break;
                case "settings":
                    GooseBrawlSetup.ConfigurePlayerSettings();
                    AssetDatabase.SaveAssets();
                    break;
                case "tune":
                    GooseBrawlTune.Apply();
                    break;
            }
        }

        /// <summary>Render the Game view at iPhone 15 Pro portrait resolution so layout and screenshots match the device.</summary>
        static void UsePhoneResolution()
        {
            try
            {
                PlayModeWindow.SetViewType(PlayModeWindow.PlayModeViewTypes.GameView);
                PlayModeWindow.SetCustomRenderingResolution(1179, 2556, "iPhone 15 Pro");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Goose Brawl Remote] Could not set the Game view resolution: " + e.Message);
            }
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(SmokeFlag, false))
            {
                SessionState.SetBool(SmokeFlag, false);
                var go = new GameObject("GooseSmokeTest");
                go.AddComponent<GooseSmokeTest>();
            }
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(IconFlag, false))
            {
                SessionState.SetBool(IconFlag, false);
                var go = new GameObject("GooseIconShot");
                go.AddComponent<GooseIconShot>();
            }
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(BenchFlag, false))
            {
                SessionState.SetBool(BenchFlag, false);
                var go = new GameObject("GooseBenchStarter");
                go.AddComponent<GooseBenchStarter>();
            }
        }
    }
}
