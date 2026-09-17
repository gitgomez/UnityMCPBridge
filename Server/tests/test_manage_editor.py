"""Tests for manage_editor tool."""
import asyncio
import inspect
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.manage_editor import manage_editor
import services.tools.manage_editor as manage_editor_mod
from services.registry import get_registered_tools

# ── Fixture ──────────────────────────────────────────────────────────


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["unity_instance"] = unity_instance
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok"}

    monkeypatch.setattr(
        "services.tools.manage_editor.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.manage_editor.send_with_unity_instance",
        fake_send,
    )
    return captured


# ── Undo/Redo ────────────────────────────────────────────────────────


def test_undo_forwards_to_unity(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="undo"))
    assert result["success"] is True
    assert mock_unity["params"]["action"] == "undo"
    assert mock_unity["tool_name"] == "manage_editor"


def test_redo_forwards_to_unity(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="redo"))
    assert result["success"] is True
    assert mock_unity["params"]["action"] == "redo"


# ── All Unity-forwarded actions ──────────────────────────────────────

UNITY_FORWARDED_ACTIONS = [
    "play", "pause", "stop", "set_active_tool",
    "add_tag", "remove_tag", "add_layer", "remove_layer",
    "deploy_package", "restore_package", "restart_mcp_server",
    "undo", "redo",
]


@pytest.mark.parametrize("action_name", UNITY_FORWARDED_ACTIONS)
def test_every_action_forwards_to_unity(mock_unity, action_name):
    result = asyncio.run(manage_editor(SimpleNamespace(), action=action_name))
    assert result["success"] is True
    assert mock_unity["params"]["action"] == action_name


# ── Python-only actions ──────────────────────────────────────────────


def test_telemetry_status_handled_python_side(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="telemetry_status"))
    assert result["success"] is True
    assert "telemetry_enabled" in result
    assert "params" not in mock_unity


def test_telemetry_ping_handled_python_side(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="telemetry_ping"))
    assert result["success"] is True
    assert "params" not in mock_unity


# ── None params omitted ─────────────────────────────────────────────


def test_undo_omits_none_params(mock_unity):
    result = asyncio.run(manage_editor(SimpleNamespace(), action="undo"))
    assert result["success"] is True
    params = mock_unity["params"]
    assert "toolName" not in params
    assert "tagName" not in params
    assert "layerName" not in params


def test_play_waits_for_stable_requested_state(mock_unity, monkeypatch):
    wait_mock = AsyncMock(return_value=(
        True,
        0.42,
        {
            "editor": {
                "play_mode": {
                    "is_playing": True,
                    "is_paused": False,
                    "is_changing": False,
                }
            }
        },
    ))
    monkeypatch.setattr(manage_editor_mod, "_wait_for_play_mode_state", wait_mock)

    result = asyncio.run(manage_editor(
        SimpleNamespace(),
        action="play",
        wait_for_completion=True,
        timeout_seconds=12,
    ))

    assert result["success"] is True
    assert result["data"]["transition_confirmed"] is True
    assert result["data"]["final_play_mode"]["is_playing"] is True
    assert "wait_for_completion" not in mock_unity["params"]
    assert "timeout_seconds" not in mock_unity["params"]
    wait_mock.assert_awaited_once()
    assert wait_mock.await_args.kwargs["desired_playing"] is True
    assert wait_mock.await_args.kwargs["timeout_s"] == 12


def test_stop_wait_timeout_is_structured_and_not_safe_to_retry(
    mock_unity,
    monkeypatch,
):
    monkeypatch.setattr(
        manage_editor_mod,
        "_wait_for_play_mode_state",
        AsyncMock(return_value=(
            False,
            1.0,
            {
                "editor": {
                    "play_mode": {
                        "is_playing": True,
                        "is_changing": True,
                    }
                }
            },
        )),
    )

    result = asyncio.run(manage_editor(
        SimpleNamespace(),
        action="stop",
        wait_for_completion="true",
        timeout_seconds="1",
    ))

    assert result["success"] is False
    assert result["code"] == "play_mode_transition_timeout"
    assert result["data"]["safe_to_retry"] is False
    assert result["data"]["recommended_next_action"] == "read_editor_state"


def test_wait_is_rejected_for_non_transition_action(mock_unity):
    result = asyncio.run(manage_editor(
        SimpleNamespace(),
        action="undo",
        wait_for_completion=True,
    ))

    assert result["success"] is False
    assert result["code"] == "invalid_wait_action"
    assert "params" not in mock_unity


@pytest.mark.parametrize("timeout", [0, 121, "-2"])
def test_wait_timeout_is_bounded_before_dispatch(mock_unity, timeout):
    result = asyncio.run(manage_editor(
        SimpleNamespace(),
        action="play",
        wait_for_completion=True,
        timeout_seconds=timeout,
    ))

    assert result["success"] is False
    assert result["code"] == "invalid_timeout"
    assert "params" not in mock_unity
