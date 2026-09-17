using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Transport
{
    [TestFixture]
    public class CommandRuntimeProtocolTests
    {
        [Test]
        public void BuildUnityAdvertisement_AdvertisesImplementedCapabilitiesAndLimits()
        {
            JObject advertisement = CommandRuntimeProtocol.BuildUnityAdvertisement("10.1.1-beta.1");

            Assert.AreEqual("command-runtime", advertisement.Value<string>("protocol"));
            Assert.AreEqual(1, advertisement.Value<int>("major"));
            Assert.AreEqual(0, advertisement.Value<int>("minor"));
            Assert.AreEqual("10.1.1-beta.1", advertisement.Value<string>("package_version"));
            CollectionAssert.AreEqual(
                new[]
                {
                    "capability_negotiation_v1",
                    "contract_manifest_v1",
                    "bounded_command_queue_v1",
                    "control_path_v1",
                    "request_receipts_v1",
                    "receipt_ledger_admin_v1",
                    "response_envelope_v1",
                    "mutation_contracts_v1",
                    "stable_handles_v1",
                    "state_revisions_v1",
                    "batch_semantics_v1",
                    "reload_lifecycle_v1"
                },
                ((JArray)advertisement["capabilities"]).Values<string>());
            Assert.IsTrue(advertisement.Value<bool>("legacy_fallback"));
            Assert.AreEqual(1, advertisement.Value<int>("contract_version"));
            StringAssert.StartsWith("sha256:", advertisement.Value<string>("built_in_schema_hash"));
            var limits = (JObject)advertisement["limits"];
            Assert.AreEqual(8 * 1024 * 1024, limits.Value<int>("max_command_message_bytes"));
            Assert.AreEqual(16 * 1024 * 1024, limits.Value<int>("max_response_message_bytes"));
            Assert.AreEqual(64, limits.Value<int>("max_queued_commands"));
            Assert.AreEqual(16 * 1024 * 1024, limits.Value<int>("max_queued_payload_bytes"));
            Assert.AreEqual(64 * 1024, limits.Value<int>("max_control_message_bytes"));
            Assert.AreEqual(512, limits.Value<int>("max_receipts"));
            Assert.AreEqual(30 * 60, limits.Value<int>("receipt_retention_seconds"));
            Assert.AreEqual(8 * 1024 * 1024, limits.Value<int>("max_persisted_receipt_bytes"));
            Assert.AreEqual(256 * 1024, limits.Value<int>("max_cached_result_bytes"));
        }

        [Test]
        public void MissingAdvertisement_UsesLegacyMode()
        {
            JObject local = CommandRuntimeProtocol.BuildUnityAdvertisement("local");

            CommandRuntimeNegotiation result = CommandRuntimeProtocol.Negotiate(local, null);

            Assert.AreEqual(CommandRuntimeMode.Legacy, result.Mode);
        }

        [Test]
        public void MatchingAdvertisements_NegotiateCapabilityIntersection()
        {
            JObject local = CommandRuntimeProtocol.BuildUnityAdvertisement("local");
            JObject remote = BuildRemoteAdvertisement();
            ((JArray)remote["capabilities"]).Add("future_feature");

            CommandRuntimeNegotiation result = CommandRuntimeProtocol.Negotiate(local, remote);

            Assert.AreEqual(CommandRuntimeMode.RuntimeV1, result.Mode);
            CollectionAssert.AreEqual(
                new[]
                {
                    "batch_semantics_v1",
                    "bounded_command_queue_v1",
                    "capability_negotiation_v1",
                    "contract_manifest_v1",
                    "control_path_v1",
                    "mutation_contracts_v1",
                    "receipt_ledger_admin_v1",
                    "reload_lifecycle_v1",
                    "request_receipts_v1",
                    "response_envelope_v1",
                    "stable_handles_v1",
                    "state_revisions_v1"
                },
                result.Capabilities);
            Assert.AreEqual(0, result.Minor);
            Assert.IsEmpty(result.Diagnostics);
        }

        [Test]
        public void SchemaMismatch_IsDegradedInsteadOfIncompatible()
        {
            JObject local = CommandRuntimeProtocol.BuildUnityAdvertisement("local");
            JObject remote = BuildRemoteAdvertisement();
            local["built_in_schema_hash"] = "sha256:local";
            remote["built_in_schema_hash"] = "sha256:remote";

            CommandRuntimeNegotiation result = CommandRuntimeProtocol.Negotiate(local, remote);

            Assert.AreEqual(CommandRuntimeMode.Degraded, result.Mode);
            CollectionAssert.Contains(result.Diagnostics, "built-in tool schema hash mismatch");
        }

        [TestCase(true, "Legacy")]
        [TestCase(false, "Incompatible")]
        public void MajorMismatch_RespectsLegacyFallback(
            bool legacyFallback,
            string expectedMode)
        {
            JObject local = CommandRuntimeProtocol.BuildUnityAdvertisement("local");
            JObject remote = BuildRemoteAdvertisement();
            local["legacy_fallback"] = legacyFallback;
            remote["legacy_fallback"] = legacyFallback;
            remote["major"] = 2;

            CommandRuntimeNegotiation result = CommandRuntimeProtocol.Negotiate(local, remote);

            Assert.AreEqual(expectedMode, result.Mode.ToString());
        }

        private static JObject BuildRemoteAdvertisement()
        {
            return new JObject
            {
                ["protocol"] = "command-runtime",
                ["major"] = 1,
                ["minor"] = 0,
                ["server_version"] = "10.1.0",
                ["contract_version"] = 1,
                ["built_in_schema_hash"] = CommandRuntimeContract.BuiltInSchemaHash,
                ["capabilities"] = new JArray(
                    "capability_negotiation_v1",
                    "contract_manifest_v1",
                    "bounded_command_queue_v1",
                    "control_path_v1",
                    "request_receipts_v1",
                    "receipt_ledger_admin_v1",
                    "response_envelope_v1",
                    "mutation_contracts_v1",
                    "stable_handles_v1",
                    "state_revisions_v1",
                    "batch_semantics_v1",
                    "reload_lifecycle_v1"),
                ["legacy_fallback"] = true
            };
        }
    }
}
