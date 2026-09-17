from typing import Annotated, Any, Literal, Optional, get_args

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


AddressablesAction = Literal[
    "ping",
    "initialize",
    "list_groups",
    "create_group",
    "set_default_group",
    "remove_group",
    "list_entries",
    "add_entry",
    "remove_entry",
    "set_address",
    "list_labels",
    "add_label",
    "remove_label",
    "set_label",
    "get_profiles",
    "set_profile_value",
    "validate",
    "build",
    "build_status",
]

ALL_ACTIONS: list[str] = list(get_args(AddressablesAction))


@mcp_for_unity_tool(
    group="core",
    description=(
        "Initialize, inspect, validate, author, and build Unity Addressables content. "
        "Manage groups, entries, addresses, labels, and profile values without a hard "
        "compile-time dependency on com.unity.addressables. Use ping before authoring in "
        "projects where the optional package may not be installed. The build action queues "
        "a job; poll build_status with its returned job_id until it completes."
    ),
    annotations=ToolAnnotations(
        title="Manage Addressables",
        readOnlyHint=False,
        destructiveHint=True,
    ),
)
async def manage_addressables(
    ctx: Context,
    action: Annotated[AddressablesAction, "Addressables action to perform."],
    group_name: Annotated[Optional[str], "Addressables group name."] = None,
    set_default: Annotated[
        Optional[bool], "Set a newly created group as the default group."
    ] = None,
    force: Annotated[
        Optional[bool], "Allow destructive removal of non-empty groups or labels in use."
    ] = None,
    asset_path: Annotated[
        Optional[str], "Assets-relative asset or folder path for an entry."
    ] = None,
    guid: Annotated[Optional[str], "Asset GUID used to identify an entry."] = None,
    address: Annotated[
        Optional[str], "Runtime address. For set_address this is the new value."
    ] = None,
    labels: Annotated[
        Optional[list[str]], "Labels applied when adding an entry."
    ] = None,
    label: Annotated[Optional[str], "Single Addressables label name."] = None,
    enabled: Annotated[
        Optional[bool], "Whether set_label enables or disables the label."
    ] = None,
    profile_name: Annotated[
        Optional[str], "Profile name. Omit to use the active profile."
    ] = None,
    variable_name: Annotated[Optional[str], "Addressables profile variable name."] = None,
    value: Annotated[Optional[str], "New Addressables profile variable value."] = None,
    create_variable: Annotated[
        Optional[bool], "Create the profile variable if it does not exist."
    ] = None,
    job_id: Annotated[
        Optional[str], "Build job identifier returned by the build action."
    ] = None,
    search: Annotated[
        Optional[str], "Case-insensitive entry path, address, GUID, or label filter."
    ] = None,
    page_size: Annotated[Optional[int], "Result page size."] = None,
    cursor: Annotated[Optional[int], "Zero-based pagination cursor."] = None,
) -> dict[str, Any]:
    """Manage optional Unity Addressables authoring and builds."""

    action_lower = action.lower()
    if action_lower not in ALL_ACTIONS:
        return {
            "success": False,
            "message": f"Unknown action '{action}'. Valid: {', '.join(ALL_ACTIONS)}",
        }

    params: dict[str, Any] = {"action": action_lower}
    optional_params = {
        "group_name": group_name,
        "set_default": set_default,
        "force": force,
        "asset_path": asset_path,
        "guid": guid,
        "address": address,
        "labels": labels,
        "label": label,
        "enabled": enabled,
        "profile_name": profile_name,
        "variable_name": variable_name,
        "value": value,
        "create_variable": create_variable,
        "job_id": job_id,
        "search": search,
        "page_size": page_size,
        "cursor": cursor,
    }
    for key, parameter_value in optional_params.items():
        if parameter_value is not None:
            params[key] = parameter_value

    unity_instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_addressables",
        params,
    )
    return result if isinstance(result, dict) else {
        "success": False,
        "message": str(result),
    }
