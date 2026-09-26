"""Texture CLI commands."""

import sys
import click
from typing import Optional, Any

from cli.utils.config import get_config
from cli.utils.output import (
    format_output,
    print_error,
    print_success,
    print_warning,
)
from cli.utils.connection import run_command, handle_unity_errors
from cli.utils.parsers import parse_json_or_exit as try_parse_json


from services.tools.texture_import_settings import (
    TEXTURE_TYPES as _TEXTURE_TYPES,
    SPRITE_MODES as _SPRITE_MODES,
    COMPRESSIONS as _COMPRESSIONS,
    NPOT_SCALES,
    normalize_import_settings,
)

_MAX_TEXTURE_DIMENSION = 1024
_MAX_TEXTURE_PIXELS = 1024 * 1024


def _validate_texture_dimensions(width: int, height: int) -> list[str]:
    if width <= 0 or height <= 0:
        raise ValueError("width and height must be positive")
    warnings: list[str] = []
    if width > _MAX_TEXTURE_DIMENSION or height > _MAX_TEXTURE_DIMENSION:
        warnings.append(
            f"width and height should be <= {_MAX_TEXTURE_DIMENSION} (got {width}x{height})")
    total_pixels = width * height
    if total_pixels > _MAX_TEXTURE_PIXELS:
        warnings.append(
            f"width*height should be <= {_MAX_TEXTURE_PIXELS} (got {width}x{height})")
    return warnings


def _is_normalized_color(values: list[Any]) -> bool:
    if not values:
        return False

    try:
        numeric_values = [float(v) for v in values]
    except (TypeError, ValueError):
        return False

    all_small = all(0 <= v <= 1.0 for v in numeric_values)
    if not all_small:
        return False

    has_fractional = any(0 < v < 1 for v in numeric_values)
    all_binary = all(v in (0, 1, 0.0, 1.0) for v in numeric_values)

    return has_fractional or all_binary


def _parse_hex_color(value: str) -> list[int]:
    h = value.lstrip("#")
    if len(h) == 6:
        return [int(h[i:i + 2], 16) for i in (0, 2, 4)] + [255]
    if len(h) == 8:
        return [int(h[i:i + 2], 16) for i in (0, 2, 4, 6)]
    raise ValueError(f"Invalid hex color: {value}")


def _normalize_color(value: Any, context: str) -> list[int]:
    if value is None:
        raise ValueError(f"{context} is required")

    if isinstance(value, str):
        if value.startswith("#"):
            return _parse_hex_color(value)
        value = try_parse_json(value, context)

    # Handle dict with r/g/b keys (e.g., {"r": 1, "g": 0, "b": 0} or {"r": 1, "g": 0, "b": 0, "a": 1})
    if isinstance(value, dict):
        if all(k in value for k in ("r", "g", "b")):
            try:
                color = [value["r"], value["g"], value["b"]]
                if "a" in value:
                    color.append(value["a"])
                else:
                    color.append(1.0 if _is_normalized_color(color) else 255)
                if _is_normalized_color(color):
                    return [int(round(float(c) * 255)) for c in color]
                return [int(c) for c in color]
            except (TypeError, ValueError):
                raise ValueError(f"{context} dict values must be numeric, got {value}")
        raise ValueError(f"{context} dict must have 'r', 'g', 'b' keys, got {list(value.keys())}")

    if isinstance(value, (list, tuple)):
        if len(value) == 3:
            value = list(value) + [1.0 if _is_normalized_color(value) else 255]
        if len(value) == 4:
            try:
                if _is_normalized_color(value):
                    return [int(round(float(c) * 255)) for c in value]
                return [int(c) for c in value]
            except (TypeError, ValueError):
                raise ValueError(
                    f"{context} values must be numeric, got {value}")
        raise ValueError(
            f"{context} must have 3 or 4 components, got {len(value)}")

    raise ValueError(f"{context} must be a list or hex string")


def _normalize_palette(value: Any, context: str) -> list[list[int]]:
    if value is None:
        return []
    if isinstance(value, str):
        value = try_parse_json(value, context)
    if not isinstance(value, list):
        raise ValueError(f"{context} must be a list of colors")
    return [_normalize_color(color, f"{context} item") for color in value]


def _normalize_pixels(value: Any, width: int, height: int, context: str) -> list[list[int]] | str:
    if value is None:
        raise ValueError(f"{context} is required")
    if isinstance(value, str):
        if value.startswith("base64:"):
            return value
        trimmed = value.strip()
        if trimmed.startswith("[") and trimmed.endswith("]"):
            value = try_parse_json(trimmed, context)
        else:
            return f"base64:{value}"
    if isinstance(value, list):
        expected_count = width * height
        if len(value) != expected_count:
            raise ValueError(
                f"{context} must have {expected_count} entries, got {len(value)}")
        return [_normalize_color(pixel, f"{context} pixel") for pixel in value]
    raise ValueError(f"{context} must be a list or base64 string")


def _normalize_set_pixels(value: Any) -> dict[str, Any]:
    if value is None:
        raise ValueError("set-pixels is required")
    if isinstance(value, str):
        value = try_parse_json(value, "set-pixels")
    if not isinstance(value, dict):
        raise ValueError("set-pixels must be a JSON object")

    result: dict[str, Any] = dict(value)

    if "pixels" in value:
        width = value.get("width")
        height = value.get("height")
        if width is None or height is None:
            raise ValueError(
                "set-pixels requires width and height when pixels are provided")
        width = int(width)
        height = int(height)
        if width <= 0 or height <= 0:
            raise ValueError("set-pixels width and height must be positive")
        result["width"] = width
        result["height"] = height
        result["pixels"] = _normalize_pixels(
            value["pixels"], width, height, "set-pixels pixels")

    if "color" in value:
        result["color"] = _normalize_color(value["color"], "set-pixels color")

    if "pixels" not in value and "color" not in value:
        raise ValueError("set-pixels requires 'color' or 'pixels'")

    if "x" in value:
        result["x"] = int(value["x"])
    if "y" in value:
        result["y"] = int(value["y"])

    if "width" in value and "pixels" not in value:
        result["width"] = int(value["width"])
    if "height" in value and "pixels" not in value:
        result["height"] = int(value["height"])

    return result


def _normalize_import_settings(value: Any) -> dict[str, Any]:
    if isinstance(value, str):
        value = try_parse_json(value, "import_settings")
    settings, error = normalize_import_settings(value)
    if error:
        raise ValueError(error)
    return settings or {}


@click.group()
def texture():
    """Texture operations - create, modify, generate sprites."""
    pass


@texture.command("create")
@click.argument("path")
@click.option("--width", default=64, help="Texture width (default: 64)")
@click.option("--height", default=64, help="Texture height (default: 64)")
@click.option("--image-path", help="Source image path (PNG/JPG) to import.")
@click.option("--color", help="Fill color (e.g., '#FF0000' or '[1,0,0,1]')")
@click.option("--pattern", type=click.Choice([
    "checkerboard", "stripes", "stripes_h", "stripes_v", "stripes_diag",
    "dots", "grid", "brick"
]), help="Pattern type")
@click.option("--palette", help="Color palette for pattern (JSON array of colors)")
@click.option("--import-settings", help="TextureImporter settings (JSON)")
@handle_unity_errors
def create(path: str, width: int, height: int, image_path: Optional[str], color: Optional[str],
           pattern: Optional[str], palette: Optional[str], import_settings: Optional[str]):
    """Create a new procedural texture.

    \b
    Examples:
        unity-mcp texture create Assets/Red.png --color '[255,0,0]'
        unity-mcp texture create Assets/Check.png --pattern checkerboard
        unity-mcp texture create Assets/UI.png --import-settings '{"texture_type": "sprite"}'
    """
    config = get_config()
    if image_path:
        if color or pattern or palette:
            print_error(
                "image-path cannot be combined with color, pattern, or palette.")
            sys.exit(1)
    else:
        try:
            warnings = _validate_texture_dimensions(width, height)
        except ValueError as e:
            print_error(str(e))
            sys.exit(1)
        for warning in warnings:
            print_warning(warning)

    params: dict[str, Any] = {
        "action": "create",
        "path": path,
        "width": width,
        "height": height,
    }

    if color:
        try:
            params["fillColor"] = _normalize_color(color, "color")
        except ValueError as e:
            print_error(str(e))
            sys.exit(1)
    elif not pattern and not image_path:
        # Default to white if no color or pattern specified
        params["fillColor"] = [255, 255, 255, 255]

    if pattern:
        params["pattern"] = pattern

    if palette:
        try:
            params["palette"] = _normalize_palette(palette, "palette")
        except ValueError as e:
            print_error(str(e))
            sys.exit(1)

    if import_settings:
        try:
            params["importSettings"] = _normalize_import_settings(
                import_settings)
        except ValueError as e:
            print_error(str(e))
            sys.exit(1)

    if image_path:
        params["imagePath"] = image_path

    result = run_command("manage_texture", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Created texture: {path}")


@texture.command("sprite")
@click.argument("path")
@click.option("--width", default=64, help="Texture width (default: 64)")
@click.option("--height", default=64, help="Texture height (default: 64)")
@click.option("--image-path", help="Source image path (PNG/JPG) to import.")
@click.option("--color", help="Fill color (e.g., '#FF0000' or '[1,0,0,1]')")
@click.option("--pattern", type=click.Choice([
    "checkerboard", "stripes", "dots", "grid"
]), help="Pattern type (defaults to checkerboard if no color specified)")
@click.option("--ppu", default=100.0, help="Pixels Per Unit")
@click.option("--pivot", help="Pivot as [x,y] (default: [0.5, 0.5])")
@handle_unity_errors
def sprite(path: str, width: int, height: int, image_path: Optional[str], color: Optional[str], pattern: Optional[str], ppu: float, pivot: Optional[str]):
    """Quickly create a sprite texture.

    \b
    Examples:
        unity-mcp texture sprite Assets/Sprites/Player.png
        unity-mcp texture sprite Assets/Sprites/Coin.png --pattern dots
        unity-mcp texture sprite Assets/Sprites/Solid.png --color '[0,255,0]'
    """
    config = get_config()
    if image_path:
        if color or pattern:
            print_error("image-path cannot be combined with color or pattern.")
            sys.exit(1)
    else:
        try:
            warnings = _validate_texture_dimensions(width, height)
        except ValueError as e:
            print_error(str(e))
            sys.exit(1)
        for warning in warnings:
            print_warning(warning)

    sprite_settings: dict[str, Any] = {"pixelsPerUnit": ppu}
    if pivot:
        sprite_settings["pivot"] = try_parse_json(pivot, "pivot")
    else:
        sprite_settings["pivot"] = [0.5, 0.5]

    params: dict[str, Any] = {
        "action": "create_sprite",
        "path": path,
        "width": width,
        "height": height,
        "spriteSettings": sprite_settings
    }

    if color:
        try:
            params["fillColor"] = _normalize_color(color, "color")
        except ValueError as e:
            print_error(str(e))
            sys.exit(1)

    # Only default pattern if no color is specified
    if pattern:
        params["pattern"] = pattern
    elif not color and not image_path:
        params["pattern"] = "checkerboard"

    if image_path:
        params["imagePath"] = image_path

    result = run_command("manage_texture", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Created sprite: {path}")


def _build_import_settings_from_flags(
    texture_type: Optional[str],
    sprite_mode: Optional[str],
    sprite_ppu: Optional[float],
    max_size: Optional[str],
    compression: Optional[str],
    generate_mipmaps: Optional[bool],
    srgb: Optional[bool],
    readable: Optional[bool],
    npot_scale: Optional[str] = None,
) -> dict[str, Any]:
    """Build importSettings dict from CLI flags. Returns empty dict if no flags set."""
    import_settings: dict[str, Any] = {}
    if texture_type:
        import_settings["textureType"] = _TEXTURE_TYPES[texture_type]
    if sprite_mode:
        import_settings["spriteImportMode"] = _SPRITE_MODES[sprite_mode]
    if sprite_ppu is not None:
        import_settings["spritePixelsPerUnit"] = sprite_ppu
    if max_size:
        import_settings["maxTextureSize"] = int(max_size)
    if compression:
        import_settings["textureCompression"] = _COMPRESSIONS[compression]
    if generate_mipmaps is not None:
        import_settings["mipmapEnabled"] = generate_mipmaps
    if srgb is not None:
        import_settings["sRGBTexture"] = srgb
    if readable is not None:
        import_settings["isReadable"] = readable
    if npot_scale is not None:
        import_settings["npotScale"] = npot_scale
    return _normalize_import_settings(import_settings)


def _apply_import_flags_to_params(
    params: dict[str, Any],
    texture_type: Optional[str],
    sprite_mode: Optional[str],
    sprite_ppu: Optional[float],
    max_size: Optional[str],
    compression: Optional[str],
    generate_mipmaps: Optional[bool],
    srgb: Optional[bool],
    readable: Optional[bool],
    as_sprite: bool,
    import_settings: Optional[str] = None,
    npot_scale: Optional[str] = None,
) -> bool:
    """Validate and apply import-setting flags to params dict. Returns True if any import setting present."""
    has_other_flags = any(v is not None for v in (
        texture_type, sprite_mode, sprite_ppu, max_size, compression, generate_mipmaps, srgb, readable,
        import_settings, npot_scale))

    if as_sprite:
        if has_other_flags:
            print_error("--as-sprite cannot be combined with other import-setting flags")
            sys.exit(1)
        params["spriteSettings"] = {"pivot": [0.5, 0.5], "pixelsPerUnit": 100}
        return True

    if has_other_flags:
        try:
            settings = _normalize_import_settings(import_settings)
            flags = _build_import_settings_from_flags(
                texture_type, sprite_mode, sprite_ppu, max_size, compression,
                generate_mipmaps, srgb, readable, npot_scale)
            for key, value in flags.items():
                if key in settings and settings[key] != value:
                    raise ValueError(f"Conflicting import_settings and flag values for {key}")
            settings.update(flags)
        except ValueError as exc:
            print_error(str(exc))
            sys.exit(1)
        if settings:
            params["importSettings"] = settings
        return bool(settings)

    return False


@texture.command("modify")
@click.argument("path")
@click.option("--set-pixels", default=None, help="Modification args as JSON")
@click.option("--import-settings", default=None, help="Importer settings JSON; snake_case or Unity property names")
@click.option("--npot-scale", type=click.Choice(list(NPOT_SCALES)), help="Non-power-of-two texture scaling")
@click.option("--texture-type", type=click.Choice(list(_TEXTURE_TYPES.keys())), help="Texture type")
@click.option("--sprite-mode", type=click.Choice(list(_SPRITE_MODES.keys())), help="Sprite import mode")
@click.option("--sprite-ppu", type=float, help="Sprite pixels per unit")
@click.option("--max-size", type=click.Choice(["32", "64", "128", "256", "512", "1024", "2048", "4096", "8192", "16384"]), help="Max texture size")
@click.option("--compression", type=click.Choice(list(_COMPRESSIONS.keys())), help="Compression quality")
@click.option("--generate-mipmaps/--no-mipmaps", default=None, help="Generate mipmaps")
@click.option("--srgb/--linear", default=None, help="sRGB color texture")
@click.option("--readable/--no-readable", default=None, help="Read/Write enabled")
@click.option("--as-sprite", is_flag=True, help="Shorthand: set texture type to Sprite with defaults")
@handle_unity_errors
def modify(path: str, set_pixels: Optional[str], texture_type: Optional[str], sprite_mode: Optional[str],
           sprite_ppu: Optional[float], max_size: Optional[str], compression: Optional[str],
           generate_mipmaps: Optional[bool], srgb: Optional[bool], readable: Optional[bool],
           as_sprite: bool, import_settings: Optional[str], npot_scale: Optional[str]):
    """Modify an existing texture.

    \b
    Examples:
        unity-mcp texture modify Assets/Tex.png --set-pixels '{"x":0,"y":0,"width":10,"height":10,"color":[255,0,0]}'
        unity-mcp texture modify Assets/Tex.png --set-pixels '{"x":0,"y":0,"width":2,"height":2,"pixels":[[255,0,0,255],[0,255,0,255],[0,0,255,255],[255,255,0,255]]}'
        unity-mcp texture modify Assets/UI/icon.png --as-sprite
        unity-mcp texture modify Assets/UI/bg.png --texture-type sprite --max-size 2048
    """
    config = get_config()

    params: dict[str, Any] = {"action": "modify", "path": path}

    has_import = _apply_import_flags_to_params(
        params, texture_type, sprite_mode, sprite_ppu, max_size,
        compression, generate_mipmaps, srgb, readable, as_sprite, import_settings, npot_scale)

    if set_pixels is not None:
        try:
            params["setPixels"] = _normalize_set_pixels(set_pixels)
        except ValueError as e:
            print_error(str(e))
            sys.exit(1)
    elif not has_import:
        print_error("At least one of --set-pixels or an import-setting flag must be provided")
        sys.exit(1)

    result = run_command("manage_texture", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Modified texture: {path}")


@texture.command("delete")
@click.argument("path")
@click.option(
    "--force", "-f",
    is_flag=True,
    help="Skip confirmation prompt."
)
@handle_unity_errors
def delete(path: str, force: bool):
    """Delete a texture.

    \\b
    Examples:
        unity-mcp texture delete "Assets/Textures/Old.png"
        unity-mcp texture delete "Assets/Textures/Old.png" --force
    """
    from cli.utils.confirmation import confirm_destructive_action
    config = get_config()

    confirm_destructive_action("Delete", "texture", path, force)

    result = run_command("manage_texture", {
                         "action": "delete", "path": path}, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Deleted texture: {path}")


@texture.command("set-import-settings")
@click.argument("path")
@click.option("--import-settings", default=None, help="Importer settings JSON; snake_case or Unity property names")
@click.option("--npot-scale", type=click.Choice(list(NPOT_SCALES)), help="Non-power-of-two texture scaling")
@click.option("--texture-type", type=click.Choice(list(_TEXTURE_TYPES.keys())), help="Texture type")
@click.option("--sprite-mode", type=click.Choice(list(_SPRITE_MODES.keys())), help="Sprite import mode")
@click.option("--sprite-ppu", type=float, help="Sprite pixels per unit")
@click.option("--max-size", type=click.Choice(["32", "64", "128", "256", "512", "1024", "2048", "4096", "8192", "16384"]), help="Max texture size")
@click.option("--compression", type=click.Choice(list(_COMPRESSIONS.keys())), help="Compression quality")
@click.option("--generate-mipmaps/--no-mipmaps", default=None, help="Generate mipmaps")
@click.option("--srgb/--linear", default=None, help="sRGB color texture")
@click.option("--readable/--no-readable", default=None, help="Read/Write enabled")
@click.option("--as-sprite", is_flag=True, help="Shorthand: set texture type to Sprite with defaults")
@handle_unity_errors
def set_import_settings(path: str, texture_type: Optional[str], sprite_mode: Optional[str],
                        sprite_ppu: Optional[float], max_size: Optional[str],
                        compression: Optional[str], generate_mipmaps: Optional[bool],
                        srgb: Optional[bool], readable: Optional[bool], as_sprite: bool,
                        import_settings: Optional[str], npot_scale: Optional[str]):
    """Change import settings on an existing texture.

    \b
    Examples:
        unity-mcp texture set-import-settings Assets/UI/icon.png --texture-type sprite
        unity-mcp texture set-import-settings Assets/UI/icon.png --as-sprite
        unity-mcp texture set-import-settings Assets/UI/icon.png --texture-type sprite --sprite-mode single --sprite-ppu 100
        unity-mcp texture set-import-settings Assets/UI/bg.png --max-size 2048 --compression high_quality
    """
    config = get_config()

    params: dict[str, Any] = {"action": "set_import_settings", "path": path}

    has_import = _apply_import_flags_to_params(
        params, texture_type, sprite_mode, sprite_ppu, max_size,
        compression, generate_mipmaps, srgb, readable, as_sprite, import_settings, npot_scale)

    if not has_import:
        print_error("At least one import setting must be specified")
        sys.exit(1)

    result = run_command("manage_texture", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Import settings updated: {path}")
