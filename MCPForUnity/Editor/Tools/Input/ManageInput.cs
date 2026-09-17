using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Tools.Input
{
    [McpForUnityTool("manage_input", AutoRegister = false, Group = "core")]
    public static class ManageInput
    {
        private static readonly string[] ValidActions =
        {
            "ping",
            "list_assets",
            "get",
            "create",
            "delete",
            "add_action_map",
            "remove_action_map",
            "add_action",
            "remove_action",
            "add_binding",
            "remove_binding",
            "add_control_scheme",
            "remove_control_scheme",
            "generate_csharp",
            "assign_player_input",
            "validate",
        };

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
                switch (action)
                {
                    case "ping":
                        return Ping();
                    case "list_assets":
                        return ListAssets(@params);
                }

                if (!InputActionsUtility.IsInputSystemInstalled)
                {
                    return new ErrorResponse(
                        "Unity Input System is unavailable. Install com.unity.inputsystem to use this action.");
                }

                switch (action)
                {
                    case "get":
                        return Get(p);
                    case "create":
                        return Create(p);
                    case "delete":
                        return Delete(p);
                    case "add_action_map":
                        return AddActionMap(p);
                    case "remove_action_map":
                        return RemoveActionMap(p);
                    case "add_action":
                        return AddAction(p);
                    case "remove_action":
                        return RemoveAction(p);
                    case "add_binding":
                        return AddBinding(p);
                    case "remove_binding":
                        return RemoveBinding(p);
                    case "add_control_scheme":
                        return AddControlScheme(p);
                    case "remove_control_scheme":
                        return RemoveControlScheme(p);
                    case "generate_csharp":
                        return GenerateCSharp(p);
                    case "assign_player_input":
                        return AssignPlayerInput(p);
                    case "validate":
                        return Validate(p);
                    default:
                        return new ErrorResponse(
                            $"Unknown action: '{action}'. Valid actions: {string.Join(", ", ValidActions)}.");
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"[ManageInput] Action '{action}' failed: {ex}");
                return new ErrorResponse($"Input System action '{action}' failed: {ex.Message}");
            }
        }

        private static object Ping()
        {
            bool installed = InputActionsUtility.IsInputSystemInstalled;
            return new SuccessResponse(
                installed
                    ? "Unity Input System management is available."
                    : "Unity Input System package is not installed.",
                new
                {
                    installed,
                    package = "com.unity.inputsystem",
                    version = InputActionsUtility.GetPackageVersion(),
                    assetCount = InputActionsUtility.ListAssetPaths().Count,
                    actions = ValidActions,
                });
        }

        private static object ListAssets(JObject raw)
        {
            List<string> paths = InputActionsUtility.ListAssetPaths();
            PaginationRequest request = PaginationRequest.FromParams(raw);
            request.PageSize = Clamp(request.PageSize, 1, 500);
            PaginationResponse<string> page = PaginationResponse<string>.Create(paths, request);
            return new SuccessResponse($"Found {paths.Count} Input Actions asset(s).", new
            {
                installed = InputActionsUtility.IsInputSystemInstalled,
                packageVersion = InputActionsUtility.GetPackageVersion(),
                items = page.Items,
                cursor = page.Cursor,
                nextCursor = page.NextCursor,
                totalCount = page.TotalCount,
                pageSize = page.PageSize,
                hasMore = page.HasMore,
            });
        }

        private static object Get(ToolParams p)
        {
            string assetPath;
            string fullPath;
            JObject document;
            ErrorResponse error;
            if (!TryLoad(p, out assetPath, out fullPath, out document, out error))
            {
                return error;
            }

            return new SuccessResponse("Input Actions asset loaded.",
                InputActionsUtility.Summarize(assetPath, document, p.GetBool("include_json", false)));
        }

        private static object Create(ToolParams p)
        {
            string assetPath;
            string fullPath;
            string pathError;
            if (!InputActionsUtility.TryResolvePath(
                p.Get("path"), false, out assetPath, out fullPath, out pathError))
            {
                return new ErrorResponse(pathError);
            }
            if (File.Exists(fullPath) || AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
            {
                return new ErrorResponse($"Input Actions asset '{assetPath}' already exists.");
            }

            string name = p.Get("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Path.GetFileNameWithoutExtension(assetPath);
            }
            JObject document = InputActionsUtility.CreateDocument(name);
            InputActionsUtility.SaveDocument(assetPath, fullPath, document);
            return new SuccessResponse($"Created Input Actions asset '{assetPath}'.",
                InputActionsUtility.Summarize(assetPath, document, false));
        }

        private static object Delete(ToolParams p)
        {
            string assetPath;
            string fullPath;
            string pathError;
            if (!InputActionsUtility.TryResolvePath(
                p.Get("path"), true, out assetPath, out fullPath, out pathError))
            {
                return new ErrorResponse(pathError);
            }
            if (!AssetDatabase.DeleteAsset(assetPath))
            {
                return new ErrorResponse($"Failed to delete Input Actions asset '{assetPath}'.");
            }
            AssetDatabase.SaveAssets();
            return new SuccessResponse($"Deleted Input Actions asset '{assetPath}'.", new { path = assetPath });
        }

        private static object AddActionMap(ToolParams p)
        {
            return Mutate(p, (document, error) =>
            {
                string mapName = Require(p, "map_name", error);
                if (mapName == null) return false;
                InputActionsUtility.AddMap(document, mapName);
                return true;
            }, "Action map added.");
        }

        private static object RemoveActionMap(ToolParams p)
        {
            return Mutate(p, (document, error) =>
            {
                string mapName = Require(p, "map_name", error);
                if (mapName == null) return false;
                if (!InputActionsUtility.RemoveMap(document, mapName))
                {
                    error.Message = $"Action map '{mapName}' does not exist.";
                    return false;
                }
                return true;
            }, "Action map removed.");
        }

        private static object AddAction(ToolParams p)
        {
            return Mutate(p, (document, error) =>
            {
                JObject map = RequireMap(document, p, error);
                string actionName = Require(p, "action_name", error);
                if (map == null || actionName == null) return false;
                string actionType = p.Get("action_type", "Button");
                if (!new[] { "Button", "Value", "PassThrough" }.Contains(
                    actionType, StringComparer.OrdinalIgnoreCase))
                {
                    error.Message = "action_type must be Button, Value, or PassThrough.";
                    return false;
                }
                InputActionsUtility.AddAction(
                    map,
                    actionName,
                    actionType,
                    p.Get("expected_control_type"),
                    p.Get("interactions"),
                    p.Get("processors"));
                return true;
            }, "Input action added.");
        }

        private static object RemoveAction(ToolParams p)
        {
            return Mutate(p, (document, error) =>
            {
                JObject map = RequireMap(document, p, error);
                string actionName = Require(p, "action_name", error);
                if (map == null || actionName == null) return false;
                if (!InputActionsUtility.RemoveAction(map, actionName))
                {
                    error.Message = $"Action '{actionName}' does not exist in map '{map.Value<string>("name")}'.";
                    return false;
                }
                return true;
            }, "Input action removed.");
        }

        private static object AddBinding(ToolParams p)
        {
            return Mutate(p, (document, error) =>
            {
                JObject map = RequireMap(document, p, error);
                string actionName = Require(p, "action_name", error);
                string bindingPath = Require(p, "binding_path", error);
                if (map == null || actionName == null || bindingPath == null) return false;
                InputActionsUtility.AddBinding(
                    map,
                    actionName,
                    bindingPath,
                    p.Get("binding_name"),
                    p.Get("interactions"),
                    p.Get("processors"),
                    p.Get("groups"),
                    p.GetBool("is_composite", false),
                    p.GetBool("is_part_of_composite", false));
                return true;
            }, "Input binding added.");
        }

        private static object RemoveBinding(ToolParams p)
        {
            return Mutate(p, (document, error) =>
            {
                JObject map = RequireMap(document, p, error);
                string actionName = Require(p, "action_name", error);
                int? bindingIndex = p.GetInt("binding_index");
                if (map == null || actionName == null) return false;
                if (!bindingIndex.HasValue)
                {
                    error.Message = "'binding_index' parameter is required.";
                    return false;
                }
                if (!InputActionsUtility.RemoveBinding(map, actionName, bindingIndex.Value))
                {
                    error.Message = $"Binding index {bindingIndex.Value} does not exist for action '{actionName}'.";
                    return false;
                }
                return true;
            }, "Input binding removed.");
        }

        private static object AddControlScheme(ToolParams p)
        {
            return Mutate(p, (document, error) =>
            {
                string schemeName = Require(p, "scheme_name", error);
                if (schemeName == null) return false;
                JToken devicesToken = p.GetRaw("devices");
                if (devicesToken != null && !(devicesToken is JArray))
                {
                    error.Message = "devices must be an array.";
                    return false;
                }
                InputActionsUtility.AddControlScheme(
                    document,
                    schemeName,
                    p.Get("binding_group"),
                    devicesToken as JArray);
                return true;
            }, "Control scheme added.");
        }

        private static object RemoveControlScheme(ToolParams p)
        {
            return Mutate(p, (document, error) =>
            {
                string schemeName = Require(p, "scheme_name", error);
                if (schemeName == null) return false;
                if (!InputActionsUtility.RemoveControlScheme(document, schemeName))
                {
                    error.Message = $"Control scheme '{schemeName}' does not exist.";
                    return false;
                }
                return true;
            }, "Control scheme removed.");
        }

        private static object GenerateCSharp(ToolParams p)
        {
            string assetPath;
            string fullPath;
            string pathError;
            if (!InputActionsUtility.TryResolvePath(
                p.Get("path"), true, out assetPath, out fullPath, out pathError))
            {
                return new ErrorResponse(pathError);
            }

            string outputPath = p.Get("output_path");
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = outputPath.Replace('\\', '/');
                if (!outputPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    outputPath = "Assets/" + outputPath.TrimStart('/');
                }
                if (!outputPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    return new ErrorResponse("output_path must end with .cs.");
                }
            }

            string configureError;
            if (!InputActionsUtility.ConfigureGeneratedWrapper(
                assetPath,
                outputPath,
                p.Get("class_name"),
                p.Get("namespace"),
                out configureError))
            {
                return new ErrorResponse(configureError);
            }
            return new SuccessResponse("Input Actions C# wrapper generation configured.", new
            {
                path = assetPath,
                outputPath,
                className = p.Get("class_name"),
                codeNamespace = p.Get("namespace"),
                compilationMayBePending = true,
            });
        }

        private static object AssignPlayerInput(ToolParams p)
        {
            string assetPath;
            string fullPath;
            string pathError;
            if (!InputActionsUtility.TryResolvePath(
                p.Get("path"), true, out assetPath, out fullPath, out pathError))
            {
                return new ErrorResponse(pathError);
            }
            string target = p.Get("target");
            if (string.IsNullOrWhiteSpace(target))
            {
                return new ErrorResponse("'target' parameter is required.");
            }

            object data;
            string assignmentError;
            if (!InputActionsUtility.AssignPlayerInput(
                assetPath,
                target,
                p.Get("default_map"),
                p.Get("default_scheme"),
                p.Get("notification_behavior"),
                out data,
                out assignmentError))
            {
                return new ErrorResponse(assignmentError);
            }
            return new SuccessResponse("PlayerInput component configured.", data);
        }

        private static object Validate(ToolParams p)
        {
            string assetPath;
            string fullPath;
            JObject document;
            ErrorResponse error;
            if (!TryLoad(p, out assetPath, out fullPath, out document, out error))
            {
                return error;
            }
            List<InputValidationIssue> issues = InputActionsUtility.Validate(assetPath, document);
            int errorCount = issues.Count(issue => issue.Severity == "error");
            int warningCount = issues.Count(issue => issue.Severity == "warning");
            UnityEngine.Object imported = AssetDatabase.LoadMainAssetAtPath(assetPath);
            return new SuccessResponse(
                $"Input Actions validation found {errorCount} error(s) and {warningCount} warning(s).",
                new
                {
                    path = assetPath,
                    valid = errorCount == 0,
                    errorCount,
                    warningCount,
                    issues,
                    importedType = imported != null ? imported.GetType().FullName : null,
                });
        }

        private static object Mutate(
            ToolParams p,
            Func<JObject, MutableError, bool> mutation,
            string message)
        {
            string assetPath;
            string fullPath;
            JObject document;
            ErrorResponse loadError;
            if (!TryLoad(p, out assetPath, out fullPath, out document, out loadError))
            {
                return loadError;
            }

            var mutationError = new MutableError();
            if (!mutation(document, mutationError))
            {
                return new ErrorResponse(mutationError.Message ?? "Input Actions mutation failed.");
            }
            InputActionsUtility.SaveDocument(assetPath, fullPath, document);
            return new SuccessResponse(message,
                InputActionsUtility.Summarize(assetPath, document, false));
        }

        private static bool TryLoad(
            ToolParams p,
            out string assetPath,
            out string fullPath,
            out JObject document,
            out ErrorResponse error)
        {
            string message;
            if (!InputActionsUtility.TryResolvePath(
                p.Get("path"), true, out assetPath, out fullPath, out message))
            {
                document = null;
                error = new ErrorResponse(message);
                return false;
            }
            if (!InputActionsUtility.TryLoadDocument(assetPath, fullPath, out document, out message))
            {
                error = new ErrorResponse(message);
                return false;
            }
            error = null;
            return true;
        }

        private static JObject RequireMap(JObject document, ToolParams p, MutableError error)
        {
            string mapName = Require(p, "map_name", error);
            if (mapName == null) return null;
            JObject map = InputActionsUtility.FindMap(document, mapName);
            if (map == null)
            {
                error.Message = $"Action map '{mapName}' does not exist.";
            }
            return map;
        }

        private static string Require(ToolParams p, string name, MutableError error)
        {
            string value = p.Get(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                error.Message = $"'{name}' parameter is required.";
                return null;
            }
            return value;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private sealed class MutableError
        {
            internal string Message { get; set; }
        }
    }
}
