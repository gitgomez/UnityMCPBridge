"""Backpressure tests for negotiated Command Runtime v1 sessions."""

import asyncio
from unittest.mock import AsyncMock

import pytest
import pytest_asyncio

import transport.plugin_hub as plugin_hub_module
from transport.plugin_hub import PluginHub
from transport.plugin_registry import PluginRegistry
from transport.runtime_protocol import (
    MAX_QUEUED_COMMANDS,
    build_server_advertisement,
    compute_command_payload_hash,
    negotiate_runtime,
)


@pytest_asyncio.fixture
async def configured_hub():
    registry = PluginRegistry()
    PluginHub.configure(registry, asyncio.get_running_loop())

    yield registry

    for task in list(PluginHub._ping_tasks.values()):
        task.cancel()
    PluginHub._connections.clear()
    PluginHub._pending.clear()
    PluginHub._ping_tasks.clear()
    PluginHub._last_pong.clear()
    PluginHub._registry = None
    PluginHub._lock = None
    PluginHub._loop = None


async def _connect_runtime_session(
    registry: PluginRegistry,
    session_id: str = "runtime-session",
) -> AsyncMock:
    advertisement = build_server_advertisement()
    negotiation = negotiate_runtime(advertisement, advertisement)
    await registry.register(
        session_id=session_id,
        project_name="RuntimeProject",
        project_hash="runtime-hash",
        unity_version="6000.3.9f1",
        runtime=advertisement,
        runtime_negotiation=negotiation,
    )
    websocket = AsyncMock()
    PluginHub._connections[session_id] = websocket
    return websocket


async def _wait_for_sends(websocket: AsyncMock, expected: int) -> None:
    for _ in range(200):
        if websocket.send_json.await_count >= expected:
            return
        await asyncio.sleep(0)
    pytest.fail(
        f"Expected {expected} WebSocket sends, got {websocket.send_json.await_count}."
    )


def _complete_sent_commands(websocket: AsyncMock) -> None:
    for call in websocket.send_json.await_args_list:
        command_id = call.args[0]["id"]
        entry = PluginHub._pending.get(command_id)
        if entry is not None and not entry["future"].done():
            entry["future"].set_result({"success": True, "id": command_id})


@pytest.mark.asyncio
async def test_runtime_rejects_sixty_fifth_command_without_sending_or_storing_it(
    configured_hub: PluginRegistry,
):
    websocket = await _connect_runtime_session(configured_hub)
    tasks = [
        asyncio.create_task(PluginHub.send_command("runtime-session", "ping", {}))
        for _ in range(MAX_QUEUED_COMMANDS)
    ]
    await _wait_for_sends(websocket, MAX_QUEUED_COMMANDS)

    rejected = await PluginHub.send_command("runtime-session", "ping", {})

    assert rejected["success"] is False
    assert rejected["code"] == "QUEUE_FULL"
    assert rejected["hint"] == "retry"
    assert rejected["data"]["queue_depth"] == MAX_QUEUED_COMMANDS
    assert websocket.send_json.await_count == MAX_QUEUED_COMMANDS
    assert len(PluginHub._pending) == MAX_QUEUED_COMMANDS

    _complete_sent_commands(websocket)
    await asyncio.gather(*tasks)
    assert PluginHub._pending == {}


@pytest.mark.asyncio
async def test_legacy_session_keeps_existing_unbounded_admission_behavior(
    configured_hub: PluginRegistry,
):
    websocket = AsyncMock()
    PluginHub._connections["legacy-session"] = websocket
    command_count = MAX_QUEUED_COMMANDS + 1
    tasks = [
        asyncio.create_task(PluginHub.send_command("legacy-session", "ping", {}))
        for _ in range(command_count)
    ]

    await _wait_for_sends(websocket, command_count)

    assert websocket.send_json.await_count == command_count
    assert len(PluginHub._pending) == command_count
    _complete_sent_commands(websocket)
    await asyncio.gather(*tasks)


@pytest.mark.asyncio
async def test_runtime_rejects_oversized_message_before_pending_storage(
    configured_hub: PluginRegistry,
    monkeypatch: pytest.MonkeyPatch,
):
    websocket = await _connect_runtime_session(configured_hub)
    monkeypatch.setattr(plugin_hub_module, "MAX_COMMAND_MESSAGE_BYTES", 128)

    rejected = await PluginHub.send_command(
        "runtime-session",
        "ping",
        {"content": "x" * 512},
    )

    assert rejected["success"] is False
    assert rejected["code"] == "MESSAGE_TOO_LARGE"
    assert websocket.send_json.await_count == 0
    assert PluginHub._pending == {}


@pytest.mark.asyncio
async def test_runtime_rejects_total_payload_bytes_before_second_send(
    configured_hub: PluginRegistry,
    monkeypatch: pytest.MonkeyPatch,
):
    websocket = await _connect_runtime_session(configured_hub)
    first = asyncio.create_task(
        PluginHub.send_command(
            "runtime-session",
            "ping",
            {"content": "x" * 256},
        )
    )
    await _wait_for_sends(websocket, 1)
    first_entry = next(iter(PluginHub._pending.values()))
    monkeypatch.setattr(
        plugin_hub_module,
        "MAX_QUEUED_PAYLOAD_BYTES",
        first_entry["payload_bytes"] + 1,
    )

    rejected = await PluginHub.send_command(
        "runtime-session",
        "ping",
        {"content": "y" * 256},
    )

    assert rejected["success"] is False
    assert rejected["code"] == "QUEUE_BYTES_EXCEEDED"
    assert rejected["hint"] == "retry"
    assert websocket.send_json.await_count == 1
    assert len(PluginHub._pending) == 1

    _complete_sent_commands(websocket)
    await first
    assert PluginHub._pending == {}


@pytest.mark.asyncio
async def test_runtime_execute_carries_retry_stable_logical_metadata(
    configured_hub: PluginRegistry,
):
    websocket = await _connect_runtime_session(configured_hub)
    sent: list[dict] = []

    async def complete(message: dict) -> None:
        sent.append(message)
        PluginHub._pending[message["id"]]["future"].set_result({"success": True})

    websocket.send_json.side_effect = complete
    request_id = "9e6ea65d-1fd1-42d2-9256-6e85d71dfa43"
    params = {"action": "save", "nested": {"b": 2, "a": 1}}

    await PluginHub.send_command(
        "runtime-session",
        "manage_scene",
        params,
        request_id=request_id,
        attempt=1,
    )

    execute = sent[0]
    assert execute["runtime"]["request_id"] == request_id
    assert execute["runtime"]["attempt"] == 1
    assert execute["runtime"]["payload_hash"] == compute_command_payload_hash(
        name="manage_scene",
        params=params,
        project_hash="runtime-hash",
    )
    assert execute["runtime"]["contract_version"] == 1
    assert execute["runtime"]["tool_name"] == "manage_scene"
    assert execute["runtime"]["profile"] == "unrestricted"
    assert execute["runtime"]["deadline_unix_ms"] > 0


@pytest.mark.asyncio
async def test_transport_retry_reuses_request_id_and_hash_but_changes_attempt_id(
    configured_hub: PluginRegistry,
):
    websocket = await _connect_runtime_session(configured_hub)
    sent: list[dict] = []

    async def complete(message: dict) -> None:
        sent.append(message)
        PluginHub._pending[message["id"]]["future"].set_result({"success": True})

    websocket.send_json.side_effect = complete
    request_id = "1a54a4c4-27eb-4c99-8a98-941709bb83db"

    await PluginHub.send_command(
        "runtime-session", "ping", {}, request_id=request_id, attempt=1
    )
    await PluginHub.send_command(
        "runtime-session", "ping", {}, request_id=request_id, attempt=2
    )

    assert sent[0]["id"] != sent[1]["id"]
    assert sent[0]["runtime"]["request_id"] == sent[1]["runtime"]["request_id"]
    assert sent[0]["runtime"]["payload_hash"] == sent[1]["runtime"]["payload_hash"]
    assert [message["runtime"]["attempt"] for message in sent] == [1, 2]


@pytest.mark.asyncio
async def test_receipt_control_query_bypasses_full_command_queue(
    configured_hub: PluginRegistry,
):
    websocket = await _connect_runtime_session(configured_hub)
    commands = [
        asyncio.create_task(PluginHub.send_command("runtime-session", "ping", {}))
        for _ in range(MAX_QUEUED_COMMANDS)
    ]
    await _wait_for_sends(websocket, MAX_QUEUED_COMMANDS)

    query = asyncio.create_task(
        PluginHub.query_receipt(
            "runtime-session",
            "ab3efe3f-8463-4f6e-b927-a8a721bd45e9",
        )
    )
    await _wait_for_sends(websocket, MAX_QUEUED_COMMANDS + 1)
    control = websocket.send_json.await_args_list[-1].args[0]
    assert control["type"] == "receipt_status"

    hub = object.__new__(PluginHub)
    await hub._handle_control_result(
        {
            "type": "receipt_status_result",
            "id": control["id"],
            "receipt": {"state": "queued"},
        }
    )
    response = await query

    assert response["receipt"]["state"] == "queued"
    assert len(PluginHub._pending) == MAX_QUEUED_COMMANDS
    _complete_sent_commands(websocket)
    await asyncio.gather(*commands)


@pytest.mark.asyncio
async def test_receipt_ledger_admin_control_bypasses_full_command_queue(
    configured_hub: PluginRegistry,
):
    websocket = await _connect_runtime_session(configured_hub)
    commands = [
        asyncio.create_task(PluginHub.send_command("runtime-session", "ping", {}))
        for _ in range(MAX_QUEUED_COMMANDS)
    ]
    await _wait_for_sends(websocket, MAX_QUEUED_COMMANDS)

    cleanup = asyncio.create_task(
        PluginHub.manage_receipt_ledger(
            "runtime-session",
            action="cleanup",
            scope="all_terminal",
            confirm_outcome_unknown=True,
        )
    )
    await _wait_for_sends(websocket, MAX_QUEUED_COMMANDS + 1)
    control = websocket.send_json.await_args_list[-1].args[0]
    assert control["type"] == "receipt_ledger"
    assert control["action"] == "cleanup"
    assert control["scope"] == "all_terminal"
    assert control["confirm_outcome_unknown"] is True

    hub = object.__new__(PluginHub)
    await hub._handle_control_result(
        {
            "type": "receipt_ledger_result",
            "id": control["id"],
            "result": {
                "success": True,
                "removed_total": 512,
                "after": {"total": 0},
            },
        }
    )
    response = await cleanup

    assert response["success"] is True
    assert response["removed_total"] == 512
    assert response["after"]["total"] == 0
    assert len(PluginHub._pending) == MAX_QUEUED_COMMANDS
    _complete_sent_commands(websocket)
    await asyncio.gather(*commands)
