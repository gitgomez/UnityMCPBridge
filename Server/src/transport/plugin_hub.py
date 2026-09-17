"""WebSocket hub for Unity plugin communication."""

from __future__ import annotations

import asyncio
import json
import logging
import os
import time
import uuid
import weakref
from datetime import datetime, timezone
from typing import TYPE_CHECKING, Any, ClassVar

from starlette.endpoints import WebSocketEndpoint
from starlette.websockets import WebSocket, WebSocketState

from core.config import config
from core.constants import API_KEY_HEADER
from models.models import MCPResponse
from transport.plugin_registry import PluginRegistry
from transport.mutation_policy import (
    authorize_mutation,
    get_server_mutation_profile,
)
from transport.runtime_protocol import (
    BOUNDED_COMMAND_QUEUE_CAPABILITY,
    CONTROL_PATH_CAPABILITY,
    MAX_COMMAND_MESSAGE_BYTES,
    MAX_QUEUED_COMMANDS,
    MAX_QUEUED_PAYLOAD_BYTES,
    MUTATION_CONTRACTS_CAPABILITY,
    RECEIPT_LEDGER_ADMIN_CAPABILITY,
    REQUEST_RECEIPTS_CAPABILITY,
    RuntimeMode,
    build_runtime_command_metadata,
    build_server_advertisement,
    negotiate_runtime,
)
from services.api_key_service import ApiKeyService

if TYPE_CHECKING:
    from fastmcp import FastMCP
from transport.models import (
    WelcomeMessage,
    RegisteredMessage,
    ExecuteCommandMessage,
    PingMessage,
    RegisterMessage,
    RegisterToolsMessage,
    PongMessage,
    PluginLifecycleMessage,
    CommandResultMessage,
    ReceiptStatusResultMessage,
    CancelRequestResultMessage,
    ReceiptLedgerResultMessage,
    SessionList,
    SessionDetails,
)

logger = logging.getLogger(__name__)


def _read_bounded_wait_env(name: str, default_s: float, max_s: float) -> float:
    """Read a wait-seconds env override, clamped to [0, max_s].

    The ceiling exists to keep a typo from stalling every command, but it must sit
    well above the default so explicit overrides actually take effect (#1207).
    """
    raw = os.environ.get(name)
    if raw is None:
        return max(0.0, min(default_s, max_s))
    try:
        value = float(raw)
    except ValueError as e:
        logger.warning("Invalid %s=%r, using default %s: %s", name, raw, default_s, e)
        value = default_s
    return max(0.0, min(value, max_s))


# ---------- MCP session tracking ----------
# FastMCP doesn't expose active MCP client sessions.  We patch
# ``MiddlewareServerSession.__aenter__`` once to register every new
# session so we can send ``tools/list_changed`` notifications later.
_active_mcp_sessions: weakref.WeakSet = weakref.WeakSet()
_session_tracking_installed = False


def _install_session_tracking() -> None:
    """Patch *MiddlewareServerSession* to track active MCP client sessions."""
    global _session_tracking_installed
    if _session_tracking_installed:
        return
    _session_tracking_installed = True

    from fastmcp.server.low_level import MiddlewareServerSession

    _original_aenter = MiddlewareServerSession.__aenter__

    async def _tracking_aenter(self):  # type: ignore[override]
        result = await _original_aenter(self)
        _active_mcp_sessions.add(self)
        return result

    MiddlewareServerSession.__aenter__ = _tracking_aenter  # type: ignore[assignment]


class PluginDisconnectedError(RuntimeError):
    """Raised when a plugin WebSocket disconnects while commands are in flight."""


class NoUnitySessionError(RuntimeError):
    """Raised when no Unity plugins are available."""

    def __init__(self, message: str, reason: str = "no_unity_session"):
        self.reason = reason
        super().__init__(message)


class InstanceSelectionRequiredError(RuntimeError):
    """Raised when the caller must explicitly select a Unity instance."""

    _SELECTION_REQUIRED = (
        "Unity instance selection is required. "
        "Call set_active_instance with Name@hash from mcpforunity://instances."
    )
    _MULTIPLE_INSTANCES = (
        "Multiple Unity instances are connected. "
        "Call set_active_instance with Name@hash from mcpforunity://instances."
    )

    def __init__(self, message: str | None = None,
                 available_instances: list[str] | None = None):
        # Carried structurally so the transport layer can surface the ids without
        # parsing the message; also appended to the text for parity with the stdio
        # guard, which lists the ids inline.
        self.available_instances = available_instances or []
        text = message or self._SELECTION_REQUIRED
        if self.available_instances:
            text = f"{text} Available instances: {self.available_instances}."
        super().__init__(text)


class PluginHub(WebSocketEndpoint):
    """Manages persistent WebSocket connections to Unity plugins."""

    encoding = "json"
    KEEP_ALIVE_INTERVAL = 15
    SERVER_TIMEOUT = 30
    COMMAND_TIMEOUT = 30
    # Server-side ping interval (seconds) - how often to send pings to Unity
    PING_INTERVAL = 10
    # Max time (seconds) to wait for pong before considering connection dead
    PING_TIMEOUT = 20
    # Timeout (seconds) for fast-fail commands like ping/read_console/get_editor_state.
    # Keep short so MCP clients aren't blocked during Unity compilation/reload/unfocused throttling.
    FAST_FAIL_TIMEOUT = 2.0
    # Fast-path commands should never block the client for long; return a retry hint instead.
    # This helps avoid the Cursor-side ~30s tool-call timeout when Unity is compiling/reloading
    # or is throttled while unfocused.
    _FAST_FAIL_COMMANDS: set[str] = {
        "read_console", "get_editor_state", "ping"}

    _registry: PluginRegistry | None = None
    _mcp: FastMCP | None = None
    # Index into mcp._transforms where Unity's server-level overrides start.
    # Transforms before this index are startup defaults; at and after are Unity syncs.
    _unity_transform_start: int | None = None
    _connections: dict[str, WebSocket] = {}
    # command_id -> {"future": Future, "session_id": str}
    _pending: dict[str, dict[str, Any]] = {}
    _lock: asyncio.Lock | None = None
    _loop: asyncio.AbstractEventLoop | None = None
    # session_id -> last pong timestamp (monotonic)
    _last_pong: ClassVar[dict[str, float]] = {}
    # session_id -> ping task
    _ping_tasks: ClassVar[dict[str, asyncio.Task]] = {}

    @classmethod
    def configure(
        cls,
        registry: PluginRegistry,
        loop: asyncio.AbstractEventLoop | None = None,
        mcp: FastMCP | None = None,
    ) -> None:
        cls._registry = registry
        cls._mcp = mcp
        cls._loop = loop or asyncio.get_running_loop()
        # Ensure coordination primitives are bound to the configured loop
        cls._lock = asyncio.Lock()
        # Start tracking MCP client sessions for tool-change notifications
        if mcp is not None:
            _install_session_tracking()

    @classmethod
    def is_configured(cls) -> bool:
        return cls._registry is not None and cls._lock is not None

    async def on_connect(self, websocket: WebSocket) -> None:
        # Validate API key in remote-hosted mode (fail closed)
        if config.http_remote_hosted:
            if not ApiKeyService.is_initialized():
                logger.debug(
                    "WebSocket connection rejected: auth service not initialized")
                await websocket.close(code=1013, reason="Try again later")
                return

            api_key = websocket.headers.get(API_KEY_HEADER)

            if not api_key:
                logger.debug("WebSocket connection rejected: API key required")
                await websocket.close(code=4401, reason="API key required")
                return

            service = ApiKeyService.get_instance()
            result = await service.validate(api_key)

            if not result.valid:
                # Transient auth failures are retryable (1013)
                if result.error and any(
                    indicator in result.error.lower()
                    for indicator in ("unavailable", "timeout", "service error")
                ):
                    logger.debug(
                        "WebSocket connection rejected: auth service unavailable")
                    await websocket.close(code=1013, reason="Try again later")
                    return

                logger.debug("WebSocket connection rejected: invalid API key")
                await websocket.close(code=4403, reason="Invalid API key")
                return

            # Both valid and user_id must be present to accept
            if not result.user_id:
                logger.debug(
                    "WebSocket connection rejected: validated key missing user_id")
                await websocket.close(code=4403, reason="Invalid API key")
                return

            # Store user_id in websocket state for later use during registration
            websocket.state.user_id = result.user_id
            websocket.state.api_key_metadata = result.metadata

        await websocket.accept()
        runtime = build_server_advertisement()
        msg = WelcomeMessage(
            serverTimeout=self.SERVER_TIMEOUT,
            keepAliveInterval=self.KEEP_ALIVE_INTERVAL,
            runtime=runtime,
        )
        await websocket.send_json(msg.model_dump(exclude_none=True))

    async def on_receive(self, websocket: WebSocket, data: Any) -> None:
        if not isinstance(data, dict):
            logger.warning(f"Received non-object payload from plugin: {data}")
            return

        message_type = data.get("type")
        try:
            if message_type == "register":
                await self._handle_register(websocket, RegisterMessage(**data))
            elif message_type == "register_tools":
                await self._handle_register_tools(websocket, RegisterToolsMessage(**data))
            elif message_type == "pong":
                await self._handle_pong(PongMessage(**data))
            elif message_type == "lifecycle":
                await self._handle_lifecycle(
                    websocket, PluginLifecycleMessage(**data)
                )
            elif message_type == "command_result":
                await self._handle_command_result(CommandResultMessage(**data))
            elif message_type == "receipt_status_result":
                await self._handle_control_result(
                    ReceiptStatusResultMessage(**data).model_dump()
                )
            elif message_type == "cancel_request_result":
                await self._handle_control_result(
                    CancelRequestResultMessage(**data).model_dump()
                )
            elif message_type == "receipt_ledger_result":
                await self._handle_control_result(
                    ReceiptLedgerResultMessage(**data).model_dump()
                )
            else:
                logger.debug(f"Ignoring plugin message: {data}")
        except Exception as e:
            logger.error(f"Error handling message type {message_type}: {e}")

    async def on_disconnect(self, websocket: WebSocket, close_code: int) -> None:
        cls = type(self)
        lock = cls._lock
        if lock is None:
            return
        async with lock:
            session_id = next(
                (sid for sid, ws in cls._connections.items() if ws is websocket), None)
            if session_id:
                cls._connections.pop(session_id, None)
                # Stop the ping loop for this session
                ping_task = cls._ping_tasks.pop(session_id, None)
                if ping_task and not ping_task.done():
                    ping_task.cancel()
                # Clean up last pong tracking
                cls._last_pong.pop(session_id, None)
                # Fail-fast any in-flight commands for this session to avoid waiting for COMMAND_TIMEOUT.
                pending_ids = [
                    command_id
                    for command_id, entry in cls._pending.items()
                    if entry.get("session_id") == session_id
                ]
                if pending_ids:
                    logger.debug(f"Cancelling {len(pending_ids)} pending commands for disconnected session")
                for command_id in pending_ids:
                    entry = cls._pending.get(command_id)
                    future = entry.get("future") if isinstance(
                        entry, dict) else None
                    if future and not future.done():
                        future.set_exception(
                            PluginDisconnectedError(
                                f"Unity plugin session {session_id} disconnected while awaiting command_result"
                            )
                        )
                if cls._registry:
                    await cls._registry.unregister(session_id)
                logger.info(
                    f"Plugin session {session_id} disconnected ({close_code})")

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------
    @classmethod
    async def send_command(
        cls,
        session_id: str,
        command_type: str,
        params: dict[str, Any],
        *,
        request_id: str | None = None,
        attempt: int = 1,
    ) -> dict[str, Any]:
        websocket = await cls._get_connection(session_id)
        command_id = str(uuid.uuid4())
        future: asyncio.Future = asyncio.get_running_loop().create_future()
        # Compute a per-command timeout:
        # - fast-path commands: short timeout (encourage retry)
        # - long-running commands: allow caller to request a longer timeout via params
        unity_timeout_s = float(cls.COMMAND_TIMEOUT)
        server_wait_s = float(cls.COMMAND_TIMEOUT)
        if command_type in cls._FAST_FAIL_COMMANDS:
            fast_timeout = float(cls.FAST_FAIL_TIMEOUT)
            unity_timeout_s = fast_timeout
            server_wait_s = fast_timeout
        else:
            # Common tools pass a requested timeout in seconds (e.g., timeout_seconds=900).
            requested = None
            try:
                if isinstance(params, dict):
                    requested = params.get("timeout_seconds", None)
                    if requested is None:
                        requested = params.get("timeoutSeconds", None)
            except Exception:
                requested = None

            if requested is not None:
                try:
                    requested_s = float(requested)
                    # Clamp to a sane upper bound to avoid accidental infinite hangs.
                    requested_s = max(1.0, min(requested_s, 60.0 * 60.0))
                    frame_driven_wait = (
                        command_type == "interact_play_mode"
                        and isinstance(params, dict)
                        and params.get("action") == "wait_ui"
                    )
                    # wait_ui owns the requested interval inside Unity. Keep the
                    # transport deadline outside that interval so the bounded
                    # timeout response can win instead of racing cancellation.
                    unity_cushion_s = 5.0 if frame_driven_wait else 0.0
                    server_cushion_s = 10.0 if frame_driven_wait else 5.0
                    unity_timeout_s = max(
                        unity_timeout_s,
                        requested_s + unity_cushion_s,
                    )
                    server_wait_s = max(
                        server_wait_s,
                        requested_s + server_cushion_s,
                    )
                except Exception:
                    pass

        runtime_capabilities, project_hash = await cls._session_runtime_details(
            session_id
        )
        uses_bounded_runtime = {
            BOUNDED_COMMAND_QUEUE_CAPABILITY,
            CONTROL_PATH_CAPABILITY,
        }.issubset(runtime_capabilities)
        uses_mutation_runtime = {
            MUTATION_CONTRACTS_CAPABILITY,
            REQUEST_RECEIPTS_CAPABILITY,
        }.issubset(runtime_capabilities)
        mutation_profile = "unrestricted"
        tool_name = None
        if uses_mutation_runtime:
            authorization = authorize_mutation(
                get_server_mutation_profile(), command_type, params
            )
            mutation_profile = authorization.profile
            tool_name = authorization.tool_name
            if not authorization.allowed:
                return cls._runtime_admission_error(
                    code=authorization.code or "MUTATION_PROFILE_DENIED",
                    message=authorization.message or "Mutation profile denied command.",
                    data=authorization.error_data(),
                )
        runtime_metadata = None
        if REQUEST_RECEIPTS_CAPABILITY in runtime_capabilities:
            if attempt > 1 and request_id is None:
                raise ValueError("A retry attempt must reuse its logical request_id")
            if_match = params.get("if_match") if isinstance(params, dict) else None
            runtime_metadata = build_runtime_command_metadata(
                name=command_type,
                params=params,
                project_hash=project_hash or session_id,
                timeout_seconds=unity_timeout_s,
                request_id=request_id,
                attempt=attempt,
                tool_name=tool_name,
                profile=mutation_profile,
                if_match=if_match if isinstance(if_match, dict) else None,
            )

        msg = ExecuteCommandMessage(
            id=command_id,
            name=command_type,
            params=params,
            timeout=unity_timeout_s,
            runtime=runtime_metadata,
        )
        message_payload = msg.model_dump(exclude_none=True)
        payload_bytes = len(
            json.dumps(
                message_payload,
                ensure_ascii=False,
                separators=(",", ":"),
            ).encode("utf-8")
        )
        if uses_bounded_runtime and payload_bytes > MAX_COMMAND_MESSAGE_BYTES:
            return cls._runtime_admission_error(
                code="MESSAGE_TOO_LARGE",
                message=(
                    f"Command message is {payload_bytes} bytes; negotiated limit is "
                    f"{MAX_COMMAND_MESSAGE_BYTES} bytes."
                ),
                data={
                    "message_bytes": payload_bytes,
                    "message_limit_bytes": MAX_COMMAND_MESSAGE_BYTES,
                },
            )

        lock = cls._lock
        if lock is None:
            raise RuntimeError("PluginHub not configured")

        async with lock:
            if command_id in cls._pending:
                raise RuntimeError(
                    f"Duplicate command id generated: {command_id}")

            if uses_bounded_runtime:
                session_pending = [
                    entry
                    for entry in cls._pending.values()
                    if entry.get("session_id") == session_id
                    and entry.get("runtime_managed") is True
                ]
                if len(session_pending) >= MAX_QUEUED_COMMANDS:
                    return cls._runtime_admission_error(
                        code="QUEUE_FULL",
                        message=(
                            "Runtime command queue is full "
                            f"({MAX_QUEUED_COMMANDS} commands)."
                        ),
                        data={
                            "retry_after_ms": 100,
                            "queue_depth": len(session_pending),
                            "queue_limit": MAX_QUEUED_COMMANDS,
                        },
                    )

                queued_payload_bytes = sum(
                    int(entry.get("payload_bytes", 0)) for entry in session_pending
                )
                if queued_payload_bytes + payload_bytes > MAX_QUEUED_PAYLOAD_BYTES:
                    return cls._runtime_admission_error(
                        code="QUEUE_BYTES_EXCEEDED",
                        message=(
                            "Runtime command queue payload limit would be exceeded "
                            f"({MAX_QUEUED_PAYLOAD_BYTES} bytes)."
                        ),
                        data={
                            "retry_after_ms": 100,
                            "queued_payload_bytes": queued_payload_bytes,
                            "message_bytes": payload_bytes,
                            "queue_payload_limit_bytes": MAX_QUEUED_PAYLOAD_BYTES,
                        },
                    )

            cls._pending[command_id] = {
                "future": future,
                "session_id": session_id,
                "runtime_managed": uses_bounded_runtime,
                "payload_bytes": payload_bytes if uses_bounded_runtime else 0,
            }

        try:
            try:
                await websocket.send_json(message_payload)
            except Exception as exc:
                # If send fails (socket already closing), fail the future so callers don't hang.
                if not future.done():
                    future.set_exception(exc)
                raise
            try:
                result = await asyncio.wait_for(future, timeout=server_wait_s)
                return result
            except PluginDisconnectedError as exc:
                if REQUEST_RECEIPTS_CAPABILITY in runtime_capabilities:
                    return cls._runtime_admission_error(
                        code="PLUGIN_DISCONNECTED",
                        message=str(exc),
                        data={"retry_after_ms": 100},
                    )
                return MCPResponse(success=False, error=str(exc), hint="retry").model_dump()
            except asyncio.TimeoutError:
                if command_type in cls._FAST_FAIL_COMMANDS:
                    return MCPResponse(
                        success=False,
                        error=f"Unity did not respond to '{command_type}' within {server_wait_s:.1f}s; please retry",
                        hint="retry",
                    ).model_dump()
                raise
        finally:
            async with lock:
                cls._pending.pop(command_id, None)

    @classmethod
    async def _session_uses_bounded_runtime(cls, session_id: str) -> bool:
        capabilities, _ = await cls._session_runtime_details(session_id)
        return {
            BOUNDED_COMMAND_QUEUE_CAPABILITY,
            CONTROL_PATH_CAPABILITY,
        }.issubset(capabilities)

    @classmethod
    async def _session_runtime_details(
        cls,
        session_id: str,
    ) -> tuple[set[str], str | None]:
        registry = cls._registry
        if registry is None:
            return set(), None

        session = await registry.get_session(session_id)
        negotiation = session.runtime_negotiation if session is not None else None
        if (
            negotiation is None
            or negotiation.mode in {RuntimeMode.LEGACY, RuntimeMode.INCOMPATIBLE}
        ):
            return set(), session.project_hash if session is not None else None

        return set(negotiation.capabilities), session.project_hash

    @staticmethod
    def _runtime_admission_error(
        *,
        code: str,
        message: str,
        data: dict[str, Any],
    ) -> dict[str, Any]:
        return {
            "status": "error",
            "success": False,
            "code": code,
            "error": message,
            "hint": "retry" if "retry_after_ms" in data else None,
            "data": data,
        }

    @classmethod
    async def query_receipt(
        cls,
        session_id: str,
        request_id: str,
    ) -> dict[str, Any]:
        """Query Unity's authoritative receipt ledger over the control path."""

        return await cls._send_runtime_control(
            session_id,
            {
                "type": "receipt_status",
                "request_id": request_id,
            },
        )

    @classmethod
    async def cancel_request(
        cls,
        session_id: str,
        request_id: str,
    ) -> dict[str, Any]:
        """Request cancellation without entering the normal command queue."""

        return await cls._send_runtime_control(
            session_id,
            {
                "type": "cancel_request",
                "request_id": request_id,
            },
        )

    @classmethod
    async def manage_receipt_ledger(
        cls,
        session_id: str,
        *,
        action: str = "diagnose",
        scope: str = "expired_terminal",
        confirm_outcome_unknown: bool = False,
    ) -> dict[str, Any]:
        """Diagnose or clean Unity's ledger without normal command admission."""

        response = await cls._send_runtime_control(
            session_id,
            {
                "type": "receipt_ledger",
                "action": action,
                "scope": scope,
                "confirm_outcome_unknown": confirm_outcome_unknown,
            },
            required_capabilities={
                CONTROL_PATH_CAPABILITY,
                REQUEST_RECEIPTS_CAPABILITY,
                RECEIPT_LEDGER_ADMIN_CAPABILITY,
            },
        )
        result = response.get("result") if isinstance(response, dict) else None
        return result if isinstance(result, dict) else response

    @classmethod
    async def _send_runtime_control(
        cls,
        session_id: str,
        message: dict[str, Any],
        required_capabilities: set[str] | None = None,
    ) -> dict[str, Any]:
        capabilities, _ = await cls._session_runtime_details(session_id)
        required = required_capabilities or {
            CONTROL_PATH_CAPABILITY,
            REQUEST_RECEIPTS_CAPABILITY,
        }
        if not required.issubset(capabilities):
            return cls._runtime_admission_error(
                code="CAPABILITY_NOT_NEGOTIATED",
                message="The requested receipt control capability was not negotiated for this Unity session.",
                data={},
            )

        websocket = await cls._get_connection(session_id)
        lock = cls._lock
        if lock is None:
            raise RuntimeError("PluginHub not configured")

        control_id = str(uuid.uuid4())
        future: asyncio.Future = asyncio.get_running_loop().create_future()
        payload = dict(message)
        payload["id"] = control_id

        async with lock:
            cls._pending[control_id] = {
                "future": future,
                "session_id": session_id,
                "runtime_managed": False,
                "payload_bytes": 0,
                "control": True,
            }

        try:
            try:
                await websocket.send_json(payload)
            except Exception as exc:
                if not future.done():
                    future.set_exception(exc)
                raise
            return await asyncio.wait_for(future, timeout=5.0)
        except PluginDisconnectedError as exc:
            return cls._runtime_admission_error(
                code="PLUGIN_DISCONNECTED",
                message=str(exc),
                data={"retry_after_ms": 100},
            )
        except asyncio.TimeoutError:
            return cls._runtime_admission_error(
                code="CONTROL_TIMEOUT",
                message="Unity did not answer the receipt control request within 5 seconds.",
                data={"retry_after_ms": 100},
            )
        finally:
            async with lock:
                cls._pending.pop(control_id, None)

    @classmethod
    async def get_sessions(cls, user_id: str | None = None) -> SessionList:
        """Get all active plugin sessions.

        Args:
            user_id: If provided (remote-hosted mode), only return sessions for this user.
        """
        if cls._registry is None:
            return SessionList(sessions={})
        sessions = await cls._registry.list_sessions(user_id=user_id)
        return SessionList(
            sessions={
                session_id: SessionDetails(
                    project=session.project_name,
                    hash=session.project_hash,
                    unity_version=session.unity_version,
                    connected_at=session.connected_at.isoformat(),
                )
                for session_id, session in sessions.items()
            }
        )

    @classmethod
    async def get_reloading_instances(
        cls, user_id: str | None = None
    ) -> list[str]:
        """Return reload-aware project ids that intentionally disconnected."""

        if cls._registry is None:
            return []
        lifecycles = await cls._registry.list_lifecycles(user_id=user_id)
        return [
            f"{lifecycle.project_name}@{lifecycle.project_hash}"
            for lifecycle in lifecycles
            if lifecycle.supports_reload_lifecycle
            and lifecycle.state == "reloading"
        ]

    @classmethod
    async def get_tools_for_project(
        cls,
        project_hash: str,
        user_id: str | None = None,
    ) -> list[Any]:
        """Retrieve tools registered for an active project hash."""
        if cls._registry is None:
            return []

        session_id = await cls._registry.get_session_id_by_hash(project_hash, user_id=user_id)
        if not session_id:
            return []

        session = await cls._registry.get_session(session_id)
        if not session:
            return []

        return list(session.tools.values())

    @classmethod
    async def get_tool_definition(
        cls,
        project_hash: str,
        tool_name: str,
        user_id: str | None = None,
    ) -> Any | None:
        """Retrieve a specific tool definition for an active project hash."""
        if cls._registry is None:
            return None

        session_id = await cls._registry.get_session_id_by_hash(project_hash, user_id=user_id)
        if not session_id:
            return None

        session = await cls._registry.get_session(session_id)
        if not session:
            return None

        return session.tools.get(tool_name)

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------
    async def _handle_register(self, websocket: WebSocket, payload: RegisterMessage) -> None:
        cls = type(self)
        registry = cls._registry
        lock = cls._lock
        if registry is None or lock is None:
            await websocket.close(code=1011)
            raise RuntimeError("PluginHub not configured")

        project_name = payload.project_name
        project_hash = payload.project_hash
        unity_version = payload.unity_version
        project_path = payload.project_path
        remote_runtime = payload.runtime
        local_runtime = build_server_advertisement()
        runtime_negotiation = negotiate_runtime(local_runtime, remote_runtime)

        if not project_hash:
            await websocket.close(code=4400)
            raise ValueError(
                "Plugin registration missing project_hash")

        # Get user_id from websocket state (set during API key validation)
        user_id = getattr(websocket.state, "user_id", None)

        session_id = str(uuid.uuid4())
        # Inform the plugin of its assigned session ID
        response = RegisteredMessage(session_id=session_id)
        await websocket.send_json(response.model_dump())

        session, evicted_session_id = await registry.register(
            session_id,
            project_name,
            project_hash,
            unity_version,
            project_path,
            user_id=user_id,
            runtime=remote_runtime,
            runtime_negotiation=runtime_negotiation,
        )
        evicted_ws = None
        async with lock:
            # Clean up the evicted session's connection, ping loop, and pending commands
            # so they don't linger as orphans after a domain-reload reconnection race.
            if evicted_session_id:
                evicted_ws = cls._connections.pop(evicted_session_id, None)
                old_ping = cls._ping_tasks.pop(evicted_session_id, None)
                if old_ping and not old_ping.done():
                    old_ping.cancel()
                cls._last_pong.pop(evicted_session_id, None)
                cancelled_commands = []
                for command_id, entry in list(cls._pending.items()):
                    if entry.get("session_id") == evicted_session_id:
                        future = entry.get("future")
                        if future and not future.done():
                            future.set_exception(
                                PluginDisconnectedError(
                                    f"Unity plugin session {evicted_session_id} superseded by {session_id}"
                                )
                            )
                            cancelled_commands.append(command_id)
                        cls._pending.pop(command_id, None)
                if cancelled_commands:
                    logger.info(
                        "Evicted session %s: cancelled pending commands %s",
                        evicted_session_id,
                        cancelled_commands,
                    )
                logger.info(f"Evicted previous session {evicted_session_id} for same instance")

            cls._connections[session.session_id] = websocket
            # Initialize last pong time and start ping loop for this session
            cls._last_pong[session_id] = time.monotonic()
            # Cancel any existing ping task for this session (shouldn't happen, but be safe)
            old_task = cls._ping_tasks.pop(session_id, None)
            if old_task and not old_task.done():
                old_task.cancel()
            # Start the server-side ping loop
            ping_task = asyncio.create_task(cls._ping_loop(session_id, websocket))
            cls._ping_tasks[session_id] = ping_task

        # Close evicted WebSocket outside the lock to avoid blocking
        if evicted_ws is not None:
            try:
                await evicted_ws.close(code=1001)
            except Exception:
                logger.debug(
                    "Failed to close evicted WebSocket for session %s",
                    evicted_session_id,
                    exc_info=True,
                )

        if user_id:
            logger.info(f"Plugin registered: {project_name} ({project_hash}) for user {user_id}")
        else:
            logger.info(f"Plugin registered: {project_name} ({project_hash})")

    async def _handle_register_tools(self, websocket: WebSocket, payload: RegisterToolsMessage) -> None:
        cls = type(self)
        registry = cls._registry
        lock = cls._lock
        if registry is None or lock is None:
            return

        # Find session_id for this websocket
        async with lock:
            session_id = next(
                (sid for sid, ws in cls._connections.items() if ws is websocket), None)

        if not session_id:
            logger.warning("Received register_tools from unknown connection")
            return

        await registry.register_tools_for_session(session_id, payload.tools)
        logger.info(
            f"Registered {len(payload.tools)} tools for session {session_id}")

        # Sync server-level FastMCP visibility so new MCP client sessions
        # (e.g. new Claude Code conversations) see the correct tool set.
        self._sync_server_tool_visibility(payload.tools)

        # Notify any already-connected MCP clients (e.g. CC over stdio) that
        # the tool list has changed so they re-fetch.
        await cls._notify_mcp_tool_list_changed()

        try:
            from services.custom_tool_service import CustomToolService

            service = CustomToolService.get_instance()
            service.register_global_tools(payload.tools)
        except RuntimeError as exc:
            logger.debug(
                "Skipping global custom tool registration: CustomToolService not initialized yet (%s)",
                exc,
            )
        except Exception as exc:
            logger.warning(
                "Unexpected error during global custom tool registration; "
                "custom tools may not be available globally",
                exc_info=exc,
            )

    async def _handle_lifecycle(
        self, websocket: WebSocket, payload: PluginLifecycleMessage
    ) -> None:
        """Record an intentional reload before the plugin closes its socket."""

        cls = type(self)
        registry = cls._registry
        lock = cls._lock
        if registry is None or lock is None:
            return

        async with lock:
            session_id = next(
                (sid for sid, ws in cls._connections.items() if ws is websocket),
                None,
            )
        if session_id is None or (
            payload.session_id is not None and payload.session_id != session_id
        ):
            logger.warning("Received lifecycle signal from unknown plugin connection")
            return

        lifecycle = await registry.mark_reloading(session_id, payload.reason)
        if lifecycle is not None:
            logger.info(
                "Plugin session %s announced %s (%s)",
                session_id,
                payload.state,
                payload.reason or "no reason",
            )

    @classmethod
    def _sync_server_tool_visibility(cls, registered_tools: list) -> None:
        """Sync FastMCP server-level tool group visibility to match Unity's state.

        When Unity sends ``register_tools``, some groups may have been toggled
        on/off via the Unity Editor GUI.  We mirror that state at the FastMCP
        server level so that **new** MCP client sessions (e.g. a fresh Claude
        Code conversation) see the correct tool set without requiring
        ``manage_tools`` activation.

        The startup ``register_all_tools()`` disables non-default groups via
        ``mcp.disable(tags=...)``.  Here we append ``mcp.enable(tags=...)``
        transforms for groups that Unity has enabled, effectively overriding
        the startup defaults.  FastMCP processes transforms in order so later
        ``enable`` calls override earlier ``disable`` calls.
        """
        mcp = cls._mcp
        if mcp is None:
            return

        try:
            from services.registry import get_group_tool_names, TOOL_GROUPS

            registered_names: set[str] = set()
            for tool in registered_tools:
                name = getattr(tool, "name", None) if not isinstance(tool, dict) else tool.get("name")
                if isinstance(name, str) and name:
                    registered_names.add(name)

            group_tools = get_group_tool_names()

            # Reset Unity overrides: trim transforms back to where Unity started,
            # then re-apply based on current registered tools.
            if cls._unity_transform_start is not None:
                mcp._transforms = mcp._transforms[:cls._unity_transform_start]
            else:
                # First time: record where startup transforms end.
                cls._unity_transform_start = len(mcp._transforms)

            enabled_groups: list[str] = []
            disabled_groups: list[str] = []

            for group_name in sorted(TOOL_GROUPS.keys()):
                tool_names = group_tools.get(group_name, [])
                has_any_registered = any(n in registered_names for n in tool_names)

                if has_any_registered:
                    # Override the startup disable with an enable.
                    tag = f"group:{group_name}"
                    mcp.enable(tags={tag}, components={"tool"})
                    enabled_groups.append(group_name)
                else:
                    # Group not present in Unity's registered tools — disable it.
                    tag = f"group:{group_name}"
                    mcp.disable(tags={tag}, components={"tool"})
                    disabled_groups.append(group_name)

            if enabled_groups or disabled_groups:
                logger.info(
                    "Server-level tool visibility synced from Unity: "
                    "enabled=[%s], disabled=[%s], total_transforms=%d, unity_start=%d",
                    ", ".join(enabled_groups),
                    ", ".join(disabled_groups),
                    len(mcp._transforms),
                    cls._unity_transform_start or 0,
                )
        except Exception:
            logger.debug(
                "Failed to sync server-level tool visibility",
                exc_info=True,
            )

    @classmethod
    async def _notify_mcp_tool_list_changed(cls) -> None:
        """Send ``tools/list_changed`` to every connected MCP client session.

        After server-level tool visibility is updated (e.g. when Unity reports
        its registered tools), existing MCP clients (especially stdio-based
        ones like Claude Code) must be told to re-fetch the tool list.
        FastMCP's ``mcp.enable()``/``mcp.disable()`` update the server-level
        transforms but do **not** push notifications to already-connected
        sessions — we do that here.
        """
        sessions = list(_active_mcp_sessions)
        if not sessions:
            return
        for session in sessions:
            try:
                await session.send_tool_list_changed()
            except Exception:
                logger.debug(
                    "Failed to notify MCP session of tool list change",
                    exc_info=True,
                )
        logger.info(
            "Sent tools/list_changed notification to %d MCP session(s)",
            len(sessions),
        )

    async def _handle_command_result(self, payload: CommandResultMessage) -> None:
        cls = type(self)
        lock = cls._lock
        if lock is None:
            return
        command_id = payload.id
        result = payload.result

        if not command_id:
            logger.warning(f"Command result missing id: {payload}")
            return

        async with lock:
            entry = cls._pending.get(command_id)
        future = entry.get("future") if isinstance(entry, dict) else None
        if future and not future.done():
            future.set_result(result)

    async def _handle_control_result(self, payload: dict[str, Any]) -> None:
        cls = type(self)
        lock = cls._lock
        if lock is None:
            return

        control_id = payload.get("id")
        if not control_id:
            logger.warning("Runtime control result missing id: %s", payload)
            return

        async with lock:
            entry = cls._pending.get(control_id)
        future = entry.get("future") if isinstance(entry, dict) else None
        if future and not future.done():
            future.set_result(payload)

    async def _handle_pong(self, payload: PongMessage) -> None:
        cls = type(self)
        registry = cls._registry
        lock = cls._lock
        if registry is None:
            return
        session_id = payload.session_id
        if session_id:
            await registry.touch(session_id)
            # Record last pong time for staleness detection (under lock for consistency)
            if lock is not None:
                async with lock:
                    cls._last_pong[session_id] = time.monotonic()

    @classmethod
    async def _ping_loop(cls, session_id: str, websocket: WebSocket) -> None:
        """Server-initiated ping loop to detect dead connections.

        Sends periodic pings to the Unity client. If no pong is received within
        PING_TIMEOUT seconds, the connection is considered dead and closed.
        This helps detect connections that die silently (e.g., Windows OSError 64).
        """
        logger.debug(f"[Ping] Starting ping loop for session {session_id}")
        try:
            while True:
                await asyncio.sleep(cls.PING_INTERVAL)

                # Check if we're still supposed to be running and get last pong time (under lock)
                lock = cls._lock
                if lock is None:
                    break
                async with lock:
                    if session_id not in cls._connections:
                        logger.debug(f"[Ping] Session {session_id} no longer in connections, stopping ping loop")
                        break
                    # Read last pong time under lock for consistency
                    last_pong = cls._last_pong.get(session_id, 0)

                # Check staleness: has it been too long since we got a pong?
                elapsed = time.monotonic() - last_pong
                if elapsed > cls.PING_TIMEOUT:
                    logger.warning(
                        f"[Ping] Session {session_id} stale: no pong for {elapsed:.1f}s "
                        f"(timeout={cls.PING_TIMEOUT}s). Closing connection."
                    )
                    try:
                        await websocket.close(code=1001)  # Going away
                    except Exception as close_ex:
                        logger.debug(f"[Ping] Error closing stale websocket: {close_ex}")
                    break

                # Send a ping to the client
                try:
                    ping_msg = PingMessage()
                    await websocket.send_json(ping_msg.model_dump())
                    logger.debug(f"[Ping] Sent ping to session {session_id}")
                except Exception as send_ex:
                    # Send failed - connection is dead
                    logger.warning(
                        f"[Ping] Failed to send ping to session {session_id}: {send_ex}. "
                        "Connection likely dead."
                    )
                    try:
                        await websocket.close(code=1006)  # Abnormal closure
                    except Exception:
                        pass
                    break

        except asyncio.CancelledError:
            logger.debug(f"[Ping] Ping loop cancelled for session {session_id}")
        except Exception as ex:
            logger.warning(f"[Ping] Ping loop error for session {session_id}: {ex}")
        finally:
            logger.debug(f"[Ping] Ping loop ended for session {session_id}")

    @classmethod
    async def _get_connection(cls, session_id: str) -> WebSocket:
        lock = cls._lock
        if lock is None:
            raise RuntimeError("PluginHub not configured")
        async with lock:
            websocket = cls._connections.get(session_id)
        if websocket is None:
            raise RuntimeError(f"Plugin session {session_id} not connected")
        return websocket

    @classmethod
    async def _evict_connection(cls, session_id: str, reason: str) -> None:
        """Drop a stale session from in-memory maps and registry."""
        lock = cls._lock
        if lock is None:
            return

        websocket: WebSocket | None = None
        ping_task: asyncio.Task | None = None
        pending_futures: list[asyncio.Future] = []
        async with lock:
            websocket = cls._connections.pop(session_id, None)
            ping_task = cls._ping_tasks.pop(session_id, None)
            cls._last_pong.pop(session_id, None)
            keys_to_remove: list[object] = []
            for key, entry in list(cls._pending.items()):
                if entry.get("session_id") == session_id:
                    future = entry.get("future")
                    if future and not future.done():
                        pending_futures.append(future)
                    keys_to_remove.append(key)
            for key in keys_to_remove:
                cls._pending.pop(key, None)

        if ping_task is not None and not ping_task.done():
            ping_task.cancel()

        for future in pending_futures:
            if not future.done():
                future.set_exception(
                    PluginDisconnectedError(
                        f"Unity plugin session {session_id} disconnected while awaiting command_result"
                    )
                )

        if websocket is not None:
            try:
                await websocket.close(code=1001)
            except Exception as close_ex:
                logger.debug("Error closing evicted WebSocket for session %s: %s", session_id, close_ex)

        if cls._registry is not None:
            try:
                await cls._registry.unregister(session_id)
            except Exception:
                logger.debug(
                    "Failed to unregister evicted plugin session %s",
                    session_id,
                    exc_info=True,
                )

        logger.debug("Evicted plugin session %s (%s)", session_id, reason)

    @classmethod
    async def _ensure_live_connection(cls, session_id: str) -> bool:
        """Best-effort pre-send liveness check for a plugin WebSocket."""
        try:
            websocket = await cls._get_connection(session_id)
        except RuntimeError:
            await cls._evict_connection(session_id, "missing_websocket")
            return False

        if (
            websocket.client_state == WebSocketState.CONNECTED
            and websocket.application_state == WebSocketState.CONNECTED
        ):
            return True

        logger.debug(
            "Detected stale plugin connection before send: session=%s app_state=%s client_state=%s",
            session_id,
            websocket.application_state,
            websocket.client_state,
        )
        await cls._evict_connection(session_id, "stale_websocket_state")
        return False

    @staticmethod
    def _unavailable_retry_response(reason: str = "no_unity_session") -> dict[str, Any]:
        return MCPResponse(
            success=False,
            error="Unity session not available; please retry",
            hint="retry",
            data={"reason": reason, "retry_after_ms": 250},
        ).model_dump()

    # ------------------------------------------------------------------
    # Session resolution helpers
    # ------------------------------------------------------------------
    @classmethod
    async def _resolve_session_id(
        cls,
        unity_instance: str | None,
        user_id: str | None = None,
        retry_on_reload: bool = True,
    ) -> str:
        """Resolve a project hash (Unity instance id) to an active plugin session.

        During Unity domain reloads the plugin's WebSocket session is torn down
        and reconnected shortly afterwards. Instead of failing immediately when
        no sessions are available, we wait for a bounded period for a plugin
        to reconnect so in-flight MCP calls can succeed transparently.

        Args:
            unity_instance: Target instance (Name@hash or hash)
            user_id: User ID from API key validation (for remote-hosted mode session isolation)
            retry_on_reload: If False, do not wait for reconnects when no session is present.
        """
        if cls._registry is None:
            raise RuntimeError("Plugin registry not configured")

        # Reload-aware plugins explicitly announce an intentional domain reload.
        # Their reconnect is awaited through the registry condition; a disconnect
        # without that signal fails immediately. Legacy peers retain the bounded
        # wait below for compatibility.
        #
        # Configurable via: UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S (default: 20.0, max: 120.0).
        # The ceiling used to equal the default, which silently neutered the override for
        # projects whose reloads/test boundaries legitimately exceed 20s (#1207).
        max_wait_s = _read_bounded_wait_env(
            "UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S", default_s=20.0, max_s=120.0)
        if not retry_on_reload:
            max_wait_s = 0.0
        # Allow callers to provide either just the hash or Name@hash
        target_hash: str | None = None
        if unity_instance:
            if "@" in unity_instance:
                _, _, suffix = unity_instance.rpartition("@")
                target_hash = suffix or None
            else:
                target_hash = unity_instance

        async def _try_once() -> tuple[str | None, int, bool]:
            explicit_required = config.http_remote_hosted
            # Prefer a specific Unity instance if one was requested
            if target_hash:
                # In remote-hosted mode with user_id, use user-scoped lookup
                if config.http_remote_hosted and user_id:
                    session_id = await cls._registry.get_session_id_by_hash(target_hash, user_id)
                    sessions = await cls._registry.list_sessions(user_id=user_id)
                else:
                    session_id = await cls._registry.get_session_id_by_hash(target_hash)
                    sessions = await cls._registry.list_sessions(user_id=user_id)
                return session_id, len(sessions), explicit_required

            # No target provided: determine if we can auto-select
            # In remote-hosted mode, filter sessions by user_id
            sessions = await cls._registry.list_sessions(user_id=user_id)
            count = len(sessions)
            if count == 0:
                return None, count, explicit_required
            if explicit_required:
                return None, count, explicit_required
            if count == 1:
                return next(iter(sessions.keys())), count, explicit_required
            # Multiple sessions but no explicit target is ambiguous
            return None, count, explicit_required

        async def _available_instance_ids() -> list[str]:
            # Error path only; one extra registry read keeps the refusal actionable.
            try:
                sessions = await cls._registry.list_sessions(user_id=user_id)
                return sorted(
                    f"{s.project_name}@{s.project_hash}" for s in sessions.values())
            except Exception:
                return []

        session_id, session_count, explicit_required = await _try_once()
        if session_id is None and explicit_required and not target_hash and session_count > 0:
            raise InstanceSelectionRequiredError(
                available_instances=await _available_instance_ids())
        unavailable_reason = "no_unity_session"
        if session_id is None and target_hash and retry_on_reload:
            lifecycle = await cls._registry.get_lifecycle(target_hash, user_id)
            if lifecycle and lifecycle.supports_reload_lifecycle:
                if lifecycle.state == "reloading":
                    unavailable_reason = "reload_timeout"
                    reload_age_s = max(
                        0.0,
                        (datetime.now(timezone.utc) - lifecycle.updated_at).total_seconds(),
                    )
                    max_wait_s = max(0.0, max_wait_s - reload_age_s)
                else:
                    # A reload-aware plugin did not announce a reload, so this is an
                    # editor/network disconnect and waiting cannot improve the result.
                    max_wait_s = 0.0
                    unavailable_reason = "unity_disconnected"

        wait_started = time.monotonic() if session_id is None and max_wait_s > 0 else None
        if wait_started is not None:
            logger.debug(
                "No plugin session available (instance=%s); waiting up to %.2fs",
                unity_instance or "default",
                max_wait_s,
            )
            if target_hash:
                await cls._registry.wait_for_session_id_by_hash(
                    target_hash,
                    user_id=user_id,
                    timeout=max_wait_s,
                )
            else:
                await cls._registry.wait_for_available_session(
                    user_id=user_id,
                    timeout=max_wait_s,
                )
            session_id, session_count, explicit_required = await _try_once()

        if session_id is not None and wait_started is not None:
            logger.debug(
                "Plugin session restored after %.3fs (instance=%s)",
                time.monotonic() - wait_started,
                unity_instance or "default",
            )
        if session_id is None and not target_hash and session_count > 1:
            raise InstanceSelectionRequiredError(
                InstanceSelectionRequiredError._MULTIPLE_INSTANCES,
                available_instances=await _available_instance_ids())

        if session_id is None and explicit_required and not target_hash and session_count > 0:
            raise InstanceSelectionRequiredError()

        if session_id is None:
            logger.warning(
                "No Unity plugin reconnected within %.2fs (instance=%s)",
                max_wait_s,
                unity_instance or "default",
            )
            # At this point we've given the plugin ample time to reconnect; surface
            # a clear error so the client can prompt the user to open Unity.
            raise NoUnitySessionError(
                "No Unity plugins are currently connected",
                reason=unavailable_reason,
            )

        return session_id

    @classmethod
    async def send_command_for_instance(
        cls,
        unity_instance: str | None,
        command_type: str,
        params: dict[str, Any],
        user_id: str | None = None,
        retry_on_reload: bool = True,
    ) -> dict[str, Any]:
        """Send a command to a Unity instance.

        Args:
            unity_instance: Target instance (Name@hash or hash)
            command_type: Command type to execute
            params: Command parameters
            user_id: User ID for session isolation in remote-hosted mode
            retry_on_reload: If False, do not wait for session reconnect on reload.
        """
        logical_request_id = str(uuid.uuid4())

        try:
            session_id = await cls._resolve_session_id(
                unity_instance,
                user_id=user_id,
                retry_on_reload=retry_on_reload,
            )
        except NoUnitySessionError as exc:
            logger.debug(
                "Unity session unavailable; returning retry: command=%s instance=%s",
                command_type,
                unity_instance or "default",
            )
            return cls._unavailable_retry_response(exc.reason)

        if not await cls._ensure_live_connection(session_id):
            if not retry_on_reload:
                return cls._unavailable_retry_response("stale_connection")
            try:
                session_id = await cls._resolve_session_id(
                    unity_instance,
                    user_id=user_id,
                    retry_on_reload=True,
                )
            except NoUnitySessionError:
                return cls._unavailable_retry_response("no_unity_session")
            if not await cls._ensure_live_connection(session_id):
                return cls._unavailable_retry_response("stale_connection")

        # During domain reload / immediate reconnect windows, the plugin may be connected but not yet
        # ready to process execute commands on the Unity main thread (which can be further delayed when
        # the Unity Editor is unfocused). For fast-path commands, we do a bounded readiness probe using
        # a main-thread ping command (handled by TransportCommandDispatcher) rather than waiting on
        # register_tools (which can be delayed by EditorApplication.delayCall).
        if retry_on_reload and command_type in cls._FAST_FAIL_COMMANDS and command_type != "ping":
            max_wait_s = _read_bounded_wait_env(
                "UNITY_MCP_SESSION_READY_WAIT_SECONDS", default_s=10.0, max_s=120.0)
            if max_wait_s > 0:
                deadline = time.monotonic() + max_wait_s
                while time.monotonic() < deadline:
                    try:
                        if not await cls._ensure_live_connection(session_id):
                            session_id = await cls._resolve_session_id(
                                unity_instance,
                                user_id=user_id,
                                retry_on_reload=False,
                            )
                        probe = await cls.send_command(session_id, "ping", {})
                    except Exception:
                        probe = None

                    if cls._is_successful_ping_response(probe):
                        break
                    await asyncio.sleep(0.1)
                else:
                    # Not ready within the bounded window: return retry hint without sending.
                    return MCPResponse(
                        success=False,
                        error=f"Unity session not ready for '{command_type}' (ping not answered); please retry",
                        hint="retry",
                    ).model_dump()

        result = await cls.send_command(
            session_id,
            command_type,
            params,
            request_id=logical_request_id,
            attempt=1,
        )
        if not retry_on_reload or not cls._requires_receipt_recovery(result):
            return result

        # The command may have crossed the WebSocket boundary before a domain
        # reload disconnected Unity. Reconnect and resend the same logical
        # request as attempt 2: Runtime v1's authoritative receipt ledger will
        # return the cached terminal result, an in-progress state, or an explicit
        # outcome_unknown without executing the mutation twice.
        try:
            recovered_session_id = await cls._resolve_session_id(
                unity_instance,
                user_id=user_id,
                retry_on_reload=True,
            )
        except NoUnitySessionError:
            return result
        if not await cls._ensure_live_connection(recovered_session_id):
            return result

        return await cls.send_command(
            recovered_session_id,
            command_type,
            params,
            request_id=logical_request_id,
            attempt=2,
        )

    @staticmethod
    def _requires_receipt_recovery(result: Any) -> bool:
        return (
            isinstance(result, dict)
            and result.get("success") is False
            and result.get("code") == "PLUGIN_DISCONNECTED"
        )

    @staticmethod
    def _is_successful_ping_response(probe: Any) -> bool:
        """Accept both legacy and Runtime v1 pong response envelopes."""
        if not isinstance(probe, dict) or probe.get("success") is False:
            return False

        status = str(probe.get("status") or "").lower()
        if status not in {"success", "succeeded"} and probe.get("success") is not True:
            return False

        messages = [probe.get("message")]
        for field in ("result", "data"):
            nested = probe.get(field)
            if isinstance(nested, dict):
                messages.append(nested.get("message"))
        return any(message == "pong" for message in messages)

    # ------------------------------------------------------------------
    # Blocking helpers for synchronous tool code
    # ------------------------------------------------------------------
    @classmethod
    def _run_coroutine_sync(cls, coro: "asyncio.Future[Any]") -> Any:
        if cls._loop is None:
            raise RuntimeError("PluginHub event loop not configured")
        loop = cls._loop
        if loop.is_running():
            try:
                running_loop = asyncio.get_running_loop()
            except RuntimeError:
                running_loop = None
            else:
                if running_loop is loop:
                    raise RuntimeError(
                        "Cannot wait synchronously for PluginHub coroutine from within the event loop"
                    )
        future = asyncio.run_coroutine_threadsafe(coro, loop)
        return future.result()

    @classmethod
    def send_command_blocking(
        cls,
        unity_instance: str | None,
        command_type: str,
        params: dict[str, Any],
    ) -> dict[str, Any]:
        return cls._run_coroutine_sync(
            cls.send_command_for_instance(unity_instance, command_type, params)
        )

    @classmethod
    def list_sessions_sync(cls) -> SessionList:
        return cls._run_coroutine_sync(cls.get_sessions())


def send_command_to_plugin(
    *,
    unity_instance: str | None,
    command_type: str,
    params: dict[str, Any],
) -> dict[str, Any]:
    return PluginHub.send_command_blocking(unity_instance, command_type, params)
