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
        "inspect_collection",
        "reveal_item",
        "set_collection_expanded",
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
        "realized",
    ]


def test_ping_forwards_only_action(mock_unity):
    result = run_tool(action="ping")

    assert result["success"] is True
    assert mock_unity["tool_name"] == "interact_play_mode"
    assert mock_unity["params"] == {"action": "ping"}


@pytest.mark.parametrize("action,collection,extra", [
    ("inspect_collection", {"offset": 100, "limit": 25}, {}),
    ("reveal_item", {"index": 1999}, {"timeout_seconds": 2}),
    ("reveal_item", {"id": 20, "expand_ancestors": True}, {}),
    ("set_collection_expanded", {"id": 10}, {"value": False}),
    ("inspect_ui", {"index": 100, "query": {"element_name": "button"}}, {}),
    ("click_ui", {"id": 100, "query": {"element_class": "row-button"}}, {"button": "right"}),
    ("wait_ui", {"id": 20}, {"condition": "realized", "expected": False}),
])
def test_collection_forwarding_is_one_command(mock_unity, action, collection, extra):
    result = run_tool(action=action, ui_system="ui_toolkit", document="UI", element_name="items", collection=collection, **extra)
    assert result["success"]
    assert len(mock_unity["calls"]) == 1
    assert mock_unity["params"]["collection"] == collection
    for key, value in extra.items():
        assert mock_unity["params"][key] == value


@pytest.mark.parametrize("action,collection,extra", [
    ("reveal_item", None, {}), ("reveal_item", {}, {}),
    ("reveal_item", {"index": 1, "id": 1}, {}),
    ("reveal_item", {"index": True}, {}), ("reveal_item", {"index": -1}, {}),
    ("reveal_item", {"id": 1, "expand_ancestors": "true"}, {}),
    ("inspect_collection", {"limit": 101}, {}),
    ("inspect_collection", {"offset": 100001}, {}),
    ("inspect_collection", {"index": 1}, {}),
    ("click_ui", {"id": 1, "expand_ancestors": True}, {}),
    ("inspect_ui", {"id": 1, "query": {}}, {}),
    ("inspect_ui", {"id": 1, "query": {"element_name": 3}}, {}),
    ("inspect_ui", {"id": 1, "query": {"element_index": 3}}, {}),
    ("set_collection_expanded", {"id": 1}, {}),
    ("set_collection_expanded", {"id": 1}, {"value": "true"}),
    ("reveal_item", {"index": 1}, {"timeout_seconds": float("nan")}),
    ("reveal_item", {"index": 1}, {"timeout_seconds": 31}),
])
def test_collection_validation_never_dispatches(mock_unity, action, collection, extra):
    result = run_tool(action=action, ui_system="ui_toolkit", document="UI", element_name="items", collection=collection, **extra)
    assert result["code"] == "invalid_collection_parameters"
    assert not mock_unity["calls"]


@pytest.mark.parametrize("action", ["inspect_collection", "reveal_item", "set_collection_expanded"])
def test_collection_actions_reject_ugui(mock_unity, action):
    assert not run_tool(action=action, target="UI", collection={"id": 1})["success"]
    assert not mock_unity["calls"]


@pytest.mark.parametrize("space", ["panel_normalized", "texture_uv"])
@pytest.mark.parametrize("action,extra", [("click_ui", {}), ("hover_ui", {}), ("scroll_ui", {"scroll_delta": [0, -1]}), ("drag_ui", {"end_position": [0.8, 0.1]})])
def test_explicit_coordinate_spaces_forward(mock_unity, space, action, extra):
    assert run_tool(action=action, ui_system="ui_toolkit", document="UI", position=[0.5, 0.2], coordinate_space=space, **extra)["success"]
    assert mock_unity["params"]["coordinate_space"] == space


@pytest.mark.parametrize("action,ui_system,space", [("click_ui", "ugui", "panel_normalized"), ("click_ui", "ui_toolkit", "world"), ("inspect_ui", "ui_toolkit", "texture_uv")])
def test_invalid_coordinate_spaces_do_not_dispatch(mock_unity, action, ui_system, space):
    assert run_tool(action=action, ui_system=ui_system, coordinate_space=space)["code"] == "invalid_coordinate_space"
    assert not mock_unity["calls"]


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


@pytest.mark.parametrize("button", [None, "left", "right"])
def test_click_ui_toolkit_button_forwarding(mock_unity, button):
    result = run_tool(action="click_ui", ui_system="ui_toolkit",
                      document="RuntimeUI", position=[0.5, 0.5], button=button)
    assert result["success"] is True
    assert mock_unity["tool_name"] == "interact_play_mode"
    assert len(mock_unity["calls"]) == 1
    assert mock_unity["params"].get("button") == button
    assert ("button" in mock_unity["params"]) == (button is not None)


@pytest.mark.parametrize("button", ["middle", "RIGHT", "", 1, False, {}])
def test_click_ui_rejects_invalid_button_before_dispatch(mock_unity, button):
    result = run_tool(action="click_ui", target="Button", button=button)
    assert result["code"] == "invalid_click_button"
    assert mock_unity["calls"] == []


@pytest.mark.parametrize("action", [action for action in ALL_ACTIONS if action != "click_ui"])
def test_button_rejected_for_other_actions(mock_unity, action):
    result = run_tool(action=action, ui_system="ui_toolkit", button="left")
    assert result["code"] == "invalid_click_parameters"
    assert mock_unity["calls"] == []


def test_click_ui_ugui_rejects_right_but_accepts_explicit_left(mock_unity):
    result = run_tool(action="click_ui", target="Button", button="right")
    assert result["code"] == "ui_toolkit_required"
    assert mock_unity["calls"] == []
    result = run_tool(action="click_ui", target="Button", button="left")
    assert result["success"] is True
    assert mock_unity["params"]["button"] == "left"


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


@pytest.mark.parametrize("action,extra", [("click_ui", {}), ("hover_ui", {}),
    ("drag_ui", {"end_position": [0.7, 0.2]}), ("scroll_ui", {"scroll_delta": [0, -2]})])
def test_camera_surface_forwarding(mock_unity, action, extra):
    surface = {"camera": "Camera", "target": "Screen", "target_search_method": "by_name"}
    result = run_tool(action=action, ui_system="ui_toolkit", document="UI", position=[0.3, 0.7],
        coordinate_space="camera_viewport", surface=surface, **extra)
    assert result["success"] is True
    assert len(mock_unity["calls"]) == 1
    assert mock_unity["params"]["surface"] == surface
    assert mock_unity["params"]["coordinate_space"] == "camera_viewport"


@pytest.mark.parametrize("surface", [None, {}, [], {"camera": "Cam"}, {"camera": True, "target": "Screen"},
    {"camera": "Cam", "target": " "}, {"camera": "Cam", "target": "Screen", "uv": 1},
    {"camera": "Cam", "target": "Screen", "camera_search_method": "wrong"},
    {"camera": "Cam", "target": "Screen", "camera_search_method": []},
    {"camera": "Cam", "target": "Screen", "target_search_method": 1}])
def test_invalid_surface_rejected_before_dispatch(mock_unity, surface):
    result = run_tool(action="click_ui", ui_system="ui_toolkit", document="UI", position=[0.5, 0.5],
        coordinate_space="camera_viewport", surface=surface)
    assert result["code"] == "invalid_coordinate_space"
    assert mock_unity["calls"] == []


@pytest.mark.parametrize("space", [None, "panel_normalized", "texture_uv"])
def test_surface_cannot_be_silently_ignored(mock_unity, space):
    result = run_tool(action="click_ui", ui_system="ui_toolkit", document="UI", position=[0.5, 0.5],
        coordinate_space=space, surface={"camera": "Cam", "target": "Screen"})
    assert result["code"] == "invalid_coordinate_space"
    assert mock_unity["calls"] == []


def test_camera_drag_cannot_start_from_element_query(mock_unity):
    result = run_tool(action="drag_ui", ui_system="ui_toolkit", document="UI", element_name="Button",
        end_position=[0.5, 0.5], coordinate_space="camera_viewport", surface={"camera": "Cam", "target": "Screen"})
    assert result["code"] == "invalid_coordinate_space"
    assert mock_unity["calls"] == []


def test_unknown_action_is_rejected_before_transport(mock_unity):
    result = run_tool(action="invoke_component")

    assert result["success"] is False
    assert result["code"] == "unknown_action"
    assert mock_unity["calls"] == []
