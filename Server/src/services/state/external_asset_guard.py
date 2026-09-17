from __future__ import annotations

from typing import Any

from models import MCPResponse
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


def _response_dict(response: Any) -> dict[str, Any]:
    if isinstance(response, dict):
        return response
    if hasattr(response, "model_dump"):
        dumped = response.model_dump()
        return dumped if isinstance(dumped, dict) else {}
    return {}


def _normalize_asset_path(path: Any) -> str:
    if not isinstance(path, str):
        return ""
    normalized = path.strip().replace("\\", "/")
    while normalized.startswith("./"):
        normalized = normalized[2:]
    return normalized.casefold()


def _yaml_candidates(
    external_state: dict[str, Any],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    raw_paths = external_state.get("external_changed_paths")
    if not isinstance(raw_paths, list):
        return ([], [])

    scenes: list[dict[str, Any]] = []
    prefabs: list[dict[str, Any]] = []
    for item in raw_paths:
        if not isinstance(item, dict):
            continue
        path = item.get("path")
        normalized = _normalize_asset_path(path)
        if not normalized.startswith("assets/"):
            continue
        kind = str(item.get("kind") or "").casefold()
        if kind == "scene" or normalized.endswith(".unity"):
            scenes.append(dict(item))
        elif kind == "prefab" or normalized.endswith(".prefab"):
            prefabs.append(dict(item))
    return (scenes, prefabs)


def _blocked_response(
    *,
    candidates: list[dict[str, Any]],
    conflicts: list[dict[str, Any]],
    loaded_scene_paths: list[str],
    prefab_stage_path: str | None,
) -> MCPResponse:
    conflict_paths = [str(item.get("path")) for item in conflicts]
    return MCPResponse(
        success=False,
        error="external_asset_reload_blocked",
        code="external_asset_reload_blocked",
        message=(
            "Refresh blocked because externally changed Unity YAML is "
            f"currently loaded: {', '.join(conflict_paths)}. Resolve or "
            "close the conflicting asset in Unity before refreshing."
        ),
        data={
            "reason": "external_loaded_yaml_changed",
            "refresh_not_sent": True,
            "safe_to_retry_refresh": False,
            "recommended_next_action": (
                "resolve_or_close_conflicting_assets_in_unity"
            ),
            "changed_yaml_paths": candidates,
            "conflicting_paths": conflicts,
            "loaded_scene_paths": loaded_scene_paths,
            "prefab_stage_path": prefab_stage_path,
        },
    )


def _context_unavailable_response(
    *,
    candidates: list[dict[str, Any]],
    context_errors: list[str],
) -> MCPResponse:
    return MCPResponse(
        success=False,
        error="external_asset_context_unavailable",
        code="external_asset_context_unavailable",
        message=(
            "Refresh blocked because externally changed Unity YAML was "
            "detected, but its loaded editor context could not be verified."
        ),
        data={
            "reason": "external_yaml_context_unavailable",
            "refresh_not_sent": True,
            "safe_to_retry_refresh": False,
            "recommended_next_action": "read_editor_state_and_asset_context",
            "changed_yaml_paths": candidates,
            "context_errors": context_errors,
        },
    )


def _changed_paths_incomplete_response(
    external_state: dict[str, Any],
) -> MCPResponse:
    raw_paths = external_state.get("external_changed_paths")
    reported_paths = raw_paths if isinstance(raw_paths, list) else []
    return MCPResponse(
        success=False,
        error="external_change_list_incomplete",
        code="external_change_list_incomplete",
        message=(
            "Refresh blocked because external changes were detected, but "
            "the changed-path list was truncated and Unity YAML risk could "
            "not be ruled out."
        ),
        data={
            "reason": "external_changed_paths_truncated",
            "refresh_not_sent": True,
            "safe_to_retry_refresh": False,
            "recommended_next_action": "inspect_external_changes",
            "reported_changed_paths": reported_paths,
        },
    )


async def guard_external_yaml_refresh(
    ctx,
    unity_instance: str | None,
    external_state: dict[str, Any],
    *,
    editor_state_data: dict[str, Any] | None = None,
) -> MCPResponse | None:
    """Block refreshes that could open Unity's external YAML reload dialog."""
    scene_candidates, prefab_candidates = _yaml_candidates(external_state)
    if not scene_candidates and not prefab_candidates:
        if (external_state.get("external_changes_dirty") is True
                and external_state.get(
                    "external_changed_paths_truncated"
                ) is True):
            return _changed_paths_incomplete_response(external_state)
        return None

    loaded_scene_paths: list[str] = []
    prefab_stage_path: str | None = None
    context_errors: list[str] = []

    active_scene = (
        ((editor_state_data or {}).get("editor") or {}).get("active_scene")
        if isinstance((editor_state_data or {}).get("editor"), dict)
        else None
    )
    if isinstance(active_scene, dict) and isinstance(active_scene.get("path"), str):
        loaded_scene_paths.append(active_scene["path"])

    if scene_candidates:
        try:
            response = await send_with_unity_instance(
                async_send_command_with_retry,
                unity_instance,
                "manage_scene",
                {"action": "get_loaded_scenes"},
            )
            payload = _response_dict(response)
            if payload.get("success") is not True:
                context_errors.append("loaded_scenes_query_failed")
            else:
                data = payload.get("data")
                scenes = data.get("scenes") if isinstance(data, dict) else None
                if not isinstance(scenes, list):
                    context_errors.append("loaded_scenes_payload_invalid")
                else:
                    for scene in scenes:
                        if not isinstance(scene, dict) or scene.get("isLoaded") is False:
                            continue
                        path = scene.get("path")
                        if isinstance(path, str) and path:
                            loaded_scene_paths.append(path)
        except Exception:
            context_errors.append("loaded_scenes_query_failed")

    if prefab_candidates:
        try:
            response = await send_with_unity_instance(
                async_send_command_with_retry,
                unity_instance,
                "get_prefab_stage",
                {},
            )
            payload = _response_dict(response)
            if payload.get("success") is not True:
                context_errors.append("prefab_stage_query_failed")
            else:
                data = payload.get("data")
                if not isinstance(data, dict):
                    context_errors.append("prefab_stage_payload_invalid")
                elif data.get("isOpen") is True:
                    path = data.get("assetPath")
                    if isinstance(path, str) and path:
                        prefab_stage_path = path
        except Exception:
            context_errors.append("prefab_stage_query_failed")

    # Preserve Unity's display casing while comparing paths case-insensitively.
    loaded_scene_paths = list(dict.fromkeys(loaded_scene_paths))
    normalized_loaded_scenes = {
        _normalize_asset_path(path) for path in loaded_scene_paths
    }
    normalized_prefab_stage = _normalize_asset_path(prefab_stage_path)

    candidates = scene_candidates + prefab_candidates
    conflicts: list[dict[str, Any]] = []
    for item in scene_candidates:
        if _normalize_asset_path(item.get("path")) in normalized_loaded_scenes:
            conflict = dict(item)
            conflict["loaded_as"] = "scene"
            conflicts.append(conflict)
    for item in prefab_candidates:
        if (_normalize_asset_path(item.get("path"))
                == normalized_prefab_stage
                and normalized_prefab_stage):
            conflict = dict(item)
            conflict["loaded_as"] = "prefab_stage"
            conflicts.append(conflict)

    if conflicts:
        return _blocked_response(
            candidates=candidates,
            conflicts=conflicts,
            loaded_scene_paths=loaded_scene_paths,
            prefab_stage_path=prefab_stage_path,
        )

    if (external_state.get("external_changes_dirty") is True
            and external_state.get(
                "external_changed_paths_truncated"
            ) is True):
        return _changed_paths_incomplete_response(external_state)

    if context_errors:
        return _context_unavailable_response(
            candidates=candidates,
            context_errors=context_errors,
        )

    return None
