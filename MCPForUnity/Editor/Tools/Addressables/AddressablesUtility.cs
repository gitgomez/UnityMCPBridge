using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Tools.Addressables
{
    internal sealed class AddressablesValidationIssue
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

    internal sealed class AddressableEntryLookup
    {
        internal object Group { get; set; }
        internal object Entry { get; set; }
    }

    internal static class AddressablesUtility
    {
        private const string DefaultObjectTypeName =
            "UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject";
        private const string SettingsTypeName =
            "UnityEditor.AddressableAssets.Settings.AddressableAssetSettings";
        private const string BundledSchemaTypeName =
            "UnityEditor.AddressableAssets.Settings.GroupSchemas.BundledAssetGroupSchema";
        private const string ContentUpdateSchemaTypeName =
            "UnityEditor.AddressableAssets.Settings.GroupSchemas.ContentUpdateGroupSchema";
        private const string ProjectConfigDataTypeName =
            "UnityEditor.AddressableAssets.Settings.ProjectConfigData";

        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;

        internal static Type DefaultObjectType => FindType(DefaultObjectTypeName);
        internal static Type SettingsType => FindType(SettingsTypeName);
        internal static bool IsInstalled => DefaultObjectType != null && SettingsType != null;

        internal static string GetPackageVersion()
        {
            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .FirstOrDefault(item => item != null
                        && item.name.Equals("com.unity.addressables", StringComparison.OrdinalIgnoreCase));
            return package != null ? package.version : null;
        }

        internal static object GetSettings(bool create)
        {
            Type type = DefaultObjectType;
            if (type == null)
            {
                return null;
            }

            MethodInfo method = type.GetMethod(
                "GetSettings",
                PublicStatic,
                null,
                new[] { typeof(bool) },
                null);
            if (method == null)
            {
                throw new MissingMethodException(type.FullName, "GetSettings(bool)");
            }
            return method.Invoke(null, new object[] { create });
        }

        internal static string GetSettingsPath(object settings)
        {
            var unityObject = settings as UnityEngine.Object;
            return unityObject != null ? AssetDatabase.GetAssetPath(unityObject) : null;
        }

        internal static List<object> GetGroups(object settings)
        {
            return Enumerate(GetMemberValue(settings, "groups"))
                .Where(item => item != null)
                .ToList();
        }

        internal static object GetDefaultGroup(object settings)
        {
            return GetMemberValue(settings, "DefaultGroup");
        }

        internal static object FindGroup(object settings, string groupName)
        {
            if (settings == null || string.IsNullOrWhiteSpace(groupName))
            {
                return null;
            }
            return GetGroups(settings).FirstOrDefault(group => string.Equals(
                GetString(group, "Name"),
                groupName,
                StringComparison.OrdinalIgnoreCase));
        }

        internal static List<object> GetEntries(object group)
        {
            return Enumerate(GetMemberValue(group, "entries"))
                .Where(item => item != null)
                .ToList();
        }

        internal static object DescribeGroup(object settings, object group)
        {
            object defaultGroup = GetDefaultGroup(settings);
            List<object> entries = GetEntries(group);
            List<string> schemas = Enumerate(GetMemberValue(group, "Schemas"))
                .Where(item => item != null)
                .Select(item => item.GetType().FullName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new
            {
                name = GetString(group, "Name"),
                guid = GetString(group, "Guid"),
                readOnly = GetBool(group, "ReadOnly"),
                isDefault = ReferenceEquals(defaultGroup, group),
                entryCount = entries.Count,
                schemas,
            };
        }

        internal static object DescribeEntry(object group, object entry)
        {
            string guid = GetString(entry, "guid");
            string assetPath = GetString(entry, "AssetPath");
            if (string.IsNullOrWhiteSpace(assetPath) && !string.IsNullOrWhiteSpace(guid))
            {
                assetPath = AssetDatabase.GUIDToAssetPath(guid);
            }
            List<string> labels = Enumerate(GetMemberValue(entry, "labels"))
                .Select(item => item?.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new
            {
                group = GetString(group, "Name"),
                guid,
                assetPath,
                address = GetString(entry, "address"),
                labels,
                assetExists = !string.IsNullOrWhiteSpace(assetPath)
                    && (!string.IsNullOrWhiteSpace(AssetDatabase.AssetPathToGUID(assetPath))
                        || AssetDatabase.IsValidFolder(assetPath)),
            };
        }

        internal static object CreateGroup(
            object settings,
            string groupName,
            bool setDefault)
        {
            if (FindGroup(settings, groupName) != null)
            {
                throw new InvalidOperationException($"Addressables group '{groupName}' already exists.");
            }

            MethodInfo method = settings.GetType().GetMethods(PublicInstance)
                .FirstOrDefault(candidate => candidate.Name == "CreateGroup"
                    && candidate.GetParameters().Length == 6);
            if (method == null)
            {
                throw new MissingMethodException(settings.GetType().FullName, "CreateGroup");
            }
            var schemaTypes = new List<Type>();
            Type contentSchema = FindType(ContentUpdateSchemaTypeName);
            Type bundledSchema = FindType(BundledSchemaTypeName);
            if (contentSchema != null) schemaTypes.Add(contentSchema);
            if (bundledSchema != null) schemaTypes.Add(bundledSchema);
            object group = method.Invoke(settings, new object[]
            {
                groupName,
                setDefault,
                false,
                true,
                null,
                schemaTypes.ToArray(),
            });
            Save(settings);
            return group;
        }

        internal static void SetDefaultGroup(object settings, object group)
        {
            SetMemberValue(settings, "DefaultGroup", group);
            Save(settings);
        }

        internal static void RemoveGroup(object settings, object group)
        {
            MethodInfo method = settings.GetType().GetMethods(PublicInstance)
                .FirstOrDefault(candidate => candidate.Name == "RemoveGroup"
                    && candidate.GetParameters().Length == 1);
            if (method == null)
            {
                throw new MissingMethodException(settings.GetType().FullName, "RemoveGroup");
            }
            method.Invoke(settings, new[] { group });
            Save(settings);
        }

        internal static object AddEntry(
            object settings,
            object group,
            string guid,
            string address,
            IEnumerable<string> labels)
        {
            MethodInfo method = settings.GetType().GetMethods(PublicInstance)
                .FirstOrDefault(candidate => candidate.Name == "CreateOrMoveEntry"
                    && candidate.GetParameters().Length == 4
                    && candidate.GetParameters()[0].ParameterType == typeof(string));
            if (method == null)
            {
                throw new MissingMethodException(settings.GetType().FullName, "CreateOrMoveEntry");
            }
            object entry = method.Invoke(settings, new object[] { guid, group, false, true });
            if (entry == null)
            {
                throw new InvalidOperationException($"Addressables rejected asset GUID '{guid}'.");
            }
            if (address != null)
            {
                SetMemberValue(entry, "address", address);
            }
            foreach (string label in labels ?? Enumerable.Empty<string>())
            {
                AddLabel(settings, label);
                SetEntryLabel(entry, label, true, true);
            }
            Save(settings);
            return entry;
        }

        internal static bool RemoveEntry(object settings, string guid)
        {
            MethodInfo method = settings.GetType().GetMethods(PublicInstance)
                .FirstOrDefault(candidate => candidate.Name == "RemoveAssetEntry"
                    && candidate.GetParameters().Length == 2
                    && candidate.GetParameters()[0].ParameterType == typeof(string));
            if (method == null)
            {
                throw new MissingMethodException(settings.GetType().FullName, "RemoveAssetEntry");
            }
            bool removed = Convert.ToBoolean(method.Invoke(settings, new object[] { guid, true }));
            if (removed) Save(settings);
            return removed;
        }

        internal static void SetAddress(object settings, object entry, string address)
        {
            SetMemberValue(entry, "address", address);
            Save(settings);
        }

        internal static List<string> GetLabels(object settings)
        {
            MethodInfo method = settings.GetType().GetMethod(
                "GetLabels", PublicInstance, null, Type.EmptyTypes, null);
            if (method == null)
            {
                throw new MissingMethodException(settings.GetType().FullName, "GetLabels");
            }
            return Enumerate(method.Invoke(settings, null))
                .Select(item => item?.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static void AddLabel(object settings, string label)
        {
            if (GetLabels(settings).Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            InvokeStringBool(settings, "AddLabel", label, true);
            Save(settings);
        }

        internal static void RemoveLabel(object settings, string label)
        {
            InvokeStringBool(settings, "RemoveLabel", label, true);
            Save(settings);
        }

        internal static void SetEntryLabel(
            object settings,
            object entry,
            string label,
            bool enabled)
        {
            if (enabled)
            {
                AddLabel(settings, label);
            }
            SetEntryLabel(entry, label, enabled, false);
            Save(settings);
        }

        private static void SetEntryLabel(object entry, string label, bool enabled, bool force)
        {
            MethodInfo method = entry.GetType().GetMethods(PublicInstance)
                .FirstOrDefault(candidate => candidate.Name == "SetLabel"
                    && candidate.GetParameters().Length == 4);
            if (method == null)
            {
                throw new MissingMethodException(entry.GetType().FullName, "SetLabel");
            }
            method.Invoke(entry, new object[] { label, enabled, force, true });
        }

        internal static AddressableEntryLookup FindEntry(
            object settings,
            string guid,
            string assetPath,
            string address)
        {
            string resolvedGuid = guid?.Trim();
            if (string.IsNullOrWhiteSpace(resolvedGuid) && !string.IsNullOrWhiteSpace(assetPath))
            {
                resolvedGuid = AssetDatabase.AssetPathToGUID(NormalizeAssetPath(assetPath));
            }

            foreach (object group in GetGroups(settings))
            {
                foreach (object entry in GetEntries(group))
                {
                    bool matches = !string.IsNullOrWhiteSpace(resolvedGuid)
                        ? string.Equals(GetString(entry, "guid"), resolvedGuid, StringComparison.OrdinalIgnoreCase)
                        : !string.IsNullOrWhiteSpace(address)
                            && string.Equals(GetString(entry, "address"), address, StringComparison.OrdinalIgnoreCase);
                    if (matches)
                    {
                        return new AddressableEntryLookup { Group = group, Entry = entry };
                    }
                }
            }
            return null;
        }

        internal static string NormalizeAssetPath(string path)
        {
            return path?.Trim().Replace('\\', '/');
        }

        internal static bool IsValidAssetPath(string path, out string normalized, out string guid)
        {
            normalized = NormalizeAssetPath(path);
            guid = null;
            if (string.IsNullOrWhiteSpace(normalized)
                || !normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            guid = AssetDatabase.AssetPathToGUID(normalized);
            return !string.IsNullOrWhiteSpace(guid);
        }

        internal static List<object> GetProfileSummaries(object settings)
        {
            object profiles = GetMemberValue(settings, "profileSettings");
            if (profiles == null)
            {
                return new List<object>();
            }
            string activeId = GetString(settings, "activeProfileId");
            List<string> variableNames = InvokeStringList(profiles, "GetVariableNames");
            List<string> profileNames = InvokeStringList(profiles, "GetAllProfileNames");
            var result = new List<object>();
            foreach (string profileName in profileNames.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                string profileId = InvokeString(profiles, "GetProfileId", profileName);
                var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string variableName in variableNames)
                {
                    values[variableName] = InvokeString(
                        profiles,
                        "GetValueByName",
                        profileId,
                        variableName);
                }
                result.Add(new
                {
                    name = profileName,
                    id = profileId,
                    active = string.Equals(profileId, activeId, StringComparison.Ordinal),
                    values,
                });
            }
            return result;
        }

        internal static void SetProfileValue(
            object settings,
            string profileName,
            string variableName,
            string value,
            bool createVariable,
            out string profileId)
        {
            object profiles = GetMemberValue(settings, "profileSettings");
            if (profiles == null)
            {
                throw new InvalidOperationException("Addressables profile settings are unavailable.");
            }
            profileId = string.IsNullOrWhiteSpace(profileName)
                ? GetString(settings, "activeProfileId")
                : InvokeString(profiles, "GetProfileId", profileName);
            if (string.IsNullOrWhiteSpace(profileId))
            {
                throw new InvalidOperationException($"Addressables profile '{profileName}' does not exist.");
            }

            List<string> variableNames = InvokeStringList(profiles, "GetVariableNames");
            if (!variableNames.Contains(variableName, StringComparer.OrdinalIgnoreCase))
            {
                if (!createVariable)
                {
                    throw new InvalidOperationException(
                        $"Profile variable '{variableName}' does not exist. Set create_variable=true to create it.");
                }
                InvokeString(profiles, "CreateValue", variableName, value);
            }
            InvokeVoid(profiles, "SetValue", profileId, variableName, value);
            Save(settings);
        }

        internal static List<AddressablesValidationIssue> Validate(object settings)
        {
            var issues = new List<AddressablesValidationIssue>();
            if (settings == null)
            {
                issues.Add(Issue(
                    "error",
                    "SETTINGS_MISSING",
                    "Assets/AddressableAssetsData",
                    "Addressables settings have not been initialized."));
                return issues;
            }

            List<object> groups = GetGroups(settings);
            if (groups.Count == 0)
            {
                issues.Add(Issue("error", "NO_GROUPS", "groups", "No Addressables groups exist."));
            }
            var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entryGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addresses = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var definedLabels = new HashSet<string>(GetLabels(settings), StringComparer.OrdinalIgnoreCase);
            foreach (object group in groups)
            {
                string groupName = GetString(group, "Name") ?? "<unnamed>";
                bool readOnly = GetBool(group, "ReadOnly");
                if (!groupNames.Add(groupName))
                {
                    issues.Add(Issue("error", "DUPLICATE_GROUP_NAME", groupName,
                        $"Duplicate group name '{groupName}'."));
                }
                if (!readOnly
                    && !Enumerate(GetMemberValue(group, "Schemas")).Any())
                {
                    issues.Add(Issue("warning", "GROUP_WITHOUT_SCHEMAS", groupName,
                        "Writable group has no build schemas."));
                }

                // Addressables owns special entries such as Resources and EditorSceneList
                // in read-only groups. Their identifiers intentionally are not asset GUIDs.
                if (readOnly)
                {
                    continue;
                }

                foreach (object entry in GetEntries(group))
                {
                    string guid = GetString(entry, "guid");
                    string path = GetString(entry, "AssetPath");
                    string address = GetString(entry, "address");
                    string location = $"{groupName}/{address ?? guid ?? "<entry>"}";
                    if (string.IsNullOrWhiteSpace(guid) || !entryGuids.Add(guid))
                    {
                        issues.Add(Issue("error", "INVALID_OR_DUPLICATE_GUID", location,
                            $"Entry GUID '{guid}' is empty or duplicated."));
                    }
                    if (string.IsNullOrWhiteSpace(path)
                        || string.IsNullOrWhiteSpace(AssetDatabase.AssetPathToGUID(path)))
                    {
                        issues.Add(Issue("error", "MISSING_ASSET", location,
                            $"Entry asset '{path}' does not exist."));
                    }
                    if (string.IsNullOrWhiteSpace(address))
                    {
                        issues.Add(Issue("warning", "EMPTY_ADDRESS", location,
                            "Entry has an empty runtime address."));
                    }
                    else
                    {
                        List<string> locations;
                        if (!addresses.TryGetValue(address, out locations))
                        {
                            locations = new List<string>();
                            addresses[address] = locations;
                        }
                        locations.Add(location);
                    }
                    foreach (string label in Enumerate(GetMemberValue(entry, "labels"))
                        .Select(item => item?.ToString())
                        .Where(item => !string.IsNullOrWhiteSpace(item)))
                    {
                        if (!definedLabels.Contains(label))
                        {
                            issues.Add(Issue("warning", "UNDEFINED_LABEL", location,
                                $"Entry uses undefined label '{label}'."));
                        }
                    }
                }
            }
            foreach (KeyValuePair<string, List<string>> pair in addresses.Where(pair => pair.Value.Count > 1))
            {
                issues.Add(Issue("error", "DUPLICATE_ADDRESS", pair.Key,
                    $"Runtime address '{pair.Key}' is used by {pair.Value.Count} entries."));
            }
            return issues;
        }

        internal static object BuildPlayerContent()
        {
            List<string> dirtyScenes = GetDirtySceneNames();
            if (dirtyScenes.Count > 0)
            {
                throw new InvalidOperationException(
                    "Addressables content cannot be built while scenes have unsaved changes. "
                    + "Save or discard these scenes first: " + string.Join(", ", dirtyScenes));
            }
            SuppressInteractiveBuildPrompts();
            MethodInfo method = SettingsType.GetMethods(PublicStatic)
                .FirstOrDefault(candidate => candidate.Name == "BuildPlayerContent"
                    && candidate.GetParameters().Length == 1
                    && candidate.GetParameters()[0].ParameterType.IsByRef);
            if (method == null)
            {
                throw new MissingMethodException(SettingsType.FullName, "BuildPlayerContent(out result)");
            }
            var arguments = new object[] { null };
            method.Invoke(null, arguments);
            object result = arguments[0];
            string error = GetString(result, "Error");
            return new
            {
                succeeded = string.IsNullOrWhiteSpace(error),
                error,
                duration = GetMemberValue(result, "Duration"),
                outputPath = GetString(result, "OutputPath"),
                locationCount = GetMemberValue(result, "LocationCount"),
            };
        }

        private static void SuppressInteractiveBuildPrompts()
        {
            // Addressables 2.x displays a first-build modal asking whether to enable the build
            // report. MCP builds may run in a background/headless editor where that dialog is
            // invisible and blocks Unity indefinitely. Mark only that informational prompt as
            // handled; do not alter unrelated migration choices or report-window preferences.
            Type type = FindType(ProjectConfigDataTypeName);
            PropertyInfo informed = type?.GetProperty(
                "UserHasBeenInformedAboutBuildReportSettingPreBuild",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (informed != null
                && informed.CanWrite
                && informed.PropertyType == typeof(bool))
            {
                informed.SetValue(null, true);
            }
        }

        internal static List<string> GetDirtySceneNames()
        {
            var dirtyScenes = new List<string>();
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (!scene.isDirty) continue;

                dirtyScenes.Add(string.IsNullOrWhiteSpace(scene.path)
                    ? string.IsNullOrWhiteSpace(scene.name) ? "<untitled>" : scene.name
                    : scene.path);
            }
            return dirtyScenes;
        }

        internal static void Save(object settings)
        {
            var unityObject = settings as UnityEngine.Object;
            if (unityObject != null)
            {
                EditorUtility.SetDirty(unityObject);
            }
            AssetDatabase.SaveAssets();
        }

        private static void InvokeStringBool(object target, string name, string value, bool flag)
        {
            MethodInfo method = target.GetType().GetMethods(PublicInstance)
                .FirstOrDefault(candidate => candidate.Name == name
                    && candidate.GetParameters().Length == 2
                    && candidate.GetParameters()[0].ParameterType == typeof(string));
            if (method == null)
            {
                throw new MissingMethodException(target.GetType().FullName, name);
            }
            method.Invoke(target, new object[] { value, flag });
        }

        private static List<string> InvokeStringList(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName, PublicInstance, null, Type.EmptyTypes, null);
            if (method == null)
            {
                throw new MissingMethodException(target.GetType().FullName, methodName);
            }
            return Enumerate(method.Invoke(target, null))
                .Select(item => item?.ToString())
                .Where(item => item != null)
                .ToList();
        }

        private static string InvokeString(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = FindMethod(target, methodName, arguments.Length);
            object result = method.Invoke(target, arguments);
            return result?.ToString();
        }

        private static void InvokeVoid(object target, string methodName, params object[] arguments)
        {
            FindMethod(target, methodName, arguments.Length).Invoke(target, arguments);
        }

        private static MethodInfo FindMethod(object target, string name, int argumentCount)
        {
            MethodInfo method = target.GetType().GetMethods(PublicInstance)
                .FirstOrDefault(candidate => candidate.Name == name
                    && candidate.GetParameters().Length == argumentCount);
            if (method == null)
            {
                throw new MissingMethodException(target.GetType().FullName, name);
            }
            return method;
        }

        private static object GetMemberValue(object target, string name)
        {
            if (target == null) return null;
            PropertyInfo property = target.GetType().GetProperties(PublicInstance)
                .FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (property != null) return property.GetValue(target, null);
            FieldInfo field = target.GetType().GetFields(PublicInstance)
                .FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return field != null ? field.GetValue(target) : null;
        }

        private static void SetMemberValue(object target, string name, object value)
        {
            PropertyInfo property = target.GetType().GetProperties(PublicInstance)
                .FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && item.CanWrite);
            if (property != null)
            {
                property.SetValue(target, value, null);
                return;
            }
            FieldInfo field = target.GetType().GetFields(PublicInstance)
                .FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }
            throw new MissingMemberException(target.GetType().FullName, name);
        }

        private static string GetString(object target, string name)
        {
            return GetMemberValue(target, name)?.ToString();
        }

        private static bool GetBool(object target, string name)
        {
            object value = GetMemberValue(target, name);
            return value != null && Convert.ToBoolean(value);
        }

        private static IEnumerable<object> Enumerate(object value)
        {
            var enumerable = value as IEnumerable;
            if (enumerable == null) yield break;
            foreach (object item in enumerable)
            {
                yield return item;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        private static AddressablesValidationIssue Issue(
            string severity,
            string code,
            string location,
            string message)
        {
            return new AddressablesValidationIssue
            {
                Severity = severity,
                Code = code,
                Location = location,
                Message = message,
            };
        }
    }
}
