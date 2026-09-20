using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

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
        public bool Capturing { get; private set; }
        public bool LastCaptureAsync { get; private set; }
        RenderTexture m_CaptureTarget, m_ReadbackTarget;
        int m_ShotGeneration;

        public void PrepareCapture()
        {
            if (Capturing || (m_CaptureTarget != null && m_CaptureTarget.width == Screen.width && m_CaptureTarget.height == Screen.height)) return;
            if (m_CaptureTarget != null) { m_CaptureTarget.Release(); Destroy(m_CaptureTarget); }
            m_CaptureTarget = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32) { name = "GooseCapture", useMipMap = false };
            m_CaptureTarget.Create();
            if (m_ReadbackTarget != null) { m_ReadbackTarget.Release(); Destroy(m_ReadbackTarget); }
            m_ReadbackTarget = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32) { name = "GooseCaptureUpright", useMipMap = false };
            m_ReadbackTarget.Create();
        }

        bool BeginReadback(out AsyncGPUReadbackRequest request)
        {
            request = default;
            try
            {
                ScreenCapture.CaptureScreenshotIntoRenderTexture(m_CaptureTarget);
                // Metal's screenshot texture is GPU-oriented. Flip on the GPU before copying to CPU pixels.
                if (SystemInfo.graphicsUVStartsAtTop)
                    Graphics.Blit(m_CaptureTarget, m_ReadbackTarget, new Vector2(1f, -1f), new Vector2(0f, 1f));
                else Graphics.Blit(m_CaptureTarget, m_ReadbackTarget);
                request = AsyncGPUReadback.Request(m_ReadbackTarget, 0, TextureFormat.RGBA32);
                return true;
            }
            catch (Exception e) { GooseTelemetry.CaptureException(e, "share.capture"); return false; }
        }

        public void CaptureResult(string line1, string line2) => StartCoroutine(StoreResult(line1, line2));

        IEnumerator StoreResult(string line1, string line2)
        {
            int generation = m_ShotGeneration;
            yield return Capture(line1, line2, tex =>
            {
                if (generation != m_ShotGeneration) { if (tex != null) Destroy(tex); return; }
                SetShot(tex);
                var mgr = GooseGameManager.Instance;
                if (mgr != null && mgr.State == GooseGameState.GameOver) mgr.UI.SetShareAvailable(HasShot);
            });
        }

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
            m_ShotGeneration++;
            if (Shot != null) Destroy(Shot);
            Shot = null;
            LastPath = null;
        }

        /// <summary>Read the stamped frame asynchronously, without blocking the catch animation on the GPU.</summary>
        public IEnumerator Capture(string line1, string line2, Action<Texture2D> done)
        {
            while (Capturing) yield return null;
            PrepareCapture();
            Capturing = true;
            var ui = GooseGameManager.Instance != null ? GooseGameManager.Instance.UI : null;
            Texture2D tex = null;
            try
            {
                if (ui != null) { ui.SetOverlayVisible(false); ui.ShowStamp(line1, line2); }
                yield return new WaitForEndOfFrame();
                LastCaptureAsync = SystemInfo.supportsAsyncGPUReadback;
                if (LastCaptureAsync)
                {
                    bool started = BeginReadback(out var readback);
                    // Restore immediately; never leave the UI hidden while readback is in flight.
                    if (ui != null) { ui.HideStamp(); ui.SetOverlayVisible(true); }
                    while (started && !readback.done) yield return null;
                    if (started && !readback.hasError)
                    {
                        tex = new Texture2D(m_CaptureTarget.width, m_CaptureTarget.height, TextureFormat.RGBA32, false);
                        tex.LoadRawTextureData(readback.GetData<byte>());
                        // Encoding uses the CPU pixels. Avoid uploading the screenshot back to the GPU.
                    }
                }
                else
                {
                    try { tex = ScreenCapture.CaptureScreenshotAsTexture(); }
                    catch (Exception e) { GooseTelemetry.CaptureException(e, "share.capture"); }
                }
            }
            finally
            {
                if (ui != null) { ui.HideStamp(); ui.SetOverlayVisible(true); }
                Capturing = false;
            }
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

        void OnDestroy()
        {
            ClearShot();
            if (m_CaptureTarget != null) { m_CaptureTarget.Release(); Destroy(m_CaptureTarget); }
            if (m_ReadbackTarget != null) { m_ReadbackTarget.Release(); Destroy(m_ReadbackTarget); }
        }
    }
}
