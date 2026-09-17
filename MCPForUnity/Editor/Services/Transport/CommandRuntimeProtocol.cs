using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services.Transport
{
    internal enum CommandRuntimeMode
    {
        Legacy,
        RuntimeV1,
        Degraded,
        Incompatible
    }

    internal sealed class CommandRuntimeNegotiation
    {
        internal CommandRuntimeNegotiation(
            CommandRuntimeMode mode,
            string protocol = null,
            int? major = null,
            int? minor = null,
            IReadOnlyList<string> capabilities = null,
            IReadOnlyList<string> diagnostics = null)
        {
            Mode = mode;
            Protocol = protocol;
            Major = major;
            Minor = minor;
            Capabilities = capabilities ?? Array.Empty<string>();
            Diagnostics = diagnostics ?? Array.Empty<string>();
        }

        internal CommandRuntimeMode Mode { get; }
        internal string Protocol { get; }
        internal int? Major { get; }
        internal int? Minor { get; }
        internal IReadOnlyList<string> Capabilities { get; }
        internal IReadOnlyList<string> Diagnostics { get; }

        internal bool HasCapability(string capability)
        {
            return Capabilities.Contains(capability, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Builds and negotiates the additive Command Runtime handshake. This phase
    /// advertises negotiation support only; later phases add capabilities as their
    /// behavior and tests land.
    /// </summary>
    internal static class CommandRuntimeProtocol
    {
        internal const string ProtocolName = "command-runtime";
        internal const int MajorVersion = 1;
        internal const int MinorVersion = 0;
        internal const string NegotiationCapability = "capability_negotiation_v1";
        internal const string ContractManifestCapability = "contract_manifest_v1";
        internal const string BoundedCommandQueueCapability = "bounded_command_queue_v1";
        internal const string ControlPathCapability = "control_path_v1";
        internal const string RequestReceiptsCapability = "request_receipts_v1";
        internal const string ReceiptLedgerAdminCapability = "receipt_ledger_admin_v1";
        internal const string ResponseEnvelopeCapability = "response_envelope_v1";
        internal const string MutationContractsCapability = "mutation_contracts_v1";
        internal const string StableHandlesCapability = "stable_handles_v1";
        internal const string StateRevisionsCapability = "state_revisions_v1";
        internal const string BatchSemanticsCapability = "batch_semantics_v1";
        internal const string ReloadLifecycleCapability = "reload_lifecycle_v1";
        internal const int MaxCommandMessageBytes = 8 * 1024 * 1024;
        internal const int MaxResponseMessageBytes = 16 * 1024 * 1024;
        internal const int MaxQueuedCommands = 64;
        internal const int MaxQueuedPayloadBytes = 16 * 1024 * 1024;
        internal const int MaxControlMessageBytes = 64 * 1024;

        internal static JObject BuildUnityAdvertisement(string packageVersion)
        {
            return new JObject
            {
                ["protocol"] = ProtocolName,
                ["major"] = MajorVersion,
                ["minor"] = MinorVersion,
                ["package_version"] = string.IsNullOrWhiteSpace(packageVersion) ? "unknown" : packageVersion,
                ["contract_version"] = CommandRuntimeContract.ContractVersion,
                ["built_in_schema_hash"] = CommandRuntimeContract.BuiltInSchemaHash,
                ["capabilities"] = new JArray(
                    NegotiationCapability,
                    ContractManifestCapability,
                    BoundedCommandQueueCapability,
                    ControlPathCapability,
                    RequestReceiptsCapability,
                    ReceiptLedgerAdminCapability,
                    ResponseEnvelopeCapability,
                    MutationContractsCapability,
                    StableHandlesCapability,
                    StateRevisionsCapability,
                    BatchSemanticsCapability,
                    ReloadLifecycleCapability),
                ["limits"] = new JObject
                {
                    ["max_command_message_bytes"] = MaxCommandMessageBytes,
                    ["max_response_message_bytes"] = MaxResponseMessageBytes,
                    ["max_queued_commands"] = MaxQueuedCommands,
                    ["max_queued_payload_bytes"] = MaxQueuedPayloadBytes,
                    ["max_control_message_bytes"] = MaxControlMessageBytes,
                    ["max_receipts"] = CommandReceiptLedger.MaxReceiptCount,
                    ["receipt_retention_seconds"] = (int)CommandReceiptLedger.ReceiptRetention.TotalSeconds,
                    ["max_persisted_receipt_bytes"] = CommandReceiptLedger.MaxPersistedBytes,
                    ["max_cached_result_bytes"] = CommandReceiptLedger.MaxCachedResultBytes
                },
                ["legacy_fallback"] = true
            };
        }

        internal static CommandRuntimeNegotiation Negotiate(JObject local, JObject remote)
        {
            if (local == null || remote == null)
            {
                return new CommandRuntimeNegotiation(
                    CommandRuntimeMode.Legacy,
                    diagnostics: new[] { "runtime advertisement missing on one side" });
            }

            string localProtocol = local.Value<string>("protocol");
            string remoteProtocol = remote.Value<string>("protocol");
            if (!string.Equals(localProtocol, remoteProtocol, StringComparison.Ordinal))
            {
                return FallbackOrIncompatible(
                    local,
                    remote,
                    "runtime protocol mismatch",
                    localProtocol);
            }

            int? localMajor = local.Value<int?>("major");
            int? remoteMajor = remote.Value<int?>("major");
            if (!localMajor.HasValue || !remoteMajor.HasValue || localMajor.Value != remoteMajor.Value)
            {
                return FallbackOrIncompatible(
                    local,
                    remote,
                    "runtime major version mismatch",
                    localProtocol);
            }

            int localMinor = local.Value<int?>("minor") ?? 0;
            int remoteMinor = remote.Value<int?>("minor") ?? 0;
            List<string> capabilities = GetCapabilities(local)
                .Intersect(GetCapabilities(remote), StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var diagnostics = new List<string>();
            CommandRuntimeMode mode = CommandRuntimeMode.RuntimeV1;

            if (capabilities.Count == 0)
            {
                mode = CommandRuntimeMode.Degraded;
                diagnostics.Add("no runtime capabilities were negotiated");
            }

            string localHash = local.Value<string>("built_in_schema_hash");
            string remoteHash = remote.Value<string>("built_in_schema_hash");
            if (!string.IsNullOrEmpty(localHash)
                && !string.IsNullOrEmpty(remoteHash)
                && !string.Equals(localHash, remoteHash, StringComparison.Ordinal))
            {
                mode = CommandRuntimeMode.Degraded;
                diagnostics.Add("built-in tool schema hash mismatch");
            }

            return new CommandRuntimeNegotiation(
                mode,
                localProtocol,
                localMajor,
                Math.Min(localMinor, remoteMinor),
                capabilities,
                diagnostics);
        }

        private static CommandRuntimeNegotiation FallbackOrIncompatible(
            JObject local,
            JObject remote,
            string diagnostic,
            string protocol)
        {
            bool fallback = (local.Value<bool?>("legacy_fallback") ?? true)
                || (remote.Value<bool?>("legacy_fallback") ?? true);
            return new CommandRuntimeNegotiation(
                fallback ? CommandRuntimeMode.Legacy : CommandRuntimeMode.Incompatible,
                protocol,
                diagnostics: new[]
                {
                    fallback
                        ? diagnostic + "; using legacy fallback"
                        : diagnostic + " without legacy fallback"
                });
        }

        private static IEnumerable<string> GetCapabilities(JObject advertisement)
        {
            return (advertisement["capabilities"] as JArray)?
                .Values<string>()
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                ?? Enumerable.Empty<string>();
        }
    }
}
