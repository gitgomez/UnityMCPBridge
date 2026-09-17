"""
Defines the manage_asset tool for interacting with Unity assets.
"""
import asyncio
import json
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.utils import parse_json_payload, coerce_int, normalize_properties
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry
from services.tools.preflight import preflight


@mcp_for_unity_tool(
    description=(
        "Performs asset operations (import, create, modify, delete, etc.) in Unity.\n\n"
        "Tip (payload safety): for `action=\"search\"`, prefer paging (`page_size`, `page_number`) and keep "
        "`generate_preview=false` (previews can add large base64 blobs)."
    ),
    annotations=ToolAnnotations(
        title="Manage Asset",
        destructiveHint=True,
    ),
)
async def manage_asset(
    ctx: Context,
    action: Annotated[Literal["import", "create", "modify", "delete", "duplicate", "move", "rename", "search", "get_info", "create_folder", "get_components"], "Perform CRUD operations on assets."],
    path: Annotated[str, "Asset path (e.g., 'Materials/MyMaterial.mat') or search scope (e.g., 'Assets')."],
    asset_type: Annotated[str,
                          "Asset type (e.g., 'Material', 'Folder') - required for 'create'. Note: For ScriptableObjects, use manage_scriptable_object."] | None = None,
    properties: Annotated[dict[str, Any] | str,
                          "Dictionary of properties for 'create'/'modify'. Keys are property names, values are property values."] | None = None,
    destination: Annotated[str,
                           "Target path for 'duplicate'/'move'."] | None = None,
    generate_preview: Annotated[bool,
                                "Generate a preview/thumbnail for the asset when supported. "
                                "Warning: previews may include large base64 payloads; keep false unless needed."] = False,
    search_pattern: Annotated[str,
                              "One Unity AssetDatabase query. Whitespace-separated terms are combined by Unity as one query (typically AND), e.g. 'Player t:Prefab'. Globs such as '*.prefab' are not supported."] | None = None,
    search_patterns: Annotated[
        list[str] | str,
        "Alternative Unity AssetDatabase queries combined with OR. Pass a JSON array/list such as ['Player', 'Enemy']; cannot be combined with search_pattern.",
    ] | None = None,
    filter_type: Annotated[str, "Filter type for search"] | None = None,
    filter_date_after: Annotated[str,
                                 "Date after which to filter"] | None = None,
    page_size: Annotated[int | float | str,
                         "Page size for pagination. Recommended: 25 (smaller for LLM-friendly responses)."] | None = None,
    page_number: Annotated[int | float | str,
                           "Page number for pagination (1-based)."] | None = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    refresh_if_dirty = action.lower() not in {"search", "get_info", "get_components"}
    gate = await preflight(
        ctx,
        wait_for_no_compile=True,
        refresh_if_dirty=refresh_if_dirty,
    )
    if gate is not None:
        return gate.model_dump()

    # --- Normalize properties using robust module-level helper ---
    properties, parse_error = normalize_properties(properties)
    if parse_error:
        await ctx.error(f"manage_asset: {parse_error}")
        return {"success": False, "message": parse_error}

    page_size = coerce_int(page_size)
    page_number = coerce_int(page_number)

    # --- Payload-safe normalization for common LLM mistakes (search) ---
    # Unity's C# handler treats `path` as a folder scope. If a model mistakenly puts a query like
    # "t:MonoScript" into `path`, Unity will consider it an invalid folder and fall back to searching
    # the entire project, which is token-heavy. Normalize such cases into search_pattern + Assets scope.
    action_l = (action or "").lower()
    if action_l == "search":
        try:
            raw_path = (path or "").strip()
        except (AttributeError, TypeError):
            # Handle case where path is not a string despite type annotation
            raw_path = ""

        # If the caller put an AssetDatabase query into `path`, treat it as `search_pattern`.
        if (not search_pattern) and not search_patterns and raw_path.startswith("t:"):
            search_pattern = raw_path
            path = "Assets"
            await ctx.info("manage_asset(search): normalized query from `path` into `search_pattern` and set path='Assets'")

        # If the caller used `asset_type` to mean a search filter, map it to filter_type.
        # (In Unity, filterType becomes `t:<filterType>`.)
        if (not filter_type) and asset_type and isinstance(asset_type, str):
            filter_type = asset_type
            await ctx.info("manage_asset(search): mapped `asset_type` into `filter_type` for safer server-side filtering")

        if search_pattern and search_patterns:
            return {
                "success": False,
                "code": "ambiguous_search_query",
                "error": "Use either search_pattern (one query) or search_patterns (OR queries), not both.",
            }

        if isinstance(search_patterns, str):
            raw_patterns = search_patterns.strip()
            try:
                parsed_patterns = json.loads(raw_patterns)
            except (json.JSONDecodeError, TypeError):
                parsed_patterns = [raw_patterns]
            search_patterns = (
                parsed_patterns
                if isinstance(parsed_patterns, list)
                else [raw_patterns]
            )

        if search_patterns is not None:
            if not isinstance(search_patterns, list):
                return {
                    "success": False,
                    "code": "invalid_search_patterns",
                    "error": "search_patterns must be a list of query strings.",
                }
            normalized_patterns: list[str] = []
            for value in search_patterns:
                if not isinstance(value, str) or not value.strip():
                    return {
                        "success": False,
                        "code": "invalid_search_patterns",
                        "error": "search_patterns entries must be non-empty strings.",
                    }
                pattern = value.strip()
                if len(pattern) > 256:
                    return {
                        "success": False,
                        "code": "invalid_search_patterns",
                        "error": "search_patterns entries must not exceed 256 characters.",
                    }
                normalized_patterns.append(pattern)
            if not normalized_patterns or len(normalized_patterns) > 16:
                return {
                    "success": False,
                    "code": "invalid_search_patterns",
                    "error": "search_patterns must contain between 1 and 16 queries.",
                }
            search_patterns = normalized_patterns

    # Prepare parameters for the C# handler
    params_dict = {
        "action": action.lower(),
        "path": path,
        "assetType": asset_type,
        "properties": properties,
        "destination": destination,
        "generatePreview": generate_preview,
        "searchPattern": search_pattern,
        "searchPatterns": search_patterns,
        "filterType": filter_type,
        "filterDateAfter": filter_date_after,
        "pageSize": page_size,
        "pageNumber": page_number
    }

    # Remove None values to avoid sending unnecessary nulls
    params_dict = {k: v for k, v in params_dict.items() if v is not None}

    # Get the current asyncio event loop
    loop = asyncio.get_running_loop()

    # Use centralized async retry helper with instance routing
    result = await send_with_unity_instance(async_send_command_with_retry, unity_instance, "manage_asset", params_dict, loop=loop)
    # Return the result obtained from Unity
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}
