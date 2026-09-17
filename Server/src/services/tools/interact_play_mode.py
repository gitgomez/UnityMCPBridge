from __future__ import annotations

from typing import Annotated, Any, Literal, Optional, get_args

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


PlayModeInteractionAction = Literal[
    "ping",
    "click_ui",
    "inspect_ui",
    "wait_ui",
    "set_text",
    "set_toggle",
    "drag_ui",
    "scroll_ui",
    "hover_ui",
    "key_ui",
]
PlayModeSearchMethod = Literal["by_id", "by_name", "by_path"]
PlayModeUiSystem = Literal["ugui", "ui_toolkit"]
PlayModeKeyCode = Literal[
    "Escape", "Tab", "Return", "Space",
    "LeftArrow", "RightArrow", "UpArrow", "DownArrow",
    "Backspace", "Delete", "Home", "End", "PageUp", "PageDown",
]
PlayModeKeyModifier = Literal["Shift", "Control", "Alt", "Command"]
ALL_KEY_CODES: list[str] = list(get_args(PlayModeKeyCode))
ALL_KEY_MODIFIERS: list[str] = list(get_args(PlayModeKeyModifier))
PlayModeWaitCondition = Literal[
    "exists",
    "active",
    "visible",
    "interactable",
    "selected",
    "text_equals",
    "text_contains",
    "toggle_equals",
    "hovered",
]
ALL_ACTIONS: list[str] = list(get_args(PlayModeInteractionAction))
ALL_WAIT_CONDITIONS: list[str] = list(get_args(PlayModeWaitCondition))

_DEFAULT_WAIT_TIMEOUT_SECONDS = 5.0
_MIN_WAIT_TIMEOUT_SECONDS = 0.1
_MAX_WAIT_TIMEOUT_SECONDS = 30.0
_DEFAULT_POLL_INTERVAL_SECONDS = 0.1
_MIN_POLL_INTERVAL_SECONDS = 0.05
_MAX_POLL_INTERVAL_SECONDS = 1.0
_DEFAULT_DRAG_STEPS = 5
_MIN_DRAG_STEPS = 1
_MAX_DRAG_STEPS = 64
_MAX_SCROLL_DELTA = 100.0


@mcp_for_unity_tool(
    group="core",
    description=(
        "Inspect, wait for, and interact with runtime uGUI or UI Toolkit in Play "
        "Mode without operating the OS mouse. uGUI remains the default; select "
        "ui_system='ui_toolkit' and provide a UIDocument plus a bounded element "
        "query for UI Toolkit. ping reports support and Play Mode state. click_ui "
        "dispatches a left pointer click. inspect_ui reports bounded runtime state "
        "for one active or inactive UI target. wait_ui performs bounded frame-driven "
        "inspection in Unity as one logical runtime command without blocking the main "
        "thread. set_text updates a TMP or "
        "legacy uGUI input field, optionally dispatching submit; its text is never "
        "echoed in the mutation response or Unity command log. set_toggle idempotently "
        "sets a Toggle. drag_ui dispatches a bounded synchronous pointer sequence. "
        "scroll_ui dispatches a bounded two-axis wheel delta to the associated "
        "scroll container. UI Toolkit hover_ui sends one pointer move without a "
        "press or release; inspect_ui/wait_ui can observe the actual hovered state. "
        "UI Toolkit key_ui sends one KeyDown/KeyUp pair to the current focus or "
        "document root, without changing focus, generating navigation events, "
        "typing text, or changing device state. Tab/Return do not imply navigation "
        "or submit. uGUI pointer actions accept a GameObject target or "
        "normalized Game View coordinates. UI Toolkit pointer actions accept an "
        "element query or normalized screen-space panel coordinates. Coordinates "
        "use a top-left origin. Mutating actions can cause gameplay or external "
        "side effects."
    ),
    annotations=ToolAnnotations(
        title="Interact With Play Mode",
        readOnlyHint=False,
        destructiveHint=True,
    ),
)
async def interact_play_mode(
    ctx: Context,
    action: Annotated[PlayModeInteractionAction, "Play Mode UI action."],
    ui_system: Annotated[
        PlayModeUiSystem,
        "Runtime UI system. Defaults to uGUI for backward compatibility.",
    ] = "ugui",
    target: Annotated[
        Optional[str],
        "uGUI GameObject name, hierarchy path, or instance ID.",
    ] = None,
    position: Annotated[
        Optional[list[float]],
        "Normalized top-left-origin [x,y] position for pointer actions, including UI Toolkit hover_ui.",
    ] = None,
    search_method: Annotated[
        Optional[PlayModeSearchMethod],
        "Optional uGUI target lookup mode; inferred from target when omitted.",
    ] = None,
    document: Annotated[
        Optional[str],
        "UI Toolkit UIDocument GameObject name, hierarchy path, or instance ID.",
    ] = None,
    document_search_method: Annotated[
        Optional[PlayModeSearchMethod],
        "Optional UI Toolkit UIDocument lookup mode; inferred when omitted.",
    ] = None,
    element_name: Annotated[
        Optional[str],
        "Exact UI Toolkit VisualElement name.",
    ] = None,
    element_class: Annotated[
        Optional[str],
        "Required UI Toolkit USS class on the target element.",
    ] = None,
    element_type: Annotated[
        Optional[str],
        "Exact UI Toolkit element type name, simple or fully qualified.",
    ] = None,
    element_index: Annotated[
        Optional[int],
        "Zero-based match index for an otherwise ambiguous UI Toolkit query (0-1023).",
    ] = None,
    include_text: Annotated[
        bool,
        "Include non-password UI text in inspect_ui and wait_ui state.",
    ] = True,
    condition: Annotated[
        Optional[PlayModeWaitCondition],
        "Condition for wait_ui.",
    ] = None,
    expected: Annotated[
        Optional[bool | str],
        "Expected bool or string for wait_ui; boolean conditions default to true.",
    ] = None,
    timeout_seconds: Annotated[
        float,
        "Bounded wait_ui timeout in seconds (0.1-30).",
    ] = _DEFAULT_WAIT_TIMEOUT_SECONDS,
    poll_interval_seconds: Annotated[
        float,
        "wait_ui polling interval in seconds (0.05-1).",
    ] = _DEFAULT_POLL_INTERVAL_SECONDS,
    text: Annotated[
        Optional[str],
        "New input-field value for set_text. Never returned or written to Unity's command log.",
    ] = None,
    submit: Annotated[
        bool,
        "Dispatch the uGUI submit event after set_text.",
    ] = False,
    sensitive: Annotated[
        bool,
        "Treat set_text as sensitive even if the input field is not password-typed.",
    ] = False,
    value: Annotated[
        Optional[bool],
        "Desired Toggle state for set_toggle.",
    ] = None,
    end_position: Annotated[
        Optional[list[float]],
        "Required normalized top-left-origin [x,y] destination for drag_ui.",
    ] = None,
    steps: Annotated[
        int,
        "Number of synchronous drag_ui movement steps (1-64).",
    ] = _DEFAULT_DRAG_STEPS,
    scroll_delta: Annotated[
        Optional[list[float]],
        "Required [x,y] Unity scroll units for scroll_ui; positive y scrolls up.",
    ] = None,
    key_code: Annotated[
        Optional[PlayModeKeyCode],
        "Named UI key for key_ui. Sends KeyDown/KeyUp only; Tab/Return do not generate navigation/submit.",
    ] = None,
    modifiers: Annotated[
        Optional[list[PlayModeKeyModifier]],
        "Distinct UI key modifiers for key_ui (at most four). Does not press physical modifier keys.",
    ] = None,
) -> dict:
    """Dispatch bounded Play Mode UI inspection, waiting, and interaction."""

    action_lower = action.lower()
    if action_lower not in ALL_ACTIONS:
        return _error(
            "unknown_action",
            f"Unknown action '{action}'. Valid: {', '.join(ALL_ACTIONS)}",
        )

    validation_error = _validate_parameters(
        action=action_lower,
        ui_system=ui_system,
        target=target,
        position=position,
        search_method=search_method,
        document=document,
        document_search_method=document_search_method,
        element_name=element_name,
        element_class=element_class,
        element_type=element_type,
        element_index=element_index,
        condition=condition,
        expected=expected,
        timeout_seconds=timeout_seconds,
        poll_interval_seconds=poll_interval_seconds,
        text=text,
        value=value,
        end_position=end_position,
        steps=steps,
        scroll_delta=scroll_delta,
        key_code=key_code,
        modifiers=modifiers,
    )
    if validation_error is not None:
        return validation_error

    unity_instance = await get_unity_instance_from_context(ctx)
    params: dict[str, Any] = {"action": action_lower}
    if ui_system != "ugui":
        params["ui_system"] = ui_system
    if target is not None:
        params["target"] = target
    if position is not None:
        params["position"] = position
    if search_method is not None:
        params["search_method"] = search_method
    if document is not None:
        params["document"] = document
    if document_search_method is not None:
        params["document_search_method"] = document_search_method
    if element_name is not None:
        params["element_name"] = element_name
    if element_class is not None:
        params["element_class"] = element_class
    if element_type is not None:
        params["element_type"] = element_type
    if element_index is not None:
        params["element_index"] = element_index
    if action_lower == "inspect_ui":
        params["include_text"] = include_text
    elif action_lower == "wait_ui":
        params.update(
            {
                "include_text": include_text,
                "condition": condition,
                "expected": expected,
                "timeout_seconds": timeout_seconds,
                "poll_interval_seconds": poll_interval_seconds,
            }
        )
    elif action_lower == "set_text":
        params.update(
            {
                "text": text,
                "submit": submit,
                "sensitive": sensitive,
            }
        )
    elif action_lower == "set_toggle":
        params["value"] = value
    elif action_lower == "drag_ui":
        params.update(
            {
                "end_position": end_position,
                "steps": steps,
            }
        )
    elif action_lower == "scroll_ui":
        params["scroll_delta"] = scroll_delta
    elif action_lower == "key_ui":
        params["key_code"] = key_code
        if modifiers is not None:
            params["modifiers"] = modifiers

    return await _send_to_unity(unity_instance, params)


def _validate_parameters(
    *,
    action: str,
    ui_system: str,
    target: str | None,
    position: list[float] | None,
    search_method: str | None,
    document: str | None,
    document_search_method: str | None,
    element_name: str | None,
    element_class: str | None,
    element_type: str | None,
    element_index: int | None,
    condition: str | None,
    expected: bool | str | None,
    timeout_seconds: float,
    poll_interval_seconds: float,
    text: str | None,
    value: bool | None,
    end_position: list[float] | None,
    steps: int,
    scroll_delta: list[float] | None,
    key_code: str | None = None,
    modifiers: list[str] | None = None,
) -> dict | None:
    if ui_system not in {"ugui", "ui_toolkit"}:
        return _error(
            "invalid_ui_system",
            "ui_system must be 'ugui' or 'ui_toolkit'.",
        )

    if action in {"hover_ui", "key_ui"} and ui_system != "ui_toolkit":
        return _error(
            "ui_toolkit_required",
            f"{action} requires ui_system='ui_toolkit'.",
        )
    if action == "key_ui":
        if key_code not in ALL_KEY_CODES:
            return _error("invalid_key_code", f"key_code must be one of: {', '.join(ALL_KEY_CODES)}.")
        if modifiers is not None and (
            not isinstance(modifiers, list)
            or len(modifiers) > 4
            or any(item not in ALL_KEY_MODIFIERS for item in modifiers)
            or len(set(modifiers)) != len(modifiers)
        ):
            return _error("invalid_key_modifiers", "modifiers must be distinct Shift, Control, Alt, or Command values.")
        if any(item is not None for item in (
            target, search_method, position,
            element_name, element_class, element_type, element_index,
        )):
            return _error("invalid_key_address", "key_ui accepts a document and uses its current focus or root; no target, position, or element query.")
    elif key_code is not None or modifiers is not None:
        return _error("invalid_key_parameters", "key_code and modifiers are only supported for key_ui.")

    toolkit_fields_present = any(
        value is not None
        for value in (
            document,
            document_search_method,
            element_name,
            element_class,
            element_type,
            element_index,
        )
    )
    if ui_system == "ugui" and toolkit_fields_present:
        return _error(
            "invalid_ui_toolkit_address",
            "document and element_* parameters require ui_system='ui_toolkit'.",
        )
    if ui_system == "ui_toolkit" and (
        target is not None or search_method is not None
    ):
        return _error(
            "invalid_ugui_address",
            "target and search_method are only supported for ui_system='ugui'.",
        )

    pointer_action = action in {"click_ui", "drag_ui", "scroll_ui", "hover_ui"}
    state_action = action in {
        "inspect_ui",
        "wait_ui",
        "set_text",
        "set_toggle",
    }
    if ui_system == "ugui" and pointer_action:
        has_target = target is not None and bool(target.strip())
        has_position = position is not None
        if has_target == has_position:
            return _error(
                "invalid_ui_address",
                f"Provide exactly one of 'target' or normalized 'position' for {action}.",
            )
        if search_method is not None and not has_target:
            return _error(
                "invalid_search_method",
                "search_method can only be used with a target.",
            )
        if has_position and not _is_bounded_vector2(position, 0.0, 1.0):
            return _error(
                "invalid_ui_position",
                "position must contain two finite numbers between 0 and 1.",
            )

    if ui_system == "ugui" and state_action:
        if target is None or not target.strip():
            return _error("target_required", f"target is required for {action}.")
        if position is not None:
            return _error(
                "invalid_ui_address",
                f"position is not supported for {action}; provide a target.",
            )

    if ui_system == "ui_toolkit" and action != "ping":
        if document is None or not document.strip():
            return _error(
                "document_required",
                f"document is required for UI Toolkit {action}.",
            )
        if document_search_method is not None and not document.strip():
            return _error(
                "invalid_document_search_method",
                "document_search_method requires a non-empty document.",
            )

        selectors = (element_name, element_class, element_type)
        has_element_query = any(
            value is not None and bool(value.strip())
            for value in selectors
        )
        if any(value is not None and not value.strip() for value in selectors):
            return _error(
                "invalid_ui_toolkit_query",
                "element_name, element_class, and element_type cannot be blank.",
            )
        if element_index is not None:
            if (
                isinstance(element_index, bool)
                or not isinstance(element_index, int)
                or not 0 <= element_index <= 1023
            ):
                return _error(
                    "invalid_ui_toolkit_query",
                    "element_index must be an integer between 0 and 1023.",
                )
            if not has_element_query:
                return _error(
                    "invalid_ui_toolkit_query",
                    "element_index requires element_name, element_class, or element_type.",
                )

        if pointer_action:
            has_position = position is not None
            if has_element_query == has_position:
                return _error(
                    "invalid_ui_address",
                    f"Provide exactly one UI Toolkit element query or normalized position for {action}.",
                )
            if has_position and not _is_bounded_vector2(position, 0.0, 1.0):
                return _error(
                    "invalid_ui_position",
                    "position must contain two finite numbers between 0 and 1.",
                )
        elif state_action:
            if not has_element_query:
                return _error(
                    "invalid_ui_toolkit_query",
                    f"An element query is required for UI Toolkit {action}.",
                )
            if position is not None:
                return _error(
                    "invalid_ui_address",
                    f"position is not supported for {action}; provide an element query.",
                )

    if action == "set_text" and text is None:
        return _error("text_required", "text is required for set_text, including when empty.")
    if action == "set_toggle" and value is None:
        return _error("value_required", "value is required for set_toggle.")

    if action == "drag_ui":
        if not _is_bounded_vector2(end_position, 0.0, 1.0):
            return _error(
                "invalid_drag_end",
                "end_position is required for drag_ui and must contain two finite numbers between 0 and 1.",
            )
        if isinstance(steps, bool) or not isinstance(steps, int) or not (
            _MIN_DRAG_STEPS <= steps <= _MAX_DRAG_STEPS
        ):
            return _error(
                "invalid_drag_steps",
                f"steps must be an integer between {_MIN_DRAG_STEPS} and {_MAX_DRAG_STEPS}.",
            )
    elif end_position is not None:
        return _error(
            "invalid_drag_end",
            "end_position is only supported for drag_ui.",
        )

    if action == "scroll_ui":
        if not _is_bounded_vector2(
            scroll_delta,
            -_MAX_SCROLL_DELTA,
            _MAX_SCROLL_DELTA,
        ) or all(float(component) == 0.0 for component in scroll_delta or []):
            return _error(
                "invalid_scroll_delta",
                "scroll_delta is required for scroll_ui, must contain two finite values between -100 and 100, and cannot be [0,0].",
            )
    elif scroll_delta is not None:
        return _error(
            "invalid_scroll_delta",
            "scroll_delta is only supported for scroll_ui.",
        )

    if action == "wait_ui":
        if condition == "hovered" and ui_system != "ui_toolkit":
            return _error("ui_toolkit_required", "The hovered condition requires ui_system='ui_toolkit'.")
        if condition not in ALL_WAIT_CONDITIONS:
            return _error(
                "invalid_wait_condition",
                f"condition is required for wait_ui. Valid: {', '.join(ALL_WAIT_CONDITIONS)}",
            )
        if condition in {"text_equals", "text_contains"} and not isinstance(expected, str):
            return _error(
                "invalid_wait_expected",
                f"expected must be a string for {condition}.",
            )
        if condition not in {"text_equals", "text_contains"} and expected is not None \
                and not isinstance(expected, bool):
            return _error(
                "invalid_wait_expected",
                f"expected must be a boolean for {condition}.",
            )
        if not _is_finite_number(timeout_seconds) or not (
            _MIN_WAIT_TIMEOUT_SECONDS
            <= float(timeout_seconds)
            <= _MAX_WAIT_TIMEOUT_SECONDS
        ):
            return _error(
                "invalid_wait_timeout",
                f"timeout_seconds must be between {_MIN_WAIT_TIMEOUT_SECONDS} and {_MAX_WAIT_TIMEOUT_SECONDS}.",
            )
        if not _is_finite_number(poll_interval_seconds) or not (
            _MIN_POLL_INTERVAL_SECONDS
            <= float(poll_interval_seconds)
            <= _MAX_POLL_INTERVAL_SECONDS
        ):
            return _error(
                "invalid_poll_interval",
                f"poll_interval_seconds must be between {_MIN_POLL_INTERVAL_SECONDS} and {_MAX_POLL_INTERVAL_SECONDS}.",
            )
    return None


async def _send_to_unity(
    unity_instance: str | None,
    params: dict[str, Any],
) -> dict:
    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "interact_play_mode",
        params,
    )
    return result if isinstance(result, dict) else _error(
        "invalid_unity_response",
        str(result),
    )


def _is_finite_number(value: Any) -> bool:
    if isinstance(value, bool):
        return False
    try:
        number = float(value)
    except (TypeError, ValueError):
        return False
    return number == number and number not in {float("inf"), float("-inf")}


def _is_bounded_vector2(value: Any, minimum: float, maximum: float) -> bool:
    return (
        isinstance(value, list)
        and len(value) == 2
        and all(
            _is_finite_number(component)
            and minimum <= float(component) <= maximum
            for component in value
        )
    )


def _error(code: str, message: str, data: dict[str, Any] | None = None) -> dict:
    result: dict[str, Any] = {
        "success": False,
        "code": code,
        "error": message,
        "message": message,
    }
    if data is not None:
        result["data"] = data
    return result
