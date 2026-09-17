import pytest

from models import MCPResponse
from services.state import external_asset_guard as guard_module
from services.state.external_asset_guard import guard_external_yaml_refresh

from .test_helpers import DummyContext


def _change(path: str, kind: str) -> dict:
    return {
        "path": path,
        "kind": kind,
        "source": "project",
        "extension": ".unity" if kind == "scene" else ".prefab",
        "requires_script_compilation": False,
        "is_unity_yaml": True,
    }


@pytest.mark.asyncio
async def test_guard_ignores_changes_without_unity_yaml(monkeypatch):
    async def unexpected_send(*args, **kwargs):
        raise AssertionError("Unity context should not be queried")

    monkeypatch.setattr(guard_module, "send_with_unity_instance", unexpected_send)

    result = await guard_external_yaml_refresh(
        DummyContext(),
        "Test@instance",
        {
            "external_changed_paths": [{
                "path": "Assets/Data/Game.asset",
                "kind": "asset",
                "is_unity_yaml": False,
            }],
        },
    )

    assert result is None


@pytest.mark.asyncio
async def test_guard_fails_closed_for_truncated_changed_path_list(monkeypatch):
    async def unexpected_send(*args, **kwargs):
        raise AssertionError("Unity context should not be queried")

    monkeypatch.setattr(guard_module, "send_with_unity_instance", unexpected_send)

    result = await guard_external_yaml_refresh(
        DummyContext(),
        "Test@instance",
        {
            "external_changes_dirty": True,
            "external_changed_paths_truncated": True,
            "external_changed_paths": [{
                "path": "Assets/Data/Game.asset",
                "kind": "asset",
                "is_unity_yaml": False,
            }],
        },
    )
    payload = result.model_dump()

    assert payload["error"] == "external_change_list_incomplete"
    assert payload["data"]["refresh_not_sent"] is True
    assert payload["data"]["safe_to_retry_refresh"] is False


@pytest.mark.asyncio
async def test_guard_blocks_changed_loaded_scene(monkeypatch):
    calls = []

    async def fake_send(send_fn, unity_instance, command, params, **kwargs):
        calls.append((command, params))
        return {
            "success": True,
            "data": {
                "scenes": [
                    {
                        "path": "Assets/Scenes/Bootstrap.unity",
                        "isLoaded": True,
                    },
                    {
                        "path": "Assets/Scenes/Additive.unity",
                        "isLoaded": True,
                    },
                ],
            },
        }

    monkeypatch.setattr(guard_module, "send_with_unity_instance", fake_send)
    change = _change("assets\\scenes\\ADDITIVE.unity", "scene")

    result = await guard_external_yaml_refresh(
        DummyContext(),
        "Test@instance",
        {"external_changed_paths": [change]},
    )
    payload = result.model_dump()

    assert payload["success"] is False
    assert payload["error"] == "external_asset_reload_blocked"
    assert payload["data"]["refresh_not_sent"] is True
    assert payload["data"]["safe_to_retry_refresh"] is False
    assert payload["data"]["conflicting_paths"][0]["loaded_as"] == "scene"
    assert calls == [("manage_scene", {"action": "get_loaded_scenes"})]


@pytest.mark.asyncio
async def test_guard_blocks_changed_open_prefab_stage(monkeypatch):
    async def fake_send(send_fn, unity_instance, command, params, **kwargs):
        assert command == "get_prefab_stage"
        return {
            "success": True,
            "data": {
                "isOpen": True,
                "assetPath": "Assets/Prefabs/Open.prefab",
            },
        }

    monkeypatch.setattr(guard_module, "send_with_unity_instance", fake_send)

    result = await guard_external_yaml_refresh(
        DummyContext(),
        "Test@instance",
        {
            "external_changed_paths": [
                _change("Assets/Prefabs/Open.prefab", "prefab")
            ],
        },
    )
    payload = result.model_dump()

    assert payload["error"] == "external_asset_reload_blocked"
    assert payload["data"]["prefab_stage_path"] == "Assets/Prefabs/Open.prefab"
    assert payload["data"]["conflicting_paths"][0]["loaded_as"] == "prefab_stage"


@pytest.mark.asyncio
async def test_guard_allows_unloaded_scene_and_closed_prefab(monkeypatch):
    async def fake_send(send_fn, unity_instance, command, params, **kwargs):
        if command == "manage_scene":
            return {
                "success": True,
                "data": {
                    "scenes": [{
                        "path": "Assets/Scenes/Loaded.unity",
                        "isLoaded": True,
                    }],
                },
            }
        if command == "get_prefab_stage":
            return {"success": True, "data": {"isOpen": False}}
        raise AssertionError(command)

    monkeypatch.setattr(guard_module, "send_with_unity_instance", fake_send)

    result = await guard_external_yaml_refresh(
        DummyContext(),
        "Test@instance",
        {
            "external_changed_paths": [
                _change("Assets/Scenes/Unloaded.unity", "scene"),
                _change("Assets/Prefabs/Closed.prefab", "prefab"),
            ],
        },
    )

    assert result is None


@pytest.mark.asyncio
async def test_guard_fails_closed_when_loaded_context_is_unavailable(monkeypatch):
    async def fake_send(*args, **kwargs):
        return {"success": False, "error": "disconnected"}

    monkeypatch.setattr(guard_module, "send_with_unity_instance", fake_send)

    result = await guard_external_yaml_refresh(
        DummyContext(),
        "Test@instance",
        {
            "external_changed_paths": [
                _change("Assets/Scenes/Unknown.unity", "scene")
            ],
        },
        editor_state_data={
            "editor": {
                "active_scene": {"path": "Assets/Scenes/Other.unity"},
            },
        },
    )
    payload = result.model_dump()

    assert payload["error"] == "external_asset_context_unavailable"
    assert payload["data"]["refresh_not_sent"] is True
    assert payload["data"]["context_errors"] == [
        "loaded_scenes_query_failed"
    ]


@pytest.mark.asyncio
async def test_refresh_unity_stops_before_transport_when_guard_blocks(monkeypatch):
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from services.tools import refresh_unity as refresh_module

    ctx = DummyContext()
    await ctx.set_state("unity_instance", "Test@instance")
    change = _change("Assets/Scenes/Loaded.unity", "scene")

    async def fake_editor_state(ctx):
        return MCPResponse(
            success=True,
            data={
                "unity": {"instance_id": "Test@instance"},
                "editor": {
                    "active_scene": {"path": "Assets/Scenes/Loaded.unity"},
                },
                "assets": {"external_changed_paths": [change]},
            },
        )

    def fake_scan(instance_id, *, force=False):
        assert instance_id == "Test@instance"
        assert force is True
        return {"external_changed_paths": [change]}

    async def fake_context_send(
        send_fn, unity_instance, command, params, **kwargs
    ):
        assert command == "manage_scene"
        return {
            "success": True,
            "data": {
                "scenes": [{
                    "path": "Assets/Scenes/Loaded.unity",
                    "isLoaded": True,
                }],
            },
        }

    async def unexpected_send(*args, **kwargs):
        raise AssertionError("refresh command must not reach Unity")

    monkeypatch.setattr(
        refresh_module.editor_state,
        "get_editor_state",
        fake_editor_state,
    )
    monkeypatch.setattr(
        refresh_module.external_changes_scanner,
        "update_and_get",
        fake_scan,
    )
    monkeypatch.setattr(
        guard_module,
        "send_with_unity_instance",
        fake_context_send,
    )
    monkeypatch.setattr(
        refresh_module.unity_transport,
        "send_with_unity_instance",
        unexpected_send,
    )

    result = await refresh_module.refresh_unity(ctx, wait_for_ready=False)
    payload = result.model_dump()

    assert payload["error"] == "external_asset_reload_blocked"
    assert payload["data"]["refresh_not_sent"] is True
