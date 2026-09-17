import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.interact_play_mode import (
    ALL_ACTIONS,
    ALL_WAIT_CONDITIONS,
    interact_play_mode,
)


@pytest.fixture
def mock_unity(monkeypatch):
    captured = {"calls": []}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["unity_instance"] = unity_instance
        captured["tool_name"] = tool_name
        captured["params"] = params
        captured["calls"].append(params)
        return {
            "success": True,
            "message": "ok",
            "data": {
                "exists": True,
                "activeInHierarchy": True,
                "visible": True,
                "interactable": True,
                "selected": False,
                "textAvailable": True,
                "textRedacted": False,
                "text": "Ready",
                "toggleValue": False,
            },
        }

    monkeypatch.setattr(
        "services.tools.interact_play_mode.get_unity_instance_from_context",
        AsyncMock(return_value="test-instance"),
    )
    monkeypatch.setattr(
        "services.tools.interact_play_mode.send_with_unity_instance",
        fake_send,
    )
    return captured


def run_tool(**kwargs):
    return asyncio.run(interact_play_mode(SimpleNamespace(), **kwargs))


def test_action_surface_is_bounded():
    assert ALL_ACTIONS == [
        "ping",
        "click_ui",
        "inspect_ui",
        "wait_ui",
        "set_text",
        "set_toggle",
        "drag_ui",
        "scroll_ui",
        "hover_ui",
        "key_ui",
    ]
    assert ALL_WAIT_CONDITIONS == [
        "exists",
        "active",
        "visible",
        "interactable",
        "selected",
        "text_equals",
        "text_contains",
        "toggle_equals",
        "hovered",
    ]


def test_ping_forwards_only_action(mock_unity):
    result = run_tool(action="ping")

    assert result["success"] is True
    assert mock_unity["tool_name"] == "interact_play_mode"
    assert mock_unity["params"] == {"action": "ping"}


def test_click_ui_forwards_target_lookup(mock_unity):
    run_tool(
        action="click_ui",
        target="Canvas/Menu/StartButton",
        search_method="by_path",
    )

    assert mock_unity["params"] == {
        "action": "click_ui",
        "target": "Canvas/Menu/StartButton",
        "search_method": "by_path",
    }


def test_click_ui_forwards_normalized_position(mock_unity):
    run_tool(action="click_ui", position=[0.5, 0.25])

    assert mock_unity["params"] == {
        "action": "click_ui",
        "position": [0.5, 0.25],
    }


def test_click_ui_requires_exactly_one_address(mock_unity):
    result = run_tool(action="click_ui")

    assert result["success"] is False
    assert result["code"] == "invalid_ui_address"
    assert mock_unity["calls"] == []


def test_inspect_ui_forwards_read_options(mock_unity):
    run_tool(
        action="inspect_ui",
        target="Canvas/Login/Username",
        search_method="by_path",
        include_text=False,
    )

    assert mock_unity["params"] == {
        "action": "inspect_ui",
        "target": "Canvas/Login/Username",
        "search_method": "by_path",
        "include_text": False,
    }


def test_set_text_forwards_sensitive_value_without_adding_it_to_response(mock_unity):
    result = run_tool(
        action="set_text",
        target="PasswordInput",
        text="secret",
        submit=True,
        sensitive=True,
    )

    assert mock_unity["params"] == {
        "action": "set_text",
        "target": "PasswordInput",
        "text": "secret",
        "submit": True,
        "sensitive": True,
    }
    assert "secret" not in repr(result)


def test_set_text_accepts_empty_string(mock_unity):
    run_tool(action="set_text", target="UsernameInput", text="")

    assert mock_unity["params"]["text"] == ""


def test_set_toggle_forwards_desired_value(mock_unity):
    run_tool(action="set_toggle", target="RememberMe", value=False)

    assert mock_unity["params"] == {
        "action": "set_toggle",
        "target": "RememberMe",
        "value": False,
    }


def test_drag_ui_forwards_target_destination_and_steps(mock_unity):
    run_tool(
        action="drag_ui",
        target="Inventory/Handle",
        search_method="by_path",
        end_position=[0.8, 0.4],
        steps=9,
    )

    assert mock_unity["params"] == {
        "action": "drag_ui",
        "target": "Inventory/Handle",
        "search_method": "by_path",
        "end_position": [0.8, 0.4],
        "steps": 9,
    }


def test_drag_ui_forwards_coordinate_start(mock_unity):
    run_tool(
        action="drag_ui",
        position=[0.2, 0.3],
        end_position=[0.2, 0.8],
    )

    assert mock_unity["params"] == {
        "action": "drag_ui",
        "position": [0.2, 0.3],
        "end_position": [0.2, 0.8],
        "steps": 5,
    }


def test_scroll_ui_forwards_bounded_delta(mock_unity):
    run_tool(
        action="scroll_ui",
        target="KnowledgeBaseViewport",
        scroll_delta=[0, -4],
    )

    assert mock_unity["params"] == {
        "action": "scroll_ui",
        "target": "KnowledgeBaseViewport",
        "scroll_delta": [0, -4],
    }


def test_ui_toolkit_inspect_forwards_document_and_bounded_query(mock_unity):
    run_tool(
        action="inspect_ui",
        ui_system="ui_toolkit",
        document="RuntimeUI",
        document_search_method="by_name",
        element_class="knowledge-row",
        element_type="Button",
        element_index=3,
        include_text=False,
    )

    assert mock_unity["params"] == {
        "action": "inspect_ui",
        "ui_system": "ui_toolkit",
        "document": "RuntimeUI",
        "document_search_method": "by_name",
        "element_class": "knowledge-row",
        "element_type": "Button",
        "element_index": 3,
        "include_text": False,
    }


def test_ui_toolkit_click_forwards_element_address(mock_unity):
    run_tool(
        action="click_ui",
        ui_system="ui_toolkit",
        document="RuntimeUI",
        element_name="start-button",
    )

    assert mock_unity["params"] == {
        "action": "click_ui",
        "ui_system": "ui_toolkit",
        "document": "RuntimeUI",
        "element_name": "start-button",
    }


def test_ui_toolkit_coordinate_click_forwards_panel_position(mock_unity):
    run_tool(
        action="click_ui",
        ui_system="ui_toolkit",
        document="RuntimeUI",
        position=[0.5, 0.25],
    )

    assert mock_unity["params"] == {
        "action": "click_ui",
        "ui_system": "ui_toolkit",
        "document": "RuntimeUI",
        "position": [0.5, 0.25],
    }


def test_ui_toolkit_set_text_keeps_value_redacted(mock_unity):
    result = run_tool(
        action="set_text",
        ui_system="ui_toolkit",
        document="RuntimeUI",
        element_name="password",
        text="toolkit-secret",
        submit=True,
        sensitive=True,
    )

    assert mock_unity["params"] == {
        "action": "set_text",
        "ui_system": "ui_toolkit",
        "document": "RuntimeUI",
        "element_name": "password",
        "text": "toolkit-secret",
        "submit": True,
        "sensitive": True,
    }
    assert "toolkit-secret" not in repr(result)


def test_ui_toolkit_wait_preserves_document_query(mock_unity):
    result = run_tool(
        action="wait_ui",
        ui_system="ui_toolkit",
        document="RuntimeUI",
        element_name="status",
        condition="text_equals",
        expected="Ready",
    )

    assert result["success"] is True
    assert mock_unity["calls"] == [
        {
            "action": "wait_ui",
            "ui_system": "ui_toolkit",
            "document": "RuntimeUI",
            "element_name": "status",
            "include_text": True,
            "condition": "text_equals",
            "expected": "Ready",
            "timeout_seconds": 5.0,
            "poll_interval_seconds": 0.1,
        }
    ]


@pytest.mark.parametrize(
    ("kwargs", "code"),
    [
        (
            {
                "ui_system": "ui_toolkit",
                "element_name": "start-button",
            },
            "document_required",
        ),
        (
            {
                "ui_system": "ui_toolkit",
                "document": "RuntimeUI",
            },
            "invalid_ui_address",
        ),
        (
            {
                "ui_system": "ui_toolkit",
                "document": "RuntimeUI",
                "element_name": "start-button",
                "position": [0.5, 0.5],
            },
            "invalid_ui_address",
        ),
        (
            {
                "ui_system": "ui_toolkit",
                "document": "RuntimeUI",
                "element_index": 1,
            },
            "invalid_ui_toolkit_query",
        ),
        (
            {
                "ui_system": "ui_toolkit",
                "document": "RuntimeUI",
                "element_name": "start-button",
                "target": "Canvas/Button",
            },
            "invalid_ugui_address",
        ),
        (
            {
                "document": "RuntimeUI",
                "target": "Canvas/Button",
            },
            "invalid_ui_toolkit_address",
        ),
    ],
)
def test_ui_toolkit_addresses_are_discriminated_before_transport(
    mock_unity, kwargs, code
):
    result = run_tool(action="click_ui", **kwargs)

    assert result["success"] is False
    assert result["code"] == code
    assert mock_unity["calls"] == []


@pytest.mark.parametrize(
    ("action", "kwargs", "code"),
    [
        ("inspect_ui", {}, "target_required"),
        ("wait_ui", {"condition": "exists"}, "target_required"),
        ("set_text", {"target": "Field"}, "text_required"),
        ("set_toggle", {"target": "Toggle"}, "value_required"),
        ("drag_ui", {"target": "Handle"}, "invalid_drag_end"),
        ("scroll_ui", {"target": "List"}, "invalid_scroll_delta"),
    ],
)
def test_action_specific_required_parameters_are_validated(
    mock_unity, action, kwargs, code
):
    result = run_tool(action=action, **kwargs)

    assert result["success"] is False
    assert result["code"] == code
    assert mock_unity["calls"] == []


@pytest.mark.parametrize(
    ("action", "kwargs", "code"),
    [
        (
            "drag_ui",
            {"position": [0.5, 0.5], "end_position": [1.1, 0.5]},
            "invalid_drag_end",
        ),
        (
            "drag_ui",
            {"position": [0.5, 0.5], "end_position": [0.5, 0.6], "steps": 0},
            "invalid_drag_steps",
        ),
        (
            "scroll_ui",
            {"position": [0.5, 0.5], "scroll_delta": [0, 0]},
            "invalid_scroll_delta",
        ),
        (
            "scroll_ui",
            {"position": [0.5, 1.5], "scroll_delta": [0, -1]},
            "invalid_ui_position",
        ),
    ],
)
def test_gesture_parameters_are_validated_before_transport(
    mock_unity, action, kwargs, code
):
    result = run_tool(action=action, **kwargs)

    assert result["success"] is False
    assert result["code"] == code
    assert mock_unity["calls"] == []


def test_wait_ui_forwards_one_logical_runtime_command(mock_unity):
    run_tool(
        action="wait_ui",
        target="StartButton",
        condition="interactable",
        timeout_seconds=12,
        poll_interval_seconds=0.5,
        include_text=False,
    )

    assert mock_unity["calls"] == [
        {
            "action": "wait_ui",
            "target": "StartButton",
            "include_text": False,
            "condition": "interactable",
            "expected": None,
            "timeout_seconds": 12,
            "poll_interval_seconds": 0.5,
        }
    ]


@pytest.mark.parametrize(
    ("condition", "expected"),
    [
        ("text_equals", "Ready"),
        ("text_contains", "ead"),
        ("toggle_equals", False),
    ],
)
def test_wait_ui_forwards_value_conditions(mock_unity, condition, expected):
    run_tool(
        action="wait_ui",
        target="Target",
        condition=condition,
        expected=expected,
    )

    assert len(mock_unity["calls"]) == 1
    assert mock_unity["params"]["action"] == "wait_ui"
    assert mock_unity["params"]["condition"] == condition
    assert mock_unity["params"]["expected"] == expected


def test_wait_ui_does_not_echo_sensitive_expected_text(mock_unity):
    result = run_tool(
        action="wait_ui",
        target="PasswordInput",
        condition="text_equals",
        expected="secret",
    )

    assert len(mock_unity["calls"]) == 1
    assert mock_unity["params"]["action"] == "wait_ui"
    assert mock_unity["params"]["expected"] == "secret"
    assert "secret" not in repr(result)


def test_wait_ui_returns_unity_timeout_without_retry(monkeypatch, mock_unity):
    async def timeout_send(send_fn, unity_instance, tool_name, params):
        mock_unity["calls"].append(params)
        return {
            "success": False,
            "code": "wait_ui_timeout",
            "error": "condition not satisfied",
            "data": {
                "attempts": 3,
                "lastState": {"exists": True, "activeInHierarchy": False},
            },
        }

    monkeypatch.setattr(
        "services.tools.interact_play_mode.send_with_unity_instance",
        timeout_send,
    )
    result = run_tool(
        action="wait_ui",
        target="Panel",
        condition="active",
        timeout_seconds=0.1,
        poll_interval_seconds=0.05,
    )

    assert result["success"] is False
    assert result["code"] == "wait_ui_timeout"
    assert result["data"]["lastState"]["activeInHierarchy"] is False
    assert len(mock_unity["calls"]) == 1
    assert mock_unity["calls"][0]["action"] == "wait_ui"


def test_wait_ui_validates_condition_and_timeout_before_transport(mock_unity):
    result = run_tool(
        action="wait_ui",
        target="Panel",
        condition="text_equals",
        expected=True,
        timeout_seconds=31,
    )

    assert result["success"] is False
    assert result["code"] == "invalid_wait_expected"
    assert mock_unity["calls"] == []


def test_unknown_action_is_rejected_before_transport(mock_unity):
    result = run_tool(action="invoke_component")

    assert result["success"] is False
    assert result["code"] == "unknown_action"
    assert mock_unity["calls"] == []
