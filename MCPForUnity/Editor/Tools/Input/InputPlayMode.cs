using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Input
{
    [InitializeOnLoad]
    [McpForUnityTool("input_play_mode", AutoRegister = false, Group = "core")]
    public static class InputPlayMode
    {
        private static Operation active;
        private static readonly HashSet<string> Keys = new HashSet<string>(
            Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
            .Concat(Enumerable.Range(0, 10).Select(i => "Digit" + i))
            .Concat(Enumerable.Range(1, 12).Select(i => "F" + i))
            .Concat("Space Enter Tab Escape Backspace Delete Insert Home End PageUp PageDown LeftArrow RightArrow UpArrow DownArrow LeftShift RightShift LeftCtrl RightCtrl LeftAlt RightAlt LeftMeta RightMeta".Split(' ')));

        static InputPlayMode()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => active?.Finish("assembly_reload");
            EditorApplication.quitting += () => active?.Finish("editor_quit");
            EditorApplication.playModeStateChanged += state => {
                if (state != PlayModeStateChange.EnteredPlayMode) active?.Finish("play_mode_changed");
            };
            EditorApplication.pauseStateChanged += state => {
                if (state == PauseState.Paused) active?.Finish("play_mode_paused");
            };
        }

        public static Task<object> HandleCommand(JObject parameters)
        {
            try
            {
                Validate(parameters);
                string action = parameters.Value<string>("action");
                if (action == "status") return Task.FromResult(Status());
                if (action == "cancel")
                {
                    if (active == null) return Task.FromResult<object>(new SuccessResponse("No active Bridge input.", new { active = false }));
                    if (parameters.Value<string>("operation_id") != active.Id)
                        return Task.FromResult<object>(ErrorResponse.FromCode("operation_id_mismatch", "The active input operation was not cancelled."));
                    object cancellation = active.Finish("cancelled");
                    // The original gesture is interrupted; the requested cancellation
                    // itself succeeds when its owned-device cleanup succeeded.
                    return Task.FromResult(cancellation is ErrorResponse error && error.Code == "input_cancelled"
                        ? (object)new SuccessResponse("Bridge input cancelled; prior gameplay effects remain.", error.Data)
                        : cancellation);
                }
                if (active != null) return Task.FromResult<object>(ErrorResponse.FromCode("input_busy", "Another Bridge input operation is active.", active.Snapshot()));
                var backend = new PlayModeInputBackend();
                string reason = NotReady(backend);
                if (reason != null) return Task.FromResult<object>(ErrorResponse.FromCode(reason, "Use status to inspect input readiness."));
                bool useKeys = parameters["keys"] is JArray;
                bool useMouse = action != "key";
                if (useMouse && (Cursor.lockState != CursorLockMode.None || Screen.width < 1 || Screen.height < 1))
                    return Task.FromResult<object>(ErrorResponse.FromCode("pointer_unavailable", "An unlocked cursor and valid primary Game View dimensions are required."));
                if (backend.PhysicalBusy(useKeys, useMouse))
                    return Task.FromResult<object>(ErrorResponse.FromCode("physical_input_busy", "Release physical keys/buttons before automated input."));
                var operation = new Operation(parameters, backend);
                active = operation;
                operation.Start();
                return operation.Completion.Task;
            }
            catch (ArgumentException ex) { return Task.FromResult<object>(ErrorResponse.FromCode("invalid_input_parameters", ex.Message)); }
            catch (Exception ex) { return Task.FromResult<object>(ErrorResponse.FromCode("input_failed", ex.GetBaseException().Message)); }
        }

        private static string NotReady(PlayModeInputBackend backend)
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPlayingOrWillChangePlaymode) return "play_mode_required";
            if (EditorApplication.isPaused) return "play_mode_paused";
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return "editor_not_ready";
            return backend.UnsupportedConfiguration();
        }

        private static object Status()
        {
            var backend = active?.Backend ?? new PlayModeInputBackend();
            string reason = active == null ? NotReady(backend) : "input_busy";
            return new SuccessResponse("Gameplay input state.", new {
                ready = reason == null, reason, backend = "input_system_virtual_devices",
                inputSystemAvailable = backend.Available, updateMode = backend.UpdateMode,
                playing = EditorApplication.isPlaying, paused = EditorApplication.isPaused,
                pointerReady = Cursor.lockState == CursorLockMode.None && Screen.width > 0 && Screen.height > 0,
                gameView = new { width = Screen.width, height = Screen.height },
                devices = backend.Devices, active = active?.Snapshot(),
                supportedKeys = Keys.OrderBy(k => k).ToArray(), maxDurationSeconds = 5, maxOperationSeconds = 10,
                actions = new[] { "status", "key", "move", "click", "drag", "scroll", "cancel" }
            });
        }

        private static bool Number(JToken value, double min, double max) => value != null
            && (value.Type == JTokenType.Float || value.Type == JTokenType.Integer)
            && !double.IsNaN(value.Value<double>()) && value.Value<double>() >= min && value.Value<double>() <= max;

        private static void Validate(JObject p)
        {
            if (p == null || p["action"]?.Type != JTokenType.String) throw new ArgumentException("action is required.");
            string action = p.Value<string>("action");
            string fields = action switch {
                "status" => "", "cancel" => "operation_id", "key" => "keys duration_seconds",
                "move" => "position delta", "click" => "position button keys duration_seconds",
                "drag" => "position end_position button keys duration_seconds steps",
                "scroll" => "position scroll_delta keys", _ => throw new ArgumentException("Unsupported input action.")
            };
            var allowed = new HashSet<string>(fields.Split(' ')) { "action" };
            foreach (var property in p.Properties())
                if (property.Value.Type != JTokenType.Null && !allowed.Contains(property.Name)) throw new ArgumentException("Parameter does not apply: " + property.Name);
            bool Has(string name) => p[name] != null && p[name].Type != JTokenType.Null;
            if (Has("keys") && (!(p["keys"] is JArray keys) || keys.Count < 1 || keys.Count > 8
                || keys.Any(k => k.Type != JTokenType.String || !Keys.Contains(k.Value<string>())) || keys.Distinct(new JTokenEqualityComparer()).Count() != keys.Count))
                throw new ArgumentException("keys requires 1-8 distinct supported Input System key names.");
            if (action == "key" && !Has("keys")) throw new ArgumentException("key requires keys.");
            foreach (string name in new[] { "position", "end_position", "delta", "scroll_delta" })
            {
                double bound = name == "delta" ? 4096 : name == "scroll_delta" ? 100 : 1;
                double min = name.EndsWith("delta") ? -bound : 0;
                if (Has(name) && (!(p[name] is JArray vector) || vector.Count != 2 || vector.Any(v => !Number(v, min, bound))))
                    throw new ArgumentException(name + " requires two finite numbers in range.");
            }
            if (new[] { "click", "drag", "scroll" }.Contains(action) && !Has("position")) throw new ArgumentException("position is required.");
            if (action == "move" && Has("position") == Has("delta")) throw new ArgumentException("move requires exactly position or delta.");
            if (action == "drag" && !Has("end_position")) throw new ArgumentException("drag requires end_position.");
            if (action == "scroll" && (!Has("scroll_delta") || p["scroll_delta"].All(v => v.Value<double>() == 0))) throw new ArgumentException("scroll_delta must be nonzero.");
            if (Has("button") && (p["button"].Type != JTokenType.String || !new[] { "left", "right", "middle" }.Contains(p.Value<string>("button")))) throw new ArgumentException("Unsupported button.");
            if (Has("duration_seconds") && !Number(p["duration_seconds"], .05, 5)) throw new ArgumentException("duration_seconds must be 0.05-5.");
            if (Has("steps") && (p["steps"].Type != JTokenType.Integer || !Number(p["steps"], 1, 64))) throw new ArgumentException("steps must be an integer 1-64.");
            if (action == "cancel" && (!Has("operation_id") || p["operation_id"].Type != JTokenType.String || !Regex.IsMatch(p.Value<string>("operation_id"), "\\A[0-9a-f]{32}\\z"))) throw new ArgumentException("cancel requires the exact operation_id from status.");
        }

        private sealed class Operation
        {
            internal readonly string Id = Guid.NewGuid().ToString("N");
            internal readonly PlayModeInputBackend Backend;
            internal readonly TaskCompletionSource<object> Completion = new TaskCompletionSource<object>();
            private readonly string action;
            private readonly string[] keys;
            private readonly double duration;
            private readonly int steps, width, height;
            private readonly ushort buttons;
            private readonly Vector2 start, end, wheel;
            private readonly bool http, stdio;
            private readonly double started = EditorApplication.timeSinceStartup;
            private Vector2 position;
            private int queuedAtUpdate, observedFrame = -1, sample;
            private double holdStarted = -1;
            private bool releasing, finishing, cleanupFailed;
            // The manager's last Start/Stop snapshot can outlive a socket loss.
            private static bool Connected(TransportMode mode) => MCPServiceLocator.TransportManager.GetClient(mode)?.State?.IsConnected == true;
            internal Operation(JObject p, PlayModeInputBackend backend)
            {
                Backend = backend;
                action = p.Value<string>("action");
                keys = (p["keys"] as JArray)?.Values<string>().ToArray() ?? Array.Empty<string>();
                duration = p.Value<double?>("duration_seconds") ?? .1;
                steps = p.Value<int?>("steps") ?? 8;
                width = Screen.width; height = Screen.height;
                Vector2 Vector(string name) => p[name] is JArray a ? new Vector2(a[0].Value<float>(), a[1].Value<float>()) : Vector2.zero;
                Vector2 Pixels(string name) { Vector2 v = Vector(name); return new Vector2(v.x * width, (1 - v.y) * height); }
                start = action == "move" ? backend.MousePosition : p["position"] is JArray ? Pixels("position") : Vector2.zero;
                end = action == "drag" ? Pixels("end_position") : action == "move" ? p["position"] is JArray ? Pixels("position") : start + Vector("delta") : start;
                wheel = Vector("scroll_delta");
                buttons = action == "click" || action == "drag" ? (ushort)(p.Value<string>("button") == "right" ? 2 : p.Value<string>("button") == "middle" ? 4 : 1) : (ushort)0;
                http = Connected(TransportMode.Http); stdio = Connected(TransportMode.Stdio);
            }

            internal object Snapshot() => new { operation_id = Id, action, phase = cleanupFailed ? "cleanup_failed" : releasing ? "release" : "input",
                requestedKeys = keys, requestedButtonMask = buttons, lastQueuedStateObserved = Backend.StateObserved,
                elapsedSeconds = EditorApplication.timeSinceStartup - started, queuedEvents = Backend.Events, samples = sample };

            internal void Start()
            {
                try {
                    Backend.Acquire(keys.Length > 0, action != "key", Id);
                    string reason = Backend.UnsupportedConfiguration();
                    if (reason != null || Backend.OwnershipLost) { Finish(reason ?? "device_unavailable"); return; }
                    position = action == "move" ? end : start;
                    Queue(false, action == "move" ? end - start : Vector2.zero, wheel);
                    EditorApplication.update += Tick;
                }
                catch (Exception ex) { Finish("input_failed", ex.GetBaseException().Message); }
            }

            private void Queue(bool release, Vector2 delta, Vector2 scroll)
            {
                Backend.Queue(release ? Array.Empty<string>() : keys, position, delta, scroll, release ? (ushort)0 : buttons);
                queuedAtUpdate = Backend.Updates;
                observedFrame = -1;
                EditorApplication.QueuePlayerLoopUpdate();
            }

            private void Tick()
            {
                try {
                    string reason = NotReady(Backend);
                    if (reason != null) { Finish(reason); return; }
                    if (http && !Connected(TransportMode.Http) || stdio && !Connected(TransportMode.Stdio)) { Finish("transport_disconnected"); return; }
                    if (EditorApplication.timeSinceStartup - started > 10) { Finish("input_deadline_exceeded"); return; }
                    if (Backend.OwnershipLost) { Finish("input_device_ownership_lost"); return; }
                    if (action != "key" && (Screen.width != width || Screen.height != height || Cursor.lockState != CursorLockMode.None)) { Finish("pointer_configuration_changed"); return; }
                    EditorApplication.QueuePlayerLoopUpdate();
                    if (Backend.Updates <= queuedAtUpdate) return;
                    if (!Backend.StateObserved) { Finish("input_state_not_observed", Backend.ObservationError); return; }
                    if (observedFrame < 0) { observedFrame = Time.frameCount; return; }
                    if (Time.frameCount <= observedFrame) return;
                    if (releasing) { Finish(null); return; }
                    if (holdStarted < 0) holdStarted = EditorApplication.timeSinceStartup;
                    double held = EditorApplication.timeSinceStartup - holdStarted;
                    if (action == "drag" && sample < steps)
                    {
                        if (held < duration * (sample + 1) / steps) return;
                        Vector2 previous = position;
                        position = Vector2.Lerp(start, end, ++sample / (float)steps);
                        Queue(false, position - previous, Vector2.zero);
                        return;
                    }
                    if ((action == "key" || action == "click") && held < duration) return;
                    releasing = true;
                    Queue(true, Vector2.zero, Vector2.zero);
                }
                catch (Exception ex) { Finish("input_failed", ex.GetBaseException().Message); }
            }

            internal object Finish(string reason, string detail = null)
            {
                // ResetDevice may synchronously invoke application callbacks. Never block
                // on our own completion if such a callback pauses/exits Play Mode.
                if (finishing) return ErrorResponse.FromCode("input_cleanup_in_progress", "Input cleanup is already in progress.");
                finishing = true;
                EditorApplication.update -= Tick;
                string[] cleanup;
                try { cleanup = Backend.Release(); }
                catch (Exception ex) { cleanup = new[] { ex.GetBaseException().Message }; }
                cleanupFailed = Backend.HasKeyboard || Backend.HasMouse;
                if (!cleanupFailed && ReferenceEquals(active, this)) active = null;
                var evidence = new { operation_id = Id, action, completed = reason == null && cleanup.Length == 0,
                    interrupted = reason != null, queuedEvents = Backend.Events, samples = sample,
                    normalReleaseObserved = reason == null && releasing, ownedDevicesReleased = !cleanupFailed,
                    cleanupErrors = cleanup, elapsedSeconds = EditorApplication.timeSinceStartup - started,
                    position = action == "key" ? (object)null : new[] { position.x, position.y }, detail };
                object result = reason == null && cleanup.Length == 0 ? (object)new SuccessResponse("Input completed through normal player frames.", evidence)
                    : ErrorResponse.FromCode(cleanup.Length == 0 ? reason.StartsWith("input_") ? reason : "input_" + reason : "input_cleanup_failed", "Input interrupted; inspect gameplay before retrying.", evidence);
                Completion.TrySetResult(result);
                finishing = false;
                return result;
            }
        }
    }
}
