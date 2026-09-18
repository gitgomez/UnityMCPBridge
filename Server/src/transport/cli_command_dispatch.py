"""Reload-aware dispatch for the local CLI HTTP control surface."""

from typing import Any

from transport.plugin_hub import InstanceSelectionRequiredError, PluginHub

_UNAVAILABLE_REASONS = {
    "no_unity_session",
    "reload_timeout",
    "stale_connection",
    "unity_disconnected",
}


async def dispatch_cli_command(
    command_type: str,
    params: dict[str, Any],
    unity_instance: str | None,
) -> tuple[dict[str, Any], int]:
    """Dispatch one local CLI command through Runtime-v1 recovery."""

    try:
        result = await PluginHub.send_command_for_instance(
            unity_instance,
            command_type,
            params,
            retry_on_reload=True,
        )
        data = result.get("data") if isinstance(result, dict) else None
        reason = data.get("reason") if isinstance(data, dict) else None
        status_code = (
            503
            if result.get("success") is False
            and reason in _UNAVAILABLE_REASONS
            else 200
        )
        return result, status_code
    except InstanceSelectionRequiredError as exc:
        return (
            {
                "success": False,
                "error": str(exc),
                "available_instances": exc.available_instances,
            },
            409,
        )
