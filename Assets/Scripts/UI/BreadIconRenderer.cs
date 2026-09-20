using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GooseBrawl
{
    /// <summary>
    /// The bread button shows the real bread roll (the same lathe mesh the player throws), rendered by a tiny extra camera
    /// into a transparent 256x256 texture that a RawImage displays. No emoji, no text: an actual asset, slowly turning.
    /// The roll lives on its own layer far below the play space and every other camera culls that layer.
    /// </summary>
    public class BreadIconRenderer : MonoBehaviour
    {
        public const string LayerName = "UIBread";
        public RenderTexture Texture { get; private set; }
        public bool Ready => Texture != null && m_Cam != null;

        Camera m_Cam;
        Transform m_Roll;
        int m_Layer = -1;
        float m_NextCull;
        Camera[] m_Cameras = new Camera[4];
        Material m_IconMaterial;

        public bool Rendering => m_Cam != null && m_Cam.enabled;
        public void SetVisible(bool visible) { if (m_Cam != null) m_Cam.enabled = visible; }

        public static BreadIconRenderer Create(MaterialLibrary mats)
        {
            int layer = LayerMask.NameToLayer(LayerName);
            if (layer < 0)
            {
                GooseLog.Warn("Layer '" + LayerName + "' missing (run Goose Brawl > Setup Project); the bread button keeps its flat icon.");
                return null;
            }
            var go = new GameObject("BreadIcon");
            go.transform.position = new Vector3(0f, -80f, 0f);
            var r = go.AddComponent<BreadIconRenderer>();
            r.m_Layer = layer;
            r.Texture = new RenderTexture(256, 256, 16, RenderTextureFormat.ARGB32) { name = "BreadIconRT", antiAliasing = 1 };
            r.Texture.Create();

            var roll = new GameObject("Roll");
            roll.transform.SetParent(go.transform, false);
            roll.layer = layer;
            roll.AddComponent<MeshFilter>().sharedMesh = ProceduralAssets.ToastSliceMesh("BreadIconSlice", BreadController.Width, BreadController.SliceHeight, BreadController.Height);
            var mr = roll.AddComponent<MeshRenderer>();
            // Unlit: the toast texture carries its own crust shading, and the icon must read the same in any room
            // (the rig's point lights at 30 cm blew a lit face out to white).
            var iconMat = ProceduralAssets.UnlitTransparent("BreadIcon_Runtime", Color.white, ProceduralAssets.ToastTexture());
            r.m_IconMaterial = iconMat;
            iconMat.SetTextureScale("_BaseMap", Vector2.one);
            iconMat.SetTextureScale("_MainTex", Vector2.one);
            mr.sharedMaterial = iconMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            // Upright, turned a little toward the key light: the classic toast icon with some depth on the crust.
            roll.transform.localPosition = new Vector3(0f, 0.022f, 0f);
            roll.transform.localRotation = Quaternion.Euler(-10f, 200f, 0f);
            r.m_Roll = roll.transform;

            var camGo = new GameObject("BreadIconCamera");
            camGo.transform.SetParent(go.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 0.14f, -0.2f);
            camGo.transform.LookAt(go.transform.position + Vector3.up * 0.022f);
            r.m_Cam = camGo.AddComponent<Camera>();
            r.m_Cam.clearFlags = CameraClearFlags.SolidColor;
            r.m_Cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            r.m_Cam.cullingMask = 1 << layer;
            r.m_Cam.fieldOfView = 34f;
            r.m_Cam.nearClipPlane = 0.02f;
            r.m_Cam.farClipPlane = 2f;
            r.m_Cam.depth = -20f;
            r.m_Cam.allowHDR = false;
            r.m_Cam.allowMSAA = false;
            r.m_Cam.targetTexture = r.Texture;
            r.m_Cam.enabled = false;
            var data = r.m_Cam.GetUniversalAdditionalCameraData();
            if (data != null)
            {
                data.renderType = CameraRenderType.Base;
                data.renderPostProcessing = false;
                data.renderShadows = false;
                data.requiresColorOption = CameraOverrideOption.Off;
                data.requiresDepthOption = CameraOverrideOption.Off;
            }
            r.CullFromOtherCameras();
            return r;
        }

        void Update()
        {
            if (m_Roll != null) m_Roll.Rotate(0f, Time.unscaledDeltaTime * 28f, 0f, Space.World);
            if (Time.unscaledTime >= m_NextCull) { m_NextCull = Time.unscaledTime + 2f; CullFromOtherCameras(); }
        }

        void LateUpdate()
        {
            // Visibility is final after the HUD's Update. Enable the camera before this frame renders.
            var mgr = GooseGameManager.Instance;
            SetVisible(mgr != null && mgr.UI != null && mgr.UI.BreadVisible);
        }

        /// <summary>No other camera may draw the icon roll (the AR camera and the mock camera cull the UIBread layer).</summary>
        void CullFromOtherCameras()
        {
            if (m_Layer < 0) return;
            int bit = 1 << m_Layer;
            int needed = Camera.allCamerasCount;
            if (m_Cameras.Length < needed) m_Cameras = new Camera[Mathf.NextPowerOfTwo(needed)];
            int count = Camera.GetAllCameras(m_Cameras);
            for (int i = 0; i < count; i++)
            {
                var cam = m_Cameras[i];
                if (cam != m_Cam && (cam.cullingMask & bit) != 0) cam.cullingMask &= ~bit;
            }
        }

        void OnDestroy()
        {
            if (Texture != null) { Texture.Release(); Destroy(Texture); }
            if (m_IconMaterial != null) Destroy(m_IconMaterial);
        }
    }
}
