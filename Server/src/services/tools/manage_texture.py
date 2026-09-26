"""
Defines the manage_texture tool for procedural texture generation in Unity.
"""
import base64
import json
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.utils import parse_json_payload, coerce_int, normalize_color
from services.tools.texture_import_settings import normalize_import_settings as _normalize_import_settings
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry
from services.tools.preflight import preflight


def _normalize_dimension(value: Any, name: str, default: int = 64) -> tuple[int | None, str | None]:
    if value is None:
        return default, None
    coerced = coerce_int(value)
    if coerced is None:
        return None, f"{name} must be an integer"
    if coerced <= 0:
        return None, f"{name} must be positive"
    return coerced, None


def _normalize_positive_int(value: Any, name: str) -> tuple[int | None, str | None]:
    if value is None:
        return None, None
    coerced = coerce_int(value)
    if coerced is None or coerced <= 0:
        return None, f"{name} must be a positive integer"
    return coerced, None


def _normalize_color_int(value: Any) -> tuple[list[int] | None, str | None]:
    """Thin wrapper for normalize_color with int output for texture operations."""
    return normalize_color(value, output_range="int")


def _normalize_palette(value: Any) -> tuple[list[list[int]] | None, str | None]:
    """
    Normalize color palette to list of [r, g, b, a] colors (0-255).
    Returns (parsed_palette, error_message).
    """
    if value is None:
        return None, None

    # Try parsing as string first
    if isinstance(value, str):
        if value in ("[object Object]", "undefined", "null", ""):
            return None, f"palette received invalid value: '{value}'"
        parsed = parse_json_payload(value)
        # If parsing succeeded and result is a list, normalize and return
        if isinstance(parsed, list):
            value = parsed
        # If parsing returned the original string (invalid JSON), treat as error
        elif parsed == value:
            return None, f"palette must be a list of colors, got invalid string: '{value}'"
        else:
            return None, f"palette must be a list of colors (list), got string that parsed to {type(parsed).__name__}"

    # Validate and normalize each color in the palette
    if not isinstance(value, list):
        return None, f"palette must be a list of colors, got {type(value).__name__}"

    normalized = []
    for i, color in enumerate(value):
        color_normalized, error = _normalize_color_int(color)
        if error:
            return None, f"palette[{i}]: {error}"
        normalized.append(color_normalized)

    return normalized, None


def _normalize_pixels(value: Any, width: int, height: int) -> tuple[list[list[int]] | str | None, str | None]:
    """
    Normalize pixel data to list of [r, g, b, a] colors or base64 string.
    Returns (pixels, error_message).
    """
    if value is None:
        return None, None

    # Base64 string
    if isinstance(value, str):
        if value.startswith("base64:"):
            return value, None  # Pass through for Unity to decode
        # Try parsing as JSON array
        parsed = parse_json_payload(value)
        if isinstance(parsed, list):
            value = parsed
        else:
            # Assume it's raw base64
            return f"base64:{value}", None

    if isinstance(value, list):
        expected_count = width * height
        if len(value) != expected_count:
            return None, f"pixels array must have {expected_count} entries for {width}x{height} texture, got {len(value)}"

        normalized = []
        for i, pixel in enumerate(value):
            parsed, error = _normalize_color_int(pixel)
            if error:
                return None, f"pixels[{i}]: {error}"
            normalized.append(parsed)
        return normalized, None

    return None, f"pixels must be a list or base64 string, got {type(value).__name__}"


def _normalize_sprite_settings(value: Any) -> tuple[dict | None, str | None]:
    """
    Normalize sprite settings.
    Returns (settings, error_message).
    """
    if value is None:
        return None, None

    if isinstance(value, str):
        value = parse_json_payload(value)

    if isinstance(value, dict):
        result = {}
        if "pivot" in value:
            pivot = value["pivot"]
            if isinstance(pivot, (list, tuple)) and len(pivot) == 2:
                result["pivot"] = [float(pivot[0]), float(pivot[1])]
            else:
                return None, f"sprite pivot must be [x, y], got {pivot}"
        if "pixels_per_unit" in value:
            result["pixelsPerUnit"] = float(value["pixels_per_unit"])
        elif "pixelsPerUnit" in value:
            result["pixelsPerUnit"] = float(value["pixelsPerUnit"])
        return result, None

    if isinstance(value, bool) and value:
        # Just enable sprite mode with defaults
        return {"pivot": [0.5, 0.5], "pixelsPerUnit": 100}, None

    return None, f"as_sprite must be a dict or boolean, got {type(value).__name__}"


@mcp_for_unity_tool(
    group="vfx",
    description=(
        "Procedural texture generation for Unity. Creates textures with solid fills, "
        "patterns (checkerboard, stripes, dots, grid, brick), gradients, and noise. "
        "Actions: create, modify, delete, create_sprite, apply_pattern, apply_gradient, apply_noise, "
        "set_import_settings. Import settings accept documented snake_case keys or Unity "
        "property names, including npot_scale/npotScale. Unsupported or conflicting "
        "settings fail before dispatch."
    ),
    annotations=ToolAnnotations(
        title="Manage Texture",
        destructiveHint=True,
    ),
)
async def manage_texture(
    ctx: Context,
    action: Annotated[Literal[
        "create",
        "modify",
        "delete",
        "create_sprite",
        "apply_pattern",
        "apply_gradient",
        "apply_noise",
        "set_import_settings"
    ], "Action to perform."],

    # Required for most actions
    path: Annotated[str,
                    "Output texture path (e.g., 'Assets/Textures/MyTexture.png')"] | None = None,

    # Dimensions (defaults to 64x64)
    width: Annotated[int, "Texture width in pixels (default: 64)"] | None = None,
    height: Annotated[int, "Texture height in pixels (default: 64)"] | None = None,

    # Solid fill (accepts both 0-255 integers and 0.0-1.0 normalized floats)
    fill_color: Annotated[list[int | float] | dict[str, int | float] | str,
                          "Fill color as [r, g, b] or [r, g, b, a] array, {r, g, b, a} object, or hex string. Accepts both 0-255 range (e.g., [255, 0, 0]) or 0.0-1.0 normalized range (e.g., [1.0, 0, 0])"] | None = None,

    # Pattern-based generation
    pattern: Annotated[Literal[
        "checkerboard", "stripes", "stripes_h", "stripes_v", "stripes_diag",
        "dots", "grid", "brick"
    ], "Pattern type for apply_pattern action"] | None = None,

    palette: Annotated[list[list[int | float]] | str,
                       "Color palette as [[r,g,b,a], ...]. Accepts both 0-255 range or 0.0-1.0 normalized range"] | None = None,

    pattern_size: Annotated[int,
                            "Pattern cell size in pixels (default: 8)"] | None = None,

    # Direct pixel data
    pixels: Annotated[list[list[int]] | str,
                      "Pixel data as JSON array of [r,g,b,a] values or base64 string"] | None = None,

    image_path: Annotated[str,
                          "Source image file path for create/create_sprite (PNG/JPG)."] | None = None,

    # Gradient settings
    gradient_type: Annotated[Literal["linear", "radial"],
                             "Gradient type (default: linear)"] | None = None,
    gradient_angle: Annotated[float,
                              "Gradient angle in degrees for linear gradient (default: 0)"] | None = None,

    # Noise settings
    noise_scale: Annotated[float,
                           "Noise scale/frequency (default: 0.1)"] | None = None,
    octaves: Annotated[int,
                       "Number of noise octaves for detail (default: 1)"] | None = None,

    # Modify action
    set_pixels: Annotated[dict,
                          "Region to modify: {x, y, width, height, color or pixels}"] | None = None,

    # Sprite creation (legacy, prefer import_settings)
    as_sprite: Annotated[dict | bool,
                         "Configure as sprite: {pivot: [x,y], pixels_per_unit: 100} or true for defaults"] | None = None,

    # TextureImporter settings
    import_settings: Annotated[dict,
        "TextureImporter settings dict. Keys: texture_type (default/normal_map/sprite/etc), "
        "texture_shape (2d/cube), srgb (bool), alpha_source (none/from_input/from_gray_scale), "
        "alpha_is_transparency (bool), readable (bool), generate_mipmaps (bool), "
        "wrap_mode/wrap_mode_u/wrap_mode_v (repeat/clamp/mirror/mirror_once), "
        "filter_mode (point/bilinear/trilinear), aniso_level (0-16), max_texture_size (32-16384), "
        "compression (none/low_quality/normal_quality/high_quality), compression_quality (0-100), "
        "sprite_mode (single/multiple/polygon), sprite_pixels_per_unit, sprite_pivot, "
        "sprite_mesh_type (full_rect/tight), sprite_extrude (0-32), "
        "npot_scale (none/to_nearest/to_larger/to_smaller). Corresponding Unity "
        "property names are also accepted, e.g. textureType, sRGBTexture, npotScale. "
        "Unknown keys and conflicting aliases are rejected."] | None = None,

) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    # Preflight check
    gate = await preflight(ctx, wait_for_no_compile=True, refresh_if_dirty=True)
    if gate is not None:
        return gate.model_dump()

    # --- Normalize parameters ---
    fill_color, fill_error = _normalize_color_int(fill_color)
    if fill_error:
        return {"success": False, "message": fill_error}

    action_lower = action.lower()

    if image_path is not None and action_lower not in ("create", "create_sprite"):
        return {"success": False, "message": "image_path is only supported for create/create_sprite."}

    if image_path is not None and (fill_color is not None or pattern is not None or pixels is not None):
        return {"success": False, "message": "image_path cannot be combined with fill_color, pattern, or pixels."}

    # Default to white for create action if nothing else specified
    if action == "create" and fill_color is None and pattern is None and pixels is None and image_path is None:
        fill_color = [255, 255, 255, 255]

    palette, palette_error = _normalize_palette(palette)
    if palette_error:
        return {"success": False, "message": palette_error}

    if image_path is None:
        # Normalize dimensions
        width, width_error = _normalize_dimension(width, "width")
        if width_error:
            return {"success": False, "message": width_error}
        height, height_error = _normalize_dimension(height, "height")
        if height_error:
            return {"success": False, "message": height_error}
        pattern_size, pattern_error = _normalize_positive_int(pattern_size, "pattern_size")
        if pattern_error:
            return {"success": False, "message": pattern_error}

        octaves, octaves_error = _normalize_positive_int(octaves, "octaves")
        if octaves_error:
            return {"success": False, "message": octaves_error}
    else:
        width = None
        height = None

    # Normalize pixels if provided
    pixels_normalized = None
    if pixels is not None:
        pixels_normalized, pixels_error = _normalize_pixels(pixels, width, height)
        if pixels_error:
            return {"success": False, "message": pixels_error}

    # Normalize sprite settings
    sprite_settings, sprite_error = _normalize_sprite_settings(as_sprite)
    if sprite_error:
        return {"success": False, "message": sprite_error}

    # Normalize import settings
    import_settings_normalized, import_error = _normalize_import_settings(import_settings)
    if import_error:
        return {"success": False, "message": import_error}
    if import_settings_normalized is not None and sprite_settings is not None:
        return {"success": False, "message": "Cannot specify both import_settings and as_sprite."}
    if action_lower == "set_import_settings" and not import_settings_normalized and not sprite_settings:
        return {"success": False, "message": "set_import_settings requires non-empty import_settings or as_sprite."}

    # Normalize set_pixels for modify action
    set_pixels_normalized = None
    if set_pixels is not None:
        if isinstance(set_pixels, str):
            parsed = parse_json_payload(set_pixels)
            if not isinstance(parsed, dict):
                return {"success": False, "message": "set_pixels must be a JSON object"}
            set_pixels = parsed
        if not isinstance(set_pixels, dict):
            return {"success": False, "message": "set_pixels must be a JSON object"}

        set_pixels_normalized = set_pixels.copy()
        if "color" in set_pixels_normalized:
            color, error = _normalize_color_int(set_pixels_normalized["color"])
            if error:
                return {"success": False, "message": f"set_pixels.color: {error}"}
            set_pixels_normalized["color"] = color
        if "pixels" in set_pixels_normalized:
            region_width = coerce_int(set_pixels_normalized.get("width"))
            region_height = coerce_int(set_pixels_normalized.get("height"))
            if region_width is None or region_height is None or region_width <= 0 or region_height <= 0:
                return {"success": False, "message": "set_pixels width and height must be positive integers"}
            pixels_normalized, pixels_error = _normalize_pixels(
                set_pixels_normalized["pixels"], region_width, region_height
            )
            if pixels_error:
                return {"success": False, "message": f"set_pixels.pixels: {pixels_error}"}
            set_pixels_normalized["pixels"] = pixels_normalized

    # --- Build params for Unity ---
    params_dict = {
        "action": action.lower(),
        "path": path,
        "width": width,
        "height": height,
        "fillColor": fill_color,
        "pattern": pattern,
        "palette": palette,
        "patternSize": pattern_size,
        "pixels": pixels_normalized,
        "imagePath": image_path,
        "gradientType": gradient_type,
        "gradientAngle": gradient_angle,
        "noiseScale": noise_scale,
        "octaves": octaves,
        "setPixels": set_pixels_normalized,
        "spriteSettings": sprite_settings,
        "importSettings": import_settings_normalized,
    }

    # Remove None values
    params_dict = {k: v for k, v in params_dict.items() if v is not None}

    # Send to Unity
    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_texture",
        params_dict,
    )

    if isinstance(result, dict):
        result["_debug_params"] = params_dict

    return result if isinstance(result, dict) else {"success": False, "message": str(result)}
