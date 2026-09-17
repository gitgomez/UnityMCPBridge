"""Mutation-profile policy and negotiated transport enforcement tests."""

import asyncio
from unittest.mock import AsyncMock

import pytest
import pytest_asyncio

from transport.mutation_policy import (
    DEFAULT_MUTATION_PROFILE,
    MUTATION_PROFILE_ENV,
    authorize_mutation,
    get_server_mutation_profile,
    resolve_tool_contract,
)
from transport.plugin_hub import PluginHub
from transport.plugin_registry import PluginRegistry
from transport.runtime_protocol import build_server_advertisement, negotiate_runtime


@pytest.mark.parametrize(
    ("action", "expected_tool"),
    [
        ("read", "find_in_file"),
        ("validate", "validate_script"),
        ("get_sha", "get_sha"),
        ("apply_text_edits", "apply_text_edits"),
        ("create", "manage_script"),
        ("delete", "manage_script"),
        ("apply_edits", "manage_script"),
    ],
)
def test_manage_script_actions_resolve_to_public_contract(action, expected_tool):
    tool_name, policy = resolve_tool_contract("manage_script", {"action": action})

    assert tool_name == expected_tool
    assert policy is not None
    assert policy["handler"] == "manage_script"


def test_unrestricted_is_compatibility_default_and_allows_unknown(monkeypatch):
    monkeypatch.delenv(MUTATION_PROFILE_ENV, raising=False)

    decision = authorize_mutation(
        get_server_mutation_profile(), "project_custom_tool", {}
    )

    assert DEFAULT_MUTATION_PROFILE == "unrestricted"
    assert decision.allowed is True
    assert decision.policy is None


@pytest.mark.parametrize(
    ("profile", "command", "allowed"),
    [
        ("read_only", "find_gameobjects", True),
        ("read_only", "import_model", False),
        ("standard", "import_model", True),
        ("standard", "manage_scene", False),
        ("destructive", "manage_scene", True),
        ("destructive", "manage_graphics", True),
        ("destructive", "manage_packages", False),
        ("destructive", "manage_build", False),
        ("destructive", "execute_code", False),
        ("unrestricted", "execute_code", True),
    ],
)
def test_profile_lattice_uses_manifest_class_and_destructive_flag(
    profile, command, allowed
):
    decision = authorize_mutation(profile, command, {})

    assert decision.allowed is allowed
    if not allowed:
        assert decision.code == "MUTATION_PROFILE_DENIED"


def test_restrictive_profile_fails_closed_for_custom_command():
    decision = authorize_mutation("standard", "project_custom_tool", {})

    assert decision.allowed is False
    assert decision.code == "TOOL_CONTRACT_NOT_FOUND"


def test_invalid_profile_is_structured_configuration_error():
    decision = authorize_mutation("typo", "find_gameobjects", {})

    assert decision.allowed is False
    assert decision.code == "INVALID_MUTATION_PROFILE"


@pytest_asyncio.fixture
async def configured_runtime_hub():
    registry = PluginRegistry()
    PluginHub.configure(registry, asyncio.get_running_loop())
    advertisement = build_server_advertisement()
    await registry.register(
        session_id="mutation-session",
        project_name="MutationProject",
        project_hash="mutation-hash",
        unity_version="6000.3.9f1",
        runtime=advertisement,
        runtime_negotiation=negotiate_runtime(advertisement, advertisement),
    )
    websocket = AsyncMock()
    PluginHub._connections["mutation-session"] = websocket

    yield websocket

    for task in list(PluginHub._ping_tasks.values()):
        task.cancel()
    PluginHub._connections.clear()
    PluginHub._pending.clear()
    PluginHub._ping_tasks.clear()
    PluginHub._last_pong.clear()
    PluginHub._registry = None
    PluginHub._lock = None
    PluginHub._loop = None


@pytest.mark.asyncio
async def test_negotiated_standard_profile_denies_before_websocket_send(
    configured_runtime_hub: AsyncMock,
    monkeypatch: pytest.MonkeyPatch,
):
    monkeypatch.setenv(MUTATION_PROFILE_ENV, "standard")

    result = await PluginHub.send_command(
        "mutation-session", "manage_scene", {"action": "save"}
    )

    assert result["success"] is False
    assert result["code"] == "MUTATION_PROFILE_DENIED"
    assert result["data"] == {
        "profile": "standard",
        "tool_name": "manage_scene",
        "mutation_class": "scene",
        "destructive": True,
    }
    configured_runtime_hub.send_json.assert_not_awaited()
    assert PluginHub._pending == {}


@pytest.mark.asyncio
async def test_negotiated_metadata_carries_resolved_alias_and_profile(
    configured_runtime_hub: AsyncMock,
    monkeypatch: pytest.MonkeyPatch,
):
    monkeypatch.setenv(MUTATION_PROFILE_ENV, "read_only")
    sent = []

    async def complete(message):
        sent.append(message)
        PluginHub._pending[message["id"]]["future"].set_result({"success": True})

    configured_runtime_hub.send_json.side_effect = complete

    await PluginHub.send_command(
        "mutation-session", "manage_script", {"action": "get_sha"}
    )

    assert sent[0]["name"] == "manage_script"
    assert sent[0]["runtime"]["tool_name"] == "get_sha"
    assert sent[0]["runtime"]["profile"] == "read_only"


@pytest.mark.asyncio
async def test_legacy_session_does_not_enable_profile_enforcement(
    monkeypatch: pytest.MonkeyPatch,
):
    registry = PluginRegistry()
    PluginHub.configure(registry, asyncio.get_running_loop())
    websocket = AsyncMock()
    PluginHub._connections["legacy-mutation"] = websocket
    monkeypatch.setenv(MUTATION_PROFILE_ENV, "read_only")

    async def complete(message):
        PluginHub._pending[message["id"]]["future"].set_result({"success": True})

    websocket.send_json.side_effect = complete
    try:
        result = await PluginHub.send_command(
            "legacy-mutation", "manage_scene", {"action": "save"}
        )

        assert result["success"] is True
        websocket.send_json.assert_awaited_once()
        assert "runtime" not in websocket.send_json.await_args.args[0]
    finally:
        PluginHub._connections.clear()
        PluginHub._pending.clear()
        PluginHub._registry = None
        PluginHub._lock = None
        PluginHub._loop = None
