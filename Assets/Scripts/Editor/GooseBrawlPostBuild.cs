#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace GooseBrawl.Editor
{
    /// <summary>
    /// After the Xcode project is generated: the home-screen label is the brand mark "GOOSED." (the product name
    /// itself stays "GOOSED" so the bundle path has no trailing dot), and the Core Haptics framework is linked.
    /// </summary>
    public static class GooseBrawlPostBuild
    {
        [PostProcessBuild(100)]
        public static void OnPostProcessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;
            try
            {
                string plistPath = Path.Combine(path, "Info.plist");
                var plist = new PlistDocument();
                plist.ReadFromFile(plistPath);
                plist.root.SetString("CFBundleDisplayName", GooseBrawlSetup.DisplayName);
                plist.root.SetBoolean("UIRequiresFullScreen", true);
                plist.WriteToFile(plistPath);

                string projPath = PBXProject.GetPBXProjectPath(path);
                var proj = new PBXProject();
                proj.ReadFromFile(projPath);
                string frameworkTarget = proj.GetUnityFrameworkTargetGuid();
                proj.AddFrameworkToProject(frameworkTarget, "CoreHaptics.framework", false);
                proj.WriteToFile(projPath);
                Debug.Log("[Goose Brawl] Post-build: display name " + GooseBrawlSetup.DisplayName + ", CoreHaptics linked.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Goose Brawl] Post-build step skipped: " + e.Message);
            }
        }
    }
}
#endif
