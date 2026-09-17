import asyncio
import time
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from core.telemetry import is_telemetry_enabled, record_tool_usage
from services.tools import get_unity_instance_from_context
from services.tools.utils import coerce_bool, coerce_float
import services.resources.editor_state as editor_state
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


async def _wait_for_play_mode_state(
    ctx: Context,
    *,
    desired_playing: bool,
    timeout_s: float,
) -> tuple[bool, float, dict[str, Any] | None]:
    """Poll editor_state until the requested stable Play Mode state is observed."""
    started = time.monotonic()
    last_state: dict[str, Any] | None = None

    while time.monotonic() - started < timeout_s:
        try:
            response = await editor_state.get_editor_state(ctx)
            payload = (
                response.model_dump()
                if hasattr(response, "model_dump")
                else response
            )
            data = payload.get("data") if isinstance(payload, dict) else None
            if isinstance(data, dict):
                last_state = data
                editor = data.get("editor")
                play_mode = (
                    editor.get("play_mode")
                    if isinstance(editor, dict)
                    else None
                )
                if (
                    isinstance(play_mode, dict)
                    and play_mode.get("is_playing") is desired_playing
                    and play_mode.get("is_changing") is not True
                ):
                    return (True, time.monotonic() - started, data)
        except Exception:
            # Domain reload and transport reconnection are expected during transitions.
            pass
        await asyncio.sleep(0.2)

    return (False, time.monotonic() - started, last_state)


@mcp_for_unity_tool(
    description="Controls and queries the Unity editor's state and settings. Read-only actions: telemetry_status, telemetry_ping. Modifying actions: play, pause, stop, set_active_tool, add_tag, remove_tag, add_layer, remove_layer, deploy_package, restore_package, restart_mcp_server, undo, redo. play/stop preserve Unity's deferred receipt behavior; optionally set wait_for_completion=true to poll editor_state until the requested stable state is observed. For prefab editing (open/save/close prefab stage), use manage_prefabs. deploy_package copies the configured MCPForUnity source folder into the project's installed package location (triggers recompile, no confirmation dialog). restore_package reverts to the pre-deployment backup. restart_mcp_server returns its response before safely restarting the Unity-managed local server; the Editor reconnects automatically. undo/redo perform Unity editor undo/redo and return the affected group name.",
    annotations=ToolAnnotations(
        title="Manage Editor",
    ),
)
async def manage_editor(
    ctx: Context,
    action: Annotated[Literal["telemetry_status", "telemetry_ping", "play", "pause", "stop", "set_active_tool", "add_tag", "remove_tag", "add_layer", "remove_layer", "deploy_package", "restore_package", "restart_mcp_server", "undo", "redo"], "Get and update the Unity Editor state. deploy_package copies the configured MCPForUnity source into the project's package location (triggers recompile). restore_package reverts the last deployment from backup. restart_mcp_server safely restarts the Unity-managed local server after returning its response; reconnect is automatic. undo/redo perform editor undo/redo. For prefab editing (open/save/close prefab stage), use manage_prefabs."],
    tool_name: Annotated[str,
                         "Tool name when setting active tool"] | None = None,
    tag_name: Annotated[str,
                        "Tag name when adding and removing tags"] | None = None,
    layer_name: Annotated[str,
                          "Layer name when adding and removing layers"] | None = None,
    wait_for_completion: Annotated[
        bool | str,
        "For play/stop, wait until editor_state confirms the stable requested state.",
    ] = False,
    timeout_seconds: Annotated[
        float | int | str | None,
        "Maximum wait for play/stop completion in seconds (default 30, range 0.1-120).",
    ] = None,
) -> dict[str, Any]:
    # Get active instance from request state (injected by middleware)
    unity_instance = await get_unity_instance_from_context(ctx)

    try:
        # Diagnostics: quick telemetry checks
        if action == "telemetry_status":
            return {"success": True, "telemetry_enabled": is_telemetry_enabled()}

        if action == "telemetry_ping":
            record_tool_usage("diagnostic_ping", True, 1.0, None)
            return {"success": True, "message": "telemetry ping queued"}

        should_wait = coerce_bool(wait_for_completion, default=False) is True
        if should_wait and action not in {"play", "stop"}:
            return {
                "success": False,
                "code": "invalid_wait_action",
                "error": "wait_for_completion is supported only for play and stop.",
                "data": {"action": action},
            }

        timeout_s = coerce_float(timeout_seconds, default=30.0)
        timeout_s = 30.0 if timeout_s is None else timeout_s
        if timeout_s < 0.1 or timeout_s > 120.0:
            return {
                "success": False,
                "code": "invalid_timeout",
                "error": "timeout_seconds must be between 0.1 and 120.",
                "data": {"timeout_seconds": timeout_s},
            }

        # Prepare parameters, removing None values
        params = {
            "action": action,
            "toolName": tool_name,
            "tagName": tag_name,
            "layerName": layer_name,
        }
        params = {k: v for k, v in params.items() if v is not None}

        # Send command using centralized retry helper with instance routing
        response = await send_with_unity_instance(async_send_command_with_retry, unity_instance, "manage_editor", params)

        # Preserve structured failure data; unwrap success into a friendlier shape
        if isinstance(response, dict) and response.get("success"):
            response_data = response.get("data")
            data = dict(response_data) if isinstance(response_data, dict) else {}
            if should_wait:
                desired_playing = action == "play"
                confirmed, elapsed, final_state = await _wait_for_play_mode_state(
                    ctx,
                    desired_playing=desired_playing,
                    timeout_s=timeout_s,
                )
                final_play_mode = None
                if isinstance(final_state, dict):
                    final_editor = final_state.get("editor")
                    if isinstance(final_editor, dict):
                        final_play_mode = final_editor.get("play_mode")

                if not confirmed:
                    return {
                        "success": False,
                        "code": "play_mode_transition_timeout",
                        "error": (
                            f"Unity did not confirm the requested "
                            f"{'playing' if desired_playing else 'stopped'} state "
                            f"within {timeout_s:g} seconds."
                        ),
                        "data": {
                            "requested_state": (
                                "playing" if desired_playing else "stopped"
                            ),
                            "wait_seconds": round(elapsed, 3),
                            "final_play_mode": final_play_mode,
                            "safe_to_retry": False,
                            "recommended_next_action": "read_editor_state",
                        },
                    }

                data.update({
                    "transition_confirmed": True,
                    "wait_seconds": round(elapsed, 3),
                    "final_play_mode": final_play_mode,
                })

            return {
                "success": True,
                "message": response.get(
                    "message",
                    "Editor operation successful.",
                ),
                "data": data or response_data,
            }
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}

    except Exception as e:
        return {"success": False, "message": f"Python error managing editor: {str(e)}"}
