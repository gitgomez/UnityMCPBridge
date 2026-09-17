import pytest

from models import MCPResponse
from services.state.external_changes_scanner import external_changes_scanner
from services.state.external_changes_scanner import ExternalChangesState

from .test_helpers import DummyContext


@pytest.mark.asyncio
async def test_refresh_unity_recovers_from_retry_disconnect(monkeypatch):
    """
    Option A: if Unity disconnects and the transport returns hint=retry, refresh_unity(wait_for_ready=true)
    should poll readiness and then return success + clear external dirty.
    """
    from services.tools.refresh_unity import refresh_unity

    ctx = DummyContext()
    await ctx.set_state("unity_instance", "UnityMCPTests@cc8756d4cce0805a")

    # Seed dirty state
    inst = "UnityMCPTests@cc8756d4cce0805a"
    external_changes_scanner._states[inst] = ExternalChangesState(dirty=True, dirty_since_unix_ms=1)

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        if command_type == "refresh_unity":
            return {"success": False, "error": "disconnected", "hint": "retry"}
        elif command_type == "get_editor_state":
            return {"success": True, "data": {"advice": {"ready_for_tools": True}}}
        raise ValueError(f"Unexpected command: {command_type}")

    import services.tools.refresh_unity as refresh_mod
    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await refresh_unity(ctx, wait_for_ready=True)
    payload = resp.model_dump() if hasattr(resp, "model_dump") else resp
    assert payload["success"] is True
    assert payload.get("data", {}).get("recovered_from_disconnect") is True

    # Dirty should be cleared
    assert external_changes_scanner._states[inst].dirty is False


@pytest.mark.asyncio
async def test_wait_for_ready_reports_final_observed_state(monkeypatch):
    """A pre-reload Unity response must not leak its stale compiling state."""
    from services.tools.refresh_unity import refresh_unity
    import services.tools.refresh_unity as refresh_mod

    ctx = DummyContext()
    await ctx.set_state("unity_instance", "UnityMCPTests@cc8756d4cce0805a")

    async def fake_send_with_unity_instance(*args, **kwargs):
        return {
            "success": True,
            "message": "Refresh requested.",
            "data": {
                "refresh_triggered": False,
                "compile_requested": True,
                "resulting_state": "compiling",
                "hint": "Poll editor_state until ready.",
            },
        }

    final_state = {
        "sequence": 7,
        "observed_at_unix_ms": 123456,
        "activity": {"phase": "idle"},
        "advice": {"ready_for_tools": True, "blocking_reasons": []},
    }

    async def fake_wait_for_editor_ready_state(*args, **kwargs):
        return (True, 1.23456, final_state)

    monkeypatch.setattr(
        refresh_mod.unity_transport,
        "send_with_unity_instance",
        fake_send_with_unity_instance,
    )
    monkeypatch.setattr(
        refresh_mod,
        "_wait_for_editor_ready_state",
        fake_wait_for_editor_ready_state,
    )

    response = await refresh_unity(
        ctx,
        scope="scripts",
        compile="request",
        wait_for_ready=True,
    )
    payload = response.model_dump()

    assert payload["success"] is True
    assert payload["message"] == "Refresh completed; editor readiness was confirmed."
    assert payload["hint"] == "Unity is ready for tool calls."
    assert payload["data"] == {
        "refresh_triggered": False,
        "compile_requested": True,
        "resulting_state": "idle",
        "ready_for_tools": True,
        "blocking_reasons": [],
        "wait_for_ready": True,
        "wait_seconds": 1.235,
        "editor_state_sequence": 7,
        "editor_state_observed_at_unix_ms": 123456,
        "hint": "Unity is ready for tool calls.",
    }


@pytest.mark.asyncio
async def test_wait_for_ready_timeout_reports_last_state_without_retrying(monkeypatch):
    from services.tools.refresh_unity import refresh_unity
    import services.tools.refresh_unity as refresh_mod

    ctx = DummyContext()
    await ctx.set_state("unity_instance", "UnityMCPTests@cc8756d4cce0805a")

    async def fake_send_with_unity_instance(*args, **kwargs):
        return {
            "success": True,
            "data": {
                "refresh_triggered": False,
                "compile_requested": True,
                "resulting_state": "compiling",
            },
        }

    last_state = {
        "sequence": 22,
        "activity": {"phase": "compiling"},
        "advice": {
            "ready_for_tools": False,
            "blocking_reasons": ["compiling"],
        },
    }

    async def fake_wait_for_editor_ready_state(*args, **kwargs):
        return (False, 60.001, last_state)

    monkeypatch.setattr(
        refresh_mod.unity_transport,
        "send_with_unity_instance",
        fake_send_with_unity_instance,
    )
    monkeypatch.setattr(
        refresh_mod,
        "_wait_for_editor_ready_state",
        fake_wait_for_editor_ready_state,
    )

    response = await refresh_unity(
        ctx,
        scope="scripts",
        compile="request",
        wait_for_ready=True,
    )
    payload = response.model_dump()

    assert payload["success"] is False
    assert payload["error"] == "editor_readiness_timeout"
    assert payload["code"] == "editor_readiness_timeout"
    assert payload["hint"] is None
    assert payload["data"]["resulting_state"] == "compiling"
    assert payload["data"]["blocking_reasons"] == ["compiling"]
    assert payload["data"]["editor_state_sequence"] == 22
    assert payload["data"]["safe_to_retry_refresh"] is False
    assert payload["data"]["recommended_next_action"] == "read_editor_state"
    assert payload["data"]["hint"] == (
        "Read editor_state; do not automatically repeat refresh."
    )
