using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Tools.Dependencies
{
    internal sealed class AssetDependencyInfo
    {
        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("guid")]
        public string Guid { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("isFolder")]
        public bool IsFolder { get; set; }
    }

    internal sealed class MissingReferenceIssue
    {
        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("assetPath")]
        public string AssetPath { get; set; }

        [JsonProperty("objectPath")]
        public string ObjectPath { get; set; }

        [JsonProperty("ownerType")]
        public string OwnerType { get; set; }

        [JsonProperty("propertyPath")]
        public string PropertyPath { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    internal sealed class MissingReferenceScanResult
    {
        public List<MissingReferenceIssue> Issues { get; } = new List<MissingReferenceIssue>();
        public List<string> Warnings { get; } = new List<string>();
    }

    internal sealed class CandidateScanResult
    {
        public List<string> Paths { get; } = new List<string>();
        public int AvailableCount { get; set; }
        public bool Truncated { get; set; }
    }

    internal static class DependencyInspectionUtility
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        internal static bool TryResolveAssetPath(string target, out string assetPath, out string error)
        {
            assetPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(target))
            {
                error = "'target' parameter is required.";
                return false;
            }

            string candidate = target.Trim().Replace('\\', '/');
            if (!candidate.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                && !candidate.Equals("Assets", StringComparison.OrdinalIgnoreCase)
                && !candidate.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                && !candidate.Equals("Packages", StringComparison.OrdinalIgnoreCase))
            {
                string guidPath = AssetDatabase.GUIDToAssetPath(candidate);
                if (!string.IsNullOrEmpty(guidPath))
                {
                    candidate = guidPath;
                }
            }

            if (!AssetDatabase.IsValidFolder(candidate)
                && AssetDatabase.LoadMainAssetAtPath(candidate) == null)
            {
                error = $"Asset '{target}' was not found. Provide an Assets/ or Packages/ path, or an asset GUID.";
                return false;
            }

            assetPath = candidate;
            return true;
        }

        internal static AssetDependencyInfo Describe(string path)
        {
            Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return new AssetDependencyInfo
            {
                Path = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                Type = type != null ? type.Name : null,
                IsFolder = AssetDatabase.IsValidFolder(path),
            };
        }

        internal static List<AssetDependencyInfo> GetDependencies(
            string target,
            bool recursive,
            bool includePackages,
            string assetType)
        {
            var paths = AssetDatabase.GetDependencies(target, recursive)
                .Where(path => !PathComparer.Equals(path, target))
                .Where(path => includePackages || !IsPackagePath(path))
                .Distinct(PathComparer)
                .OrderBy(path => path, PathComparer);

            return FilterByType(paths, assetType)
                .Select(Describe)
                .ToList();
        }

        internal static List<AssetDependencyInfo> GetDependents(
            string target,
            bool recursive,
            bool includePackages,
            string searchRoot,
            string assetType,
            int scanLimit,
            out int scannedCount,
            out int availableCount,
            out bool scanTruncated)
        {
            CandidateScanResult candidates = GetCandidateAssetPaths(
                includePackages,
                searchRoot,
                scanLimit);
            scannedCount = 0;
            availableCount = candidates.AvailableCount;
            scanTruncated = candidates.Truncated;

            var result = new List<string>();
            foreach (string candidate in candidates.Paths)
            {
                scannedCount++;
                if (PathComparer.Equals(candidate, target))
                {
                    continue;
                }

                string[] dependencies;
                try
                {
                    dependencies = AssetDatabase.GetDependencies(candidate, recursive);
                }
                catch
                {
                    continue;
                }

                if (dependencies.Any(path => PathComparer.Equals(path, target)))
                {
                    result.Add(candidate);
                }
            }

            return FilterByType(result.OrderBy(path => path, PathComparer), assetType)
                .Select(Describe)
                .ToList();
        }

        internal static CandidateScanResult GetCandidateAssetPaths(
            bool includePackages,
            string searchRoot,
            int scanLimit)
        {
            string normalizedRoot = NormalizeSearchRoot(searchRoot);
            var all = AssetDatabase.GetAllAssetPaths()
                .Where(path => !string.IsNullOrEmpty(path))
                .Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    || (includePackages && path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)))
                .Where(path => string.IsNullOrEmpty(normalizedRoot)
                    || path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
                .Where(path => !AssetDatabase.IsValidFolder(path))
                .Distinct(PathComparer)
                .OrderBy(path => path, PathComparer)
                .ToList();

            var result = new CandidateScanResult
            {
                AvailableCount = all.Count,
                Truncated = all.Count > scanLimit,
            };
            result.Paths.AddRange(all.Take(scanLimit));
            return result;
        }

        internal static MissingReferenceScanResult FindMissingReferences(string assetPath)
        {
            var result = new MissingReferenceScanResult();
            string extension = System.IO.Path.GetExtension(assetPath).ToLowerInvariant();

            if (extension == ".prefab")
            {
                ScanPrefab(assetPath, result);
            }
            else if (extension == ".unity")
            {
                ScanScene(assetPath, result);
            }
            else
            {
                ScanLoadedAssets(assetPath, result);
            }

            result.Issues.Sort((left, right) =>
            {
                int byObject = string.Compare(left.ObjectPath, right.ObjectPath, StringComparison.OrdinalIgnoreCase);
                return byObject != 0
                    ? byObject
                    : string.Compare(left.PropertyPath, right.PropertyPath, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        internal static List<List<string>> FindCycles(
            string target,
            bool includePackages,
            string searchRoot,
            int scanLimit,
            int maxResults,
            out int scannedCount,
            out int availableCount,
            out bool scanTruncated)
        {
            List<string> nodes;
            if (!string.IsNullOrEmpty(target))
            {
                nodes = AssetDatabase.GetDependencies(target, true)
                    .Where(path => includePackages || !IsPackagePath(path))
                    .Where(path => !AssetDatabase.IsValidFolder(path))
                    .Distinct(PathComparer)
                    .OrderBy(path => path, PathComparer)
                    .ToList();
                availableCount = nodes.Count;
                scanTruncated = nodes.Count > scanLimit;
                nodes = nodes.Take(scanLimit).ToList();
            }
            else
            {
                CandidateScanResult candidates = GetCandidateAssetPaths(
                    includePackages,
                    searchRoot,
                    scanLimit);
                nodes = candidates.Paths;
                availableCount = candidates.AvailableCount;
                scanTruncated = candidates.Truncated;
            }

            scannedCount = nodes.Count;
            var nodeSet = new HashSet<string>(nodes, PathComparer);
            var graph = new Dictionary<string, List<string>>(PathComparer);
            foreach (string node in nodes)
            {
                IEnumerable<string> direct;
                try
                {
                    direct = AssetDatabase.GetDependencies(node, false);
                }
                catch
                {
                    direct = Array.Empty<string>();
                }

                graph[node] = direct
                    .Where(dependency => !PathComparer.Equals(dependency, node))
                    .Where(nodeSet.Contains)
                    .Distinct(PathComparer)
                    .OrderBy(path => path, PathComparer)
                    .ToList();
            }

            var states = new Dictionary<string, int>(PathComparer);
            var stack = new List<string>();
            var cycles = new List<List<string>>();
            var signatures = new HashSet<string>(PathComparer);
            foreach (string node in nodes)
            {
                if (cycles.Count >= maxResults)
                {
                    break;
                }

                if (!states.ContainsKey(node))
                {
                    VisitForCycles(node, graph, states, stack, cycles, signatures, maxResults);
                }
            }

            cycles.Sort((left, right) => string.Compare(
                string.Join(" -> ", left),
                string.Join(" -> ", right),
                StringComparison.OrdinalIgnoreCase));
            return cycles;
        }

        private static void VisitForCycles(
            string node,
            Dictionary<string, List<string>> graph,
            Dictionary<string, int> states,
            List<string> stack,
            List<List<string>> cycles,
            HashSet<string> signatures,
            int maxResults)
        {
            if (cycles.Count >= maxResults)
            {
                return;
            }

            states[node] = 1;
            stack.Add(node);
            foreach (string dependency in graph[node])
            {
                if (cycles.Count >= maxResults)
                {
                    break;
                }

                int state;
                if (!states.TryGetValue(dependency, out state))
                {
                    VisitForCycles(dependency, graph, states, stack, cycles, signatures, maxResults);
                }
                else if (state == 1)
                {
                    int start = stack.FindIndex(item => PathComparer.Equals(item, dependency));
                    if (start >= 0)
                    {
                        var cycle = stack.Skip(start).ToList();
                        string signature = CanonicalCycleSignature(cycle);
                        if (signatures.Add(signature))
                        {
                            cycle.Add(cycle[0]);
                            cycles.Add(cycle);
                        }
                    }
                }
            }

            stack.RemoveAt(stack.Count - 1);
            states[node] = 2;
        }

        private static string CanonicalCycleSignature(List<string> cycle)
        {
            if (cycle.Count == 0)
            {
                return string.Empty;
            }

            int minimumIndex = 0;
            for (int index = 1; index < cycle.Count; index++)
            {
                if (string.Compare(cycle[index], cycle[minimumIndex], StringComparison.OrdinalIgnoreCase) < 0)
                {
                    minimumIndex = index;
                }
            }

            var rotated = new List<string>();
            for (int offset = 0; offset < cycle.Count; offset++)
            {
                rotated.Add(cycle[(minimumIndex + offset) % cycle.Count]);
            }
            return string.Join("|", rotated);
        }

        private static IEnumerable<string> FilterByType(IEnumerable<string> paths, string assetType)
        {
            if (string.IsNullOrWhiteSpace(assetType))
            {
                return paths;
            }

            return paths.Where(path =>
            {
                Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
                return type != null
                    && (type.Name.Equals(assetType, StringComparison.OrdinalIgnoreCase)
                        || type.FullName.Equals(assetType, StringComparison.OrdinalIgnoreCase));
            });
        }

        private static string NormalizeSearchRoot(string searchRoot)
        {
            if (string.IsNullOrWhiteSpace(searchRoot))
            {
                return "Assets";
            }
            return searchRoot.Trim().Replace('\\', '/').TrimEnd('/');
        }

        private static bool IsPackagePath(string path)
        {
            return path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
        }

        private static void ScanPrefab(string assetPath, MissingReferenceScanResult result)
        {
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(assetPath);
                if (root == null)
                {
                    result.Warnings.Add($"Could not load prefab contents for '{assetPath}'.");
                    return;
                }
                ScanGameObjectHierarchy(root, assetPath, result);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Prefab scan failed for '{assetPath}': {ex.Message}");
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        private static void ScanScene(string assetPath, MissingReferenceScanResult result)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                result.Warnings.Add("Scene reference scanning is unavailable while entering or running Play Mode.");
                return;
            }

            Scene scene = default(Scene);
            bool openedForScan = false;
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene loaded = SceneManager.GetSceneAt(index);
                if (loaded.path.Equals(assetPath, StringComparison.OrdinalIgnoreCase))
                {
                    scene = loaded;
                    break;
                }
            }

            try
            {
                if (!scene.IsValid())
                {
                    scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
                    openedForScan = true;
                }

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    ScanGameObjectHierarchy(root, assetPath, result);
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Scene scan failed for '{assetPath}': {ex.Message}");
            }
            finally
            {
                if (openedForScan && scene.IsValid())
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static void ScanLoadedAssets(string assetPath, MissingReferenceScanResult result)
        {
            UnityEngine.Object[] assets;
            try
            {
                assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Asset scan failed for '{assetPath}': {ex.Message}");
                return;
            }

            foreach (UnityEngine.Object asset in assets)
            {
                if (asset == null)
                {
                    continue;
                }

                var gameObject = asset as GameObject;
                if (gameObject != null)
                {
                    ScanGameObjectHierarchy(gameObject, assetPath, result);
                }
                else
                {
                    ScanSerializedObject(asset, assetPath, asset.name, result);
                }
            }
        }

        private static void ScanGameObjectHierarchy(
            GameObject root,
            string assetPath,
            MissingReferenceScanResult result)
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                GameObject gameObject = transform.gameObject;
                string objectPath = GetHierarchyPath(gameObject.transform, root.transform);
                int missingScripts = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject);
                if (missingScripts > 0)
                {
                    result.Issues.Add(new MissingReferenceIssue
                    {
                        Kind = "missing_script",
                        AssetPath = assetPath,
                        ObjectPath = objectPath,
                        OwnerType = "GameObject",
                        Message = $"GameObject has {missingScripts} missing script component(s).",
                    });
                }

                foreach (Component component in gameObject.GetComponents<Component>())
                {
                    if (component != null)
                    {
                        ScanSerializedObject(component, assetPath, objectPath, result);
                    }
                }
            }
        }

        private static void ScanSerializedObject(
            UnityEngine.Object owner,
            string assetPath,
            string objectPath,
            MissingReferenceScanResult result)
        {
            try
            {
                var serializedObject = new SerializedObject(owner);
                SerializedProperty property = serializedObject.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType == SerializedPropertyType.ObjectReference
                        && property.objectReferenceValue == null
                        && property.objectReferenceInstanceIDValue != 0)
                    {
                        result.Issues.Add(new MissingReferenceIssue
                        {
                            Kind = "missing_object_reference",
                            AssetPath = assetPath,
                            ObjectPath = objectPath,
                            OwnerType = owner.GetType().FullName,
                            PropertyPath = property.propertyPath,
                            Message = "Serialized object reference cannot be resolved.",
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"Could not inspect '{objectPath}' ({owner.GetType().Name}): {ex.Message}");
            }
        }

        private static string GetHierarchyPath(Transform transform, Transform root)
        {
            var names = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                names.Add(current.name);
                if (current == root)
                {
                    break;
                }
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
