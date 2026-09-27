import json
from unittest.mock import patch

import pytest
from click.testing import CliRunner
from cli.commands.editor import editor


@pytest.mark.parametrize("action,options,flags", [
    ("inspect_collection", {"offset": 10, "limit": 5}, []),
    ("reveal_item", {"index": 500}, ["--timeout", "2"]),
    ("reveal_item", {"id": 12, "expand_ancestors": True}, []),
    ("set_collection_expanded", {"id": 10}, ["--value", "false"]),
])
def test_collection_command_forwards(action, options, flags):
    with patch("cli.commands.editor.run_command", return_value={"success": True}) as send:
        result = CliRunner().invoke(editor, ["collection-ui", "--action", action, "--document", "UI",
            "--element-name", "items", "--collection", json.dumps(options), *flags])
    assert result.exit_code == 0, result.output
    params = send.call_args.args[1]
    assert params["action"] == action
    assert params["collection"] == options
    if action == "set_collection_expanded":
        assert params["value"] is False


@pytest.mark.parametrize("command,extra", [
    ("inspect-ui", []), ("click-ui", ["--button", "right"]),
    ("wait-ui", ["--condition", "realized"]),
    ("set-ui-text", ["--text", "new"]), ("set-ui-toggle", ["--value", "false"]),
    ("drag-ui", ["--end-position", "0.5", "0.5"]), ("scroll-ui", ["--delta", "0", "-1"]),
])
def test_existing_commands_accept_scoped_item(command, extra):
    address = {"index": 800, "query": {"element_name": "control"}}
    with patch("cli.commands.editor.run_command", return_value={"success": True}) as send:
        result = CliRunner().invoke(editor, [command, "--ui-system", "ui_toolkit", "--document", "UI",
            "--element-name", "items", "--collection", json.dumps(address), *extra])
    assert result.exit_code == 0, result.output
    assert send.call_args.args[1]["collection"] == address


@pytest.mark.parametrize("options", ['{}', '{"id":1,"index":1}', '{"index":true}', '{"id":1,"query":{}}'])
def test_invalid_collection_does_not_dispatch(options):
    with patch("cli.commands.editor.run_command") as send:
        result = CliRunner().invoke(editor, ["collection-ui", "--action", "reveal_item", "--document", "UI",
            "--element-name", "items", "--collection", options])
    assert result.exit_code != 0
    send.assert_not_called()


@pytest.mark.parametrize("command,extra", [("click-ui", []), ("hover-ui", []), ("drag-ui", ["--end-position", "0.8", "0.1"]), ("scroll-ui", ["--delta", "0", "-1"])])
def test_coordinate_space_is_forwarded(command, extra):
    flags = [] if command == "hover-ui" else ["--ui-system", "ui_toolkit"]
    with patch("cli.commands.editor.run_command", return_value={"success": True}) as send:
        result = CliRunner().invoke(editor, [command, *flags, "--document", "TextureUI",
            "--position", "0.3", "0.7", "--coordinate-space", "texture_uv", *extra])
    assert result.exit_code == 0, result.output
    assert send.call_args.args[1]["coordinate_space"] == "texture_uv"


@pytest.mark.parametrize("command,extra", [("click-ui", []), ("hover-ui", []),
    ("drag-ui", ["--end-position", "0.8", "0.1"]), ("scroll-ui", ["--delta", "0", "-1"])])
def test_camera_surface_is_forwarded(command, extra):
    flags = [] if command == "hover-ui" else ["--ui-system", "ui_toolkit"]
    surface = {"camera": "Cam", "target": "Screen"}
    with patch("cli.commands.editor.run_command", return_value={"success": True}) as send:
        result = CliRunner().invoke(editor, [command, *flags, "--document", "UI",
            "--position", "0.3", "0.7", "--coordinate-space", "camera_viewport", "--surface", json.dumps(surface), *extra])
    assert result.exit_code == 0, result.output
    assert send.call_args.args[1]["surface"] == surface


@pytest.mark.parametrize("surface", ['{}', 'null', '[]', 'bad json', '{"camera":"Cam","target":"Screen","extra":1}'])
def test_cli_rejects_invalid_surface_before_dispatch(surface):
    with patch("cli.commands.editor.run_command") as send:
        result = CliRunner().invoke(editor, ["hover-ui", "--document", "UI", "--position", "0.3", "0.7",
            "--coordinate-space", "camera_viewport", "--surface", surface])
    assert result.exit_code != 0
    send.assert_not_called()
