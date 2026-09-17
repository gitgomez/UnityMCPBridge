"""Utilities for normalizing Unity transport responses."""
from __future__ import annotations

from typing import Any, Type

from models.models import MCPResponse


def normalize_unity_response(response: Any) -> Any:
    """Normalize Unity's {status,result} payloads into MCPResponse shape."""
    if not isinstance(response, dict):
        return response

    if response.get("runtime_version") == 1:
        status = response.get("status")
        success = status == "succeeded"
        message = response.get("message")
        error = None if success else message or "Unity command failed"
        data = response.get("data")
        if isinstance(data, dict):
            # Runtime v1 wraps the complete legacy command result in ``data``.
            # Flatten MCPResponse-shaped results so existing resources and tools
            # continue to see their original payload at the transport boundary.
            if "success" in data:
                legacy_success = data.get("success") is not False
                success = success and legacy_success
                message = data.get("message") or message
                error = data.get("error") or (None if success else message or "Unity command failed")
                has_legacy_data = "data" in data
                legacy_data = data.get("data")
                if not has_legacy_data:
                    legacy_data = {
                        key: value
                        for key, value in data.items()
                        if key not in {"success", "message", "error", "status", "code"}
                    } or None
                data = legacy_data
            else:
                data = {
                    key: value
                    for key, value in data.items()
                    if key not in {"message", "error", "status", "code"}
                } or None
        return {
            "success": success,
            "message": message,
            "error": None if success else error,
            "data": data,
            "code": response.get("code"),
            "runtime": {
                "request_id": response.get("request_id"),
                "status": status,
                "changes": response.get("changes"),
                "diagnostics": response.get("diagnostics"),
                "timing": response.get("timing"),
                "state": response.get("state"),
                "receipt": response.get("receipt"),
            },
        }

    status = response.get("status")
    result = response.get("result") if isinstance(
        response.get("result"), dict) else response.get("result")

    # Already MCPResponse-shaped
    if "success" in response:
        return response
    if isinstance(result, dict) and "success" in result:
        return result

    if status is None:
        return response

    payload = result if isinstance(result, dict) else {}
    success = status == "success"
    message = payload.get("message") or response.get("message")
    error = payload.get("error") or response.get("error")

    data = payload.get("data")
    if data is None and isinstance(payload, dict) and payload:
        data = {k: v for k, v in payload.items() if k not in {
            "message", "error", "status", "code"}}
        if not data:
            data = None

    normalized: dict[str, Any] = {
        "success": success,
        "message": message,
        "error": error if not success else None,
        "data": data,
    }

    if not success and not normalized["error"]:
        normalized["error"] = message or "Unity command failed"

    return normalized


def parse_resource_response(response: Any, typed_cls: Type[MCPResponse]) -> MCPResponse:
    """Parse a Unity response into a typed response class.

    Returns a base ``MCPResponse`` for error responses so that typed subclasses
    with strict ``data`` fields (e.g. ``list[str]``) don't raise Pydantic
    validation errors when ``data`` is ``None``.
    """
    if not isinstance(response, dict):
        return response

    # Detect errors from both normalized (success=False) and raw (status="error") shapes.
    if response.get("success") is False or response.get("status") == "error":
        return MCPResponse(
            success=False,
            error=response.get("error"),
            message=response.get("message"),
            data=response.get("data"),
            code=response.get("code"),
            runtime=response.get("runtime"),
            hint=response.get("hint"),
        )

    return typed_cls(**response)
