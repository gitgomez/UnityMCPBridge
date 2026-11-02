"""Compile evidence, uncertainty and single-receipt recovery regressions."""

import time
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from core.config import config
from services.resources.editor_diagnostics import enrich_diagnostics
from services.state.refresh_observation import try_refresh_runtime
from transport.plugin_hub import PluginHub
from transport.runtime_protocol import build_server_advertisement


@pytest.fixture
def runtime(monkeypatch):
    monkeypatch.setattr(config, "transport_mode", "http")
    monkeypatch.setattr(PluginHub, "_resolve_session_id", AsyncMock(return_value="session"))
    monkeypatch.setattr(PluginHub, "_session_runtime_details", AsyncMock(
        return_value=(set(build_server_advertisement().capabilities), "project-hash")))
    receipt = AsyncMock(return_value={"receipt": {"state": "succeeded"}})
    monkeypatch.setattr(PluginHub, "query_receipt", receipt)
    return SimpleNamespace(ctx=SimpleNamespace(), receipt=receipt)


def observation(request_id, *, errors=0, correlation="observed_not_exclusive", main="ticking"):
    return {"success": True, "data": {
        "main_thread": {"status": main},
        "editor_state": {"observed_at_unix_ms": int(time.time() * 1000),
                         "activity": {"phase": "idle"}, "compilation": {}, "assets": {}},
        "compilation": {"requests": [{"request_id": request_id, "request_returned": True,
                                      "correlation": correlation, "candidate_cycle_ids": ["epoch:1"]}],
                        "last_cycle": {"id": "epoch:1", "status": "completed", "capture_complete": True,
                                       "errors": errors, "succeeded": errors == 0}},
    }}


def configure(monkeypatch, *, response=None, errors=0, foreign=False, main="ticking"):
    sent = []
    async def send(session, command, params, **kwargs):
        sent.append((command, params, kwargs))
        return response or {"success": True, "data": {"compile_requested": True, "refresh_triggered": False}}
    async def read(session):
        request_id = "foreign-request" if foreign else sent[0][2]["request_id"]
        return observation(request_id, errors=errors, main=main)
    monkeypatch.setattr(PluginHub, "send_command", send)
    monkeypatch.setattr(PluginHub, "query_editor_diagnostics", read)
    return sent


@pytest.mark.asyncio
@pytest.mark.parametrize("errors,success,code", [(0, True, None), (2, False, "compilation_failed")])
async def test_readiness_is_not_compile_success(runtime, monkeypatch, errors, success, code):
    sent = configure(monkeypatch, errors=errors)
    result = await try_refresh_runtime(runtime.ctx, "project-hash",
        {"scope": "scripts", "compile": "request", "wait_for_ready": True}, deadline=time.monotonic() + 1)
    assert result.success is success
    assert result.code == code
    assert result.data["ready_for_tools"] is True
    assert result.data["compile_succeeded"] is (errors == 0)
    assert result.data["resulting_state"] == "idle"
    assert result.data["compile_request"]["request_returned"] is True
    assert len(sent) == 1 and sent[0][1]["wait_for_ready"] is False


@pytest.mark.asyncio
async def test_reconnect_queries_original_receipt_without_repeating_refresh(runtime, monkeypatch):
    sent = configure(monkeypatch, response={"success": False, "code": "PLUGIN_DISCONNECTED", "error": "disconnected"})
    result = await try_refresh_runtime(runtime.ctx, "project-hash",
        {"compile": "request", "wait_for_ready": True}, deadline=time.monotonic() + 1)
    assert result.success is True
    assert len(sent) == 1
    runtime.receipt.assert_awaited_once_with("session", sent[0][2]["request_id"])


@pytest.mark.asyncio
async def test_unknown_receipt_never_becomes_success_from_ready_or_compile(runtime, monkeypatch):
    sent = configure(monkeypatch, response={"success": False, "code": "PLUGIN_DISCONNECTED", "error": "disconnected"})
    runtime.receipt.return_value = {"receipt": {"state": "outcome_unknown"}}
    result = await try_refresh_runtime(runtime.ctx, "project-hash",
        {"compile": "request", "wait_for_ready": True}, deadline=time.monotonic() + 1)
    assert result.success is False
    assert result.code == "refresh_outcome_unknown"
    assert result.data["ready_for_tools"] is True
    assert result.data["request_outcome"] == "outcome_unknown"
    assert result.data["safe_to_retry_refresh"] is False
    assert len(sent) == 1


@pytest.mark.asyncio
async def test_foreign_compile_and_stalled_main_thread_cannot_finish_wait(runtime, monkeypatch):
    sent = configure(monkeypatch, foreign=True, main="not_ticking")
    started = time.monotonic()
    result = await try_refresh_runtime(runtime.ctx, "project-hash",
        {"compile": "request", "wait_for_ready": True}, deadline=started + 0.02)
    assert result.success is False and result.code == "editor_readiness_timeout"
    assert result.data["compile_cycle_confirmed"] is False
    assert result.data["ready_for_tools"] is False
    assert time.monotonic() - started < 0.2
    assert len(sent) == 1


def test_unknown_heartbeat_and_stale_observation_are_not_all_clear():
    unknown = enrich_diagnostics(observation("receipt", main="unknown"))
    assert unknown["data"]["editor_state"]["advice"]["ready_for_tools"] is False
    stale = observation("receipt")
    stale["data"]["editor_state"]["observed_at_unix_ms"] = 1
    assert enrich_diagnostics(stale)["data"]["editor_state"]["advice"]["ready_for_tools"] is False


@pytest.mark.asyncio
async def test_expired_budget_does_not_send_a_refresh(runtime, monkeypatch):
    sent = configure(monkeypatch)
    result = await try_refresh_runtime(runtime.ctx, "project-hash",
        {"compile": "request", "wait_for_ready": True}, deadline=time.monotonic() - 1)
    assert result.success is False and result.code == "refresh_prechecks_timeout"
    assert result.data["refresh_not_sent"] is True
    assert sent == []


@pytest.mark.asyncio
async def test_legacy_state_read_is_bounded_by_the_remaining_budget(monkeypatch):
    import asyncio
    import services.tools.refresh_unity as refresh
    monkeypatch.setattr(refresh, "_in_pytest", lambda: False)
    async def blocked_read(ctx):
        await asyncio.sleep(10)
    monkeypatch.setattr(refresh.editor_state, "get_editor_state", blocked_read)
    started = time.monotonic()
    ready, _, _ = await refresh._wait_for_editor_ready_state(SimpleNamespace(), timeout_s=0.02)
    assert ready is False
    assert time.monotonic() - started < 0.2


@pytest.mark.asyncio
async def test_legacy_wait_requires_explicit_true_even_without_known_blockers(monkeypatch):
    import services.tools.refresh_unity as refresh
    monkeypatch.setattr(refresh, "_in_pytest", lambda: False)
    monkeypatch.setattr(refresh.editor_state, "get_editor_state", AsyncMock(return_value={
        "success": True, "data": {"advice": {"ready_for_tools": False, "blocking_reasons": []}}}))
    ready, _, _ = await refresh._wait_for_editor_ready_state(SimpleNamespace(), timeout_s=0.01)
    assert ready is False
