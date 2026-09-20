#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace GooseBrawl
{
    /// <summary>
    /// Editor-only: renders the app icon from the real goose model. The mock world is hidden, a yolk backdrop and a cream
    /// disc stand behind the goose, the post-FX volume is muted, and the mock camera frames the head in a three-quarter view
    /// while the procedural head tracking makes it glare at the lens. Several poses are captured to Library/IconShots so a
    /// human can pick one and copy it to Assets/Art/Generated/AppIcon.png. Remote command: `icon` (Game view 1024x1024).
    /// </summary>
    public class GooseIconShot : MonoBehaviour
    {
        const string Dir = "Library/IconShots";

        IEnumerator Start()
        {
            yield return null;
            var mgr = GooseGameManager.Instance;
            if (mgr == null || !GooseGameManager.UseMockAR || mgr.Mock == null) { Debug.LogError("[IconShot] needs the Editor mock."); Finish(); yield break; }
            Directory.CreateDirectory(Dir);

            // Clean stage: no mock room, no UI, no vignette / grain / distortion.
            var world = GameObject.Find("MockWorld");
            if (world != null) world.SetActive(false);
            if (mgr.UI != null) mgr.UI.gameObject.SetActive(false);
            foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None)) v.weight = 0f;
            var mats = mgr.Materials;

            // Backdrop: yolk plane far behind, cream disc close behind the head (placed along the view direction once the head is known).
            var yolk = new Color(0.98f, 0.72f, 0.14f);
            var cream = new Color(1f, 0.965f, 0.87f);
            var back = GameObject.CreatePrimitive(PrimitiveType.Quad);
            back.name = "IconBackdrop";
            back.transform.localScale = new Vector3(14f, 14f, 1f);
            back.GetComponent<Renderer>().sharedMaterial = ProceduralAssets.UnlitTransparent("IconYolk", yolk, null);
            Destroy(back.GetComponent<Collider>());
            var disc = new GameObject("IconDisc");
            disc.AddComponent<MeshFilter>().sharedMesh = ProceduralAssets.DiscMesh("IconDisc", 0.55f, 0f, 1, 64);
            disc.AddComponent<MeshRenderer>().sharedMaterial = ProceduralAssets.UnlitTransparent("IconCream", cream, null);

            // The goose (always the white one), standing at the origin.
            var go = mgr.goosePrefab != null ? Instantiate(mgr.goosePrefab) : GoosePlaceholderFactory.CreateRuntimeGoose(mats);
            var goose = go.GetComponent<GooseChaseController>();
            if (goose == null) goose = go.AddComponent<GooseChaseController>();
            goose.Initialize(mgr);
            if (mats != null && mats.gooseMaterial != null) goose.Visual.ApplyMaterial(mats.gooseMaterial);
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.Euler(0f, 200f, 0f);
            goose.Movement.ExternalControl = true;
            yield return null;

            var cam = mgr.Mock.MockCamera;
            cam.backgroundColor = yolk;
            var key = mgr.Look != null ? mgr.Look.keyLight : null;
            if (key != null) { key.transform.rotation = Quaternion.Euler(38f, 215f, 0f); key.intensity = Mathf.Max(key.intensity, 1.4f); }

            var poses = new (string name, GooseAnimationResolver.Slot slot, float speed, bool flap, float wait, float fov, Vector3 offset)[]
            {
                ("glare", GooseAnimationResolver.Slot.Idle, 1f, false, 1.2f, 24f, new Vector3(0.62f, 0.14f, -0.62f)),
                ("glare_close", GooseAnimationResolver.Slot.Idle, 1f, false, 0.6f, 18f, new Vector3(0.55f, 0.10f, -0.55f)),
                ("hiss", GooseAnimationResolver.Slot.Flap, 0.6f, true, 0.7f, 28f, new Vector3(0.75f, 0.2f, -0.75f)),
                ("profile", GooseAnimationResolver.Slot.Idle, 1f, false, 0.8f, 24f, new Vector3(0.9f, 0.1f, -0.15f)),
            };
            foreach (var pose in poses)
            {
                goose.Visual.Play(pose.slot, 0.05f, pose.speed, true);
                goose.Visual.Procedural.Flapping = pose.flap;
                goose.Visual.Procedural.FlapIntensity = 0.7f;
                yield return new WaitForSeconds(pose.wait);
                Vector3 head = goose.Visual.headBone != null ? goose.Visual.headBone.position : go.transform.position + Vector3.up * 0.85f;
                Vector3 camPos = head + pose.offset;
                Vector3 viewDir = (head - camPos).normalized;
                // The mock rig owns the camera transform: set its eye height and aim it through the mock API.
                mgr.Mock.eyeHeight = camPos.y;
                mgr.Mock.TeleportTo(new Vector3(camPos.x, 0f, camPos.z), viewDir);
                mgr.Mock.LookAt(head + Vector3.up * 0.01f);
                cam.fieldOfView = pose.fov;
                goose.Visual.Procedural.HasLookTarget = true;
                goose.Visual.Procedural.LookTarget = camPos;
                back.transform.position = head + viewDir * 3f;
                back.transform.rotation = Quaternion.LookRotation(viewDir);
                disc.transform.position = head + viewDir * 0.9f - Vector3.up * 0.05f;
                disc.transform.rotation = Quaternion.FromToRotation(Vector3.up, -viewDir);
                for (int i = 0; i < 14; i++) yield return null;
                string file = Path.Combine(Dir, "icon_" + pose.name + ".png");
                ScreenCapture.CaptureScreenshot(file);
                yield return new WaitForEndOfFrame();
                yield return null;
                Debug.Log("[IconShot] " + file);
            }
            yield return new WaitForSeconds(0.5f);
            Debug.Log("[IconShot] END");
            Finish();
        }

        void Finish()
        {
            UnityEditor.EditorApplication.ExitPlaymode();
        }
    }
}
#endif
