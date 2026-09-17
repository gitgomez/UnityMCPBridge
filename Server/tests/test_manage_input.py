import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.manage_input import ALL_ACTIONS, manage_input


@pytest.fixture
def mock_unity(monkeypatch):
    captured = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["unity_instance"] = unity_instance
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok", "data": {}}

    monkeypatch.setattr(
        "services.tools.manage_input.get_unity_instance_from_context",
        AsyncMock(return_value="test-instance"),
    )
    monkeypatch.setattr(
        "services.tools.manage_input.send_with_unity_instance",
        fake_send,
    )
    return captured


def test_action_surface_contains_authoring_workflow():
    assert "create" in ALL_ACTIONS
    assert "add_action_map" in ALL_ACTIONS
    assert "add_action" in ALL_ACTIONS
    assert "add_binding" in ALL_ACTIONS
    assert "add_control_scheme" in ALL_ACTIONS
    assert "generate_csharp" in ALL_ACTIONS
    assert "assign_player_input" in ALL_ACTIONS
    assert "validate" in ALL_ACTIONS


def test_ping_forwards_only_action(mock_unity):
    result = asyncio.run(manage_input(SimpleNamespace(), action="ping"))

    assert result["success"] is True
    assert mock_unity["tool_name"] == "manage_input"
    assert mock_unity["params"] == {"action": "ping"}


def test_add_action_forwards_action_configuration(mock_unity):
    asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="add_action",
            path="Assets/Input/Controls.inputactions",
            map_name="Player",
            action_name="Move",
            action_type="Value",
            expected_control_type="Vector2",
            interactions="Hold",
            processors="NormalizeVector2",
        )
    )

    assert mock_unity["params"]["action_type"] == "Value"
    assert mock_unity["params"]["expected_control_type"] == "Vector2"
    assert mock_unity["params"]["map_name"] == "Player"


def test_add_binding_forwards_composite_fields(mock_unity):
    asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="add_binding",
            path="Assets/Input/Controls.inputactions",
            map_name="Player",
            action_name="Move",
            binding_path="<Keyboard>/w",
            binding_name="up",
            groups="KeyboardMouse",
            is_part_of_composite=True,
        )
    )

    assert mock_unity["params"]["binding_path"] == "<Keyboard>/w"
    assert mock_unity["params"]["is_part_of_composite"] is True


def test_add_control_scheme_forwards_devices(mock_unity):
    devices = [
        {"device_path": "<Keyboard>", "optional": False},
        {"device_path": "<Mouse>", "optional": False},
    ]
    asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="add_control_scheme",
            path="Assets/Input/Controls.inputactions",
            scheme_name="KeyboardMouse",
            devices=devices,
        )
    )

    assert mock_unity["params"]["devices"] == devices


def test_assign_player_input_forwards_scene_configuration(mock_unity):
    asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="assign_player_input",
            path="Assets/Input/Controls.inputactions",
            target="Player",
            default_map="Player",
            notification_behavior="InvokeUnityEvents",
        )
    )

    assert mock_unity["params"]["target"] == "Player"
    assert mock_unity["params"]["default_map"] == "Player"
    assert mock_unity["params"]["notification_behavior"] == "InvokeUnityEvents"


def test_unknown_action_is_rejected_before_transport(mock_unity):
    result = asyncio.run(manage_input(SimpleNamespace(), action="bind_everything"))

    assert result["success"] is False
    assert "Unknown action" in result["message"]
    assert mock_unity == {}
