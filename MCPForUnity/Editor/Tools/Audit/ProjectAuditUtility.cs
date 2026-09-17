using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Tools.Dependencies;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Tools.Audit
{
    internal sealed class ProjectAuditIssue
    {
        [JsonProperty("check")]
        public string Check { get; set; }

        [JsonProperty("severity")]
        public string Severity { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("assetPath", NullValueHandling = NullValueHandling.Ignore)]
        public string AssetPath { get; set; }

        [JsonProperty("objectPath", NullValueHandling = NullValueHandling.Ignore)]
        public string ObjectPath { get; set; }

        [JsonProperty("details", NullValueHandling = NullValueHandling.Ignore)]
        public object Details { get; set; }
    }

    internal sealed class ProjectAuditRun
    {
        public List<ProjectAuditIssue> Issues { get; } = new List<ProjectAuditIssue>();
        public List<string> ChecksRun { get; } = new List<string>();
        public int AssetsScanned { get; set; }
        public int MetaFilesScanned { get; set; }
        public bool AssetScanTruncated { get; set; }
        public bool MetaScanTruncated { get; set; }
        public long DurationMs { get; set; }
    }

    internal static class ProjectAuditUtility
    {
        internal static readonly string[] AllChecks =
        {
            "compilation",
            "build_scenes",
            "missing_references",
            "duplicate_guids",
            "orphaned_meta_files",
            "package_health",
            "loaded_scene_structure",
        };

        internal static readonly Dictionary<string, string> CheckDescriptions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "compilation", "Compilation failure, active compilation, and pending script update state." },
                { "build_scenes", "Missing, duplicate, disabled, or absent scenes in EditorBuildSettings." },
                { "missing_references", "Missing scripts and unresolved serialized object references in assets." },
                { "duplicate_guids", "Duplicate GUID declarations in .meta files below the search root." },
                { "orphaned_meta_files", ".meta files whose corresponding asset or folder is absent." },
                { "package_health", "Unresolved manifest dependencies and missing local file packages." },
                { "loaded_scene_structure", "Missing scripts and multiple active AudioListeners in loaded scenes." },
            };

        internal static ProjectAuditRun Run(
            IEnumerable<string> checks,
            string searchRoot,
            bool includeScenes,
            int scanLimit)
        {
            var stopwatch = Stopwatch.StartNew();
            var run = new ProjectAuditRun();
            foreach (string check in checks)
            {
                run.ChecksRun.Add(check);
                switch (check)
                {
                    case "compilation":
                        CheckCompilation(run);
                        break;
                    case "build_scenes":
                        CheckBuildScenes(run);
                        break;
                    case "missing_references":
                        CheckMissingReferences(run, searchRoot, includeScenes, scanLimit);
                        break;
                    case "duplicate_guids":
                    case "orphaned_meta_files":
                        CheckMetaFiles(run, searchRoot, scanLimit, check);
                        break;
                    case "package_health":
                        CheckPackageHealth(run);
                        break;
                    case "loaded_scene_structure":
                        CheckLoadedSceneStructure(run);
                        break;
                }
            }

            run.Issues.Sort(CompareIssues);
            stopwatch.Stop();
            run.DurationMs = stopwatch.ElapsedMilliseconds;
            return run;
        }

        internal static int SeverityRank(string severity)
        {
            switch ((severity ?? string.Empty).ToLowerInvariant())
            {
                case "error": return 3;
                case "warning": return 2;
                case "info": return 1;
                default: return 0;
            }
        }

        private static void CheckCompilation(ProjectAuditRun run)
        {
            if (EditorUtility.scriptCompilationFailed)
            {
                run.Issues.Add(Issue(
                    "compilation",
                    "error",
                    "SCRIPT_COMPILATION_FAILED",
                    "Unity reports script compilation failures. Inspect the Console before making further changes."));
            }
            if (EditorApplication.isCompiling)
            {
                run.Issues.Add(Issue(
                    "compilation",
                    "warning",
                    "COMPILATION_IN_PROGRESS",
                    "Unity is currently compiling scripts; audit results may be incomplete."));
            }
            if (EditorApplication.isUpdating)
            {
                run.Issues.Add(Issue(
                    "compilation",
                    "info",
                    "ASSET_DATABASE_UPDATING",
                    "Unity's AssetDatabase is currently updating."));
            }
        }

        private static void CheckBuildScenes(ProjectAuditRun run)
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes ?? Array.Empty<EditorBuildSettingsScene>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int enabledCount = 0;
            foreach (EditorBuildSettingsScene scene in scenes)
            {
                if (scene == null || string.IsNullOrWhiteSpace(scene.path))
                {
                    run.Issues.Add(Issue(
                        "build_scenes",
                        "error",
                        "EMPTY_BUILD_SCENE_PATH",
                        "Build Settings contains an entry with no scene path."));
                    continue;
                }

                if (!seen.Add(scene.path))
                {
                    run.Issues.Add(Issue(
                        "build_scenes",
                        "warning",
                        "DUPLICATE_BUILD_SCENE",
                        "Scene appears more than once in Build Settings.",
                        scene.path));
                }

                if (scene.enabled)
                {
                    enabledCount++;
                }

                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path) == null)
                {
                    run.Issues.Add(Issue(
                        "build_scenes",
                        "error",
                        "BUILD_SCENE_MISSING",
                        "Build Settings references a scene asset that cannot be loaded.",
                        scene.path));
                }
            }

            if (enabledCount == 0)
            {
                run.Issues.Add(Issue(
                    "build_scenes",
                    "warning",
                    "NO_ENABLED_BUILD_SCENES",
                    "No enabled scenes are configured in Build Settings."));
            }
        }

        private static void CheckMissingReferences(
            ProjectAuditRun run,
            string searchRoot,
            bool includeScenes,
            int scanLimit)
        {
            string normalizedRoot = NormalizeAssetsRoot(searchRoot);
            string[] supportedExtensions =
            {
                ".prefab", ".asset", ".mat", ".controller", ".overridecontroller", ".playable", ".anim"
            };
            var candidates = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase)
                    || path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                .Where(path => !AssetDatabase.IsValidFolder(path))
                .Where(path => includeScenes && path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                    || supportedExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            run.AssetScanTruncated |= candidates.Count > scanLimit;
            foreach (string path in candidates.Take(scanLimit))
            {
                run.AssetsScanned++;
                MissingReferenceScanResult scan = DependencyInspectionUtility.FindMissingReferences(path);
                foreach (MissingReferenceIssue missing in scan.Issues)
                {
                    run.Issues.Add(new ProjectAuditIssue
                    {
                        Check = "missing_references",
                        Severity = "error",
                        Code = missing.Kind == "missing_script"
                            ? "MISSING_SCRIPT"
                            : "MISSING_OBJECT_REFERENCE",
                        Message = missing.Message,
                        AssetPath = missing.AssetPath,
                        ObjectPath = missing.ObjectPath,
                        Details = string.IsNullOrEmpty(missing.PropertyPath)
                            ? null
                            : new { propertyPath = missing.PropertyPath, ownerType = missing.OwnerType },
                    });
                }
                foreach (string warning in scan.Warnings)
                {
                    run.Issues.Add(Issue(
                        "missing_references",
                        "warning",
                        "REFERENCE_SCAN_WARNING",
                        warning,
                        path));
                }
            }
        }

        private static void CheckMetaFiles(
            ProjectAuditRun run,
            string searchRoot,
            int scanLimit,
            string requestedCheck)
        {
            string fullRoot;
            string rootError;
            if (!TryResolvePhysicalAssetsRoot(searchRoot, out fullRoot, out rootError))
            {
                run.Issues.Add(Issue(
                    requestedCheck,
                    "error",
                    "INVALID_SEARCH_ROOT",
                    rootError));
                return;
            }

            if (!Directory.Exists(fullRoot))
            {
                run.Issues.Add(Issue(
                    requestedCheck,
                    "error",
                    "SEARCH_ROOT_MISSING",
                    $"Audit search root '{searchRoot}' does not exist."));
                return;
            }

            List<string> metaFiles;
            try
            {
                metaFiles = Directory.GetFiles(fullRoot, "*.meta", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                run.Issues.Add(Issue(
                    requestedCheck,
                    "error",
                    "META_SCAN_FAILED",
                    $"Could not enumerate meta files: {ex.Message}"));
                return;
            }

            run.MetaScanTruncated |= metaFiles.Count > scanLimit;
            var guidPaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string metaPath in metaFiles.Take(scanLimit))
            {
                run.MetaFilesScanned++;
                string assetPath = ToProjectRelative(metaPath.Substring(0, metaPath.Length - ".meta".Length));
                if (requestedCheck == "orphaned_meta_files"
                    && !File.Exists(metaPath.Substring(0, metaPath.Length - ".meta".Length))
                    && !Directory.Exists(metaPath.Substring(0, metaPath.Length - ".meta".Length)))
                {
                    run.Issues.Add(Issue(
                        "orphaned_meta_files",
                        "warning",
                        "ORPHANED_META_FILE",
                        "Meta file has no corresponding asset or folder.",
                        assetPath));
                }

                if (requestedCheck != "duplicate_guids")
                {
                    continue;
                }

                string guid = ReadMetaGuid(metaPath);
                if (string.IsNullOrEmpty(guid))
                {
                    continue;
                }
                List<string> paths;
                if (!guidPaths.TryGetValue(guid, out paths))
                {
                    paths = new List<string>();
                    guidPaths[guid] = paths;
                }
                paths.Add(ToProjectRelative(metaPath));
            }

            if (requestedCheck == "duplicate_guids")
            {
                foreach (KeyValuePair<string, List<string>> pair in guidPaths.Where(pair => pair.Value.Count > 1))
                {
                    run.Issues.Add(Issue(
                        "duplicate_guids",
                        "error",
                        "DUPLICATE_ASSET_GUID",
                        $"GUID '{pair.Key}' is declared by multiple meta files.",
                        details: new { guid = pair.Key, metaFiles = pair.Value }));
                }
            }
        }

        private static void CheckPackageHealth(ProjectAuditRun run)
        {
            string manifestPath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
            {
                run.Issues.Add(Issue(
                    "package_health",
                    "error",
                    "PACKAGE_MANIFEST_MISSING",
                    "Packages/manifest.json does not exist."));
                return;
            }

            JObject manifest;
            try
            {
                manifest = JObject.Parse(File.ReadAllText(manifestPath));
            }
            catch (Exception ex)
            {
                run.Issues.Add(Issue(
                    "package_health",
                    "error",
                    "PACKAGE_MANIFEST_INVALID",
                    $"Packages/manifest.json is not valid JSON: {ex.Message}"));
                return;
            }

            var registered = new HashSet<string>(
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .Where(package => package != null)
                    .Select(package => package.name),
                StringComparer.OrdinalIgnoreCase);
            var dependencies = manifest["dependencies"] as JObject;
            if (dependencies == null)
            {
                run.Issues.Add(Issue(
                    "package_health",
                    "error",
                    "PACKAGE_DEPENDENCIES_MISSING",
                    "Package manifest does not contain a dependencies object."));
                return;
            }

            foreach (JProperty dependency in dependencies.Properties())
            {
                string source = dependency.Value.Type == JTokenType.String
                    ? dependency.Value.Value<string>()
                    : null;
                if (!registered.Contains(dependency.Name))
                {
                    run.Issues.Add(Issue(
                        "package_health",
                        "error",
                        "PACKAGE_NOT_RESOLVED",
                        $"Manifest dependency '{dependency.Name}' is not registered by Package Manager.",
                        details: new { package = dependency.Name, source }));
                }

                if (!string.IsNullOrEmpty(source)
                    && source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    string relative = source.Substring("file:".Length).Replace('/', System.IO.Path.DirectorySeparatorChar);
                    string resolved = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(Directory.GetCurrentDirectory(), "Packages", relative));
                    if (!Directory.Exists(resolved) && !File.Exists(resolved))
                    {
                        run.Issues.Add(Issue(
                            "package_health",
                            "error",
                            "LOCAL_PACKAGE_MISSING",
                            $"Local package dependency '{dependency.Name}' points to a missing path.",
                            details: new { package = dependency.Name, source, resolvedPath = resolved }));
                    }
                }
            }
        }

        private static void CheckLoadedSceneStructure(ProjectAuditRun run)
        {
            int activeAudioListeners = 0;
            var listenerPaths = new List<string>();
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        GameObject gameObject = transform.gameObject;
                        int missingScripts = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject);
                        if (missingScripts > 0)
                        {
                            run.Issues.Add(Issue(
                                "loaded_scene_structure",
                                "error",
                                "LOADED_SCENE_MISSING_SCRIPT",
                                $"GameObject has {missingScripts} missing script component(s).",
                                scene.path,
                                GetHierarchyPath(transform)));
                        }
                    }

                    foreach (AudioListener listener in root.GetComponentsInChildren<AudioListener>(true))
                    {
                        if (listener.enabled && listener.gameObject.activeInHierarchy)
                        {
                            activeAudioListeners++;
                            listenerPaths.Add($"{scene.name}:{GetHierarchyPath(listener.transform)}");
                        }
                    }
                }
            }

            if (activeAudioListeners > 1)
            {
                run.Issues.Add(Issue(
                    "loaded_scene_structure",
                    "warning",
                    "MULTIPLE_ACTIVE_AUDIO_LISTENERS",
                    $"Loaded scenes contain {activeAudioListeners} active AudioListeners.",
                    details: new { listeners = listenerPaths }));
            }
        }

        private static ProjectAuditIssue Issue(
            string check,
            string severity,
            string code,
            string message,
            string assetPath = null,
            string objectPath = null,
            object details = null)
        {
            return new ProjectAuditIssue
            {
                Check = check,
                Severity = severity,
                Code = code,
                Message = message,
                AssetPath = assetPath,
                ObjectPath = objectPath,
                Details = details,
            };
        }

        private static int CompareIssues(ProjectAuditIssue left, ProjectAuditIssue right)
        {
            int severity = SeverityRank(right.Severity).CompareTo(SeverityRank(left.Severity));
            if (severity != 0) return severity;
            int check = string.Compare(left.Check, right.Check, StringComparison.OrdinalIgnoreCase);
            if (check != 0) return check;
            int path = string.Compare(left.AssetPath, right.AssetPath, StringComparison.OrdinalIgnoreCase);
            if (path != 0) return path;
            return string.Compare(left.Code, right.Code, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAssetsRoot(string searchRoot)
        {
            string normalized = string.IsNullOrWhiteSpace(searchRoot)
                ? "Assets"
                : searchRoot.Trim().Replace('\\', '/').TrimEnd('/');
            return normalized;
        }

        private static bool TryResolvePhysicalAssetsRoot(
            string searchRoot,
            out string fullRoot,
            out string error)
        {
            string normalized = NormalizeAssetsRoot(searchRoot);
            if (!normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                fullRoot = null;
                error = "search_root must be Assets or a folder below Assets/.";
                return false;
            }

            string projectRoot = System.IO.Path.GetFullPath(Directory.GetCurrentDirectory());
            string assetsRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, "Assets"));
            fullRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                projectRoot,
                normalized.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!fullRoot.Equals(assetsRoot, StringComparison.OrdinalIgnoreCase)
                && !fullRoot.StartsWith(assetsRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                fullRoot = null;
                error = "search_root resolves outside the project's Assets directory.";
                return false;
            }

            error = null;
            return true;
        }

        private static string ReadMetaGuid(string metaPath)
        {
            try
            {
                foreach (string line in File.ReadLines(metaPath).Take(20))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmed.Substring("guid:".Length).Trim();
                    }
                }
            }
            catch
            {
                // An unreadable meta file is surfaced by Unity's own importer/console.
            }
            return null;
        }

        private static string ToProjectRelative(string fullPath)
        {
            string projectRoot = System.IO.Path.GetFullPath(Directory.GetCurrentDirectory())
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            string normalized = System.IO.Path.GetFullPath(fullPath);
            if (normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(projectRoot.Length);
            }
            return normalized.Replace('\\', '/');
        }

        private static string GetHierarchyPath(Transform transform)
        {
            var names = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
