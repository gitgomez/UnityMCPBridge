"""Validated TextureImporter settings shared by MCP and texture CLI commands."""

import math
from typing import Any

from services.tools.utils import coerce_bool, coerce_float, parse_json_payload

TEXTURE_TYPES = {
    "default": "Default", "normal_map": "NormalMap", "editor_gui": "GUI",
    "sprite": "Sprite", "cursor": "Cursor", "cookie": "Cookie",
    "lightmap": "Lightmap", "directional_lightmap": "DirectionalLightmap",
    "shadow_mask": "Shadowmask", "single_channel": "SingleChannel",
}
TEXTURE_SHAPES = {"2d": "Texture2D", "cube": "TextureCube"}
ALPHA_SOURCES = {"none": "None", "from_input": "FromInput", "from_gray_scale": "FromGrayScale"}
WRAP_MODES = {"repeat": "Repeat", "clamp": "Clamp", "mirror": "Mirror", "mirror_once": "MirrorOnce"}
FILTER_MODES = {"point": "Point", "bilinear": "Bilinear", "trilinear": "Trilinear"}
COMPRESSIONS = {
    "none": "Uncompressed", "low_quality": "CompressedLQ",
    "normal_quality": "Compressed", "high_quality": "CompressedHQ",
}
SPRITE_MODES = {"single": "Single", "multiple": "Multiple", "polygon": "Polygon"}
SPRITE_MESH_TYPES = {"full_rect": "FullRect", "tight": "Tight"}
MIPMAP_FILTERS = {"box": "BoxFilter", "kaiser": "KaiserFilter"}
NPOT_SCALES = {
    "none": "None", "to_nearest": "ToNearest",
    "to_larger": "ToLarger", "to_smaller": "ToSmaller",
}

_SETTING_NAMES = {
    "texture_type": "textureType", "texture_shape": "textureShape",
    "srgb": "sRGBTexture", "alpha_source": "alphaSource",
    "alpha_is_transparency": "alphaIsTransparency", "readable": "isReadable",
    "generate_mipmaps": "mipmapEnabled", "compression_crunched": "crunchedCompression",
    "wrap_mode": "wrapMode", "wrap_mode_u": "wrapModeU", "wrap_mode_v": "wrapModeV",
    "filter_mode": "filterMode", "mipmap_filter": "mipmapFilter",
    "aniso_level": "anisoLevel", "max_texture_size": "maxTextureSize",
    "compression": "textureCompression", "compression_quality": "compressionQuality",
    "sprite_mode": "spriteImportMode", "sprite_pixels_per_unit": "spritePixelsPerUnit",
    "sprite_pivot": "spritePivot", "sprite_mesh_type": "spriteMeshType",
    "sprite_extrude": "spriteExtrude", "npot_scale": "npotScale",
}
_ALIASES = {**{name: name for name in _SETTING_NAMES},
            **{camel: snake for snake, camel in _SETTING_NAMES.items()}}
_ENUMS = {
    "texture_type": TEXTURE_TYPES, "texture_shape": TEXTURE_SHAPES,
    "alpha_source": ALPHA_SOURCES, "wrap_mode": WRAP_MODES,
    "wrap_mode_u": WRAP_MODES, "wrap_mode_v": WRAP_MODES,
    "filter_mode": FILTER_MODES, "mipmap_filter": MIPMAP_FILTERS,
    "compression": COMPRESSIONS, "sprite_mode": SPRITE_MODES,
    "sprite_mesh_type": SPRITE_MESH_TYPES, "npot_scale": NPOT_SCALES,
}
_BOOLEANS = {"srgb", "alpha_is_transparency", "readable", "generate_mipmaps", "compression_crunched"}
_INTEGER_RANGES = {"aniso_level": (0, 16), "compression_quality": (0, 100), "sprite_extrude": (0, 32)}
_MAX_TEXTURE_SIZES = {32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384}


def _normalize_setting(name: str, value: Any) -> Any:
    if name in _ENUMS:
        mapping = _ENUMS[name]
        choices = {**mapping, **{item.lower(): item for item in mapping.values()}}
        if isinstance(value, str) and value.strip().lower() in choices:
            return choices[value.strip().lower()]
        raise ValueError(f"Invalid {name} '{value}'. Valid: {list(mapping)}")

    if name in _BOOLEANS:
        if not isinstance(value, (bool, str, int, float)):
            raise ValueError(f"{name} must be a boolean")
        if isinstance(value, (int, float)) and not isinstance(value, bool) and value not in (0, 1):
            raise ValueError(f"{name} must be a boolean")
        result = coerce_bool(value, default=None)
        if result is None:
            raise ValueError(f"{name} must be a boolean")
        return result

    if name == "sprite_pivot":
        if not isinstance(value, (list, tuple)) or len(value) != 2:
            raise ValueError("sprite_pivot must be [x, y]")
        pivot = [coerce_float(item) for item in value]
        if any(item is None or not math.isfinite(item) for item in pivot):
            raise ValueError("sprite_pivot must contain two finite numbers")
        return pivot

    number = coerce_float(value)
    if number is None or not math.isfinite(number):
        raise ValueError(f"{name} must be a finite number")
    if name == "sprite_pixels_per_unit":
        if number <= 0:
            raise ValueError("sprite_pixels_per_unit must be positive")
        return number
    if not number.is_integer():
        raise ValueError(f"{name} must be an integer")
    integer = int(number)
    if name == "max_texture_size":
        if integer not in _MAX_TEXTURE_SIZES:
            raise ValueError(f"max_texture_size must be one of {sorted(_MAX_TEXTURE_SIZES)}")
    else:
        minimum, maximum = _INTEGER_RANGES[name]
        if not minimum <= integer <= maximum:
            raise ValueError(f"{name} must be {minimum}-{maximum}")
    return integer


def normalize_import_settings(value: Any) -> tuple[dict[str, Any] | None, str | None]:
    """Normalize supported aliases without dropping fields or resolving conflicts silently."""
    if value is None:
        return None, None
    if isinstance(value, str):
        value = parse_json_payload(value)
    if not isinstance(value, dict):
        return None, f"import_settings must be a dict, got {type(value).__name__}"

    unknown = [key for key in value if key not in _ALIASES]
    if unknown:
        return None, "Unsupported import_settings keys: " + ", ".join(sorted(map(str, unknown)))

    result: dict[str, Any] = {}
    sources: dict[str, str] = {}
    try:
        for key, raw in value.items():
            name = _ALIASES[key]
            canonical = _SETTING_NAMES[name]
            normalized = _normalize_setting(name, raw)
            if canonical in result and result[canonical] != normalized:
                return None, f"Conflicting import_settings keys: {sources[canonical]} and {key}"
            result[canonical] = normalized
            sources[canonical] = key
    except ValueError as exc:
        return None, str(exc)
    return result, None
