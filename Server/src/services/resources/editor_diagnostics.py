"""Main-thread, modal and compilation observations outside receipt admission."""

from typing import Any

from fastmcp import Context

from core.config import config
from models import MCPResponse
from services.registry import mcp_for_unity_resource
from services.tools import get_unity_instance_from_context
from services.resources.editor_state import _enrich_advice_and_staleness
from transport.plugin_hub import PluginHub


def enrich_diagnostics(result: dict[str, Any]) -> dict[str, Any]:
    if result.get("success") is not True or not isinstance(result.get("data"), dict):
        return result
    data = result["data"]
    state = data.get("editor_state")
    if isinstance(state, dict) and state.get("observed_at_unix_ms") is not None:
        _enrich_advice_and_staleness(state)
        if (data.get("main_thread") or {}).get("status") != "ticking":
            state["advice"]["ready_for_tools"] = False
            state["advice"]["blocking_reasons"].append("main_thread_not_ticking")
    else:
        data["editor_state"] = {"advice": {"ready_for_tools": False,
                                           "blocking_reasons": ["observation_unavailable"]}}
    modal = data.get("native_modal") or {}
    if modal.get("status") == "modal_candidate_observed":
        data["editor_state"]["advice"]["ready_for_tools"] = False
        data["editor_state"]["advice"]["blocking_reasons"].append("native_modal_candidate")
    data["transport"] = {"control_reply_received": True}
    return result


@mcp_for_unity_resource(
    uri="mcpforunity://editor/diagnostics",
    name="editor_diagnostics",
    description="Read-only control-plane main-thread, native modal and compile-cycle evidence; no receipts or refresh.",
)
async def get_editor_diagnostics(ctx: Context) -> MCPResponse:
    if config.transport_mode.lower() != "http":
        return MCPResponse(success=False, code="CAPABILITY_NOT_NEGOTIATED",
                           error="Independent diagnostics require a negotiated HTTP plugin session.")
    instance = await get_unity_instance_from_context(ctx)
    user_id = await ctx.get_state("user_id") if config.http_remote_hosted else None
    if config.http_remote_hosted and not user_id:
        return MCPResponse(success=False, code="auth_required", error="API key required")
    try:
        session = await PluginHub._resolve_session_id(instance, user_id=user_id, retry_on_reload=False)
        result = enrich_diagnostics(await PluginHub.query_editor_diagnostics(session))
        return MCPResponse(**result)
    except Exception as exc:
        return MCPResponse(success=False, code="EDITOR_DIAGNOSTICS_UNAVAILABLE", error=str(exc))
