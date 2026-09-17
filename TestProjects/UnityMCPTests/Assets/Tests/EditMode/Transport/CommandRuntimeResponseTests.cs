using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Transport
{
    [TestFixture]
    public class CommandRuntimeResponseTests
    {
        [Test]
        public void SuccessResult_IsMappedToStableEnvelope()
        {
            var legacy = new JObject
            {
                ["status"] = "success",
                ["result"] = new JObject
                {
                    ["message"] = "Created.",
                    ["instance_id"] = 42,
                    ["changes"] = new JObject
                    {
                        ["objects"] = new JArray(new JObject { ["instance_id"] = 42 })
                    }
                }
            };

            JObject envelope = CommandRuntimeResponse.Adapt(
                legacy,
                "request-1",
                queuedMilliseconds: 3,
                executionMilliseconds: 12);

            Assert.AreEqual(1, envelope.Value<int>("runtime_version"));
            Assert.AreEqual("request-1", envelope.Value<string>("request_id"));
            Assert.AreEqual("succeeded", envelope.Value<string>("status"));
            Assert.AreEqual("OK", envelope.Value<string>("code"));
            Assert.AreEqual("Created.", envelope.Value<string>("message"));
            Assert.AreEqual(42, envelope["data"]?.Value<int>("instance_id"));
            Assert.AreEqual(42, envelope["changes"]?["objects"]?[0]?.Value<int>("instance_id"));
            Assert.AreEqual(3, envelope["timing"]?.Value<int>("queued_ms"));
            Assert.AreEqual(12, envelope["timing"]?.Value<int>("execution_ms"));
            Assert.AreEqual(15, envelope["timing"]?.Value<int>("total_ms"));
            StringAssert.StartsWith("sha256:", envelope["receipt"]?.Value<string>("result_digest"));
            Assert.IsNotNull(envelope["state"]?.Value<string>("epoch"));
            Assert.AreEqual(
                envelope["state"]?.Value<long>("before_revision"),
                envelope["state"]?.Value<long>("after_revision"));
        }

        [Test]
        public void NestedToolFailure_IsNotMaskedBySuccessfulDispatcherStatus()
        {
            var legacy = new JObject
            {
                ["status"] = "success",
                ["result"] = new JObject
                {
                    ["success"] = false,
                    ["code"] = "BATCH_EXECUTION_FAILED",
                    ["error"] = "Child failed."
                }
            };

            JObject envelope = CommandRuntimeResponse.Adapt(legacy, "request-nested", 0, 1);

            Assert.AreEqual("failed", envelope.Value<string>("status"));
            Assert.AreEqual("BATCH_EXECUTION_FAILED", envelope.Value<string>("code"));
            Assert.AreEqual("Child failed.", envelope.Value<string>("message"));
        }

        [Test]
        public void ErrorResult_PreservesStableCodeAndOmitsStackTraceFromData()
        {
            var legacy = new JObject
            {
                ["status"] = "error",
                ["code"] = "INVALID_ARGUMENT",
                ["error"] = "Bad input.",
                ["stackTrace"] = "sensitive stack"
            };

            JObject envelope = CommandRuntimeResponse.Adapt(legacy, "request-2", 0, 1);

            Assert.AreEqual("failed", envelope.Value<string>("status"));
            Assert.AreEqual("INVALID_ARGUMENT", envelope.Value<string>("code"));
            Assert.AreEqual("Bad input.", envelope.Value<string>("message"));
            Assert.IsNull(envelope["data"]?["stackTrace"]);
        }

        [Test]
        public void ReceiptState_ControlsNonTerminalEnvelopeStatus()
        {
            var legacy = new JObject
            {
                ["status"] = "error",
                ["code"] = "REQUEST_IN_PROGRESS",
                ["error"] = "Request is queued.",
                ["receipt"] = new JObject
                {
                    ["request_id"] = "request-3",
                    ["state"] = "queued",
                    ["result_hash"] = "sha256:" + new string('a', 64)
                },
                ["data"] = new JObject { ["retry_after_ms"] = 100 }
            };

            JObject envelope = CommandRuntimeResponse.Adapt(legacy, "request-3", 5, 0);

            Assert.AreEqual("queued", envelope.Value<string>("status"));
            Assert.AreEqual("queued", envelope["receipt"]?.Value<string>("state"));
            Assert.AreEqual(100, envelope["data"]?.Value<int>("retry_after_ms"));
        }

        [Test]
        public void DiagnosticMessage_IsBoundedWithExplicitMarker()
        {
            var legacy = new JObject
            {
                ["status"] = "error",
                ["error"] = new string('x', CommandRuntimeResponse.MaxMessageCharacters + 10)
            };

            JObject envelope = CommandRuntimeResponse.Adapt(legacy, "request-4", 0, 0);

            Assert.AreEqual(
                CommandRuntimeResponse.MaxMessageCharacters,
                envelope.Value<string>("message").Length);
            Assert.AreEqual(
                "MESSAGE_TRUNCATED",
                envelope["diagnostics"]?[0]?.Value<string>("code"));
        }
    }
}
