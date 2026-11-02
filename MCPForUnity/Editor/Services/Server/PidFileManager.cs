using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MCPForUnity.Editor.Constants;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Server
{
    /// <summary>
    /// Manages PID files and handshake state for the local HTTP server.
    /// Handles persistence of server process information across Unity domain reloads.
    /// </summary>
    public class PidFileManager : IPidFileManager
    {
        // Ownership survives only this Editor process and its domain reloads.
        internal static string ScopedKey(string key) => key + "." +
            MCPForUnity.Editor.Helpers.ProjectIdentityUtility.GetProjectHash();

        /// <inheritdoc/>
        public string GetPidDirectory()
        {
            return Path.Combine(GetProjectRootPath(), "Library", "MCPForUnity", "RunState");
        }

        /// <inheritdoc/>
        public string GetPidFilePath(int port)
        {
            string dir = GetPidDirectory();
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"mcp_http_{port}.pid");
        }

        /// <inheritdoc/>
        public bool TryReadPid(string pidFilePath, out int pid)
        {
            pid = 0;
            try
            {
                if (string.IsNullOrEmpty(pidFilePath) || !File.Exists(pidFilePath))
                {
                    return false;
                }

                string text = File.ReadAllText(pidFilePath).Trim();
                if (int.TryParse(text, out pid))
                {
                    return pid > 0;
                }

                // Best-effort: tolerate accidental extra whitespace/newlines.
                var firstLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (int.TryParse(firstLine, out pid))
                {
                    return pid > 0;
                }

                pid = 0;
                return false;
            }
            catch
            {
                pid = 0;
                return false;
            }
        }

        /// <inheritdoc/>
        public bool TryGetPortFromPidFilePath(string pidFilePath, out int port)
        {
            port = 0;
            if (string.IsNullOrEmpty(pidFilePath))
            {
                return false;
            }

            try
            {
                string fileName = Path.GetFileNameWithoutExtension(pidFilePath);
                if (string.IsNullOrEmpty(fileName))
                {
                    return false;
                }

                const string prefix = "mcp_http_";
                if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string portText = fileName.Substring(prefix.Length);
                return int.TryParse(portText, out port) && port > 0;
            }
            catch
            {
                port = 0;
                return false;
            }
        }

        /// <inheritdoc/>
        public void DeletePidFile(string pidFilePath)
        {
            try
            {
                if (!string.IsNullOrEmpty(pidFilePath) && File.Exists(pidFilePath))
                {
                    File.Delete(pidFilePath);
                }
            }
            catch { }
        }

        /// <inheritdoc/>
        public void StoreHandshake(string pidFilePath, string instanceToken)
        {
            try
            {
                if (!string.IsNullOrEmpty(pidFilePath))
                {
                    SessionState.SetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidFilePath), pidFilePath);
                }
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(instanceToken))
                {
                    SessionState.SetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerInstanceToken), instanceToken);
                }
            }
            catch { }
        }

        /// <inheritdoc/>
        public bool TryGetHandshake(out string pidFilePath, out string instanceToken)
        {
            pidFilePath = null;
            instanceToken = null;
            try
            {
                pidFilePath = SessionState.GetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidFilePath), string.Empty);
                instanceToken = SessionState.GetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerInstanceToken), string.Empty);
                if (string.IsNullOrEmpty(pidFilePath) || string.IsNullOrEmpty(instanceToken))
                {
                    pidFilePath = null;
                    instanceToken = null;
                    return false;
                }
                return true;
            }
            catch
            {
                pidFilePath = null;
                instanceToken = null;
                return false;
            }
        }

        /// <inheritdoc/>
        public void StoreTracking(int pid, int port, string argsHash = null)
        {
            try { EditorPrefs.SetInt(ScopedKey(EditorPrefKeys.LastLocalHttpServerPid), pid); } catch { }
            try { EditorPrefs.SetInt(ScopedKey(EditorPrefKeys.LastLocalHttpServerPort), port); } catch { }
            try { EditorPrefs.SetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerStartedUtc), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); } catch { }
            try
            {
                if (!string.IsNullOrEmpty(argsHash))
                {
                    EditorPrefs.SetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidArgsHash), argsHash);
                }
                else
                {
                    EditorPrefs.DeleteKey(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidArgsHash));
                }
            }
            catch { }
        }

        /// <inheritdoc/>
        public bool TryGetStoredPid(int expectedPort, out int pid)
        {
            pid = 0;
            try
            {
                int storedPid = EditorPrefs.GetInt(ScopedKey(EditorPrefKeys.LastLocalHttpServerPid), 0);
                int storedPort = EditorPrefs.GetInt(ScopedKey(EditorPrefKeys.LastLocalHttpServerPort), 0);
                string storedUtc = EditorPrefs.GetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerStartedUtc), string.Empty);

                if (storedPid <= 0 || storedPort != expectedPort)
                {
                    return false;
                }

                // This is short-lived discovery information, never stop authority.
                // Only the authenticated launch-owner control path may authorize termination.
                if (!string.IsNullOrEmpty(storedUtc)
                    && DateTime.TryParse(storedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startedAt))
                {
                    if ((DateTime.UtcNow - startedAt) > TimeSpan.FromHours(6))
                    {
                        return false;
                    }
                }

                pid = storedPid;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <inheritdoc/>
        public string GetStoredArgsHash()
        {
            try
            {
                return EditorPrefs.GetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidArgsHash), string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <inheritdoc/>
        public void ClearTracking()
        {
            try { EditorPrefs.DeleteKey(ScopedKey(EditorPrefKeys.LastLocalHttpServerPid)); } catch { }
            try { EditorPrefs.DeleteKey(ScopedKey(EditorPrefKeys.LastLocalHttpServerPort)); } catch { }
            try { EditorPrefs.DeleteKey(ScopedKey(EditorPrefKeys.LastLocalHttpServerStartedUtc)); } catch { }
            try { EditorPrefs.DeleteKey(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidArgsHash)); } catch { }
            try { SessionState.EraseString(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidFilePath)); } catch { }
            try { SessionState.EraseString(ScopedKey(EditorPrefKeys.LastLocalHttpServerInstanceToken)); } catch { }
        }

        /// <inheritdoc/>
        public string ComputeShortHash(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            try
            {
                using var sha = SHA256.Create();
                byte[] bytes = Encoding.UTF8.GetBytes(input);
                byte[] hash = sha.ComputeHash(bytes);
                // 8 bytes => 16 hex chars is plenty as a stable fingerprint for our purposes.
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8 && i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetProjectRootPath()
        {
            try
            {
                // Application.dataPath is ".../<Project>/Assets"
                return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            }
            catch
            {
                return Application.dataPath;
            }
        }
    }
}
