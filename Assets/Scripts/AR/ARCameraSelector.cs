using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GooseBrawl
{
    /// <summary>
    /// Picks the camera video format with the widest view: the ultra-wide (0.5x) camera when ARKit
    /// offers it for world tracking, otherwise the 4:3 format at the highest frame rate. Uses a tiny
    /// native helper (Assets/Plugins/iOS/GooseCamera.mm) because AR Foundation does not expose the
    /// capture device of a configuration. Everything degrades to "keep the default" silently.
    /// </summary>
    public class ARCameraSelector : MonoBehaviour
    {
        public bool preferUltraWide = true;
        public bool preferHighFrameRate = true;

        public bool UltraWideSelected { get; private set; }
        public string Report { get; private set; } = "";

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int GooseCamera_FormatInfo(IntPtr handle, out int isUltraWide, out int width, out int height, out int fps);
        [DllImport("__Internal")] static extern int GooseCamera_SupportedFormatCount(out int ultraWideCount);
#else
        static int GooseCamera_FormatInfo(IntPtr handle, out int isUltraWide, out int width, out int height, out int fps)
        {
            isUltraWide = 0; width = 0; height = 0; fps = 0;
            return 0;
        }
        static int GooseCamera_SupportedFormatCount(out int ultraWideCount)
        {
            ultraWideCount = 0;
            return 0;
        }
#endif

        public IEnumerator SelectBestConfiguration(ARCameraManager cameraManager)
        {
            if (cameraManager == null) yield break;
            float t = 0f;
            while ((ARSession.state < ARSessionState.SessionInitializing || cameraManager.currentFacingDirection != CameraFacingDirection.World) && t < 8f)
            {
                if (cameraManager.requestedFacingDirection != CameraFacingDirection.World) yield break;
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.5f);
            if (cameraManager.requestedFacingDirection != CameraFacingDirection.World || cameraManager.currentFacingDirection != CameraFacingDirection.World) yield break;

            NativeArray<XRCameraConfiguration> configs;
            try
            {
                configs = cameraManager.GetConfigurations(Allocator.Temp);
            }
            catch (Exception e)
            {
                Report = "Camera configurations unavailable: " + e.Message;
                GooseLog.Info(Report);
                yield break;
            }

            try
            {
                int ultraWideAvailable = 0;
                try { GooseCamera_SupportedFormatCount(out ultraWideAvailable); } catch { ultraWideAvailable = 0; }

                var sb = new StringBuilder();
                sb.Append("Camera formats (").Append(configs.Length).Append(", ultra-wide available: ").Append(ultraWideAvailable).Append("): ");
                XRCameraConfiguration? best = null;
                float bestScore = float.MinValue;
                bool bestUltra = false;
                for (int i = 0; i < configs.Length; i++)
                {
                    var c = configs[i];
                    int uw = 0, w = c.width, h = c.height, fps = c.framerate ?? 30;
                    try
                    {
                        if (GooseCamera_FormatInfo(c.nativeConfigurationHandle, out int nUw, out int nW, out int nH, out int nFps) == 1)
                        {
                            uw = nUw;
                            if (nW > 0) w = nW;
                            if (nH > 0) h = nH;
                            if (nFps > 0) fps = nFps;
                        }
                    }
                    catch { uw = 0; }
                    sb.Append(w).Append('x').Append(h).Append('@').Append(fps).Append(uw == 1 ? " (ultra-wide) " : " ");

                    float aspect = h > 0 ? (float)w / h : 1.78f;
                    float score = 0f;
                    if (preferUltraWide && uw == 1) score += 100f;
                    score += (1.9f - aspect) * 10f;                         // 4:3 shows more than 16:9
                    if (preferHighFrameRate) score += fps >= 60 ? 5f : 0f;
                    score -= Mathf.Abs(w - 1920) / 1000f;                   // stay near 1080p for performance
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = c;
                        bestUltra = uw == 1;
                    }
                }
                Report = sb.ToString();
                GooseLog.Info(Report);

                if (best.HasValue && configs.Length > 1)
                {
                    var current = cameraManager.currentConfiguration;
                    if (!current.HasValue || !current.Value.Equals(best.Value))
                    {
                        try
                        {
                            cameraManager.currentConfiguration = best.Value;
                            UltraWideSelected = bestUltra;
                            GooseLog.Info("Camera format set to " + best.Value.width + "x" + best.Value.height + "@" + (best.Value.framerate ?? 0) + (bestUltra ? " ultra-wide" : ""));
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning("[GooseBrawl] Could not switch camera format: " + e.Message);
                        }
                    }
                }
            }
            finally
            {
                if (configs.IsCreated) configs.Dispose();
            }
        }
    }
}
