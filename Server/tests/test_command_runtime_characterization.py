"""Characterize the legacy wire contract before Command Runtime v1 changes.

These tests intentionally assert the observable v10.1 beta behavior. Runtime v1
must remain additive so legacy package/server pairs keep these wire shapes and
per-attempt command correlation semantics.
"""

import asyncio
import uuid
from unittest.mock import AsyncMock

import pytest
import pytest_asyncio

from transport.models import (
    CommandResultMessage,
    RegisterMessage,
    WelcomeMessage,
)
from transport.plugin_hub import PluginHub
from transport.plugin_registry import PluginRegistry


@pytest_asyncio.fixture
async def configured_hub():
    """Provide an isolated PluginHub class state for command-routing tests."""
    registry = PluginRegistry()
    PluginHub.configure(registry, asyncio.get_running_loop())

    yield

    for task in list(PluginHub._ping_tasks.values()):
        task.cancel()
    PluginHub._connections.clear()
    PluginHub._pending.clear()
    PluginHub._ping_tasks.clear()
    PluginHub._last_pong.clear()
    PluginHub._registry = None
    PluginHub._lock = None
    PluginHub._loop = None


def test_legacy_handshake_wire_shapes_are_stable():
    """Legacy peers exchange no runtime or schema-negotiation fields."""
    register_payload = {
        "type": "register",
        "project_name": "TestProject",
        "project_hash": "hash-123",
        "unity_version": "2022.3.62f1",
        "project_path": "D:/Projects/TestProject",
    }

    register = RegisterMessage.model_validate(register_payload)
    welcome = WelcomeMessage(serverTimeout=30, keepAliveInterval=15)

    assert register.model_dump(exclude_none=True) == register_payload
    assert welcome.model_dump(exclude_none=True) == {
        "type": "welcome",
        "serverTimeout": 30,
        "keepAliveInterval": 15,
    }


async def _complete_each_send(websocket: AsyncMock, sent: list[dict]) -> None:
    """Complete a command exactly as a legacy command_result would."""
    message = websocket.send_json.call_args.args[0]
    sent.append(message)
    pending = PluginHub._pending[message["id"]]
    pending["future"].set_result({"success": True, "id": message["id"]})


@pytest.mark.asyncio
async def test_repeated_logical_call_gets_a_new_transport_id_per_attempt(
    configured_hub,
):
    """Two identical sends currently have no shared logical request identity."""
    websocket = AsyncMock()
    sent: list[dict] = []

    async def send_json(_message: dict) -> None:
        await _complete_each_send(websocket, sent)

    websocket.send_json.side_effect = send_json
    PluginHub._connections["session-1"] = websocket

    params = {"action": "save"}
    first = await PluginHub.send_command("session-1", "manage_scene", params)
    second = await PluginHub.send_command("session-1", "manage_scene", params)

    assert len(sent) == 2
    assert sent[0]["id"] != sent[1]["id"]
    assert uuid.UUID(sent[0]["id"])
    assert uuid.UUID(sent[1]["id"])
    assert set(sent[0]) == {"type", "id", "name", "params", "timeout"}
    assert "request_id" not in sent[0]
    assert first["id"] == sent[0]["id"]
    assert second["id"] == sent[1]["id"]
    assert PluginHub._pending == {}


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("command", "params", "expected_timeout"),
    [
        ("manage_scene", {}, 30.0),
        ("manage_scene", {"timeout_seconds": 100}, 100.0),
        ("manage_scene", {"timeoutSeconds": 5000}, 3600.0),
        (
            "interact_play_mode",
            {"action": "wait_ui", "timeout_seconds": 30},
            35.0,
        ),
        ("read_console", {"timeout_seconds": 100}, 2.0),
    ],
)
async def test_legacy_execute_timeout_is_encoded_on_each_command(
    configured_hub,
    command: str,
    params: dict,
    expected_timeout: float,
):
    websocket = AsyncMock()
    sent: list[dict] = []

    async def send_json(_message: dict) -> None:
        await _complete_each_send(websocket, sent)

    websocket.send_json.side_effect = send_json
    PluginHub._connections["session-1"] = websocket

    await PluginHub.send_command("session-1", command, params)

    assert sent[0]["timeout"] == expected_timeout


@pytest.mark.asyncio
async def test_command_results_are_correlated_only_by_transport_id(configured_hub):
    """A result resolves only the pending future with the matching legacy id."""
    websocket = AsyncMock()
    sent: list[dict] = []

    async def capture_send(message: dict) -> None:
        sent.append(message)

    websocket.send_json.side_effect = capture_send
    PluginHub._connections["session-1"] = websocket

    first_task = asyncio.create_task(
        PluginHub.send_command("session-1", "manage_scene", {"action": "get_active"})
    )
    second_task = asyncio.create_task(
        PluginHub.send_command("session-1", "manage_scene", {"action": "get_hierarchy"})
    )

    for _ in range(20):
        if len(sent) == 2:
            break
        await asyncio.sleep(0)
    assert len(sent) == 2

    hub = object.__new__(PluginHub)
    await hub._handle_command_result(
        CommandResultMessage(id=sent[1]["id"], result={"success": True, "order": 2})
    )

    second = await asyncio.wait_for(second_task, timeout=1.0)
    assert second["order"] == 2
    assert not first_task.done()

    await hub._handle_command_result(
        CommandResultMessage(id=sent[0]["id"], result={"success": True, "order": 1})
    )

    first = await first_task
    assert first["order"] == 1
    assert PluginHub._pending == {}


@pytest.mark.asyncio
async def test_failed_websocket_send_does_not_leave_a_pending_command(configured_hub):
    websocket = AsyncMock()
    captured_futures: list[asyncio.Future] = []

    async def fail_send(message: dict) -> None:
        captured_futures.append(PluginHub._pending[message["id"]]["future"])
        raise OSError("socket closed")

    websocket.send_json.side_effect = fail_send
    PluginHub._connections["session-1"] = websocket

    with pytest.raises(OSError, match="socket closed"):
        await PluginHub.send_command("session-1", "manage_scene", {})

    assert PluginHub._pending == {}
    assert isinstance(captured_futures[0].exception(), OSError)
