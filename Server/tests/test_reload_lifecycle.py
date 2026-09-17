"""Deterministic Unity reload lifecycle signalling and reconnect waits."""

from __future__ import annotations

import asyncio
import time
from datetime import datetime, timedelta, timezone
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from transport.models import PluginLifecycleMessage
from transport.plugin_hub import NoUnitySessionError, PluginHub
from transport.plugin_registry import PluginRegistry
from transport.runtime_protocol import (
    RELOAD_LIFECYCLE_CAPABILITY,
    RuntimeAdvertisement,
)
from transport.unity_instance_middleware import UnityInstanceMiddleware


class StateContext:
    def __init__(self):
        self.state: dict[str, object] = {}

    async def get_state(self, key: str):
        return self.state.get(key)


def reload_aware_runtime() -> RuntimeAdvertisement:
    return RuntimeAdvertisement(capabilities=[RELOAD_LIFECYCLE_CAPABILITY])


def reset_plugin_hub() -> None:
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
async def test_reload_marker_survives_disconnect_until_new_session_is_ready():
    registry = PluginRegistry()
    runtime = reload_aware_runtime()
    await registry.register(
        session_id="before",
        project_name="Project",
        project_hash="hash",
        unity_version="6000.3.9f1",
        runtime=runtime,
    )

    lifecycle = await registry.mark_reloading("before", "assembly_reload")
    assert lifecycle is not None
    assert lifecycle.state == "reloading"
    await registry.unregister("before")

    disconnected = await registry.get_lifecycle("hash")
    assert disconnected is not None
    assert disconnected.state == "reloading"
    assert disconnected.session_id is None
    assert disconnected.reason == "assembly_reload"

    waiter = asyncio.create_task(
        registry.wait_for_session_id_by_hash("hash", timeout=1.0)
    )
    await asyncio.sleep(0)
    assert not waiter.done()

    await registry.register(
        session_id="after",
        project_name="Project",
        project_hash="hash",
        unity_version="6000.3.9f1",
        runtime=runtime,
    )

    assert await waiter == "after"
    ready = await registry.get_lifecycle("hash")
    assert ready is not None
    assert ready.state == "ready"
    assert ready.session_id == "after"


@pytest.mark.asyncio
async def test_reload_aware_unannounced_disconnect_fails_without_wait(monkeypatch):
    registry = PluginRegistry()
    await registry.register(
        session_id="before",
        project_name="Project",
        project_hash="hash",
        unity_version="6000.3.9f1",
        runtime=reload_aware_runtime(),
    )
    await registry.unregister("before")
    PluginHub.configure(registry, asyncio.get_running_loop())
    monkeypatch.setenv("UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S", "1.0")

    started = time.monotonic()
    try:
        with pytest.raises(NoUnitySessionError) as exc_info:
            await PluginHub._resolve_session_id("Project@hash")
    finally:
        reset_plugin_hub()

    assert exc_info.value.reason == "unity_disconnected"
    assert time.monotonic() - started < 0.1


@pytest.mark.asyncio
async def test_stale_reload_marker_does_not_restart_wait_budget(monkeypatch):
    registry = PluginRegistry()
    await registry.register(
        session_id="before",
        project_name="Project",
        project_hash="hash",
        unity_version="6000.3.9f1",
        runtime=reload_aware_runtime(),
    )
    await registry.mark_reloading("before", "assembly_reload")
    await registry.unregister("before")
    lifecycle = await registry.get_lifecycle("hash")
    assert lifecycle is not None
    lifecycle.updated_at = datetime.now(timezone.utc) - timedelta(seconds=2)

    PluginHub.configure(registry, asyncio.get_running_loop())
    monkeypatch.setenv("UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S", "1.0")
    started = time.monotonic()
    try:
        with pytest.raises(NoUnitySessionError) as exc_info:
            await PluginHub._resolve_session_id("Project@hash")
    finally:
        reset_plugin_hub()

    assert exc_info.value.reason == "reload_timeout"
    assert time.monotonic() - started < 0.1


@pytest.mark.asyncio
async def test_plugin_lifecycle_message_marks_registered_connection_reloading():
    registry = PluginRegistry()
    PluginHub.configure(registry, asyncio.get_running_loop())
    websocket = AsyncMock()
    websocket.state = SimpleNamespace(user_id=None)
    await registry.register(
        session_id="session",
        project_name="Project",
        project_hash="hash",
        unity_version="6000.3.9f1",
        runtime=reload_aware_runtime(),
    )
    PluginHub._connections["session"] = websocket
    hub = object.__new__(PluginHub)

    try:
        await hub._handle_lifecycle(
            websocket,
            PluginLifecycleMessage(
                state="reloading",
                session_id="session",
                reason="assembly_reload",
            ),
        )
        lifecycle = await registry.get_lifecycle("hash")
    finally:
        reset_plugin_hub()

    assert lifecycle is not None
    assert lifecycle.state == "reloading"
    assert lifecycle.reason == "assembly_reload"


@pytest.mark.asyncio
async def test_inline_instance_resolution_preserves_announced_reload_target(monkeypatch):
    from core.config import config

    registry = PluginRegistry()
    await registry.register(
        session_id="before",
        project_name="Project",
        project_hash="abcdef123456",
        unity_version="6000.3.9f1",
        runtime=reload_aware_runtime(),
    )
    await registry.mark_reloading("before", "assembly_reload")
    await registry.unregister("before")
    PluginHub.configure(registry, asyncio.get_running_loop())
    monkeypatch.setattr(config, "transport_mode", "http")
    monkeypatch.setattr(config, "http_remote_hosted", False)
    middleware = UnityInstanceMiddleware()
    context = StateContext()

    try:
        assert await middleware._resolve_instance_value(
            "Project@abcdef123456", context
        ) == "Project@abcdef123456"
        assert await middleware._resolve_instance_value(
            "abcdef", context
        ) == "Project@abcdef123456"
        with pytest.raises(ValueError, match="not found"):
            await middleware._resolve_instance_value(
                "WrongProject@abcdef123456", context
            )
    finally:
        reset_plugin_hub()
