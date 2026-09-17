#!/usr/bin/env python3
"""Exercise the project-workflow tools through a real MCP stdio session.

Unlike ``bridge_smoke.py``, this driver starts the Python MCP server as a
subprocess and talks to it with an MCP client. The resulting path is therefore
MCP client -> FastMCP server -> legacy TCP bridge -> live Unity Editor.

The test intentionally generates and deletes a C# Input System wrapper. Both
operations cause a Unity domain reload and gate bridge reconnection behaviour.
Run this only against a disposable test project: Addressables initialization
creates project settings that are not removed by the public authoring API.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
import tempfile
import time
import uuid
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any

from fastmcp import Client
from fastmcp.client.transports import StdioTransport


SERVER_ROOT = Path(__file__).resolve().parents[2]
SERVER_MAIN = SERVER_ROOT / "src" / "main.py"
RUN_ID = uuid.uuid4().hex[:8]
TEMP_ROOT = f"Assets/Temp/McpWorkflowE2E_{RUN_ID}"
INPUT_PATH = f"{TEMP_ROOT}/E2EControls.inputactions"
WRAPPER_PATH = f"{TEMP_ROOT}/E2EControls.cs"
PLAYER_NAME = f"MCP_Workflow_E2E_Player_{RUN_ID}"
GROUP_NAME = f"MCP Workflow E2E {RUN_ID}"
CLASS_NAME = f"E2EControls_{RUN_ID}"
CODE_NAMESPACE = f"UnityMCP.E2E.Run_{RUN_ID}"


def _dig(value: Any, key: str) -> Any:
    stack = [value]
    while stack:
        current = stack.pop()
        if isinstance(current, dict):
            if key in current:
                return current[key]
            stack.extend(current.values())
        elif isinstance(current, list):
            stack.extend(current)
    return None


def _payload(result: Any) -> dict[str, Any]:
    structured = getattr(result, "structured_content", None)
    if isinstance(structured, dict):
        return structured
    for block in getattr(result, "content", []) or []:
        text = getattr(block, "text", None)
        if not text:
            continue
        try:
            parsed = json.loads(text)
        except json.JSONDecodeError:
            continue
        if isinstance(parsed, dict):
            return parsed
    return {}


def _message(payload: dict[str, Any]) -> str:
    return str(
        _dig(payload, "message")
        or _dig(payload, "error")
        or payload
    )


class WorkflowFailure(RuntimeError):
    pass


async def run(instance: str, server_log: Path) -> None:
    args = [
        "-X", "utf8", str(SERVER_MAIN),
        "--transport", "stdio",
        "--default-instance", instance,
        "--project-scoped-tools",
    ]
    transport = StdioTransport(
        command=sys.executable,
        args=args,
        env=dict(os.environ),
        cwd=str(SERVER_ROOT),
        keep_alive=False,
        log_file=server_log,
    )
    client = Client(transport, timeout=240, init_timeout=60)
    completed: list[tuple[str, float]] = []
    workflow_completed = False

    async def call(name: str, arguments: dict[str, Any], step: str) -> dict[str, Any]:
        started = time.monotonic()
        result = await client.call_tool(name, arguments)
        payload = _payload(result)
        elapsed = time.monotonic() - started
        if _dig(payload, "success") is not True:
            raise WorkflowFailure(f"{step}: {_message(payload)}")
        completed.append((step, elapsed))
        print(f"  [PASS] {step} ({elapsed:.2f}s)", flush=True)
        return payload

    async def cleanup(name: str, arguments: dict[str, Any], step: str) -> None:
        try:
            payload = _payload(await client.call_tool(name, arguments))
            if _dig(payload, "success") is not True:
                print(f"  [WARN] cleanup {step}: {_message(payload)}", flush=True)
        except asyncio.CancelledError:
            # Deleting the generated C# wrapper intentionally reloads Unity and may cancel the
            # final MCP request after the asset deletion has already committed.
            print(f"  [WARN] cleanup {step}: Unity reloaded after the mutation", flush=True)
        except Exception as exc:
            print(f"  [WARN] cleanup {step}: {exc}", flush=True)

    @asynccontextmanager
    async def cleanup_after_run():
        try:
            yield
        finally:
            await cleanup(
                "manage_addressables", {"action": "remove_entry", "asset_path": INPUT_PATH},
                "Addressables entry",
            )
            await cleanup(
                "manage_addressables",
                {"action": "remove_group", "group_name": GROUP_NAME, "force": True},
                "Addressables group",
            )
            await cleanup(
                "manage_gameobject",
                {"action": "delete", "target": PLAYER_NAME, "search_method": "by_name"},
                "PlayerInput host",
            )
            await cleanup("manage_input", {"action": "delete", "path": INPUT_PATH}, "Input Actions asset")
            await cleanup("delete_script", {"uri": WRAPPER_PATH}, "generated wrapper")

    try:
        async with client, cleanup_after_run():
            # Calling the tools is the authoritative availability check. Avoid a tools/list
            # request here because a visibility-changed notification can race that request while
            # the freshly spawned server is synchronising project-scoped tool groups.
            print("  [PASS] MCP stdio session connected", flush=True)

            impact = await call(
                "inspect_dependencies",
                {
                    "action": "impact",
                    "target": "Assets/Tests/EditMode/Tools/ManageInputTests.cs",
                    "max_results": 20,
                },
                "dependency impact",
            )
            if _dig(impact, "target") is None:
                raise WorkflowFailure("dependency impact returned no target")

            audit = await call(
                "audit_project",
                {
                    "action": "run",
                    "checks": ["compilation", "package_health"],
                    "minimum_severity": "info",
                },
                "project audit",
            )
            if _dig(audit, "errorCount") != 0:
                raise WorkflowFailure(f"project audit reported errors: {_message(audit)}")

            ping = await call("manage_input", {"action": "ping"}, "Input System availability")
            if _dig(ping, "installed") is not True:
                raise WorkflowFailure("Input System package is not installed")
            await call(
                "manage_input", {"action": "create", "path": INPUT_PATH},
                "create Input Actions asset",
            )
            await call(
                "manage_input",
                {"action": "add_action_map", "path": INPUT_PATH, "map_name": "Player"},
                "add action map",
            )
            await call(
                "manage_input",
                {
                    "action": "add_action", "path": INPUT_PATH,
                    "map_name": "Player", "action_name": "Jump",
                    "action_type": "Button", "expected_control_type": "Button",
                },
                "add input action",
            )
            await call(
                "manage_input",
                {
                    "action": "add_binding", "path": INPUT_PATH,
                    "map_name": "Player", "action_name": "Jump",
                    "binding_path": "<Keyboard>/space", "groups": "Keyboard",
                },
                "add input binding",
            )
            await call(
                "manage_input",
                {
                    "action": "add_control_scheme", "path": INPUT_PATH,
                    "scheme_name": "Keyboard",
                    "devices": [{"device_path": "<Keyboard>", "optional": False}],
                },
                "add control scheme",
            )
            validation = await call(
                "manage_input", {"action": "validate", "path": INPUT_PATH},
                "validate Input Actions",
            )
            if _dig(validation, "errorCount") != 0:
                raise WorkflowFailure("Input Actions validation reported errors")

            addressables_ping = await call(
                "manage_addressables", {"action": "ping"},
                "Addressables availability",
            )
            if _dig(addressables_ping, "installed") is not True:
                raise WorkflowFailure("Addressables package is not installed")
            await call("manage_addressables", {"action": "initialize"}, "initialize Addressables")
            await call(
                "manage_addressables",
                {"action": "create_group", "group_name": GROUP_NAME},
                "create Addressables group",
            )
            await call(
                "manage_addressables",
                {
                    "action": "add_entry", "group_name": GROUP_NAME,
                    "asset_path": INPUT_PATH, "address": f"mcp/e2e/{RUN_ID}/controls",
                },
                "add Addressables entry",
            )
            await call(
                "manage_addressables",
                {
                    "action": "set_address", "asset_path": INPUT_PATH,
                    "address": f"mcp/e2e/{RUN_ID}/controls-v2",
                },
                "update runtime address",
            )
            await call(
                "manage_addressables",
                {
                    "action": "set_profile_value", "variable_name": f"MCP.E2ERoot_{RUN_ID}",
                    "value": f"https://example.invalid/{RUN_ID}", "create_variable": True,
                },
                "set Addressables profile value",
            )
            addressables_validation = await call(
                "manage_addressables", {"action": "validate"},
                "validate Addressables",
            )
            if _dig(addressables_validation, "errorCount") != 0:
                raise WorkflowFailure("Addressables validation reported errors")
            build = await call(
                "manage_addressables", {"action": "build"},
                "queue Addressables content build",
            )
            build_job_id = _dig(build, "jobId")
            if not isinstance(build_job_id, str) or not build_job_id:
                raise WorkflowFailure("Addressables build returned no jobId")

            build_status: dict[str, Any] | None = None
            for attempt in range(12):
                if attempt:
                    await asyncio.sleep(2)
                try:
                    build_status = await call(
                        "manage_addressables",
                        {"action": "build_status", "job_id": build_job_id},
                        f"read Addressables build status ({attempt + 1})",
                    )
                except Exception:
                    if attempt == 11:
                        raise
                    continue
                status = _dig(build_status, "status")
                if status in {"completed", "failed"}:
                    break
            if _dig(build_status, "status") != "completed":
                raise WorkflowFailure(f"Addressables build did not complete: {_message(build_status or {})}")
            if _dig(build_status, "succeeded") is not True:
                raise WorkflowFailure(f"Addressables build did not succeed: {_message(build_status)}")

            await call(
                "manage_gameobject", {"action": "create", "name": PLAYER_NAME},
                "create PlayerInput host",
            )
            await call(
                "manage_input",
                {
                    "action": "assign_player_input", "path": INPUT_PATH,
                    "target": PLAYER_NAME, "default_map": "Player",
                    "default_scheme": "Keyboard",
                    "notification_behavior": "InvokeUnityEvents",
                },
                "assign PlayerInput",
            )

            await call(
                "manage_input",
                {
                    "action": "generate_csharp", "path": INPUT_PATH,
                    "output_path": WRAPPER_PATH, "class_name": CLASS_NAME,
                    "namespace": CODE_NAMESPACE,
                },
                "generate C# wrapper (triggers reload)",
            )
            # This call can only succeed if the bridge survived compilation and domain reload.
            await call(
                "manage_input", {"action": "get", "path": INPUT_PATH},
                "reconnect after wrapper compilation",
            )
            workflow_completed = True
    except Exception as exc:
        if not workflow_completed:
            raise
        # The last cleanup deletes a C# file and intentionally reloads Unity. The mutation may
        # close the legacy transport just before FastMCP exits its stdio session; at that point
        # every asserted workflow step has passed and the generated file has already gone.
        print(f"  [WARN] MCP session closed during final reload cleanup: {exc}", flush=True)

    print(f"== {len(completed)} workflow calls passed ==", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Unity workflow tools MCP E2E smoke test")
    parser.add_argument(
        "--instance", default=os.environ.get("UNITY_MCP_DEFAULT_INSTANCE"),
        help="Required Unity instance id (for example Project@hash).",
    )
    parser.add_argument(
        "--server-log",
        default=str(Path(tempfile.gettempdir()) / "unity-mcp-workflow-smoke-server.log"),
        help="File receiving stderr from the spawned MCP server.",
    )
    args = parser.parse_args()
    if not args.instance or not args.instance.strip():
        parser.error("--instance or UNITY_MCP_DEFAULT_INSTANCE is required")

    print(f"== Unity workflow tools MCP smoke (run={RUN_ID}, instance={args.instance}) ==", flush=True)
    try:
        asyncio.run(run(args.instance.strip(), Path(args.server_log)))
    except Exception as exc:
        print(f"  [FAIL] {exc}", flush=True)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
