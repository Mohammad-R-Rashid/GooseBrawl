using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GooseBrawl
{
    /// <summary>
    /// The take-home: a frame of the goose with the GOOSED. stamp (the slow-motion tackle, the sulk after a win, or a
    /// shot the player frames themselves in photo mode) and the iOS share sheet (AirDrop, Messages, Save Image).
    /// The capture reads the final back buffer at the end of a frame in which the overlay is hidden and the stamp shown.
    /// </summary>
    public class GooseShareService : MonoBehaviour
    {
        public Texture2D Shot { get; private set; }
        public bool HasShot => Shot != null;
        public string LastPath { get; private set; }
        public int ShotCount { get; private set; }

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void GooseShare_Image(string pngPath, string text);
#endif

        public void SetShot(Texture2D tex)
        {
            if (tex == null) return;
            ClearShot();
            Shot = tex;
            ShotCount++;
#if UNITY_EDITOR
            // No share sheet in the Editor: write the PNG straight away so the frame can be inspected (Library/ShareShots).
            LastPath = WritePng();
            GooseLog.Info("[Share] saved " + LastPath);
#endif
        }

        public void ClearShot()
        {
            if (Shot != null) Destroy(Shot);
            Shot = null;
            LastPath = null;
        }

        /// <summary>Hide the overlay, show the stamp, grab the rendered frame, restore. Takes one frame.</summary>
        public IEnumerator Capture(string line1, string line2, Action<Texture2D> done)
        {
            var ui = GooseGameManager.Instance != null ? GooseGameManager.Instance.UI : null;
            if (ui != null) { ui.SetOverlayVisible(false); ui.ShowStamp(line1, line2); }
            yield return new WaitForEndOfFrame();
            Texture2D tex = null;
            try { tex = ScreenCapture.CaptureScreenshotAsTexture(); }
            catch (Exception e) { GooseTelemetry.CaptureException(e, "share.capture"); }
            if (ui != null) { ui.HideStamp(); ui.SetOverlayVisible(true); }
            if (tex != null) tex.name = "GooseShot";
            done?.Invoke(tex);
        }

        /// <summary>Open the share sheet with the last shot (Editor: logs the PNG path).</summary>
        public void ShareLast(string text)
        {
            if (Shot == null) return;
            if (string.IsNullOrEmpty(LastPath) || !File.Exists(LastPath)) LastPath = WritePng();
            if (LastPath == null) return;
#if UNITY_IOS && !UNITY_EDITOR
            try { GooseShare_Image(LastPath, text ?? ""); }
            catch (Exception e) { GooseTelemetry.CaptureException(e, "share.sheet"); }
#else
            GooseLog.Info("[Share] " + LastPath + "  |  " + text);
#endif
        }

        string WritePng()
        {
            if (Shot == null) return null;
            try
            {
                byte[] png = ImageConversion.EncodeToPNG(Shot);
                string file = "goosed_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png";
#if UNITY_EDITOR
                string dir = Path.Combine(Directory.GetCurrentDirectory(), "Library", "ShareShots");
#else
                string dir = Application.temporaryCachePath;
#endif
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, file);
                File.WriteAllBytes(path, png);
                return path;
            }
            catch (Exception e)
            {
                GooseTelemetry.CaptureException(e, "share.encode");
                return null;
            }
        }

        void OnDestroy() { ClearShot(); }
    }
}
