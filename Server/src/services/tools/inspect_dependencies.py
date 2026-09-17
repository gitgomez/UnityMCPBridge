from typing import Annotated, Any, Literal, Optional, get_args

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


DependencyAction = Literal[
    "ping",
    "dependencies",
    "dependents",
    "impact",
    "missing_references",
    "cycles",
]

ALL_ACTIONS: list[str] = list(get_args(DependencyAction))


@mcp_for_unity_tool(
    group="core",
    description=(
        "Inspect Unity asset dependencies without modifying the project. "
        "Use dependencies for forward references, dependents for reverse references, "
        "impact before moving or deleting an asset, missing_references for broken serialized "
        "references and missing scripts, and cycles for circular asset dependency chains. "
        "Results are paginated and package assets are excluded by default."
    ),
    annotations=ToolAnnotations(
        title="Inspect Asset Dependencies",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def inspect_dependencies(
    ctx: Context,
    action: Annotated[DependencyAction, "Dependency inspection action."],
    target: Annotated[
        Optional[str],
        "Asset path or GUID. Required for dependencies, dependents, impact, and missing_references; optional for cycles.",
    ] = None,
    recursive: Annotated[
        Optional[bool],
        "Include transitive dependencies/dependents. Defaults to true for dependencies and false for dependents.",
    ] = None,
    include_packages: Annotated[
        Optional[bool], "Include assets under Packages/. Defaults to false."
    ] = None,
    search_root: Annotated[
        Optional[str],
        "Limit reverse-dependency or cycle scanning to this project-relative folder. Defaults to Assets.",
    ] = None,
    asset_type: Annotated[
        Optional[str],
        "Optional Unity asset type filter for returned assets, such as Prefab, Material, or SceneAsset.",
    ] = None,
    page_size: Annotated[
        Optional[int], "Maximum results per page. Defaults to 50 and is capped at 500."
    ] = None,
    cursor: Annotated[Optional[int], "Zero-based pagination cursor."] = None,
    scan_limit: Annotated[
        Optional[int],
        "Maximum candidate assets scanned for dependents or cycles. Defaults to 20000 and is capped at 100000.",
    ] = None,
    max_results: Annotated[
        Optional[int],
        "Maximum dependency lists or cycles returned by impact/cycles. Defaults to 100.",
    ] = None,
) -> dict[str, Any]:
    """Inspect dependency relationships and broken serialized references in Unity assets."""

    action_lower = action.lower()
    if action_lower not in ALL_ACTIONS:
        return {
            "success": False,
            "message": f"Unknown action '{action}'. Valid: {', '.join(ALL_ACTIONS)}",
        }

    params: dict[str, Any] = {"action": action_lower}
    optional_params = {
        "target": target,
        "recursive": recursive,
        "include_packages": include_packages,
        "search_root": search_root,
        "asset_type": asset_type,
        "page_size": page_size,
        "cursor": cursor,
        "scan_limit": scan_limit,
        "max_results": max_results,
    }
    for key, value in optional_params.items():
        if value is not None:
            params[key] = value

    unity_instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "inspect_dependencies",
        params,
    )
    return result if isinstance(result, dict) else {
        "success": False,
        "message": str(result),
    }
