"""One refresh receipt, one overall deadline, observation-only reload recovery."""

import asyncio
import time
import uuid

from core.config import config
from models import MCPResponse
from models.unity_response import normalize_unity_response
from services.resources.editor_diagnostics import enrich_diagnostics
from transport.plugin_hub import PluginHub
from transport.runtime_protocol import CONTROL_PATH_CAPABILITY, EDITOR_DIAGNOSTICS_CAPABILITY, COMPILE_DIAGNOSTICS_CAPABILITY, REQUEST_RECEIPTS_CAPABILITY


async def try_refresh_runtime(ctx, instance, params, *, deadline):
    if config.transport_mode.lower() != "http":
        return None
    user_id = await ctx.get_state("user_id") if config.http_remote_hosted else None
    if config.http_remote_hosted and not user_id:
        return MCPResponse(success=False, code="auth_required", error="API key required")
    try:
        session = await PluginHub._resolve_session_id(instance, user_id=user_id, retry_on_reload=False)
        capabilities, project_hash = await PluginHub._session_runtime_details(session)
    except Exception as exc:
        return MCPResponse(success=False, code="UNITY_SESSION_UNAVAILABLE", error=str(exc))
    if not {CONTROL_PATH_CAPABILITY, EDITOR_DIAGNOSTICS_CAPABILITY, COMPILE_DIAGNOSTICS_CAPABILITY, REQUEST_RECEIPTS_CAPABILITY}.issubset(capabilities):
        return None  # Existing refresh remains available; no new diagnostic guarantee is claimed.
    if time.monotonic() >= deadline:
        return MCPResponse(success=False, code="refresh_prechecks_timeout",
                           error="The refresh budget expired before command admission.",
                           data={"refresh_not_sent": True, "request_outcome": "not_sent", "safe_to_retry_refresh": False})
    request_id = str(uuid.uuid4())
    sent_params = {**params, "wait_for_ready": False}
    try:
        response = normalize_unity_response(await asyncio.wait_for(
            PluginHub.send_command(session, "refresh_unity", sent_params, request_id=request_id),
            timeout=max(0.001, deadline - time.monotonic())))
    except Exception:
        response = {"success": False, "code": "refresh_outcome_unknown",
                    "error": "No authoritative refresh result was received."}
    data = dict(response.get("data") or {})
    known_success = response.get("success") is True
    receipt_state = "succeeded" if known_success else (response.get("runtime") or {}).get("status", "unobserved")
    # Admission/validation failures are definite failures, not reload recovery opportunities.
    if not known_success and response.get("code") not in {
        "PLUGIN_DISCONNECTED", "refresh_outcome_unknown", "OUTCOME_UNKNOWN", "REQUEST_IN_PROGRESS"
    }:
        return MCPResponse(**response)
    data.update({"request_id": request_id, "safe_to_retry_refresh": False,
                 "diagnostics_available": True})
    if not params.get("wait_for_ready"):
        data["request_outcome"] = receipt_state if known_success else "outcome_unknown"
        return MCPResponse(success=known_success, code=response.get("code"),
                           error=response.get("error"), data=data)

    last = None
    ready = False
    compile_done = params.get("compile") != "request"
    observed_cycle = None
    while time.monotonic() < deadline:
        try:
            # Re-resolve ONLY the same project after reload; never re-send refresh.
            session = await PluginHub._resolve_session_id(project_hash, user_id=user_id, retry_on_reload=False)
            observed = enrich_diagnostics(await asyncio.wait_for(
                PluginHub.query_editor_diagnostics(session),
                timeout=max(0.001, deadline - time.monotonic())))
            if observed.get("success") is True:
                last = observed.get("data") or {}
                ready = ((last.get("editor_state") or {}).get("advice") or {}).get("ready_for_tools") is True
                compilation = last.get("compilation") or {}
                request = next((r for r in compilation.get("requests", [])
                                if r.get("request_id") == request_id), None)
                cycle = compilation.get("last_cycle") or {}
                # A foreign cycle or a coalesced/multiple candidate is not an exclusive proof.
                compile_done = params.get("compile") != "request" or bool(
                    request and request.get("request_returned") is True
                    and request.get("correlation") == "observed_not_exclusive"
                    and request.get("candidate_cycle_ids") == [cycle.get("id")]
                    and cycle.get("status") == "completed" and cycle.get("capture_complete") is True
                )
                observed_cycle = cycle or None
                if not known_success and receipt_state not in {"outcome_unknown", "failed", "cancelled"}:
                    query = await asyncio.wait_for(PluginHub.query_receipt(session, request_id),
                                                  timeout=max(0.001, deadline - time.monotonic()))
                    receipt = query.get("receipt") or {}
                    receipt_state = receipt.get("state", receipt_state)
                    known_success = receipt_state == "succeeded"
                if ready and (compile_done or receipt_state in {"outcome_unknown", "failed", "cancelled"}):
                    break
        except Exception:
            pass
        await asyncio.sleep(min(0.25, max(0, deadline - time.monotonic())))
    data.update({"request_outcome": receipt_state, "ready_for_tools": ready,
                 "compilation_observation": (last or {}).get("compilation"),
                 "compile_cycle_confirmed": compile_done,
                 "compile_succeeded": observed_cycle.get("succeeded") if observed_cycle else None,
                 "recommended_next_action": "read_editor_diagnostics_and_original_receipt"})
    if last:
        data["editor_diagnostics"] = last
        state = last.get("editor_state") or {}
        data["resulting_state"] = (state.get("activity") or {}).get("phase", "unknown")
        data["blocking_reasons"] = (state.get("advice") or {}).get("blocking_reasons", [])
        matching = next((r for r in (last.get("compilation") or {}).get("requests", [])
                         if r.get("request_id") == request_id), None)
        if matching:
            data["compile_request"] = matching
    data["hint"] = "Read independent diagnostics and the original receipt; do not automatically repeat refresh."
    if not known_success:
        return MCPResponse(success=False, code="refresh_outcome_unknown" if receipt_state not in {"failed", "cancelled"} else "refresh_failed",
                           error="The original refresh outcome is not confirmed successful; it was not repeated.", data=data)
    if not ready or not compile_done:
        return MCPResponse(success=False, code="editor_readiness_timeout", error="Refresh completed, but readiness or the requested compile cycle was not confirmed within the overall budget.", data=data)
    if params.get("compile") == "request" and observed_cycle.get("succeeded") is not True:
        return MCPResponse(success=False, code="compilation_failed", error="Unity is reachable, but the observed compile cycle has compiler errors.", data=data)
    data["hint"] = "Unity is ready; the requested compile cycle is reported separately."
    return MCPResponse(success=True, message="Original refresh and editor readiness confirmed.",
                       data=data, runtime=response.get("runtime"), hint=data["hint"])
