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
            if ((cmd == "play" || cmd == "smoke" || cmd == "setup" || cmd == "build") && EditorApplication.isPlaying)
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
                case "smoke":
                    SessionState.SetBool(SmokeFlag, true);
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
                case "settings":
                    GooseBrawlSetup.ConfigurePlayerSettings();
                    AssetDatabase.SaveAssets();
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
        }
    }
}
