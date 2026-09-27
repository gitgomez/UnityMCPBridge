using UnityEngine;
using UnityEngine.InputSystem;

namespace MCPForUnityTests.PlayMode
{
    // Used by coroutine regressions and the real MCP acceptance path; all
    // observations originate in ordinary gameplay Update or InputAction events.
    public sealed class GameplayInputProbe : MonoBehaviour
    {
        public int keyDownFrame = -1, keyUpFrame = -1, heldFrames, chordFrames;
        public int mouseDownFrame = -1, mouseUpFrame = -1, mouseHeldFrames, moveFrames;
        public int actionPerformed, actionCancelled;
        public Vector2 lastPosition, totalDelta, totalScroll;
        private InputAction space;
        private void OnEnable()
        {
            space = new InputAction("MCP probe space", InputActionType.Button, "<Keyboard>/space");
            space.performed += _ => actionPerformed++;
            space.canceled += _ => actionCancelled++;
            space.Enable();
        }
        private void OnDisable() { space?.Dispose(); }
        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.name.StartsWith("MCPKeyboard-"))
            {
                if (keyboard.spaceKey.wasPressedThisFrame) keyDownFrame = Time.frameCount;
                if (keyboard.spaceKey.wasReleasedThisFrame) keyUpFrame = Time.frameCount;
                if (keyboard.spaceKey.isPressed) heldFrames++;
                if (keyboard.spaceKey.isPressed && keyboard.leftShiftKey.isPressed) chordFrames++;
            }
            var mouse = Mouse.current;
            if (mouse == null || !mouse.name.StartsWith("MCPMouse-")) return;
            var button = mouse.leftButton.isPressed || mouse.leftButton.wasReleasedThisFrame ? mouse.leftButton
                : mouse.rightButton.isPressed || mouse.rightButton.wasReleasedThisFrame ? mouse.rightButton : mouse.middleButton;
            if (button.wasPressedThisFrame) mouseDownFrame = Time.frameCount;
            if (button.wasReleasedThisFrame) mouseUpFrame = Time.frameCount;
            if (button.isPressed) mouseHeldFrames++;
            Vector2 delta = mouse.delta.ReadValue();
            if (delta != Vector2.zero) moveFrames++;
            lastPosition = mouse.position.ReadValue();
            totalDelta += delta;
            totalScroll += mouse.scroll.ReadValue();
        }
    }
}
