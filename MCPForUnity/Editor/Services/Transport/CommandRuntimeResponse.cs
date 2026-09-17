using System;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services.Transport
{
    /// <summary>
    /// Converts today's heterogeneous Unity command results into the negotiated
    /// Runtime v1 wire envelope without changing legacy connections.
    /// </summary>
    internal static class CommandRuntimeResponse
    {
        internal const int MaxMessageCharacters = 32 * 1024;

        internal static JObject Adapt(
            JToken legacyResult,
            string requestId,
            long queuedMilliseconds,
            long executionMilliseconds,
            JObject beforeState = null,
            JObject afterState = null)
        {
            legacyResult ??= new JObject();
            bool success = IsSuccessful(legacyResult);
            string runtimeStatus = ExtractRuntimeStatus(legacyResult, success);
            string message = ExtractMessage(legacyResult, success);
            bool messageTruncated = message?.Length > MaxMessageCharacters;
            if (messageTruncated)
            {
                message = message.Substring(0, MaxMessageCharacters);
            }

            JObject changes = ExtractChanges(legacyResult);
            JToken data = ExtractData(legacyResult);
            var diagnostics = new JArray();
            if (messageTruncated)
            {
                diagnostics.Add(new JObject
                {
                    ["code"] = "MESSAGE_TRUNCATED",
                    ["message"] = "Diagnostic message exceeded the Runtime v1 limit."
                });
            }

            return new JObject
            {
                ["runtime_version"] = 1,
                ["request_id"] = requestId,
                ["status"] = runtimeStatus,
                ["code"] = ExtractCode(legacyResult, success),
                ["message"] = message ?? (success ? "Command succeeded." : "Unity command failed."),
                ["data"] = data,
                ["changes"] = changes,
                ["diagnostics"] = diagnostics,
                ["timing"] = new JObject
                {
                    ["queued_ms"] = Math.Max(0L, queuedMilliseconds),
                    ["execution_ms"] = Math.Max(0L, executionMilliseconds),
                    ["total_ms"] = Math.Max(0L, queuedMilliseconds + executionMilliseconds)
                },
                ["state"] = CommandRuntimeState.BuildResponseState(beforeState, afterState),
                ["receipt"] = ExtractReceipt(legacyResult)
            };
        }

        internal static JObject Failure(
            string requestId,
            string code,
            string message,
            long queuedMilliseconds = 0L,
            long executionMilliseconds = 0L)
        {
            return Adapt(
                new JObject
                {
                    ["status"] = "error",
                    ["success"] = false,
                    ["code"] = code,
                    ["error"] = message
                },
                requestId,
                queuedMilliseconds,
                executionMilliseconds);
        }

        internal static bool IsSuccessful(JToken result)
        {
            bool? explicitSuccess = result.Value<bool?>("success");
            if (explicitSuccess.HasValue)
            {
                return explicitSuccess.Value;
            }

            if (result["result"] is JObject nested)
            {
                bool? nestedSuccess = nested.Value<bool?>("success");
                if (nestedSuccess.HasValue)
                {
                    return nestedSuccess.Value;
                }
            }

            string status = result.Value<string>("status");
            return string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractCode(JToken result, bool success)
        {
            string code = result.Value<string>("code")
                ?? (result["result"] as JObject)?.Value<string>("code");
            return string.IsNullOrWhiteSpace(code)
                ? success ? "OK" : "UNITY_COMMAND_FAILED"
                : code;
        }

        private static string ExtractMessage(JToken result, bool success)
        {
            JToken nested = result["result"];
            return result.Value<string>(success ? "message" : "error")
                ?? result.Value<string>("message")
                ?? (nested as JObject)?.Value<string>(success ? "message" : "error")
                ?? (nested as JObject)?.Value<string>("message");
        }

        private static JToken ExtractData(JToken result)
        {
            JToken data = result["data"] ?? result["result"];
            if (data == null)
            {
                if (result is JObject obj)
                {
                    var copy = (JObject)obj.DeepClone();
                    foreach (string field in new[]
                    {
                        "status", "success", "code", "message", "error", "changes",
                        "stackTrace", "stack_trace", "traceback", "receivedText"
                    })
                    {
                        copy.Remove(field);
                    }
                    data = copy;
                }
                else
                {
                    data = result.DeepClone();
                }
            }
            else
            {
                data = data.DeepClone();
            }

            return data is JObject ? data : new JObject { ["result"] = data };
        }

        private static JObject ExtractChanges(JToken result)
        {
            if (result["changes"] is JObject changes)
            {
                return (JObject)changes.DeepClone();
            }

            if (result["result"] is JObject nested
                && nested["changes"] is JObject nestedChanges)
            {
                return (JObject)nestedChanges.DeepClone();
            }

            return new JObject
            {
                ["objects"] = new JArray(),
                ["assets"] = new JArray(),
                ["scenes"] = new JArray()
            };
        }

        private static string ExtractRuntimeStatus(JToken result, bool success)
        {
            string receiptState = result["receipt"]?.Value<string>("state");
            return receiptState is "accepted" or "queued" or "executing" or "cancelled" or "outcome_unknown"
                ? receiptState
                : success ? "succeeded" : "failed";
        }

        private static JObject ExtractReceipt(JToken result)
        {
            JObject receipt = result["receipt"] is JObject existing
                ? (JObject)existing.DeepClone()
                : new JObject();
            receipt["retained_until_unix_ms"] ??= DateTimeOffset.UtcNow
                .Add(CommandReceiptLedger.ReceiptRetention)
                .ToUnixTimeMilliseconds();
            receipt["result_digest"] ??= receipt.Value<string>("result_hash")
                ?? CommandReceiptLedger.ComputeTokenHash(result);
            return receipt;
        }
    }
}
