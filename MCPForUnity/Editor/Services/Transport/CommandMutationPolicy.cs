using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Services.Transport
{
    internal sealed class CommandMutationAuthorization
    {
        internal CommandMutationAuthorization(
            bool allowed,
            string code,
            string message,
            JObject data)
        {
            Allowed = allowed;
            Code = code;
            Message = message;
            Data = data ?? new JObject();
        }

        internal bool Allowed { get; }
        internal string Code { get; }
        internal string Message { get; }
        internal JObject Data { get; }
    }

    /// <summary>
    /// Repeats the server's generated mutation-contract check inside Unity. The
    /// requested server profile and the local Editor profile must both permit a
    /// command; the local side can therefore only tighten the negotiated policy.
    /// </summary>
    internal static class CommandMutationPolicy
    {
        internal const string DefaultProfile = "unrestricted";
        internal const string ReadOnlyProfile = "read_only";
        internal const string StandardProfile = "standard";
        internal const string DestructiveProfile = "destructive";
        internal const string UnrestrictedProfile = "unrestricted";

        private static readonly HashSet<string> ValidProfiles = new(StringComparer.Ordinal)
        {
            ReadOnlyProfile,
            StandardProfile,
            DestructiveProfile,
            UnrestrictedProfile
        };

        private static readonly HashSet<string> StandardClasses = new(StringComparer.Ordinal)
        {
            "read_only",
            "editor_state",
            "scene",
            "asset"
        };

        private static readonly HashSet<string> DestructiveClasses = new(StringComparer.Ordinal)
        {
            "read_only",
            "editor_state",
            "scene",
            "asset",
            "project_settings"
        };

        internal static string GetConfiguredProfile()
        {
            string configured = EditorPrefs.GetString(
                GetProjectPreferenceKey(),
                DefaultProfile);
            return Normalize(configured);
        }

        internal static string GetProjectPreferenceKey()
        {
            string projectHash = ProjectIdentityUtility.GetProjectHash();
            return string.IsNullOrEmpty(projectHash)
                ? EditorPrefKeys.CommandRuntimeMutationProfile
                : EditorPrefKeys.CommandRuntimeMutationProfile + "." + projectHash;
        }

        internal static CommandMutationAuthorization Authorize(
            string commandHandler,
            JObject runtime,
            string localProfile,
            JObject parameters = null)
        {
            string requestedProfile = Normalize(runtime?.Value<string>("profile"));
            string normalizedLocalProfile = Normalize(localProfile);
            string toolName = runtime?.Value<string>("tool_name");
            if (string.IsNullOrWhiteSpace(toolName))
            {
                TryResolvePolicy(
                    commandHandler,
                    parameters,
                    out toolName,
                    out _);
            }

            var data = new JObject
            {
                ["profile"] = requestedProfile,
                ["local_profile"] = normalizedLocalProfile,
                ["tool_name"] = toolName,
                ["command"] = commandHandler ?? string.Empty
            };

            if (!ValidProfiles.Contains(requestedProfile)
                || !ValidProfiles.Contains(normalizedLocalProfile))
            {
                return Denied(
                    "INVALID_MUTATION_PROFILE",
                    "The requested or local mutation profile is invalid.",
                    data);
            }

            if (!CommandRuntimeContract.ToolPolicies.TryGetValue(
                    toolName,
                    out CommandRuntimeToolPolicy policy))
            {
                if (requestedProfile == UnrestrictedProfile
                    && normalizedLocalProfile == UnrestrictedProfile)
                {
                    return new CommandMutationAuthorization(true, null, null, data);
                }

                return Denied(
                    "TOOL_CONTRACT_NOT_FOUND",
                    $"No built-in mutation contract is available for Unity command '{commandHandler}'. "
                        + "Restrictive profiles fail closed.",
                    data);
            }

            data["mutation_class"] = policy.MutationClass;
            data["destructive"] = policy.Destructive;
            if (!string.Equals(policy.Handler, commandHandler, StringComparison.Ordinal))
            {
                return Denied(
                    "TOOL_CONTRACT_MISMATCH",
                    $"Tool contract '{toolName}' targets handler '{policy.Handler}', not "
                        + $"'{commandHandler}'.",
                    data);
            }

            if (!ProfileAllows(requestedProfile, policy)
                || !ProfileAllows(normalizedLocalProfile, policy))
            {
                return Denied(
                    "MUTATION_PROFILE_DENIED",
                    $"Mutation profiles '{requestedProfile}' (server) and "
                        + $"'{normalizedLocalProfile}' (Unity) do not both allow tool "
                        + $"'{toolName}'.",
                    data);
            }

            return new CommandMutationAuthorization(true, null, null, data);
        }

        internal static bool TryResolvePolicy(
            string commandHandler,
            JObject parameters,
            out string toolName,
            out CommandRuntimeToolPolicy policy)
        {
            toolName = commandHandler ?? string.Empty;
            policy = null;
            if (string.Equals(commandHandler, "manage_script", StringComparison.Ordinal))
            {
                string action = parameters?.Value<string>("action")?.Trim().ToLowerInvariant();
                toolName = action switch
                {
                    "read" => "find_in_file",
                    "validate" => "validate_script",
                    "get_sha" => "get_sha",
                    "apply_text_edits" => "apply_text_edits",
                    _ => "manage_script"
                };
                return CommandRuntimeContract.ToolPolicies.TryGetValue(toolName, out policy);
            }

            if (CommandRuntimeContract.ToolPolicies.TryGetValue(toolName, out policy)
                && string.Equals(policy.Handler, commandHandler, StringComparison.Ordinal))
            {
                return true;
            }

            var matches = CommandRuntimeContract.ToolPolicies
                .Where(pair => pair.Value.RequiresUnity
                    && string.Equals(pair.Value.Handler, commandHandler, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
            {
                toolName = matches[0].Key;
                policy = matches[0].Value;
                return true;
            }

            policy = null;
            return false;
        }

        internal static bool ProfileAllows(
            string profile,
            CommandRuntimeToolPolicy policy)
        {
            if (profile == UnrestrictedProfile)
            {
                return true;
            }

            if (policy == null)
            {
                return false;
            }

            if (profile == ReadOnlyProfile)
            {
                return policy.MutationClass == "read_only" && !policy.Destructive;
            }

            if (profile == StandardProfile)
            {
                return StandardClasses.Contains(policy.MutationClass) && !policy.Destructive;
            }

            return profile == DestructiveProfile
                && DestructiveClasses.Contains(policy.MutationClass);
        }

        internal static string Normalize(string profile)
        {
            return (profile ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static CommandMutationAuthorization Denied(
            string code,
            string message,
            JObject data)
        {
            return new CommandMutationAuthorization(false, code, message, data);
        }
    }
}
