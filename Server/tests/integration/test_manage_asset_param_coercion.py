import asyncio

from .test_helpers import DummyContext
import services.tools.manage_asset as manage_asset_mod


def test_manage_asset_pagination_coercion(monkeypatch):
    captured = {}

    async def fake_async_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(
        manage_asset_mod, "async_send_command_with_retry", fake_async_send)

    result = asyncio.run(
        manage_asset_mod.manage_asset(
            ctx=DummyContext(),
            action="search",
            path="Assets",
            page_size="50",
            page_number="2",
        )
    )

    assert result == {"success": True, "data": {}}
    assert captured["params"]["pageSize"] == 50
    assert captured["params"]["pageNumber"] == 2


def test_manage_asset_search_patterns_are_forwarded_as_or_queries(monkeypatch):
    captured = {}

    async def fake_async_send(cmd, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {}}

    monkeypatch.setattr(
        manage_asset_mod, "async_send_command_with_retry", fake_async_send)

    result = asyncio.run(
        manage_asset_mod.manage_asset(
            ctx=DummyContext(),
            action="search",
            path="Assets",
            search_patterns='["Player", "Enemy"]',
            filter_type="Prefab",
        )
    )

    assert result["success"] is True
    assert captured["params"]["searchPatterns"] == ["Player", "Enemy"]
    assert captured["params"]["filterType"] == "Prefab"
    assert "searchPattern" not in captured["params"]


def test_manage_asset_rejects_ambiguous_search_query(monkeypatch):
    result = asyncio.run(
        manage_asset_mod.manage_asset(
            ctx=DummyContext(),
            action="search",
            path="Assets",
            search_pattern="Player",
            search_patterns=["Enemy"],
        )
    )

    assert result["success"] is False
    assert result["code"] == "ambiguous_search_query"
