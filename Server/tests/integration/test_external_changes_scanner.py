import os
import time
from pathlib import Path

import pytest


def test_external_changes_scanner_marks_dirty_and_clears(tmp_path, monkeypatch):
    # Ensure the scanner is active for this unit-style test (not gated by PYTEST_CURRENT_TEST).
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from services.state.external_changes_scanner import ExternalChangesScanner

    # Create a minimal Unity-like layout
    root = tmp_path / "Project"
    (root / "Assets").mkdir(parents=True)
    (root / "ProjectSettings").mkdir(parents=True)
    (root / "Packages").mkdir(parents=True)

    inst = "Test@deadbeef"
    s = ExternalChangesScanner(scan_interval_ms=0, max_entries=10000)
    s.set_project_root(inst, str(root))

    # Create a file before baseline so the initial scan establishes a stable reference point.
    p = root / "Assets" / "x.txt"
    p.write_text("hi")

    # Baseline scan: should not be dirty.
    first = s.update_and_get(inst)
    assert first["external_changes_dirty"] is False
    assert first["external_script_changes_dirty"] is False

    # Touch the file and scan again: should become dirty.
    now = time.time()
    os.utime(p, (now + 10.0, now + 10.0))

    second = s.update_and_get(inst)
    assert second["external_changes_dirty"] is True
    assert second["external_script_changes_dirty"] is False
    assert isinstance(second["external_changes_last_seen_unix_ms"], int)
    assert isinstance(second["dirty_since_unix_ms"], int)

    # Clear and confirm dirty flag resets.
    s.clear_dirty(inst)
    third = s.update_and_get(inst)
    assert third["external_changes_dirty"] is False
    assert third["external_script_changes_dirty"] is False
    assert isinstance(third["last_cleared_unix_ms"], int)


def test_external_changes_scanner_includes_file_dependency_roots(tmp_path, monkeypatch):
    # Ensure the scanner is active for this unit-style test (not gated by PYTEST_CURRENT_TEST).
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from services.state.external_changes_scanner import ExternalChangesScanner

    # Unity project root
    root = tmp_path / "Project"
    (root / "Assets").mkdir(parents=True)
    (root / "ProjectSettings").mkdir(parents=True)
    (root / "Packages").mkdir(parents=True)

    # External local package root (outside project root)
    pkg = tmp_path / "ExternalPkg"
    (pkg / "Editor").mkdir(parents=True)
    target = pkg / "Editor" / "Some.cs"
    target.write_text("// v1")

    # manifest.json referencing file: dependency
    manifest = root / "Packages" / "manifest.json"
    manifest.write_text(
        '{\n  "dependencies": {\n    "com.example.pkg": "file:../../ExternalPkg"\n  }\n}\n',
        encoding="utf-8",
    )

    inst = "Test@deadbeef"
    s = ExternalChangesScanner(scan_interval_ms=0, max_entries=10000)
    s.set_project_root(inst, str(root))

    # Baseline scan captures current mtimes across project + external pkg
    baseline = s.update_and_get(inst)
    assert baseline["external_changes_dirty"] is False

    # Touch external package file and scan again -> should mark dirty
    now = time.time()
    os.utime(target, (now + 10.0, now + 10.0))

    changed = s.update_and_get(inst)
    assert changed["external_changes_dirty"] is True


def test_external_changes_scanner_classifies_scripts_and_ignores_legacy_mcp_logs(tmp_path, monkeypatch):
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from services.state.external_changes_scanner import ExternalChangesScanner

    root = tmp_path / "Project"
    assets = root / "Assets"
    (root / "ProjectSettings").mkdir(parents=True)
    (root / "Packages").mkdir(parents=True)
    legacy_log = assets / "UnityMCP" / "Log" / "mcp.log"
    legacy_log.parent.mkdir(parents=True)
    legacy_log.write_text("baseline")
    marker = assets / "marker.asset"
    marker.write_text("baseline")

    inst = "Test@deadbeef"
    scanner = ExternalChangesScanner(scan_interval_ms=0, max_entries=10000)
    scanner.set_project_root(inst, str(root))
    assert scanner.update_and_get(inst)["external_changes_dirty"] is False

    future = time.time() + 10.0
    legacy_log.write_text("MCP traffic")
    os.utime(legacy_log, (future, future))
    ignored = scanner.update_and_get(inst)
    assert ignored["external_changes_dirty"] is False
    assert ignored["external_script_changes_dirty"] is False

    script = assets / "Runtime" / "Changed.cs"
    script.parent.mkdir(parents=True)
    script.write_text("// changed")
    os.utime(script, (future + 1.0, future + 1.0))
    changed = scanner.update_and_get(inst)
    assert changed["external_changes_dirty"] is True
    assert changed["external_script_changes_dirty"] is True


def test_external_changes_scanner_reports_classified_paths(tmp_path, monkeypatch):
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from services.state.external_changes_scanner import ExternalChangesScanner

    root = tmp_path / "Project"
    assets = root / "Assets"
    settings = root / "ProjectSettings"
    packages = root / "Packages"
    assets.mkdir(parents=True)
    settings.mkdir()
    packages.mkdir()
    marker = assets / "marker.asset"
    marker.write_text("baseline")

    scanner = ExternalChangesScanner(scan_interval_ms=60_000, max_entries=10000)
    scanner.set_project_root("Test@paths", str(root))
    assert scanner.update_and_get("Test@paths")["external_changed_paths"] == []

    changed_files = [
        assets / "Scenes" / "Loaded.unity",
        assets / "Prefabs" / "Open.prefab",
        assets / "Scripts" / "Changed.cs",
        settings / "TagManager.asset",
    ]
    future = time.time() + 10.0
    for index, path in enumerate(changed_files):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(f"changed-{index}")
        os.utime(path, (future + index, future + index))

    # force=True must bypass the long scan interval before a refresh.
    changed = scanner.update_and_get("Test@paths", force=True)
    by_path = {
        item["path"]: item for item in changed["external_changed_paths"]
    }

    assert by_path["Assets/Scenes/Loaded.unity"]["kind"] == "scene"
    assert by_path["Assets/Scenes/Loaded.unity"]["is_unity_yaml"] is True
    assert by_path["Assets/Scenes/Loaded.unity"]["change_type"] == "added"
    assert by_path["Assets/Prefabs/Open.prefab"]["kind"] == "prefab"
    assert by_path["Assets/Scripts/Changed.cs"]["kind"] == "script"
    assert by_path["Assets/Scripts/Changed.cs"]["requires_script_compilation"] is True
    assert by_path["ProjectSettings/TagManager.asset"]["kind"] == "project_settings"
    assert changed["external_changed_paths_truncated"] is False

    scanner.clear_dirty("Test@paths")
    assert scanner.update_and_get("Test@paths", force=True)[
        "external_changed_paths"
    ] == []


def test_external_changes_scanner_detects_yaml_mtime_rollback_and_delete(
    tmp_path, monkeypatch
):
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from services.state.external_changes_scanner import ExternalChangesScanner

    root = tmp_path / "Project"
    scene = root / "Assets" / "Scenes" / "Loaded.unity"
    scene.parent.mkdir(parents=True)
    (root / "ProjectSettings").mkdir()
    (root / "Packages").mkdir()
    scene.write_text("baseline")

    scanner = ExternalChangesScanner(scan_interval_ms=0, max_entries=10000)
    scanner.set_project_root("Test@yaml", str(root))
    assert scanner.update_and_get("Test@yaml")["external_changes_dirty"] is False

    # A checkout can replace contents while moving mtime backwards.
    original_mtime = scene.stat().st_mtime
    scene.write_text("older checkout")
    os.utime(scene, (original_mtime - 10.0, original_mtime - 10.0))
    modified = scanner.update_and_get("Test@yaml", force=True)
    assert modified["external_changes_dirty"] is True
    assert modified["external_changed_paths"][0]["path"] == (
        "Assets/Scenes/Loaded.unity"
    )
    assert modified["external_changed_paths"][0]["change_type"] == "modified"

    scanner.clear_dirty("Test@yaml")
    assert scanner.update_and_get("Test@yaml", force=True)[
        "external_changes_dirty"
    ] is False
    scene.unlink()

    deleted = scanner.update_and_get("Test@yaml", force=True)
    assert deleted["external_changes_dirty"] is True
    assert deleted["external_changed_paths"][0]["path"] == (
        "Assets/Scenes/Loaded.unity"
    )
    assert deleted["external_changed_paths"][0]["change_type"] == "deleted"


def test_external_changes_scanner_acknowledges_exact_editor_scene_save(
    tmp_path, monkeypatch
):
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from services.state.external_changes_scanner import ExternalChangesScanner

    root = tmp_path / "Project"
    scene = root / "Assets" / "Scenes" / "GameScene.unity"
    scene.parent.mkdir(parents=True)
    (root / "ProjectSettings").mkdir()
    (root / "Packages").mkdir()
    scene.write_text("baseline")

    instance_id = "Test@editor-save"
    scanner = ExternalChangesScanner(scan_interval_ms=0, max_entries=10000)
    scanner.set_project_root(instance_id, str(root))
    assert scanner.update_and_get(instance_id)["external_changes_dirty"] is False

    saved_mtime_ns = scene.stat().st_mtime_ns + 2_000_000_037
    scene.write_text("saved by Unity")
    os.utime(scene, ns=(saved_mtime_ns, saved_mtime_ns))
    actual_saved_mtime_ns = scene.stat().st_mtime_ns
    receipt = {
        "epoch": "editor-epoch",
        "sequence": 1,
        "kind": "scene",
        "path": "Assets/Scenes/GameScene.unity",
        "mtime_unix_ns": (actual_saved_mtime_ns // 100) * 100,
        "mtime_unix_100ns": actual_saved_mtime_ns // 100,
        "file_size_bytes": scene.stat().st_size,
        "saved_unix_ms": 1_750_000_000_000,
    }

    assert scanner.acknowledge_editor_save(instance_id, receipt) is True
    assert scanner.acknowledge_editor_save(instance_id, receipt) is True
    clean = scanner.update_and_get(instance_id, force=True)
    assert clean["external_changes_dirty"] is False
    assert clean["external_changed_paths"] == []

    external_mtime_ns = scene.stat().st_mtime_ns + 2_000_000_000
    scene.write_text("external edit")
    os.utime(scene, ns=(external_mtime_ns, external_mtime_ns))
    changed = scanner.update_and_get(instance_id, force=True)
    assert changed["external_changes_dirty"] is True
    assert changed["external_changed_paths"][0]["path"] == (
        "Assets/Scenes/GameScene.unity"
    )


def test_external_changes_scanner_rejects_stale_or_unsafe_editor_save_receipt(
    tmp_path, monkeypatch
):
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from services.state.external_changes_scanner import ExternalChangesScanner

    root = tmp_path / "Project"
    scene = root / "Assets" / "Scenes" / "GameScene.unity"
    scene.parent.mkdir(parents=True)
    (root / "ProjectSettings").mkdir()
    (root / "Packages").mkdir()
    scene.write_text("baseline")

    instance_id = "Test@unsafe-save"
    scanner = ExternalChangesScanner(scan_interval_ms=0, max_entries=10000)
    scanner.set_project_root(instance_id, str(root))
    scanner.update_and_get(instance_id)

    stale = {
        "epoch": "editor-epoch",
        "sequence": 1,
        "kind": "scene",
        "path": "Assets/Scenes/GameScene.unity",
        "mtime_unix_ns": scene.stat().st_mtime_ns - 100,
        "mtime_unix_100ns": scene.stat().st_mtime_ns // 100 - 1,
        "file_size_bytes": scene.stat().st_size,
        "saved_unix_ms": 1_750_000_000_000,
    }
    assert scanner.acknowledge_editor_save(instance_id, stale) is False

    unsafe = dict(stale)
    unsafe["sequence"] = 2
    unsafe["path"] = "../Outside.unity"
    unsafe["mtime_unix_ns"] = scene.stat().st_mtime_ns
    assert scanner.acknowledge_editor_save(instance_id, unsafe) is False

    escaped = root / "Outside.unity"
    escaped.write_text("outside Assets")
    traversal = {
        "epoch": "editor-epoch",
        "sequence": 3,
        "kind": "scene",
        "path": "Assets/../Outside.unity",
        "mtime_unix_100ns": escaped.stat().st_mtime_ns // 100,
        "file_size_bytes": escaped.stat().st_size,
        "saved_unix_ms": 1_750_000_000_000,
    }
    assert scanner.acknowledge_editor_save(instance_id, traversal) is False


def test_external_changes_scanner_acknowledges_scene_and_prefab_save_queue(
    tmp_path, monkeypatch
):
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from services.state.external_changes_scanner import ExternalChangesScanner

    root = tmp_path / "Project"
    scene = root / "Assets" / "Scenes" / "One.unity"
    prefab = root / "Assets" / "Prefabs" / "One.prefab"
    scene.parent.mkdir(parents=True)
    prefab.parent.mkdir(parents=True)
    (root / "ProjectSettings").mkdir()
    (root / "Packages").mkdir()
    scene.write_text("scene baseline")
    prefab.write_text("prefab baseline")

    instance_id = "Test@save-queue"
    scanner = ExternalChangesScanner(scan_interval_ms=0, max_entries=10000)
    scanner.set_project_root(instance_id, str(root))
    scanner.update_and_get(instance_id)

    scene.write_text("scene saved")
    prefab.write_text("prefab saved")
    scene_ns = scene.stat().st_mtime_ns
    prefab_ns = prefab.stat().st_mtime_ns
    receipts = [
        {
            "epoch": "editor-epoch",
            "sequence": 1,
            "kind": "scene",
            "path": "Assets/Scenes/One.unity",
            "mtime_unix_ns": (scene_ns // 100) * 100,
            "mtime_unix_100ns": scene_ns // 100,
            "file_size_bytes": scene.stat().st_size,
            "saved_unix_ms": 1_750_000_000_001,
        },
        {
            "epoch": "editor-epoch",
            "sequence": 2,
            "kind": "prefab",
            "path": "Assets/Prefabs/One.prefab",
            "mtime_unix_ns": (prefab_ns // 100) * 100,
            "mtime_unix_100ns": prefab_ns // 100,
            "file_size_bytes": prefab.stat().st_size,
            "saved_unix_ms": 1_750_000_000_002,
        },
    ]

    assert scanner.acknowledge_editor_saves(instance_id, receipts) == 2
    clean = scanner.update_and_get(instance_id, force=True)
    assert clean["external_changes_dirty"] is False
    assert clean["external_changed_paths"] == []

    wrong_size = dict(receipts[1])
    wrong_size["sequence"] = 3
    wrong_size["file_size_bytes"] += 1
    assert scanner.acknowledge_editor_save(instance_id, wrong_size) is False


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("script_dirty", "expected_compile"),
    [(False, "none"), (True, "request")],
)
async def test_preflight_only_requests_compilation_for_script_changes(
    monkeypatch, script_dirty, expected_compile
):
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from models import MCPResponse
    from services.tools.preflight import preflight
    from services.resources import editor_state as editor_state_module
    from services.tools import refresh_unity as refresh_unity_module
    from .test_helpers import DummyContext

    async def fake_editor_state(ctx):
        return MCPResponse(
            success=True,
            data={
                "assets": {
                    "external_changes_dirty": True,
                    "external_script_changes_dirty": script_dirty,
                },
                "compilation": {
                    "is_compiling": False,
                    "is_domain_reload_pending": False,
                },
            },
        )

    calls = []

    async def fake_refresh(ctx, **kwargs):
        calls.append(kwargs)
        return MCPResponse(success=True)

    monkeypatch.setattr(editor_state_module, "get_editor_state", fake_editor_state)
    monkeypatch.setattr(refresh_unity_module, "refresh_unity", fake_refresh)

    assert await preflight(DummyContext(), refresh_if_dirty=True) is None
    assert calls == [{
        "mode": "if_dirty",
        "scope": "all",
        "compile": expected_compile,
        "wait_for_ready": True,
    }]
