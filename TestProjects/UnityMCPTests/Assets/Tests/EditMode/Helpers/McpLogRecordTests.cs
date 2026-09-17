using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Helpers
{
    public class McpLogRecordTests
    {
        [Test]
        public void SanitizeParametersForLog_RedactsSetTextWithoutMutatingRequest()
        {
            var request = new JObject
            {
                ["action"] = "set_text",
                ["target"] = "PasswordInput",
                ["text"] = "secret-value",
            };

            JObject sanitized = McpLogRecord.SanitizeParametersForLog(
                "interact_play_mode",
                request);

            Assert.AreEqual("<redacted>", sanitized.Value<string>("text"));
            Assert.AreEqual("secret-value", request.Value<string>("text"));
            Assert.AreEqual("PasswordInput", sanitized.Value<string>("target"));
        }

        [Test]
        public void SanitizeParametersForLog_RedactsNestedBatchSetText()
        {
            var request = new JObject
            {
                ["commands"] = new JArray
                {
                    new JObject
                    {
                        ["tool"] = "interact_play_mode",
                        ["params"] = new JObject
                        {
                            ["action"] = "set_text",
                            ["text"] = "nested-secret",
                        },
                    },
                    new JObject
                    {
                        ["tool"] = "interact_play_mode",
                        ["params"] = new JObject
                        {
                            ["action"] = "set_toggle",
                            ["value"] = true,
                        },
                    },
                },
            };

            JObject sanitized = McpLogRecord.SanitizeParametersForLog(
                "batch_execute",
                request);

            Assert.AreEqual(
                "<redacted>",
                sanitized["commands"][0]["params"].Value<string>("text"));
            Assert.IsTrue(sanitized["commands"][1]["params"].Value<bool>("value"));
        }
    }
}
