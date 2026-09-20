#if GOOSE_SENTRY
using Sentry.Unity;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// Runtime Sentry options for GOOSED. (asset: Assets/Resources/Sentry/GooseSentryOptions.asset, referenced by the
    /// SDK's SentryOptions asset). Tracing at 100% (a demo day is a few hundred rounds), structured logs on, the
    /// environment tells Editor-mock rounds from phone rounds so the benchmark dashboards can be filtered.
    /// </summary>
    [CreateAssetMenu(fileName = "GooseSentryOptions", menuName = "Goose Brawl/Sentry Options Configuration")]
    public class GooseSentryOptions : SentryOptionsConfiguration
    {
        public override void Configure(SentryUnityOptions options)
        {
            options.TracesSampleRate = 1.0;
            options.EnableLogs = true;
            options.Environment = Application.isEditor ? "editor-mock" : (Debug.isDebugBuild ? "device-dev" : "device");
            options.Release = "goosed@1.0.0";
            options.AutoSessionTracking = true;
            options.DefaultTags["device_model"] = SystemInfo.deviceModel;
            options.DefaultTags["os"] = SystemInfo.operatingSystem;
            options.DefaultTags["lidar"] = (SystemInfo.deviceModel.Contains("iPhone1") && !SystemInfo.deviceModel.Contains("iPhone11")).ToString();
        }
    }
}
#endif
