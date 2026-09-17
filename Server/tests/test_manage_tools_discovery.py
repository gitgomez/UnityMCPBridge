import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

from models.models import ToolDefinitionModel
from services.resources import custom_tools as custom_tools_resource
from services.tools.manage_tools import manage_tools
import services.tools.manage_camera  # noqa: F401 - populate discovery registry
import services.tools.manage_profiler  # noqa: F401 - populate discovery registry


class DiscoveryContext:
    async def _get_visibility_rules(self):
        return []


def test_find_discovers_hidden_tool_and_returns_activation_payload():
    result = asyncio.run(manage_tools(
        DiscoveryContext(),
        action="find",
        query="profiler memory",
    ))

    assert result["success"] is True
    profiler = next(
        tool for tool in result["tools"]
        if tool["name"] == "manage_profiler"
    )
    assert profiler["group"] == "profiling"
    assert profiler["enabled"] is False
    assert profiler["activation"] == {
        "tool": "manage_tools",
        "action": "activate",
        "group": "profiling",
    }


def test_find_reports_parameters_for_matching_tool():
    result = asyncio.run(manage_tools(
        DiscoveryContext(),
        action="find",
        query="screenshot camera",
    ))

    assert result["success"] is True
    camera = next(
        tool for tool in result["tools"]
        if tool["name"] == "manage_camera"
    )
    assert "action" in camera["required_parameters"]
    assert "include_image" in camera["parameters"]


def test_custom_tools_resource_excludes_builtin_registrations(monkeypatch):
    service = SimpleNamespace(
        list_registered_tools=AsyncMock(return_value=[
            ToolDefinitionModel(
                name="manage_scene",
                is_built_in=True,
            ),
            ToolDefinitionModel(
                name="project_spawn_fleet",
                description="Spawn a project fleet",
                is_built_in=False,
            ),
        ])
    )
    monkeypatch.setattr(
        custom_tools_resource.CustomToolService,
        "get_instance",
        lambda: service,
    )
    monkeypatch.setattr(
        custom_tools_resource,
        "get_unity_instance_from_context",
        AsyncMock(return_value="Project@123"),
    )
    monkeypatch.setattr(
        custom_tools_resource,
        "resolve_project_id_for_unity_instance",
        lambda instance: "123",
    )
    monkeypatch.setattr(
        custom_tools_resource,
        "get_user_id_from_context",
        AsyncMock(return_value=None),
    )

    result = asyncio.run(custom_tools_resource.get_custom_tools(
        SimpleNamespace(),
    ))

    assert result.success is True
    assert result.data.tool_count == 1
    assert result.data.tools[0].name == "project_spawn_fleet"
