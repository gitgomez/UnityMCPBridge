using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>Independent read-only control observations. No Unity API on the sampler thread.</summary>
    [InitializeOnLoad]
    internal static class EditorDiagnostics
    {
        private static long _lastTick;
        private static long _lastTickUnixMs;
        private static long _ticks;
        private static volatile JObject _activity;
        private static volatile JObject _native = new JObject { ["status"] = "unknown" };
        private static readonly ManualResetEventSlim Stop = new(false);
        private static volatile bool _stopping;
        private static readonly Thread Sampler;
        private static readonly string Epoch;
        private static readonly int ProcessId;
        private static long _lastActivityTick;

        static EditorDiagnostics()
        {
            Epoch = CommandRuntimeState.Epoch;
            ProcessId = Process.GetCurrentProcess().Id;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
            if (Application.platform == RuntimePlatform.WindowsEditor && !Application.isBatchMode)
            {
                Sampler = new Thread(SampleLoop) { IsBackground = true, Name = "UnityMCP modal diagnosis" };
                Sampler.Start();
            }
            else _native = new JObject { ["status"] = "unsupported", ["scope"] = "win32_interactive_only" };
        }

        private static void Tick()
        {
            long tick = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _lastTick, tick);
            Interlocked.Exchange(ref _lastTickUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Interlocked.Increment(ref _ticks);
            if ((tick - _lastActivityTick) * 1000.0 / Stopwatch.Frequency >= 500)
            {
                // Called ONLY by an actual Editor update. Readers cannot freshen this observation.
                _activity = EditorStateCache.GetSnapshot();
                _lastActivityTick = tick;
            }
        }

        internal static JObject Snapshot()
        {
            long tick = Interlocked.Read(ref _lastTick);
            double? age = tick == 0 ? null : Math.Max(0,
                (Stopwatch.GetTimestamp() - tick) * 1000.0 / Stopwatch.Frequency);
            var native = (JObject)_native.DeepClone();
            long? sampled = native.Value<long?>("sampled_tick");
            double? nativeAge = sampled.HasValue ? Math.Max(0,
                (Stopwatch.GetTimestamp() - sampled.Value) * 1000.0 / Stopwatch.Frequency) : null;
            native.Remove("sampled_tick");
            native["age_ms"] = nativeAge.HasValue ? new JValue(nativeAge.Value) : JValue.CreateNull();
            if (nativeAge > 2000) native["status"] = "stale";
            var activity = _activity == null ? new JObject() : (JObject)_activity.DeepClone();
            var snapshot = new JObject
            {
                ["schema_version"] = "editor_diagnostics@1", ["epoch"] = Epoch,
                ["editor_pid"] = ProcessId, ["observed_at_unix_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["main_thread"] = new JObject
                {
                    ["status"] = !age.HasValue ? "unknown" : age > 5000 ? "not_ticking" : "ticking",
                    ["age_ms"] = age.HasValue ? new JValue(age.Value) : JValue.CreateNull(),
                    ["last_tick_unix_ms"] = Interlocked.Read(ref _lastTickUnixMs),
                    ["tick_count"] = Interlocked.Read(ref _ticks),
                    ["cause"] = "not_inferred"
                },
                ["editor_state"] = activity,
                ["queue"] = TransportCommandDispatcher.QueueDiagnostics(),
                ["native_modal"] = native,
                ["compilation"] = CompileDiagnostics.Snapshot()
            };
            BoundControlPayload(snapshot);
            return snapshot;
        }

        internal static void BoundControlPayload(JObject snapshot)
        {
            // Leave space for the control envelope within the negotiated 64 KiB bound.
            if (Encoding.UTF8.GetByteCount(snapshot.ToString(Newtonsoft.Json.Formatting.None)) > 60 * 1024)
            {
                var native = snapshot["native_modal"] as JObject ?? new JObject();
                foreach (var window in (native["windows"] as JArray ?? new JArray()))
                    if (window is JObject item) item.Remove("controls");
                native["text_truncated"] = true;
                snapshot["payload_truncated"] = true;
            }
            if (Encoding.UTF8.GetByteCount(snapshot.ToString(Newtonsoft.Json.Formatting.None)) > 60 * 1024)
            {
                if (snapshot["compilation"]?["last_cycle"] is JObject cycle)
                {
                    cycle["messages"] = new JArray();
                    cycle["messages_truncated"] = true;
                }
            }
            if (Encoding.UTF8.GetByteCount(snapshot.ToString(Newtonsoft.Json.Formatting.None)) > 60 * 1024)
            {
                // Save receipts can contain many long paths. They are not compile evidence.
                if (snapshot["editor_state"]?["assets"] is JObject assets)
                {
                    assets.Remove("editor_save_receipts");
                    assets.Remove("last_editor_save");
                    assets["save_receipts_truncated"] = true;
                }
            }
            if (Encoding.UTF8.GetByteCount(snapshot.ToString(Newtonsoft.Json.Formatting.None)) > 60 * 1024)
            {
                // Never let unusually large cached state/IDs defeat the independent control path.
                // Missing observation/correlation must remain unknown, not become an all-clear.
                snapshot["editor_state"] = new JObject();
                if (snapshot["compilation"] is JObject compilation)
                {
                    compilation["requests"] = new JArray();
                    compilation["requests_truncated"] = true;
                }
            }
        }

        internal static void TickForTests() => Tick();

        private static void Shutdown()
        {
            _stopping = true; // Fence late publications before joining the bounded worker.
            Stop.Set();
            Sampler?.Join(300);
            EditorApplication.update -= Tick;
        }

        private static void SampleLoop()
        {
            while (!_stopping)
            {
                JObject result;
                try { result = ProbeWindows(); }
                catch { result = new JObject { ["status"] = "probe_failed" }; }
                result["sampled_tick"] = Stopwatch.GetTimestamp();
                result["observed_at_unix_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_stopping) return;
                _native = result;
                if (Stop.Wait(500)) return;
            }
        }

        private static JObject ProbeWindows()
        {
            var watch = Stopwatch.StartNew();
            var windows = new JArray();
            int seen = 0;
            bool truncated = false;
            bool complete = EnumWindows((window, parameter) =>
            {
                if (_stopping || watch.ElapsedMilliseconds >= 200 || ++seen > 4096)
                { truncated = true; return false; }
                GetWindowThreadProcessId(window, out uint pid);
                if (pid != ProcessId || !IsWindowVisible(window)) return true;
                var className = new StringBuilder(128);
                GetClassName(window, className, className.Capacity);
                IntPtr owner = GetWindow(window, 4 /* GW_OWNER */);
                bool dialogClass = className.ToString() == "#32770";
                bool disabledOwner = owner != IntPtr.Zero && !IsWindowEnabled(owner);
                if (!dialogClass && !disabledOwner) return true;
                var controls = new JArray();
                string title = SafeText(window);
                EnumChildWindows(window, (child, unused) =>
                {
                    if (_stopping || watch.ElapsedMilliseconds >= 200 || controls.Count >= 16)
                    { truncated = true; return false; }
                    GetWindowThreadProcessId(child, out uint childPid);
                    if (childPid == ProcessId && IsWindowVisible(child))
                        controls.Add(SafeText(child));
                    return true;
                }, IntPtr.Zero);
                windows.Add(new JObject { ["title"] = title, ["class"] = className.ToString(),
                    ["disabled_owner"] = disabledOwner, ["controls"] = controls });
                if (windows.Count >= 4) { truncated = true; return false; }
                return true;
            }, IntPtr.Zero);
            return new JObject
            {
                ["status"] = windows.Count > 0 ? "modal_candidate_observed" :
                    truncated ? "incomplete" : !complete ? "probe_failed" : "no_supported_modal_observed",
                ["scope"] = "current_process_win32_dialogs_and_disabled_owners",
                ["windows"] = windows, ["truncated"] = truncated,
                ["automatic_answering"] = false
            };
        }

        private static string SafeText(IntPtr window)
        {
            var text = new StringBuilder(256);
            IntPtr result = SendMessageTimeout(window, 0x000D /* WM_GETTEXT */,
                new IntPtr(text.Capacity), text, 0x0002 /* SMTO_ABORTIFHUNG */, 20, out _);
            return result == IntPtr.Zero ? "[text unavailable]" : text.ToString();
        }

        private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(
            IntPtr window, uint message, IntPtr count, StringBuilder text, uint flags, uint timeout, out IntPtr result);
    }
}
