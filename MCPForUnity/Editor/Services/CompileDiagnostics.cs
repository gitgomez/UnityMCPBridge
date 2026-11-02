using System;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>Bounded event evidence, persisted in this Editor's SessionState across reloads.</summary>
    [InitializeOnLoad]
    internal static class CompileDiagnostics
    {
        private const string StateKey = "MCPForUnity.CompileDiagnostics.v1";
        private const int MaxCycles = 4, MaxRequests = 8, MaxMessages = 16;
        private static JObject _state;
        private static volatile JObject _published;
        private static string _projectRoot;
        private static object _context;

        static CompileDiagnostics()
        {
            _projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/');
            RestoreSessionState();
            CompilationPipeline.compilationStarted += Started;
            CompilationPipeline.assemblyCompilationFinished += AssemblyFinished;
            CompilationPipeline.compilationFinished += Finished;
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            { _state["before_reload_unix_ms"] = Now; Publish(); };
        }

        internal static void RestoreSessionState()
        {
            _context = null;
            try
            {
                _state = JObject.Parse(SessionState.GetString(StateKey, "{}"));
                if (_state.Value<string>("epoch") != CommandRuntimeState.Epoch ||
                    _state["counter"]?.Type != JTokenType.Integer ||
                    _state.Value<long>("counter") < 0 || _state.Value<long>("counter") == long.MaxValue ||
                    !(_state["cycles"] is JArray cycles) || cycles.Count > MaxCycles ||
                    cycles.Any(c => !(c is JObject)) ||
                    !(_state["requests"] is JArray requests) || requests.Count > MaxRequests ||
                    requests.Any(r => !(r is JObject)))
                    throw new JsonException("Invalid retained compile observation.");
            }
            catch
            {
                _state = new JObject { ["epoch"] = CommandRuntimeState.Epoch, ["counter"] = 0,
                    ["cycles"] = new JArray(), ["requests"] = new JArray() };
            }
            // A started cycle without its finish event is not a successful compilation.
            if (Latest?.Value<string>("status") == "compiling")
            {
                Latest["status"] = "interrupted_by_reload";
                Latest["capture_complete"] = false;
            }
            _state["after_reload_unix_ms"] = Now;
            Publish();
        }

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static JArray Cycles => (JArray)_state["cycles"];
        private static JArray Requests => (JArray)_state["requests"];
        private static JObject Latest => Cycles?.LastOrDefault() as JObject;

        internal static JObject BeginRequest(string requestId)
        {
            string id = string.IsNullOrEmpty(requestId) ? "legacy-" + Guid.NewGuid().ToString("N") : requestId;
            var active = Latest?.Value<string>("status") == "compiling" ? Latest : null;
            var request = new JObject
            {
                ["request_id"] = id, ["baseline_counter"] = _state.Value<long>("counter"),
                ["baseline_captured_unix_ms"] = Now, ["request_returned"] = false,
                ["correlation"] = active == null ? "awaiting_observed_cycle" : "shared_or_coalesced",
                ["candidate_cycle_ids"] = active == null ? new JArray() : new JArray(active.Value<string>("id"))
            };
            Requests.Add(request);
            while (Requests.Count > MaxRequests) Requests.RemoveAt(0);
            Publish();
            return (JObject)request.DeepClone();
        }

        internal static void MarkRequested(string requestId)
        {
            var request = Requests.OfType<JObject>().LastOrDefault(r => r.Value<string>("request_id") == requestId);
            if (request != null) { request["request_returned"] = true; Publish(); }
        }

        internal static void Started(object context)
        {
            _context = context;
            if (Latest?.Value<string>("status") == "compiling")
            { Latest["capture_complete"] = false; Latest["status"] = "superseded"; }
            long count = _state.Value<long>("counter") + 1;
            _state["counter"] = count;
            string id = _state.Value<string>("epoch") + ":" + count + ":" + Guid.NewGuid().ToString("N");
            Cycles.Add(new JObject
            {
                ["id"] = id, ["counter"] = count, ["status"] = "compiling",
                ["started_unix_ms"] = Now, ["capture_complete"] = true,
                ["errors"] = 0, ["warnings"] = 0, ["messages"] = new JArray(),
                ["messages_truncated"] = false, ["assemblies_observed"] = 0
            });
            while (Cycles.Count > MaxCycles) Cycles.RemoveAt(0);
            foreach (var request in Requests.OfType<JObject>())
            {
                string correlation = request.Value<string>("correlation");
                if (correlation == "awaiting_observed_cycle" || correlation == "shared_or_coalesced")
                {
                    ((JArray)request["candidate_cycle_ids"]).Add(id);
                    request["correlation"] = correlation == "shared_or_coalesced" ?
                        "multiple_candidates" : "observed_not_exclusive";
                }
            }
            Publish();
        }

        internal static void AssemblyFinished(string assembly, CompilerMessage[] messages)
        {
            var cycle = Latest;
            if (cycle?.Value<string>("status") != "compiling") return;
            cycle["assemblies_observed"] = cycle.Value<int>("assemblies_observed") + 1;
            var retained = (JArray)cycle["messages"];
            foreach (var message in messages ?? Array.Empty<CompilerMessage>())
            {
                string severity = message.type == CompilerMessageType.Error ? "error" : "warning";
                string field = severity == "error" ? "errors" : "warnings";
                cycle[field] = cycle.Value<int>(field) + 1;
                if (retained.Count >= MaxMessages) { cycle["messages_truncated"] = true; continue; }
                retained.Add(new JObject { ["severity"] = severity,
                    ["assembly"] = Limit(Path.GetFileName(assembly), 128),
                    ["file"] = SafePath(message.file), ["line"] = message.line, ["column"] = message.column,
                    ["message"] = Limit(message.message, 256),
                    ["text_truncated"] = (message.message?.Length ?? 0) > 256 });
            }
            Publish();
        }

        internal static void Finished(object context)
        {
            var cycle = Latest;
            if (cycle?.Value<string>("status") != "compiling") return;
            bool matches = ReferenceEquals(_context, context);
            cycle["capture_complete"] = cycle.Value<bool>("capture_complete") && matches;
            cycle["status"] = matches ? "completed" : "context_mismatch";
            cycle["finished_unix_ms"] = Now;
            cycle["succeeded"] = matches && cycle.Value<bool>("capture_complete")
                ? new JValue(cycle.Value<int>("errors") == 0) : JValue.CreateNull();
            _context = null;
            Publish();
        }

        private static string Limit(string text, int count) =>
            string.IsNullOrEmpty(text) ? "" : text.Length <= count ? text : text.Substring(0, count);

        private static string SafePath(string path)
        {
            string normalized = (path ?? "").Replace('\\', '/');
            if (normalized.StartsWith(_projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(_projectRoot.Length + 1);
            else if (Path.IsPathRooted(normalized)) normalized = Path.GetFileName(normalized);
            return Limit(normalized, 256);
        }

        private static void Publish()
        {
            // Main-thread events own SessionState. Transport readers see an immutable clone.
            SessionState.SetString(StateKey, _state.ToString(Formatting.None));
            _published = (JObject)_state.DeepClone();
        }

        internal static JObject Snapshot()
        {
            var state = _published == null ? new JObject() : (JObject)_published.DeepClone();
            var cycles = state["cycles"] as JArray;
            state["last_cycle"] = cycles?.LastOrDefault()?.DeepClone();
            state["history"] = new JArray((cycles ?? new JArray()).OfType<JObject>().Select(c =>
                new JObject { ["id"] = c["id"], ["status"] = c["status"],
                    ["errors"] = c["errors"], ["capture_complete"] = c["capture_complete"] }));
            state.Remove("cycles");
            state["capture_available"] = true;
            state["history_limit"] = MaxCycles;
            return state;
        }
    }
}
