using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MCPForUnity.Editor.Helpers;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Server
{
    /// <summary>
    /// Platform-specific process inspection for detecting MCP server processes.
    /// </summary>
    public class ProcessDetector : IProcessDetector
    {
        /// <inheritdoc/>
        public string NormalizeForMatch(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <inheritdoc/>
        public int GetCurrentProcessId()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { return -1; }
        }

        /// <inheritdoc/>
        public bool ProcessExists(int pid)
        {
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // On Windows, use tasklist to check if process exists
                    bool ok = ExecPath.TryRun("tasklist", $"/FI \"PID eq {pid}\"", Application.dataPath, out var stdout, out var stderr, 5000);
                    string combined = ((stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty)).ToLowerInvariant();
                    return ok && combined.Contains(pid.ToString());
                }

                // Unix: ps exits non-zero when PID is not found.
                string psPath = "/bin/ps";
                if (!File.Exists(psPath)) psPath = "ps";
                ExecPath.TryRun(psPath, $"-p {pid} -o pid=", Application.dataPath, out var psStdout, out var psStderr, 2000);
                string combined2 = ((psStdout ?? string.Empty) + "\n" + (psStderr ?? string.Empty)).Trim();
                return !string.IsNullOrEmpty(combined2) && combined2.Any(char.IsDigit);
            }
            catch
            {
                return true; // Assume it exists if we cannot verify.
            }
        }

        /// <inheritdoc/>
        public bool TryGetProcessCommandLine(int pid, out string argsLower)
        {
            argsLower = string.Empty;
            if (pid <= 0)
            {
                return false;
            }

            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // WMIC is absent on current Windows installations, but remains a cheap
                    // compatibility path for older supported systems.
                    ExecPath.TryRun(
                        "wmic.exe",
                        $"process where \"ProcessId={pid}\" get CommandLine /value",
                        Application.dataPath,
                        out var wmicOut,
                        out _,
                        5000);
                    if (TryExtractWmicCommandLine(wmicOut, out string commandLine))
                    {
                        argsLower = NormalizeForMatch(commandLine);
                        return true;
                    }

                    // PowerShell/CIM is the supported replacement for WMIC on Windows 10/11.
                    string script =
                        $"$p=Get-CimInstance Win32_Process -Filter 'ProcessId={pid}' -ErrorAction Stop;" +
                        "if($null-ne$p){[Console]::Out.Write($p.CommandLine)}";
                    bool cimOk = ExecPath.TryRun(
                        "powershell.exe",
                        $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
                        Application.dataPath,
                        out var cimOut,
                        out _,
                        5000);
                    if (cimOk && !string.IsNullOrWhiteSpace(cimOut))
                    {
                        argsLower = NormalizeForMatch(cimOut);
                        return true;
                    }

                    return false;
                }

                // Unix: ps -p pid -ww -o args=
                string psPath = "/bin/ps";
                if (!File.Exists(psPath)) psPath = "ps";

                bool ok = ExecPath.TryRun(psPath, $"-p {pid} -ww -o args=", Application.dataPath, out var stdout, out var stderr, 5000);
                if (!ok && string.IsNullOrWhiteSpace(stdout))
                {
                    return false;
                }
                string combined = ((stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty)).Trim();
                if (string.IsNullOrEmpty(combined)) return false;
                // Normalize for matching to tolerate ps wrapping/newlines.
                argsLower = NormalizeForMatch(combined);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <inheritdoc/>
        public List<int> GetListeningProcessIdsForPort(int port)
        {
            var results = new List<int>();
            try
            {
                string stdout, stderr;
                bool success;

                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // Run netstat -ano directly (without findstr) and filter in C#.
                    // Using findstr in a pipe causes the entire command to return exit code 1 when no matches are found,
                    // which ExecPath.TryRun interprets as failure. Running netstat alone gives us exit code 0 on success.
                    ExecPath.TryRun("netstat.exe", "-ano -p TCP", Application.dataPath, out stdout, out stderr);

                    // The state label is localized (for example LISTENING vs. ABHÖREN).
                    // Identify listener rows structurally by their zero-valued foreign endpoint.
                    results.AddRange(ParseWindowsNetstatListeners(stdout, port));
                }
                else
                {
                    // lsof: only return LISTENers (avoids capturing random clients)
                    // Use /usr/sbin/lsof directly as it might not be in PATH for Unity
                    string lsofPath = "/usr/sbin/lsof";
                    if (!File.Exists(lsofPath)) lsofPath = "lsof"; // Fallback

                    // -nP: avoid DNS/service name lookups; faster and less error-prone
                    success = ExecPath.TryRun(lsofPath, $"-nP -iTCP:{port} -sTCP:LISTEN -t", Application.dataPath, out stdout, out stderr);
                    if (success && !string.IsNullOrWhiteSpace(stdout))
                    {
                        var pidStrings = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var pidString in pidStrings)
                        {
                            if (int.TryParse(pidString.Trim(), out int parsedPid))
                            {
                                results.Add(parsedPid);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Error checking port {port}: {ex.Message}");
            }
            return results.Distinct().ToList();
        }

        /// <inheritdoc/>
        public bool LooksLikeMcpServerProcess(int pid)
        {
            if (pid <= 0)
            {
                return false;
            }

            try
            {
                // Windows: validate the full command line via WMIC or PowerShell/CIM. If
                // neither is available, only trust a specifically named MCP executable.
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    ExecPath.TryRun("cmd.exe", $"/c tasklist /FI \"PID eq {pid}\"", Application.dataPath, out var tasklistOut, out var tasklistErr, 5000);
                    string tasklistCombined = ((tasklistOut ?? string.Empty) + "\n" + (tasklistErr ?? string.Empty)).ToLowerInvariant();
                    bool specificallyNamedMcp = MentionsMcpServer(tasklistCombined);

                    if (TryGetProcessCommandLine(pid, out string commandLine))
                    {
                        return LooksLikeMcpServerCommandLine(commandLine);
                    }

                    return specificallyNamedMcp;
                }

                // macOS/Linux: ps -p pid -ww -o comm= -o args=
                // Use -ww to avoid truncating long command lines (important for reliably spotting 'mcp-for-unity').
                // Use an absolute ps path to avoid relying on PATH inside the Unity Editor process.
                string psPath = "/bin/ps";
                if (!File.Exists(psPath)) psPath = "ps";
                // Important: ExecPath.TryRun returns false when exit code != 0, but ps output can still be useful.
                // Always parse stdout/stderr regardless of exit code to avoid false negatives.
                ExecPath.TryRun(psPath, $"-p {pid} -ww -o comm= -o args=", Application.dataPath, out var psOut, out var psErr, 5000);
                string raw = ((psOut ?? string.Empty) + "\n" + (psErr ?? string.Empty)).Trim();
                string s = raw.ToLowerInvariant();
                string sCompact = NormalizeForMatch(raw);
                if (!string.IsNullOrEmpty(s))
                {
                    bool mentionsMcp = sCompact.Contains("mcp-for-unity")
                                       || sCompact.Contains("mcp_for_unity")
                                       || sCompact.Contains("mcpforunity");

                    // If it explicitly mentions the server package/entrypoint, that is sufficient.
                    // Note: Check before Unity exclusion since "mcp-for-unity" contains "unity".
                    if (mentionsMcp)
                    {
                        return true;
                    }

                    // Explicitly never kill Unity / Unity Hub processes
                    // Note: explicit !mentionsMcp is defensive; we already return early for mentionsMcp above.
                    if (s.Contains("unityhub") || s.Contains("unity hub") || (s.Contains("unity") && !mentionsMcp))
                    {
                        return false;
                    }

                    // Positive indicators
                    bool mentionsUvx = s.Contains("uvx") || s.Contains(" uvx ");
                    bool mentionsUv = s.Contains("uv ") || s.Contains("/uv");
                    bool mentionsPython = s.Contains("python");
                    bool mentionsUvicorn = s.Contains("uvicorn");
                    bool mentionsTransport = sCompact.Contains("--transporthttp") || (sCompact.Contains("--transport") && sCompact.Contains("http"));

                    // Accept if it looks like uv/uvx/python launching our server package/entrypoint
                    if ((mentionsUvx || mentionsUv || mentionsPython || mentionsUvicorn) && mentionsTransport)
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        internal static List<int> ParseWindowsNetstatListeners(string stdout, int port)
        {
            var results = new List<int>();
            if (port <= 0 || port > 65535 || string.IsNullOrWhiteSpace(stdout))
            {
                return results;
            }

            var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !string.Equals(parts[0], "TCP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryGetEndpointPort(parts[1], out int localPort) || localPort != port)
                {
                    continue;
                }

                // A TCP listener has no peer, represented by a foreign port of zero.
                // This remains stable when the human-readable state column is localized.
                if (!TryGetEndpointPort(parts[2], out int foreignPort) || foreignPort != 0)
                {
                    continue;
                }

                if (int.TryParse(parts[parts.Length - 1], out int pid) && pid > 0)
                {
                    results.Add(pid);
                }
            }

            return results.Distinct().ToList();
        }

        internal static bool LooksLikeMcpServerCommandLine(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return false;
            }

            string compact = NormalizeStatic(commandLine);
            if (MentionsMcpServer(compact))
            {
                return true;
            }

            bool mentionsLauncher = compact.Contains("python")
                                    || compact.Contains("uvx")
                                    || compact.Contains("uvicorn")
                                    || compact.Contains("fastmcp");
            bool mentionsHttpTransport = compact.Contains("--transporthttp");
            return mentionsLauncher && mentionsHttpTransport;
        }

        private static bool TryExtractWmicCommandLine(string output, out string commandLine)
        {
            commandLine = string.Empty;
            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            const string prefix = "CommandLine=";
            int index = output.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            commandLine = output.Substring(index + prefix.Length).Trim();
            return !string.IsNullOrWhiteSpace(commandLine);
        }

        private static bool TryGetEndpointPort(string endpoint, out int port)
        {
            port = 0;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return false;
            }

            int separator = endpoint.LastIndexOf(':');
            return separator >= 0
                && separator + 1 < endpoint.Length
                && int.TryParse(endpoint.Substring(separator + 1), out port);
        }

        private static bool MentionsMcpServer(string value)
        {
            string compact = NormalizeStatic(value);
            return compact.Contains("mcp-for-unity")
                || compact.Contains("mcp_for_unity")
                || compact.Contains("mcpforunity")
                || compact.Contains("mcpforunityserver");
        }

        private static string NormalizeStatic(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
