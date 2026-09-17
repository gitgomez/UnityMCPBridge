import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.manage_addressables import ALL_ACTIONS, manage_addressables


@pytest.fixture
def mock_unity(monkeypatch):
    captured = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["unity_instance"] = unity_instance
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok", "data": {}}

    monkeypatch.setattr(
        "services.tools.manage_addressables.get_unity_instance_from_context",
        AsyncMock(return_value="test-instance"),
    )
    monkeypatch.setattr(
        "services.tools.manage_addressables.send_with_unity_instance",
        fake_send,
    )
    return captured


def test_action_surface_contains_complete_authoring_workflow():
    assert "initialize" in ALL_ACTIONS
    assert "create_group" in ALL_ACTIONS
    assert "add_entry" in ALL_ACTIONS
    assert "set_address" in ALL_ACTIONS
    assert "set_label" in ALL_ACTIONS
    assert "set_profile_value" in ALL_ACTIONS
    assert "validate" in ALL_ACTIONS
    assert "build" in ALL_ACTIONS


def test_ping_forwards_only_action(mock_unity):
    result = asyncio.run(manage_addressables(SimpleNamespace(), action="ping"))

    assert result["success"] is True
    assert mock_unity["tool_name"] == "manage_addressables"
    assert mock_unity["params"] == {"action": "ping"}


def test_create_group_forwards_group_configuration(mock_unity):
    asyncio.run(
        manage_addressables(
            SimpleNamespace(),
            action="create_group",
            group_name="Remote Content",
            set_default=True,
        )
    )

    assert mock_unity["params"]["group_name"] == "Remote Content"
    assert mock_unity["params"]["set_default"] is True


def test_add_entry_forwards_address_and_labels(mock_unity):
    asyncio.run(
        manage_addressables(
            SimpleNamespace(),
            action="add_entry",
            group_name="Remote Content",
            asset_path="Assets/Content/Hero.prefab",
            address="characters/hero",
            labels=["preload", "characters"],
        )
    )

    assert mock_unity["params"]["asset_path"] == "Assets/Content/Hero.prefab"
    assert mock_unity["params"]["address"] == "characters/hero"
    assert mock_unity["params"]["labels"] == ["preload", "characters"]


def test_entry_can_be_identified_by_guid(mock_unity):
    asyncio.run(
        manage_addressables(
            SimpleNamespace(),
            action="set_label",
            guid="0123456789abcdef0123456789abcdef",
            label="preload",
            enabled=False,
        )
    )

    assert mock_unity["params"]["guid"] == "0123456789abcdef0123456789abcdef"
    assert mock_unity["params"]["enabled"] is False


def test_profile_update_forwards_create_variable(mock_unity):
    asyncio.run(
        manage_addressables(
            SimpleNamespace(),
            action="set_profile_value",
            profile_name="Default",
            variable_name="CDN.Root",
            value="https://cdn.example.test",
            create_variable=True,
        )
    )

    assert mock_unity["params"]["profile_name"] == "Default"
    assert mock_unity["params"]["create_variable"] is True


def test_unknown_action_is_rejected_before_transport(mock_unity):
    result = asyncio.run(
        manage_addressables(SimpleNamespace(), action="upload_everything")
    )

    assert result["success"] is False
    assert "Unknown action" in result["message"]
    assert mock_unity == {}
