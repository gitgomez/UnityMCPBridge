using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MCPForUnityTests.Editor
{
    /// <summary>
    /// Shared test utilities for EditMode tests across the MCP for Unity test suite.
    /// Consolidates common patterns to avoid duplication across test files.
    /// </summary>
    public static class TestUtilities
    {
        /// <summary>
        /// Safely converts a command result to JObject, handling both JSON objects and other types.
        /// Returns an empty JObject if result is null.
        /// </summary>
        public static JObject ToJObject(object result)
        {
            if (result == null) return new JObject();
            return result as JObject ?? JObject.FromObject(result);
        }

        /// <summary>
        /// Creates all parent directories for the given asset path if they don't exist.
        /// Handles normalization and validates against dangerous patterns.
        /// </summary>
        /// <param name="folderPath">An Assets-relative folder path (e.g., "Assets/Temp/MyFolder")</param>
        public static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
                return;

            var sanitized = MCPForUnity.Editor.Helpers.AssetPathUtility.SanitizeAssetPath(folderPath);
            if (string.Equals(sanitized, "Assets", StringComparison.OrdinalIgnoreCase))
                return;

            var parts = sanitized.Split('/');
            string current = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        /// <summary>
        /// Waits for Unity to finish compiling and updating, with a configurable timeout.
        /// Some EditMode tests trigger script compilation/domain reload. 
        /// Tools intentionally return "compiling_or_reloading" during these windows.
        /// </summary>
        /// <param name="timeoutSeconds">Maximum time to wait before failing the test.</param>
        public static IEnumerator WaitForUnityReady(double timeoutSeconds = 30.0)
        {
            double start = EditorApplication.timeSinceStartup;
            while (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                if (EditorApplication.timeSinceStartup - start > timeoutSeconds)
                {
                    Assert.Fail($"Timed out waiting for Unity to finish compiling/updating (>{timeoutSeconds:0.0}s).");
                }
                yield return null;
            }
        }

        /// <summary>
        /// Finds a fallback shader for creating materials in tests.
        /// Tries modern pipelines first, then falls back to Standard/Unlit.
        /// </summary>
        /// <returns>A shader suitable for test materials, or null if none found.</returns>
        public static Shader FindFallbackShader()
        {
            return Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("HDRP/Lit")
                ?? Shader.Find("Standard")
                ?? Shader.Find("Unlit/Color");
        }

        /// <summary>
        /// Safely deletes an asset if it exists.
        /// </summary>
        /// <param name="path">The asset path to delete.</param>
        public static void SafeDeleteAsset(string path)
        {
            if (!string.IsNullOrEmpty(path) && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        /// <summary>
        /// Cleans up empty parent folders recursively while preserving the shared
        /// "Assets/Temp" test root and the project "Assets" root.
        /// </summary>
        /// <param name="folderPath">The starting folder path to check.</param>
        public static void CleanupEmptyParentFolders(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return;

            var current = folderPath.Replace('\\', '/').TrimEnd('/');
            while (!string.IsNullOrEmpty(current)
                && current != "Assets"
                && current != "Assets/Temp")
            {
                if (AssetDatabase.IsValidFolder(current))
                {
                    try
                    {
                        var dirs = Directory.GetDirectories(current);
                        var files = Directory.GetFiles(current);
                        if (dirs.Length == 0 && files.Length == 0)
                        {
                            AssetDatabase.DeleteAsset(current);
                            current = Path.GetDirectoryName(current)?.Replace('\\', '/');
                        }
                        else
                        {
                            break;
                        }
                    }
                    catch
                    {
                        break;
                    }
                }
                else
                {
                    try
                    {
                        if (Directory.Exists(current)
                            && Directory.GetDirectories(current).Length == 0
                            && Directory.GetFiles(current).Length == 0)
                        {
                            Directory.Delete(current);
                            var metaPath = current + ".meta";
                            if (File.Exists(metaPath))
                            {
                                File.Delete(metaPath);
                            }
                        }
                    }
                    catch
                    {
                        break;
                    }
                    current = Path.GetDirectoryName(current)?.Replace('\\', '/');
                }
            }
        }
    }

    /// <summary>
    /// Synchronous IMcpTransportClient fake for transport-lifecycle tests: StartAsync
    /// completes immediately with StartResult so retry loops run without awaits
    /// (the test framework floor cannot run async tests).
    /// </summary>
    public sealed class FakeTransportClient : IMcpTransportClient, IReloadLifecycleTransport
    {
        public bool StartResult = true;
        public int StartCalls;
        public int ReloadNotifications;
        public string LastReloadReason;
        public Action OnStart;

        public bool IsConnected { get; private set; }
        public string TransportName => "http";
        public TransportState State { get; private set; }
            = TransportState.Disconnected("http");

        public Task<bool> StartAsync()
        {
            StartCalls++;
            OnStart?.Invoke();
            IsConnected = StartResult;
            State = StartResult
                ? TransportState.Connected("http")
                : TransportState.Disconnected("http", "fake start failure");
            return Task.FromResult(StartResult);
        }

        public Task StopAsync()
        {
            IsConnected = false;
            State = TransportState.Disconnected("http");
            return Task.CompletedTask;
        }

        public Task<bool> VerifyAsync()
            => Task.FromResult(IsConnected);

        public Task ReregisterToolsAsync()
            => Task.CompletedTask;

        public bool NotifyReloading(string reason = null)
        {
            ReloadNotifications++;
            LastReloadReason = reason;
            return true;
        }
    }
}
