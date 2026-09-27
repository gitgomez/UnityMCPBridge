using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Tools.Input;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Users;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;

namespace MCPForUnityTests.PlayMode
{
    public class GameplayInputTests
    {
        private GameObject fixture;
        private GameplayInputProbe probe;
        private InputSettings.UpdateMode oldMode;
        private CursorLockMode oldLock;
        private InputSettings.BackgroundBehavior oldBackground;
        private InputSettings.EditorInputBehaviorInPlayMode oldEditorInput;
        [SetUp] public void Setup()
        {
            oldMode = InputSystem.settings.updateMode;
            oldLock = Cursor.lockState;
            oldBackground = InputSystem.settings.backgroundBehavior;
            oldEditorInput = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsInDynamicUpdate;
            // Batch tests have no focused Game View. This is fixture configuration,
            // not production tool behavior; restore it in TearDown.
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            Cursor.lockState = CursorLockMode.None;
            fixture = new GameObject("MCPGameplayInputProbe");
            probe = fixture.AddComponent<GameplayInputProbe>();
        }
        [TearDown] public void Cleanup()
        {
            var status = JObject.FromObject(InputPlayMode.HandleCommand(JObject.Parse("{\"action\":\"status\"}")).Result);
            var id = (status["data"]?["active"] as JObject)?["operation_id"];
            if (id != null) InputPlayMode.HandleCommand(new JObject { ["action"] = "cancel", ["operation_id"] = id }).GetAwaiter().GetResult();
            Object.DestroyImmediate(fixture);
            InputSystem.settings.updateMode = oldMode;
            InputSystem.settings.backgroundBehavior = oldBackground;
            InputSystem.settings.editorInputBehaviorInPlayMode = oldEditorInput;
            Cursor.lockState = oldLock;
            Assert.That(InputSystem.devices.Any(d => d.name.StartsWith("MCPKeyboard-") || d.name.StartsWith("MCPMouse-")), Is.False);
        }
        private static Task<object> Send(string json) => InputPlayMode.HandleCommand(JObject.Parse(json));
        private static IEnumerator AwaitSuccess(Task<object> task)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 12;
            while (!task.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Input did not terminate within its deadline.");
            var result = JObject.FromObject(task.Result);
            Assert.That(result.Value<bool>("success"), Is.True, result.ToString());
            Assert.That(result["data"].Value<bool>("ownedDevicesReleased"), Is.True);
        }
        [UnityTest] public IEnumerator ChordHasNormalGameplayAndActionEdges()
        {
            var oldKeyboard = Keyboard.current;
            yield return AwaitSuccess(Send("{\"action\":\"key\",\"keys\":[\"Space\",\"LeftShift\"],\"duration_seconds\":0.2}"));
            Assert.That(probe.keyDownFrame, Is.GreaterThanOrEqualTo(0));
            Assert.That(probe.keyUpFrame, Is.GreaterThan(probe.keyDownFrame));
            Assert.That(probe.heldFrames, Is.GreaterThanOrEqualTo(2));
            Assert.That(probe.chordFrames, Is.EqualTo(probe.heldFrames));
            Assert.That(probe.actionPerformed, Is.EqualTo(1));
            Assert.That(probe.actionCancelled, Is.EqualTo(1));
            Assert.That(Keyboard.current, Is.SameAs(oldKeyboard));
        }
        [UnityTest] public IEnumerator PointerClickDragRelativeMoveAndWheel()
        {
            foreach (string button in new[] { "left", "right", "middle" })
            {
                yield return AwaitSuccess(Send("{\"action\":\"click\",\"position\":[0.3,0.7],\"button\":\"" + button + "\"}"));
                Assert.That(probe.mouseUpFrame, Is.GreaterThan(probe.mouseDownFrame));
            }
            int before = probe.moveFrames;
            yield return AwaitSuccess(Send("{\"action\":\"drag\",\"position\":[0.1,0.8],\"end_position\":[0.8,0.2],\"steps\":4,\"duration_seconds\":0.2}"));
            Assert.That(probe.moveFrames - before, Is.GreaterThanOrEqualTo(4));
            Assert.That(probe.lastPosition.x, Is.EqualTo(Screen.width * .8).Within(.1));
            Assert.That(probe.lastPosition.y, Is.EqualTo(Screen.height * .8).Within(.1));
            probe.totalDelta = Vector2.zero;
            yield return AwaitSuccess(Send("{\"action\":\"move\",\"delta\":[12,-5]}"));
            Assert.That(probe.totalDelta, Is.EqualTo(new Vector2(12, -5)));
            yield return AwaitSuccess(Send("{\"action\":\"scroll\",\"position\":[0.5,0.5],\"scroll_delta\":[0,2]}"));
            Assert.That(probe.totalScroll.y, Is.EqualTo(2).Within(.01));
        }
        [UnityTest] public IEnumerator BusyStaleCancelAndOwnedCleanup()
        {
            int devices = InputSystem.devices.Count;
            var task = Send("{\"action\":\"key\",\"keys\":[\"Space\"],\"duration_seconds\":5}");
            yield return null;
            Assert.That(JObject.FromObject(Send("{\"action\":\"key\",\"keys\":[\"W\"]}").Result).Value<string>("code"), Is.EqualTo("input_busy"));
            Assert.That(JObject.FromObject(Send("{\"action\":\"cancel\",\"operation_id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}").Result).Value<string>("code"), Is.EqualTo("operation_id_mismatch"));
            var id = JObject.FromObject(Send("{\"action\":\"status\"}").Result)["data"]["active"]["operation_id"];
            var cancelled = InputPlayMode.HandleCommand(new JObject { ["action"] = "cancel", ["operation_id"] = id }).Result;
            Assert.That(JObject.FromObject(cancelled).Value<bool>("success"), Is.True);
            Assert.That(task.IsCompleted, Is.True);
            Assert.That(JObject.FromObject(task.Result).Value<string>("code"), Is.EqualTo("input_cancelled"));
            Assert.That(InputSystem.devices.Count, Is.EqualTo(devices));
        }
        [Test] public void UnsupportedUpdateAndPairedUserAreRejectedWithoutDevices()
        {
            int devices = InputSystem.devices.Count;
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
            Assert.That(JObject.FromObject(Send("{\"action\":\"key\",\"keys\":[\"Space\"]}").Result).Value<string>("code"), Is.EqualTo("dynamic_input_updates_required"));
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsInDynamicUpdate;
            var user = InputUser.CreateUserWithoutPairedDevices();
            try { Assert.That(JObject.FromObject(Send("{\"action\":\"key\",\"keys\":[\"Space\"]}").Result).Value<string>("code"), Is.EqualTo("paired_input_unsupported")); }
            finally { user.UnpairDevicesAndRemoveUser(); }
            Assert.That(InputSystem.devices.Count, Is.EqualTo(devices));
        }

        [UnityTest] public IEnumerator PauseReleasesOnlyOwnedDevices()
        {
            int count = InputSystem.devices.Count;
            var task = Send("{\"action\":\"key\",\"keys\":[\"Space\"],\"duration_seconds\":5}");
            yield return null;
            try {
                Assert.That(task.IsCompleted, Is.False, "The hold must be active before testing pause cleanup.");
                EditorApplication.isPaused = true;
                Assert.That(task.IsCompleted, Is.True);
                Assert.That(JObject.FromObject(task.Result)["data"].Value<bool>("ownedDevicesReleased"), Is.True);
                Assert.That(InputSystem.devices.Count, Is.EqualTo(count));
            }
            finally { EditorApplication.isPaused = false; }
        }

        [UnityTest] public IEnumerator OtherDeviceInputInterruptsWithoutResettingIt()
        {
            var other = InputSystem.AddDevice<Keyboard>("NonBridgeTestKeyboard");
            try {
                var task = Send("{\"action\":\"key\",\"keys\":[\"Space\"],\"duration_seconds\":5}");
                yield return null;
                InputSystem.QueueStateEvent(other, new KeyboardState(Key.A));
                double deadline = Time.realtimeSinceStartupAsDouble + 3;
                while (!task.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.That(task.IsCompleted, Is.True);
                Assert.That(JObject.FromObject(task.Result).Value<string>("code"), Is.EqualTo("input_device_ownership_lost"));
                Assert.That(other.aKey.isPressed, Is.True, "Cleanup must not reset another device.");
                Assert.That(Keyboard.current, Is.SameAs(other));
            }
            finally { InputSystem.RemoveDevice(other); }
        }
    }
}
