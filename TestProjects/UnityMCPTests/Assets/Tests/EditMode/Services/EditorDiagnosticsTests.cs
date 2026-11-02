using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;

namespace MCPForUnityTests.Editor.Services
{
    [TestFixture]
    public class EditorDiagnosticsTests
    {
        private const string Key = "MCPForUnity.CompileDiagnostics.v1";
        private string saved;

        [SetUp]
        public void SetUp()
        {
            // Initialise on the main thread, then isolate only this diagnostic state.
            CompileDiagnostics.Snapshot();
            saved = SessionState.GetString(Key, "{}");
            SessionState.EraseString(Key);
            CompileDiagnostics.RestoreSessionState();
        }

        [TearDown]
        public void TearDown()
        {
            SessionState.SetString(Key, saved);
            CompileDiagnostics.RestoreSessionState();
        }

        [Test]
        public void ControlReadDoesNotAdvanceMainThreadHeartbeat()
        {
            EditorDiagnostics.TickForTests();
            JObject before = EditorDiagnostics.Snapshot();
            JObject background = Task.Run(() => EditorDiagnostics.Snapshot()).GetAwaiter().GetResult();
            Assert.AreEqual(before["main_thread"].Value<long>("tick_count"),
                background["main_thread"].Value<long>("tick_count"));
            Assert.GreaterOrEqual(background["main_thread"].Value<double>("age_ms"),
                before["main_thread"].Value<double>("age_ms"));
        }

        [Test]
        public void OversizedCachedStateCannotDefeatTheControlPayloadBound()
        {
            var snapshot = new JObject
            {
                ["editor_state"] = new JObject { ["assets"] = new JObject
                    { ["editor_save_receipts"] = new JArray(new string('\u754c', 40000)) },
                    ["editor"] = new JObject { ["long_path"] = new string('x', 70000) } },
                ["compilation"] = new JObject
                {
                    ["requests"] = new JArray(new string('r', 70000)),
                    ["last_cycle"] = new JObject { ["errors"] = 2, ["succeeded"] = false,
                        ["messages"] = new JArray(new string('\u754c', 40000)) }
                },
                ["native_modal"] = new JObject { ["windows"] = new JArray() }
            };
            EditorDiagnostics.BoundControlPayload(snapshot);
            Assert.LessOrEqual(System.Text.Encoding.UTF8.GetByteCount(
                snapshot.ToString(Newtonsoft.Json.Formatting.None)), 60 * 1024);
            Assert.IsTrue(snapshot.Value<bool>("payload_truncated"));
            Assert.IsFalse(snapshot["compilation"]["last_cycle"].Value<bool>("succeeded"));
            Assert.AreEqual(2, snapshot["compilation"]["last_cycle"].Value<int>("errors"));
            Assert.IsTrue(snapshot["compilation"].Value<bool>("requests_truncated"));
            Assert.IsEmpty((JObject)snapshot["editor_state"]);
        }

        [Test]
        public void InvalidRetainedStateBecomesUnknownInsteadOfAFalseCompileSuccess()
        {
            var invalid = new JObject
            {
                ["epoch"] = CompileDiagnostics.Snapshot()["epoch"], ["counter"] = "not-a-counter",
                ["cycles"] = new JArray(), ["requests"] = new JArray()
            };
            SessionState.SetString(Key, invalid.ToString());
            Assert.DoesNotThrow(() => CompileDiagnostics.RestoreSessionState());
            JObject snapshot = CompileDiagnostics.Snapshot();
            Assert.AreEqual(JTokenType.Null, snapshot["last_cycle"].Type);
            Assert.AreEqual(0, snapshot.Value<long>("counter"));
            Assert.IsEmpty((JArray)snapshot["requests"]);
        }

        [Test]
        public void CompletedCycleRetainsCompilerErrorsAndIdentityAcrossReload()
        {
            JObject request = CompileDiagnostics.BeginRequest("original-receipt");
            CompileDiagnostics.MarkRequested("original-receipt");
            object context = new object();
            CompileDiagnostics.Started(context);
            CompileDiagnostics.AssemblyFinished("Assembly-CSharp.dll", new[]
            {
                new CompilerMessage { type = CompilerMessageType.Error, message = "example error",
                    file = "Assets/Example.cs", line = 7, column = 3 }
            });
            CompileDiagnostics.Finished(context);
            JObject before = CompileDiagnostics.Snapshot();
            CompileDiagnostics.RestoreSessionState();
            JObject after = CompileDiagnostics.Snapshot();
            Assert.AreEqual(before["last_cycle"].Value<string>("id"), after["last_cycle"].Value<string>("id"));
            Assert.AreEqual("completed", after["last_cycle"].Value<string>("status"));
            Assert.IsFalse(after["last_cycle"].Value<bool>("succeeded"));
            Assert.AreEqual(1, after["last_cycle"].Value<int>("errors"));
            Assert.AreEqual("original-receipt", after["requests"][0].Value<string>("request_id"));
            Assert.AreEqual(0, request.Value<long>("baseline_counter"));
        }

        [Test]
        public void MissingFinishAndMismatchedContextNeverClaimSuccess()
        {
            CompileDiagnostics.Started(new object());
            CompileDiagnostics.RestoreSessionState();
            JObject interrupted = (JObject)CompileDiagnostics.Snapshot()["last_cycle"];
            Assert.AreEqual("interrupted_by_reload", interrupted.Value<string>("status"));
            Assert.IsFalse(interrupted.Value<bool>("capture_complete"));
            CompileDiagnostics.Started(new object());
            CompileDiagnostics.Finished(new object());
            JObject mismatch = (JObject)CompileDiagnostics.Snapshot()["last_cycle"];
            Assert.AreEqual("context_mismatch", mismatch.Value<string>("status"));
            Assert.AreEqual(JTokenType.Null, mismatch["succeeded"].Type);
        }

        [Test]
        public void RequestDuringRunningCycleIsExplicitlyAmbiguousAndCaptureIsBounded()
        {
            object context = new object();
            CompileDiagnostics.Started(context);
            JObject request = CompileDiagnostics.BeginRequest("coalesced");
            Assert.AreEqual("shared_or_coalesced", request.Value<string>("correlation"));
            var messages = new CompilerMessage[32];
            for (int i = 0; i < messages.Length; i++)
                messages[i] = new CompilerMessage { type = CompilerMessageType.Warning, message = new string('w', 400) };
            CompileDiagnostics.AssemblyFinished("Assembly.dll", messages);
            CompileDiagnostics.Finished(context);
            JObject cycle = (JObject)CompileDiagnostics.Snapshot()["last_cycle"];
            Assert.AreEqual(32, cycle.Value<int>("warnings"));
            Assert.AreEqual(16, ((JArray)cycle["messages"]).Count);
            Assert.IsTrue(cycle.Value<bool>("messages_truncated"));
            Assert.IsTrue(cycle["messages"][0].Value<bool>("text_truncated"));
        }
    }
}
