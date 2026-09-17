"""Tests for additive Command Runtime capability negotiation."""

import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from transport.models import RegisterMessage, WelcomeMessage
from transport.plugin_hub import PluginHub
from transport.plugin_registry import PluginRegistry
from transport.runtime_protocol import (
    BATCH_SEMANTICS_CAPABILITY,
    BOUNDED_COMMAND_QUEUE_CAPABILITY,
    CONTRACT_MANIFEST_CAPABILITY,
    CONTROL_PATH_CAPABILITY,
    MAX_CACHED_RESULT_BYTES,
    MAX_COMMAND_MESSAGE_BYTES,
    MAX_CONTROL_MESSAGE_BYTES,
    MAX_PERSISTED_RECEIPT_BYTES,
    MAX_QUEUED_COMMANDS,
    MAX_QUEUED_PAYLOAD_BYTES,
    MAX_RECEIPTS,
    MAX_RESPONSE_MESSAGE_BYTES,
    MUTATION_CONTRACTS_CAPABILITY,
    NEGOTIATION_CAPABILITY,
    RECEIPT_LEDGER_ADMIN_CAPABILITY,
    REQUEST_RECEIPTS_CAPABILITY,
    RELOAD_LIFECYCLE_CAPABILITY,
    RESPONSE_ENVELOPE_CAPABILITY,
    STABLE_HANDLES_CAPABILITY,
    STATE_REVISIONS_CAPABILITY,
    RECEIPT_RETENTION_SECONDS,
    RuntimeAdvertisement,
    RuntimeMode,
    build_server_advertisement,
    build_runtime_command_metadata,
    compute_command_payload_hash,
    negotiate_runtime,
)


def test_server_advertises_implemented_runtime_capabilities_and_limits():
    advertisement = build_server_advertisement()

    assert advertisement.protocol == "command-runtime"
    assert advertisement.major == 1
    assert advertisement.minor == 0
    assert advertisement.server_version
    assert advertisement.capabilities == [
        NEGOTIATION_CAPABILITY,
        CONTRACT_MANIFEST_CAPABILITY,
        BOUNDED_COMMAND_QUEUE_CAPABILITY,
        CONTROL_PATH_CAPABILITY,
        REQUEST_RECEIPTS_CAPABILITY,
        RECEIPT_LEDGER_ADMIN_CAPABILITY,
        RESPONSE_ENVELOPE_CAPABILITY,
        MUTATION_CONTRACTS_CAPABILITY,
        STABLE_HANDLES_CAPABILITY,
        STATE_REVISIONS_CAPABILITY,
        BATCH_SEMANTICS_CAPABILITY,
        RELOAD_LIFECYCLE_CAPABILITY,
    ]
    assert advertisement.contract_version == 1
    assert advertisement.built_in_schema_hash.startswith("sha256:")
    assert advertisement.limits == {
        "max_command_message_bytes": MAX_COMMAND_MESSAGE_BYTES,
        "max_response_message_bytes": MAX_RESPONSE_MESSAGE_BYTES,
        "max_queued_commands": MAX_QUEUED_COMMANDS,
        "max_queued_payload_bytes": MAX_QUEUED_PAYLOAD_BYTES,
        "max_control_message_bytes": MAX_CONTROL_MESSAGE_BYTES,
        "max_receipts": MAX_RECEIPTS,
        "receipt_retention_seconds": RECEIPT_RETENTION_SECONDS,
        "max_persisted_receipt_bytes": MAX_PERSISTED_RECEIPT_BYTES,
        "max_cached_result_bytes": MAX_CACHED_RESULT_BYTES,
    }


def test_runtime_fields_are_optional_for_legacy_messages():
    welcome = WelcomeMessage(serverTimeout=30, keepAliveInterval=15)
    register = RegisterMessage(project_hash="legacy-project")

    assert "runtime" not in welcome.model_dump(exclude_none=True)
    assert "runtime" not in register.model_dump(exclude_none=True)


def test_matching_runtime_advertisements_negotiate_runtime_v1():
    local = build_server_advertisement()
    remote = RuntimeAdvertisement(
        package_version="10.1.1-beta.1",
        capabilities=[NEGOTIATION_CAPABILITY, "future_feature"],
    )

    result = negotiate_runtime(local, remote)

    assert result.mode is RuntimeMode.RUNTIME_V1
    assert result.capabilities == [NEGOTIATION_CAPABILITY]
    assert result.minor == 0
    assert result.diagnostics == []


@pytest.mark.parametrize("missing_side", ["local", "remote"])
def test_missing_advertisement_uses_legacy_mode(missing_side: str):
    advertisement = build_server_advertisement()
    local = None if missing_side == "local" else advertisement
    remote = None if missing_side == "remote" else advertisement

    assert negotiate_runtime(local, remote).mode is RuntimeMode.LEGACY


def test_major_mismatch_uses_advertised_legacy_fallback():
    local = build_server_advertisement()
    remote = RuntimeAdvertisement(
        major=2,
        capabilities=[NEGOTIATION_CAPABILITY],
        legacy_fallback=True,
    )

    result = negotiate_runtime(local, remote)

    assert result.mode is RuntimeMode.LEGACY
    assert "major version mismatch" in result.diagnostics[0]


def test_major_mismatch_without_fallback_is_incompatible():
    local = RuntimeAdvertisement(
        capabilities=[NEGOTIATION_CAPABILITY],
        legacy_fallback=False,
    )
    remote = RuntimeAdvertisement(
        major=2,
        capabilities=[NEGOTIATION_CAPABILITY],
        legacy_fallback=False,
    )

    assert negotiate_runtime(local, remote).mode is RuntimeMode.INCOMPATIBLE


def test_schema_mismatch_is_degraded_instead_of_incompatible():
    local = RuntimeAdvertisement(
        capabilities=[NEGOTIATION_CAPABILITY],
        built_in_schema_hash="sha256:local",
    )
    remote = RuntimeAdvertisement(
        capabilities=[NEGOTIATION_CAPABILITY],
        built_in_schema_hash="sha256:remote",
    )

    result = negotiate_runtime(local, remote)

    assert result.mode is RuntimeMode.DEGRADED
    assert result.capabilities == [NEGOTIATION_CAPABILITY]
    assert result.diagnostics == ["built-in tool schema hash mismatch"]


@pytest.mark.asyncio
async def test_websocket_welcome_advertises_runtime(monkeypatch):
    from core.config import config

    monkeypatch.setattr(config, "http_remote_hosted", False)
    websocket = AsyncMock()
    websocket.headers = {}
    websocket.state = SimpleNamespace()
    hub = object.__new__(PluginHub)

    await hub.on_connect(websocket)

    websocket.accept.assert_awaited_once()
    payload = websocket.send_json.await_args.args[0]
    assert payload["type"] == "welcome"
    assert payload["runtime"]["protocol"] == "command-runtime"
    assert payload["runtime"]["capabilities"] == [
        NEGOTIATION_CAPABILITY,
        CONTRACT_MANIFEST_CAPABILITY,
        BOUNDED_COMMAND_QUEUE_CAPABILITY,
        CONTROL_PATH_CAPABILITY,
        REQUEST_RECEIPTS_CAPABILITY,
        RECEIPT_LEDGER_ADMIN_CAPABILITY,
        RESPONSE_ENVELOPE_CAPABILITY,
        MUTATION_CONTRACTS_CAPABILITY,
        STABLE_HANDLES_CAPABILITY,
        STATE_REVISIONS_CAPABILITY,
        BATCH_SEMANTICS_CAPABILITY,
        RELOAD_LIFECYCLE_CAPABILITY,
    ]
    assert "package_version" not in payload["runtime"]


@pytest.mark.asyncio
async def test_plugin_registration_persists_negotiated_runtime(monkeypatch):
    from core.config import config

    monkeypatch.setattr(config, "http_remote_hosted", False)
    registry = PluginRegistry()
    PluginHub.configure(registry, asyncio.get_running_loop())
    websocket = AsyncMock()
    websocket.state = SimpleNamespace(user_id=None)
    hub = object.__new__(PluginHub)
    remote = RuntimeAdvertisement(
        package_version="10.1.1-beta.1",
        capabilities=[NEGOTIATION_CAPABILITY],
    )

    try:
        await hub._handle_register(
            websocket,
            RegisterMessage(
                project_name="TestProject",
                project_hash="hash-runtime",
                unity_version="6000.3.9f1",
                runtime=remote,
            ),
        )

        registered_payload = websocket.send_json.await_args.args[0]
        session = await registry.get_session(registered_payload["session_id"])
        assert session.runtime == remote
        assert session.runtime_negotiation.mode is RuntimeMode.RUNTIME_V1
        assert session.runtime_negotiation.capabilities == [NEGOTIATION_CAPABILITY]
    finally:
        for task in list(PluginHub._ping_tasks.values()):
            task.cancel()
        PluginHub._connections.clear()
        PluginHub._pending.clear()
        PluginHub._ping_tasks.clear()
        PluginHub._last_pong.clear()
        PluginHub._registry = None
        PluginHub._lock = None
        PluginHub._loop = None


def test_logical_payload_hash_is_stable_and_covers_target_and_parameters():
    first = compute_command_payload_hash(
        name="manage_scene",
        params={"action": "save", "nested": {"b": 2, "a": 1}},
        project_hash="project-a",
    )
    reordered = compute_command_payload_hash(
        name="manage_scene",
        params={"nested": {"a": 1, "b": 2}, "action": "save"},
        project_hash="project-a",
    )
    different_target = compute_command_payload_hash(
        name="manage_scene",
        params={"action": "save", "nested": {"a": 1, "b": 2}},
        project_hash="project-b",
    )

    assert first == reordered
    assert first.startswith("sha256:")
    assert first != different_target


def test_runtime_command_metadata_reuses_logical_id_without_hashing_attempt():
    request_id = "6e37852a-f6fa-49db-9fb8-746d9d699cf1"
    first = build_runtime_command_metadata(
        name="ping",
        params={},
        project_hash="runtime-project",
        timeout_seconds=30,
        request_id=request_id,
        attempt=1,
    )
    retry = build_runtime_command_metadata(
        name="ping",
        params={},
        project_hash="runtime-project",
        timeout_seconds=30,
        request_id=request_id,
        attempt=2,
    )

    assert first.request_id == retry.request_id == request_id
    assert first.payload_hash == retry.payload_hash
    assert first.attempt == 1
    assert retry.attempt == 2
