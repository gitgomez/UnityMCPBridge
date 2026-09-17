"""Validate the canonical public MCP tool contract and generate runtime hashes.

The checked-in JSON manifest is the normative contract. This utility extracts the
actual FastMCP surface only to detect drift; mutation, retry, and precondition policy
remain authored exclusively in the manifest.
"""

from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import pprint
import sys
from typing import Any


REPO_ROOT = Path(__file__).resolve().parents[1]
SERVER_SRC = REPO_ROOT / "Server" / "src"
MANIFEST_PATH = REPO_ROOT / "Contracts" / "tool-contracts.v1.json"
PYTHON_CONSTANT_PATH = SERVER_SRC / "transport" / "contract_hash.py"
CSHARP_CONSTANT_PATH = (
    REPO_ROOT
    / "MCPForUnity"
    / "Editor"
    / "Services"
    / "Transport"
    / "CommandRuntimeContract.g.cs"
)

SURFACE_FIELDS = (
    "name",
    "description",
    "group",
    "unity_target",
    "input_schema",
    "output_schema",
    "annotations",
)
MUTATION_CLASSES = {
    "read_only",
    "editor_state",
    "scene",
    "asset",
    "project_settings",
    "package",
    "build",
    "external_side_effect",
}
RETRY_SEMANTICS = {"safe", "idempotent", "unsafe_without_receipt"}


def canonical_json_bytes(value: Any) -> bytes:
    """Serialize using the normative contract canonicalization rules."""

    return json.dumps(
        value,
        ensure_ascii=False,
        allow_nan=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")


def compute_manifest_hash(manifest: dict[str, Any]) -> str:
    return "sha256:" + hashlib.sha256(canonical_json_bytes(manifest)).hexdigest()


async def _extract_public_surface_async() -> list[dict[str, Any]]:
    if str(SERVER_SRC) not in sys.path:
        sys.path.insert(0, str(SERVER_SRC))

    from core.config import config
    from fastmcp import FastMCP
    from services.registry import get_registered_tools
    from services.tools import register_all_tools

    # Tool-group visibility is session behavior, not part of surface extraction.
    config.transport_mode = "stdio"
    mcp = FastMCP("contract-surface")
    register_all_tools(mcp)

    fastmcp_tools = {tool.name: tool for tool in await mcp.list_tools()}
    registry_tools = {tool["name"]: tool for tool in get_registered_tools()}
    if set(fastmcp_tools) != set(registry_tools):
        missing_fastmcp = sorted(set(registry_tools) - set(fastmcp_tools))
        missing_registry = sorted(set(fastmcp_tools) - set(registry_tools))
        raise RuntimeError(
            "FastMCP/registry tool mismatch: "
            f"missing from FastMCP={missing_fastmcp}, "
            f"missing from registry={missing_registry}"
        )

    surface: list[dict[str, Any]] = []
    for name in sorted(fastmcp_tools):
        tool = fastmcp_tools[name]
        registry = registry_tools[name]
        annotations = (
            tool.annotations.model_dump(exclude_none=True)
            if tool.annotations is not None
            else {}
        )
        surface.append(
            {
                "name": name,
                "description": tool.description or "",
                "group": registry["group"],
                "unity_target": registry["unity_target"],
                "input_schema": tool.parameters,
                "output_schema": tool.output_schema
                or {"type": "object", "additionalProperties": True},
                "annotations": annotations,
            }
        )
    return surface


def extract_public_surface() -> list[dict[str, Any]]:
    return asyncio.run(_extract_public_surface_async())


def load_manifest() -> dict[str, Any]:
    with MANIFEST_PATH.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def validate_manifest(manifest: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    if manifest.get("manifest_version") != 1:
        errors.append("manifest_version must be 1")
    if manifest.get("protocol") != "command-runtime":
        errors.append("protocol must be 'command-runtime'")

    tools = manifest.get("tools")
    if not isinstance(tools, list) or not tools:
        return errors + ["tools must be a non-empty array"]

    names = [entry.get("name") for entry in tools if isinstance(entry, dict)]
    if names != sorted(names):
        errors.append("tools must be sorted by name")
    if len(names) != len(set(names)):
        errors.append("tool names must be unique")

    for index, entry in enumerate(tools):
        prefix = f"tools[{index}]"
        if not isinstance(entry, dict):
            errors.append(f"{prefix} must be an object")
            continue
        name = entry.get("name") or prefix
        prefix = f"tool '{name}'"
        for field in SURFACE_FIELDS:
            if field not in entry:
                errors.append(f"{prefix} missing {field}")
        if entry.get("contract_version") != 1:
            errors.append(f"{prefix} contract_version must be 1")

        availability = entry.get("availability")
        if not isinstance(availability, dict):
            errors.append(f"{prefix} availability must be an object")
        elif not isinstance(availability.get("requires_unity"), bool):
            errors.append(f"{prefix} availability.requires_unity must be boolean")

        execution_target = entry.get("execution_target")
        if not isinstance(execution_target, dict):
            errors.append(f"{prefix} execution_target must be an object")
        else:
            kind = execution_target.get("kind")
            if kind not in {"server", "unity", "hybrid", "custom_unity"}:
                errors.append(f"{prefix} has invalid execution_target.kind")
            handler = execution_target.get("handler")
            if kind == "unity" and not isinstance(handler, str):
                errors.append(f"{prefix} Unity execution target requires handler")
            if kind != "unity" and handler is not None:
                errors.append(f"{prefix} non-Unity execution target must not set handler")

        mutation = entry.get("mutation")
        if not isinstance(mutation, dict):
            errors.append(f"{prefix} mutation must be an object")
        else:
            if mutation.get("class") not in MUTATION_CLASSES:
                errors.append(f"{prefix} has invalid mutation.class")
            for field in (
                "destructive",
                "triggers_compilation",
                "requires_edit_mode",
                "undoable",
            ):
                if not isinstance(mutation.get(field), bool):
                    errors.append(f"{prefix} mutation.{field} must be boolean")

        execution = entry.get("execution")
        if not isinstance(execution, dict):
            errors.append(f"{prefix} execution must be an object")
        else:
            if execution.get("retry_semantics") not in RETRY_SEMANTICS:
                errors.append(f"{prefix} has invalid execution.retry_semantics")
            for field in ("default_timeout_ms", "max_timeout_ms"):
                if not isinstance(execution.get(field), int) or execution[field] <= 0:
                    errors.append(f"{prefix} execution.{field} must be positive integer")
            if not isinstance(execution.get("polling"), bool):
                errors.append(f"{prefix} execution.polling must be boolean")

        preconditions = entry.get("preconditions")
        if not isinstance(preconditions, dict) or not isinstance(
            preconditions.get("supports_if_match"), bool
        ):
            errors.append(f"{prefix} preconditions.supports_if_match must be boolean")

    return errors


def compare_surface(
    manifest: dict[str, Any], surface: list[dict[str, Any]]
) -> list[str]:
    errors: list[str] = []
    manifest_by_name = {entry["name"]: entry for entry in manifest["tools"]}
    surface_by_name = {entry["name"]: entry for entry in surface}
    if set(manifest_by_name) != set(surface_by_name):
        errors.append(
            "tool name mismatch: "
            f"manifest_only={sorted(set(manifest_by_name) - set(surface_by_name))}, "
            f"surface_only={sorted(set(surface_by_name) - set(manifest_by_name))}"
        )
        return errors

    for name in sorted(surface_by_name):
        manifest_entry = manifest_by_name[name]
        surface_entry = surface_by_name[name]
        for field in SURFACE_FIELDS:
            if manifest_entry.get(field) != surface_entry.get(field):
                errors.append(f"tool '{name}' surface field drifted: {field}")
    return errors


def sync_surface(
    manifest: dict[str, Any], surface: list[dict[str, Any]]
) -> dict[str, Any]:
    manifest_by_name = {entry["name"]: entry for entry in manifest["tools"]}
    surface_names = {entry["name"] for entry in surface}
    if set(manifest_by_name) != surface_names:
        raise RuntimeError(
            "Refusing to add/remove normative tool policies automatically. "
            "Add the new manifest entry with explicit policy first."
        )

    for surface_entry in surface:
        manifest_entry = manifest_by_name[surface_entry["name"]]
        for field in SURFACE_FIELDS:
            manifest_entry[field] = surface_entry[field]
    manifest["tools"] = [manifest_by_name[name] for name in sorted(manifest_by_name)]
    return manifest


def runtime_policy_entries(manifest: dict[str, Any]) -> list[dict[str, Any]]:
    """Return the manifest subset needed for transport-time authorization."""

    entries: list[dict[str, Any]] = []
    for tool in manifest["tools"]:
        target = tool["execution_target"]
        mutation = tool["mutation"]
        entries.append(
            {
                "name": tool["name"],
                # Non-Unity tools do not normally cross this transport. Keeping a
                # deterministic fallback still makes the generated table complete.
                "handler": target.get("handler") or tool["name"],
                "requires_unity": tool["availability"]["requires_unity"],
                "mutation_class": mutation["class"],
                "destructive": mutation["destructive"],
                "triggers_compilation": mutation["triggers_compilation"],
                "requires_edit_mode": mutation["requires_edit_mode"],
                "undoable": mutation["undoable"],
                "supports_if_match": tool["preconditions"]["supports_if_match"],
            }
        )
    return entries


def render_python_constant(
    contract_hash: str, manifest: dict[str, Any]
) -> str:
    policies = {
        entry["name"]: {key: value for key, value in entry.items() if key != "name"}
        for entry in runtime_policy_entries(manifest)
    }
    return (
        '"""Generated by tools/tool_contracts.py; do not edit manually."""\n\n'
        "CONTRACT_VERSION = 1\n"
        f'BUILT_IN_SCHEMA_HASH = "{contract_hash}"\n\n'
        "TOOL_CONTRACTS = "
        + pprint.pformat(policies, sort_dicts=True, width=100)
        + "\n"
    )


def _csharp_string(value: str) -> str:
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def render_csharp_constant(
    contract_hash: str, manifest: dict[str, Any]
) -> str:
    policy_lines = []
    for entry in runtime_policy_entries(manifest):
        policy_lines.append(
            "            ["
            + _csharp_string(entry["name"])
            + "] = new CommandRuntimeToolPolicy("
            + ", ".join(
                (
                    _csharp_string(entry["name"]),
                    _csharp_string(entry["handler"]),
                    str(entry["requires_unity"]).lower(),
                    _csharp_string(entry["mutation_class"]),
                    str(entry["destructive"]).lower(),
                    str(entry["triggers_compilation"]).lower(),
                    str(entry["requires_edit_mode"]).lower(),
                    str(entry["undoable"]).lower(),
                    str(entry["supports_if_match"]).lower(),
                )
            )
            + "),"
        )
    policies = "\n".join(policy_lines)
    return f"""// Generated by tools/tool_contracts.py; do not edit manually.
using System;
using System.Collections.Generic;

namespace MCPForUnity.Editor.Services.Transport
{{
    internal sealed class CommandRuntimeToolPolicy
    {{
        internal CommandRuntimeToolPolicy(
            string name,
            string handler,
            bool requiresUnity,
            string mutationClass,
            bool destructive,
            bool triggersCompilation,
            bool requiresEditMode,
            bool undoable,
            bool supportsIfMatch)
        {{
            Name = name;
            Handler = handler;
            RequiresUnity = requiresUnity;
            MutationClass = mutationClass;
            Destructive = destructive;
            TriggersCompilation = triggersCompilation;
            RequiresEditMode = requiresEditMode;
            Undoable = undoable;
            SupportsIfMatch = supportsIfMatch;
        }}

        internal string Name {{ get; }}
        internal string Handler {{ get; }}
        internal bool RequiresUnity {{ get; }}
        internal string MutationClass {{ get; }}
        internal bool Destructive {{ get; }}
        internal bool TriggersCompilation {{ get; }}
        internal bool RequiresEditMode {{ get; }}
        internal bool Undoable {{ get; }}
        internal bool SupportsIfMatch {{ get; }}
    }}

    internal static class CommandRuntimeContract
    {{
        internal const int ContractVersion = 1;
        internal const string BuiltInSchemaHash = \"{contract_hash}\";

        internal static readonly IReadOnlyDictionary<string, CommandRuntimeToolPolicy>
            ToolPolicies = new Dictionary<string, CommandRuntimeToolPolicy>(StringComparer.Ordinal)
        {{
{policies}
        }};
    }}
}}
"""


def update_generated_constants(manifest: dict[str, Any]) -> None:
    contract_hash = compute_manifest_hash(manifest)
    PYTHON_CONSTANT_PATH.write_text(
        render_python_constant(contract_hash, manifest), encoding="utf-8", newline="\n"
    )
    CSHARP_CONSTANT_PATH.write_text(
        render_csharp_constant(contract_hash, manifest), encoding="utf-8", newline="\n"
    )


def check_generated_constants(manifest: dict[str, Any]) -> list[str]:
    contract_hash = compute_manifest_hash(manifest)
    expected = {
        PYTHON_CONSTANT_PATH: render_python_constant(contract_hash, manifest),
        CSHARP_CONSTANT_PATH: render_csharp_constant(contract_hash, manifest),
    }
    errors: list[str] = []
    for path, content in expected.items():
        actual = path.read_text(encoding="utf-8") if path.exists() else None
        if actual != content:
            errors.append(f"generated contract hash is stale: {path.relative_to(REPO_ROOT)}")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser()
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--check", action="store_true")
    action.add_argument("--sync-surface", action="store_true")
    action.add_argument("--update-generated", action="store_true")
    action.add_argument("--print-surface", action="store_true")
    args = parser.parse_args()

    if args.print_surface:
        print(json.dumps(extract_public_surface(), ensure_ascii=False, indent=2))
        return 0

    manifest = load_manifest()
    errors = validate_manifest(manifest)
    if errors:
        for error in errors:
            print(error, file=sys.stderr)
        return 1

    if args.sync_surface:
        manifest = sync_surface(manifest, extract_public_surface())
        MANIFEST_PATH.write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
            newline="\n",
        )
        update_generated_constants(manifest)
        return 0

    if args.update_generated:
        update_generated_constants(manifest)
        return 0

    errors.extend(compare_surface(manifest, extract_public_surface()))
    errors.extend(check_generated_constants(manifest))
    if errors:
        for error in errors:
            print(error, file=sys.stderr)
        return 1

    print(
        f"Validated {len(manifest['tools'])} tool contracts "
        f"({compute_manifest_hash(manifest)})"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
