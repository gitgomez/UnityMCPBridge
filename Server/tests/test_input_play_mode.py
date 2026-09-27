import asyncio
import json
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

import pytest
from click.testing import CliRunner
from cli.commands.editor import editor
from services.tools.input_play_mode import input_play_mode, validate_input_parameters


VALID = [
    {"action": "status"},
    {"action": "key", "keys": ["Space", "LeftShift"], "duration_seconds": .05},
    {"action": "move", "position": [0, 1]},
    {"action": "move", "delta": [-12, 5]},
    {"action": "click", "position": [.3, .7], "button": "middle"},
    {"action": "drag", "position": [0, 0], "end_position": [1, 1], "steps": 64, "duration_seconds": 5},
    {"action": "scroll", "position": [.5, .5], "scroll_delta": [0, -100], "keys": ["LeftCtrl"]},
    {"action": "cancel", "operation_id": "a" * 32},
]
INVALID = [
    {"action": "key"}, {"action": "key", "keys": []},
    {"action": "key", "keys": ["Space", "Space"]},
    {"action": "key", "keys": ["a"]}, {"action": "key", "keys": ["None"]},
    {"action": "key", "keys": [True]}, {"action": "key", "keys": list("ABCDEFGHI")},
    {"action": "move"}, {"action": "move", "position": [0, 1], "delta": [1, 1]},
    {"action": "move", "position": [False, 1]}, {"action": "move", "position": [float("nan"), 0]},
    {"action": "move", "delta": [float("inf"), 0]}, {"action": "move", "delta": [4097, 0]},
    {"action": "click", "position": [0, 2]}, {"action": "click", "position": [0, 0], "button": "back"},
    {"action": "key", "keys": ["W"], "duration_seconds": True},
    {"action": "key", "keys": ["W"], "duration_seconds": 5.01},
    {"action": "key", "keys": ["W"], "duration_seconds": 0},
    {"action": "drag", "position": [0, 0]},
    {"action": "drag", "position": [0, 0], "end_position": [1, 1], "steps": True},
    {"action": "drag", "position": [0, 0], "end_position": [1, 1], "steps": 0},
    {"action": "scroll", "position": [0, 0], "scroll_delta": [0, 0]},
    {"action": "scroll", "position": [0, 0], "scroll_delta": [0, 101]},
    {"action": "status", "keys": ["Space"]}, {"action": "cancel"},
    {"action": "cancel", "operation_id": "invalid"},
    {"action": "move", "position": [0, 0], "duration_seconds": .1},
]


@pytest.mark.parametrize("params", VALID)
def test_tool_forwards_once(params):
    with patch("services.tools.input_play_mode.get_unity_instance_from_context", AsyncMock(return_value="instance")), \
         patch("services.tools.input_play_mode.send_with_unity_instance", AsyncMock(return_value={"success": True})) as send:
        assert asyncio.run(input_play_mode(SimpleNamespace(), **params))["success"]
    assert send.await_count == 1
    assert send.call_args.args[1:] == ("instance", "input_play_mode", params)


@pytest.mark.parametrize("params", INVALID)
def test_invalid_never_dispatches(params):
    assert validate_input_parameters(params)
    with patch("services.tools.input_play_mode.send_with_unity_instance", AsyncMock()) as send:
        assert not asyncio.run(input_play_mode(SimpleNamespace(), **params))["success"]
    send.assert_not_called()


@pytest.mark.parametrize("params", VALID)
def test_cli_parity(params):
    args = ["input", params["action"]]
    for name, value in params.items():
        if name == "action":
            continue
        if name == "keys":
            for key in value:
                args += ["--key", key]
        else:
            args.append("--duration" if name == "duration_seconds" else "--" + name.replace("_", "-"))
            args.extend(str(v) for v in value) if isinstance(value, list) else args.append(str(value))
    with patch("cli.commands.editor.run_command", return_value={"success": True}) as send:
        result = CliRunner().invoke(editor, args)
    assert result.exit_code == 0, result.output
    sent = send.call_args.args[1]
    assert {k: list(v) if isinstance(v, tuple) else v for k, v in sent.items()} == params


@pytest.mark.parametrize("args", [
    ["key"], ["key", "--key", "Space", "--duration", "nan"],
    ["status", "--position", "0", "0"], ["cancel"],
    ["drag", "--position", "0", "0", "--end-position", "1", "1", "--steps", "65"],
])
def test_cli_invalid_never_dispatches(args):
    with patch("cli.commands.editor.run_command") as send:
        result = CliRunner().invoke(editor, ["input", *args])
    assert result.exit_code != 0
    send.assert_not_called()


@pytest.mark.parametrize("params", [
    {"action": "key", "keys": ["Space"], "duration_seconds": True},
    {"action": "move", "position": [False, 1]},
    {"action": "drag", "position": [0, 0], "end_position": [1, 1], "steps": True},
])
def test_mcp_schema_does_not_coerce_booleans_to_numbers(params):
    # integration/conftest.py installs global FastMCP stubs during collection.
    # Exercise the real MCP schema in an isolated interpreter in both full-suite
    # and targeted runs; never mistake a stub for protocol-level validation.
    script = """
import asyncio
import json
import sys
from unittest.mock import AsyncMock, patch
sys.path.insert(0, 'src')
from fastmcp import Client, FastMCP
from services.tools.input_play_mode import input_play_mode

async def invoke():
    app = FastMCP('input-validation')
    app.tool(input_play_mode)
    async with Client(app) as client:
        result = await client.call_tool('input_play_mode', json.loads(sys.argv[1]), raise_on_error=False)
        assert result.is_error

with patch('services.tools.input_play_mode.send_with_unity_instance', AsyncMock()) as send:
    asyncio.run(invoke())
    send.assert_not_called()
"""
    result = subprocess.run(
        [sys.executable, "-c", script, json.dumps(params)],
        cwd=Path(__file__).resolve().parents[1],
        capture_output=True, text=True, timeout=30,
    )
    assert result.returncode == 0, result.stdout + result.stderr
