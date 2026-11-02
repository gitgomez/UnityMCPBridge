"""Shutdown authority, reload gaps and the registration/admission race."""

import asyncio
import json
import os

import pytest
from starlette.requests import Request

from services.managed_server_control import managed_server_control
from transport.plugin_registry import PluginRegistry, ServerDrainingError
from transport.runtime_protocol import RELOAD_LIFECYCLE_CAPABILITY, RuntimeAdvertisement


async def register(registry, session, project):
    return await registry.register(session, project, project, "6000.3.9f1",
                                   runtime=RuntimeAdvertisement(capabilities=[RELOAD_LIFECYCLE_CAPABILITY]))


@pytest.mark.asyncio
async def test_other_editor_and_disconnected_reload_block_shutdown():
    registry = PluginRegistry()
    await register(registry, "owner", "owner")
    await register(registry, "other", "other")
    assert "other_editor_connected" in (await registry.prepare_shutdown("owner"))["reasons"]
    await registry.mark_reloading("other", "assembly_reload")
    await registry.unregister("other")
    assert "editor_reloading" in (await registry.prepare_shutdown("owner"))["reasons"]
    await register(registry, "reconnected", "other")
    assert not (await registry.prepare_shutdown("owner"))["allowed"]


@pytest.mark.asyncio
async def test_atomic_prepare_closes_command_and_registration_admission():
    registry = PluginRegistry()
    await register(registry, "owner", "owner")
    assert await registry.begin_command("owner")
    assert not (await registry.prepare_shutdown("owner"))["allowed"]
    await registry.end_command("owner")
    grant = await registry.prepare_shutdown("owner")
    assert grant["allowed"]
    assert not await registry.begin_command("owner")
    with pytest.raises(ServerDrainingError):
        await register(registry, "racer", "other")
    assert not await registry.cancel_shutdown("foreign-ticket")
    assert await registry.cancel_shutdown(grant["ticket"])
    assert await registry.begin_command("owner")
    await registry.end_command("owner")


@pytest.mark.asyncio
async def test_racing_registration_never_has_both_a_grant_and_a_new_user():
    registry = PluginRegistry()
    await register(registry, "owner", "owner")
    results = await asyncio.gather(registry.prepare_shutdown("owner"),
                                   register(registry, "other", "other"), return_exceptions=True)
    grant, connection = results
    assert (grant["allowed"] and isinstance(connection, ServerDrainingError)) or (
        not grant["allowed"] and isinstance(connection, tuple))


@pytest.mark.asyncio
async def test_failed_stop_lease_expires_without_permanent_lockout(monkeypatch):
    registry = PluginRegistry()
    await register(registry, "owner", "owner")
    assert (await registry.prepare_shutdown("owner"))["allowed"]
    monkeypatch.setattr(registry, "_shutdown_until", 0)
    assert await registry.begin_command("owner")
    await registry.end_command("owner")


def request(body, token="owner-token", peer="127.0.0.1"):
    async def receive():
        return {"type": "http.request", "body": json.dumps(body).encode(), "more_body": False}
    return Request({"type": "http", "method": "POST", "path": "/api/server-control",
                    "headers": [(b"x-unity-mcp-instance-token", token.encode())],
                    "client": (peer, 32100)}, receive)


@pytest.mark.asyncio
@pytest.mark.parametrize("token,peer,expected", [("wrong", "127.0.0.1", 403),
                                               ("owner-token", "192.0.2.1", 403),
                                               ("owner-token", "127.0.0.1", 200)])
async def test_control_requires_loopback_and_private_launch_secret(monkeypatch, token, peer, expected):
    monkeypatch.setenv("UNITY_MCP_INSTANCE_TOKEN", "owner-token")
    monkeypatch.setenv("UNITY_MCP_OWNER_PROJECT_HASH", "owner")
    monkeypatch.setenv("UNITY_MCP_HTTP_HOST", "127.0.0.1")
    registry = PluginRegistry()
    response = await managed_server_control(
        request({"action": "prepare", "expected_pid": os.getpid()}, token, peer), lambda: registry)
    assert response.status_code == expected
    assert b"owner-token" not in response.body


@pytest.mark.asyncio
async def test_wrong_pid_and_public_listener_never_grant_stop(monkeypatch):
    monkeypatch.setenv("UNITY_MCP_INSTANCE_TOKEN", "owner-token")
    monkeypatch.setenv("UNITY_MCP_OWNER_PROJECT_HASH", "owner")
    monkeypatch.setenv("UNITY_MCP_HTTP_HOST", "127.0.0.1")
    registry = PluginRegistry()
    response = await managed_server_control(request({"expected_pid": os.getpid() + 1}), lambda: registry)
    assert response.status_code == 409
    monkeypatch.setenv("UNITY_MCP_HTTP_HOST", "0.0.0.0")
    response = await managed_server_control(request({"expected_pid": os.getpid()}), lambda: registry)
    assert response.status_code == 403
