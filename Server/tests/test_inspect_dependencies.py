import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.inspect_dependencies import ALL_ACTIONS, inspect_dependencies


@pytest.fixture
def mock_unity(monkeypatch):
    captured = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["unity_instance"] = unity_instance
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok", "data": {}}

    monkeypatch.setattr(
        "services.tools.inspect_dependencies.get_unity_instance_from_context",
        AsyncMock(return_value="test-instance"),
    )
    monkeypatch.setattr(
        "services.tools.inspect_dependencies.send_with_unity_instance",
        fake_send,
    )
    return captured


def test_actions_are_stable():
    assert ALL_ACTIONS == [
        "ping",
        "dependencies",
        "dependents",
        "impact",
        "missing_references",
        "cycles",
    ]


def test_ping_forwards_only_action(mock_unity):
    result = asyncio.run(inspect_dependencies(SimpleNamespace(), action="ping"))

    assert result["success"] is True
    assert mock_unity["tool_name"] == "inspect_dependencies"
    assert mock_unity["params"] == {"action": "ping"}


def test_dependencies_forwards_filters(mock_unity):
    asyncio.run(
        inspect_dependencies(
            SimpleNamespace(),
            action="dependencies",
            target="Assets/Player.prefab",
            recursive=False,
            include_packages=True,
            asset_type="Material",
            page_size=25,
            cursor=50,
        )
    )

    assert mock_unity["params"] == {
        "action": "dependencies",
        "target": "Assets/Player.prefab",
        "recursive": False,
        "include_packages": True,
        "asset_type": "Material",
        "page_size": 25,
        "cursor": 50,
    }


def test_dependents_forwards_scan_controls(mock_unity):
    asyncio.run(
        inspect_dependencies(
            SimpleNamespace(),
            action="dependents",
            target="0123456789abcdef0123456789abcdef",
            search_root="Assets/Scenes",
            scan_limit=1234,
        )
    )

    assert mock_unity["params"]["search_root"] == "Assets/Scenes"
    assert mock_unity["params"]["scan_limit"] == 1234


def test_cycles_does_not_require_target(mock_unity):
    result = asyncio.run(
        inspect_dependencies(
            SimpleNamespace(),
            action="cycles",
            max_results=10,
        )
    )

    assert result["success"] is True
    assert "target" not in mock_unity["params"]
    assert mock_unity["params"]["max_results"] == 10


def test_unknown_action_is_rejected_before_transport(mock_unity):
    result = asyncio.run(
        inspect_dependencies(SimpleNamespace(), action="not_real")
    )

    assert result["success"] is False
    assert "Unknown action" in result["message"]
    assert mock_unity == {}
