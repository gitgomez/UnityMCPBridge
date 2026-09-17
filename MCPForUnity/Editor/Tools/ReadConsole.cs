using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers; // For Response class
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Handles reading and clearing Unity Editor console log entries.
    /// Uses reflection to access internal LogEntry methods/properties.
    /// </summary>
    [McpForUnityTool("read_console", AutoRegister = false)]
    public static class ReadConsole
    {
        // (Calibration removed)

        // Reflection members for accessing internal LogEntry data
        // private static MethodInfo _getEntriesMethod; // Removed as it's unused and fails reflection
        private static MethodInfo _startGettingEntriesMethod;
        private static MethodInfo _endGettingEntriesMethod; // Renamed from _stopGettingEntriesMethod, trying End...
        private static MethodInfo _clearMethod;
        private static MethodInfo _getCountMethod;
        private static MethodInfo _getEntryMethod;
        private static FieldInfo _modeField;
        private static FieldInfo _messageField;
        private static FieldInfo _fileField;
        private static FieldInfo _lineField;
    
        // Static constructor for reflection setup
        static ReadConsole()
        {
            try
            {
                Type logEntriesType = typeof(EditorApplication).Assembly.GetType(
                    "UnityEditor.LogEntries"
                );
                if (logEntriesType == null)
                    throw new Exception("Could not find internal type UnityEditor.LogEntries");



                // Include NonPublic binding flags as internal APIs might change accessibility
                BindingFlags staticFlags =
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                BindingFlags instanceFlags =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _startGettingEntriesMethod = logEntriesType.GetMethod(
                    "StartGettingEntries",
                    staticFlags
                );
                if (_startGettingEntriesMethod == null)
                    throw new Exception("Failed to reflect LogEntries.StartGettingEntries");

                // Try reflecting EndGettingEntries based on warning message
                _endGettingEntriesMethod = logEntriesType.GetMethod(
                    "EndGettingEntries",
                    staticFlags
                );
                if (_endGettingEntriesMethod == null)
                    throw new Exception("Failed to reflect LogEntries.EndGettingEntries");

                _clearMethod = logEntriesType.GetMethod("Clear", staticFlags);
                if (_clearMethod == null)
                    throw new Exception("Failed to reflect LogEntries.Clear");

                _getCountMethod = logEntriesType.GetMethod("GetCount", staticFlags);
                if (_getCountMethod == null)
                    throw new Exception("Failed to reflect LogEntries.GetCount");

                _getEntryMethod = logEntriesType.GetMethod("GetEntryInternal", staticFlags);
                if (_getEntryMethod == null)
                    throw new Exception("Failed to reflect LogEntries.GetEntryInternal");

                Type logEntryType = typeof(EditorApplication).Assembly.GetType(
                    "UnityEditor.LogEntry"
                );
                if (logEntryType == null)
                    throw new Exception("Could not find internal type UnityEditor.LogEntry");

                _modeField = logEntryType.GetField("mode", instanceFlags);
                if (_modeField == null)
                    throw new Exception("Failed to reflect LogEntry.mode");

                _messageField = logEntryType.GetField("message", instanceFlags);
                if (_messageField == null)
                    throw new Exception("Failed to reflect LogEntry.message");

                _fileField = logEntryType.GetField("file", instanceFlags);
                if (_fileField == null)
                    throw new Exception("Failed to reflect LogEntry.file");

                _lineField = logEntryType.GetField("line", instanceFlags);
                if (_lineField == null)
                    throw new Exception("Failed to reflect LogEntry.line");

                InitializeLogMessageFlags(logEntryType.Assembly);
            }
            catch (Exception e)
            {
                McpLog.Error(
                    $"[ReadConsole] Static Initialization Failed: Could not setup reflection for LogEntries/LogEntry. Console reading/clearing will likely fail. Specific Error: {e.Message}"
                );
                // Set members to null to prevent NullReferenceExceptions later, HandleCommand should check this.
                _startGettingEntriesMethod =
                    _endGettingEntriesMethod =
                    _clearMethod =
                    _getCountMethod =
                    _getEntryMethod =
                        null;
                _modeField = _messageField = _fileField = _lineField = null;
            }
        }

        // --- Main Handler ---

        public static object HandleCommand(JObject @params)
        {
            // Check if ALL required reflection members were successfully initialized.
            if (
                _startGettingEntriesMethod == null
                || _endGettingEntriesMethod == null
                || _clearMethod == null
                || _getCountMethod == null
                || _getEntryMethod == null
                || _modeField == null
                || _messageField == null
                || _fileField == null
                || _lineField == null
            )
            {
                // Log the error here as well for easier debugging in Unity Console
                McpLog.Error(
                    "[ReadConsole] HandleCommand called but reflection members are not initialized. Static constructor might have failed silently or there's an issue."
                );
                return new ErrorResponse(
                    "ReadConsole handler failed to initialize due to reflection errors. Cannot access console logs."
                );
            }

            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            var p = new ToolParams(@params);
            string action = p.Get("action", "get").ToLower();

            try
            {
                if (action == "clear")
                {
                    return ClearConsole();
                }
                else if (action == "get")
                {
                    // Extract parameters for 'get'
                    var types =
                        (p.GetRaw("types") as JArray)?.Select(t => t.ToString().ToLower()).ToList()
                        ?? new List<string> { "error", "warning" };
                    int? count = p.GetInt("count");
                    int? pageSize = p.GetInt("pageSize");
                    int? cursor = p.GetInt("cursor");
                    string filterText = p.Get("filterText");
                    string format = p.Get("format", "plain").ToLower();
                    bool includeStacktrace = p.GetBool("includeStacktrace", false);

                    if (types.Contains("all"))
                    {
                        types = new List<string> { "error", "warning", "log" }; // Expand 'all'
                    }

                    return GetConsoleEntries(
                        types,
                        count,
                        pageSize,
                        cursor,
                        filterText,
                        format,
                        includeStacktrace
                    );
                }
                else
                {
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Valid actions are 'get' or 'clear'."
                    );
                }
            }
            catch (Exception e)
            {
                McpLog.Error($"[ReadConsole] Action '{action}' failed: {e}");
                return new ErrorResponse($"Internal error processing action '{action}': {e.Message}");
            }
        }

        // --- Action Implementations ---

        private static object ClearConsole()
        {
            try
            {
                _clearMethod.Invoke(null, null); // Static method, no instance, no parameters
                return new SuccessResponse("Console cleared successfully.");
            }
            catch (Exception e)
            {
                McpLog.Error($"[ReadConsole] Failed to clear console: {e}");
                return new ErrorResponse($"Failed to clear console: {e.Message}");
            }
        }

        /// <summary>
        /// Retrieves console log entries with optional filtering and paging.
        /// </summary>
        /// <param name="types">Log types to include (e.g., "error", "warning", "log").</param>
        /// <param name="count">Maximum entries to return in non-paging mode. Ignored when paging is active.</param>
        /// <param name="pageSize">Number of entries per page. Defaults to 50 when omitted.</param>
        /// <param name="cursor">Starting index for paging (0-based). Defaults to 0.</param>
        /// <param name="filterText">Optional text filter (case-insensitive substring match).</param>
        /// <param name="format">Output format: "plain", "detailed", or "json".</param>
        /// <param name="includeStacktrace">Whether to include stack traces in the output.</param>
        /// <returns>A success response with entries, or an error response.</returns>
        private static object GetConsoleEntries(
            List<string> types,
            int? count,
            int? pageSize,
            int? cursor,
            string filterText,
            string format,
            bool includeStacktrace
        )
        {
            List<object> formattedEntries = new List<object>();
            int retrievedCount = 0;
            int totalMatches = 0;
            bool usePaging = pageSize.HasValue || cursor.HasValue;
            // pageSize defaults to 50 when omitted; count is the overall non-paging limit only
            int resolvedPageSize = Mathf.Clamp(pageSize ?? 50, 1, 500);
            int resolvedCursor = Mathf.Max(0, cursor ?? 0);
            int pageEndExclusive = resolvedCursor + resolvedPageSize;

            try
            {
                // LogEntries requires calling Start/Stop around GetEntries/GetEntryInternal.
                // StartGettingEntries() returns the entry count — use it instead of GetCount()
                // which may return stale values within an active iteration session.
                object startResult = _startGettingEntriesMethod.Invoke(null, null);
                int totalEntries = startResult is int startCount
                    ? startCount
                    : (int)_getCountMethod.Invoke(null, null);
                // Create instance to pass to GetEntryInternal - Ensure the type is correct
                Type logEntryType = typeof(EditorApplication).Assembly.GetType(
                    "UnityEditor.LogEntry"
                );
                if (logEntryType == null)
                    throw new Exception(
                        "Could not find internal type UnityEditor.LogEntry during GetConsoleEntries."
                    );
                object logEntryInstance = Activator.CreateInstance(logEntryType);

                for (int i = 0; i < totalEntries; i++)
                {
                    // Get the entry data into our instance using reflection
                    _getEntryMethod.Invoke(null, new object[] { i, logEntryInstance });

                    // Extract data using reflection
                    int mode = (int)_modeField.GetValue(logEntryInstance);
                    string message = (string)_messageField.GetValue(logEntryInstance);
                    string file = (string)_fileField.GetValue(logEntryInstance);

                    int line = (int)_lineField.GetValue(logEntryInstance);

                    if (string.IsNullOrEmpty(message))
                    {
                        continue; // Skip empty messages
                    }

                    // (Calibration removed)

                    var (messageOnly, stackTrace) = SplitMessageAndStackTrace(message);

                    // --- Filtering ---
                    // Unity's mode bits are authoritative when they carry a known severity.
                    // Only the message body is used as a fallback; stack-trace method names
                    // such as SetException must never change an informational log's severity.
                    LogType unityType = ClassifyLogType(mode, messageOnly);

                    bool want;
                    // Treat Exception/Assert as errors for filtering convenience
                    if (unityType == LogType.Exception)
                    {
                        want = types.Contains("error") || types.Contains("exception");
                    }
                    else if (unityType == LogType.Assert)
                    {
                        want = types.Contains("error") || types.Contains("assert");
                    }
                    else
                    {
                        want = types.Contains(unityType.ToString().ToLowerInvariant());
                    }

                    if (!want) continue;

                    // Filter by text (case-insensitive)
                    if (
                        !string.IsNullOrEmpty(filterText)
                        && message.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) < 0
                    )
                    {
                        continue;
                    }

                    if (!includeStacktrace)
                    {
                        stackTrace = null;
                    }

                    object formattedEntry = null;
                    switch (format)
                    {
                        case "plain":
                            formattedEntry = messageOnly;
                            break;
                        case "json":
                        case "detailed": // Treat detailed as json for structured return
                        default:
                            formattedEntry = new
                            {
                                type = unityType.ToString(),
                                message = messageOnly,
                                file = file,
                                line = line,
                                stackTrace = stackTrace, // Will be null if includeStacktrace is false or no stack found
                            };
                            break;
                    }

                    totalMatches++;

                    if (usePaging)
                    {
                        if (totalMatches > resolvedCursor && totalMatches <= pageEndExclusive)
                        {
                            formattedEntries.Add(formattedEntry);
                            retrievedCount++;
                        }
                        // Early exit: we've filled the page and only need to check if more exist
                        else if (totalMatches > pageEndExclusive)
                        {
                            // We've passed the page; totalMatches now indicates truncation
                            break;
                        }
                    }
                    else
                    {
                        formattedEntries.Add(formattedEntry);
                        retrievedCount++;

                        // Apply count limit (after filtering)
                        if (count.HasValue && retrievedCount >= count.Value)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                McpLog.Error($"[ReadConsole] Error while retrieving log entries: {e}");
                // EndGettingEntries will be called in the finally block
                return new ErrorResponse($"Error retrieving log entries: {e.Message}");
            }
            finally
            {
                // Ensure we always call EndGettingEntries
                try
                {
                    _endGettingEntriesMethod.Invoke(null, null);
                }
                catch (Exception e)
                {
                    McpLog.Error($"[ReadConsole] Failed to call EndGettingEntries: {e}");
                    // Don't return error here as we might have valid data, but log it.
                }
            }

            if (usePaging)
            {
                bool truncated = totalMatches > pageEndExclusive;
                string nextCursor = truncated ? pageEndExclusive.ToString() : null;
                var payload = new
                {
                    cursor = resolvedCursor,
                    pageSize = resolvedPageSize,
                    nextCursor = nextCursor,
                    truncated = truncated,
                    total = totalMatches,
                    items = formattedEntries,
                };

                return new SuccessResponse(
                    $"Retrieved {formattedEntries.Count} log entries.",
                    payload
                );
            }

            // Return the filtered and formatted list (might be empty)
            return new SuccessResponse(
                $"Retrieved {formattedEntries.Count} log entries.",
                formattedEntries
            );
        }

        // --- Internal Helpers ---

        // LogEntry.mode values changed between Unity releases. Resolve the editor's
        // internal enum by name rather than maintaining a second numeric definition.
        private static int _errorModeBits;
        private static int _assertModeBits;
        private static int _warningModeBits;
        private static int _logModeBits;
        private static int _exceptionModeBits;
        private static int _knownSeverityModeBits;

        private static void InitializeLogMessageFlags(Assembly editorAssembly)
        {
            Type flagsType = editorAssembly.GetType("UnityEditor.LogMessageFlags");
            if (flagsType == null || !flagsType.IsEnum)
            {
                return;
            }

            _errorModeBits = CombineFlagValues(
                flagsType,
                "kError",
                "kAssetImportError",
                "kScriptingError",
                "kScriptCompileError",
                "kGraphCompileError",
                "kVisualScriptingError"
            );
            _assertModeBits = CombineFlagValues(flagsType, "kAssert", "kScriptingAssertion");
            _warningModeBits = CombineFlagValues(
                flagsType,
                "kWarning",
                "kAssetImportWarning",
                "kScriptingWarning",
                "kScriptCompileWarning"
            );
            _logModeBits = CombineFlagValues(flagsType, "kLog", "kScriptingLog");
            _exceptionModeBits = CombineFlagValues(
                flagsType,
                "kException",
                "kFatal",
                "kScriptingException"
            );
            _knownSeverityModeBits =
                _errorModeBits
                | _assertModeBits
                | _warningModeBits
                | _logModeBits
                | _exceptionModeBits;
        }

        private static int CombineFlagValues(Type flagsType, params string[] names)
        {
            const BindingFlags flags =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            int value = 0;

            foreach (string name in names)
            {
                FieldInfo field = flagsType.GetField(name, flags);
                if (field != null)
                {
                    value |= Convert.ToInt32(field.GetRawConstantValue());
                }
            }

            return value;
        }

        internal static LogType ClassifyLogType(int mode, string messageBody)
        {
            if ((mode & _knownSeverityModeBits) != 0)
            {
                return GetLogTypeFromMode(mode);
            }

            return InferTypeFromMessageBody(messageBody);
        }

        private static LogType GetLogTypeFromMode(int mode)
        {
            if ((mode & _exceptionModeBits) != 0) return LogType.Exception;
            if ((mode & _errorModeBits) != 0) return LogType.Error;
            if ((mode & _assertModeBits) != 0) return LogType.Assert;
            if ((mode & _warningModeBits) != 0) return LogType.Warning;
            return LogType.Log;
        }

        // (Calibration helpers removed)

        /// <summary>
        /// Classifies severity from the message body when Unity exposes no recognized mode bit.
        /// </summary>
        private static LogType InferTypeFromMessageBody(string messageBody)
        {
            if (string.IsNullOrEmpty(messageBody)) return LogType.Log;

            // Compiler diagnostics (C#): "warning CSxxxx" / "error CSxxxx"
            if (messageBody.IndexOf(" warning CS", StringComparison.OrdinalIgnoreCase) >= 0
                || messageBody.IndexOf(": warning CS", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Warning;
            if (messageBody.IndexOf(" error CS", StringComparison.OrdinalIgnoreCase) >= 0
                || messageBody.IndexOf(": error CS", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Error;

            // Exceptions (avoid misclassifying compiler diagnostics)
            if (messageBody.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Exception;

            // Unity assertions
            if (messageBody.IndexOf("Assertion", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Assert;

            return LogType.Log;
        }

        /// <summary>
        /// Splits a Unity log message into its body and appended stack trace.
        /// Unity concatenates both, separated by newlines, so the body may span
        /// several lines before the stack trace begins.
        /// </summary>
        /// <param name="fullMessage">The complete log message including any appended stack trace.</param>
        /// <returns>The message body (line endings normalized to "\n", internal blank lines preserved) and the stack trace, or null when none is found.</returns>
        private static (string body, string stackTrace) SplitMessageAndStackTrace(string fullMessage)
        {
            if (string.IsNullOrEmpty(fullMessage))
                return (fullMessage, null);

            string[] lines = fullMessage.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            // If there's only one line or less, there's no separate stack trace.
            if (lines.Length <= 1)
                return (fullMessage, null);

            int stackStartIndex = FindStackStartIndex(lines);
            if (stackStartIndex <= 0)
                return (string.Join("\n", lines), null);

            return (
                string.Join("\n", lines.Take(stackStartIndex)),
                string.Join("\n", lines.Skip(stackStartIndex))
            );
        }

        private static int FindStackStartIndex(string[] lines)
        {
            // Start checking from the second line onwards.
            for (int i = 1; i < lines.Length; ++i)
            {
                // Performance: TrimStart creates a new string. Consider using IsWhiteSpace check if performance critical.
                string trimmedLine = lines[i].TrimStart();

                // Check for common stack trace patterns.
                if (
                    trimmedLine.StartsWith("at ")
                    || trimmedLine.StartsWith("UnityEngine.")
                    || trimmedLine.StartsWith("UnityEditor.")
                    || trimmedLine.Contains("(at ")
                    || // Covers "(at Assets/..." pattern
                       // Heuristic: Check if line starts with likely namespace/class pattern (Uppercase.Something)
                    (
                        trimmedLine.Length > 0
                        && char.IsUpper(trimmedLine[0])
                        && trimmedLine.Contains('.')
                    )
                )
                {
                    return i; // Found the likely start of the stack trace
                }
            }

            return -1;
        }

    }
}
