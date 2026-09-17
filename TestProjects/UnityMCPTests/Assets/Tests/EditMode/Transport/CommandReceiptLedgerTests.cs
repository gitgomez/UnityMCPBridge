using System;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Transport
{
    [TestFixture]
    public class CommandReceiptLedgerTests
    {
        private string projectRoot;

        [SetUp]
        public void SetUp()
        {
            projectRoot = Path.Combine(
                Path.GetTempPath(),
                "UnityMCPReceiptTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projectRoot);
            CommandReceiptLedger.ResetForTests(projectRoot);
        }

        [TearDown]
        public void TearDown()
        {
            CommandReceiptLedger.ResetForTests();
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }

        [Test]
        public void TerminalDuplicate_ReturnsCachedResultWithoutExecutingAgain()
        {
            JObject runtime = BuildRuntime();
            ReceiptAdmission first = Admit(runtime);
            Assert.IsTrue(first.ShouldExecute);

            CommandReceiptLedger.MarkQueued(first.RequestId);
            CommandReceiptLedger.MarkExecuting(first.RequestId);
            var terminal = new JObject
            {
                ["status"] = "success",
                ["result"] = new JObject { ["value"] = 42 }
            };
            CommandReceiptLedger.Complete(first.RequestId, terminal);

            ReceiptAdmission duplicate = Admit(runtime);

            Assert.IsFalse(duplicate.ShouldExecute);
            Assert.IsTrue(JToken.DeepEquals(terminal, duplicate.ImmediateResult));
            Assert.AreEqual("succeeded", CommandReceiptLedger.Query(first.RequestId).Value<string>("state"));
        }

        [Test]
        public void RuntimeSucceededEnvelope_IsPersistedAsSucceeded()
        {
            ReceiptAdmission first = Admit(BuildRuntime());
            CommandReceiptLedger.MarkQueued(first.RequestId);
            CommandReceiptLedger.MarkExecuting(first.RequestId);

            CommandReceiptLedger.Complete(
                first.RequestId,
                new JObject
                {
                    ["runtime_version"] = 1,
                    ["status"] = "succeeded",
                    ["message"] = "pong",
                    ["data"] = new JObject { ["message"] = "pong" }
                });

            Assert.AreEqual(
                "succeeded",
                CommandReceiptLedger.Query(first.RequestId).Value<string>("state"));
        }

        [Test]
        public void SameRequestIdWithDifferentHash_IsRejectedAsConflict()
        {
            JObject runtime = BuildRuntime();
            ReceiptAdmission first = Admit(runtime);
            Assert.IsTrue(first.ShouldExecute);

            JObject conflicting = (JObject)runtime.DeepClone();
            conflicting["payload_hash"] = "sha256:" + new string('b', 64);
            ReceiptAdmission duplicate = Admit(conflicting);

            Assert.IsFalse(duplicate.ShouldExecute);
            Assert.AreEqual("REQUEST_ID_CONFLICT", duplicate.ImmediateResult.Value<string>("code"));
        }

        [Test]
        public void RetryWithoutRetainedReceipt_DoesNotExecute()
        {
            JObject runtime = BuildRuntime(attempt: 2);

            ReceiptAdmission admission = Admit(runtime);

            Assert.IsFalse(admission.ShouldExecute);
            Assert.AreEqual("RECEIPT_NOT_AVAILABLE", admission.ImmediateResult.Value<string>("code"));
            Assert.AreEqual("not_found", CommandReceiptLedger.Query(runtime.Value<string>("request_id")).Value<string>("state"));
        }

        [Test]
        public void ReloadBeforeExecution_ConvertsAcceptedReceiptToCancelled()
        {
            JObject runtime = BuildRuntime();
            ReceiptAdmission first = Admit(runtime);
            Assert.IsTrue(first.ShouldExecute);

            CommandReceiptLedger.ResetForTests();
            CommandReceiptLedger.Initialize(projectRoot);

            Assert.AreEqual("cancelled", CommandReceiptLedger.Query(first.RequestId).Value<string>("state"));
            ReceiptAdmission duplicate = Admit(runtime);
            Assert.AreEqual("RELOAD_BEFORE_EXECUTION", duplicate.ImmediateResult.Value<string>("code"));
        }

        [Test]
        public void ReloadDuringExecution_ConvertsReceiptToOutcomeUnknown()
        {
            JObject runtime = BuildRuntime();
            ReceiptAdmission first = Admit(runtime);
            CommandReceiptLedger.MarkQueued(first.RequestId);
            CommandReceiptLedger.MarkExecuting(first.RequestId);

            CommandReceiptLedger.ResetForTests();
            CommandReceiptLedger.Initialize(projectRoot);

            Assert.AreEqual("outcome_unknown", CommandReceiptLedger.Query(first.RequestId).Value<string>("state"));
            ReceiptAdmission duplicate = Admit(runtime);
            Assert.AreEqual("OUTCOME_UNKNOWN", duplicate.ImmediateResult.Value<string>("code"));
        }

        [Test]
        public void OversizedTerminalResult_IsDigestedButNotCached()
        {
            JObject runtime = BuildRuntime();
            ReceiptAdmission first = Admit(runtime);
            CommandReceiptLedger.MarkQueued(first.RequestId);
            CommandReceiptLedger.MarkExecuting(first.RequestId);
            CommandReceiptLedger.Complete(
                first.RequestId,
                new JObject
                {
                    ["status"] = "success",
                    ["content"] = new string('x', CommandReceiptLedger.MaxCachedResultBytes)
                });

            JObject snapshot = CommandReceiptLedger.Query(first.RequestId);
            Assert.IsTrue(snapshot.Value<bool>("result_evicted"));
            StringAssert.StartsWith("sha256:", snapshot.Value<string>("result_hash"));

            ReceiptAdmission duplicate = Admit(runtime);
            Assert.AreEqual("RESULT_EVICTED", duplicate.ImmediateResult.Value<string>("code"));
        }

        [Test]
        public void PersistedLedger_DoesNotContainRawCommandParameters()
        {
            const string secretParameter = "do-not-persist-this-parameter";
            ReceiptAdmission admission = CommandReceiptLedger.TryAdmit(
                BuildRuntime(),
                "manage_scene",
                new JObject { ["secret"] = secretParameter },
                "project-hash");
            Assert.IsTrue(admission.ShouldExecute);

            string ledger = File.ReadAllText(Path.Combine(
                projectRoot,
                "Library",
                "MCPForUnity",
                "RunState",
                "command-receipts-v1.json"));

            StringAssert.DoesNotContain(secretParameter, ledger);
            StringAssert.Contains(admission.RequestId, ledger);
        }

        [Test]
        public void AdministrativeCleanup_Exactly512TerminalReceipts_PreservesActiveAndRequiresOutcomeUnknownConfirmation()
        {
            long initialNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            CommandReceiptLedger.SetUtcNowForTests(initialNow);
            for (int index = 0; index < CommandReceiptLedger.MaxReceiptCount; index++)
            {
                ReceiptAdmission terminal = Admit(BuildRuntime());
                Assert.IsTrue(terminal.ShouldExecute, $"Receipt {index} was not admitted.");
                CommandReceiptLedger.Complete(
                    terminal.RequestId,
                    new JObject { ["status"] = "success", ["index"] = index });
            }

            JObject full = CommandReceiptLedger.Diagnose();
            Assert.AreEqual(512, full.Value<int>("total"));
            Assert.AreEqual(512, full["counts"].Value<int>("succeeded"));

            long expiredNow = initialNow
                + (long)CommandReceiptLedger.ReceiptRetention.TotalMilliseconds
                + 1;
            CommandReceiptLedger.SetUtcNowForTests(expiredNow);
            JObject expiredCleanup = CommandReceiptLedger.Cleanup();

            Assert.IsTrue(expiredCleanup.Value<bool>("success"));
            Assert.AreEqual(512, expiredCleanup.Value<int>("removed_total"));
            Assert.AreEqual(512, expiredCleanup["removed_counts"].Value<int>("succeeded"));
            Assert.AreEqual(0, expiredCleanup["after"].Value<int>("total"));

            CommandReceiptLedger.ResetForTests();
            CommandReceiptLedger.Initialize(projectRoot);
            Assert.AreEqual(0, CommandReceiptLedger.Diagnose().Value<int>("total"));

            ReceiptAdmission unknown = Admit(BuildRuntime());
            CommandReceiptLedger.MarkQueued(unknown.RequestId);
            CommandReceiptLedger.MarkExecuting(unknown.RequestId);
            CommandReceiptLedger.ResetForTests();
            CommandReceiptLedger.Initialize(projectRoot);
            Assert.AreEqual(
                "outcome_unknown",
                CommandReceiptLedger.Query(unknown.RequestId).Value<string>("state"));

            ReceiptAdmission accepted = Admit(BuildRuntime());
            ReceiptAdmission queued = Admit(BuildRuntime());
            CommandReceiptLedger.MarkQueued(queued.RequestId);
            ReceiptAdmission executing = Admit(BuildRuntime());
            CommandReceiptLedger.MarkQueued(executing.RequestId);
            CommandReceiptLedger.MarkExecuting(executing.RequestId);
            ReceiptAdmission succeeded = Admit(BuildRuntime());
            CommandReceiptLedger.Complete(
                succeeded.RequestId,
                new JObject { ["status"] = "success" });

            JObject diagnosed = CommandReceiptLedger.Diagnose();
            Assert.AreEqual(1, diagnosed["counts"].Value<int>("accepted"));
            Assert.AreEqual(1, diagnosed["counts"].Value<int>("queued"));
            Assert.AreEqual(1, diagnosed["counts"].Value<int>("executing"));
            Assert.AreEqual(1, diagnosed["counts"].Value<int>("succeeded"));
            Assert.AreEqual(1, diagnosed["counts"].Value<int>("outcome_unknown"));

            JObject knownTerminalCleanup = CommandReceiptLedger.Cleanup("all_terminal");
            Assert.AreEqual(1, knownTerminalCleanup.Value<int>("removed_total"));
            Assert.AreEqual(1, knownTerminalCleanup.Value<int>("preserved_outcome_unknown"));
            Assert.AreEqual(
                "outcome_unknown",
                CommandReceiptLedger.Query(unknown.RequestId).Value<string>("state"));

            JObject confirmedCleanup = CommandReceiptLedger.Cleanup(
                "all_terminal",
                confirmOutcomeUnknown: true);
            Assert.AreEqual(1, confirmedCleanup.Value<int>("removed_total"));
            Assert.AreEqual(1, confirmedCleanup["removed_counts"].Value<int>("outcome_unknown"));
            Assert.AreEqual(3, confirmedCleanup["after"].Value<int>("active"));
            Assert.AreEqual(0, confirmedCleanup["after"].Value<int>("terminal"));
            Assert.AreEqual("accepted", CommandReceiptLedger.Query(accepted.RequestId).Value<string>("state"));
            Assert.AreEqual("queued", CommandReceiptLedger.Query(queued.RequestId).Value<string>("state"));
            Assert.AreEqual("executing", CommandReceiptLedger.Query(executing.RequestId).Value<string>("state"));

            JObject persisted = JObject.Parse(File.ReadAllText(Path.Combine(
                projectRoot,
                "Library",
                "MCPForUnity",
                "RunState",
                "command-receipts-v1.json")));
            string[] persistedIds = persisted["receipts"]
                .Values<string>("request_id")
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            string[] expectedActiveIds = new[]
                {
                    accepted.RequestId,
                    queued.RequestId,
                    executing.RequestId
                }
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(expectedActiveIds, persistedIds);
        }

        private ReceiptAdmission Admit(JObject runtime)
        {
            return CommandReceiptLedger.TryAdmit(
                runtime,
                "manage_scene",
                new JObject { ["action"] = "save" },
                "project-hash");
        }

        private static JObject BuildRuntime(int attempt = 1)
        {
            return new JObject
            {
                ["version"] = 1,
                ["request_id"] = Guid.NewGuid().ToString(),
                ["attempt"] = attempt,
                ["payload_hash"] = "sha256:" + new string('a', 64),
                ["deadline_unix_ms"] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
                ["contract_version"] = 1,
                ["profile"] = "standard"
            };
        }
    }
}
