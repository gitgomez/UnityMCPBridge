"""Authenticated local launch-owner control. Never discovers or kills processes."""

from __future__ import annotations

import hmac
import ipaddress
import json
import os
import uuid
from typing import Callable

from starlette.requests import Request
from starlette.responses import JSONResponse

from core.config import config
from transport.plugin_registry import PluginRegistry

_BOOT_ID = uuid.uuid4().hex


async def managed_server_control(
    request: Request, get_registry: Callable[[], PluginRegistry | None]
) -> JSONResponse:
    # Do not trust forwarded headers, DNS names, or a publicly bound listener.
    try:
        peer_local = bool(request.client and ipaddress.ip_address(request.client.host).is_loopback)
        host_local = os.environ.get("UNITY_MCP_HTTP_HOST", "") in {"localhost", "127.0.0.1", "::1"}
    except ValueError:
        peer_local = host_local = False
    token = os.environ.get("UNITY_MCP_INSTANCE_TOKEN", "")
    owner = os.environ.get("UNITY_MCP_OWNER_PROJECT_HASH", "")
    supplied = request.headers.get("x-unity-mcp-instance-token", "")
    if (config.http_remote_hosted or not peer_local or not host_local or not token or not owner
            or not hmac.compare_digest(token.encode(), supplied.encode())):
        return JSONResponse({"success": False, "code": "STOP_AUTHORITY_UNAVAILABLE"}, status_code=403)
    raw = bytearray()
    async for chunk in request.stream():
        raw.extend(chunk)
        if len(raw) > 4096:
            return JSONResponse({"success": False, "code": "MESSAGE_TOO_LARGE"}, status_code=413)
    try:
        body = json.loads(raw)
        if (not isinstance(body, dict) or type(body.get("expected_pid")) is not int
                or body["expected_pid"] != os.getpid()):
            raise ValueError()
        action = body.get("action", "status")
        if action not in {"status", "prepare", "cancel"}:
            raise ValueError()
    except (ValueError, TypeError):
        return JSONResponse({"success": False, "code": "SERVER_IDENTITY_MISMATCH"}, status_code=409)
    registry = get_registry()
    if registry is None:
        return JSONResponse({"success": False, "code": "REGISTRY_UNAVAILABLE"}, status_code=503)
    if action == "prepare":
        result = await registry.prepare_shutdown(owner)
    elif action == "cancel":
        result = {"cancelled": await registry.cancel_shutdown(str(body.get("ticket", "")))}
    else:
        result = await registry.shutdown_status(owner)
    return JSONResponse({"success": True, "capability": "managed_shutdown_v1",
                         "pid": os.getpid(), "boot_id": _BOOT_ID, **result})
