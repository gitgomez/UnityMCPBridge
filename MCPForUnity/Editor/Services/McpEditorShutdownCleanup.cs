using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Best-effort cleanup when the Unity Editor is quitting.
    /// - Stops active transports so clients don't see a "hung" session longer than necessary.
    /// - Stops a launch-owned server only after its authenticated shared-use check.
    ///   Shared/uncertain servers are deliberately left running. Quit only, never domain reload.
    /// </summary>
    [InitializeOnLoad]
    internal static class McpEditorShutdownCleanup
    {
        static McpEditorShutdownCleanup()
        {
            // Guard against duplicate subscriptions across domain reloads.
            try { EditorApplication.quitting -= OnEditorQuitting; } catch { }
            EditorApplication.quitting += OnEditorQuitting;
        }

        // Batch runtime opt-in permits transport cleanup, never inherits stop ownership.
        internal static bool ShouldRunCleanup() =>
            ShouldRunCleanup(Application.isBatchMode, Environment.GetEnvironmentVariable("UNITY_MCP_ALLOW_BATCH"));

        internal static bool ShouldRunCleanup(bool isBatchMode, string allowBatchEnv) =>
            !isBatchMode || !string.IsNullOrWhiteSpace(allowBatchEnv);

        private static void OnEditorQuitting()
        {
            if (!ShouldRunCleanup()) return;

            // 1) Stop transports (best-effort, bounded wait).
            try
            {
                var transport = MCPServiceLocator.TransportManager;

                Task stopHttp = transport.StopAsync(TransportMode.Http);
                Task stopStdio = transport.StopAsync(TransportMode.Stdio);

                try { Task.WaitAll(new[] { stopHttp, stopStdio }, 750); } catch { }
            }
            catch (Exception ex)
            {
                // Avoid hard failures on quit.
                McpLog.Warn($"Shutdown cleanup: failed to stop transports: {ex.Message}");
            }

            // 2) Stop the local HTTP server this Unity instance launched (best-effort).
            // SessionState proves launch ownership; the server's atomic stop lease checks other users.
            // If the owner leaves while a peer still uses it, retain the server (no ownership transfer).
            // This runs on quit only;
            // domain reloads must NOT stop the server (and don't — this handler is gated on EditorApplication.quitting).
            try
            {
                MCPServiceLocator.Server.StopManagedLocalHttpServer();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Shutdown cleanup: failed to stop local HTTP server: {ex.Message}");
            }
        }
    }
}
