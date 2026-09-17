from __future__ import annotations

import asyncio
import logging
import os
import time
from collections.abc import Awaitable, Callable
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from models import MCPResponse
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
import transport.unity_transport as unity_transport
import transport.legacy.unity_connection as _legacy_conn
from transport.legacy.unity_connection import _extract_response_reason
from services.state.external_changes_scanner import external_changes_scanner
from services.state.external_asset_guard import guard_external_yaml_refresh
import services.resources.editor_state as editor_state

logger = logging.getLogger(__name__)

# Blocking reasons that indicate Unity is actually busy (not just stale status).
# Must match activityPhase values from EditorStateCache.cs
_REAL_BLOCKING_REASONS = {
    "compiling",
    "domain_reload",
    "running_tests",
    "asset_import",
    "asset_refresh",
}


def _in_pytest() -> bool:
    """Return True when running inside pytest to avoid polling unmocked resources."""
    return "PYTEST_CURRENT_TEST" in os.environ


def _state_advanced(current: dict[str, Any], baseline: dict[str, Any]) -> bool:
    current_sequence = current.get("sequence")
    baseline_sequence = baseline.get("sequence")
    if (isinstance(current_sequence, int)
            and isinstance(baseline_sequence, int)
            and current_sequence != baseline_sequence):
        return True

    current_observed = current.get("observed_at_unix_ms")
    baseline_observed = baseline.get("observed_at_unix_ms")
    return (
        not isinstance(current_sequence, int)
        and isinstance(current_observed, int)
        and isinstance(baseline_observed, int)
        and current_observed > baseline_observed
    )


def _compilation_cycle_advanced(current: dict[str, Any], baseline: dict[str, Any]) -> bool:
    current_sequence = current.get("sequence")
    baseline_sequence = baseline.get("sequence")
    if (isinstance(current_sequence, int)
            and isinstance(baseline_sequence, int)
            and current_sequence < baseline_sequence):
        return True

    current_compilation = current.get("compilation") or {}
    baseline_compilation = baseline.get("compilation") or {}
    for field in (
        "last_compile_started_unix_ms",
        "last_compile_finished_unix_ms",
        "last_domain_reload_before_unix_ms",
        "last_domain_reload_after_unix_ms",
    ):
        current_value = current_compilation.get(field)
        if current_value is not None and current_value != baseline_compilation.get(field):
            return True
    return False


async def _wait_for_editor_ready_state(
    ctx: Context,
    timeout_s: float = 30.0,
    *,
    after_state: dict[str, Any] | None = None,
    require_compilation_cycle: bool = False,
) -> tuple[bool, float, dict[str, Any] | None]:
    """Poll editor_state until Unity is ready for tool calls.

    Returns (ready, elapsed_seconds, last_state). When after_state is supplied,
    an old cached ready snapshot is not accepted. Treats exceptions from
    get_editor_state as "not ready yet" so the loop survives transient
    connection errors during domain reload. The final state is retained so the
    caller can report the observation that actually fulfilled the wait.
    """
    if _in_pytest():
        return (True, 0.0, None)

    start = time.monotonic()
    saw_blocking_state = False
    last_state: dict[str, Any] | None = None
    while time.monotonic() - start < timeout_s:
        try:
            state_resp = await editor_state.get_editor_state(ctx)
            state = state_resp.model_dump() if hasattr(state_resp, "model_dump") else state_resp
            data = (state or {}).get("data") if isinstance(state, dict) else None
            if isinstance(data, dict):
                last_state = data
            advice = (data or {}).get("advice") if isinstance(data, dict) else None
            if isinstance(advice, dict):
                blocking = set(advice.get("blocking_reasons") or [])
                if blocking & _REAL_BLOCKING_REASONS:
                    saw_blocking_state = True

                state_advanced = (
                    after_state is None
                    or _state_advanced(data, after_state)
                    or (
                        after_state is not None
                        and _compilation_cycle_advanced(data, after_state)
                    )
                )
                compilation_advanced = (
                    not require_compilation_cycle
                    or saw_blocking_state
                    or (
                        after_state is not None
                        and _compilation_cycle_advanced(data, after_state)
                    )
                )
                ready = (
                    advice.get("ready_for_tools") is True
                    or not (blocking & _REAL_BLOCKING_REASONS)
                )
                if ready and state_advanced and compilation_advanced:
                    return (True, time.monotonic() - start, data)
        except Exception:
            pass  # not ready yet — keep polling
        await asyncio.sleep(0.25)

    return (False, time.monotonic() - start, last_state)


async def wait_for_editor_ready(
    ctx: Context,
    timeout_s: float = 30.0,
    *,
    after_state: dict[str, Any] | None = None,
    require_compilation_cycle: bool = False,
) -> tuple[bool, float]:
    """Poll editor_state until ready while preserving the public tuple contract."""
    ready, elapsed, _ = await _wait_for_editor_ready_state(
        ctx,
        timeout_s,
        after_state=after_state,
        require_compilation_cycle=require_compilation_cycle,
    )
    return (ready, elapsed)


def _editor_state_result(state: dict[str, Any] | None) -> tuple[str, list[str]]:
    """Extract a concise final state and blocking reasons for tool responses."""
    if not isinstance(state, dict):
        return ("unknown", [])

    activity = state.get("activity")
    phase = activity.get("phase") if isinstance(activity, dict) else None
    advice = state.get("advice")
    blocking = advice.get("blocking_reasons") if isinstance(advice, dict) else None
    return (
        phase if isinstance(phase, str) and phase else "unknown",
        list(blocking) if isinstance(blocking, list) else [],
    )


def is_reloading_rejection(resp: Any) -> bool:
    """True when Unity rejected a command because it thinks it is reloading.

    The command was never executed, so retrying is safe.
    """
    if not isinstance(resp, dict) or resp.get("success"):
        return False
    data = resp.get("data") or {}
    return data.get("reason") == "reloading" and resp.get("hint") == "retry"


def is_connection_lost_after_send(resp: Any) -> bool:
    """True when a mutation's response indicates TCP was lost after command was sent.

    Script mutations trigger domain reload which kills the TCP connection.
    The mutation was likely executed but the response was lost.
    """
    if isinstance(resp, dict):
        if resp.get("success"):
            return False
        err = (resp.get("error") or resp.get("message") or "").lower()
    else:
        if getattr(resp, "success", None):
            return False
        err = (getattr(resp, "error", "") or "").lower()
    return "connection closed" in err or "disconnected" in err or "aborted" in err


async def send_mutation(
    ctx: Context,
    unity_instance: str | None,
    command: str,
    params: dict[str, Any],
    *,
    verify_after_disconnect: Callable[[], Awaitable[dict | None]] | None = None,
) -> dict | Any:
    """Send a non-idempotent mutation with reload recovery.

    Handles the full retry/recovery pattern for script mutations:
    1. Send with retry_on_reload=False (don't re-send if Unity is reloading)
    2. If reloading rejection (command never executed) → wait + retry once
    3. If connection lost after send → wait + verify via callback
    4. Wait for editor readiness before returning

    Args:
        verify_after_disconnect: async callable returning a replacement response
            dict if the mutation was verified after connection loss, or None to
            keep the original error response.
    """
    resp = await unity_transport.send_with_unity_instance(
        _legacy_conn.async_send_command_with_retry,
        unity_instance,
        command,
        params,
        retry_on_reload=False,
    )
    if is_reloading_rejection(resp):
        await wait_for_editor_ready(ctx)
        resp = await unity_transport.send_with_unity_instance(
            _legacy_conn.async_send_command_with_retry,
            unity_instance,
            command,
            params,
            retry_on_reload=False,
        )
    if is_connection_lost_after_send(resp) and verify_after_disconnect:
        await wait_for_editor_ready(ctx)
        verified = await verify_after_disconnect()
        if verified is not None:
            resp = verified
    await wait_for_editor_ready(ctx)
    return resp


async def verify_edit_by_sha(
    unity_instance: str | None,
    name: str,
    path: str,
    pre_sha: str | None,
) -> bool:
    """Verify a script edit was applied by comparing SHA before and after.

    Returns True if the file's SHA changed (edit likely applied).
    """
    if not pre_sha:
        return False
    try:
        verify = await unity_transport.send_with_unity_instance(
            _legacy_conn.async_send_command_with_retry,
            unity_instance,
            "manage_script",
            {"action": "get_sha", "name": name, "path": path},
        )
        if isinstance(verify, dict) and verify.get("success"):
            new_sha = (verify.get("data") or {}).get("sha256")
            return bool(new_sha and new_sha != pre_sha)
    except Exception as exc:
        logger.debug(
            "Failed to verify edit after disconnect for %s at %s: %r",
            name, path, exc,
        )
    return False


@mcp_for_unity_tool(
    description="Request a Unity asset database refresh and optionally a script compilation. Can optionally wait for readiness.",
    annotations=ToolAnnotations(
        title="Refresh Unity",
        destructiveHint=True,
    ),
)
async def refresh_unity(
    ctx: Context,
    mode: Annotated[Literal["if_dirty", "force"], "Refresh mode"] = "if_dirty",
    scope: Annotated[Literal["assets", "scripts", "all"],
                     "Refresh scope"] = "all",
    compile: Annotated[Literal["none", "request"],
                       "Whether to request compilation"] = "none",
    wait_for_ready: Annotated[bool,
                              "If true, wait until editor_state.advice.ready_for_tools is true"] = True,
) -> MCPResponse | dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    pre_refresh_state: dict[str, Any] | None = None
    if not _in_pytest():
        try:
            state_response = await editor_state.get_editor_state(ctx)
            state_payload = (
                state_response.model_dump()
                if hasattr(state_response, "model_dump")
                else state_response
            )
            candidate = (
                state_payload.get("data")
                if isinstance(state_payload, dict)
                else None
            )
            if isinstance(candidate, dict):
                pre_refresh_state = candidate
        except Exception:
            pass

        external_state: dict[str, Any] = {}
        if isinstance(pre_refresh_state, dict):
            assets = pre_refresh_state.get("assets")
            if isinstance(assets, dict):
                external_state = dict(assets)

        instance_id = unity_instance
        if not instance_id and isinstance(pre_refresh_state, dict):
            unity_data = pre_refresh_state.get("unity")
            if isinstance(unity_data, dict):
                candidate_id = unity_data.get("instance_id")
                if isinstance(candidate_id, str) and candidate_id:
                    instance_id = candidate_id
        if not instance_id:
            try:
                instance_id = await editor_state.infer_single_instance_id(ctx)
            except Exception:
                instance_id = None

        if instance_id:
            try:
                external_state = external_changes_scanner.update_and_get(
                    instance_id,
                    force=True,
                )
            except Exception:
                logger.exception(
                    "refresh_unity: Failed to force-scan external changes"
                )

        try:
            yaml_guard = await guard_external_yaml_refresh(
                ctx,
                unity_instance,
                external_state,
                editor_state_data=pre_refresh_state,
            )
            if yaml_guard is not None:
                return yaml_guard
        except Exception:
            changed_paths = external_state.get("external_changed_paths")
            known_yaml_paths = [
                item for item in changed_paths
                if isinstance(item, dict)
                and item.get("is_unity_yaml") is True
            ] if isinstance(changed_paths, list) else []
            if known_yaml_paths:
                logger.exception(
                    "refresh_unity: Failed to evaluate external YAML guard"
                )
                return MCPResponse(
                    success=False,
                    error="external_asset_context_unavailable",
                    code="external_asset_context_unavailable",
                    message=(
                        "Refresh blocked because externally changed Unity "
                        "YAML was detected, but its loaded context could not "
                        "be verified."
                    ),
                    data={
                        "reason": "external_yaml_context_unavailable",
                        "refresh_not_sent": True,
                        "safe_to_retry_refresh": False,
                        "recommended_next_action": (
                            "read_editor_state_and_asset_context"
                        ),
                        "changed_yaml_paths": known_yaml_paths,
                        "context_errors": ["guard_evaluation_failed"],
                    },
                )

    baseline_state: dict[str, Any] | None = None
    if wait_for_ready and compile == "request":
        baseline_state = pre_refresh_state

    params: dict[str, Any] = {
        "mode": mode,
        "scope": scope,
        "compile": compile,
        "wait_for_ready": bool(wait_for_ready),
    }

    recovered_from_disconnect = False
    # Don't retry on reload - refresh_unity triggers compilation/reload,
    # so retrying would cause multiple reloads (issue #577)
    response = await unity_transport.send_with_unity_instance(
        _legacy_conn.async_send_command_with_retry,
        unity_instance,
        "refresh_unity",
        params,
        retry_on_reload=False,
    )

    # Handle connection errors during refresh/compile gracefully.
    # Unity disconnects during domain reload, which is expected behavior - not a failure.
    # If we sent the command and connection closed, the refresh was likely triggered successfully.
    # Convert MCPResponse to dict if needed
    response_dict = response if isinstance(response, dict) else (response.model_dump() if hasattr(response, "model_dump") else response.__dict__)
    if not response_dict.get("success", True):
        hint = response_dict.get("hint")
        err = (response_dict.get("error") or response_dict.get("message") or "").lower()
        reason = _extract_response_reason(response_dict)

        # Connection closed/timeout during compile = refresh was triggered, Unity is reloading
        # This is SUCCESS, not failure - don't return error to prevent Claude Code from retrying
        is_connection_lost = (
            "connection closed" in err
            or "disconnected" in err
            or "aborted" in err  # WinError 10053: connection aborted
            or "timeout" in err
            or reason == "reloading"
        )

        if is_connection_lost and compile == "request":
            # EXPECTED BEHAVIOR: When compile="request", Unity triggers domain reload which
            # causes connection to close mid-command. This is NOT a failure - the refresh
            # was successfully triggered. Treating this as success prevents Claude Code from
            # retrying unnecessarily (which would cause multiple domain reloads - issue #577).
            # The subsequent wait_for_ready loop (below) will verify Unity becomes ready.
            logger.info("refresh_unity: Connection lost during compile (expected - domain reload triggered)")
            recovered_from_disconnect = True
        elif hint == "retry" or "could not connect" in err:
            # Retryable error - proceed to wait loop if wait_for_ready
            if not wait_for_ready:
                return MCPResponse(**response_dict)
            recovered_from_disconnect = True
        else:
            # Non-recoverable error - connection issue unrelated to domain reload
            logger.warning(f"refresh_unity: Non-recoverable error (compile={compile}): {err[:100]}")
            return MCPResponse(**response_dict)

    # Optional server-side wait loop (defensive): if Unity tool doesn't wait or returns quickly,
    # poll the canonical editor_state resource until ready or timeout.
    ready_confirmed = False
    wait_elapsed_s = 0.0
    final_state: dict[str, Any] | None = None
    if wait_for_ready:
        ready_confirmed, wait_elapsed_s, final_state = await _wait_for_editor_ready_state(
            ctx,
            timeout_s=60.0,
            after_state=baseline_state,
            require_compilation_cycle=(
                compile == "request" and baseline_state is not None
            ),
        )

        # If we timed out without confirming readiness, log and return failure
        if not ready_confirmed:
            logger.warning("refresh_unity: Timed out after 60s waiting for editor to become ready")
            resulting_state, blocking_reasons = _editor_state_result(final_state)
            timeout_data = (
                dict(response_dict.get("data"))
                if isinstance(response_dict.get("data"), dict)
                else {}
            )
            timeout_data.update({
                "timeout": True,
                "wait_seconds": round(wait_elapsed_s, 3),
                "wait_for_ready": True,
                "ready_for_tools": False,
                "resulting_state": resulting_state,
                "blocking_reasons": blocking_reasons,
                "editor_state_sequence": (
                    final_state.get("sequence")
                    if isinstance(final_state, dict)
                    else None
                ),
                "refresh_may_have_completed": True,
                "safe_to_retry_refresh": False,
                "recommended_next_action": "read_editor_state",
                "hint": "Read editor_state; do not automatically repeat refresh.",
            })
            return MCPResponse(
                success=False,
                error="editor_readiness_timeout",
                code="editor_readiness_timeout",
                message=(
                    "Refresh was triggered, but editor readiness was not "
                    "confirmed within 60 seconds. Read editor_state before "
                    "deciding whether another refresh is needed."
                ),
                data=timeout_data,
            )

    # After readiness is restored, clear any external-dirty flag for this instance so future tools can proceed cleanly.
    try:
        inst = unity_instance or await editor_state.infer_single_instance_id(ctx)
        if inst:
            external_changes_scanner.clear_dirty(inst)
    except Exception:
        pass

    if wait_for_ready:
        resulting_state, blocking_reasons = _editor_state_result(final_state)
        if resulting_state == "unknown":
            # Tests and legacy transports may not provide a snapshot. The wait
            # result itself still establishes the readiness contract.
            resulting_state = "ready"
        ready_data = (
            dict(response_dict.get("data"))
            if isinstance(response_dict.get("data"), dict)
            else {}
        )
        ready_data.update({
            "resulting_state": resulting_state,
            "ready_for_tools": True,
            "blocking_reasons": blocking_reasons,
            "wait_for_ready": True,
            "wait_seconds": round(wait_elapsed_s, 3),
            "editor_state_sequence": (
                final_state.get("sequence")
                if isinstance(final_state, dict)
                else None
            ),
            "editor_state_observed_at_unix_ms": (
                final_state.get("observed_at_unix_ms")
                if isinstance(final_state, dict)
                else None
            ),
            # Unity historically embeds a hint in data in addition to the MCP
            # response hint. Keep both levels consistent after the server wait.
            "hint": "Unity is ready for tool calls.",
        })
        if recovered_from_disconnect:
            ready_data["recovered_from_disconnect"] = True

        return MCPResponse(
            success=True,
            message="Refresh completed; editor readiness was confirmed.",
            data=ready_data,
            hint="Unity is ready for tool calls.",
        )

    return MCPResponse(**response_dict) if isinstance(response, dict) else response
