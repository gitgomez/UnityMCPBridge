from __future__ import annotations

import math
import re
from typing import Annotated, Any, Literal, Optional

from fastmcp import Context
from mcp.types import ToolAnnotations
from pydantic import StrictFloat, StrictInt
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance

InputAction = Literal["status", "key", "move", "click", "drag", "scroll", "cancel"]
KEYS = set("ABCDEFGHIJKLMNOPQRSTUVWXYZ") | {f"Digit{i}" for i in range(10)} | {
    f"F{i}" for i in range(1, 13)
} | set("Space Enter Tab Escape Backspace Delete Insert Home End PageUp PageDown LeftArrow RightArrow UpArrow DownArrow LeftShift RightShift LeftCtrl RightCtrl LeftAlt RightAlt LeftMeta RightMeta".split())


def validate_input_parameters(params: dict[str, Any]) -> Optional[str]:
    action = params.get("action")
    allowed = {
        "status": set(), "cancel": {"operation_id"}, "key": {"keys", "duration_seconds"},
        "move": {"position", "delta"},
        "click": {"position", "button", "keys", "duration_seconds"},
        "drag": {"position", "end_position", "button", "keys", "duration_seconds", "steps"},
        "scroll": {"position", "scroll_delta", "keys"},
    }
    if action not in allowed:
        return "Unsupported input action."
    values = {k: v for k, v in params.items() if v is not None and k != "action"}
    if set(values) - allowed[action]:
        return "Parameters do not apply to this action: " + ", ".join(sorted(set(values) - allowed[action]))
    keys = values.get("keys")
    if keys is not None and (not isinstance(keys, list) or not 1 <= len(keys) <= 8
            or any(not isinstance(k, str) or k not in KEYS for k in keys) or len(set(keys)) != len(keys)):
        return "keys must contain 1-8 distinct supported Input System key names."
    if action == "key" and keys is None:
        return "key requires keys."
    def number(value, lo, hi):
        return type(value) in (int, float) and math.isfinite(value) and lo <= value <= hi
    for name, lo, hi in [("position", 0, 1), ("end_position", 0, 1), ("delta", -4096, 4096), ("scroll_delta", -100, 100)]:
        value = values.get(name)
        if value is not None and (not isinstance(value, (list, tuple)) or len(value) != 2 or not all(number(v, lo, hi) for v in value)):
            return f"{name} requires two finite numbers in [{lo}, {hi}]."
    if action in ("click", "drag", "scroll") and "position" not in values:
        return f"{action} requires position."
    if action == "move" and ("position" in values) == ("delta" in values):
        return "move requires exactly one of position or delta."
    if action == "drag" and "end_position" not in values:
        return "drag requires end_position."
    if action == "scroll" and ("scroll_delta" not in values or not any(values["scroll_delta"])):
        return "scroll requires a nonzero scroll_delta."
    if "button" in values and values["button"] not in ("left", "right", "middle"):
        return "button must be left, right or middle."
    if "duration_seconds" in values and not number(values["duration_seconds"], .05, 5):
        return "duration_seconds must be between 0.05 and 5."
    if "steps" in values and (type(values["steps"]) is not int or not 1 <= values["steps"] <= 64):
        return "steps must be an integer from 1 to 64."
    if action == "cancel" and not re.fullmatch(r"[0-9a-f]{32}", str(values.get("operation_id", ""))):
        return "cancel requires the exact 32-character operation_id from status."
    return None


@mcp_for_unity_tool(
    group="core",
    description="Bounded gameplay keyboard/mouse input through optional Unity Input System virtual devices. status is read-only. key holds a chord; move uses absolute Game View coordinates or relative pixels; click/drag/scroll use an explicit position. Stable unpaused Play Mode and Dynamic updates required; paired InputUser/PlayerInput, legacy input and locked cursor are unsupported. No OS input, rebinding or settings changes. One gesture per Editor, normal player frames, at most 10 seconds including cleanup. cancel requires the active operation_id from status and releases only Bridge-owned devices. Input may cause irreversible gameplay; inspect after timeout, never blindly replay.",
    annotations=ToolAnnotations(title="Gameplay Input In Play Mode", readOnlyHint=False, destructiveHint=True),
)
async def input_play_mode(
    ctx: Context,
    action: Annotated[InputAction, "Bounded input action."],
    keys: Annotated[Optional[list[str]], "1-8 distinct Input System names: A-Z, Digit0-9, F1-12, Space/Enter/Tab/Escape, editing/navigation keys, Left/Right Shift/Ctrl/Alt/Meta. No text or IME."] = None,
    position: Annotated[Optional[list[StrictFloat]], "Absolute Game View [x,y], normalized 0-1, top-left origin."] = None,
    end_position: Annotated[Optional[list[StrictFloat]], "Drag endpoint, same normalized space."] = None,
    delta: Annotated[Optional[list[StrictFloat]], "Relative mouse pixels, positive Y up, each axis -4096..4096; move only."] = None,
    scroll_delta: Annotated[Optional[list[StrictFloat]], "Nonzero wheel delta in Input System units, each axis -100..100, positive Y up."] = None,
    button: Annotated[Optional[Literal["left", "right", "middle"]], "Click/drag button; default left."] = None,
    duration_seconds: Annotated[Optional[StrictFloat], "Key/click/drag minimum duration 0.05-5 seconds, default 0.1. Frame scheduling can extend it."] = None,
    steps: Annotated[Optional[StrictInt], "Drag movement samples 1-64, default 8; each survives a normal player frame."] = None,
    operation_id: Annotated[Optional[str], "Exact active operation ID from status; cancel only."] = None,
) -> dict[str, Any]:
    params = {k: v for k, v in locals().items() if k != "ctx" and v is not None}
    error = validate_input_parameters(params)
    if error:
        return {"success": False, "error": error, "code": "invalid_input_parameters"}
    instance = await get_unity_instance_from_context(ctx)
    return await send_with_unity_instance(async_send_command_with_retry, instance, "input_play_mode", params)
