using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools.Audit
{
    [McpForUnityTool("audit_project", AutoRegister = false, Group = "core")]
    public static class AuditProject
    {
        private const int DefaultScanLimit = 5000;
        private const int MaximumScanLimit = 50000;

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
                    case "list_checks":
                        return ListChecks();
                    case "run":
                        return Run(p, @params);
                    default:
                        return new ErrorResponse(
                            $"Unknown action: '{action}'. Valid actions: ping, list_checks, run.");
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"[AuditProject] Action '{action}' failed: {ex}");
                return new ErrorResponse($"Project audit action '{action}' failed: {ex.Message}");
            }
        }

        private static object Ping()
        {
            return new SuccessResponse("Project auditing is available.", new
            {
                readOnly = true,
                checkCount = ProjectAuditUtility.AllChecks.Length,
                checks = ProjectAuditUtility.AllChecks,
            });
        }

        private static object ListChecks()
        {
            var checks = ProjectAuditUtility.AllChecks.Select(name => new
            {
                name,
                description = ProjectAuditUtility.CheckDescriptions[name],
            }).ToList();
            return new SuccessResponse($"Found {checks.Count} project audit check(s).", new
            {
                checks,
                defaultMinimumSeverity = "warning",
            });
        }

        private static object Run(ToolParams p, JObject raw)
        {
            string[] requested = p.GetStringArray("checks");
            List<string> checks = requested == null
                ? ProjectAuditUtility.AllChecks.ToList()
                : requested.Select(value => value.ToLowerInvariant()).Distinct().ToList();
            List<string> unknown = checks
                .Where(check => !ProjectAuditUtility.AllChecks.Contains(
                    check,
                    StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (unknown.Count > 0)
            {
                return new ErrorResponse(
                    $"Unknown audit check(s): {string.Join(", ", unknown)}. "
                    + $"Valid checks: {string.Join(", ", ProjectAuditUtility.AllChecks)}.");
            }

            string minimumSeverity = p.Get("minimum_severity", "warning").ToLowerInvariant();
            if (!new[] { "info", "warning", "error" }.Contains(minimumSeverity))
            {
                return new ErrorResponse("minimum_severity must be info, warning, or error.");
            }

            string searchRoot = p.Get("search_root", "Assets");
            bool includeScenes = p.GetBool("include_scenes", false);
            int scanLimit = Clamp(p.GetInt("scan_limit", DefaultScanLimit).Value, 1, MaximumScanLimit);
            ProjectAuditRun run = ProjectAuditUtility.Run(
                checks,
                searchRoot,
                includeScenes,
                scanLimit);
            int errorCount = run.Issues.Count(issue => issue.Severity == "error");
            int warningCount = run.Issues.Count(issue => issue.Severity == "warning");
            int infoCount = run.Issues.Count(issue => issue.Severity == "info");
            int minimumRank = ProjectAuditUtility.SeverityRank(minimumSeverity);
            List<ProjectAuditIssue> filtered = run.Issues
                .Where(issue => ProjectAuditUtility.SeverityRank(issue.Severity) >= minimumRank)
                .ToList();

            PaginationRequest request = PaginationRequest.FromParams(raw);
            request.PageSize = Clamp(request.PageSize, 1, 500);
            PaginationResponse<ProjectAuditIssue> page = PaginationResponse<ProjectAuditIssue>.Create(
                filtered,
                request);
            return new SuccessResponse(
                $"Project audit completed with {errorCount} error(s), {warningCount} warning(s), and {infoCount} info item(s).",
                new
                {
                    checksRun = run.ChecksRun,
                    searchRoot,
                    includeScenes,
                    scanLimit,
                    minimumSeverity,
                    errorCount,
                    warningCount,
                    infoCount,
                    issueCount = run.Issues.Count,
                    returnedIssueCount = filtered.Count,
                    hasErrors = errorCount > 0,
                    hasWarnings = warningCount > 0,
                    assetsScanned = run.AssetsScanned,
                    metaFilesScanned = run.MetaFilesScanned,
                    assetScanTruncated = run.AssetScanTruncated,
                    metaScanTruncated = run.MetaScanTruncated,
                    durationMs = run.DurationMs,
                    items = page.Items,
                    cursor = page.Cursor,
                    nextCursor = page.NextCursor,
                    totalCount = page.TotalCount,
                    pageSize = page.PageSize,
                    hasMore = page.HasMore,
                });
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
