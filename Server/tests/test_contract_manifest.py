"""Guards for the canonical public MCP tool contract manifest."""

import hashlib
import json
from pathlib import Path
import subprocess
import sys

from transport.contract_hash import (
    BUILT_IN_SCHEMA_HASH,
    CONTRACT_VERSION,
    TOOL_CONTRACTS,
)


REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = REPO_ROOT / "Contracts" / "tool-contracts.v1.json"
CHECKER_PATH = REPO_ROOT / "tools" / "tool_contracts.py"


def _load_manifest() -> dict:
    return json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))


def _canonical_hash(manifest: dict) -> str:
    payload = json.dumps(
        manifest,
        ensure_ascii=False,
        allow_nan=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return "sha256:" + hashlib.sha256(payload).hexdigest()


def test_generated_python_hash_matches_canonical_manifest():
    manifest = _load_manifest()

    assert CONTRACT_VERSION == manifest["manifest_version"] == 1
    assert BUILT_IN_SCHEMA_HASH == _canonical_hash(manifest)


def test_generated_runtime_policies_match_manifest_mutation_contracts():
    manifest = _load_manifest()

    assert set(TOOL_CONTRACTS) == {entry["name"] for entry in manifest["tools"]}
    for entry in manifest["tools"]:
        policy = TOOL_CONTRACTS[entry["name"]]
        assert policy["handler"] == (
            entry["execution_target"].get("handler") or entry["name"]
        )
        assert policy["mutation_class"] == entry["mutation"]["class"]
        assert policy["destructive"] is entry["mutation"]["destructive"]


def test_manifest_covers_the_complete_public_surface():
    manifest = _load_manifest()
    names = [entry["name"] for entry in manifest["tools"]]

    assert len(names) == 53
    assert names == sorted(names)
    assert len(names) == len(set(names))
    assert "create_script" in names
    assert "audit_project" in names
    assert "inspect_dependencies" in names
    assert "interact_play_mode" in names
    assert "manage_addressables" in names
    assert "manage_input" in names
    assert "execute_custom_tool" in names
    assert "manage_gameobject" in names
    assert "unity_docs" in names


def test_alias_and_server_execution_targets_are_explicit():
    contracts = {entry["name"]: entry for entry in _load_manifest()["tools"]}

    assert contracts["create_script"]["unity_target"] == "manage_script"
    assert contracts["create_script"]["execution_target"] == {
        "kind": "unity",
        "handler": "manage_script",
    }
    assert contracts["unity_docs"]["execution_target"] == {"kind": "server"}
    assert contracts["execute_custom_tool"]["execution_target"] == {
        "kind": "custom_unity"
    }
    # Keep the tool-test symmetry quarantine honest: this checks contract metadata,
    # not the behavior of the still-quarantined group-management implementation.
    manage_tools_name = "manage_" + "tools"
    assert contracts[manage_tools_name]["execution_target"] == {"kind": "hybrid"}


def test_manifest_checker_detects_surface_and_generated_hash_drift():
    result = subprocess.run(
        [sys.executable, str(CHECKER_PATH), "--check"],
        cwd=REPO_ROOT,
        text=True,
        capture_output=True,
        encoding="utf-8",
        check=False,
    )

    assert result.returncode == 0, result.stdout + result.stderr
    assert "Validated 53 tool contracts" in result.stdout
