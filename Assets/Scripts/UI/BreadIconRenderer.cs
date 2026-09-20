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
            roll.AddComponent<MeshFilter>().sharedMesh = ProceduralAssets.BreadRollMesh("BreadIconRoll", BreadController.Width, BreadController.Height, 3);
            var mr = roll.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mats != null ? mats.Bread : ProceduralAssets.LitMaterial("Bread_Runtime", new Color(0.83f, 0.6f, 0.33f), 0.15f);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            roll.transform.localRotation = Quaternion.Euler(-32f, 0f, 0f);
            r.m_Roll = roll.transform;

            // A small warm light of its own so the icon reads the same in a dark room.
            var lightGo = new GameObject("Light");
            lightGo.transform.SetParent(go.transform, false);
            lightGo.transform.localPosition = new Vector3(0.12f, 0.25f, -0.15f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.93f, 0.8f);
            light.intensity = 4.5f;
            light.range = 1.5f;
            light.cullingMask = 1 << layer;
            light.shadows = LightShadows.None;

            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(go.transform, false);
            fillGo.transform.localPosition = new Vector3(-0.2f, 0.12f, -0.22f);
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Point;
            fill.color = new Color(0.95f, 0.97f, 1f);
            fill.intensity = 2.2f;
            fill.range = 1.2f;
            fill.cullingMask = 1 << layer;
            fill.shadows = LightShadows.None;

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

        /// <summary>No other camera may draw the icon roll (the AR camera and the mock camera cull the UIBread layer).</summary>
        void CullFromOtherCameras()
        {
            if (m_Layer < 0) return;
            int bit = 1 << m_Layer;
            foreach (var cam in Camera.allCameras)
                if (cam != m_Cam && (cam.cullingMask & bit) != 0) cam.cullingMask &= ~bit;
        }

        void OnDestroy()
        {
            if (Texture != null) { Texture.Release(); Destroy(Texture); }
        }
    }
}
