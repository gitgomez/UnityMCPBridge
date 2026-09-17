using System;
using System.IO;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Services.Transport
{
    internal sealed class CommandStateConflictException : InvalidOperationException
    {
        internal CommandStateConflictException(string code, string message, JObject data)
            : base(message)
        {
            Code = code;
            Data = data ?? new JObject();
        }

        internal string Code { get; }
        internal new JObject Data { get; }
    }

    /// <summary>
    /// Maintains one editor-session epoch and monotonic coarse revisions. SessionState
    /// survives domain reload but resets on a full Editor restart, which prevents an
    /// instance ID from being mistaken for a stable cross-process identity.
    /// </summary>
    [InitializeOnLoad]
    internal static class CommandRuntimeState
    {
        private const string EpochKey = "MCPForUnity.CommandRuntime.State.Epoch";
        private const string RevisionKey = "MCPForUnity.CommandRuntime.State.Revision";
        private const string SceneRevisionKey = "MCPForUnity.CommandRuntime.State.SceneRevision";
        private const string AssetRevisionKey = "MCPForUnity.CommandRuntime.State.AssetRevision";
        private const string SceneSaveSequenceKey = "MCPForUnity.CommandRuntime.State.SceneSaveSequence";
        private const string LastSceneSavedPathKey = "MCPForUnity.CommandRuntime.State.LastSceneSavedPath";
        private const string LastSceneSavedMtimeUnixNsKey = "MCPForUnity.CommandRuntime.State.LastSceneSavedMtimeUnixNs";
        private const string LastSceneSavedUnixMsKey = "MCPForUnity.CommandRuntime.State.LastSceneSavedUnixMs";
        private const string EditorSaveReceiptsKey = "MCPForUnity.CommandRuntime.State.EditorSaveReceipts";
        private const long UnixEpochTicks = 621355968000000000L;
        private const int MaxEditorSaveReceipts = 64;

        private static readonly object StateLock = new();
        private static string _epoch;
        private static long _revision;
        private static long _sceneRevision;
        private static long _assetRevision;
        private static long _sceneSaveSequence;
        private static string _lastSceneSavedPath;
        private static long _lastSceneSavedMtimeUnixNs;
        private static long _lastSceneSavedUnixMs;
        private static JArray _editorSaveReceipts;

        static CommandRuntimeState()
        {
            lock (StateLock)
            {
                _epoch = SessionState.GetString(EpochKey, string.Empty);
                if (string.IsNullOrEmpty(_epoch))
                {
                    _epoch = Guid.NewGuid().ToString("D");
                    SessionState.SetString(EpochKey, _epoch);
                }

                _revision = ReadLong(RevisionKey);
                _sceneRevision = ReadLong(SceneRevisionKey);
                _assetRevision = ReadLong(AssetRevisionKey);
                _sceneSaveSequence = ReadLong(SceneSaveSequenceKey);
                _lastSceneSavedPath = SessionState.GetString(LastSceneSavedPathKey, string.Empty);
                _lastSceneSavedMtimeUnixNs = ReadLong(LastSceneSavedMtimeUnixNsKey);
                _lastSceneSavedUnixMs = ReadLong(LastSceneSavedUnixMsKey);
                _editorSaveReceipts = ReadEditorSaveReceipts();
                if (_editorSaveReceipts.Count == 0
                    && _sceneSaveSequence > 0L
                    && !string.IsNullOrEmpty(_lastSceneSavedPath)
                    && _lastSceneSavedMtimeUnixNs > 0L)
                {
                    _editorSaveReceipts.Add(new JObject
                    {
                        ["epoch"] = _epoch,
                        ["sequence"] = _sceneSaveSequence,
                        ["kind"] = "scene",
                        ["path"] = _lastSceneSavedPath,
                        ["mtime_unix_ns"] = _lastSceneSavedMtimeUnixNs,
                        ["mtime_unix_100ns"] = _lastSceneSavedMtimeUnixNs / 100L,
                        ["saved_unix_ms"] = _lastSceneSavedUnixMs
                    });
                }
            }

            EditorApplication.hierarchyChanged += MarkSceneChange;
            EditorApplication.projectChanged += MarkAssetChange;
            Undo.undoRedoPerformed += MarkSceneChange;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            PrefabStage.prefabSaved += OnPrefabSaved;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        internal static string Epoch
        {
            get
            {
                lock (StateLock)
                {
                    return _epoch;
                }
            }
        }

        internal static JObject Snapshot()
        {
            lock (StateLock)
            {
                return new JObject
                {
                    ["epoch"] = _epoch,
                    ["revision"] = _revision,
                    ["scene_revision"] = _sceneRevision,
                    ["asset_revision"] = _assetRevision
                };
            }
        }

        internal static JObject BuildResponseState(JObject before, JObject after)
        {
            JObject effectiveAfter = after ?? Snapshot();
            JObject effectiveBefore = before ?? effectiveAfter;
            return new JObject
            {
                ["epoch"] = effectiveAfter.Value<string>("epoch"),
                ["before_revision"] = effectiveBefore.Value<long?>("revision") ?? 0L,
                ["after_revision"] = effectiveAfter.Value<long?>("revision") ?? 0L,
                ["before_scene_revision"] = effectiveBefore.Value<long?>("scene_revision") ?? 0L,
                ["scene_revision"] = effectiveAfter.Value<long?>("scene_revision") ?? 0L,
                ["before_asset_revision"] = effectiveBefore.Value<long?>("asset_revision") ?? 0L,
                ["asset_revision"] = effectiveAfter.Value<long?>("asset_revision") ?? 0L
            };
        }

        internal static JObject LastEditorSaveSnapshot()
        {
            lock (StateLock)
            {
                return _editorSaveReceipts.Count == 0
                    ? null
                    : (JObject)_editorSaveReceipts[_editorSaveReceipts.Count - 1].DeepClone();
            }
        }

        internal static JArray EditorSaveReceiptsSnapshot()
        {
            lock (StateLock)
            {
                return (JArray)_editorSaveReceipts.DeepClone();
            }
        }

        internal static void ValidateIfMatch(
            JObject ifMatch,
            CommandRuntimeToolPolicy policy)
        {
            if (ifMatch == null || !ifMatch.HasValues)
            {
                return;
            }

            if (policy == null || !policy.SupportsIfMatch)
            {
                throw new CommandStateConflictException(
                    "PRECONDITION_NOT_SUPPORTED",
                    $"Tool '{policy?.Name ?? "unknown"}' does not support if_match.",
                    new JObject
                    {
                        ["tool_name"] = policy?.Name,
                        ["current"] = Snapshot()
                    });
            }

            JObject current = Snapshot();
            bool matches = MatchesString(ifMatch, "epoch", current)
                && MatchesLong(ifMatch, new[] { "revision", "global_revision", "after_revision" }, "revision", current)
                && MatchesLong(ifMatch, new[] { "scene_revision" }, "scene_revision", current)
                && MatchesLong(ifMatch, new[] { "asset_revision" }, "asset_revision", current);
            if (matches)
            {
                return;
            }

            throw new CommandStateConflictException(
                "STATE_CONFLICT",
                "The Unity Editor state no longer matches the supplied if_match precondition.",
                new JObject
                {
                    ["expected"] = ifMatch.DeepClone(),
                    ["current"] = current,
                    ["refresh_required"] = true
                });
        }

        internal static void MarkCommandMutation(CommandRuntimeToolPolicy policy)
        {
            if (policy == null || policy.MutationClass == "read_only")
            {
                return;
            }

            if (policy.MutationClass == "scene")
            {
                Increment(scene: true, asset: false);
                return;
            }

            if (policy.MutationClass == "asset"
                || policy.MutationClass == "project_settings"
                || policy.MutationClass == "package"
                || policy.MutationClass == "build")
            {
                Increment(scene: false, asset: true);
                return;
            }

            Increment(scene: false, asset: false);
        }

        internal static void ResetForTests(
            string epoch,
            long revision = 0L,
            long sceneRevision = 0L,
            long assetRevision = 0L,
            JArray editorSaveReceipts = null)
        {
            lock (StateLock)
            {
                _epoch = epoch;
                _revision = revision;
                _sceneRevision = sceneRevision;
                _assetRevision = assetRevision;
                _editorSaveReceipts = editorSaveReceipts == null
                    ? new JArray()
                    : (JArray)editorSaveReceipts.DeepClone();
                JObject last = _editorSaveReceipts.Count == 0
                    ? null
                    : _editorSaveReceipts[_editorSaveReceipts.Count - 1] as JObject;
                _sceneSaveSequence = last?.Value<long?>("sequence") ?? 0L;
                _lastSceneSavedPath = last?.Value<string>("path") ?? string.Empty;
                _lastSceneSavedMtimeUnixNs = last?.Value<long?>("mtime_unix_ns") ?? 0L;
                _lastSceneSavedUnixMs = last?.Value<long?>("saved_unix_ms") ?? 0L;
                Persist();
            }
        }

        internal static void RecordEditorSave(
            string path,
            string kind,
            long mtimeUnix100Ns,
            long fileSizeBytes,
            long? savedUnixMs = null)
        {
            bool isScene = string.Equals(kind, "scene", StringComparison.Ordinal);
            bool isPrefab = string.Equals(kind, "prefab", StringComparison.Ordinal);
            if (string.IsNullOrEmpty(path)
                || (!isScene && !isPrefab)
                || mtimeUnix100Ns <= 0L
                || fileSizeBytes < 0L)
            {
                Increment(scene: isScene, asset: isPrefab);
                return;
            }

            lock (StateLock)
            {
                JObject previous = _editorSaveReceipts.Count == 0
                    ? null
                    : _editorSaveReceipts[_editorSaveReceipts.Count - 1] as JObject;
                if (previous != null
                    && string.Equals(previous.Value<string>("kind"), kind, StringComparison.Ordinal)
                    && string.Equals(
                        previous.Value<string>("path"),
                        path.Replace('\\', '/'),
                        StringComparison.OrdinalIgnoreCase)
                    && previous.Value<long?>("mtime_unix_100ns") == mtimeUnix100Ns
                    && previous.Value<long?>("file_size_bytes") == fileSizeBytes)
                {
                    return;
                }

                _revision++;
                if (isScene) _sceneRevision++;
                if (isPrefab) _assetRevision++;
                _sceneSaveSequence++;
                _lastSceneSavedPath = path.Replace('\\', '/');
                _lastSceneSavedMtimeUnixNs = checked(mtimeUnix100Ns * 100L);
                _lastSceneSavedUnixMs = savedUnixMs
                    ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _editorSaveReceipts.Add(new JObject
                {
                    ["epoch"] = _epoch,
                    ["sequence"] = _sceneSaveSequence,
                    ["kind"] = kind,
                    ["path"] = _lastSceneSavedPath,
                    ["mtime_unix_ns"] = _lastSceneSavedMtimeUnixNs,
                    ["mtime_unix_100ns"] = mtimeUnix100Ns,
                    ["file_size_bytes"] = fileSizeBytes,
                    ["saved_unix_ms"] = _lastSceneSavedUnixMs
                });
                while (_editorSaveReceipts.Count > MaxEditorSaveReceipts)
                {
                    _editorSaveReceipts.RemoveAt(0);
                }
                Persist();
            }

            EditorStateCache.RecordEditorSave();
        }

        private static bool MatchesString(JObject expected, string key, JObject current)
        {
            JToken token = expected[key];
            return token == null
                || string.Equals(
                    token.ToString(),
                    current.Value<string>(key),
                    StringComparison.Ordinal);
        }

        private static bool MatchesLong(
            JObject expected,
            string[] aliases,
            string currentKey,
            JObject current)
        {
            foreach (string alias in aliases)
            {
                JToken token = expected[alias];
                if (token == null)
                {
                    continue;
                }

                return token.Type == JTokenType.Integer
                    && token.Value<long>() == current.Value<long>(currentKey);
            }

            return true;
        }

        private static void MarkSceneChange()
        {
            Increment(scene: true, asset: false);
        }

        private static void MarkAssetChange()
        {
            Increment(scene: false, asset: true);
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode) => MarkSceneChange();
        private static void OnSceneClosed(Scene scene) => MarkSceneChange();
        private static void OnSceneSaved(Scene scene)
        {
            CaptureEditorSave(scene.path, "scene");
        }

        private static void OnPrefabSaved(GameObject prefabRoot)
        {
            string path = prefabRoot == null ? null : AssetDatabase.GetAssetPath(prefabRoot);
            if (string.IsNullOrEmpty(path))
            {
                path = PrefabStageUtility.GetCurrentPrefabStage()?.assetPath;
            }
            CaptureEditorSave(path, "prefab");
        }

        internal static void CaptureEditorSave(string path, string kind)
        {
            bool isScene = string.Equals(kind, "scene", StringComparison.Ordinal);
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    Increment(scene: isScene, asset: !isScene);
                    return;
                }

                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (string.IsNullOrEmpty(projectRoot))
                {
                    Increment(scene: isScene, asset: !isScene);
                    return;
                }

                var file = new FileInfo(Path.GetFullPath(Path.Combine(projectRoot, path)));
                file.Refresh();
                if (!file.Exists)
                {
                    throw new FileNotFoundException("Saved Unity YAML was not found.", file.FullName);
                }

                long mtimeUnix100Ns = file.LastWriteTimeUtc.Ticks - UnixEpochTicks;
                RecordEditorSave(path, kind, mtimeUnix100Ns, file.Length);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to record {kind}-save receipt: {ex.Message}");
                Increment(scene: isScene, asset: !isScene);
            }
        }
        private static void OnPlayModeStateChanged(PlayModeStateChange state) =>
            Increment(scene: false, asset: false);

        private static void Increment(bool scene, bool asset)
        {
            lock (StateLock)
            {
                _revision++;
                if (scene)
                {
                    _sceneRevision++;
                }
                if (asset)
                {
                    _assetRevision++;
                }
                Persist();
            }
        }

        private static long ReadLong(string key)
        {
            return long.TryParse(SessionState.GetString(key, "0"), out long value)
                ? Math.Max(0L, value)
                : 0L;
        }

        private static JArray ReadEditorSaveReceipts()
        {
            try
            {
                string json = SessionState.GetString(EditorSaveReceiptsKey, string.Empty);
                if (string.IsNullOrEmpty(json)) return new JArray();
                JArray parsed = JArray.Parse(json);
                while (parsed.Count > MaxEditorSaveReceipts) parsed.RemoveAt(0);
                return parsed;
            }
            catch
            {
                return new JArray();
            }
        }

        private static void Persist()
        {
            SessionState.SetString(EpochKey, _epoch ?? string.Empty);
            SessionState.SetString(RevisionKey, _revision.ToString());
            SessionState.SetString(SceneRevisionKey, _sceneRevision.ToString());
            SessionState.SetString(AssetRevisionKey, _assetRevision.ToString());
            SessionState.SetString(SceneSaveSequenceKey, _sceneSaveSequence.ToString());
            SessionState.SetString(LastSceneSavedPathKey, _lastSceneSavedPath ?? string.Empty);
            SessionState.SetString(LastSceneSavedMtimeUnixNsKey, _lastSceneSavedMtimeUnixNs.ToString());
            SessionState.SetString(LastSceneSavedUnixMsKey, _lastSceneSavedUnixMs.ToString());
            SessionState.SetString(
                EditorSaveReceiptsKey,
                _editorSaveReceipts.ToString(Formatting.None));
        }
    }
}
