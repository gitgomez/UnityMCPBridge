using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Tools.Dependencies
{
    [McpForUnityTool("inspect_dependencies", AutoRegister = false, Group = "core")]
    public static class InspectDependencies
    {
        private const int DefaultScanLimit = 20000;
        private const int MaximumScanLimit = 100000;
        private const int DefaultMaxResults = 100;
        private const int MaximumMaxResults = 1000;

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            var p = new ToolParams(@params);
            string action = p.Get("action")?.ToLowerInvariant();
            if (string.IsNullOrEmpty(action))
            {
                return new ErrorResponse("'action' parameter is required.");
            }

            try
            {
                switch (action)
                {
                    case "ping":
                        return Ping();
                    case "dependencies":
                        return Dependencies(p, @params);
                    case "dependents":
                        return Dependents(p, @params);
                    case "impact":
                        return Impact(p);
                    case "missing_references":
                        return MissingReferences(p, @params);
                    case "cycles":
                        return Cycles(p);
                    default:
                        return new ErrorResponse(
                            $"Unknown action: '{action}'. Valid actions: ping, dependencies, "
                            + "dependents, impact, missing_references, cycles.");
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"[InspectDependencies] Action '{action}' failed: {ex}");
                return new ErrorResponse(
                    $"Dependency inspection action '{action}' failed: {ex.Message}");
            }
        }

        private static object Ping()
        {
            int assetCount = AssetDatabase.GetAllAssetPaths().Count(path =>
                path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase));
            return new SuccessResponse("Dependency inspection is available.", new
            {
                assetCount,
                actions = new[]
                {
                    "dependencies",
                    "dependents",
                    "impact",
                    "missing_references",
                    "cycles",
                },
                readOnly = true,
            });
        }

        private static object Dependencies(ToolParams p, JObject raw)
        {
            string target;
            ErrorResponse error;
            if (!TryGetTarget(p, out target, out error))
            {
                return error;
            }

            bool recursive = p.GetBool("recursive", true);
            bool includePackages = p.GetBool("include_packages", false);
            string assetType = p.Get("asset_type");
            List<AssetDependencyInfo> dependencies = DependencyInspectionUtility.GetDependencies(
                target,
                recursive,
                includePackages,
                assetType);
            PaginationResponse<AssetDependencyInfo> page = CreatePage(dependencies, raw);
            return new SuccessResponse($"Found {dependencies.Count} dependency asset(s).", new
            {
                target = DependencyInspectionUtility.Describe(target),
                recursive,
                includePackages,
                assetType,
                page.Items,
                page.Cursor,
                page.NextCursor,
                page.TotalCount,
                page.PageSize,
                page.HasMore,
            });
        }

        private static object Dependents(ToolParams p, JObject raw)
        {
            string target;
            ErrorResponse error;
            if (!TryGetTarget(p, out target, out error))
            {
                return error;
            }

            bool recursive = p.GetBool("recursive", false);
            bool includePackages = p.GetBool("include_packages", false);
            string searchRoot = p.Get("search_root", "Assets");
            string assetType = p.Get("asset_type");
            int scanLimit = Clamp(p.GetInt("scan_limit", DefaultScanLimit).Value, 1, MaximumScanLimit);
            int scannedCount;
            int availableCount;
            bool scanTruncated;
            List<AssetDependencyInfo> dependents = DependencyInspectionUtility.GetDependents(
                target,
                recursive,
                includePackages,
                searchRoot,
                assetType,
                scanLimit,
                out scannedCount,
                out availableCount,
                out scanTruncated);
            PaginationResponse<AssetDependencyInfo> page = CreatePage(dependents, raw);
            return new SuccessResponse($"Found {dependents.Count} dependent asset(s).", new
            {
                target = DependencyInspectionUtility.Describe(target),
                recursive,
                includePackages,
                searchRoot,
                assetType,
                scannedCount,
                availableCount,
                scanTruncated,
                page.Items,
                page.Cursor,
                page.NextCursor,
                page.TotalCount,
                page.PageSize,
                page.HasMore,
            });
        }

        private static object Impact(ToolParams p)
        {
            string target;
            ErrorResponse error;
            if (!TryGetTarget(p, out target, out error))
            {
                return error;
            }

            bool recursive = p.GetBool("recursive", true);
            bool includePackages = p.GetBool("include_packages", false);
            string searchRoot = p.Get("search_root", "Assets");
            string assetType = p.Get("asset_type");
            int scanLimit = Clamp(p.GetInt("scan_limit", DefaultScanLimit).Value, 1, MaximumScanLimit);
            int maxResults = Clamp(p.GetInt("max_results", DefaultMaxResults).Value, 1, MaximumMaxResults);
            List<AssetDependencyInfo> dependencies = DependencyInspectionUtility.GetDependencies(
                target,
                recursive,
                includePackages,
                assetType);
            int scannedCount;
            int availableCount;
            bool scanTruncated;
            List<AssetDependencyInfo> dependents = DependencyInspectionUtility.GetDependents(
                target,
                recursive,
                includePackages,
                searchRoot,
                assetType,
                scanLimit,
                out scannedCount,
                out availableCount,
                out scanTruncated);

            return new SuccessResponse("Dependency impact analysis completed.", new
            {
                target = DependencyInspectionUtility.Describe(target),
                recursive,
                dependencyCount = dependencies.Count,
                dependentCount = dependents.Count,
                dependencies = dependencies.Take(maxResults).ToList(),
                dependents = dependents.Take(maxResults).ToList(),
                resultsTruncated = dependencies.Count > maxResults || dependents.Count > maxResults,
                scannedCount,
                availableCount,
                scanTruncated,
            });
        }

        private static object MissingReferences(ToolParams p, JObject raw)
        {
            string target;
            ErrorResponse error;
            if (!TryGetTarget(p, out target, out error))
            {
                return error;
            }

            MissingReferenceScanResult result = DependencyInspectionUtility.FindMissingReferences(target);
            PaginationResponse<MissingReferenceIssue> page = CreatePage(result.Issues, raw);
            return new SuccessResponse($"Found {result.Issues.Count} missing reference issue(s).", new
            {
                target = DependencyInspectionUtility.Describe(target),
                issueCount = result.Issues.Count,
                warnings = result.Warnings,
                page.Items,
                page.Cursor,
                page.NextCursor,
                page.TotalCount,
                page.PageSize,
                page.HasMore,
            });
        }

        private static object Cycles(ToolParams p)
        {
            string target = null;
            string rawTarget = p.Get("target");
            if (!string.IsNullOrWhiteSpace(rawTarget))
            {
                string error;
                if (!DependencyInspectionUtility.TryResolveAssetPath(rawTarget, out target, out error))
                {
                    return new ErrorResponse(error);
                }
            }

            bool includePackages = p.GetBool("include_packages", false);
            string searchRoot = p.Get("search_root", "Assets");
            int scanLimit = Clamp(p.GetInt("scan_limit", DefaultScanLimit).Value, 1, MaximumScanLimit);
            int maxResults = Clamp(p.GetInt("max_results", DefaultMaxResults).Value, 1, MaximumMaxResults);
            int scannedCount;
            int availableCount;
            bool scanTruncated;
            List<List<string>> cycles = DependencyInspectionUtility.FindCycles(
                target,
                includePackages,
                searchRoot,
                scanLimit,
                maxResults,
                out scannedCount,
                out availableCount,
                out scanTruncated);
            return new SuccessResponse($"Found {cycles.Count} dependency cycle(s).", new
            {
                target = target != null ? DependencyInspectionUtility.Describe(target) : null,
                cycles,
                cycleCount = cycles.Count,
                maxResults,
                resultsTruncated = cycles.Count >= maxResults,
                scannedCount,
                availableCount,
                scanTruncated,
            });
        }

        private static bool TryGetTarget(ToolParams p, out string target, out ErrorResponse error)
        {
            string message;
            if (!DependencyInspectionUtility.TryResolveAssetPath(p.Get("target"), out target, out message))
            {
                error = new ErrorResponse(message);
                return false;
            }
            error = null;
            return true;
        }

        private static PaginationResponse<T> CreatePage<T>(List<T> items, JObject raw)
        {
            PaginationRequest request = PaginationRequest.FromParams(raw);
            request.PageSize = Clamp(request.PageSize, 1, 500);
            return PaginationResponse<T>.Create(items, request);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
