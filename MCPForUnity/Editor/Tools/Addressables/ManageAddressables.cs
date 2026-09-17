using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Tools.Addressables
{
    [McpForUnityTool("manage_addressables", AutoRegister = false, Group = "core")]
    public static class ManageAddressables
    {
        private static readonly string[] ValidActions =
        {
            "ping",
            "initialize",
            "list_groups",
            "create_group",
            "set_default_group",
            "remove_group",
            "list_entries",
            "add_entry",
            "remove_entry",
            "set_address",
            "list_labels",
            "add_label",
            "remove_label",
            "set_label",
            "get_profiles",
            "set_profile_value",
            "validate",
            "build",
            "build_status",
        };

        private static string _buildJobId;
        private static string _buildStatus = "idle";
        private static string _buildError;
        private static object _buildResult;
        private static string _buildStartedAt;
        private static string _buildCompletedAt;
        private static int _buildDelayTicks;
        private static double _buildNotBefore;

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }
            var p = new ToolParams(@params);
            string action = p.Get("action")?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
            {
                return new ErrorResponse("'action' parameter is required.");
            }

            try
            {
                if (action == "ping") return Ping();
                if (!AddressablesUtility.IsInstalled)
                {
                    return ErrorResponse.FromCode(
                        "PACKAGE_UNAVAILABLE",
                        "Unity Addressables is unavailable. Install com.unity.addressables to use this action.",
                        new { package = "com.unity.addressables" });
                }
                if (action == "initialize") return Initialize();
                if (action == "validate") return Validate();
                if (action == "build_status") return BuildStatus(p);

                object settings = AddressablesUtility.GetSettings(false);
                if (settings == null)
                {
                    return ErrorResponse.FromCode(
                        "ADDRESSABLES_NOT_INITIALIZED",
                        "Addressables settings are not initialized. Call manage_addressables with action='initialize'.",
                        new { package = "com.unity.addressables" });
                }

                switch (action)
                {
                    case "list_groups":
                        return ListGroups(settings, @params);
                    case "create_group":
                        return CreateGroup(settings, p);
                    case "set_default_group":
                        return SetDefaultGroup(settings, p);
                    case "remove_group":
                        return RemoveGroup(settings, p);
                    case "list_entries":
                        return ListEntries(settings, p, @params);
                    case "add_entry":
                        return AddEntry(settings, p);
                    case "remove_entry":
                        return RemoveEntry(settings, p);
                    case "set_address":
                        return SetAddress(settings, p);
                    case "list_labels":
                        return ListLabels(settings);
                    case "add_label":
                        return AddLabel(settings, p);
                    case "remove_label":
                        return RemoveLabel(settings, p);
                    case "set_label":
                        return SetLabel(settings, p);
                    case "get_profiles":
                        return GetProfiles(settings);
                    case "set_profile_value":
                        return SetProfileValue(settings, p);
                    case "build":
                        return Build();
                    default:
                        return new ErrorResponse(
                            $"Unknown action: '{action}'. Valid actions: {string.Join(", ", ValidActions)}.");
                }
            }
            catch (Exception ex)
            {
                Exception cause = ex is System.Reflection.TargetInvocationException
                    && ex.InnerException != null ? ex.InnerException : ex;
                McpLog.Error($"[ManageAddressables] Action '{action}' failed: {cause}");
                return new ErrorResponse($"Addressables action '{action}' failed: {cause.Message}");
            }
        }

        private static object Ping()
        {
            bool installed = AddressablesUtility.IsInstalled;
            object settings = installed ? AddressablesUtility.GetSettings(false) : null;
            return new SuccessResponse(
                installed
                    ? "Unity Addressables management is available."
                    : "Unity Addressables package is not installed.",
                new
                {
                    installed,
                    package = "com.unity.addressables",
                    version = AddressablesUtility.GetPackageVersion(),
                    initialized = settings != null,
                    settingsPath = AddressablesUtility.GetSettingsPath(settings),
                    groupCount = settings != null ? AddressablesUtility.GetGroups(settings).Count : 0,
                    actions = ValidActions,
                });
        }

        private static object Initialize()
        {
            bool alreadyInitialized = AddressablesUtility.GetSettings(false) != null;
            object settings = AddressablesUtility.GetSettings(true);
            if (settings == null)
            {
                return new ErrorResponse("Addressables settings could not be initialized.");
            }
            return new SuccessResponse(
                alreadyInitialized
                    ? "Addressables settings were already initialized."
                    : "Addressables settings initialized.",
                SettingsSummary(settings));
        }

        private static object ListGroups(object settings, JObject raw)
        {
            List<object> items = AddressablesUtility.GetGroups(settings)
                .Select(group => AddressablesUtility.DescribeGroup(settings, group))
                .ToList();
            PaginationResponse<object> page = CreatePage(items, raw);
            return new SuccessResponse($"Found {items.Count} Addressables group(s).", new
            {
                settingsPath = AddressablesUtility.GetSettingsPath(settings),
                items = page.Items,
                cursor = page.Cursor,
                nextCursor = page.NextCursor,
                totalCount = page.TotalCount,
                pageSize = page.PageSize,
                hasMore = page.HasMore,
            });
        }

        private static object CreateGroup(object settings, ToolParams p)
        {
            string groupName = Require(p, "group_name");
            if (groupName == null) return new ErrorResponse("'group_name' parameter is required.");
            object group = AddressablesUtility.CreateGroup(
                settings,
                groupName,
                p.GetBool("set_default", false));
            return new SuccessResponse($"Created Addressables group '{groupName}'.",
                AddressablesUtility.DescribeGroup(settings, group));
        }

        private static object SetDefaultGroup(object settings, ToolParams p)
        {
            object group;
            ErrorResponse error;
            if (!TryGetGroup(settings, p, out group, out error)) return error;
            if (Convert.ToBoolean(JObject.FromObject(
                AddressablesUtility.DescribeGroup(settings, group))["readOnly"]))
            {
                return new ErrorResponse("A read-only group cannot be the default group.");
            }
            AddressablesUtility.SetDefaultGroup(settings, group);
            return new SuccessResponse("Default Addressables group updated.",
                AddressablesUtility.DescribeGroup(settings, group));
        }

        private static object RemoveGroup(object settings, ToolParams p)
        {
            object group;
            ErrorResponse error;
            if (!TryGetGroup(settings, p, out group, out error)) return error;
            if (ReferenceEquals(AddressablesUtility.GetDefaultGroup(settings), group))
            {
                return new ErrorResponse(
                    "The default Addressables group cannot be removed. Set another default group first.");
            }
            int entryCount = AddressablesUtility.GetEntries(group).Count;
            if (entryCount > 0 && !p.GetBool("force", false))
            {
                return new ErrorResponse(
                    $"Group contains {entryCount} entry or entries. Set force=true to remove it.");
            }
            string groupName = JObject.FromObject(
                AddressablesUtility.DescribeGroup(settings, group)).Value<string>("name");
            AddressablesUtility.RemoveGroup(settings, group);
            return new SuccessResponse($"Removed Addressables group '{groupName}'.", new
            {
                groupName,
                removedEntryCount = entryCount,
            });
        }

        private static object ListEntries(object settings, ToolParams p, JObject raw)
        {
            IEnumerable<object> groups = AddressablesUtility.GetGroups(settings);
            string groupName = p.Get("group_name");
            if (!string.IsNullOrWhiteSpace(groupName))
            {
                object group = AddressablesUtility.FindGroup(settings, groupName);
                if (group == null)
                {
                    return new ErrorResponse($"Addressables group '{groupName}' does not exist.");
                }
                groups = new[] { group };
            }
            var items = new List<object>();
            foreach (object group in groups)
            {
                items.AddRange(AddressablesUtility.GetEntries(group)
                    .Select(entry => AddressablesUtility.DescribeEntry(group, entry)));
            }
            string search = p.Get("search");
            if (!string.IsNullOrWhiteSpace(search))
            {
                items = items.Where(item => JObject.FromObject(item).ToString()
                    .IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }
            items = items.OrderBy(item => JObject.FromObject(item).Value<string>("address"),
                StringComparer.OrdinalIgnoreCase).ToList();
            PaginationResponse<object> page = CreatePage(items, raw);
            return new SuccessResponse($"Found {items.Count} Addressables entry or entries.", new
            {
                groupName,
                search,
                items = page.Items,
                cursor = page.Cursor,
                nextCursor = page.NextCursor,
                totalCount = page.TotalCount,
                pageSize = page.PageSize,
                hasMore = page.HasMore,
            });
        }

        private static object AddEntry(object settings, ToolParams p)
        {
            string normalized;
            string guid;
            if (!AddressablesUtility.IsValidAssetPath(p.Get("asset_path"), out normalized, out guid))
            {
                return new ErrorResponse(
                    "'asset_path' must identify an existing asset or folder below Assets/.");
            }
            object group;
            string groupName = p.Get("group_name");
            if (string.IsNullOrWhiteSpace(groupName))
            {
                group = AddressablesUtility.GetDefaultGroup(settings);
            }
            else
            {
                group = AddressablesUtility.FindGroup(settings, groupName);
                if (group == null)
                {
                    return new ErrorResponse($"Addressables group '{groupName}' does not exist.");
                }
            }
            object entry = AddressablesUtility.AddEntry(
                settings,
                group,
                guid,
                p.Has("address") ? p.Get("address", string.Empty) : null,
                p.GetStringArray("labels") ?? new string[0]);
            return new SuccessResponse($"Added '{normalized}' to Addressables.",
                AddressablesUtility.DescribeEntry(group, entry));
        }

        private static object RemoveEntry(object settings, ToolParams p)
        {
            AddressableEntryLookup lookup;
            ErrorResponse error;
            if (!TryGetEntry(settings, p, out lookup, out error)) return error;
            JObject description = JObject.FromObject(
                AddressablesUtility.DescribeEntry(lookup.Group, lookup.Entry));
            string guid = description.Value<string>("guid");
            if (!AddressablesUtility.RemoveEntry(settings, guid))
            {
                return new ErrorResponse($"Failed to remove Addressables entry '{guid}'.");
            }
            return new SuccessResponse("Addressables entry removed.", description);
        }

        private static object SetAddress(object settings, ToolParams p)
        {
            if (!p.Has("address"))
            {
                return new ErrorResponse("'address' parameter is required.");
            }
            string guid = p.Get("guid");
            string assetPath = p.Get("asset_path");
            if (string.IsNullOrWhiteSpace(guid) && string.IsNullOrWhiteSpace(assetPath))
            {
                return new ErrorResponse(
                    "Identify the entry to update with 'guid' or 'asset_path'.");
            }
            AddressableEntryLookup lookup = AddressablesUtility.FindEntry(
                settings, guid, assetPath, null);
            if (lookup == null)
            {
                return new ErrorResponse("The requested Addressables entry does not exist.");
            }
            AddressablesUtility.SetAddress(settings, lookup.Entry, p.Get("address", string.Empty));
            return new SuccessResponse("Addressables entry address updated.",
                AddressablesUtility.DescribeEntry(lookup.Group, lookup.Entry));
        }

        private static object ListLabels(object settings)
        {
            List<string> labels = AddressablesUtility.GetLabels(settings);
            return new SuccessResponse($"Found {labels.Count} Addressables label(s).", new
            {
                labels,
                count = labels.Count,
            });
        }

        private static object AddLabel(object settings, ToolParams p)
        {
            string label = Require(p, "label");
            if (label == null) return new ErrorResponse("'label' parameter is required.");
            AddressablesUtility.AddLabel(settings, label);
            return new SuccessResponse($"Addressables label '{label}' is available.", new { label });
        }

        private static object RemoveLabel(object settings, ToolParams p)
        {
            string label = Require(p, "label");
            if (label == null) return new ErrorResponse("'label' parameter is required.");
            if (!AddressablesUtility.GetLabels(settings).Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                return new ErrorResponse($"Addressables label '{label}' does not exist.");
            }
            var matches = new List<AddressableEntryLookup>();
            foreach (object group in AddressablesUtility.GetGroups(settings))
            {
                matches.AddRange(AddressablesUtility.GetEntries(group)
                    .Where(entry => EntryHasLabel(group, entry, label))
                    .Select(entry => new AddressableEntryLookup { Group = group, Entry = entry }));
            }
            if (matches.Count > 0 && !p.GetBool("force", false))
            {
                return new ErrorResponse(
                    $"Label '{label}' is used by {matches.Count} entry or entries. Set force=true to remove it.");
            }
            foreach (AddressableEntryLookup match in matches)
            {
                AddressablesUtility.SetEntryLabel(settings, match.Entry, label, false);
            }
            AddressablesUtility.RemoveLabel(settings, label);
            return new SuccessResponse($"Removed Addressables label '{label}'.", new
            {
                label,
                clearedEntryCount = matches.Count,
            });
        }

        private static object SetLabel(object settings, ToolParams p)
        {
            string label = Require(p, "label");
            if (label == null) return new ErrorResponse("'label' parameter is required.");
            AddressableEntryLookup lookup;
            ErrorResponse error;
            if (!TryGetEntry(settings, p, out lookup, out error)) return error;
            bool enabled = p.GetBool("enabled", true);
            AddressablesUtility.SetEntryLabel(settings, lookup.Entry, label, enabled);
            return new SuccessResponse(
                enabled ? "Addressables label enabled on entry." : "Addressables label disabled on entry.",
                AddressablesUtility.DescribeEntry(lookup.Group, lookup.Entry));
        }

        private static object GetProfiles(object settings)
        {
            List<object> profiles = AddressablesUtility.GetProfileSummaries(settings);
            return new SuccessResponse($"Found {profiles.Count} Addressables profile(s).", new
            {
                profiles,
                count = profiles.Count,
            });
        }

        private static object SetProfileValue(object settings, ToolParams p)
        {
            string variableName = Require(p, "variable_name");
            if (variableName == null) return new ErrorResponse("'variable_name' parameter is required.");
            if (!p.Has("value")) return new ErrorResponse("'value' parameter is required.");
            string profileId;
            AddressablesUtility.SetProfileValue(
                settings,
                p.Get("profile_name"),
                variableName,
                p.Get("value", string.Empty),
                p.GetBool("create_variable", false),
                out profileId);
            return new SuccessResponse("Addressables profile value updated.", new
            {
                profileName = p.Get("profile_name"),
                profileId,
                variableName,
                value = p.Get("value", string.Empty),
            });
        }

        private static object Validate()
        {
            object settings = AddressablesUtility.GetSettings(false);
            List<AddressablesValidationIssue> issues = AddressablesUtility.Validate(settings);
            int errorCount = issues.Count(issue => issue.Severity == "error");
            int warningCount = issues.Count(issue => issue.Severity == "warning");
            return new SuccessResponse(
                $"Addressables validation found {errorCount} error(s) and {warningCount} warning(s).",
                new
                {
                    initialized = settings != null,
                    settingsPath = AddressablesUtility.GetSettingsPath(settings),
                    valid = errorCount == 0,
                    errorCount,
                    warningCount,
                    issues,
                });
        }

        private static object Build()
        {
            if (_buildStatus == "queued" || _buildStatus == "running")
            {
                return new SuccessResponse("An Addressables content build is already running.", BuildSummary());
            }

            List<string> dirtyScenes = AddressablesUtility.GetDirtySceneNames();
            if (dirtyScenes.Count > 0)
            {
                return new ErrorResponse(
                    "Addressables content cannot be built while scenes have unsaved changes. "
                    + "Save or discard these scenes first: " + string.Join(", ", dirtyScenes));
            }

            _buildJobId = Guid.NewGuid().ToString("N");
            _buildStatus = "queued";
            _buildError = null;
            _buildResult = null;
            _buildStartedAt = null;
            _buildCompletedAt = null;

            // Do not run the synchronous Addressables build inside the MCP command callback.
            // Packed content builds can take minutes and would otherwise outlive the framed
            // request timeout, invite unsafe retries, and block every subsequent cleanup call.
            // Two editor update turns ensure the queued response has left the bridge before the
            // build occupies Unity's main thread. update is used instead of delayCall because
            // background/headless editor sessions do not consistently dispatch delayCall.
            _buildDelayTicks = 1;
            _buildNotBefore = EditorApplication.timeSinceStartup + 2.0;
            EditorApplication.update -= RunBuildOnEditorUpdate;
            EditorApplication.update += RunBuildOnEditorUpdate;
            return new SuccessResponse("Addressables content build queued.", BuildSummary());
        }

        private static void RunBuildOnEditorUpdate()
        {
            if (_buildDelayTicks-- > 0) return;
            if (EditorApplication.isCompiling
                || EditorApplication.isUpdating
                || EditorApplication.timeSinceStartup < _buildNotBefore)
            {
                return;
            }
            EditorApplication.update -= RunBuildOnEditorUpdate;
            RunBuildJob();
        }

        private static void RunBuildJob()
        {
            if (_buildStatus != "queued") return;

            _buildStatus = "running";
            _buildStartedAt = DateTime.UtcNow.ToString("O");
            try
            {
                object data = AddressablesUtility.BuildPlayerContent();
                JObject serialized = JObject.FromObject(data);
                _buildResult = data;
                _buildError = serialized.Value<string>("error");
                _buildStatus = serialized.Value<bool>("succeeded") ? "completed" : "failed";
                if (_buildStatus == "failed" && string.IsNullOrWhiteSpace(_buildError))
                {
                    _buildError = "Addressables content build failed.";
                }
            }
            catch (Exception ex)
            {
                Exception cause = ex is System.Reflection.TargetInvocationException
                    && ex.InnerException != null ? ex.InnerException : ex;
                _buildError = cause.Message;
                _buildStatus = "failed";
                McpLog.Error($"[ManageAddressables] Build job '{_buildJobId}' failed: {cause}");
            }
            finally
            {
                _buildCompletedAt = DateTime.UtcNow.ToString("O");
            }
        }

        private static object BuildStatus(ToolParams p)
        {
            string requestedJobId = p.Get("job_id");
            if (string.IsNullOrWhiteSpace(_buildJobId))
            {
                return new ErrorResponse("No Addressables content build has been queued in this editor session.");
            }
            if (!string.IsNullOrWhiteSpace(requestedJobId)
                && !string.Equals(requestedJobId, _buildJobId, StringComparison.OrdinalIgnoreCase))
            {
                return new ErrorResponse($"Addressables build job '{requestedJobId}' was not found.");
            }
            return new SuccessResponse("Addressables content build status.", BuildSummary());
        }

        private static object BuildSummary()
        {
            return new
            {
                jobId = _buildJobId,
                status = _buildStatus,
                error = _buildError,
                startedAt = _buildStartedAt,
                completedAt = _buildCompletedAt,
                result = _buildResult,
            };
        }

        private static object SettingsSummary(object settings)
        {
            object defaultGroup = AddressablesUtility.GetDefaultGroup(settings);
            return new
            {
                settingsPath = AddressablesUtility.GetSettingsPath(settings),
                groupCount = AddressablesUtility.GetGroups(settings).Count,
                defaultGroup = defaultGroup != null
                    ? JObject.FromObject(AddressablesUtility.DescribeGroup(settings, defaultGroup))
                        .Value<string>("name")
                    : null,
            };
        }

        private static bool TryGetGroup(
            object settings,
            ToolParams p,
            out object group,
            out ErrorResponse error)
        {
            string groupName = Require(p, "group_name");
            if (groupName == null)
            {
                group = null;
                error = new ErrorResponse("'group_name' parameter is required.");
                return false;
            }
            group = AddressablesUtility.FindGroup(settings, groupName);
            if (group == null)
            {
                error = new ErrorResponse($"Addressables group '{groupName}' does not exist.");
                return false;
            }
            error = null;
            return true;
        }

        private static bool TryGetEntry(
            object settings,
            ToolParams p,
            out AddressableEntryLookup lookup,
            out ErrorResponse error)
        {
            string guid = p.Get("guid");
            string assetPath = p.Get("asset_path");
            string address = p.Get("address");
            if (string.IsNullOrWhiteSpace(guid)
                && string.IsNullOrWhiteSpace(assetPath)
                && string.IsNullOrWhiteSpace(address))
            {
                lookup = null;
                error = new ErrorResponse("Identify the entry with 'guid', 'asset_path', or 'address'.");
                return false;
            }
            lookup = AddressablesUtility.FindEntry(settings, guid, assetPath, address);
            if (lookup == null)
            {
                error = new ErrorResponse("The requested Addressables entry does not exist.");
                return false;
            }
            error = null;
            return true;
        }

        private static bool EntryHasLabel(object group, object entry, string label)
        {
            JObject description = JObject.FromObject(AddressablesUtility.DescribeEntry(group, entry));
            return (description["labels"] as JArray)?.Values<string>()
                .Any(item => item.Equals(label, StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static string Require(ToolParams p, string name)
        {
            string value = p.Get(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static PaginationResponse<object> CreatePage(List<object> items, JObject raw)
        {
            PaginationRequest request = PaginationRequest.FromParams(raw);
            request.PageSize = Math.Max(1, Math.Min(500, request.PageSize));
            return PaginationResponse<object>.Create(items, request);
        }
    }
}
