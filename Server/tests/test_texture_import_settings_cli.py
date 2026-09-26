"""Texture import settings retain values and reject invalid input before CLI dispatch."""

import json
from unittest.mock import patch

import pytest
from click.testing import CliRunner

from cli.main import cli
from cli.commands.texture import _normalize_import_settings

SETTINGS = {
    "textureType": "Default", "sRGBTexture": True, "alphaSource": "FromInput",
    "alphaIsTransparency": True, "wrapMode": "Clamp", "filterMode": "Bilinear",
    "maxTextureSize": 2048, "textureCompression": "Uncompressed",
    "mipmapEnabled": False, "isReadable": False, "npotScale": "None",
}


@pytest.mark.parametrize("command", ["create", "modify", "set-import-settings"])
def test_reported_dictionary_reaches_cli_transport_unchanged(command):
    with patch("cli.commands.texture.run_command", return_value={"success": True}) as send:
        result = CliRunner().invoke(cli, [
            "texture", command, "Assets/Textures/Belt.png",
            "--import-settings", json.dumps(SETTINGS),
        ])
    assert result.exit_code == 0, result.output
    assert send.call_args.args[1]["importSettings"] == SETTINGS


@pytest.mark.parametrize("command", ["create", "modify", "set-import-settings"])
@pytest.mark.parametrize("settings, expected", [
    ({"srgb": True, "unknown": 1}, "Unsupported import_settings keys"),
    ({"wrap_mode": "clamp", "wrapMode": "Repeat"}, "Conflicting import_settings keys"),
    ({"isReadable": {}}, "readable must be a boolean"),
    ({"npotScale": "unknown"}, "Invalid npot_scale"),
])
def test_invalid_dictionary_is_rejected_before_cli_transport(command, settings, expected):
    with patch("cli.commands.texture.run_command") as send:
        result = CliRunner().invoke(cli, [
            "texture", command, "Assets/Textures/Belt.png",
            "--import-settings", json.dumps(settings),
        ])
    assert result.exit_code == 1
    assert expected in result.output
    send.assert_not_called()


def test_json_and_flag_conflict_is_rejected():
    with patch("cli.commands.texture.run_command") as send:
        result = CliRunner().invoke(cli, [
            "texture", "set-import-settings", "Assets/Textures/Belt.png",
            "--import-settings", '{"mipmapEnabled": true}', "--no-mipmaps",
        ])
    assert result.exit_code == 1
    assert "Conflicting import_settings and flag values" in result.output
    send.assert_not_called()


def test_npot_flag_and_false_flags_are_preserved():
    with patch("cli.commands.texture.run_command", return_value={"success": True}) as send:
        result = CliRunner().invoke(cli, [
            "texture", "set-import-settings", "Assets/Textures/Belt.png",
            "--npot-scale", "to_nearest", "--no-mipmaps", "--no-readable",
        ])
    assert result.exit_code == 0, result.output
    assert send.call_args.args[1]["importSettings"] == {
        "npotScale": "ToNearest", "mipmapEnabled": False, "isReadable": False,
    }


def test_cli_and_mcp_use_the_same_validated_aliases():
    assert _normalize_import_settings({
        "npot_scale": "none", "generate_mipmaps": "false", "readable": False,
    }) == {"npotScale": "None", "mipmapEnabled": False, "isReadable": False}
