using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MCPForUnity.Editor.Tools.GameObjects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Input
{
    internal sealed class InputValidationIssue
    {
        [JsonProperty("severity")]
        public string Severity { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("location")]
        public string Location { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    internal static class InputActionsUtility
    {
        private const string InputActionAssetTypeName = "UnityEngine.InputSystem.InputActionAsset";
        private const string PlayerInputTypeName = "UnityEngine.InputSystem.PlayerInput";
        private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(false);

        internal static Type InputActionAssetType => FindType(InputActionAssetTypeName);

        internal static bool IsInputSystemInstalled => InputActionAssetType != null;

        internal static string GetPackageVersion()
        {
            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .FirstOrDefault(item => item != null
                        && item.name.Equals("com.unity.inputsystem", StringComparison.OrdinalIgnoreCase));
            return package != null ? package.version : null;
        }

        internal static List<string> ListAssetPaths()
        {
            return AssetDatabase.GetAllAssetPaths()
                .Where(path => path.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static bool TryResolvePath(
            string rawPath,
            bool mustExist,
            out string assetPath,
            out string fullPath,
            out string error)
        {
            assetPath = null;
            fullPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                error = "'path' parameter is required.";
                return false;
            }

            string normalized = rawPath.Trim().Replace('\\', '/');
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                error = "Input Actions path must be below Assets/.";
                return false;
            }
            if (!normalized.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase))
            {
                error = "Input Actions path must end with .inputactions.";
                return false;
            }

            string projectRoot = System.IO.Path.GetFullPath(Directory.GetCurrentDirectory());
            string assetsRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, "Assets"));
            string resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                projectRoot,
                normalized.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!resolved.StartsWith(
                assetsRoot + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            {
                error = "Input Actions path resolves outside Assets/.";
                return false;
            }

            if (mustExist && !File.Exists(resolved))
            {
                error = $"Input Actions asset '{normalized}' does not exist.";
                return false;
            }

            assetPath = normalized;
            fullPath = resolved;
            return true;
        }

        internal static JObject CreateDocument(string assetName)
        {
            return new JObject
            {
                ["name"] = assetName,
                ["maps"] = new JArray(),
                ["controlSchemes"] = new JArray(),
            };
        }

        internal static bool TryLoadDocument(
            string assetPath,
            string fullPath,
            out JObject document,
            out string error)
        {
            document = null;
            error = null;
            try
            {
                document = JObject.Parse(File.ReadAllText(fullPath));
                return true;
            }
            catch (Exception ex)
            {
                error = $"Input Actions asset '{assetPath}' contains invalid JSON: {ex.Message}";
                return false;
            }
        }

        internal static void SaveDocument(string assetPath, string fullPath, JObject document)
        {
            string directory = System.IO.Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string content = document.ToString(Formatting.Indented) + "\n";
            File.WriteAllText(fullPath, content, Utf8WithoutBom);
            AssetDatabase.ImportAsset(
                assetPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
        }

        internal static object Summarize(string assetPath, JObject document, bool includeJson)
        {
            JArray maps = GetArray(document, "maps");
            JArray schemes = GetArray(document, "controlSchemes");
            var mapSummaries = maps.OfType<JObject>().Select(map =>
            {
                JArray actions = GetArray(map, "actions");
                JArray bindings = GetArray(map, "bindings");
                return new
                {
                    name = map.Value<string>("name"),
                    id = map.Value<string>("id"),
                    actionCount = actions.Count,
                    bindingCount = bindings.Count,
                    actions = actions.OfType<JObject>().Select(action => new
                    {
                        name = action.Value<string>("name"),
                        id = action.Value<string>("id"),
                        type = action.Value<string>("type"),
                        expectedControlType = action.Value<string>("expectedControlType"),
                    }).ToList(),
                };
            }).ToList();
            object json = includeJson ? document : null;
            return new
            {
                path = assetPath,
                name = document.Value<string>("name"),
                mapCount = maps.Count,
                controlSchemeCount = schemes.Count,
                maps = mapSummaries,
                controlSchemes = schemes,
                json,
            };
        }

        internal static JObject FindMap(JObject document, string mapName)
        {
            return GetArray(document, "maps")
                .OfType<JObject>()
                .FirstOrDefault(map => string.Equals(
                    map.Value<string>("name"),
                    mapName,
                    StringComparison.OrdinalIgnoreCase));
        }

        internal static JObject FindAction(JObject map, string actionName)
        {
            return GetArray(map, "actions")
                .OfType<JObject>()
                .FirstOrDefault(action => string.Equals(
                    action.Value<string>("name"),
                    actionName,
                    StringComparison.OrdinalIgnoreCase));
        }

        internal static JObject AddMap(JObject document, string mapName)
        {
            if (FindMap(document, mapName) != null)
            {
                throw new InvalidOperationException($"Action map '{mapName}' already exists.");
            }

            var map = new JObject
            {
                ["name"] = mapName,
                ["id"] = NewId(),
                ["actions"] = new JArray(),
                ["bindings"] = new JArray(),
            };
            GetArray(document, "maps").Add(map);
            return map;
        }

        internal static bool RemoveMap(JObject document, string mapName)
        {
            JObject map = FindMap(document, mapName);
            if (map == null)
            {
                return false;
            }
            map.Remove();
            return true;
        }

        internal static JObject AddAction(
            JObject map,
            string actionName,
            string actionType,
            string expectedControlType,
            string interactions,
            string processors)
        {
            if (FindAction(map, actionName) != null)
            {
                throw new InvalidOperationException(
                    $"Action '{actionName}' already exists in map '{map.Value<string>("name")}'.");
            }

            var action = new JObject
            {
                ["name"] = actionName,
                ["type"] = string.IsNullOrWhiteSpace(actionType) ? "Button" : actionType,
                ["id"] = NewId(),
                ["expectedControlType"] = expectedControlType ?? string.Empty,
                ["processors"] = processors ?? string.Empty,
                ["interactions"] = interactions ?? string.Empty,
                ["initialStateCheck"] = true,
            };
            GetArray(map, "actions").Add(action);
            return action;
        }

        internal static bool RemoveAction(JObject map, string actionName)
        {
            JObject action = FindAction(map, actionName);
            if (action == null)
            {
                return false;
            }
            action.Remove();
            JArray bindings = GetArray(map, "bindings");
            foreach (JObject binding in bindings.OfType<JObject>()
                .Where(binding => string.Equals(
                    binding.Value<string>("action"),
                    actionName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                binding.Remove();
            }
            return true;
        }

        internal static JObject AddBinding(
            JObject map,
            string actionName,
            string bindingPath,
            string bindingName,
            string interactions,
            string processors,
            string groups,
            bool isComposite,
            bool isPartOfComposite)
        {
            if (FindAction(map, actionName) == null)
            {
                throw new InvalidOperationException(
                    $"Action '{actionName}' does not exist in map '{map.Value<string>("name")}'.");
            }

            var binding = new JObject
            {
                ["name"] = bindingName ?? string.Empty,
                ["id"] = NewId(),
                ["path"] = bindingPath,
                ["interactions"] = interactions ?? string.Empty,
                ["processors"] = processors ?? string.Empty,
                ["groups"] = groups ?? string.Empty,
                ["action"] = actionName,
                ["isComposite"] = isComposite,
                ["isPartOfComposite"] = isPartOfComposite,
            };
            GetArray(map, "bindings").Add(binding);
            return binding;
        }

        internal static bool RemoveBinding(JObject map, string actionName, int bindingIndex)
        {
            List<JObject> actionBindings = GetArray(map, "bindings")
                .OfType<JObject>()
                .Where(binding => string.Equals(
                    binding.Value<string>("action"),
                    actionName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (bindingIndex < 0 || bindingIndex >= actionBindings.Count)
            {
                return false;
            }
            actionBindings[bindingIndex].Remove();
            return true;
        }

        internal static JObject AddControlScheme(
            JObject document,
            string schemeName,
            string bindingGroup,
            JArray devices)
        {
            JArray schemes = GetArray(document, "controlSchemes");
            if (schemes.OfType<JObject>().Any(scheme => string.Equals(
                scheme.Value<string>("name"),
                schemeName,
                StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Control scheme '{schemeName}' already exists.");
            }

            var normalizedDevices = new JArray();
            if (devices != null)
            {
                foreach (JObject device in devices.OfType<JObject>())
                {
                    string devicePath = ReadString(device, "device_path", "devicePath");
                    if (string.IsNullOrWhiteSpace(devicePath))
                    {
                        throw new InvalidOperationException("Each control scheme device requires device_path.");
                    }
                    normalizedDevices.Add(new JObject
                    {
                        ["devicePath"] = devicePath,
                        ["isOptional"] = ReadBool(device, "optional", "isOptional"),
                        ["isOR"] = ReadBool(device, "or", "isOR"),
                    });
                }
            }

            var scheme = new JObject
            {
                ["name"] = schemeName,
                ["bindingGroup"] = string.IsNullOrWhiteSpace(bindingGroup) ? schemeName : bindingGroup,
                ["devices"] = normalizedDevices,
            };
            schemes.Add(scheme);
            return scheme;
        }

        internal static bool RemoveControlScheme(JObject document, string schemeName)
        {
            JObject scheme = GetArray(document, "controlSchemes")
                .OfType<JObject>()
                .FirstOrDefault(item => string.Equals(
                    item.Value<string>("name"),
                    schemeName,
                    StringComparison.OrdinalIgnoreCase));
            if (scheme == null)
            {
                return false;
            }
            scheme.Remove();
            return true;
        }

        internal static List<InputValidationIssue> Validate(string assetPath, JObject document)
        {
            var issues = new List<InputValidationIssue>();
            if (string.IsNullOrWhiteSpace(document.Value<string>("name")))
            {
                issues.Add(ValidationIssue("error", "ASSET_NAME_MISSING", "$", "Asset name is missing."));
            }

            JArray maps = document["maps"] as JArray;
            if (maps == null)
            {
                issues.Add(ValidationIssue("error", "MAPS_MISSING", "$.maps", "maps must be an array."));
                maps = new JArray();
            }

            var mapNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int mapIndex = 0; mapIndex < maps.Count; mapIndex++)
            {
                var map = maps[mapIndex] as JObject;
                if (map == null)
                {
                    issues.Add(ValidationIssue("error", "MAP_INVALID", $"$.maps[{mapIndex}]", "Map must be an object."));
                    continue;
                }
                string mapName = map.Value<string>("name");
                if (string.IsNullOrWhiteSpace(mapName))
                {
                    issues.Add(ValidationIssue("error", "MAP_NAME_MISSING", $"$.maps[{mapIndex}]", "Map name is missing."));
                }
                else if (!mapNames.Add(mapName))
                {
                    issues.Add(ValidationIssue("error", "DUPLICATE_MAP_NAME", $"$.maps[{mapIndex}]", $"Duplicate map name '{mapName}'."));
                }
                ValidateId(map.Value<string>("id"), $"$.maps[{mapIndex}].id", ids, issues);

                JArray actions = map["actions"] as JArray ?? new JArray();
                var actionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int actionIndex = 0; actionIndex < actions.Count; actionIndex++)
                {
                    var action = actions[actionIndex] as JObject;
                    if (action == null) continue;
                    string actionName = action.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(actionName))
                    {
                        issues.Add(ValidationIssue("error", "ACTION_NAME_MISSING", $"$.maps[{mapIndex}].actions[{actionIndex}]", "Action name is missing."));
                    }
                    else if (!actionNames.Add(actionName))
                    {
                        issues.Add(ValidationIssue("error", "DUPLICATE_ACTION_NAME", $"$.maps[{mapIndex}].actions[{actionIndex}]", $"Duplicate action name '{actionName}'."));
                    }
                    ValidateId(action.Value<string>("id"), $"$.maps[{mapIndex}].actions[{actionIndex}].id", ids, issues);
                }

                JArray bindings = map["bindings"] as JArray ?? new JArray();
                for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
                {
                    var binding = bindings[bindingIndex] as JObject;
                    if (binding == null) continue;
                    string actionName = binding.Value<string>("action");
                    if (string.IsNullOrWhiteSpace(actionName) || !actionNames.Contains(actionName))
                    {
                        issues.Add(ValidationIssue("error", "BINDING_ACTION_MISSING", $"$.maps[{mapIndex}].bindings[{bindingIndex}]", $"Binding references unknown action '{actionName}'."));
                    }
                    if (string.IsNullOrWhiteSpace(binding.Value<string>("path")))
                    {
                        issues.Add(ValidationIssue("error", "BINDING_PATH_MISSING", $"$.maps[{mapIndex}].bindings[{bindingIndex}]", "Binding path is missing."));
                    }
                    ValidateId(binding.Value<string>("id"), $"$.maps[{mapIndex}].bindings[{bindingIndex}].id", ids, issues);
                }
            }

            JArray schemes = document["controlSchemes"] as JArray;
            if (schemes == null)
            {
                issues.Add(ValidationIssue("error", "CONTROL_SCHEMES_MISSING", "$.controlSchemes", "controlSchemes must be an array."));
            }
            else
            {
                var schemeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JObject scheme in schemes.OfType<JObject>())
                {
                    string schemeName = scheme.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(schemeName))
                    {
                        issues.Add(ValidationIssue("error", "CONTROL_SCHEME_NAME_MISSING", "$.controlSchemes", "Control scheme name is missing."));
                    }
                    else if (!schemeNames.Add(schemeName))
                    {
                        issues.Add(ValidationIssue("error", "DUPLICATE_CONTROL_SCHEME", "$.controlSchemes", $"Duplicate control scheme '{schemeName}'."));
                    }
                }
            }

            if (IsInputSystemInstalled)
            {
                UnityEngine.Object imported = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (imported == null || !InputActionAssetType.IsInstanceOfType(imported))
                {
                    issues.Add(ValidationIssue(
                        "error",
                        "INPUT_ACTION_IMPORT_FAILED",
                        assetPath,
                        "Input System did not import this file as an InputActionAsset. Inspect the Console for importer errors."));
                }
            }
            return issues;
        }

        internal static bool ConfigureGeneratedWrapper(
            string assetPath,
            string outputPath,
            string className,
            string codeNamespace,
            out string error)
        {
            error = null;
            AssetImporter importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null)
            {
                error = $"No importer is available for '{assetPath}'.";
                return false;
            }

            var serialized = new SerializedObject(importer);
            SerializedProperty generate = serialized.FindProperty("m_GenerateWrapperCode");
            SerializedProperty codePath = serialized.FindProperty("m_WrapperCodePath");
            SerializedProperty wrapperClass = serialized.FindProperty("m_WrapperClassName");
            SerializedProperty wrapperNamespace = serialized.FindProperty("m_WrapperCodeNamespace");
            if (generate == null || codePath == null || wrapperClass == null || wrapperNamespace == null)
            {
                error = "Installed Input System importer does not expose wrapper generation settings.";
                return false;
            }

            Undo.RecordObject(importer, "Configure Input Actions C# Wrapper");
            generate.boolValue = true;
            if (outputPath != null) codePath.stringValue = outputPath;
            if (className != null) wrapperClass.stringValue = className;
            if (codeNamespace != null) wrapperNamespace.stringValue = codeNamespace;
            serialized.ApplyModifiedProperties();
            importer.SaveAndReimport();
            return true;
        }

        internal static bool AssignPlayerInput(
            string assetPath,
            string target,
            string defaultMap,
            string defaultScheme,
            string notificationBehavior,
            out object data,
            out string error)
        {
            data = null;
            error = null;
            Type playerInputType = FindType(PlayerInputTypeName);
            if (playerInputType == null)
            {
                error = "PlayerInput type is unavailable. Install com.unity.inputsystem.";
                return false;
            }

            UnityEngine.Object actions = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (actions == null || !InputActionAssetType.IsInstanceOfType(actions))
            {
                error = $"'{assetPath}' is not an imported InputActionAsset.";
                return false;
            }

            GameObject gameObject = ManageGameObjectCommon.FindObjectInternal(
                new JValue(target),
                "by_id_or_name_or_path");
            if (gameObject == null)
            {
                error = $"Target GameObject '{target}' was not found.";
                return false;
            }

            Component component = gameObject.GetComponent(playerInputType);
            bool added = component == null;
            if (component == null)
            {
                component = Undo.AddComponent(gameObject, playerInputType);
            }
            Undo.RecordObject(component, "Configure PlayerInput");
            var serialized = new SerializedObject(component);
            SerializedProperty actionsProperty = serialized.FindProperty("m_Actions");
            SerializedProperty mapProperty = serialized.FindProperty("m_DefaultActionMap");
            SerializedProperty schemeProperty = serialized.FindProperty("m_DefaultControlScheme");
            SerializedProperty behaviorProperty = serialized.FindProperty("m_NotificationBehavior");
            if (actionsProperty == null || mapProperty == null || schemeProperty == null || behaviorProperty == null)
            {
                error = "Installed PlayerInput component has an unsupported serialized layout.";
                return false;
            }

            int behaviorIndex;
            if (!TryNotificationBehaviorIndex(notificationBehavior, out behaviorIndex))
            {
                error = "notification_behavior must be SendMessages, BroadcastMessages, InvokeUnityEvents, or InvokeCSharpEvents.";
                return false;
            }

            actionsProperty.objectReferenceValue = actions;
            if (defaultMap != null) mapProperty.stringValue = defaultMap;
            if (defaultScheme != null) schemeProperty.stringValue = defaultScheme;
            behaviorProperty.enumValueIndex = behaviorIndex;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(component);
            data = new
            {
                target = gameObject.name,
                instanceId = gameObject.GetInstanceID(),
                componentAdded = added,
                actions = assetPath,
                defaultMap,
                defaultScheme,
                notificationBehavior = string.IsNullOrWhiteSpace(notificationBehavior)
                    ? "SendMessages"
                    : notificationBehavior,
            };
            return true;
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        private static JArray GetArray(JObject owner, string name)
        {
            var array = owner[name] as JArray;
            if (array == null)
            {
                array = new JArray();
                owner[name] = array;
            }
            return array;
        }

        private static string NewId()
        {
            return Guid.NewGuid().ToString();
        }

        private static string ReadString(JObject owner, string snakeName, string camelName)
        {
            return owner.Value<string>(snakeName) ?? owner.Value<string>(camelName);
        }

        private static bool ReadBool(JObject owner, string firstName, string secondName)
        {
            JToken token = owner[firstName] ?? owner[secondName];
            return token != null && token.Type != JTokenType.Null && token.Value<bool>();
        }

        private static InputValidationIssue ValidationIssue(
            string severity,
            string code,
            string location,
            string message)
        {
            return new InputValidationIssue
            {
                Severity = severity,
                Code = code,
                Location = location,
                Message = message,
            };
        }

        private static void ValidateId(
            string id,
            string location,
            HashSet<string> ids,
            List<InputValidationIssue> issues)
        {
            Guid parsed;
            if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out parsed))
            {
                issues.Add(ValidationIssue("error", "INVALID_ID", location, "ID must be a GUID."));
            }
            else if (!ids.Add(parsed.ToString()))
            {
                issues.Add(ValidationIssue("error", "DUPLICATE_ID", location, $"Duplicate ID '{id}'."));
            }
        }

        private static bool TryNotificationBehaviorIndex(string behavior, out int index)
        {
            string normalized = string.IsNullOrWhiteSpace(behavior) ? "SendMessages" : behavior;
            string[] values =
            {
                "SendMessages",
                "BroadcastMessages",
                "InvokeUnityEvents",
                "InvokeCSharpEvents",
            };
            for (int valueIndex = 0; valueIndex < values.Length; valueIndex++)
            {
                if (values[valueIndex].Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    index = valueIndex;
                    return true;
                }
            }
            index = 0;
            return false;
        }
    }
}
