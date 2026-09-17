using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services.Transport
{
    internal enum CommandReceiptState
    {
        Accepted,
        Queued,
        Executing,
        Succeeded,
        Failed,
        Cancelled,
        OutcomeUnknown
    }

    internal sealed class ReceiptAdmission
    {
        internal ReceiptAdmission(bool shouldExecute, string requestId, JToken immediateResult = null)
        {
            ShouldExecute = shouldExecute;
            RequestId = requestId;
            ImmediateResult = immediateResult;
        }

        internal bool ShouldExecute { get; }
        internal string RequestId { get; }
        internal JToken ImmediateResult { get; }
    }

    /// <summary>
    /// Project-local, bounded receipt ledger for retry-safe Runtime v1 commands.
    /// It persists no raw command parameters, only hashes, lifecycle state and a
    /// bounded terminal result.
    /// </summary>
    internal static class CommandReceiptLedger
    {
        internal const int MaxReceiptCount = 512;
        internal const int MaxPersistedBytes = 8 * 1024 * 1024;
        internal const int MaxCachedResultBytes = 256 * 1024;
        internal static readonly TimeSpan ReceiptRetention = TimeSpan.FromMinutes(30);

        private const int FileVersion = 1;
        private static readonly object Sync = new();
        private static readonly Dictionary<string, ReceiptRecord> Receipts =
            new(StringComparer.Ordinal);
        private static readonly UTF8Encoding Utf8NoBom = new(false);
        private static readonly string[] WireStates =
        {
            "accepted",
            "queued",
            "executing",
            "succeeded",
            "failed",
            "cancelled",
            "outcome_unknown"
        };

        private static string ledgerPath;
        private static bool initialized;
        private static long? utcNowOverrideMs;

        private sealed class ReceiptFile
        {
            [JsonProperty("version")]
            public int Version { get; set; } = FileVersion;

            [JsonProperty("receipts")]
            public List<ReceiptRecord> Receipts { get; set; } = new();
        }

        private sealed class ReceiptRecord
        {
            [JsonProperty("request_id")]
            public string RequestId { get; set; }

            [JsonProperty("payload_hash")]
            public string PayloadHash { get; set; }

            [JsonProperty("state")]
            public string State { get; set; }

            [JsonProperty("created_unix_ms")]
            public long CreatedUnixMs { get; set; }

            [JsonProperty("updated_unix_ms")]
            public long UpdatedUnixMs { get; set; }

            [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)]
            public JToken Result { get; set; }

            [JsonProperty("result_hash", NullValueHandling = NullValueHandling.Ignore)]
            public string ResultHash { get; set; }

            [JsonProperty("result_evicted")]
            public bool ResultEvicted { get; set; }

            [JsonProperty("cancellation_requested")]
            public bool CancellationRequested { get; set; }
        }

        internal static void Initialize(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                throw new ArgumentException("Project root is required for the receipt ledger.", nameof(projectRoot));
            }

            string path = Path.GetFullPath(Path.Combine(
                projectRoot,
                "Library",
                "MCPForUnity",
                "RunState",
                "command-receipts-v1.json"));

            lock (Sync)
            {
                if (initialized
                    && string.Equals(ledgerPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                ledgerPath = path;
                Receipts.Clear();
                initialized = true;
                LoadLocked();
            }
        }

        internal static ReceiptAdmission TryAdmit(
            JObject runtime,
            string commandName,
            JObject parameters,
            string projectHash)
        {
            string requestId = runtime?.Value<string>("request_id");
            string payloadHash = runtime?.Value<string>("payload_hash");
            int attempt = runtime?.Value<int?>("attempt") ?? 0;
            int version = runtime?.Value<int?>("version") ?? 0;
            long deadlineUnixMs = runtime?.Value<long?>("deadline_unix_ms") ?? 0L;

            if (version != 1
                || !Guid.TryParse(requestId, out _)
                || attempt < 1
                || string.IsNullOrWhiteSpace(commandName)
                || string.IsNullOrWhiteSpace(projectHash)
                || !IsSha256(payloadHash))
            {
                return Reject(
                    requestId,
                    "INVALID_RUNTIME_METADATA",
                    "Runtime command metadata is missing or invalid.");
            }

            if (deadlineUnixMs > 0 && deadlineUnixMs < UtcNowMs())
            {
                return Reject(requestId, "DEADLINE_EXCEEDED", "Command deadline has already expired.");
            }

            lock (Sync)
            {
                EnsureInitializedLocked();
                PruneLocked();

                if (Receipts.TryGetValue(requestId, out ReceiptRecord existing))
                {
                    if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                    {
                        return Reject(
                            requestId,
                            "REQUEST_ID_CONFLICT",
                            "The request_id already exists with a different payload hash.");
                    }

                    if (existing.Result != null)
                    {
                        return new ReceiptAdmission(
                            shouldExecute: false,
                            requestId,
                            existing.Result.DeepClone());
                    }

                    return new ReceiptAdmission(
                        shouldExecute: false,
                        requestId,
                        BuildStateResult(existing));
                }

                if (attempt > 1)
                {
                    return Reject(
                        requestId,
                        "RECEIPT_NOT_AVAILABLE",
                        "No retained receipt exists for this retry; execution outcome is unknown.");
                }

                if (Receipts.Count >= MaxReceiptCount)
                {
                    return Reject(
                        requestId,
                        "RECEIPT_LEDGER_FULL",
                        "The bounded receipt ledger has no evictable terminal entry.");
                }

                long now = UtcNowMs();
                var receipt = new ReceiptRecord
                {
                    RequestId = requestId,
                    PayloadHash = payloadHash,
                    State = ToWireState(CommandReceiptState.Accepted),
                    CreatedUnixMs = now,
                    UpdatedUnixMs = now
                };
                Receipts.Add(requestId, receipt);

                try
                {
                    SaveLocked();
                }
                catch (Exception ex)
                {
                    Receipts.Remove(requestId);
                    McpLog.Error($"[CommandRuntime] Could not persist accepted receipt: {ex.Message}");
                    return Reject(
                        requestId,
                        "RECEIPT_PERSIST_FAILED",
                        "The command was not executed because its receipt could not be persisted.");
                }

                return new ReceiptAdmission(shouldExecute: true, requestId);
            }
        }

        internal static void MarkQueued(string requestId)
        {
            Transition(requestId, CommandReceiptState.Queued, persistRequired: true);
        }

        internal static void MarkExecuting(string requestId)
        {
            Transition(requestId, CommandReceiptState.Executing, persistRequired: true);
        }

        internal static void Complete(string requestId, JToken result)
        {
            if (string.IsNullOrEmpty(requestId) || result == null)
            {
                return;
            }

            lock (Sync)
            {
                EnsureInitializedLocked();
                if (!Receipts.TryGetValue(requestId, out ReceiptRecord receipt)
                    || IsTerminal(receipt.State))
                {
                    return;
                }

                receipt.State = ToWireState(IsSuccessful(result)
                    ? CommandReceiptState.Succeeded
                    : CommandReceiptState.Failed);
                receipt.UpdatedUnixMs = UtcNowMs();
                receipt.ResultHash = ComputeTokenHash(result);

                int resultBytes = Encoding.UTF8.GetByteCount(result.ToString(Formatting.None));
                if (resultBytes <= MaxCachedResultBytes)
                {
                    receipt.Result = result.DeepClone();
                    receipt.ResultEvicted = false;
                }
                else
                {
                    receipt.Result = null;
                    receipt.ResultEvicted = true;
                }

                SaveBestEffortLocked("terminal receipt");
            }
        }

        internal static bool RequestCancellation(string requestId)
        {
            lock (Sync)
            {
                EnsureInitializedLocked();
                if (!Receipts.TryGetValue(requestId, out ReceiptRecord receipt)
                    || IsTerminal(receipt.State))
                {
                    return false;
                }

                receipt.CancellationRequested = true;
                receipt.UpdatedUnixMs = UtcNowMs();
                SaveBestEffortLocked("cancellation request");
                return true;
            }
        }

        internal static void MarkCancelled(
            string requestId,
            string code,
            string message,
            JToken terminalResult = null)
        {
            lock (Sync)
            {
                EnsureInitializedLocked();
                if (!Receipts.TryGetValue(requestId, out ReceiptRecord receipt)
                    || string.Equals(receipt.State, "executing", StringComparison.Ordinal)
                    || IsTerminal(receipt.State))
                {
                    return;
                }

                JToken result = terminalResult?.DeepClone() ?? BuildError(code, message);
                receipt.State = ToWireState(CommandReceiptState.Cancelled);
                receipt.UpdatedUnixMs = UtcNowMs();
                receipt.Result = result;
                receipt.ResultHash = ComputeTokenHash(result);
                receipt.ResultEvicted = false;
                SaveBestEffortLocked("cancelled receipt");
            }
        }

        internal static JObject Query(string requestId)
        {
            lock (Sync)
            {
                EnsureInitializedLocked();
                return Receipts.TryGetValue(requestId ?? string.Empty, out ReceiptRecord receipt)
                    ? BuildReceiptSnapshot(receipt)
                    : new JObject
                    {
                        ["request_id"] = requestId,
                        ["state"] = "not_found",
                        ["code"] = "RECEIPT_NOT_AVAILABLE"
                    };
            }
        }

        internal static JObject Diagnose()
        {
            lock (Sync)
            {
                EnsureInitializedLocked();
                return BuildLedgerSummaryLocked(UtcNowMs());
            }
        }

        internal static JObject Cleanup(
            string scope = "expired_terminal",
            bool confirmOutcomeUnknown = false)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                scope = "expired_terminal";
            }

            if (scope is not "expired_terminal" and not "all_terminal")
            {
                return BuildError(
                    "INVALID_RECEIPT_CLEANUP_SCOPE",
                    "Receipt cleanup scope must be 'expired_terminal' or 'all_terminal'.");
            }

            lock (Sync)
            {
                EnsureInitializedLocked();
                long now = UtcNowMs();
                JObject before = BuildLedgerSummaryLocked(now);
                long cutoff = now - (long)ReceiptRetention.TotalMilliseconds;

                ReceiptRecord[] selected = Receipts.Values
                    .Where(receipt => IsCleanupCandidate(
                        receipt,
                        scope,
                        cutoff,
                        confirmOutcomeUnknown))
                    .ToArray();
                var selectedIds = new HashSet<string>(
                    selected.Select(receipt => receipt.RequestId),
                    StringComparer.Ordinal);
                ReceiptRecord[] retained = Receipts.Values
                    .Where(receipt => !selectedIds.Contains(receipt.RequestId))
                    .ToArray();

                try
                {
                    PersistSnapshotLocked(retained);
                }
                catch (Exception ex)
                {
                    McpLog.Error($"[CommandRuntime] Could not persist receipt cleanup: {ex.Message}");
                    JObject failure = BuildError(
                        "RECEIPT_CLEANUP_PERSIST_FAILED",
                        "No receipts were removed because the cleaned ledger could not be persisted.");
                    failure["scope"] = scope;
                    failure["before"] = before;
                    failure["after"] = BuildLedgerSummaryLocked(now);
                    return failure;
                }

                foreach (ReceiptRecord receipt in selected)
                {
                    Receipts.Remove(receipt.RequestId);
                }

                int preservedOutcomeUnknown = Receipts.Values.Count(receipt =>
                    string.Equals(receipt.State, "outcome_unknown", StringComparison.Ordinal)
                    && (scope == "all_terminal" || receipt.UpdatedUnixMs < cutoff));
                return new JObject
                {
                    ["status"] = "success",
                    ["success"] = true,
                    ["action"] = "cleanup",
                    ["scope"] = scope,
                    ["outcome_unknown_confirmed"] = confirmOutcomeUnknown,
                    ["removed_total"] = selected.Length,
                    ["removed_counts"] = BuildStatusCounts(selected),
                    ["preserved_outcome_unknown"] = preservedOutcomeUnknown,
                    ["before"] = before,
                    ["after"] = BuildLedgerSummaryLocked(now)
                };
            }
        }

        internal static string ComputeTokenHash(JToken token)
        {
            string canonical = Canonicalize(token).ToString(Formatting.None);
            using var sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            return "sha256:" + string.Concat(digest.Select(value => value.ToString("x2")));
        }

        internal static void ResetForTests(string projectRoot = null)
        {
            lock (Sync)
            {
                Receipts.Clear();
                ledgerPath = null;
                initialized = false;
                utcNowOverrideMs = null;
            }

            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                Initialize(projectRoot);
            }
        }

        internal static void SetUtcNowForTests(long? unixMs)
        {
            lock (Sync)
            {
                utcNowOverrideMs = unixMs;
            }
        }

        private static void Transition(
            string requestId,
            CommandReceiptState state,
            bool persistRequired)
        {
            lock (Sync)
            {
                EnsureInitializedLocked();
                if (!Receipts.TryGetValue(requestId ?? string.Empty, out ReceiptRecord receipt)
                    || IsTerminal(receipt.State))
                {
                    throw new InvalidOperationException($"Receipt '{requestId}' is unavailable or terminal.");
                }

                receipt.State = ToWireState(state);
                receipt.UpdatedUnixMs = UtcNowMs();
                if (persistRequired)
                {
                    SaveLocked();
                }
            }
        }

        private static ReceiptAdmission Reject(string requestId, string code, string message)
        {
            return new ReceiptAdmission(
                shouldExecute: false,
                requestId,
                BuildError(code, message));
        }

        private static JObject BuildError(string code, string message)
        {
            return new JObject
            {
                ["status"] = "error",
                ["success"] = false,
                ["code"] = code,
                ["error"] = message
            };
        }

        private static JObject BuildStateResult(ReceiptRecord receipt)
        {
            string code = receipt.ResultEvicted
                ? "RESULT_EVICTED"
                : string.Equals(receipt.State, "outcome_unknown", StringComparison.Ordinal)
                    ? "OUTCOME_UNKNOWN"
                    : IsTerminal(receipt.State)
                        ? "TERMINAL_RECEIPT"
                        : "REQUEST_IN_PROGRESS";
            var result = BuildError(code, $"Request is {receipt.State}.");
            result["receipt"] = BuildReceiptSnapshot(receipt);
            if (!IsTerminal(receipt.State))
            {
                result["hint"] = "poll_receipt";
                result["data"] = new JObject { ["retry_after_ms"] = 100 };
            }
            return result;
        }

        private static JObject BuildReceiptSnapshot(ReceiptRecord receipt)
        {
            return new JObject
            {
                ["request_id"] = receipt.RequestId,
                ["payload_hash"] = receipt.PayloadHash,
                ["state"] = receipt.State,
                ["created_unix_ms"] = receipt.CreatedUnixMs,
                ["updated_unix_ms"] = receipt.UpdatedUnixMs,
                ["result_hash"] = receipt.ResultHash,
                ["result_evicted"] = receipt.ResultEvicted,
                ["cancellation_requested"] = receipt.CancellationRequested
            };
        }

        private static JObject BuildLedgerSummaryLocked(long now)
        {
            JObject counts = BuildStatusCounts(Receipts.Values);
            long cutoff = now - (long)ReceiptRetention.TotalMilliseconds;
            int active = Receipts.Values.Count(receipt => IsActive(receipt.State));
            int terminal = Receipts.Values.Count(receipt => IsTerminal(receipt.State));
            int expiredTerminal = Receipts.Values.Count(receipt =>
                IsTerminal(receipt.State) && receipt.UpdatedUnixMs < cutoff);
            int outcomeUnknown = counts.Value<int>("outcome_unknown");
            int expiredOutcomeUnknown = Receipts.Values.Count(receipt =>
                string.Equals(receipt.State, "outcome_unknown", StringComparison.Ordinal)
                && receipt.UpdatedUnixMs < cutoff);

            return new JObject
            {
                ["total"] = Receipts.Count,
                ["counts"] = counts,
                ["active"] = active,
                ["terminal"] = terminal,
                ["expired_terminal"] = expiredTerminal,
                ["outcome_unknown"] = new JObject
                {
                    ["total"] = outcomeUnknown,
                    ["expired"] = expiredOutcomeUnknown,
                    ["requires_explicit_confirmation"] = outcomeUnknown > 0
                },
                ["retention_seconds"] = (int)ReceiptRetention.TotalSeconds,
                ["max_receipts"] = MaxReceiptCount,
                ["ledger_path"] = ledgerPath
            };
        }

        private static JObject BuildStatusCounts(IEnumerable<ReceiptRecord> receipts)
        {
            ReceiptRecord[] snapshot = receipts.ToArray();
            var counts = new JObject();
            foreach (string state in WireStates)
            {
                counts[state] = snapshot.Count(receipt =>
                    string.Equals(receipt.State, state, StringComparison.Ordinal));
            }
            return counts;
        }

        private static bool IsSuccessful(JToken result)
        {
            bool? success = result.Value<bool?>("success");
            if (success.HasValue)
            {
                return success.Value;
            }
            string status = result.Value<string>("status");
            return string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTerminal(string state)
        {
            return state is "succeeded" or "failed" or "cancelled" or "outcome_unknown";
        }

        private static bool IsKnownTerminal(string state)
        {
            return state is "succeeded" or "failed" or "cancelled";
        }

        private static bool IsActive(string state)
        {
            return state is "accepted" or "queued" or "executing";
        }

        private static bool IsCleanupCandidate(
            ReceiptRecord receipt,
            string scope,
            long cutoff,
            bool confirmOutcomeUnknown)
        {
            if (!IsTerminal(receipt.State))
            {
                return false;
            }

            if (string.Equals(receipt.State, "outcome_unknown", StringComparison.Ordinal)
                && !confirmOutcomeUnknown)
            {
                return false;
            }

            return scope == "all_terminal" || receipt.UpdatedUnixMs < cutoff;
        }

        private static string ToWireState(CommandReceiptState state)
        {
            return state switch
            {
                CommandReceiptState.Accepted => "accepted",
                CommandReceiptState.Queued => "queued",
                CommandReceiptState.Executing => "executing",
                CommandReceiptState.Succeeded => "succeeded",
                CommandReceiptState.Failed => "failed",
                CommandReceiptState.Cancelled => "cancelled",
                CommandReceiptState.OutcomeUnknown => "outcome_unknown",
                _ => "outcome_unknown"
            };
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
            {
                return false;
            }
            return value.Skip(7).All(Uri.IsHexDigit);
        }

        private static JToken Canonicalize(JToken token)
        {
            return token switch
            {
                JObject obj => new JObject(obj.Properties()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => new JProperty(property.Name, Canonicalize(property.Value)))),
                JArray array => new JArray(array.Select(Canonicalize)),
                _ => token.DeepClone()
            };
        }

        private static long UtcNowMs()
        {
            return utcNowOverrideMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static void EnsureInitializedLocked()
        {
            if (!initialized || string.IsNullOrWhiteSpace(ledgerPath))
            {
                throw new InvalidOperationException("Command receipt ledger has not been initialized.");
            }
        }

        private static void LoadLocked()
        {
            bool changed = false;
            if (File.Exists(ledgerPath))
            {
                try
                {
                    var file = JsonConvert.DeserializeObject<ReceiptFile>(
                        File.ReadAllText(ledgerPath, Encoding.UTF8));
                    if (file?.Version == FileVersion)
                    {
                        foreach (ReceiptRecord receipt in file.Receipts ?? new List<ReceiptRecord>())
                        {
                            if (!string.IsNullOrWhiteSpace(receipt.RequestId)
                                && !string.IsNullOrWhiteSpace(receipt.PayloadHash))
                            {
                                Receipts[receipt.RequestId] = receipt;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[CommandRuntime] Ignoring unreadable receipt ledger: {ex.Message}");
                }
            }

            foreach (ReceiptRecord receipt in Receipts.Values)
            {
                if (receipt.State is "accepted" or "queued")
                {
                    receipt.State = ToWireState(CommandReceiptState.Cancelled);
                    receipt.Result = BuildError(
                        "RELOAD_BEFORE_EXECUTION",
                        "Unity reloaded before the command entered execution.");
                    receipt.ResultHash = ComputeTokenHash(receipt.Result);
                    receipt.UpdatedUnixMs = UtcNowMs();
                    changed = true;
                }
                else if (string.Equals(receipt.State, "executing", StringComparison.Ordinal))
                {
                    receipt.State = ToWireState(CommandReceiptState.OutcomeUnknown);
                    receipt.Result = null;
                    receipt.ResultEvicted = false;
                    receipt.UpdatedUnixMs = UtcNowMs();
                    changed = true;
                }
            }

            changed |= PruneLocked();
            if (changed)
            {
                SaveBestEffortLocked("reload recovery");
            }
        }

        private static bool PruneLocked()
        {
            bool changed = false;
            long cutoff = UtcNowMs() - (long)ReceiptRetention.TotalMilliseconds;
            foreach (string requestId in Receipts.Values
                         .Where(receipt => receipt.UpdatedUnixMs < cutoff && IsKnownTerminal(receipt.State))
                         .Select(receipt => receipt.RequestId)
                         .ToArray())
            {
                Receipts.Remove(requestId);
                changed = true;
            }

            while (Receipts.Count > MaxReceiptCount)
            {
                ReceiptRecord oldest = Receipts.Values
                    .Where(receipt => IsKnownTerminal(receipt.State))
                    .OrderBy(receipt => receipt.UpdatedUnixMs)
                    .FirstOrDefault();
                if (oldest == null)
                {
                    break;
                }
                Receipts.Remove(oldest.RequestId);
                changed = true;
            }

            return changed;
        }

        private static void SaveBestEffortLocked(string operation)
        {
            try
            {
                SaveLocked();
            }
            catch (Exception ex)
            {
                McpLog.Error($"[CommandRuntime] Could not persist {operation}: {ex.Message}");
            }
        }

        private static void SaveLocked()
        {
            EnsureInitializedLocked();
            PruneLocked();

            string json = SerializeLocked();
            while (Encoding.UTF8.GetByteCount(json) > MaxPersistedBytes)
            {
                ReceiptRecord withResult = Receipts.Values
                    .Where(receipt => receipt.Result != null && IsKnownTerminal(receipt.State))
                    .OrderBy(receipt => receipt.UpdatedUnixMs)
                    .FirstOrDefault();
                if (withResult != null)
                {
                    withResult.Result = null;
                    withResult.ResultEvicted = true;
                }
                else
                {
                    ReceiptRecord removable = Receipts.Values
                        .Where(receipt => IsKnownTerminal(receipt.State))
                        .OrderBy(receipt => receipt.UpdatedUnixMs)
                        .FirstOrDefault();
                    if (removable == null)
                    {
                        throw new IOException("Active receipt metadata exceeds the persisted byte limit.");
                    }
                    Receipts.Remove(removable.RequestId);
                }
                json = SerializeLocked();
            }

            PersistSnapshotLocked(Receipts.Values, json);
        }

        private static void PersistSnapshotLocked(
            IEnumerable<ReceiptRecord> receipts,
            string serialized = null)
        {
            ReceiptRecord[] snapshot = receipts.ToArray();
            string json = serialized ?? Serialize(snapshot);
            if (Encoding.UTF8.GetByteCount(json) > MaxPersistedBytes)
            {
                throw new IOException("Receipt ledger exceeds the persisted byte limit.");
            }

            string directory = Path.GetDirectoryName(ledgerPath);
            Directory.CreateDirectory(directory);
            string temporaryPath = ledgerPath + ".tmp";
            File.WriteAllText(temporaryPath, json, Utf8NoBom);

            if (File.Exists(ledgerPath))
            {
                try
                {
                    File.Replace(temporaryPath, ledgerPath, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(temporaryPath, ledgerPath, overwrite: true);
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (Exception ex)
                    {
                        McpLog.Warn($"[CommandRuntime] Could not remove replaced receipt ledger temporary file: {ex.Message}");
                    }
                }
            }
            else
            {
                File.Move(temporaryPath, ledgerPath);
            }
        }

        private static string SerializeLocked()
        {
            return Serialize(Receipts.Values);
        }

        private static string Serialize(IEnumerable<ReceiptRecord> receipts)
        {
            return JsonConvert.SerializeObject(
                new ReceiptFile
                {
                    Receipts = receipts
                        .OrderBy(receipt => receipt.CreatedUnixMs)
                        .ToList()
                },
                Formatting.None);
        }
    }
}
