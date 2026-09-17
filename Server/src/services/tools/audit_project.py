from typing import Annotated, Any, Literal, Optional, get_args

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


AuditAction = Literal["ping", "list_checks", "run"]
ALL_ACTIONS: list[str] = list(get_args(AuditAction))


@mcp_for_unity_tool(
    group="core",
    description=(
        "Run read-only Unity project health checks and return a structured, paginated report. "
        "Checks cover compilation state, build scenes, missing serialized references/scripts, "
        "duplicate GUIDs, orphaned meta files, package resolution, and loaded-scene structure. "
        "Use list_checks to discover check names and run with an explicit subset for fast audits."
    ),
    annotations=ToolAnnotations(
        title="Audit Unity Project",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def audit_project(
    ctx: Context,
    action: Annotated[AuditAction, "Audit action: ping, list_checks, or run."],
    checks: Annotated[
        Optional[list[str]],
        "Optional check names. Omit to run all checks returned by list_checks.",
    ] = None,
    search_root: Annotated[
        Optional[str],
        "Project-relative Assets folder scanned by asset/meta checks. Defaults to Assets.",
    ] = None,
    include_scenes: Annotated[
        Optional[bool],
        "Open and inspect scene assets during missing-reference scanning. Defaults to false; loaded scenes are checked separately.",
    ] = None,
    scan_limit: Annotated[
        Optional[int],
        "Maximum assets or meta files scanned per applicable check. Defaults to 5000, capped at 50000.",
    ] = None,
    minimum_severity: Annotated[
        Optional[Literal["info", "warning", "error"]],
        "Minimum issue severity returned. Defaults to warning.",
    ] = None,
    page_size: Annotated[
        Optional[int], "Maximum issues per page. Defaults to 50, capped at 500."
    ] = None,
    cursor: Annotated[Optional[int], "Zero-based issue pagination cursor."] = None,
) -> dict[str, Any]:
    """Run deterministic, read-only Unity project audits."""

    action_lower = action.lower()
    if action_lower not in ALL_ACTIONS:
        return {
            "success": False,
            "message": f"Unknown action '{action}'. Valid: {', '.join(ALL_ACTIONS)}",
        }

    params: dict[str, Any] = {"action": action_lower}
    optional_params = {
        "checks": checks,
        "search_root": search_root,
        "include_scenes": include_scenes,
        "scan_limit": scan_limit,
        "minimum_severity": minimum_severity,
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
        "audit_project",
        params,
    )
    return result if isinstance(result, dict) else {
        "success": False,
        "message": str(result),
    }
