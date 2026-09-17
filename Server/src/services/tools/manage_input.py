from typing import Annotated, Any, Literal, Optional, get_args

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


InputAction = Literal[
    "ping",
    "list_assets",
    "get",
    "create",
    "delete",
    "add_action_map",
    "remove_action_map",
    "add_action",
    "remove_action",
    "add_binding",
    "remove_binding",
    "add_control_scheme",
    "remove_control_scheme",
    "generate_csharp",
    "assign_player_input",
    "validate",
]

ALL_ACTIONS: list[str] = list(get_args(InputAction))


@mcp_for_unity_tool(
    group="core",
    description=(
        "Create, inspect, validate, and modify Unity Input System .inputactions assets. "
        "Manage action maps, actions, bindings, and control schemes; configure generated C# "
        "wrappers; and assign an action asset to a PlayerInput component. The tool has no "
        "compile-time dependency on com.unity.inputsystem and reports a clear unavailable "
        "response when the optional package is not installed."
    ),
    annotations=ToolAnnotations(
        title="Manage Input System",
        readOnlyHint=False,
        destructiveHint=True,
    ),
)
async def manage_input(
    ctx: Context,
    action: Annotated[InputAction, "Input System action to perform."],
    path: Annotated[
        Optional[str], "Assets-relative .inputactions asset path."
    ] = None,
    name: Annotated[
        Optional[str], "Asset name for create when it cannot be derived from path."
    ] = None,
    map_name: Annotated[Optional[str], "Input Action Map name."] = None,
    action_name: Annotated[Optional[str], "Input Action name."] = None,
    action_type: Annotated[
        Optional[Literal["Button", "Value", "PassThrough"]],
        "Input Action type. Defaults to Button.",
    ] = None,
    expected_control_type: Annotated[
        Optional[str], "Expected control layout, such as Button, Axis, Vector2, or Vector3."
    ] = None,
    binding_path: Annotated[
        Optional[str], "Input control path, such as <Keyboard>/space."
    ] = None,
    interactions: Annotated[Optional[str], "Binding/action interactions string."] = None,
    processors: Annotated[Optional[str], "Binding/action processors string."] = None,
    groups: Annotated[
        Optional[str], "Semicolon-separated binding groups/control schemes."
    ] = None,
    binding_name: Annotated[
        Optional[str], "Optional binding or composite-part name."
    ] = None,
    binding_index: Annotated[
        Optional[int], "Zero-based binding index within the selected action."
    ] = None,
    is_composite: Annotated[Optional[bool], "Whether a new binding is a composite."] = None,
    is_part_of_composite: Annotated[
        Optional[bool], "Whether a new binding is a composite part."
    ] = None,
    scheme_name: Annotated[Optional[str], "Control scheme name."] = None,
    binding_group: Annotated[
        Optional[str], "Control scheme binding group. Defaults to scheme_name."
    ] = None,
    devices: Annotated[
        Optional[list[dict[str, Any]]],
        "Control scheme devices as {device_path, optional?, or?} objects.",
    ] = None,
    output_path: Annotated[
        Optional[str], "Generated C# wrapper path, relative to Assets or Assets-relative."
    ] = None,
    class_name: Annotated[Optional[str], "Generated C# wrapper class name."] = None,
    namespace: Annotated[Optional[str], "Generated C# wrapper namespace."] = None,
    target: Annotated[
        Optional[str], "GameObject name, hierarchy path, or instance ID for assign_player_input."
    ] = None,
    default_map: Annotated[Optional[str], "PlayerInput default action map."] = None,
    default_scheme: Annotated[Optional[str], "PlayerInput default control scheme."] = None,
    notification_behavior: Annotated[
        Optional[Literal[
            "SendMessages",
            "BroadcastMessages",
            "InvokeUnityEvents",
            "InvokeCSharpEvents",
        ]],
        "PlayerInput notification behavior. Defaults to SendMessages.",
    ] = None,
    include_json: Annotated[
        Optional[bool], "Include the complete .inputactions JSON in get responses."
    ] = None,
    page_size: Annotated[Optional[int], "Asset list page size."] = None,
    cursor: Annotated[Optional[int], "Zero-based asset list cursor."] = None,
) -> dict[str, Any]:
    """Manage optional Unity Input System authoring without a hard package dependency."""

    action_lower = action.lower()
    if action_lower not in ALL_ACTIONS:
        return {
            "success": False,
            "message": f"Unknown action '{action}'. Valid: {', '.join(ALL_ACTIONS)}",
        }

    params: dict[str, Any] = {"action": action_lower}
    optional_params = {
        "path": path,
        "name": name,
        "map_name": map_name,
        "action_name": action_name,
        "action_type": action_type,
        "expected_control_type": expected_control_type,
        "binding_path": binding_path,
        "interactions": interactions,
        "processors": processors,
        "groups": groups,
        "binding_name": binding_name,
        "binding_index": binding_index,
        "is_composite": is_composite,
        "is_part_of_composite": is_part_of_composite,
        "scheme_name": scheme_name,
        "binding_group": binding_group,
        "devices": devices,
        "output_path": output_path,
        "class_name": class_name,
        "namespace": namespace,
        "target": target,
        "default_map": default_map,
        "default_scheme": default_scheme,
        "notification_behavior": notification_behavior,
        "include_json": include_json,
        "page_size": page_size,
        "cursor": cursor,
    }
    for key, value in optional_params.items():
        if value is not None:
            params[key] = value

    unity_instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_input",
        params,
    )
    return result if isinstance(result, dict) else {
        "success": False,
        "message": str(result),
    }
