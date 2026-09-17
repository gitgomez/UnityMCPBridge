using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Services.Transport.Transports;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    [TestFixture]
    public class WebSocketTransportClientTests
    {
        private const string CandidateBuilderMethodName = "BuildConnectionCandidateUris";
        private const string WebSocketTransportClientTypeName = "MCPForUnity.Editor.Services.Transport.Transports.WebSocketTransportClient";
        private static readonly MethodInfo BuildConnectionCandidateUrisMethod = ResolveCandidateBuilderMethod();

        [Test]
        public void BuildConnectionCandidateUris_NullEndpoint_ReturnsEmptyList()
        {
            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(null);

            // Assert
            Assert.IsNotNull(candidates);
            Assert.AreEqual(0, candidates.Count);
        }

        [Test]
        public void BuildConnectionCandidateUris_NonLocalhost_ReturnsOriginalOnly()
        {
            // Arrange
            var endpoint = new Uri("ws://127.0.0.1:8080/hub/plugin");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual(endpoint, candidates[0]);
        }

        [Test]
        public void BuildConnectionCandidateUris_Localhost_AddsIPv4AndIPv6Fallbacks()
        {
            // Arrange
            var endpoint = new Uri("ws://localhost:8080/hub/plugin");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(3, candidates.Count);
            CollectionAssert.AreEqual(
                new[] { "localhost", "127.0.0.1", "::1" },
                candidates.Select(uri => NormalizeHostForComparison(uri.Host)).ToArray());

            int uniqueCount = candidates
                .Select(uri => uri.AbsoluteUri)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            Assert.AreEqual(candidates.Count, uniqueCount, "Fallback list should not contain duplicate endpoints.");
        }

        [Test]
        public void BuildConnectionCandidateUris_LocalhostFallbacks_PreserveSchemePortPathAndQuery()
        {
            // Arrange
            var endpoint = new Uri("wss://localhost:9443/custom/path?mode=test");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(3, candidates.Count);
            foreach (Uri candidate in candidates)
            {
                Assert.AreEqual("wss", candidate.Scheme);
                Assert.AreEqual(9443, candidate.Port);
                Assert.AreEqual("/custom/path", candidate.AbsolutePath);
                Assert.AreEqual("?mode=test", candidate.Query);
            }
        }

        [Test]
        public void ApplyWelcome_UnknownRuntimeFields_DoNotChangeLegacySettingsBehavior()
        {
            // Runtime negotiation will be additive. Characterize the current old-client
            // behavior: unknown fields are ignored while legacy timing fields still apply.
            var client = new WebSocketTransportClient();
            var payload = new JObject
            {
                ["type"] = "welcome",
                ["serverTimeout"] = 30,
                ["keepAliveInterval"] = 17,
                ["runtime"] = new JObject
                {
                    ["version"] = "command-runtime/1",
                    ["mode"] = "runtime_v1"
                }
            };

            InvokeInstanceMethod(client, "ApplyWelcome", payload);

            Assert.AreEqual(TimeSpan.FromSeconds(17), GetInstanceField<TimeSpan>(client, "_keepAliveInterval"));
            Assert.AreEqual(TimeSpan.FromSeconds(17), GetInstanceField<TimeSpan>(client, "_socketKeepAliveInterval"));
        }

        [Test]
        public async Task HandleMessageAsync_UnknownControlMessage_IsIgnored()
        {
            var client = new WebSocketTransportClient();
            const string message = "{\"type\":\"runtime_status\",\"request_id\":\"request-1\"}";

            var task = (Task)InvokeInstanceMethod(
                client,
                "HandleMessageAsync",
                message,
                CancellationToken.None);

            await task;

            Assert.IsFalse(client.IsConnected);
            Assert.IsNull(GetInstanceField<string>(client, "_sessionId"));
        }

        [Test]
        public void BuildRegisterPayload_PreservesLegacyFieldsAndAddsRuntimeAdvertisement()
        {
            var client = new WebSocketTransportClient();
            SetInstanceField(client, "_projectName", "TestProject");
            SetInstanceField(client, "_projectHash", "hash-123");
            SetInstanceField(client, "_unityVersion", "6000.3.9f1");
            SetInstanceField(client, "_projectPath", "D:/Projects/TestProject");
            SetInstanceField(client, "_packageVersion", "10.1.1-beta.1");

            JObject payload = client.BuildRegisterPayload();

            Assert.AreEqual("register", payload.Value<string>("type"));
            Assert.AreEqual("TestProject", payload.Value<string>("project_name"));
            Assert.AreEqual("hash-123", payload.Value<string>("project_hash"));
            Assert.AreEqual("6000.3.9f1", payload.Value<string>("unity_version"));
            Assert.AreEqual("D:/Projects/TestProject", payload.Value<string>("project_path"));

            var runtime = (JObject)payload["runtime"];
            Assert.AreEqual("command-runtime", runtime.Value<string>("protocol"));
            Assert.AreEqual(1, runtime.Value<int>("major"));
            Assert.AreEqual("10.1.1-beta.1", runtime.Value<string>("package_version"));
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
                ((JArray)runtime["capabilities"]).Values<string>().ToArray());
            Assert.AreEqual(1, runtime.Value<int>("contract_version"));
            StringAssert.StartsWith("sha256:", runtime.Value<string>("built_in_schema_hash"));
            var limits = (JObject)runtime["limits"];
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
        public void MessageLimitCheck_IsOverflowSafeAndAllowsExactBoundary()
        {
            Assert.IsFalse(WebSocketTransportClient.WouldExceedMessageLimit(0, 8, 8));
            Assert.IsFalse(WebSocketTransportClient.WouldExceedMessageLimit(7, 1, 8));
            Assert.IsTrue(WebSocketTransportClient.WouldExceedMessageLimit(8, 1, 8));
            Assert.IsTrue(WebSocketTransportClient.WouldExceedMessageLimit(long.MaxValue, 1, 8));
            Assert.IsTrue(WebSocketTransportClient.WouldExceedMessageLimit(0, -1, 8));
        }

        [Test]
        public void RuntimeAdmissionError_IsStructuredAndRetryable()
        {
            JObject result = WebSocketTransportClient.BuildRuntimeErrorResult(
                "QUEUE_FULL",
                "Queue is full.");

            Assert.AreEqual("error", result.Value<string>("status"));
            Assert.IsFalse(result.Value<bool>("success"));
            Assert.AreEqual("QUEUE_FULL", result.Value<string>("code"));
            Assert.AreEqual("Queue is full.", result.Value<string>("error"));
            Assert.AreEqual(100, result["data"]?.Value<int>("retry_after_ms"));
        }

        [Test]
        public void ApplyWelcome_MatchingRuntimeEnablesBoundedControlPath()
        {
            var client = new WebSocketTransportClient();
            SetInstanceField(client, "_packageVersion", "10.1.1-beta.1");
            JObject remoteRuntime = CommandRuntimeProtocol.BuildUnityAdvertisement("server");
            remoteRuntime.Remove("package_version");
            remoteRuntime["server_version"] = "10.1.1-beta.1";

            InvokeInstanceMethod(
                client,
                "ApplyWelcome",
                new JObject { ["runtime"] = remoteRuntime });

            Assert.AreEqual(CommandRuntimeMode.RuntimeV1, client.RuntimeNegotiation.Mode);
            Assert.IsTrue(client.RuntimeNegotiation.HasCapability("bounded_command_queue_v1"));
            Assert.IsTrue(client.RuntimeNegotiation.HasCapability("control_path_v1"));
            Assert.IsTrue(client.RuntimeNegotiation.HasCapability("request_receipts_v1"));
            Assert.IsTrue(client.RuntimeNegotiation.HasCapability("receipt_ledger_admin_v1"));
            Assert.IsTrue(client.RuntimeNegotiation.HasCapability("response_envelope_v1"));
            Assert.IsTrue(client.RuntimeNegotiation.HasCapability("mutation_contracts_v1"));
            Assert.IsTrue(client.RuntimeNegotiation.HasCapability("stable_handles_v1"));
            Assert.IsTrue(client.RuntimeNegotiation.HasCapability("state_revisions_v1"));
            Assert.IsTrue(client.RuntimeNegotiation.HasCapability("batch_semantics_v1"));
            Assert.IsTrue(client.RuntimeNegotiation.HasCapability("reload_lifecycle_v1"));
        }

        [Test]
        public void BuildReloadLifecyclePayload_IsExplicitAndCorrelated()
        {
            JObject payload = WebSocketTransportClient.BuildReloadLifecyclePayload(
                "session-123",
                "assembly_reload");

            Assert.AreEqual("lifecycle", payload.Value<string>("type"));
            Assert.AreEqual("reloading", payload.Value<string>("state"));
            Assert.AreEqual("session-123", payload.Value<string>("session_id"));
            Assert.AreEqual("assembly_reload", payload.Value<string>("reason"));
        }

        private static List<Uri> InvokeBuildConnectionCandidateUris(Uri endpoint)
        {
            if (BuildConnectionCandidateUrisMethod == null)
            {
                Assert.Fail(BuildMissingMethodDiagnostic());
            }
            var result = BuildConnectionCandidateUrisMethod.Invoke(null, new object[] { endpoint });
            Assert.IsNotNull(result);
            Assert.IsInstanceOf<List<Uri>>(result);
            return (List<Uri>)result;
        }

        private static object InvokeInstanceMethod(object instance, string methodName, params object[] arguments)
        {
            Type[] argumentTypes = arguments.Select(argument => argument.GetType()).ToArray();
            MethodInfo method = instance.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: argumentTypes,
                modifiers: null);

            Assert.IsNotNull(method, $"Expected private method '{methodName}' to exist.");
            return method.Invoke(instance, arguments);
        }

        private static T GetInstanceField<T>(object instance, string fieldName)
        {
            FieldInfo field = instance.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(field, $"Expected private field '{fieldName}' to exist.");
            return (T)field.GetValue(instance);
        }

        private static void SetInstanceField<T>(object instance, string fieldName, T value)
        {
            FieldInfo field = instance.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(field, $"Expected private field '{fieldName}' to exist.");
            field.SetValue(instance, value);
        }

        private static MethodInfo ResolveCandidateBuilderMethod()
        {
            MethodInfo direct = GetCandidateBuilderMethod(typeof(WebSocketTransportClient));
            if (direct != null)
            {
                return direct;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidateType = assembly.GetType(WebSocketTransportClientTypeName);
                if (candidateType == null)
                {
                    continue;
                }

                MethodInfo method = GetCandidateBuilderMethod(candidateType);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        private static MethodInfo GetCandidateBuilderMethod(Type type)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            MethodInfo direct = type.GetMethod(
                CandidateBuilderMethodName,
                flags,
                binder: null,
                types: new[] { typeof(Uri) },
                modifiers: null);
            if (direct != null)
            {
                return direct;
            }

            // Fallback for environments where signature binding can differ between loaded copies.
            return type.GetMethods(flags).FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, CandidateBuilderMethodName, StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(Uri);
            });
        }

        private static string BuildMissingMethodDiagnostic()
        {
            var sb = new StringBuilder();
            sb.Append("Expected private candidate builder method to exist. Searched loaded assemblies for ")
              .Append(WebSocketTransportClientTypeName)
              .Append('.')
              .Append(CandidateBuilderMethodName)
              .Append(". Loaded candidate types:");

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidateType = assembly.GetType(WebSocketTransportClientTypeName);
                if (candidateType == null)
                {
                    continue;
                }

                sb.Append("\n- ")
                  .Append(assembly.FullName)
                  .Append(" @ ")
                  .Append(string.IsNullOrEmpty(assembly.Location) ? "<dynamic>" : assembly.Location);
            }

            return sb.ToString();
        }

        private static string NormalizeHostForComparison(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return host;
            }

            return host.Trim('[', ']');
        }
    }
}
