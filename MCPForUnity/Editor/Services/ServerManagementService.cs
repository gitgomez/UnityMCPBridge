using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Server;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Service for managing MCP server lifecycle
    /// </summary>
    public class ServerManagementService : IServerManagementService
    {
        private readonly IProcessDetector _processDetector;
        private readonly IPidFileManager _pidFileManager;
        private readonly IProcessTerminator _processTerminator;
        private readonly IServerCommandBuilder _commandBuilder;
        private readonly ITerminalLauncher _terminalLauncher;

        private readonly object _localHttpLaunchGate = new object();
        private System.Diagnostics.Process _lastLaunchedProcess;

        /// <summary>
        /// Creates a new ServerManagementService with default dependencies.
        /// </summary>
        public ServerManagementService() : this(null, null, null, null, null) { }

        /// <summary>
        /// Creates a new ServerManagementService with injected dependencies (for testing).
        /// </summary>
        /// <param name="processDetector">Process detector implementation (null for default)</param>
        /// <param name="pidFileManager">PID file manager implementation (null for default)</param>
        /// <param name="processTerminator">Process terminator implementation (null for default)</param>
        /// <param name="commandBuilder">Server command builder implementation (null for default)</param>
        /// <param name="terminalLauncher">Terminal launcher implementation (null for default)</param>
        public ServerManagementService(
            IProcessDetector processDetector,
            IPidFileManager pidFileManager = null,
            IProcessTerminator processTerminator = null,
            IServerCommandBuilder commandBuilder = null,
            ITerminalLauncher terminalLauncher = null)
        {
            _processDetector = processDetector ?? new ProcessDetector();
            _pidFileManager = pidFileManager ?? new PidFileManager();
            _processTerminator = processTerminator ?? new ProcessTerminator(_processDetector);
            _commandBuilder = commandBuilder ?? new ServerCommandBuilder();
            _terminalLauncher = terminalLauncher ?? new TerminalLauncher();
        }

        private string QuoteIfNeeded(string s)
        {
            return _commandBuilder.QuoteIfNeeded(s);
        }

        private string NormalizeForMatch(string s)
        {
            return _processDetector.NormalizeForMatch(s);
        }

        private void ClearLocalServerPidTracking()
        {
            _pidFileManager.ClearTracking();
        }

        private void StoreLocalHttpServerHandshake(string pidFilePath, string instanceToken)
        {
            _pidFileManager.StoreHandshake(pidFilePath, instanceToken);
        }

        private bool TryGetLocalHttpServerHandshake(out string pidFilePath, out string instanceToken)
        {
            return _pidFileManager.TryGetHandshake(out pidFilePath, out instanceToken);
        }

        private string GetLocalHttpServerPidFilePath(int port)
        {
            return _pidFileManager.GetPidFilePath(port);
        }

        private bool TryReadPidFromPidFile(string pidFilePath, out int pid)
        {
            return _pidFileManager.TryReadPid(pidFilePath, out pid);
        }

        private bool TryProcessCommandLineContainsInstanceToken(int pid, string instanceToken, out bool containsToken)
        {
            containsToken = false;
            if (pid <= 0 || string.IsNullOrEmpty(instanceToken))
            {
                return false;
            }

            try
            {
                string tokenNeedle = instanceToken.ToLowerInvariant();

                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // Query full command line so we can validate token (reduces PID reuse risk).
                    // Use CIM via PowerShell (wmic is deprecated).
                    string ps = $"(Get-CimInstance Win32_Process -Filter \\\"ProcessId={pid}\\\").CommandLine";
                    bool ok = ExecPath.TryRun("powershell", $"-NoProfile -Command \"{ps}\"", Application.dataPath, out var stdout, out var stderr, 5000);
                    string combined = ((stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty)).ToLowerInvariant();
                    containsToken = combined.Contains(tokenNeedle);
                    return ok;
                }

                if (TryGetUnixProcessArgs(pid, out var argsLowerNow))
                {
                    containsToken = argsLowerNow.Contains(NormalizeForMatch(tokenNeedle));
                    return true;
                }
            }
            catch { }

            return false;
        }

        private string ComputeShortHash(string input)
        {
            return _pidFileManager.ComputeShortHash(input);
        }

        private bool TryGetStoredLocalServerPid(int expectedPort, out int pid)
        {
            return _pidFileManager.TryGetStoredPid(expectedPort, out pid);
        }

        private string GetStoredArgsHash()
        {
            return _pidFileManager.GetStoredArgsHash();
        }

        /// <summary>
        /// Clear the local uvx cache for the MCP server package
        /// </summary>
        /// <returns>True if successful, false otherwise</returns>
        public bool ClearUvxCache()
        {
            try
            {
                string uvxPath = MCPServiceLocator.Paths.GetUvxPath();
                string uvCommand = BuildUvPathFromUvx(uvxPath);

                // Get the package name
                string packageName = "mcp-for-unity";

                // Run uvx cache clean command
                string args = $"cache clean {packageName}";

                bool success;
                string stdout;
                string stderr;

                success = ExecuteUvCommand(uvCommand, args, out stdout, out stderr);

                if (success)
                {
                    McpLog.Info($"uv cache cleared successfully: {stdout}");
                    return true;
                }
                string combinedOutput = string.Join(
                    Environment.NewLine,
                    new[] { stderr, stdout }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

                string lockHint = (!string.IsNullOrEmpty(combinedOutput) &&
                                   combinedOutput.IndexOf("currently in-use", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? "Another uv process may be holding the cache lock; wait a moment and try again or clear with '--force' from a terminal."
                    : string.Empty;

                if (string.IsNullOrEmpty(combinedOutput))
                {
                    combinedOutput = "Command failed with no output. Ensure uv is installed, on PATH, or set an override in Advanced Settings.";
                }

                McpLog.Error(
                    $"Failed to clear uv cache using '{uvCommand} {args}'. " +
                    $"Details: {combinedOutput}{(string.IsNullOrEmpty(lockHint) ? string.Empty : " Hint: " + lockHint)}");
                return false;
            }
            catch (Exception ex)
            {
                McpLog.Error($"Error clearing uv cache: {ex.Message}");
                return false;
            }
        }

        private bool ExecuteUvCommand(string uvCommand, string args, out string stdout, out string stderr)
        {
            stdout = null;
            stderr = null;

            string uvxPath = MCPServiceLocator.Paths.GetUvxPath();
            string uvPath = BuildUvPathFromUvx(uvxPath);

            if (!string.Equals(uvCommand, uvPath, StringComparison.OrdinalIgnoreCase))
            {
                return ExecPath.TryRun(uvCommand, args, Application.dataPath, out stdout, out stderr, 30000);
            }

            string command = $"{uvPath} {args}";
            string extraPathPrepend = GetPlatformSpecificPathPrepend();

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                return ExecPath.TryRun("cmd.exe", $"/c {command}", Application.dataPath, out stdout, out stderr, 30000, extraPathPrepend);
            }

            string shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";

            if (!string.IsNullOrEmpty(shell) && File.Exists(shell))
            {
                string escaped = command.Replace("\"", "\\\"");
                return ExecPath.TryRun(shell, $"-lc \"{escaped}\"", Application.dataPath, out stdout, out stderr, 30000, extraPathPrepend);
            }

            return ExecPath.TryRun(uvPath, args, Application.dataPath, out stdout, out stderr, 30000, extraPathPrepend);
        }

        private string BuildUvPathFromUvx(string uvxPath)
        {
            return _commandBuilder.BuildUvPathFromUvx(uvxPath);
        }

        private string GetPlatformSpecificPathPrepend()
        {
            return _commandBuilder.GetPlatformSpecificPathPrepend();
        }

        /// <summary>
        /// Start the local HTTP server headless (no terminal window), redirecting its
        /// stdout/stderr to Library/MCPForUnity/Logs/server-launch-{port}.log.
        /// Reuses an already reachable or in-flight managed server. Otherwise stops stale
        /// server state on the port and clears stale build artifacts first.
        /// </summary>
        public bool StartLocalHttpServer(bool quiet = false)
        {
            lock (_localHttpLaunchGate)
            {
                // Start is idempotent across domain reloads, where the server can remain healthy
                // even though this service no longer owns the original Process handle.
                if (IsLocalHttpServerReachable())
                {
                    McpLog.Info("Local HTTP server is already reachable; reusing it");
                    return true;
                }

                // Starting the dev-mode server can take a minute while uvx resolves and installs
                // dependencies. Reuse that in-flight launch instead of stopping it and spawning a
                // second process when auto-start and the UI request startup at nearly the same time.
                if (IsManagedServerLaunchProcessAliveUnsafe())
                {
                    McpLog.Info("Local HTTP server launch is already in progress; reusing it");
                    return true;
                }

                DisposeManagedLaunchHandleUnsafe();
                return StartLocalHttpServerCore(quiet);
            }
        }

        private bool StartLocalHttpServerCore(bool quiet)
        {
            /// Clean stale Python build artifacts when using a local dev server path
            AssetPathUtility.CleanLocalServerBuildArtifacts();

            if (!TryGetLocalHttpServerCommandParts(out _, out _, out var displayCommand, out var error))
            {
                if (!quiet)
                {
                    EditorUtility.DisplayDialog(
                        "Cannot Start HTTP Server",
                        error ?? "The server command could not be constructed with the current settings.",
                        "OK");
                }
                return false;
            }

            // First, try to stop any existing server (quietly; we'll only warn if the port remains occupied).
            StopLocalHttpServerInternal(quiet: true);

            // If the port is still occupied, don't start and explain why (avoid confusing "refusing to stop" warnings).
            try
            {
                string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
                if (Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
                {
                    var remaining = GetListeningProcessIdsForPort(uri.Port);
                    if (remaining.Count > 0)
                    {
                        if (!quiet)
                        {
                            EditorUtility.DisplayDialog(
                                "Port In Use",
                                $"Cannot start the local HTTP server because port {uri.Port} is already in use by PID(s): " +
                                $"{string.Join(", ", remaining)}\n\n" +
                                $"{ProductInfo.ProductName} will not terminate unrelated processes. Stop the owning process manually or change the HTTP URL.",
                                "OK");
                        }
                        return false;
                    }
                }
            }
            catch { }

            // Source-mode isolation/cache-busting is handled by the generated uvx command.

            // Create a per-launch token + pidfile path so Stop can be deterministic without relying on port/PID heuristics.
            string baseUrlForPid = HttpEndpointUtility.GetLocalBaseUrl();
            Uri.TryCreate(baseUrlForPid, UriKind.Absolute, out var uriForPid);
            int portForPid = uriForPid?.Port ?? 0;
            string instanceToken = Guid.NewGuid().ToString("N");
            string pidFilePath = portForPid > 0 ? GetLocalHttpServerPidFilePath(portForPid) : null;

            string launchCommand = displayCommand;
            if (!string.IsNullOrEmpty(pidFilePath))
            {
                launchCommand = $"{displayCommand} --pidfile {QuoteIfNeeded(pidFilePath)} --unity-instance-token {instanceToken}";
            }

            // First-time-only confirmation. Subsequent launches (and the quiet auto-start path) skip the dialog.
            if (!quiet && !EditorPrefs.GetBool(EditorPrefKeys.HttpServerLaunchConfirmed, false))
            {
                if (!EditorUtility.DisplayDialog(
                    "Start Local HTTP Server",
                    "Start the local MCP server in the background?\n\n" +
                    "It launches headless (no terminal window) and logs progress to the Unity Console. " +
                    "This confirmation is shown only once.",
                    "Start",
                    "Cancel"))
                {
                    return false;
                }
                try { EditorPrefs.SetBool(EditorPrefKeys.HttpServerLaunchConfirmed, true); } catch { }
            }

            string launchLog = portForPid > 0 ? GetLocalHttpServerLaunchLogPath(portForPid) : null;

            try
            {
                // Clear any stale handshake state from prior launches.
                ClearLocalServerPidTracking();
                _lastLaunchedProcess = null;

                // Best-effort: delete stale pidfile if it exists.
                try
                {
                    if (!string.IsNullOrEmpty(pidFilePath) && File.Exists(pidFilePath))
                    {
                        DeletePidFile(pidFilePath);
                    }
                }
                catch { }

                // Truncate the launch log so the tail always reflects the current launch.
                if (!string.IsNullOrEmpty(launchLog))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(launchLog));
                        File.WriteAllText(launchLog, string.Empty);
                    }
                    catch { }
                }

                McpLog.Info("Starting local HTTP server… (first run may take a minute while dependencies install)");

                // Launch the server headless (no terminal window); stdout+stderr go to the launch log.
                string effectiveLog = launchLog ?? Path.Combine(Path.GetTempPath(), "mcp-for-unity-server-launch.log");
                var startInfo = CreateHeadlessProcessStartInfo(launchCommand, effectiveLog);

                // The headless shell is not a login shell, so it does not inherit the user's
                // profile PATH (on macOS, GUI-launched Unity has a minimal PATH). Prepend the
                // platform uv/uvx locations so a bare `uvx`/`uv` resolves the same way the old
                // terminal (login shell) launch did.
                string extraPathPrepend = GetPlatformSpecificPathPrepend();
                if (!string.IsNullOrEmpty(extraPathPrepend))
                {
                    string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    startInfo.EnvironmentVariables["PATH"] = string.IsNullOrEmpty(currentPath)
                        ? extraPathPrepend
                        : (extraPathPrepend + Path.PathSeparator + currentPath);
                }

                _lastLaunchedProcess = System.Diagnostics.Process.Start(startInfo);
                if (!string.IsNullOrEmpty(pidFilePath))
                {
                    StoreLocalHttpServerHandshake(pidFilePath, instanceToken);
                }
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Error($"Failed to start server: {ex.Message}");
                if (!quiet)
                {
                    EditorUtility.DisplayDialog(
                        "Error",
                        $"Failed to start server: {ex.Message}",
                        "OK");
                }
                return false;
            }
        }

        /// <summary>
        /// Stop the local HTTP server by finding the process listening on the configured port
        /// </summary>
        public bool StopLocalHttpServer()
        {
            return StopLocalHttpServerInternal(quiet: false);
        }

        public bool StopManagedLocalHttpServer()
        {
            if (!TryGetLocalHttpServerHandshake(out var pidFilePath, out _))
            {
                return false;
            }

            int port = 0;
            if (!TryGetPortFromPidFilePath(pidFilePath, out port) || port <= 0)
            {
                string baseUrl = HttpEndpointUtility.GetLocalBaseUrl();
                if (IsLocalUrl(baseUrl)
                    && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                    && uri.Port > 0)
                {
                    port = uri.Port;
                }
            }

            if (port <= 0)
            {
                return false;
            }

            return StopLocalHttpServerInternal(quiet: true, portOverride: port, allowNonLocalUrl: true);
        }

        public bool IsLocalHttpServerRunning()
        {
            try
            {
                string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
                if (!IsLocalUrl(httpUrl))
                {
                    return false;
                }

                if (!Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri) || uri.Port <= 0)
                {
                    return false;
                }

                int port = uri.Port;

                // Handshake path: if we have a pidfile+token and the PID is still the listener, treat as running.
                if (TryGetLocalHttpServerHandshake(out var pidFilePath, out var instanceToken)
                    && TryReadPidFromPidFile(pidFilePath, out var pidFromFile)
                    && pidFromFile > 0)
                {
                    var pidsNow = GetListeningProcessIdsForPort(port);
                    if (pidsNow.Contains(pidFromFile))
                    {
                        return true;
                    }
                }

                var pids = GetListeningProcessIdsForPort(port);
                if (pids.Count == 0)
                {
                    return false;
                }

                // Strong signal: stored PID is still the listener.
                if (TryGetStoredLocalServerPid(port, out int storedPid) && storedPid > 0)
                {
                    if (pids.Contains(storedPid))
                    {
                        return true;
                    }
                }

                // Best-effort: if anything listening looks like our server, treat as running.
                foreach (var pid in pids)
                {
                    if (pid <= 0) continue;
                    if (LooksLikeMcpServerProcess(pid))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public bool IsLocalHttpServerReachable()
        {
            try
            {
                string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
                if (!IsLocalUrl(httpUrl))
                {
                    return false;
                }

                if (!Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri) || uri.Port <= 0)
                {
                    return false;
                }

                // 250ms, not 50ms: on a machine busy with test runs or domain reloads a 50ms
                // connect wait produces false "server gone" readings that tore down healthy
                // sessions via the orphaned-session detector (#1207).
                return TryConnectToLocalPort(uri.Host, uri.Port, timeoutMs: 250);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryConnectToLocalPort(string host, int port, int timeoutMs)
        {
            try
            {
                // timeoutMs is an overall budget shared across candidate hosts, so a
                // filtered/dropped first candidate cannot multiply the worst-case wait
                // (this can run on the editor UI tick).
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                foreach (string target in BuildLocalProbeHosts(host))
                {
                    int remainingMs = timeoutMs - (int)elapsed.ElapsedMilliseconds;
                    if (remainingMs <= 0)
                    {
                        break;
                    }

                    try
                    {
                        using (var client = new TcpClient())
                        {
                            var connectTask = client.ConnectAsync(target, port);
                            if (connectTask.Wait(remainingMs) && client.Connected)
                            {
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore per-host failures.
                    }
                }
            }
            catch
            {
                // Ignore probe failures and treat as unreachable.
            }

            return false;
        }

        private static IReadOnlyList<string> BuildLocalProbeHosts(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                host = "127.0.0.1";
            }
            else
            {
                host = host.Trim();
            }

            var hosts = new List<string>();
            AddHostCandidate(hosts, host);

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                // Probe both loopback families for localhost to avoid false negatives on systems where
                // localhost resolution prefers an address family different from the server bind.
                AddHostCandidate(hosts, "127.0.0.1");
                AddHostCandidate(hosts, "::1");
            }
            else if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                AddHostCandidate(hosts, "127.0.0.1");
            }
            else if (string.Equals(host, "::", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(host, "0:0:0:0:0:0:0:0", StringComparison.OrdinalIgnoreCase))
            {
                AddHostCandidate(hosts, "::1");
            }

            return hosts;
        }

        private static void AddHostCandidate(List<string> hosts, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            if (hosts.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            hosts.Add(candidate);
        }

        private bool StopLocalHttpServerInternal(bool quiet, int? portOverride = null, bool allowNonLocalUrl = false)
        {
            string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
            if (!allowNonLocalUrl && !IsLocalUrl(httpUrl))
            {
                if (!quiet)
                {
                    McpLog.Warn("Cannot stop server: URL is not local.");
                }
                return false;
            }

            try
            {
                int port = 0;
                if (portOverride.HasValue)
                {
                    port = portOverride.Value;
                }
                else
                {
                    var uri = new Uri(httpUrl);
                    port = uri.Port;
                }

                if (port <= 0)
                {
                    if (!quiet)
                    {
                        McpLog.Warn("Cannot stop server: Invalid port.");
                    }
                    return false;
                }

                // Guardrails:
                // - Never terminate the Unity Editor process.
                // - Only terminate processes that look like the MCP server (uv/uvx/python running mcp-for-unity).
                // This prevents accidental termination of unrelated services (including Unity itself).
                int unityPid = GetCurrentProcessIdSafe();
                bool stoppedAny = false;

                // Preferred deterministic stop path: if we have a pidfile+token from a Unity-managed launch,
                // validate and terminate exactly that PID.
                if (TryGetLocalHttpServerHandshake(out var pidFilePath, out var instanceToken))
                {
                    // Prefer deterministic stop when Unity started the server (pidfile+token).
                    // If the pidfile isn't available yet (fast quit after start), we can optionally fall back
                    // to port-based heuristics when a port override was supplied (managed-stop path).
                    if (!TryReadPidFromPidFile(pidFilePath, out var pidFromFile) || pidFromFile <= 0)
                    {
                        if (!portOverride.HasValue)
                        {
                            if (!quiet)
                            {
                                McpLog.Warn(
                                    $"Cannot stop local HTTP server on port {port}: pidfile not available yet at '{pidFilePath}'. " +
                                    "If you just started the server, wait a moment and try again.");
                            }
                            return false;
                        }

                        // Managed-stop fallback: proceed with port-based heuristics below.
                        // We intentionally do NOT clear handshake state here; it will be cleared if we successfully
                        // stop a server process and/or the port is freed.
                    }
                    else
                    {
                        // Never kill Unity/Hub.
                        if (unityPid > 0 && pidFromFile == unityPid)
                        {
                            if (!quiet)
                            {
                                McpLog.Warn($"Refusing to stop port {port}: pidfile PID {pidFromFile} is the Unity Editor process.");
                            }
                        }
                        else
                        {
                            var listeners = GetListeningProcessIdsForPort(port);
                            if (listeners.Count == 0)
                            {
                                // Nothing is listening anymore; clear stale handshake state.
                                try { DeletePidFile(pidFilePath); } catch { }
                                ClearLocalServerPidTracking();
                                if (!quiet)
                                {
                                    McpLog.Info($"No process found listening on port {port}");
                                }
                                return false;
                            }
                            bool pidIsListener = listeners.Contains(pidFromFile);
                            bool tokenQueryOk = TryProcessCommandLineContainsInstanceToken(pidFromFile, instanceToken, out bool tokenMatches);
                            bool allowKill;
                            if (tokenQueryOk)
                            {
                                allowKill = tokenMatches;
                            }
                            else
                            {
                                // If token validation is unavailable (e.g. Windows CIM permission issues),
                                // fall back to a stricter heuristic: only allow stop if the PID still looks like our server.
                                allowKill = LooksLikeMcpServerProcess(pidFromFile);
                            }

                            if (pidIsListener && allowKill)
                            {
                                if (TerminateProcess(pidFromFile))
                                {
                                    stoppedAny = true;
                                    try { DeletePidFile(pidFilePath); } catch { }
                                    ClearLocalServerPidTracking();
                                    if (!quiet)
                                    {
                                        McpLog.Info($"Stopped local HTTP server on port {port} (PID: {pidFromFile})");
                                    }
                                    return true;
                                }
                                if (!quiet)
                                {
                                    McpLog.Warn($"Failed to terminate local HTTP server on port {port} (PID: {pidFromFile}).");
                                }
                                return false;
                            }

                            // If the pidfile PID is no longer the active listener, treat handshake state as stale
                            // and continue with guarded port-based heuristics below.
                            if (!pidIsListener)
                            {
                                if (!quiet)
                                {
                                    McpLog.Warn(
                                        $"Stale pidfile for port {port}: pidfile PID {pidFromFile} is not the current listener " +
                                        $"(tokenMatch={tokenMatches}, tokenQueryOk={tokenQueryOk}). Falling back to guarded port heuristics.");
                                }
                                try { DeletePidFile(pidFilePath); } catch { }
                                ClearLocalServerPidTracking();
                            }
                            else
                            {
                                // PID still owns the listener, but identity validation failed.
                                // Fail closed to avoid terminating unrelated processes.
                                if (!quiet)
                                {
                                    McpLog.Warn(
                                        $"Refusing to stop port {port}: pidfile PID {pidFromFile} failed validation " +
                                        $"(listener={pidIsListener}, tokenMatch={tokenMatches}, tokenQueryOk={tokenQueryOk}).");
                                }
                                return false;
                            }
                        }
                    }
                }

                var pids = GetListeningProcessIdsForPort(port);
                if (pids.Count == 0)
                {
                    if (stoppedAny)
                    {
                        // We stopped what Unity started; the port is now free.
                        if (!quiet)
                        {
                            McpLog.Info($"Stopped local HTTP server on port {port}");
                        }
                        ClearLocalServerPidTracking();
                        return true;
                    }

                    if (!quiet)
                    {
                        McpLog.Info($"No process found listening on port {port}");
                    }
                    ClearLocalServerPidTracking();
                    return false;
                }

                // Prefer killing the PID that we previously observed binding this port (if still valid).
                if (TryGetStoredLocalServerPid(port, out int storedPid))
                {
                    if (pids.Contains(storedPid))
                    {
                        string expectedHash = string.Empty;
                        expectedHash = GetStoredArgsHash();

                        // Prefer a fingerprint match (reduces PID reuse risk). If missing (older installs),
                        // fall back to a looser check to avoid leaving orphaned servers after domain reload.
                        if (TryGetUnixProcessArgs(storedPid, out var storedArgsLowerNow))
                        {
                            // Never kill Unity/Hub.
                            // Note: "mcp-for-unity" includes "unity", so detect MCP indicators first.
                            bool storedMentionsMcp = storedArgsLowerNow.Contains("mcp-for-unity")
                                                     || storedArgsLowerNow.Contains("mcp_for_unity")
                                                     || storedArgsLowerNow.Contains("mcpforunity");
                            if (storedArgsLowerNow.Contains("unityhub")
                                || storedArgsLowerNow.Contains("unity hub")
                                || (storedArgsLowerNow.Contains("unity") && !storedMentionsMcp))
                            {
                                if (!quiet)
                                {
                                    McpLog.Warn($"Refusing to stop port {port}: stored PID {storedPid} appears to be a Unity process.");
                                }
                            }
                            else
                            {
                                bool allowKill = false;
                                if (!string.IsNullOrEmpty(expectedHash))
                                {
                                    allowKill = string.Equals(expectedHash, ComputeShortHash(storedArgsLowerNow), StringComparison.OrdinalIgnoreCase);
                                }
                                else
                                {
                                    // Older versions didn't store a fingerprint; accept common server indicators.
                                    allowKill = storedArgsLowerNow.Contains("uvicorn")
                                                || storedArgsLowerNow.Contains("fastmcp")
                                                || storedArgsLowerNow.Contains("mcpforunity")
                                                || storedArgsLowerNow.Contains("mcp-for-unity")
                                                || storedArgsLowerNow.Contains("mcp_for_unity")
                                                || storedArgsLowerNow.Contains("uvx")
                                                || storedArgsLowerNow.Contains("python");
                                }

                                if (allowKill && TerminateProcess(storedPid))
                                {
                                    if (!quiet)
                                    {
                                        McpLog.Info($"Stopped local HTTP server on port {port} (PID: {storedPid})");
                                    }
                                    stoppedAny = true;
                                    ClearLocalServerPidTracking();
                                    // Refresh the PID list to avoid double-work.
                                    pids = GetListeningProcessIdsForPort(port);
                                }
                                else if (!allowKill && !quiet)
                                {
                                    McpLog.Warn($"Refusing to stop port {port}: stored PID {storedPid} did not match expected server fingerprint.");
                                }
                            }
                        }
                    }
                    else
                    {
                        // Stale PID (no longer listening). Clear.
                        ClearLocalServerPidTracking();
                    }
                }

                foreach (var pid in pids)
                {
                    if (pid <= 0) continue;
                    if (unityPid > 0 && pid == unityPid)
                    {
                        if (!quiet)
                        {
                            McpLog.Warn($"Refusing to stop port {port}: owning PID appears to be the Unity Editor process (PID {pid}).");
                        }
                        continue;
                    }

                    if (!LooksLikeMcpServerProcess(pid))
                    {
                        if (!quiet)
                        {
                            McpLog.Warn($"Refusing to stop port {port}: owning PID {pid} does not look like mcp-for-unity.");
                        }
                        continue;
                    }

                    if (TerminateProcess(pid))
                    {
                        McpLog.Info($"Stopped local HTTP server on port {port} (PID: {pid})");
                        stoppedAny = true;
                    }
                    else
                    {
                        if (!quiet)
                        {
                            McpLog.Warn($"Failed to stop process PID {pid} on port {port}");
                        }
                    }
                }

                if (stoppedAny)
                {
                    ClearLocalServerPidTracking();
                }
                return stoppedAny;
            }
            catch (Exception ex)
            {
                if (!quiet)
                {
                    McpLog.Error($"Failed to stop server: {ex.Message}");
                }
                return false;
            }
        }

        private bool TryGetUnixProcessArgs(int pid, out string argsLower)
        {
            return _processDetector.TryGetProcessCommandLine(pid, out argsLower);
        }

        private bool TryGetPortFromPidFilePath(string pidFilePath, out int port)
        {
            return _pidFileManager.TryGetPortFromPidFilePath(pidFilePath, out port);
        }

        private void DeletePidFile(string pidFilePath)
        {
            _pidFileManager.DeletePidFile(pidFilePath);
        }

        private List<int> GetListeningProcessIdsForPort(int port)
        {
            return _processDetector.GetListeningProcessIdsForPort(port);
        }

        private int GetCurrentProcessIdSafe()
        {
            return _processDetector.GetCurrentProcessId();
        }

        private bool LooksLikeMcpServerProcess(int pid)
        {
            return _processDetector.LooksLikeMcpServerProcess(pid);
        }

        private bool TerminateProcess(int pid)
        {
            return _processTerminator.Terminate(pid);
        }

        /// <summary>
        /// Attempts to build the command used for starting the local HTTP server
        /// </summary>
        public bool TryGetLocalHttpServerCommand(out string command, out string error)
        {
            command = null;
            error = null;
            if (!TryGetLocalHttpServerCommandParts(out var fileName, out var args, out var displayCommand, out error))
            {
                return false;
            }

            // Maintain existing behavior: return a single command string suitable for display/copy.
            command = displayCommand;
            return true;
        }

        private bool TryGetLocalHttpServerCommandParts(out string fileName, out string arguments, out string displayCommand, out string error)
        {
            return _commandBuilder.TryBuildCommand(out fileName, out arguments, out displayCommand, out error);
        }

        /// <summary>
        /// Check if the configured HTTP URL is a local address
        /// </summary>
        public bool IsLocalUrl()
        {
            string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
            return IsLocalUrl(httpUrl);
        }

        /// <summary>
        /// Check if a URL is local or bind-all (localhost/loopback and 0.0.0.0/::).
        /// This helper is intentionally broader than local-launch policy checks.
        /// </summary>
        private static bool IsLocalUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;

            try
            {
                var uri = new Uri(url);
                string host = uri.Host;
                return HttpEndpointUtility.IsLoopbackHost(host) || HttpEndpointUtility.IsBindAllInterfacesHost(host);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if the local HTTP server can be started
        /// </summary>
        public bool CanStartLocalServer()
        {
            bool useHttpTransport = EditorConfigurationCache.Instance.UseHttpTransport;
            if (!useHttpTransport)
            {
                return false;
            }

            string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
            return HttpEndpointUtility.IsHttpLocalUrlAllowedForLaunch(httpUrl, out _);
        }

        private System.Diagnostics.ProcessStartInfo CreateTerminalProcessStartInfo(string command)
        {
            return _terminalLauncher.CreateTerminalProcessStartInfo(command);
        }

        private System.Diagnostics.ProcessStartInfo CreateHeadlessProcessStartInfo(string command, string logFilePath)
        {
            return _terminalLauncher.CreateHeadlessProcessStartInfo(command, logFilePath);
        }

        public string GetLocalHttpServerLaunchLogPath()
        {
            string baseUrl = HttpEndpointUtility.GetLocalBaseUrl();
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return GetLocalHttpServerLaunchLogPath(uri.Port);
            }
            return null;
        }

        private string GetLocalHttpServerLaunchLogPath(int port)
        {
            string dir = Path.Combine(_terminalLauncher.GetProjectRootPath(), "Library", "MCPForUnity", "Logs");
            return Path.Combine(dir, $"server-launch-{port}.log");
        }

        public bool HasManagedServerLaunchHandle
        {
            get
            {
                lock (_localHttpLaunchGate)
                {
                    return _lastLaunchedProcess != null;
                }
            }
        }

        public bool IsManagedServerLaunchProcessAlive()
        {
            lock (_localHttpLaunchGate)
            {
                return IsManagedServerLaunchProcessAliveUnsafe();
            }
        }

        public bool HasManagedServerLaunchFailed()
        {
            lock (_localHttpLaunchGate)
            {
                try
                {
                    var proc = _lastLaunchedProcess;
                    return proc != null && proc.HasExited && proc.ExitCode != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        private bool IsManagedServerLaunchProcessAliveUnsafe()
        {
            try
            {
                return _lastLaunchedProcess != null && !_lastLaunchedProcess.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private void DisposeManagedLaunchHandleUnsafe()
        {
            var proc = _lastLaunchedProcess;
            _lastLaunchedProcess = null;
            if (proc == null) return;

            try { proc.Dispose(); }
            catch { }
        }

        public void LogLocalHttpServerLaunchFailure()
        {
            string logPath = GetLocalHttpServerLaunchLogPath();
            string tail = TailFile(logPath, 40);

            string copyHint;
            if (TryGetLocalHttpServerCommand(out var command, out _) && !string.IsNullOrEmpty(command))
            {
                copyHint = $"To run it yourself, copy this command into a terminal:\n{command}";
            }
            else
            {
                copyHint = "Use the \"Manual Server Launch\" foldout to copy the command and run it yourself.";
            }

            string logRef = string.IsNullOrEmpty(logPath) ? "(launch log unavailable)" : logPath;
            string body = string.IsNullOrEmpty(tail) ? "(no output captured)" : tail;

            McpLog.Error(
                "Local HTTP server did not become reachable. " +
                $"Launch log: {logRef}\n{body}\n{copyHint}");
        }

        private static string TailFile(string path, int maxLines)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return string.Empty;
                }

                var lines = File.ReadAllLines(path);
                if (lines.Length <= maxLines)
                {
                    return string.Join(Environment.NewLine, lines).Trim();
                }

                return string.Join(Environment.NewLine, lines.Skip(lines.Length - maxLines)).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Cross-thread hint used by the transport to distinguish an intentional local
    /// server handoff from an unexpected outage. The hint expires automatically so a
    /// failed launch can never suppress diagnostics indefinitely.
    /// </summary>
    internal static class PlannedServerRestartState
    {
        private static long _activeUntilUtcTicks;

        internal static bool IsActive =>
            DateTime.UtcNow.Ticks < System.Threading.Interlocked.Read(ref _activeUntilUtcTicks);

        internal static void Begin(TimeSpan timeout)
        {
            long until = DateTime.UtcNow.Add(timeout).Ticks;
            System.Threading.Interlocked.Exchange(ref _activeUntilUtcTicks, until);
        }

        internal static void MarkReconnected()
        {
            System.Threading.Interlocked.Exchange(ref _activeUntilUtcTicks, 0L);
        }

        internal static void Clear() => MarkReconnected();
    }

    /// <summary>
    /// Performs a response-safe restart of the Unity-managed local MCP server. Work is
    /// deferred to Editor update ticks so the command response reaches the caller before
    /// the current server process is stopped.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlannedServerRestartCoordinator
    {
        internal const int ResponseGraceMilliseconds = 750;
        private const int PortReleaseTimeoutMilliseconds = 5000;
        private static readonly TimeSpan PlannedRestartTimeout = TimeSpan.FromMinutes(2);
        private static readonly object Gate = new object();

        private enum RestartPhase
        {
            Idle,
            ResponseGrace,
            AwaitingPortRelease,
            AwaitingReplacement
        }

        private static RestartPhase _phase;
        private static DateTime _nextActionUtc;
        private static DateTime _portReleaseDeadlineUtc;
        private static DateTime _replacementDeadlineUtc;

        internal static Func<DateTime> UtcNowProvider = () => DateTime.UtcNow;
        internal static Func<bool> CanScheduleProvider = () =>
            MCPServiceLocator.Server.IsLocalHttpServerRunning();
        internal static Func<bool> StopProvider = () =>
            MCPServiceLocator.Server.StopManagedLocalHttpServer();
        internal static Func<bool> IsReachableProvider = () =>
            MCPServiceLocator.Server.IsLocalHttpServerReachable();
        internal static Func<bool> PreviousLaunchProcessAliveProvider = () =>
            MCPServiceLocator.Server.IsManagedServerLaunchProcessAlive();
        internal static Func<bool> LaunchLogAvailableProvider = IsLaunchLogAvailable;
        internal static Func<bool> StartProvider = () =>
            MCPServiceLocator.Server.StartLocalHttpServer(quiet: true);
        internal static Func<bool> HasLaunchHandleProvider = () =>
            MCPServiceLocator.Server.HasManagedServerLaunchHandle;
        internal static Func<bool> HasLaunchFailedProvider = () =>
            MCPServiceLocator.Server.HasManagedServerLaunchFailed();
        internal static Action LogLaunchFailureProvider = () =>
            MCPServiceLocator.Server.LogLocalHttpServerLaunchFailure();

        static PlannedServerRestartCoordinator() { }

        /// <returns>Null on success; otherwise a user-facing validation error.</returns>
        internal static string Schedule()
        {
            lock (Gate)
            {
                if (_phase != RestartPhase.Idle)
                {
                    return null;
                }

                bool canSchedule;
                try
                {
                    canSchedule = CanScheduleProvider();
                }
                catch (Exception ex)
                {
                    return $"Could not validate the managed local MCP server: {ex.Message}";
                }

                if (!canSchedule)
                {
                    return "A running Unity-managed local MCP server is required for a planned restart.";
                }

                DateTime now = UtcNowProvider();
                _phase = RestartPhase.ResponseGrace;
                _nextActionUtc = now.AddMilliseconds(ResponseGraceMilliseconds);
                _portReleaseDeadlineUtc = DateTime.MinValue;
                _replacementDeadlineUtc = DateTime.MinValue;
                PlannedServerRestartState.Begin(PlannedRestartTimeout);
                EditorApplication.update -= Tick;
                EditorApplication.update += Tick;
            }

            McpLog.Info("Planned MCP server restart scheduled; the Editor will reconnect automatically.");
            return null;
        }

        private static void Tick()
        {
            lock (Gate)
            {
                if (_phase == RestartPhase.Idle)
                {
                    EditorApplication.update -= Tick;
                    return;
                }

                DateTime now = UtcNowProvider();
                if (_phase == RestartPhase.AwaitingReplacement)
                {
                    bool replacementReachable;
                    try { replacementReachable = IsReachableProvider(); }
                    catch { replacementReachable = false; }
                    if (replacementReachable)
                    {
                        _phase = RestartPhase.Idle;
                        EditorApplication.update -= Tick;
                        McpLog.Info("Replacement MCP server is reachable; waiting for transport registration.");
                        return;
                    }

                    bool launchFailed = false;
                    try
                    {
                        launchFailed = HasLaunchHandleProvider()
                            && HasLaunchFailedProvider();
                    }
                    catch { }
                    if (launchFailed)
                    {
                        try { LogLaunchFailureProvider(); }
                        catch { }
                        Fail("The replacement MCP server launcher exited with an error.");
                        return;
                    }

                    if (now >= _replacementDeadlineUtc)
                    {
                        Fail("The replacement MCP server did not become reachable in time.");
                    }
                    return;
                }

                if (_phase == RestartPhase.ResponseGrace)
                {
                    if (now < _nextActionUtc)
                    {
                        return;
                    }

                    bool stopped;
                    try
                    {
                        stopped = StopProvider();
                    }
                    catch (Exception ex)
                    {
                        Fail($"Could not stop the managed MCP server: {ex.Message}");
                        return;
                    }

                    if (!stopped)
                    {
                        bool stillReachable;
                        try { stillReachable = IsReachableProvider(); }
                        catch { stillReachable = true; }
                        if (stillReachable)
                        {
                            Fail("Could not stop the managed MCP server; its launch handshake no longer matches.");
                            return;
                        }
                    }

                    _phase = RestartPhase.AwaitingPortRelease;
                    _portReleaseDeadlineUtc = now.AddMilliseconds(PortReleaseTimeoutMilliseconds);
                }

                bool reachable;
                try
                {
                    reachable = IsReachableProvider();
                }
                catch (Exception ex)
                {
                    Fail($"Could not probe the MCP server port: {ex.Message}");
                    return;
                }

                bool previousLaunchAlive;
                bool launchLogAvailable;
                try { previousLaunchAlive = PreviousLaunchProcessAliveProvider(); }
                catch { previousLaunchAlive = true; }
                try { launchLogAvailable = LaunchLogAvailableProvider(); }
                catch { launchLogAvailable = false; }

                if (reachable || previousLaunchAlive || !launchLogAvailable)
                {
                    if (now >= _portReleaseDeadlineUtc)
                    {
                        Fail("The managed MCP server did not finish releasing its port and launch resources in time.");
                    }
                    return;
                }

                bool started;
                try
                {
                    started = StartProvider();
                }
                catch (Exception ex)
                {
                    Fail($"Could not launch the replacement MCP server: {ex.Message}");
                    return;
                }

                if (!started)
                {
                    Fail("The replacement MCP server could not be launched. Check the server launch log.");
                    return;
                }

                _phase = RestartPhase.AwaitingReplacement;
                _replacementDeadlineUtc = now.Add(PlannedRestartTimeout);
                McpLog.Info("Replacement MCP server launch started; waiting for reachability and transport reconnect.");
            }
        }

        private static bool IsLaunchLogAvailable()
        {
            string path = MCPServiceLocator.Server.GetLocalHttpServerLaunchLogPath();
            if (string.IsNullOrEmpty(path))
            {
                return true;
            }

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                using (new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.ReadWrite))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void Fail(string message)
        {
            _phase = RestartPhase.Idle;
            EditorApplication.update -= Tick;
            PlannedServerRestartState.Clear();
            McpLog.Error($"Planned MCP server restart failed: {message}");
        }

        internal static void TickForTests() => Tick();

        internal static void ResetForTests()
        {
            lock (Gate)
            {
                EditorApplication.update -= Tick;
                _phase = RestartPhase.Idle;
                _nextActionUtc = DateTime.MinValue;
                _portReleaseDeadlineUtc = DateTime.MinValue;
                _replacementDeadlineUtc = DateTime.MinValue;
                UtcNowProvider = () => DateTime.UtcNow;
                CanScheduleProvider = () => MCPServiceLocator.Server.IsLocalHttpServerRunning();
                StopProvider = () => MCPServiceLocator.Server.StopManagedLocalHttpServer();
                IsReachableProvider = () => MCPServiceLocator.Server.IsLocalHttpServerReachable();
                PreviousLaunchProcessAliveProvider = () => MCPServiceLocator.Server.IsManagedServerLaunchProcessAlive();
                LaunchLogAvailableProvider = IsLaunchLogAvailable;
                StartProvider = () => MCPServiceLocator.Server.StartLocalHttpServer(quiet: true);
                HasLaunchHandleProvider = () => MCPServiceLocator.Server.HasManagedServerLaunchHandle;
                HasLaunchFailedProvider = () => MCPServiceLocator.Server.HasManagedServerLaunchFailed();
                LogLaunchFailureProvider = () => MCPServiceLocator.Server.LogLocalHttpServerLaunchFailure();
                PlannedServerRestartState.Clear();
            }
        }
    }
}
