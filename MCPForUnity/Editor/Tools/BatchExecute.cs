using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Executes a preflighted sequence of Unity commands on the main thread. Runtime
    /// v1 can request a dry run or a truthful Unity Undo group; unsupported rollback
    /// claims are rejected before the first child command starts.
    /// </summary>
    [McpForUnityTool("batch_execute", AutoRegister = false)]
    public static class BatchExecute
    {
        internal const int DefaultMaxCommandsPerBatch = 25;
        internal const int AbsoluteMaxCommandsPerBatch = 100;

        private sealed class PreflightCommand
        {
            internal int Index { get; set; }
            internal string ToolName { get; set; }
            internal JObject Parameters { get; set; }
            internal string ContractToolName { get; set; }
            internal CommandRuntimeToolPolicy Policy { get; set; }
        }

        internal static int GetMaxCommandsPerBatch()
        {
            int configured = EditorPrefs.GetInt(
                EditorPrefKeys.BatchExecuteMaxCommands,
                DefaultMaxCommandsPerBatch);
            return Math.Clamp(configured, 1, AbsoluteMaxCommandsPerBatch);
        }

        public static async Task<object> HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("'commands' payload is required.");
            }

            var commandsToken = @params["commands"] as JArray;
            if (commandsToken == null || commandsToken.Count == 0)
            {
                return new ErrorResponse("Provide at least one command entry in 'commands'.");
            }

            int maxCommands = GetMaxCommandsPerBatch();
            if (commandsToken.Count > maxCommands)
            {
                return new ErrorResponse(
                    $"A maximum of {maxCommands} commands are allowed per batch (configurable in MCP Tools window, hard max {AbsoluteMaxCommandsPerBatch}).");
            }

            bool failFast = @params.Value<bool?>("failFast")
                ?? @params.Value<bool?>("fail_fast")
                ?? false;
            bool parallelRequested = @params.Value<bool?>("parallel") ?? false;
            int? maxParallel = @params.Value<int?>("maxParallelism")
                ?? @params.Value<int?>("max_parallelism");
            bool dryRun = @params.Value<bool?>("dryRun")
                ?? @params.Value<bool?>("dry_run")
                ?? false;
            string atomicity = (@params.Value<string>("atomicity") ?? "none")
                .Trim()
                .ToLowerInvariant();
            bool rollbackOnFailure = @params.Value<bool?>("rollbackOnFailure")
                ?? @params.Value<bool?>("rollback_on_failure")
                ?? false;

            if (atomicity != "none" && atomicity != "undo_group")
            {
                return BatchError(
                    "INVALID_BATCH_ATOMICITY",
                    "atomicity must be 'none' or 'undo_group'.");
            }
            if (rollbackOnFailure && atomicity != "undo_group")
            {
                return BatchError(
                    "ROLLBACK_REQUIRES_UNDO_GROUP",
                    "rollback_on_failure requires atomicity='undo_group'.");
            }

            if (parallelRequested)
            {
                McpLog.Warn(
                    "batch_execute parallel mode requested, but commands will run sequentially on the main thread for safety.");
            }

            List<PreflightCommand> commands = Preflight(commandsToken, out JArray errors);
            if (errors.Count > 0)
            {
                return BatchError(
                    "BATCH_PREFLIGHT_FAILED",
                    "Batch preflight failed; no child command was executed.",
                    new JObject
                    {
                        ["errors"] = errors,
                        ["started_count"] = 0,
                        ["skipped_count"] = commandsToken.Count
                    });
            }

            if (atomicity == "undo_group")
            {
                JArray unsupported = new JArray(
                    commands
                        .Where(command => !IsUndoCompatible(command.Policy))
                        .Select(command => new JObject
                        {
                            ["index"] = command.Index,
                            ["tool"] = command.ToolName,
                            ["contract_tool"] = command.ContractToolName,
                            ["mutation_class"] = command.Policy?.MutationClass,
                            ["undoable"] = command.Policy?.Undoable ?? false,
                            ["triggers_compilation"] = command.Policy?.TriggersCompilation ?? false
                        }));
                if (unsupported.Count > 0)
                {
                    return BatchError(
                        "BATCH_ROLLBACK_UNSUPPORTED",
                        "The requested Undo group contains commands whose rollback cannot be proven.",
                        new JObject
                        {
                            ["unsupported"] = unsupported,
                            ["started_count"] = 0,
                            ["skipped_count"] = commands.Count
                        });
                }
            }

            if (dryRun)
            {
                return new SuccessResponse(
                    "Batch preflight completed; dry_run executed no commands.",
                    BuildBatchData(
                        new JArray(commands.Select(command => new JObject
                        {
                            ["index"] = command.Index,
                            ["tool"] = command.ToolName,
                            ["contract_tool"] = command.ContractToolName,
                            ["callSucceeded"] = true,
                            ["status"] = "validated"
                        })),
                        commands.Count,
                        parallelRequested,
                        maxParallel,
                        dryRun,
                        atomicity,
                        rollbackOnFailure,
                        rollbackApplied: false));
            }

            int undoGroup = -1;
            if (atomicity == "undo_group")
            {
                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("MCP batch_execute");
            }

            var commandResults = new JArray();
            bool anyCommandFailed = false;
            bool rollbackApplied = false;
            int nextIndex = 0;
            for (; nextIndex < commands.Count; nextIndex++)
            {
                PreflightCommand command = commands[nextIndex];
                try
                {
                    object result = await CommandRegistry.InvokeCommandAsync(
                        command.ToolName,
                        command.Parameters).ConfigureAwait(true);
                    bool callSucceeded = DetermineCallSucceeded(result);
                    commandResults.Add(new JObject
                    {
                        ["index"] = command.Index,
                        ["tool"] = command.ToolName,
                        ["contract_tool"] = command.ContractToolName,
                        ["callSucceeded"] = callSucceeded,
                        ["status"] = callSucceeded ? "succeeded" : "failed",
                        ["result"] = result == null ? null : JToken.FromObject(result)
                    });

                    if (!callSucceeded)
                    {
                        anyCommandFailed = true;
                    }
                }
                catch (Exception ex)
                {
                    anyCommandFailed = true;
                    commandResults.Add(new JObject
                    {
                        ["index"] = command.Index,
                        ["tool"] = command.ToolName,
                        ["contract_tool"] = command.ContractToolName,
                        ["callSucceeded"] = false,
                        ["status"] = "failed",
                        ["error"] = ex.Message
                    });
                }

                if (anyCommandFailed && rollbackOnFailure)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    rollbackApplied = true;
                    foreach (JObject prior in commandResults.Children<JObject>())
                    {
                        if (prior.Value<string>("status") == "succeeded")
                        {
                            prior["status"] = "reverted";
                            prior["reverted"] = true;
                        }
                    }
                    nextIndex++;
                    break;
                }

                if (anyCommandFailed && failFast)
                {
                    nextIndex++;
                    break;
                }
            }

            for (; nextIndex < commands.Count; nextIndex++)
            {
                PreflightCommand skipped = commands[nextIndex];
                commandResults.Add(new JObject
                {
                    ["index"] = skipped.Index,
                    ["tool"] = skipped.ToolName,
                    ["contract_tool"] = skipped.ContractToolName,
                    ["callSucceeded"] = false,
                    ["status"] = "skipped",
                    ["reason"] = rollbackApplied
                        ? "rollback_on_failure"
                        : "fail_fast"
                });
            }

            if (undoGroup >= 0 && !rollbackApplied)
            {
                Undo.CollapseUndoOperations(undoGroup);
            }

            JObject data = BuildBatchData(
                commandResults,
                commands.Count,
                parallelRequested,
                maxParallel,
                dryRun,
                atomicity,
                rollbackOnFailure,
                rollbackApplied);
            return anyCommandFailed
                ? BatchError(
                    "BATCH_EXECUTION_FAILED",
                    rollbackApplied
                        ? "A child command failed; completed Undo-compatible mutations were reverted."
                        : "One or more child commands failed.",
                    data)
                : new SuccessResponse("Batch execution completed.", data);
        }

        private static List<PreflightCommand> Preflight(
            JArray commandTokens,
            out JArray errors)
        {
            var commands = new List<PreflightCommand>(commandTokens.Count);
            errors = new JArray();
            for (int index = 0; index < commandTokens.Count; index++)
            {
                if (commandTokens[index] is not JObject commandObject)
                {
                    errors.Add(PreflightError(
                        index,
                        null,
                        "INVALID_BATCH_ENTRY",
                        "Command entries must be JSON objects."));
                    continue;
                }

                string toolName = commandObject.Value<string>("tool")?.Trim();
                if (string.IsNullOrEmpty(toolName))
                {
                    errors.Add(PreflightError(
                        index,
                        toolName,
                        "MISSING_BATCH_TOOL",
                        "Each command must include a non-empty 'tool' field."));
                    continue;
                }
                if (string.Equals(toolName, "batch_execute", StringComparison.Ordinal))
                {
                    errors.Add(PreflightError(
                        index,
                        toolName,
                        "BATCH_RECURSION_DENIED",
                        "Nested batch_execute commands are not supported."));
                    continue;
                }

                JToken paramsToken = commandObject["params"];
                if (paramsToken != null
                    && paramsToken.Type != JTokenType.Null
                    && paramsToken is not JObject)
                {
                    errors.Add(PreflightError(
                        index,
                        toolName,
                        "INVALID_BATCH_PARAMS",
                        "Command params must be a JSON object."));
                    continue;
                }
                JObject parameters = NormalizeParameterKeys(paramsToken as JObject ?? new JObject());

                if (!CommandRegistry.IsRegistered(toolName))
                {
                    errors.Add(PreflightError(
                        index,
                        toolName,
                        "BATCH_TOOL_NOT_FOUND",
                        $"Tool '{toolName}' is not registered."));
                    continue;
                }

                var toolMetadata = MCPServiceLocator.ToolDiscovery.GetToolMetadata(toolName);
                if (toolMetadata != null
                    && !MCPServiceLocator.ToolDiscovery.IsToolEnabled(toolName))
                {
                    errors.Add(PreflightError(
                        index,
                        toolName,
                        "BATCH_TOOL_DISABLED",
                        $"Tool '{toolName}' is disabled in the Unity Editor."));
                    continue;
                }

                CommandMutationPolicy.TryResolvePolicy(
                    toolName,
                    parameters,
                    out string contractToolName,
                    out CommandRuntimeToolPolicy policy);
                commands.Add(new PreflightCommand
                {
                    Index = index,
                    ToolName = toolName,
                    Parameters = parameters,
                    ContractToolName = contractToolName,
                    Policy = policy
                });
            }

            return commands;
        }

        private static JObject BuildBatchData(
            JArray results,
            int commandCount,
            bool parallelRequested,
            int? maxParallel,
            bool dryRun,
            string atomicity,
            bool rollbackOnFailure,
            bool rollbackApplied)
        {
            int succeeded = results.Children<JObject>().Count(
                result => result.Value<string>("status") == "succeeded");
            int failed = results.Children<JObject>().Count(
                result => result.Value<string>("status") == "failed");
            int skipped = results.Children<JObject>().Count(
                result => result.Value<string>("status") == "skipped");
            int reverted = results.Children<JObject>().Count(
                result => result.Value<string>("status") == "reverted");
            int validated = results.Children<JObject>().Count(
                result => result.Value<string>("status") == "validated");
            int started = succeeded + failed + reverted;

            return new JObject
            {
                ["results"] = results,
                ["command_count"] = commandCount,
                ["started_count"] = started,
                ["succeeded_count"] = succeeded,
                ["failed_count"] = failed,
                ["skipped_count"] = skipped,
                ["reverted_count"] = reverted,
                ["validated_count"] = validated,
                // Retain today's field names for clients during migration.
                ["callSuccessCount"] = succeeded,
                ["callFailureCount"] = failed,
                ["parallelRequested"] = parallelRequested,
                ["parallelApplied"] = false,
                ["maxParallelism"] = maxParallel,
                ["dry_run"] = dryRun,
                ["atomicity"] = atomicity,
                ["rollback_on_failure"] = rollbackOnFailure,
                ["rollback_applied"] = rollbackApplied
            };
        }

        private static bool IsUndoCompatible(CommandRuntimeToolPolicy policy)
        {
            if (policy == null)
            {
                return false;
            }
            if (policy.MutationClass == "read_only")
            {
                return true;
            }
            return policy.MutationClass == "scene"
                && policy.Undoable
                && !policy.TriggersCompilation;
        }

        private static ErrorResponse BatchError(
            string code,
            string message,
            JObject data = null)
        {
            data ??= new JObject();
            data["message"] = message;
            return new ErrorResponse(code, data);
        }

        private static JObject PreflightError(
            int index,
            string tool,
            string code,
            string message)
        {
            return new JObject
            {
                ["index"] = index,
                ["tool"] = tool,
                ["code"] = code,
                ["message"] = message
            };
        }

        private static bool DetermineCallSucceeded(object result)
        {
            if (result == null)
            {
                return true;
            }
            if (result is IMcpResponse response)
            {
                return response.Success;
            }
            if (result is JToken token)
            {
                bool? success = token.Value<bool?>("success");
                return success ?? true;
            }
            return true;
        }

        private static JObject NormalizeParameterKeys(JObject source)
        {
            var normalized = new JObject();
            foreach (JProperty property in source.Properties())
            {
                normalized[StringCaseUtility.ToCamelCase(property.Name)] = property.Value;
            }
            return normalized;
        }
    }
}
