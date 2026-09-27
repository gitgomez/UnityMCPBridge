using System;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Input
{
    // Public API reflection keeps Input System an optional package dependency.
    internal sealed class PlayModeInputBackend
    {
        internal static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("UnityEngine.InputSystem." + name, false)).FirstOrDefault(t => t != null);
        internal static object Read(object value, string property) => value?.GetType().GetProperty(property)?.GetValue(value);
        private readonly Type system = Find("InputSystem");
        private readonly Type keyboard = Find("Keyboard");
        private readonly Type mouse = Find("Mouse");
        private readonly Type key = Find("Key");
        private readonly Type keyboardState = Find("LowLevel.KeyboardState");
        private readonly Type mouseState = Find("LowLevel.MouseState");
        private readonly Type inputState = Find("LowLevel.InputState");
        private object ownedKeyboard, ownedMouse, previousKeyboard, previousMouse;
        private Action afterUpdate;
        private string[] expectedKeys;
        private Vector2 expectedPosition, expectedDelta, expectedScroll;
        private ushort expectedButtons;
        private bool pendingObservation;
        internal int Updates { get; private set; }
        internal bool StateObserved { get; private set; }
        internal string ObservationError { get; private set; }
        internal int Events { get; private set; }
        internal bool Available => system != null && keyboardState != null && mouseState != null;
        internal string UpdateMode => Read(system?.GetProperty("settings")?.GetValue(null), "updateMode")?.ToString();
        internal bool HasKeyboard => ownedKeyboard != null;
        internal bool HasMouse => ownedMouse != null;
        private object Current(Type type) => type?.GetProperty("current")?.GetValue(null);
        internal Vector2 MousePosition => ReadVector(Read(Current(mouse), "position"));
        private static Vector2 ReadVector(object control) => control == null ? Vector2.zero
            : (Vector2)control.GetType().GetMethod("ReadValue", Type.EmptyTypes).Invoke(control, null);

        internal object[] Devices => !Available ? Array.Empty<object>() : ((IEnumerable)system.GetProperty("devices").GetValue(null))
            .Cast<object>().Take(64).Select(d => (object)new {
                id = Read(d, "deviceId"), layout = Read(d, "layout"), native = Read(d, "native"), enabled = Read(d, "enabled"),
                bridgeOwned = ReferenceEquals(d, ownedKeyboard) || ReferenceEquals(d, ownedMouse)
            }).ToArray();

        internal string UnsupportedConfiguration()
        {
            if (!Available) return "input_system_unavailable";
#if !ENABLE_INPUT_SYSTEM
            return "input_system_backend_disabled";
#else
            if (UpdateMode != "ProcessEventsInDynamicUpdate") return "dynamic_input_updates_required";
            var users = Find("Users.InputUser")?.GetProperty("all")?.GetValue(null) as IEnumerable;
            if (users != null && users.Cast<object>().Any()) return "paired_input_unsupported";
            var player = Find("PlayerInput");
            if (player != null && UnityEngine.Object.FindObjectsByType(player, FindObjectsSortMode.None).Length != 0)
                return "player_input_unsupported";
            // Explicit device filters cannot resolve a newly created virtual device.
            var actions = (IEnumerable)system.GetMethod("ListEnabledActions", Type.EmptyTypes).Invoke(null, null);
            if (actions.Cast<object>().Any(a => Read(Read(a, "actionMap"), "devices") != null))
                return "device_filtered_actions_unsupported";
            return null;
#endif
        }

        private static bool Pressed(object device, string control) => Read(Read(device, control), "isPressed") is true;
        internal bool PhysicalBusy(bool useKeyboard, bool useMouse)
        {
            return useKeyboard && Pressed(Current(keyboard), "anyKey") || useMouse && new[] {
                "leftButton", "rightButton", "middleButton", "forwardButton", "backButton"
            }.Any(p => Pressed(Current(mouse), p));
        }

        internal bool OwnershipLost => ownedKeyboard != null && (!ReferenceEquals(Current(keyboard), ownedKeyboard) || Read(ownedKeyboard, "enabled") is not true)
            || ownedMouse != null && (!ReferenceEquals(Current(mouse), ownedMouse) || Read(ownedMouse, "enabled") is not true);

        internal void Acquire(bool useKeyboard, bool useMouse, string id)
        {
            previousKeyboard = Current(keyboard);
            previousMouse = Current(mouse);
            var add = system.GetMethod("AddDevice", new[] { typeof(string), typeof(string), typeof(string) });
            // Set ownership immediately after each allocation so partial setup is recoverable.
            if (useKeyboard) ownedKeyboard = add.Invoke(null, new object[] { "Keyboard", "MCPKeyboard-" + id, null });
            if (useMouse) ownedMouse = add.Invoke(null, new object[] { "Mouse", "MCPMouse-" + id, null });
            afterUpdate = () => {
                if (inputState.GetProperty("currentUpdateType").GetValue(null).ToString() != "Dynamic") return;
                Updates++;
                if (!pendingObservation) return;
                pendingObservation = false;
                try {
                    StateObserved = !OwnershipLost;
                    if (ownedKeyboard != null)
                        StateObserved &= ((IEnumerable)Read(ownedKeyboard, "allKeys")).Cast<object>()
                            .All(k => (Read(k, "isPressed") is true) == expectedKeys.Contains(Read(k, "keyCode").ToString()));
                    if (ownedMouse != null)
                        StateObserved &= (ReadVector(Read(ownedMouse, "position")) - expectedPosition).sqrMagnitude < .01f
                            && (ReadVector(Read(ownedMouse, "delta")) - expectedDelta).sqrMagnitude < .01f
                            && (ReadVector(Read(ownedMouse, "scroll")) - expectedScroll).sqrMagnitude < .01f
                            && Pressed(ownedMouse, "leftButton") == ((expectedButtons & 1) != 0)
                            && Pressed(ownedMouse, "rightButton") == ((expectedButtons & 2) != 0)
                            && Pressed(ownedMouse, "middleButton") == ((expectedButtons & 4) != 0);
                }
                catch (Exception ex) { StateObserved = false; ObservationError = ex.GetBaseException().Message; }
            };
            system.GetEvent("onAfterUpdate").AddEventHandler(null, afterUpdate);
        }

        internal void Queue(string[] keys, Vector2 position, Vector2 delta, Vector2 scroll, ushort buttons)
        {
            expectedKeys = keys; expectedPosition = position; expectedDelta = delta;
            expectedScroll = scroll; expectedButtons = buttons;
            pendingObservation = true; StateObserved = false; ObservationError = null;
            var queue = system.GetMethods().Single(m => m.Name == "QueueStateEvent" && m.IsGenericMethodDefinition);
            if (ownedKeyboard != null)
            {
                var pressed = Array.CreateInstance(key, keys.Length);
                for (int i = 0; i < keys.Length; i++) pressed.SetValue(Enum.Parse(key, keys[i]), i);
                var state = Activator.CreateInstance(keyboardState, new object[] { pressed });
                queue.MakeGenericMethod(keyboardState).Invoke(null, new[] { ownedKeyboard, state, (object)(-1d) });
                Events++;
            }
            if (ownedMouse != null)
            {
                object state = Activator.CreateInstance(mouseState);
                mouseState.GetField("position").SetValue(state, position);
                mouseState.GetField("delta").SetValue(state, delta);
                mouseState.GetField("scroll").SetValue(state, scroll);
                mouseState.GetField("buttons").SetValue(state, buttons);
                queue.MakeGenericMethod(mouseState).Invoke(null, new[] { ownedMouse, state, (object)(-1d) });
                Events++;
            }
        }

        internal string[] Release()
        {
            var errors = new System.Collections.Generic.List<string>();
            if (afterUpdate != null)
            {
                try { system.GetEvent("onAfterUpdate").RemoveEventHandler(null, afterUpdate); }
                catch (Exception ex) { errors.Add("unsubscribe: " + ex.GetBaseException().Message); }
                afterUpdate = null;
            }
            var deviceType = Find("InputDevice");
            bool restoreKeyboard = ReferenceEquals(Current(keyboard), ownedKeyboard);
            bool restoreMouse = ReferenceEquals(Current(mouse), ownedMouse);
            foreach (object device in new[] { ownedKeyboard, ownedMouse }.Where(d => d != null))
            {
                try { system.GetMethod("ResetDevice", new[] { deviceType, typeof(bool) }).Invoke(null, new[] { device, (object)true }); }
                catch (Exception ex) { errors.Add("reset: " + ex.GetBaseException().Message); }
                try { system.GetMethod("RemoveDevice", new[] { deviceType }).Invoke(null, new[] { device }); }
                catch (Exception ex) { errors.Add("remove: " + ex.GetBaseException().Message); }
            }
            // Do not overwrite a different device which became current through human input.
            Restore(ownedKeyboard, previousKeyboard, restoreKeyboard, errors);
            Restore(ownedMouse, previousMouse, restoreMouse, errors);
            if (Read(ownedKeyboard, "added") is not true) ownedKeyboard = null;
            if (Read(ownedMouse, "added") is not true) ownedMouse = null;
            return errors.ToArray();
        }

        private void Restore(object owned, object previous, bool wasCurrent, System.Collections.Generic.List<string> errors)
        {
            if (!wasCurrent || owned == null || previous == null || Read(previous, "added") is not true) return;
            try { previous.GetType().GetMethod("MakeCurrent").Invoke(previous, null); }
            catch (Exception ex) { errors.Add("restore current: " + ex.GetBaseException().Message); }
        }
    }
}
