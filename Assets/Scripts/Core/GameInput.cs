using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace GooseBrawl
{
    /// <summary>
    /// Tiny input facade so gameplay code does not care whether the new Input System or the
    /// legacy Input Manager is active. Touch on device, mouse + keyboard in the Editor mock mode.
    /// </summary>
    public static class GameInput
    {
        static readonly List<RaycastResult> s_RaycastResults = new List<RaycastResult>();

        /// <summary>True on the frame a tap / click starts. Returns the screen position.</summary>
        public static bool TryGetTap(out Vector2 screenPos)
        {
            screenPos = default;
#if ENABLE_INPUT_SYSTEM
            var ts = Touchscreen.current;
            if (ts != null)
            {
                var touch = ts.primaryTouch;
                if (touch.press.wasPressedThisFrame)
                {
                    screenPos = touch.position.ReadValue();
                    return true;
                }
            }
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                screenPos = mouse.position.ReadValue();
                return true;
            }
            return false;
#else
            if (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began)
            {
                screenPos = Input.GetTouch(0).position;
                return true;
            }
            if (Input.GetMouseButtonDown(0))
            {
                screenPos = Input.mousePosition;
                return true;
            }
            return false;
#endif
        }

        public static bool IsPointerOverUI(Vector2 screenPos)
        {
            var es = EventSystem.current;
            if (es == null) return false;
            var data = new PointerEventData(es) { position = screenPos };
            s_RaycastResults.Clear();
            es.RaycastAll(data, s_RaycastResults);
            return s_RaycastResults.Count > 0;
        }

        /// <summary>WASD / arrow keys as a -1..1 vector (x = strafe, y = forward).</summary>
        public static Vector2 MoveAxis()
        {
            float x = 0f, y = 0f;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return Vector2.zero;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) y += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) y -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
#else
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) y += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) y -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) x += 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) x -= 1f;
#endif
            return Vector2.ClampMagnitude(new Vector2(x, y), 1f);
        }

        /// <summary>Q / E rotate the mock camera.</summary>
        public static float RotateAxis()
        {
            float r = 0f;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return 0f;
            if (kb.eKey.isPressed) r += 1f;
            if (kb.qKey.isPressed) r -= 1f;
#else
            if (Input.GetKey(KeyCode.E)) r += 1f;
            if (Input.GetKey(KeyCode.Q)) r -= 1f;
#endif
            return r;
        }

        /// <summary>Mouse delta while the right button is held (mock camera look).</summary>
        public static Vector2 LookDelta()
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse == null || !mouse.rightButton.isPressed) return Vector2.zero;
            return mouse.delta.ReadValue();
#else
            if (!Input.GetMouseButton(1)) return Vector2.zero;
            return new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10f;
#endif
        }

        /// <summary>Number of fingers currently on the screen (0 with a mouse).</summary>
        public static int TouchCount()
        {
#if ENABLE_INPUT_SYSTEM
            var ts = Touchscreen.current;
            if (ts == null) return 0;
            int n = 0;
            foreach (var t in ts.touches) if (t.press.isPressed) n++;
            return n;
#else
            return Input.touchCount;
#endif
        }

        /// <summary>Editor mock: B throws bread, Y simulates a shout.</summary>
        public static bool BreadPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.bKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.B);
#endif
        }

        public static bool YellPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.yKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Y);
#endif
        }

        /// <summary>Editor mock: N shouts the goose's name, M shouts "sorry" (speech reactions), P = photo mode / shutter.</summary>
        public static bool NamePressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.nKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.N);
#endif
        }

        public static bool SorryPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.mKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.M);
#endif
        }

        public static bool PhotoPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.pKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.P);
#endif
        }

        public static bool RestartPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.rKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.R);
#endif
        }

        public static bool SpacePressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.spaceKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Space);
#endif
        }
    }
}
