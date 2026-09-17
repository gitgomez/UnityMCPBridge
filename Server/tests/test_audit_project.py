import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.audit_project import ALL_ACTIONS, audit_project


@pytest.fixture
def mock_unity(monkeypatch):
    captured = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["unity_instance"] = unity_instance
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok", "data": {}}

    monkeypatch.setattr(
        "services.tools.audit_project.get_unity_instance_from_context",
        AsyncMock(return_value="test-instance"),
    )
    monkeypatch.setattr(
        "services.tools.audit_project.send_with_unity_instance",
        fake_send,
    )
    return captured


def test_actions_are_stable():
    assert ALL_ACTIONS == ["ping", "list_checks", "run"]


def test_ping_forwards_only_action(mock_unity):
    result = asyncio.run(audit_project(SimpleNamespace(), action="ping"))

    assert result["success"] is True
    assert mock_unity["tool_name"] == "audit_project"
    assert mock_unity["params"] == {"action": "ping"}


def test_run_forwards_selected_checks_and_limits(mock_unity):
    asyncio.run(
        audit_project(
            SimpleNamespace(),
            action="run",
            checks=["compilation", "build_scenes"],
            search_root="Assets/Game",
            include_scenes=True,
            scan_limit=200,
            minimum_severity="error",
            page_size=25,
            cursor=50,
        )
    )

    assert mock_unity["params"] == {
        "action": "run",
        "checks": ["compilation", "build_scenes"],
        "search_root": "Assets/Game",
        "include_scenes": True,
        "scan_limit": 200,
        "minimum_severity": "error",
        "page_size": 25,
        "cursor": 50,
    }


def test_list_checks_does_not_add_none_values(mock_unity):
    asyncio.run(audit_project(SimpleNamespace(), action="list_checks"))

    assert mock_unity["params"] == {"action": "list_checks"}


def test_unknown_action_is_rejected_before_transport(mock_unity):
    result = asyncio.run(audit_project(SimpleNamespace(), action="repair"))

    assert result["success"] is False
    assert "Unknown action" in result["message"]
    assert mock_unity == {}
