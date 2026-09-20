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
                // The share sheet's "Save Image" and the on-device speech recogniser each need their purpose string.
                plist.root.SetString("NSPhotoLibraryAddUsageDescription", "GOOSED. saves your photo with the goose to Photos.");
                plist.root.SetString("NSSpeechRecognitionUsageDescription", "GOOSED. checks whether you shouted the goose's name. Recognition stays on the phone.");
                plist.WriteToFile(plistPath);

                string projPath = PBXProject.GetPBXProjectPath(path);
                var proj = new PBXProject();
                proj.ReadFromFile(projPath);
                string frameworkTarget = proj.GetUnityFrameworkTargetGuid();
                proj.AddFrameworkToProject(frameworkTarget, "CoreHaptics.framework", false);
                proj.AddFrameworkToProject(frameworkTarget, "Speech.framework", false);
                proj.WriteToFile(projPath);
                Debug.Log("[Goose Brawl] Post-build: display name " + GooseBrawlSetup.DisplayName + ", CoreHaptics + Speech linked, Photos/Speech usage strings set.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Goose Brawl] Post-build step skipped: " + e.Message);
            }
        }
    }
}
#endif
