using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Models;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Services.Transport
{
    internal sealed class CommandQueueFullException : InvalidOperationException
    {
        internal CommandQueueFullException(string code, string message) : base(message)
        {
            Code = code;
        }

        internal string Code { get; }
    }

    /// <summary>
    /// Centralised command execution pipeline shared by all transport implementations.
    /// Guarantees that MCP commands are executed on the Unity main thread while preserving
    /// the legacy response format expected by the server.
    /// </summary>
    [InitializeOnLoad]
    internal static class TransportCommandDispatcher
    {
        internal const int RuntimeMaxQueuedCommands = CommandRuntimeProtocol.MaxQueuedCommands;
        internal const long RuntimeMaxQueuedPayloadBytes = CommandRuntimeProtocol.MaxQueuedPayloadBytes;
        internal const int RuntimeMaxCommandsPerPump = 4;
        internal const int RuntimeDispatchBudgetMilliseconds = 4;

        private static SynchronizationContext _mainThreadContext;
        private static int _mainThreadId;
        private static int _processingFlag;

        private sealed class PendingCommand
        {
            public PendingCommand(
                string commandJson,
                TaskCompletionSource<string> completionSource,
                CancellationToken cancellationToken,
                CancellationTokenRegistration registration,
                bool runtimeManaged,
                int payloadBytes,
                Action onExecuting)
            {
                CommandJson = commandJson;
                CompletionSource = completionSource;
                CancellationToken = cancellationToken;
                CancellationRegistration = registration;
                RuntimeManaged = runtimeManaged;
                PayloadBytes = payloadBytes;
                OnExecuting = onExecuting;
                QueuedAt = DateTime.UtcNow;
            }

            public string CommandJson { get; }
            public TaskCompletionSource<string> CompletionSource { get; }
            public CancellationToken CancellationToken { get; }
            public CancellationTokenRegistration CancellationRegistration { get; }
            public bool IsExecuting { get; set; }
            public DateTime QueuedAt { get; }
            public bool RuntimeManaged { get; }
            public int PayloadBytes { get; }
            public Action OnExecuting { get; }

            public void Dispose()
            {
                CancellationRegistration.Dispose();
            }

            public void TrySetResult(string payload)
            {
                CompletionSource.TrySetResult(payload);
            }

            public void TrySetCanceled()
            {
                CompletionSource.TrySetCanceled(CancellationToken);
            }
        }

        private static readonly Dictionary<string, PendingCommand> Pending = new();
        private static readonly object PendingLock = new();
        private static long runtimeQueuedPayloadBytes;
        private static bool updateHooked;
        private static bool initialised;

        static TransportCommandDispatcher()
        {
            // Ensure this runs on the Unity main thread at editor load.
            _mainThreadContext = SynchronizationContext.Current;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            EnsureInitialised();

            // Always keep the update hook installed so commands arriving from background
            // websocket tasks don't depend on a background-thread event subscription.
            if (!updateHooked)
            {
                updateHooked = true;
                EditorApplication.update += ProcessQueue;
            }
        }

        /// <summary>
        /// Schedule a command for execution on the Unity main thread and await its JSON response.
        /// </summary>
        public static Task<string> ExecuteCommandJsonAsync(string commandJson, CancellationToken cancellationToken)
        {
            return EnqueueCommandJsonAsync(commandJson, cancellationToken, runtimeManaged: false);
        }

        internal static Task<string> ExecuteRuntimeCommandJsonAsync(
            string commandJson,
            CancellationToken cancellationToken,
            Action onExecuting = null)
        {
            return EnqueueCommandJsonAsync(
                commandJson,
                cancellationToken,
                runtimeManaged: true,
                onExecuting);
        }

        private static Task<string> EnqueueCommandJsonAsync(
            string commandJson,
            CancellationToken cancellationToken,
            bool runtimeManaged,
            Action onExecuting = null)
        {
            if (commandJson is null)
            {
                throw new ArgumentNullException(nameof(commandJson));
            }

            EnsureInitialised();
            cancellationToken.ThrowIfCancellationRequested();
            EditorStateCache.RecordTransportMessage();

            var id = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            int payloadBytes = Encoding.UTF8.GetByteCount(commandJson);

            var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => CancelPending(id, cancellationToken))
                : default;

            var pending = new PendingCommand(
                commandJson,
                tcs,
                cancellationToken,
                registration,
                runtimeManaged,
                payloadBytes,
                onExecuting);

            lock (PendingLock)
            {
                if (runtimeManaged)
                {
                    int runtimeCount = 0;
                    foreach (PendingCommand existing in Pending.Values)
                    {
                        if (existing.RuntimeManaged)
                        {
                            runtimeCount++;
                        }
                    }

                    if (runtimeCount >= RuntimeMaxQueuedCommands)
                    {
                        pending.Dispose();
                        throw new CommandQueueFullException(
                            "QUEUE_FULL",
                            $"Runtime command queue is full ({RuntimeMaxQueuedCommands} commands).");
                    }

                    if (runtimeQueuedPayloadBytes + payloadBytes > RuntimeMaxQueuedPayloadBytes)
                    {
                        pending.Dispose();
                        throw new CommandQueueFullException(
                            "QUEUE_BYTES_EXCEEDED",
                            $"Runtime command queue payload limit exceeded ({RuntimeMaxQueuedPayloadBytes} bytes).");
                    }

                    runtimeQueuedPayloadBytes += payloadBytes;
                }
                Pending[id] = pending;
            }

            // Proactively wake up the main thread execution loop. This improves responsiveness
            // in scenarios where EditorApplication.update is throttled or temporarily not firing
            // (e.g., Unity unfocused, compiling, or during domain reload transitions).
            RequestMainThreadPump();

            return tcs.Task;
        }

        internal static Task<T> RunOnMainThreadAsync<T>(Func<T> func, CancellationToken cancellationToken)
        {
            if (func is null)
            {
                throw new ArgumentNullException(nameof(func));
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken))
                : default;

            void Invoke()
            {
                try
                {
                    if (tcs.Task.IsCompleted)
                    {
                        return;
                    }

                    var result = func();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    registration.Dispose();
                }
            }

            // Best-effort nudge: if we're posting from a background thread (e.g., websocket receive),
            // encourage Unity to run a loop iteration so the posted callback can execute even when unfocused.
            try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }

            if (_mainThreadContext != null && Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                _mainThreadContext.Post(_ => Invoke(), null);
                return tcs.Task;
            }

            Invoke();
            return tcs.Task;
        }

        private static void RequestMainThreadPump()
        {
            void Pump()
            {
                try
                {
                    // Hint Unity to run a loop iteration soon.
                    EditorApplication.QueuePlayerLoopUpdate();
                }
                catch
                {
                    // Best-effort only.
                }

                ProcessQueue();
            }

            if (_mainThreadContext != null && Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                _mainThreadContext.Post(_ => Pump(), null);
                return;
            }

            Pump();
        }

        private static void EnsureInitialised()
        {
            if (initialised)
            {
                return;
            }

            CommandRegistry.Initialize();
            initialised = true;
        }

        private static void HookUpdate()
        {
            // Deprecated: we keep the update hook installed permanently (see static ctor).
            if (updateHooked) return;
            updateHooked = true;
            EditorApplication.update += ProcessQueue;
        }

        private static void UnhookUpdateIfIdle()
        {
            // Intentionally no-op: keep update hook installed so background commands always process.
            // This avoids "must focus Unity to re-establish contact" edge cases.
            return;
        }

        private static void ProcessQueue()
        {
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1)
            {
                return;
            }

            try
            {
            List<(string id, PendingCommand pending)> ready;

            lock (PendingLock)
            {
                // Early exit inside lock to prevent per-frame List allocations (GitHub issue #577)
                if (Pending.Count == 0)
                {
                    return;
                }

                ready = new List<(string, PendingCommand)>(Pending.Count);
                int runtimeSelected = 0;
                foreach (var kvp in Pending)
                {
                    if (kvp.Value.IsExecuting)
                    {
                        continue;
                    }

                    if (kvp.Value.RuntimeManaged
                        && runtimeSelected >= RuntimeMaxCommandsPerPump)
                    {
                        continue;
                    }

                    kvp.Value.IsExecuting = true;
                    ready.Add((kvp.Key, kvp.Value));
                    if (kvp.Value.RuntimeManaged)
                    {
                        runtimeSelected++;
                    }
                }

                if (ready.Count == 0)
                {
                    UnhookUpdateIfIdle();
                    return;
                }
            }

            var dispatchBudget = Stopwatch.StartNew();
            int runtimeProcessed = 0;
            foreach (var (id, pending) in ready)
            {
                if (pending.RuntimeManaged
                    && runtimeProcessed > 0
                    && dispatchBudget.ElapsedMilliseconds >= RuntimeDispatchBudgetMilliseconds)
                {
                    ResetExecuting(id, pending);
                    continue;
                }

                ProcessCommand(id, pending);
                if (pending.RuntimeManaged)
                {
                    runtimeProcessed++;
                }
            }
            }
            finally
            {
                Interlocked.Exchange(ref _processingFlag, 0);
            }
        }

        private static void ProcessCommand(string id, PendingCommand pending)
        {
            if (pending.CancellationToken.IsCancellationRequested)
            {
                RemovePending(id, pending);
                pending.TrySetCanceled();
                return;
            }

            if (pending.RuntimeManaged && pending.OnExecuting != null)
            {
                try
                {
                    pending.OnExecuting();
                }
                catch (Exception ex)
                {
                    if (ex is CommandStateConflictException stateConflict)
                    {
                        pending.TrySetResult(JsonConvert.SerializeObject(new
                        {
                            status = "error",
                            success = false,
                            code = stateConflict.Code,
                            error = stateConflict.Message,
                            data = stateConflict.Data
                        }));
                        RemovePending(id, pending);
                        return;
                    }

                    pending.TrySetResult(JsonConvert.SerializeObject(new
                    {
                        status = "error",
                        success = false,
                        code = "RECEIPT_PERSIST_FAILED",
                        error = ex.Message
                    }));
                    RemovePending(id, pending);
                    return;
                }
            }

            string commandText = pending.CommandJson?.Trim();
            if (string.IsNullOrEmpty(commandText))
            {
                pending.TrySetResult(SerializeError("Empty command received"));
                RemovePending(id, pending);
                return;
            }

            if (string.Equals(commandText, "ping", StringComparison.OrdinalIgnoreCase))
            {
                var pingResponse = new
                {
                    status = "success",
                    result = new { message = "pong" }
                };
                pending.TrySetResult(JsonConvert.SerializeObject(pingResponse));
                RemovePending(id, pending);
                return;
            }

            if (!IsValidJson(commandText))
            {
                var invalidJsonResponse = new
                {
                    status = "error",
                    error = "Invalid JSON format",
                    receivedText = commandText.Length > 50 ? commandText[..50] + "..." : commandText
                };
                pending.TrySetResult(JsonConvert.SerializeObject(invalidJsonResponse));
                RemovePending(id, pending);
                return;
            }

            try
            {
                var command = JsonConvert.DeserializeObject<Command>(commandText);
                if (command == null)
                {
                    pending.TrySetResult(SerializeError("Command deserialized to null", "Unknown", commandText));
                    RemovePending(id, pending);
                    return;
                }

                if (string.IsNullOrWhiteSpace(command.type))
                {
                    pending.TrySetResult(SerializeError("Command type cannot be empty"));
                    RemovePending(id, pending);
                    return;
                }

                if (string.Equals(command.type, "ping", StringComparison.OrdinalIgnoreCase))
                {
                    var pingResponse = new
                    {
                        status = "success",
                        result = new { message = "pong" }
                    };
                    pending.TrySetResult(JsonConvert.SerializeObject(pingResponse));
                    RemovePending(id, pending);
                    return;
                }

                var parameters = command.@params ?? new JObject();

                // Block execution of disabled resources
                var resourceMeta = MCPServiceLocator.ResourceDiscovery.GetResourceMetadata(command.type);
                if (resourceMeta != null && !MCPServiceLocator.ResourceDiscovery.IsResourceEnabled(command.type))
                {
                    pending.TrySetResult(SerializeError(
                        $"Resource '{command.type}' is disabled in the Unity Editor."));
                    RemovePending(id, pending);
                    return;
                }

                // Block execution of disabled tools
                var toolMeta = MCPServiceLocator.ToolDiscovery.GetToolMetadata(command.type);
                if (toolMeta != null && !MCPServiceLocator.ToolDiscovery.IsToolEnabled(command.type))
                {
                    pending.TrySetResult(SerializeError(
                        $"Tool '{command.type}' is disabled in the Unity Editor."));
                    RemovePending(id, pending);
                    return;
                }

                var logType = resourceMeta != null ? "resource" : toolMeta != null ? "tool" : "unknown";
                var sw = McpLogRecord.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
                var result = CommandRegistry.ExecuteCommand(command.type, parameters, pending.CompletionSource);

                if (result == null)
                {
                    // Async command – cleanup after completion on next editor frame to preserve order.
                    var capturedType = command.type;
                    var capturedParams = parameters;
                    var capturedLogType = logType;
                    pending.CompletionSource.Task.ContinueWith(t =>
                    {
                        sw?.Stop();
                        var logStatus = "SUCCESS";
                        string logError = null;
                        if (t.IsFaulted)
                        {
                            logStatus = "ERROR";
                            logError = t.Exception?.InnerException?.Message;
                        }
                        else if (t.IsCompletedSuccessfully && t.Result != null)
                        {
                            try
                            {
                                var resultObj = JObject.Parse(t.Result);
                                if (string.Equals(resultObj.Value<string>("status"), "error", StringComparison.OrdinalIgnoreCase))
                                {
                                    logStatus = "ERROR";
                                    logError = resultObj.Value<string>("error");
                                }
                            }
                            catch { }
                        }
                        McpLogRecord.Log(capturedType, capturedParams, capturedLogType,
                            logStatus, sw?.ElapsedMilliseconds ?? 0, logError);
                        EditorApplication.delayCall += () => RemovePending(id, pending);
                    }, TaskScheduler.Default);
                    return;
                }

                sw?.Stop();

                string syncLogStatus = "SUCCESS";
                string syncLogError = null;
                if (result is ErrorResponse errResp)
                {
                    syncLogStatus = "ERROR";
                    syncLogError = errResp.Error;
                }
                McpLogRecord.Log(command.type, parameters, logType, syncLogStatus, sw?.ElapsedMilliseconds ?? 0, syncLogError);

                var response = new { status = "success", result };
                pending.TrySetResult(JsonConvert.SerializeObject(response));
                RemovePending(id, pending);
            }
            catch (Exception ex)
            {
                McpLog.Error($"Error processing command: {ex.Message}\n{ex.StackTrace}");
                pending.TrySetResult(SerializeError(ex.Message, "Unknown (error during processing)", ex.StackTrace));
                RemovePending(id, pending);
            }
        }

        private static void CancelPending(string id, CancellationToken token)
        {
            PendingCommand pending = null;
            lock (PendingLock)
            {
                if (Pending.TryGetValue(id, out PendingCommand existing)
                    && existing.RuntimeManaged
                    && existing.IsExecuting)
                {
                    // Once a runtime command has been selected by the main-thread
                    // dispatcher, cancellation cannot prove that side effects did
                    // not start. Preserve the actual terminal result for its receipt.
                    return;
                }

                if (Pending.Remove(id, out pending))
                {
                    DecrementRuntimePayload(pending);
                    UnhookUpdateIfIdle();
                }
            }

            pending?.TrySetCanceled();
            pending?.Dispose();
        }

        private static void RemovePending(string id, PendingCommand pending)
        {
            lock (PendingLock)
            {
                if (Pending.Remove(id))
                {
                    DecrementRuntimePayload(pending);
                }
                UnhookUpdateIfIdle();
            }

            pending.Dispose();
        }

        private static void ResetExecuting(string id, PendingCommand pending)
        {
            lock (PendingLock)
            {
                if (Pending.TryGetValue(id, out PendingCommand current)
                    && ReferenceEquals(current, pending))
                {
                    current.IsExecuting = false;
                }
            }
        }

        private static void DecrementRuntimePayload(PendingCommand pending)
        {
            if (!pending.RuntimeManaged)
            {
                return;
            }

            runtimeQueuedPayloadBytes = Math.Max(
                0L,
                runtimeQueuedPayloadBytes - pending.PayloadBytes);
        }

        private static string SerializeError(string message, string commandType = null, string stackTrace = null)
        {
            var errorResponse = new
            {
                status = "error",
                error = message,
                command = commandType ?? "Unknown",
                stackTrace
            };
            return JsonConvert.SerializeObject(errorResponse);
        }

        private static bool IsValidJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim();
            if ((text.StartsWith("{") && text.EndsWith("}")) || (text.StartsWith("[") && text.EndsWith("]")))
            {
                try
                {
                    JToken.Parse(text);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }
    }
}
