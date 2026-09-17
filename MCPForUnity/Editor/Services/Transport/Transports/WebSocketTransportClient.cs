using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    /// <summary>
    /// Maintains a persistent WebSocket connection to the MCP server plugin hub.
    /// Handles registration, keep-alives, and command dispatch back into Unity via
    /// <see cref="TransportCommandDispatcher"/>.
    /// </summary>
    public class WebSocketTransportClient : IMcpTransportClient, IReloadLifecycleTransport, IDisposable
    {
        private const string TransportDisplayName = "websocket";
        private static readonly TimeSpan[] ReconnectSchedule =
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };
        private static readonly TimeSpan[] PlannedRestartReconnectSchedule =
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5)
        };
        private static readonly TimeSpan ReconnectTailInterval = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

        private readonly IToolDiscoveryService _toolDiscoveryService;
        private ClientWebSocket _socket;
        private CancellationTokenSource _lifecycleCts;
        private CancellationTokenSource _connectionCts;
        private Task _receiveTask;
        private Task _keepAliveTask;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly object _runtimeCommandTasksLock = new();
        private readonly HashSet<Task> _runtimeCommandTasks = new();
        private readonly object _runtimeCancellationLock = new();
        private readonly Dictionary<string, CancellationTokenSource> _runtimeCancellations = new();
        private int _admittedRuntimeCommands;

        private Uri _endpointUri;
        private string _sessionId;
        private string _projectHash;
        private string _projectName;
        private string _projectPath;
        private string _unityVersion;
        private string _packageVersion;
        private JObject _localRuntime;
        private string _mutationProfile = CommandMutationPolicy.DefaultProfile;
        private CommandRuntimeNegotiation _runtimeNegotiation = new(CommandRuntimeMode.Legacy);
        private TimeSpan _keepAliveInterval = DefaultKeepAliveInterval;
        private TimeSpan _socketKeepAliveInterval = DefaultKeepAliveInterval;
        private volatile bool _isConnected;
        private int _isReconnectingFlag;
        private TransportState _state = TransportState.Disconnected(TransportDisplayName, "Transport not started");
        private string _apiKey;
        private bool _disposed;

        public WebSocketTransportClient(IToolDiscoveryService toolDiscoveryService = null)
        {
            _toolDiscoveryService = toolDiscoveryService;
        }

        public bool IsConnected => _isConnected;
        public string TransportName => TransportDisplayName;
        public TransportState State => _state;
        internal CommandRuntimeNegotiation RuntimeNegotiation => _runtimeNegotiation;

        private Task<List<ToolMetadata>> GetEnabledToolsOnMainThreadAsync(CancellationToken token)
        {
            return TransportCommandDispatcher.RunOnMainThreadAsync(
                () => _toolDiscoveryService?.GetEnabledTools() ?? new List<ToolMetadata>(),
                token);
        }

        public async Task<bool> StartAsync()
        {
            // Capture identity values on the main thread before any async context switching
            _projectName = ProjectIdentityUtility.GetProjectName();
            _projectHash = ProjectIdentityUtility.GetProjectHash();
            _unityVersion = Application.unityVersion;
            _packageVersion = AssetPathUtility.GetPackageVersion();
            _localRuntime = CommandRuntimeProtocol.BuildUnityAdvertisement(_packageVersion);
            _mutationProfile = CommandMutationPolicy.GetConfiguredProfile();
            _apiKey = HttpEndpointUtility.IsRemoteScope()
                ? EditorPrefs.GetString(EditorPrefKeys.ApiKey, string.Empty)
                : string.Empty;

            if (HttpEndpointUtility.IsRemoteScope()
                && !HttpEndpointUtility.IsCurrentRemoteUrlAllowed(out string remoteUrlError))
            {
                string message = remoteUrlError ?? "HTTP Remote URL is not allowed by current security settings.";
                _state = TransportState.Disconnected(TransportDisplayName, message);
                McpLog.Error($"[WebSocket] {message}");
                return false;
            }

            // Get project root path (strip /Assets from dataPath) for focus nudging
            string dataPath = Application.dataPath;
            if (!string.IsNullOrEmpty(dataPath))
            {
                string normalized = dataPath.TrimEnd('/', '\\');
                if (string.Equals(System.IO.Path.GetFileName(normalized), "Assets", StringComparison.Ordinal))
                {
                    _projectPath = System.IO.Path.GetDirectoryName(normalized) ?? normalized;
                }
                else
                {
                    _projectPath = normalized;  // Fallback if path doesn't end with Assets
                }
            }

            if (!string.IsNullOrWhiteSpace(_projectPath))
            {
                CommandReceiptLedger.Initialize(_projectPath);
            }

            await StopAsync();

            _lifecycleCts = new CancellationTokenSource();
            _endpointUri = BuildWebSocketUri(HttpEndpointUtility.GetBaseUrl());
            _sessionId = null;

            if (!await EstablishConnectionAsync(_lifecycleCts.Token))
            {
                await StopAsync();
                return false;
            }

            // State is connected but session ID might be pending until 'registered' message
            _state = TransportState.Connected(TransportDisplayName, sessionId: "pending", details: _endpointUri.ToString());
            _isConnected = true;
            return true;
        }

        public async Task StopAsync()
        {
            if (_lifecycleCts == null)
            {
                return;
            }

            try
            {
                _lifecycleCts.Cancel();
            }
            catch { }

            await StopConnectionLoopsAsync().ConfigureAwait(false);

            if (_socket != null)
            {
                try
                {
                    if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                    {
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutdown", CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch { }
                finally
                {
                    _socket.Dispose();
                    _socket = null;
                }
            }

            _isConnected = false;
            _state = TransportState.Disconnected(TransportDisplayName);

            _lifecycleCts.Dispose();
            _lifecycleCts = null;
        }

        /// <summary>
        /// Synchronous teardown for use in beforeAssemblyReload where async is not possible.
        /// Skips the graceful WebSocket close handshake and just disposes resources immediately.
        /// The server handles ungraceful disconnects via its ping timeout.
        /// </summary>
        public void ForceStop()
        {
            try { _lifecycleCts?.Cancel(); } catch { }
            try { _connectionCts?.Cancel(); } catch { }

            if (_socket != null)
            {
                try { _socket.Abort(); } catch { }
                try { _socket.Dispose(); } catch { }
                _socket = null;
            }

            try { _connectionCts?.Dispose(); } catch { }
            _connectionCts = null;
            _receiveTask = null;
            _keepAliveTask = null;
            Interlocked.Exchange(ref _isReconnectingFlag, 0);
            _isConnected = false;
            _state = TransportState.Disconnected(TransportDisplayName);

            try { _lifecycleCts?.Dispose(); } catch { }
            _lifecycleCts = null;
        }

        public async Task<bool> VerifyAsync()
        {
            if (_socket == null || _socket.State != WebSocketState.Open)
            {
                return false;
            }

            if (_lifecycleCts == null)
            {
                return false;
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                await SendPongAsync(timeoutCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[WebSocket] Verify ping failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Announces an intentional domain reload and completes a bounded close handshake
        /// before the synchronous reload hook tears down the socket.
        /// </summary>
        public bool NotifyReloading(string reason = null)
        {
            if (_socket == null
                || _socket.State != WebSocketState.Open
                || string.IsNullOrEmpty(_sessionId)
                || !_runtimeNegotiation.HasCapability(CommandRuntimeProtocol.ReloadLifecycleCapability))
            {
                return false;
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                NotifyReloadingAndCloseAsync(reason, timeout.Token).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Debug($"[WebSocket] Reload lifecycle notification failed: {ex.Message}");
                return false;
            }
        }

        private async Task NotifyReloadingAndCloseAsync(string reason, CancellationToken token)
        {
            await SendJsonAsync(BuildReloadLifecyclePayload(_sessionId, reason), token)
                .ConfigureAwait(false);

            if (_socket != null && _socket.State == WebSocketState.Open)
            {
                await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Unity domain reload",
                    token).ConfigureAwait(false);
            }
        }

        internal static JObject BuildReloadLifecyclePayload(string sessionId, string reason = null)
        {
            var payload = new JObject
            {
                ["type"] = "lifecycle",
                ["state"] = "reloading",
                ["session_id"] = sessionId
            };
            if (!string.IsNullOrWhiteSpace(reason))
            {
                payload["reason"] = reason;
            }
            return payload;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // Ensure background loops are stopped before disposing shared resources
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[WebSocket] Dispose failed to stop cleanly: {ex.Message}");
            }

            _sendLock?.Dispose();
            _socket?.Dispose();
            _lifecycleCts?.Dispose();
            _disposed = true;
        }

        private async Task<bool> EstablishConnectionAsync(CancellationToken token)
        {
            await StopConnectionLoopsAsync().ConfigureAwait(false);

            _connectionCts?.Dispose();
            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            CancellationToken connectionToken = _connectionCts.Token;

            Uri originalEndpoint = _endpointUri;
            Uri connectedEndpoint = null;
            Exception lastConnectError = null;

            foreach (Uri candidate in BuildConnectionCandidateUris(originalEndpoint))
            {
                connectionToken.ThrowIfCancellationRequested();

                _socket?.Dispose();
                _socket = new ClientWebSocket();
                _socket.Options.KeepAliveInterval = _socketKeepAliveInterval;

                // Add API key header if configured (for remote-hosted mode)
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    _socket.Options.SetRequestHeader(AuthConstants.ApiKeyHeader, _apiKey);
                }

                try
                {
                    await _socket.ConnectAsync(candidate, connectionToken).ConfigureAwait(false);
                    connectedEndpoint = candidate;
                    break;
                }
                catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastConnectError = ex;
                    if (!PlannedServerRestartState.IsActive)
                    {
                        McpLog.Debug($"[WebSocket] Connect failed for {candidate}: {ex.Message}");
                    }
                }
            }

            if (connectedEndpoint == null)
            {
                string errorMsg = "Connection failed. Check that the server URL is correct, the server is running, and your API key (if required) is valid.";
                string detail = lastConnectError?.Message ?? "Unknown error";
                if (!PlannedServerRestartState.IsActive)
                {
                    McpLog.Error($"[WebSocket] {errorMsg} (Detail: {detail})");
                }
                // A failed connection is expected while a planned replacement process
                // starts. Do not log from this worker thread: Unity can classify even
                // Debug.Log calls from it as Exception console entries.
                _state = TransportState.Disconnected(TransportDisplayName, errorMsg);
                return false;
            }

            if (!string.Equals(connectedEndpoint.Host, originalEndpoint.Host, StringComparison.OrdinalIgnoreCase))
            {
                McpLog.Warn($"[WebSocket] Connected via fallback host '{connectedEndpoint.Host}' after '{originalEndpoint.Host}' failed.");
                _endpointUri = connectedEndpoint;
            }

            StartBackgroundLoops(connectionToken);

            try
            {
                await SendRegisterAsync(connectionToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string regMsg = $"Registration with server failed: {ex.Message}";
                if (!PlannedServerRestartState.IsActive)
                {
                    McpLog.Error($"[WebSocket] {regMsg}");
                }
                _state = TransportState.Disconnected(TransportDisplayName, regMsg);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Stops the connection loops and disposes of the connection CTS.
        /// Particularly useful when reconnecting, we want to ensure that background loops are cancelled correctly before starting new oens
        /// </summary>
        /// <param name="awaitTasks">Whether to await the receive and keep alive tasks before disposing.</param>
        private async Task StopConnectionLoopsAsync(bool awaitTasks = true)
        {
            if (_connectionCts != null && !_connectionCts.IsCancellationRequested)
            {
                try { _connectionCts.Cancel(); } catch { }
            }

            if (_receiveTask != null)
            {
                if (awaitTasks)
                {
                    try { await _receiveTask.ConfigureAwait(false); } catch { }
                    _receiveTask = null;
                }
                else if (_receiveTask.IsCompleted)
                {
                    _receiveTask = null;
                }
            }

            if (_keepAliveTask != null)
            {
                if (awaitTasks)
                {
                    try { await _keepAliveTask.ConfigureAwait(false); } catch { }
                    _keepAliveTask = null;
                }
                else if (_keepAliveTask.IsCompleted)
                {
                    _keepAliveTask = null;
                }
            }

            if (awaitTasks)
            {
                await DrainRuntimeCommandTasksAsync().ConfigureAwait(false);
            }
            else
            {
                RemoveCompletedRuntimeCommandTasks();
            }

            if (_connectionCts != null)
            {
                _connectionCts.Dispose();
                _connectionCts = null;
            }
        }

        private void StartBackgroundLoops(CancellationToken token)
        {
            if ((_receiveTask != null && !_receiveTask.IsCompleted) || (_keepAliveTask != null && !_keepAliveTask.IsCompleted))
            {
                return;
            }

            _receiveTask = Task.Run(() => ReceiveLoopAsync(token), CancellationToken.None);
            _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(token), CancellationToken.None);
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string message = await ReceiveMessageAsync(token).ConfigureAwait(false);
                    if (message == null)
                    {
                        continue;
                    }
                    await HandleMessageAsync(message, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException wse)
                {
                    if (!PlannedServerRestartState.IsActive)
                        McpLog.Warn($"[WebSocket] Receive loop error: {wse.Message}");
                    await HandleSocketClosureAsync(wse.Message).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex)
                {
                    if (!PlannedServerRestartState.IsActive)
                        McpLog.Warn($"[WebSocket] Unexpected receive error: {ex.Message}");
                    await HandleSocketClosureAsync(ex.Message).ConfigureAwait(false);
                    break;
                }
            }
        }

        private async Task<string> ReceiveMessageAsync(CancellationToken token)
        {
            if (_socket == null)
            {
                return null;
            }

            byte[] rentedBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
            var buffer = new ArraySegment<byte>(rentedBuffer);
            using var ms = new MemoryStream(8192);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await HandleSocketClosureAsync(result.CloseStatusDescription ?? "Server closed connection").ConfigureAwait(false);
                        return null;
                    }

                    if (result.Count > 0)
                    {
                        if (UsesBoundedRuntimeQueue()
                            && WouldExceedMessageLimit(
                                ms.Length,
                                result.Count,
                                CommandRuntimeProtocol.MaxCommandMessageBytes))
                        {
                            throw new InvalidDataException(
                                $"WebSocket message exceeds negotiated limit of "
                                + $"{CommandRuntimeProtocol.MaxCommandMessageBytes} bytes.");
                        }
                        ms.Write(buffer.Array!, buffer.Offset, result.Count);
                    }

                    if (result.EndOfMessage)
                    {
                        break;
                    }
                }

                if (ms.Length == 0)
                {
                    return null;
                }

                return Encoding.UTF8.GetString(ms.ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }

        private async Task HandleMessageAsync(string message, CancellationToken token)
        {
            JObject payload;
            try
            {
                payload = JObject.Parse(message);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[WebSocket] Invalid JSON payload: {ex.Message}");
                return;
            }

            string messageType = payload.Value<string>("type") ?? string.Empty;
            if (UsesBoundedRuntimeQueue()
                && !string.Equals(messageType, "execute", StringComparison.Ordinal)
                && Encoding.UTF8.GetByteCount(message) > CommandRuntimeProtocol.MaxControlMessageBytes)
            {
                McpLog.Warn(
                    $"[WebSocket] Ignoring oversized control message '{messageType}' "
                    + $"(limit {CommandRuntimeProtocol.MaxControlMessageBytes} bytes).");
                return;
            }

            switch (messageType)
            {
                case "welcome":
                    ApplyWelcome(payload);
                    break;
                case "registered":
                    await HandleRegisteredAsync(payload, token).ConfigureAwait(false);
                    break;
                case "execute":
                    if (UsesBoundedRuntimeQueue())
                    {
                        await AdmitRuntimeExecuteAsync(payload, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await HandleExecuteAsync(payload, token, runtimeManaged: false).ConfigureAwait(false);
                    }
                    break;
                case "receipt_status":
                    await HandleReceiptStatusAsync(payload, token).ConfigureAwait(false);
                    break;
                case "cancel_request":
                    await HandleCancelRequestAsync(payload, token).ConfigureAwait(false);
                    break;
                case "receipt_ledger":
                    await HandleReceiptLedgerAsync(payload, token).ConfigureAwait(false);
                    break;
                case "ping":
                    await SendPongAsync(token).ConfigureAwait(false);
                    break;
                default:
                    // No-op for unrecognised types (keep-alives, telemetry, etc.)
                    break;
            }
        }

        private void ApplyWelcome(JObject payload)
        {
            _localRuntime ??= CommandRuntimeProtocol.BuildUnityAdvertisement(_packageVersion);
            _runtimeNegotiation = CommandRuntimeProtocol.Negotiate(
                _localRuntime,
                payload["runtime"] as JObject);

            int? keepAliveSeconds = payload.Value<int?>("keepAliveInterval");
            if (keepAliveSeconds.HasValue && keepAliveSeconds.Value > 0)
            {
                _keepAliveInterval = TimeSpan.FromSeconds(keepAliveSeconds.Value);
                _socketKeepAliveInterval = _keepAliveInterval;
            }

            int? serverTimeoutSeconds = payload.Value<int?>("serverTimeout");
            if (serverTimeoutSeconds.HasValue)
            {
                int sourceSeconds = keepAliveSeconds ?? serverTimeoutSeconds.Value;
                int safeSeconds = Math.Max(5, Math.Min(serverTimeoutSeconds.Value, sourceSeconds));
                _socketKeepAliveInterval = TimeSpan.FromSeconds(safeSeconds);
            }
        }

        private async Task HandleRegisteredAsync(JObject payload, CancellationToken token)
        {
            string newSessionId = payload.Value<string>("session_id");
            if (!string.IsNullOrEmpty(newSessionId))
            {
                _sessionId = newSessionId;
                ProjectIdentityUtility.SetSessionId(_sessionId);
                _state = TransportState.Connected(TransportDisplayName, sessionId: _sessionId, details: _endpointUri.ToString());
                McpLog.Info($"[WebSocket] Registered with session ID: {_sessionId}", false);

                await SendRegisterToolsAsync(token).ConfigureAwait(false);
                PlannedServerRestartState.MarkReconnected();
            }
        }

        private async Task SendRegisterToolsAsync(CancellationToken token)
        {
            if (_toolDiscoveryService == null) return;

            token.ThrowIfCancellationRequested();
            var tools = await GetEnabledToolsOnMainThreadAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            McpLog.Info($"[WebSocket] Preparing to register {tools.Count} tool(s) with the bridge.", false);
            var toolsArray = new JArray();

            foreach (var tool in tools)
            {
                var toolObj = new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["structured_output"] = tool.StructuredOutput,
                    ["requires_polling"] = tool.RequiresPolling,
                    ["poll_action"] = tool.PollAction ?? "status",
                    ["max_poll_seconds"] = tool.MaxPollSeconds,
                    ["group"] = string.IsNullOrWhiteSpace(tool.Group) ? "core" : tool.Group,
                    ["is_built_in"] = tool.IsBuiltIn
                };

                var paramsArray = new JArray();
                if (tool.Parameters != null)
                {
                    foreach (var p in tool.Parameters)
                    {
                        paramsArray.Add(new JObject
                        {
                            ["name"] = p.Name,
                            ["description"] = p.Description,
                            ["type"] = p.Type,
                            ["required"] = p.Required,
                            ["default_value"] = p.DefaultValue
                        });
                    }
                }
                toolObj["parameters"] = paramsArray;
                toolsArray.Add(toolObj);
            }

            var payload = new JObject
            {
                ["type"] = "register_tools",
                ["tools"] = toolsArray
            };

            await SendJsonAsync(payload, token).ConfigureAwait(false);
            McpLog.Info($"[WebSocket] Sent {tools.Count} tools registration", false);
        }

        public async Task ReregisterToolsAsync()
        {
            if (!IsConnected || _lifecycleCts == null)
            {
                McpLog.Warn("[WebSocket] Cannot reregister tools: not connected");
                return;
            }

            try
            {
                await SendRegisterToolsAsync(_lifecycleCts.Token).ConfigureAwait(false);
                McpLog.Info("[WebSocket] Tool reregistration completed", false);
            }
            catch (System.OperationCanceledException)
            {
                McpLog.Warn("[WebSocket] Tool reregistration cancelled");
            }
            catch (System.Exception ex)
            {
                McpLog.Error($"[WebSocket] Tool reregistration failed: {ex.Message}");
            }
        }

        private async Task AdmitRuntimeExecuteAsync(JObject payload, CancellationToken token)
        {
            long admittedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            ReceiptAdmission receiptAdmission = null;
            if (UsesReceiptRuntime())
            {
                receiptAdmission = CommandReceiptLedger.TryAdmit(
                    payload["runtime"] as JObject,
                    payload.Value<string>("name"),
                    payload.Value<JObject>("params") ?? new JObject(),
                    _projectHash);
                if (!receiptAdmission.ShouldExecute)
                {
                    await SendCommandResultAsync(
                        payload.Value<string>("id"),
                        receiptAdmission.ImmediateResult,
                        receiptAdmission.RequestId,
                        token).ConfigureAwait(false);
                    return;
                }
            }

            if (UsesMutationRuntime())
            {
                CommandMutationAuthorization authorization = CommandMutationPolicy.Authorize(
                    payload.Value<string>("name"),
                    payload["runtime"] as JObject,
                    _mutationProfile,
                    payload.Value<JObject>("params") ?? new JObject());
                if (!authorization.Allowed)
                {
                    JToken error = BuildRuntimeErrorResult(
                        authorization.Code,
                        authorization.Message,
                        includeRetry: false);
                    if (error is JObject errorObject)
                    {
                        errorObject["data"] = authorization.Data;
                    }
                    if (receiptAdmission != null)
                    {
                        error = PrepareRuntimeResult(
                            error,
                            receiptAdmission.RequestId,
                            queuedMilliseconds: 0L,
                            executionMilliseconds: 0L);
                        CommandReceiptLedger.Complete(receiptAdmission.RequestId, error);
                    }
                    await SendCommandResultAsync(
                        payload.Value<string>("id"),
                        error,
                        receiptAdmission?.RequestId,
                        token).ConfigureAwait(false);
                    return;
                }
            }

            int admitted = Interlocked.Increment(ref _admittedRuntimeCommands);
            if (admitted > CommandRuntimeProtocol.MaxQueuedCommands)
            {
                Interlocked.Decrement(ref _admittedRuntimeCommands);
                JToken error = BuildRuntimeErrorResult(
                    "QUEUE_FULL",
                    $"Runtime command queue is full ({CommandRuntimeProtocol.MaxQueuedCommands} commands).");
                if (receiptAdmission != null)
                {
                    error = PrepareRuntimeResult(
                        error,
                        receiptAdmission.RequestId,
                        queuedMilliseconds: 0L,
                        executionMilliseconds: 0L);
                    CommandReceiptLedger.Complete(receiptAdmission.RequestId, error);
                }
                await SendCommandResultAsync(
                    payload.Value<string>("id"),
                    error,
                    receiptAdmission?.RequestId,
                    token).ConfigureAwait(false);
                return;
            }

            Task task = HandleExecuteAsync(
                payload,
                token,
                runtimeManaged: true,
                receiptAdmission?.RequestId,
                admittedTimestamp);
            lock (_runtimeCommandTasksLock)
            {
                _runtimeCommandTasks.Add(task);
            }

            _ = task.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    lock (_runtimeCommandTasksLock)
                    {
                        _runtimeCommandTasks.Remove(completed);
                    }
                    Interlocked.Decrement(ref _admittedRuntimeCommands);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task HandleExecuteAsync(
            JObject payload,
            CancellationToken token,
            bool runtimeManaged,
            string requestId = null,
            long admittedTimestamp = 0L)
        {
            string commandId = payload.Value<string>("id");
            string commandName = payload.Value<string>("name");
            JObject parameters = payload.Value<JObject>("params") ?? new JObject();
            int timeoutSeconds = payload.Value<int?>("timeout") ?? (int)DefaultCommandTimeout.TotalSeconds;

            if (string.IsNullOrEmpty(commandId) || string.IsNullOrEmpty(commandName))
            {
                McpLog.Warn("[WebSocket] Invalid execute payload (missing id or name)");
                return;
            }

            var commandEnvelope = new JObject
            {
                ["type"] = commandName,
                ["params"] = parameters
            };

            string responseJson;
            CancellationTokenSource timeoutCts = null;
            long executingTimestamp = 0L;
            bool cancelledBeforeExecution = false;
            bool enteredExecution = false;
            JObject beforeState = null;
            JObject afterState = null;
            CommandRuntimeToolPolicy commandPolicy = null;
            JObject runtimeMetadata = payload["runtime"] as JObject;
            string runtimeToolName = runtimeMetadata?.Value<string>("tool_name");
            if (!string.IsNullOrEmpty(runtimeToolName))
            {
                CommandRuntimeContract.ToolPolicies.TryGetValue(
                    runtimeToolName,
                    out commandPolicy);
            }
            else
            {
                CommandMutationPolicy.TryResolvePolicy(
                    commandName,
                    parameters,
                    out _,
                    out commandPolicy);
            }
            try
            {
                timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
                if (!string.IsNullOrEmpty(requestId))
                {
                    lock (_runtimeCancellationLock)
                    {
                        _runtimeCancellations[requestId] = timeoutCts;
                    }
                    CommandReceiptLedger.MarkQueued(requestId);
                }
                string commandJson = commandEnvelope.ToString(Formatting.None);
                responseJson = runtimeManaged
                    ? await TransportCommandDispatcher.ExecuteRuntimeCommandJsonAsync(
                        commandJson,
                        timeoutCts.Token,
                        string.IsNullOrEmpty(requestId)
                            ? null
                            : () =>
                            {
                                if (UsesStateRuntime())
                                {
                                    beforeState = CommandRuntimeState.Snapshot();
                                    CommandRuntimeState.ValidateIfMatch(
                                        runtimeMetadata?["if_match"] as JObject,
                                        commandPolicy);
                                }
                                executingTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                                enteredExecution = true;
                                CommandReceiptLedger.MarkExecuting(requestId);
                            }).ConfigureAwait(false)
                    : await TransportCommandDispatcher.ExecuteCommandJsonAsync(
                        commandJson,
                        timeoutCts.Token).ConfigureAwait(false);
            }
            catch (CommandQueueFullException ex)
            {
                responseJson = JsonConvert.SerializeObject(new
                {
                    status = "error",
                    success = false,
                    code = ex.Code,
                    error = ex.Message,
                    data = new { retry_after_ms = 100 }
                });
            }
            catch (OperationCanceledException)
            {
                const string cancellationCode = "CANCELLED_BEFORE_EXECUTION";
                const string cancellationMessage = "Command was cancelled before execution completed.";
                responseJson = BuildRuntimeErrorResult(
                    cancellationCode,
                    cancellationMessage,
                    includeRetry: false).ToString(Formatting.None);
                cancelledBeforeExecution = true;
            }
            catch (Exception ex)
            {
                responseJson = JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = ex.Message
                });
            }
            finally
            {
                if (!string.IsNullOrEmpty(requestId))
                {
                    lock (_runtimeCancellationLock)
                    {
                        _runtimeCancellations.Remove(requestId);
                    }
                }
                timeoutCts?.Dispose();
            }

            JToken resultToken;
            try
            {
                resultToken = JToken.Parse(responseJson);
            }
            catch
            {
                resultToken = new JObject
                {
                    ["status"] = "error",
                    ["error"] = "Invalid response payload"
                };
            }

            if (UsesStateRuntime())
            {
                afterState = await TransportCommandDispatcher.RunOnMainThreadAsync(
                    () =>
                    {
                        if (enteredExecution && CommandRuntimeResponse.IsSuccessful(resultToken))
                        {
                            CommandRuntimeState.MarkCommandMutation(commandPolicy);
                        }
                        return CommandRuntimeState.Snapshot();
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }

            var responsePayload = new JObject
            {
                ["type"] = "command_result",
                ["id"] = commandId,
                ["result"] = resultToken
            };

            long completedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            long queuedMilliseconds = executingTimestamp > 0L && admittedTimestamp > 0L
                ? StopwatchMilliseconds(admittedTimestamp, executingTimestamp)
                : 0L;
            long executionMilliseconds = executingTimestamp > 0L
                ? StopwatchMilliseconds(executingTimestamp, completedTimestamp)
                : admittedTimestamp > 0L
                    ? StopwatchMilliseconds(admittedTimestamp, completedTimestamp)
                    : 0L;
            responsePayload["result"] = PrepareRuntimeResult(
                responsePayload["result"],
                requestId,
                queuedMilliseconds,
                executionMilliseconds,
                beforeState,
                afterState);

            if (runtimeManaged
                && Encoding.UTF8.GetByteCount(responsePayload.ToString(Formatting.None))
                    > CommandRuntimeProtocol.MaxResponseMessageBytes)
            {
                JToken responseTooLarge = BuildRuntimeErrorResult(
                    "RESPONSE_TOO_LARGE",
                    $"Command response exceeds negotiated limit of "
                    + $"{CommandRuntimeProtocol.MaxResponseMessageBytes} bytes.");
                responsePayload["result"] = PrepareRuntimeResult(
                    responseTooLarge,
                    requestId,
                    queuedMilliseconds,
                    executionMilliseconds,
                    beforeState,
                    afterState);
            }

            if (!string.IsNullOrEmpty(requestId))
            {
                if (cancelledBeforeExecution)
                {
                    if (responsePayload["result"] is JObject runtimeCancellation
                        && runtimeCancellation.Value<int?>("runtime_version") == 1)
                    {
                        runtimeCancellation["status"] = "cancelled";
                    }
                    CommandReceiptLedger.MarkCancelled(
                        requestId,
                        "CANCELLED_BEFORE_EXECUTION",
                        "Command was cancelled before execution completed.",
                        responsePayload["result"]);
                }
                else
                {
                    CommandReceiptLedger.Complete(requestId, responsePayload["result"]);
                }
            }

            await SendJsonAsync(responsePayload, token).ConfigureAwait(false);
        }

        internal static bool WouldExceedMessageLimit(
            long accumulatedBytes,
            int incomingBytes,
            int limitBytes)
        {
            return accumulatedBytes < 0
                || incomingBytes < 0
                || accumulatedBytes > limitBytes
                || incomingBytes > limitBytes - accumulatedBytes;
        }

        internal static JObject BuildRuntimeErrorResult(
            string code,
            string message,
            bool includeRetry = true)
        {
            var result = new JObject
            {
                ["status"] = "error",
                ["success"] = false,
                ["code"] = code,
                ["error"] = message
            };
            if (includeRetry)
            {
                result["data"] = new JObject { ["retry_after_ms"] = 100 };
            }
            return result;
        }

        private async Task SendCommandResultAsync(
            string commandId,
            JToken result,
            string requestId,
            CancellationToken token)
        {
            if (string.IsNullOrEmpty(commandId))
            {
                McpLog.Warn("[WebSocket] Cannot send command result without command id.");
                return;
            }

            await SendJsonAsync(
                new JObject
                {
                    ["type"] = "command_result",
                    ["id"] = commandId,
                    ["result"] = PrepareRuntimeResult(
                        result ?? BuildRuntimeErrorResult(
                        "INVALID_RUNTIME_RESULT",
                        "Runtime result is unavailable.",
                        includeRetry: false),
                        requestId,
                        queuedMilliseconds: 0L,
                        executionMilliseconds: 0L)
                },
                token).ConfigureAwait(false);
        }

        private bool UsesBoundedRuntimeQueue()
        {
            return _runtimeNegotiation.Mode != CommandRuntimeMode.Legacy
                && _runtimeNegotiation.HasCapability(
                    CommandRuntimeProtocol.BoundedCommandQueueCapability)
                && _runtimeNegotiation.HasCapability(
                    CommandRuntimeProtocol.ControlPathCapability);
        }

        private bool UsesReceiptRuntime()
        {
            return UsesBoundedRuntimeQueue()
                && _runtimeNegotiation.HasCapability(
                    CommandRuntimeProtocol.RequestReceiptsCapability);
        }

        private bool UsesReceiptLedgerAdmin()
        {
            return UsesReceiptRuntime()
                && _runtimeNegotiation.HasCapability(
                    CommandRuntimeProtocol.ReceiptLedgerAdminCapability);
        }

        private bool UsesResponseRuntime()
        {
            return UsesReceiptRuntime()
                && _runtimeNegotiation.HasCapability(
                    CommandRuntimeProtocol.ResponseEnvelopeCapability);
        }

        private bool UsesMutationRuntime()
        {
            return UsesReceiptRuntime()
                && _runtimeNegotiation.HasCapability(
                    CommandRuntimeProtocol.MutationContractsCapability);
        }

        private bool UsesStateRuntime()
        {
            return UsesResponseRuntime()
                && _runtimeNegotiation.HasCapability(
                    CommandRuntimeProtocol.StateRevisionsCapability);
        }

        private JToken PrepareRuntimeResult(
            JToken result,
            string requestId,
            long queuedMilliseconds,
            long executionMilliseconds,
            JObject beforeState = null,
            JObject afterState = null)
        {
            if (!UsesResponseRuntime()
                || string.IsNullOrEmpty(requestId)
                || result?.Value<int?>("runtime_version") == 1)
            {
                return result;
            }

            return CommandRuntimeResponse.Adapt(
                result,
                requestId,
                queuedMilliseconds,
                executionMilliseconds,
                beforeState,
                afterState);
        }

        private static long StopwatchMilliseconds(long startTimestamp, long endTimestamp)
        {
            if (startTimestamp <= 0L || endTimestamp <= startTimestamp)
            {
                return 0L;
            }

            return (long)((endTimestamp - startTimestamp) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency);
        }

        private async Task HandleReceiptStatusAsync(JObject payload, CancellationToken token)
        {
            if (!UsesReceiptRuntime())
            {
                return;
            }

            await SendJsonAsync(
                new JObject
                {
                    ["type"] = "receipt_status_result",
                    ["id"] = payload.Value<string>("id"),
                    ["receipt"] = CommandReceiptLedger.Query(
                        payload.Value<string>("request_id"))
                },
                token).ConfigureAwait(false);
        }

        private async Task HandleCancelRequestAsync(JObject payload, CancellationToken token)
        {
            if (!UsesReceiptRuntime())
            {
                return;
            }

            string requestId = payload.Value<string>("request_id");
            bool recorded = CommandReceiptLedger.RequestCancellation(requestId);
            bool cancellationSignalled = false;
            lock (_runtimeCancellationLock)
            {
                if (_runtimeCancellations.TryGetValue(
                        requestId ?? string.Empty,
                        out CancellationTokenSource cancellation))
                {
                    try
                    {
                        cancellation.Cancel();
                        cancellationSignalled = true;
                    }
                    catch (ObjectDisposedException) { }
                }
            }

            await SendJsonAsync(
                new JObject
                {
                    ["type"] = "cancel_request_result",
                    ["id"] = payload.Value<string>("id"),
                    ["accepted"] = recorded,
                    ["cancellation_signalled"] = cancellationSignalled,
                    ["receipt"] = CommandReceiptLedger.Query(requestId)
                },
                token).ConfigureAwait(false);
        }

        private async Task HandleReceiptLedgerAsync(JObject payload, CancellationToken token)
        {
            if (!UsesReceiptLedgerAdmin())
            {
                return;
            }

            string action = payload.Value<string>("action") ?? "diagnose";
            JObject result;
            if (string.Equals(action, "diagnose", StringComparison.Ordinal))
            {
                result = new JObject
                {
                    ["status"] = "success",
                    ["success"] = true,
                    ["action"] = "diagnose",
                    ["ledger"] = CommandReceiptLedger.Diagnose()
                };
            }
            else if (string.Equals(action, "cleanup", StringComparison.Ordinal))
            {
                result = CommandReceiptLedger.Cleanup(
                    payload.Value<string>("scope"),
                    payload.Value<bool?>("confirm_outcome_unknown") ?? false);
            }
            else
            {
                result = new JObject
                {
                    ["status"] = "error",
                    ["success"] = false,
                    ["code"] = "INVALID_RECEIPT_LEDGER_ACTION",
                    ["error"] = "Receipt ledger action must be 'diagnose' or 'cleanup'."
                };
            }

            await SendJsonAsync(
                new JObject
                {
                    ["type"] = "receipt_ledger_result",
                    ["id"] = payload.Value<string>("id"),
                    ["result"] = result
                },
                token).ConfigureAwait(false);
        }

        private async Task DrainRuntimeCommandTasksAsync()
        {
            Task[] tasks;
            lock (_runtimeCommandTasksLock)
            {
                tasks = _runtimeCommandTasks.ToArray();
            }

            if (tasks.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                // Connection cancellation and per-command errors are already converted
                // to command_result payloads or observed by task continuations.
            }
        }

        private void RemoveCompletedRuntimeCommandTasks()
        {
            lock (_runtimeCommandTasksLock)
            {
                _runtimeCommandTasks.RemoveWhere(task => task.IsCompleted);
            }
        }

        private async Task KeepAliveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_keepAliveInterval, token).ConfigureAwait(false);
                    if (_socket == null || _socket.State != WebSocketState.Open)
                    {
                        break;
                    }
                    await SendPongAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[WebSocket] Keep-alive failed: {ex.Message}");
                    await HandleSocketClosureAsync(ex.Message).ConfigureAwait(false);
                    break;
                }
            }
        }

        private async Task SendRegisterAsync(CancellationToken token)
        {
            await SendJsonAsync(BuildRegisterPayload(), token).ConfigureAwait(false);
        }

        internal JObject BuildRegisterPayload()
        {
            _localRuntime ??= CommandRuntimeProtocol.BuildUnityAdvertisement(_packageVersion);
            return new JObject
            {
                ["type"] = "register",
                // session_id is now server-authoritative; omitted here or sent as null
                ["project_name"] = _projectName,
                ["project_hash"] = _projectHash,
                ["unity_version"] = _unityVersion,
                ["project_path"] = _projectPath,
                ["runtime"] = _localRuntime.DeepClone()
            };
        }

        private Task SendPongAsync(CancellationToken token)
        {
            var payload = new JObject
            {
                ["type"] = "pong",
                ["session_id"] = _sessionId  // Include session ID for server-side tracking
            };
            return SendJsonAsync(payload, token);
        }

        private async Task SendJsonAsync(JObject payload, CancellationToken token)
        {
            if (_socket == null)
            {
                throw new InvalidOperationException("WebSocket is not initialised");
            }

            string json = payload.ToString(Formatting.None);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var buffer = new ArraySegment<byte>(bytes);

            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_socket.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("WebSocket is not open");
                }

                await _socket.SendAsync(buffer, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task HandleSocketClosureAsync(string reason)
        {
            bool plannedRestart = PlannedServerRestartState.IsActive;
            if (!plannedRestart)
            {
                // Unexpected disconnects retain the detailed stack used for diagnostics.
                var stackTrace = new System.Diagnostics.StackTrace(true);
                McpLog.Debug($"[WebSocket] HandleSocketClosureAsync called. Reason: {reason}\nStack trace:\n{stackTrace}");
            }

            if (_lifecycleCts == null || _lifecycleCts.IsCancellationRequested)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _isReconnectingFlag, 1, 0) != 0)
            {
                return;
            }

            _isConnected = false;
            _state = _state.WithError(reason ?? "Connection closed");
            if (!plannedRestart)
            {
                McpLog.Warn($"[WebSocket] Connection closed: {reason}");
            }

            await StopConnectionLoopsAsync(awaitTasks: false).ConfigureAwait(false);

            _ = Task.Run(() => AttemptReconnectAsync(_lifecycleCts.Token), CancellationToken.None);
        }

        private async Task AttemptReconnectAsync(CancellationToken token)
        {
            try
            {
                await StopConnectionLoopsAsync().ConfigureAwait(false);

                IEnumerable<TimeSpan> reconnectSchedule = PlannedServerRestartState.IsActive
                    ? PlannedRestartReconnectSchedule
                    : ReconnectSchedule;
                foreach (TimeSpan delay in reconnectSchedule)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (delay > TimeSpan.Zero)
                    {
                        try { await Task.Delay(delay, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }

                    if (await EstablishConnectionAsync(token).ConfigureAwait(false))
                    {
                        _state = TransportState.Connected(TransportDisplayName, sessionId: _sessionId, details: _endpointUri.ToString());
                        _isConnected = true;
                        McpLog.Info("[WebSocket] Reconnected to MCP server", false);
                        return;
                    }
                }

                // Schedule exhausted — keep retrying every 30 s indefinitely so a transient
                // server outage longer than ~49 s doesn't leave the plugin permanently dead.
                if (!PlannedServerRestartState.IsActive)
                {
                    McpLog.Warn($"[WebSocket] Initial reconnect schedule exhausted. Retrying every {ReconnectTailInterval.TotalSeconds}s until cancelled.");
                }
                _state = _state.WithError($"Server unreachable – retrying every {ReconnectTailInterval.TotalSeconds} s");
                while (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(ReconnectTailInterval, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    if (await EstablishConnectionAsync(token).ConfigureAwait(false))
                    {
                        _state = TransportState.Connected(TransportDisplayName, sessionId: _sessionId, details: _endpointUri.ToString());
                        _isConnected = true;
                        McpLog.Info("[WebSocket] Reconnected to MCP server", false);
                        return;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isReconnectingFlag, 0);
            }
        }

        private static Uri BuildWebSocketUri(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var httpUri))
            {
                throw new InvalidOperationException($"Invalid MCP base URL: {baseUrl}");
            }

            // Replace bind-only addresses for client connections
            // 0.0.0.0 and :: are only valid for server binding, not client connections
            string host = httpUri.Host;
            if (host == "0.0.0.0")
            {
                McpLog.Warn($"[WebSocket] Base URL host '{host}' is bind-only; using '127.0.0.1' for client connection.");
                host = "127.0.0.1";
            }
            else if (host == "::")
            {
                McpLog.Warn($"[WebSocket] Base URL host '{host}' is bind-only; using '::1' for client connection.");
                host = "::1";
            }

            var builder = new UriBuilder(httpUri)
            {
                Scheme = httpUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
                Host = host,
                Path = httpUri.AbsolutePath.TrimEnd('/') + "/hub/plugin"
            };

            return builder.Uri;
        }

        private static List<Uri> BuildConnectionCandidateUris(Uri endpointUri)
        {
            var candidates = new List<Uri>();
            if (endpointUri == null)
            {
                return candidates;
            }

            candidates.Add(endpointUri);

            if (!string.Equals(endpointUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return candidates;
            }

            // Retry localhost using explicit loopback hosts to avoid DNS family ambiguity on some machines.
            TryAddCandidate(candidates, endpointUri, "127.0.0.1");
            TryAddCandidate(candidates, endpointUri, "::1");
            return candidates;
        }

        private static void TryAddCandidate(List<Uri> candidates, Uri template, string host)
        {
            try
            {
                var builder = new UriBuilder(template) { Host = host };
                Uri candidate = builder.Uri;
                foreach (Uri existing in candidates)
                {
                    if (Uri.Compare(existing, candidate, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        return;
                    }
                }
                candidates.Add(candidate);
            }
            catch
            {
                // Ignore malformed fallback candidate and continue with remaining options.
            }
        }
    }
}
