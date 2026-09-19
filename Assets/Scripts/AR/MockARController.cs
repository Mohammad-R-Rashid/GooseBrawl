using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GooseBrawl
{
    /// <summary>
    /// Editor stand-in for AR: a floor, a few walls/furniture blocks on the AREnvironment layer and a
    /// first person camera you drive with WASD / arrows, Q/E to turn and right-drag to look.
    /// </summary>
    public class MockARController : MonoBehaviour
    {
        [Header("Mock player")]
        public float moveSpeed = 2.2f;
        public float turnSpeedDeg = 110f;
        public float mouseSensitivity = 0.12f;
        public float eyeHeight = 1.4f;

        [Header("Mock world")]
        public float scanDelay = 1.5f;
        public bool FloorReady { get; private set; }
        public float FloorY => 0f;
        public Camera MockCamera { get; private set; }
        /// <summary>Scripted movement input (automation); added to keyboard input.</summary>
        public Vector2 AutoMove;
        /// <summary>Degrees to turn this second (automation).</summary>
        public float AutoTurn;

        Transform m_Rig;
        float m_Yaw, m_Pitch;
        bool m_Built;
        LayerMask m_EnvMask;

        public void Build(int envLayer)
        {
            if (m_Built) return;
            m_Built = true;
            m_EnvMask = 1 << envLayer;
            var mats = MaterialLibrary.Instance;

            var world = new GameObject("MockWorld");
            world.transform.SetParent(transform, false);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "MockFloor";
            floor.transform.SetParent(world.transform, false);
            floor.transform.localScale = new Vector3(4f, 1f, 4f);
            floor.layer = envLayer;
            var fr = floor.GetComponent<Renderer>();
            fr.sharedMaterial = mats != null ? mats.MockFloor : null;
            fr.receiveShadows = true;

            MakeBox(world.transform, "MockWall_Back", new Vector3(0f, 1.1f, -5f), new Vector3(7f, 2.2f, 0.15f), envLayer, mats);
            MakeBox(world.transform, "MockWall_Side", new Vector3(3.2f, 1.1f, -1.5f), new Vector3(0.15f, 2.2f, 7f), envLayer, mats);
            MakeBox(world.transform, "MockCouch", new Vector3(-1.8f, 0.35f, -2.6f), new Vector3(1.8f, 0.7f, 0.8f), envLayer, mats);
            MakeBox(world.transform, "MockTable", new Vector3(1.3f, 0.36f, 1.8f), new Vector3(1.2f, 0.72f, 0.7f), envLayer, mats);
            MakeBox(world.transform, "MockPillar", new Vector3(-2.4f, 1.1f, 1.0f), new Vector3(0.4f, 2.2f, 0.4f), envLayer, mats);
            // A crate directly between the goose spawn point and the player, so avoidance is exercised immediately.
            MakeBox(world.transform, "MockCrate", new Vector3(0f, 0.4f, -1.4f), new Vector3(3.2f, 0.8f, 0.4f), envLayer, mats);

            var rig = new GameObject("MockPlayer");
            rig.transform.SetParent(transform, false);
            rig.transform.position = new Vector3(0f, eyeHeight, 0f);
            m_Rig = rig.transform;

            var camGo = new GameObject("Mock Camera");
            camGo.transform.SetParent(rig.transform, false);
            camGo.tag = "MainCamera";
            MockCamera = camGo.AddComponent<Camera>();
            MockCamera.clearFlags = CameraClearFlags.SolidColor;
            MockCamera.backgroundColor = new Color(0.16f, 0.17f, 0.21f);
            MockCamera.nearClipPlane = 0.05f;
            MockCamera.farClipPlane = 60f;
            MockCamera.fieldOfView = 65f;
            MockCamera.allowHDR = true;
            MockCamera.allowMSAA = true;
            var camData = MockCamera.GetUniversalAdditionalCameraData();
            if (camData != null)
            {
                camData.renderPostProcessing = true;
                camData.dithering = true;
                camData.renderShadows = true;
            }
            camGo.AddComponent<AudioListener>();
        }

        static void MakeBox(Transform parent, string name, Vector3 center, Vector3 size, int layer, MaterialLibrary mats)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent, false);
            box.transform.position = center;
            box.transform.localScale = size;
            box.layer = layer;
            var r = box.GetComponent<Renderer>();
            if (mats != null) r.sharedMaterial = mats.MockWall;
        }

        public void BeginScan()
        {
            StartCoroutine(ScanRoutine());
        }

        /// <summary>Automation: put the mock player at a floor position and face a direction (flat).</summary>
        public void TeleportTo(Vector3 floorPos, Vector3 facing)
        {
            if (m_Rig == null) return;
            m_Rig.position = new Vector3(floorPos.x, eyeHeight, floorPos.z);
            facing.y = 0f;
            if (facing.sqrMagnitude > 1e-4f) m_Yaw = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
            m_Pitch = 0f;
            m_Rig.rotation = Quaternion.Euler(0f, m_Yaw, 0f);
            if (MockCamera != null) MockCamera.transform.localRotation = Quaternion.Euler(m_Pitch, 0f, 0f);
        }

        /// <summary>Automation: aim the mock camera at a world point (yaw and pitch).</summary>
        public void LookAt(Vector3 worldPos)
        {
            if (m_Rig == null || MockCamera == null) return;
            Vector3 from = MockCamera.transform.position;
            Vector3 d = worldPos - from;
            Vector3 flat = new Vector3(d.x, 0f, d.z);
            if (flat.sqrMagnitude > 1e-4f) m_Yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
            m_Pitch = Mathf.Clamp(-Mathf.Atan2(d.y, Mathf.Max(0.001f, flat.magnitude)) * Mathf.Rad2Deg, -80f, 80f);
            m_Rig.rotation = Quaternion.Euler(0f, m_Yaw, 0f);
            MockCamera.transform.localRotation = Quaternion.Euler(m_Pitch, 0f, 0f);
        }

        IEnumerator ScanRoutine()
        {
            FloorReady = false;
            yield return new WaitForSeconds(scanDelay);
            FloorReady = true;
        }

        void Update()
        {
            if (!m_Built || m_Rig == null) return;
            float dt = Time.deltaTime;
            var move = Vector2.ClampMagnitude(GameInput.MoveAxis() + AutoMove, 1f);
            float rot = GameInput.RotateAxis();
            var look = GameInput.LookDelta();

            m_Yaw += rot * turnSpeedDeg * dt + look.x * mouseSensitivity + AutoTurn * dt;
            m_Pitch = Mathf.Clamp(m_Pitch - look.y * mouseSensitivity, -80f, 80f);
            m_Rig.rotation = Quaternion.Euler(0f, m_Yaw, 0f);
            MockCamera.transform.localRotation = Quaternion.Euler(m_Pitch, 0f, 0f);

            Vector3 delta = (m_Rig.forward * move.y + m_Rig.right * move.x) * moveSpeed * dt;
            if (delta.sqrMagnitude > 0f)
            {
                Vector3 next = m_Rig.position + delta;
                if (!Physics.CheckSphere(new Vector3(next.x, 0.6f, next.z), 0.25f, m_EnvMask, QueryTriggerInteraction.Ignore))
                    m_Rig.position = next;
            }
        }
    }
}
