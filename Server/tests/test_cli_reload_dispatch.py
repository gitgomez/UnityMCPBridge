"""CLI HTTP routing keeps Runtime-v1 reload and receipt guarantees."""

from unittest.mock import AsyncMock

import pytest

from transport.cli_command_dispatch import dispatch_cli_command
from transport.plugin_hub import PluginHub


@pytest.mark.asyncio
async def test_cli_command_recovers_session_replacement_with_same_receipt(
    monkeypatch,
):
    """The local REST route must not bypass reload-aware instance dispatch."""

    resolve = AsyncMock(side_effect=["session-before", "session-after"])
    ensure_live = AsyncMock(return_value=True)
    send = AsyncMock(
        side_effect=[
            {
                "success": False,
                "code": "PLUGIN_DISCONNECTED",
                "error": "Unity reloaded while awaiting command_result",
                "hint": "retry",
                "data": {"retry_after_ms": 100},
            },
            {"success": True, "message": "Recovered cached result."},
        ]
    )

    monkeypatch.setattr(PluginHub, "_resolve_session_id", resolve)
    monkeypatch.setattr(PluginHub, "_ensure_live_connection", ensure_live)
    monkeypatch.setattr(PluginHub, "send_command", send)
    monkeypatch.setattr(PluginHub, "_FAST_FAIL_COMMANDS", set())

    result, status_code = await dispatch_cli_command(
        "manage_editor",
        {"action": "play"},
        "Project@hash-reload",
    )

    assert status_code == 200
    assert result == {
        "success": True,
        "message": "Recovered cached result.",
    }
    assert resolve.await_count == 2
    assert ensure_live.await_count == 2
    assert send.await_count == 2
    assert send.await_args_list[0].args[:3] == (
        "session-before",
        "manage_editor",
        {"action": "play"},
    )
    assert send.await_args_list[1].args[:3] == (
        "session-after",
        "manage_editor",
        {"action": "play"},
    )
    first_runtime = send.await_args_list[0].kwargs
    retry_runtime = send.await_args_list[1].kwargs
    assert first_runtime["attempt"] == 1
    assert retry_runtime["attempt"] == 2
    assert first_runtime["request_id"] == retry_runtime["request_id"]


@pytest.mark.asyncio
async def test_cli_command_preserves_service_unavailable_http_status(
    monkeypatch,
):
    """A genuinely absent Editor remains an HTTP-level CLI failure."""

    send = AsyncMock(
        return_value={
            "success": False,
            "error": "Unity session not available; please retry",
            "hint": "retry",
            "data": {
                "reason": "no_unity_session",
                "retry_after_ms": 250,
            },
        }
    )
    monkeypatch.setattr(PluginHub, "send_command_for_instance", send)

    result, status_code = await dispatch_cli_command(
        "get_editor_state",
        {},
        "Project@hash-offline",
    )

    assert status_code == 503
    assert result["data"]["reason"] == "no_unity_session"
