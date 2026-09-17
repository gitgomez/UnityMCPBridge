from __future__ import annotations

import os
import json
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable


_SCRIPT_COMPILE_SUFFIXES = frozenset({".cs", ".asmdef", ".asmref", ".rsp"})
_MAX_REPORTED_CHANGED_PATHS = 200
_MAX_ACKNOWLEDGED_EDITOR_SAVES = 256


def _now_unix_ms() -> int:
    return int(time.time() * 1000)


def _in_pytest() -> bool:
    # Keep scanner inert during the Python integration suite unless explicitly invoked.
    return bool(os.environ.get("PYTEST_CURRENT_TEST"))


@dataclass
class ExternalChangesState:
    project_root: str | None = None
    last_scan_unix_ms: int | None = None
    last_seen_mtime_ns: int | None = None
    last_seen_script_mtime_ns: int | None = None
    dirty: bool = False
    script_dirty: bool = False
    dirty_since_unix_ms: int | None = None
    external_changes_last_seen_unix_ms: int | None = None
    last_cleared_unix_ms: int | None = None
    # Cached package roots referenced by Packages/manifest.json "file:" dependencies
    extra_roots: list[str] | None = None
    manifest_last_mtime_ns: int | None = None
    changed_paths: dict[str, dict[str, Any]] = field(default_factory=dict)
    changed_paths_truncated: bool = False
    unity_yaml_mtimes_ns: dict[str, int] = field(default_factory=dict)
    unity_yaml_baseline_initialized: bool = False
    acknowledged_editor_save_tokens: list[tuple[str, int]] = field(
        default_factory=list
    )


@dataclass
class ExternalScanResult:
    newest_mtime_ns: int | None
    newest_script_mtime_ns: int | None
    changed_paths: list[dict[str, Any]]
    changed_paths_truncated: bool
    unity_yaml_mtimes_ns: dict[str, int]
    complete: bool


def _snapshot(st: ExternalChangesState) -> dict[str, Any]:
    return {
        "external_changes_dirty": st.dirty,
        "external_script_changes_dirty": st.script_dirty,
        "external_changes_last_seen_unix_ms": st.external_changes_last_seen_unix_ms,
        "dirty_since_unix_ms": st.dirty_since_unix_ms,
        "last_cleared_unix_ms": st.last_cleared_unix_ms,
        "external_changed_paths": [
            dict(item) for item in st.changed_paths.values()
        ],
        "external_changed_paths_truncated": st.changed_paths_truncated,
    }


def _classify_changed_path(path: Path, project_root: Path) -> dict[str, Any]:
    try:
        relative = path.resolve().relative_to(project_root.resolve())
        display_path = relative.as_posix()
        source = "project"
    except (OSError, ValueError):
        display_path = path.resolve().as_posix()
        source = "local_package"

    suffix = path.suffix.casefold()
    display_lower = display_path.casefold()
    if suffix == ".unity":
        kind = "scene"
    elif suffix == ".prefab":
        kind = "prefab"
    elif suffix in _SCRIPT_COMPILE_SUFFIXES:
        kind = "script"
    elif display_lower.startswith("projectsettings/"):
        kind = "project_settings"
    elif display_lower.startswith("packages/"):
        kind = "package"
    elif source == "local_package":
        kind = "local_package"
    else:
        kind = "asset"

    return {
        "path": display_path,
        "kind": kind,
        "source": source,
        "extension": suffix,
        "requires_script_compilation": suffix in _SCRIPT_COMPILE_SUFFIXES,
        "is_unity_yaml": suffix in {".unity", ".prefab"},
    }


class ExternalChangesScanner:
    """
    Lightweight external-changes detector using recursive max-mtime scan.

    This is intentionally conservative:
    - It only marks dirty when it sees a strictly newer mtime than the baseline.
    - It scans at most once per scan_interval_ms per instance to keep overhead bounded.
    """

    def __init__(self, *, scan_interval_ms: int = 1500, max_entries: int = 20000):
        self._states: dict[str, ExternalChangesState] = {}
        self._scan_interval_ms = int(scan_interval_ms)
        self._max_entries = int(max_entries)

    def _get_state(self, instance_id: str) -> ExternalChangesState:
        return self._states.setdefault(instance_id, ExternalChangesState())

    def set_project_root(self, instance_id: str, project_root: str | None) -> None:
        st = self._get_state(instance_id)
        if project_root:
            st.project_root = project_root

    def clear_dirty(self, instance_id: str) -> None:
        st = self._get_state(instance_id)
        st.dirty = False
        st.script_dirty = False
        st.dirty_since_unix_ms = None
        st.last_cleared_unix_ms = _now_unix_ms()
        # Reset baseline to “now” on next scan.
        st.last_seen_mtime_ns = None
        st.last_seen_script_mtime_ns = None
        st.changed_paths.clear()
        st.changed_paths_truncated = False
        st.unity_yaml_mtimes_ns.clear()
        st.unity_yaml_baseline_initialized = False

    def acknowledge_editor_save(
        self,
        instance_id: str,
        receipt: dict[str, Any] | None,
    ) -> bool:
        """Accept an Editor-authored Unity YAML save only when its file signature matches.

        The exact-match check is important: a path-only acknowledgement could hide an
        external write that raced the Editor save before the next scanner pass.
        """
        st = self._get_state(instance_id)
        if not st.project_root or not isinstance(receipt, dict):
            return False

        epoch = receipt.get("epoch")
        sequence = receipt.get("sequence")
        raw_path = receipt.get("path")
        mtime_100ns = receipt.get("mtime_unix_100ns")
        legacy_mtime_ns = receipt.get("mtime_unix_ns")
        file_size_bytes = receipt.get("file_size_bytes")
        kind = receipt.get("kind")
        has_mtime_100ns = (
            type(mtime_100ns) is int and mtime_100ns > 0
        )
        has_legacy_mtime_ns = (
            type(legacy_mtime_ns) is int and legacy_mtime_ns > 0
        )
        if (
            not isinstance(epoch, str)
            or not epoch
            or isinstance(sequence, bool)
            or not isinstance(sequence, int)
            or sequence <= 0
            or not isinstance(raw_path, str)
            or not raw_path
            or (mtime_100ns is not None and not has_mtime_100ns)
            or not (has_mtime_100ns or has_legacy_mtime_ns)
            or (
                file_size_bytes is not None
                and (type(file_size_bytes) is not int or file_size_bytes < 0)
            )
        ):
            return False

        token = (epoch, sequence)
        if token in st.acknowledged_editor_save_tokens:
            return True

        relative = Path(raw_path.replace("\\", "/"))
        suffix = relative.suffix.casefold()
        if (
            relative.is_absolute()
            or suffix not in {".unity", ".prefab"}
            or (kind == "scene" and suffix != ".unity")
            or (kind == "prefab" and suffix != ".prefab")
            or kind not in {None, "scene", "prefab"}
            or not relative.as_posix().casefold().startswith("assets/")
        ):
            return False

        root = Path(st.project_root).resolve()
        assets_root = (root / "Assets").resolve()
        target = (root / relative).resolve()
        try:
            target.relative_to(assets_root)
            stat = target.stat()
        except (OSError, ValueError):
            return False

        current_mtime_ns = int(getattr(
            stat, "st_mtime_ns", int(stat.st_mtime * 1_000_000_000)
        ))
        if has_mtime_100ns:
            if current_mtime_ns // 100 != mtime_100ns:
                return False
        elif current_mtime_ns != legacy_mtime_ns:
            return False
        if file_size_bytes is not None and stat.st_size != file_size_bytes:
            return False

        normalized_path = relative.as_posix()
        normalized_key = normalized_path.casefold()
        if st.unity_yaml_baseline_initialized:
            baseline_key = next(
                (
                    key
                    for key in st.unity_yaml_mtimes_ns
                    if key.casefold() == normalized_key
                ),
                normalized_path,
            )
            st.unity_yaml_mtimes_ns[baseline_key] = current_mtime_ns

        st.changed_paths.pop(normalized_key, None)
        if (
            not st.changed_paths
            and not st.changed_paths_truncated
            and not st.script_dirty
        ):
            st.dirty = False
            st.dirty_since_unix_ms = None

        st.acknowledged_editor_save_tokens.append(token)
        if len(st.acknowledged_editor_save_tokens) > _MAX_ACKNOWLEDGED_EDITOR_SAVES:
            del st.acknowledged_editor_save_tokens[
                :-_MAX_ACKNOWLEDGED_EDITOR_SAVES
            ]
        return True

    def acknowledge_editor_saves(
        self,
        instance_id: str,
        receipts: list[dict[str, Any]] | None,
    ) -> int:
        """Acknowledge every independently verified receipt in a bounded Editor queue."""
        if not isinstance(receipts, list):
            return 0
        acknowledged = 0
        for receipt in receipts:
            if self.acknowledge_editor_save(instance_id, receipt):
                acknowledged += 1
        return acknowledged

    def _scan_paths_max_mtimes_ns(
        self,
        roots: Iterable[Path],
        *,
        project_root: Path,
        changed_after_ns: int | None,
        script_changed_after_ns: int | None,
    ) -> ExternalScanResult:
        newest: int | None = None
        newest_script: int | None = None
        changed_paths: dict[str, dict[str, Any]] = {}
        changed_paths_truncated = False
        unity_yaml_mtimes_ns: dict[str, int] = {}
        entries = 0

        def result(*, complete: bool) -> ExternalScanResult:
            return ExternalScanResult(
                newest_mtime_ns=newest,
                newest_script_mtime_ns=newest_script,
                changed_paths=list(changed_paths.values()),
                changed_paths_truncated=(
                    changed_paths_truncated or not complete
                ),
                unity_yaml_mtimes_ns=unity_yaml_mtimes_ns,
                complete=complete,
            )

        for root in roots:
            if not root.exists():
                continue

            # Walk the tree; skip common massive/irrelevant dirs (Library/Temp/Logs).
            for dirpath, dirnames, filenames in os.walk(str(root)):
                entries += 1
                if entries > self._max_entries:
                    return result(complete=False)

                dp = Path(dirpath)
                name = dp.name.lower()
                if name in {"library", "temp", "logs", "obj", ".git", "node_modules"}:
                    dirnames[:] = []
                    continue

                # Legacy bridge versions wrote execution logs below Assets. Ignore
                # that exact directory so MCP traffic cannot dirty its own project.
                tail = tuple(part.casefold() for part in dp.parts[-3:])
                if tail == ("assets", "unitymcp", "log"):
                    dirnames[:] = []
                    continue

                # Allow skipping hidden directories quickly
                dirnames[:] = [d for d in dirnames if not d.startswith(".")]

                for fn in filenames:
                    if fn.startswith("."):
                        continue
                    entries += 1
                    if entries > self._max_entries:
                        return result(complete=False)
                    p = dp / fn
                    try:
                        stat = p.stat()
                    except OSError:
                        continue
                    m = getattr(stat, "st_mtime_ns", None)
                    if m is None:
                        # Fallback when st_mtime_ns is unavailable
                        m = int(stat.st_mtime * 1_000_000_000)
                    suffix = p.suffix.casefold()
                    if suffix in {".unity", ".prefab"}:
                        yaml_item = _classify_changed_path(p, project_root)
                        yaml_key = str(yaml_item["path"])
                        unity_yaml_mtimes_ns[yaml_key] = int(m)
                        continue

                    # Unity YAML has its own per-file baseline so rollbacks and
                    # deletions are detectable. Keeping it out of the generic max
                    # also lets an exact Editor-save receipt advance only that file.
                    newest = m if newest is None else max(newest, int(m))
                    if suffix in _SCRIPT_COMPILE_SUFFIXES:
                        newest_script = m if newest_script is None else max(
                            newest_script, int(m))

                    threshold = (
                        script_changed_after_ns
                        if suffix in _SCRIPT_COMPILE_SUFFIXES
                        else changed_after_ns
                    )
                    changed = (
                        threshold is not None and int(m) > threshold
                    ) or (
                        suffix in _SCRIPT_COMPILE_SUFFIXES
                        and changed_after_ns is not None
                        and script_changed_after_ns is None
                    )
                    if not changed:
                        continue

                    item = _classify_changed_path(p, project_root)
                    item["change_type"] = "modified_or_added"
                    key = str(item["path"]).casefold()
                    if key in changed_paths:
                        continue
                    if len(changed_paths) >= _MAX_REPORTED_CHANGED_PATHS:
                        changed_paths_truncated = True
                        continue
                    changed_paths[key] = item

        return result(complete=True)

    def _resolve_manifest_extra_roots(self, project_root: Path, st: ExternalChangesState) -> list[Path]:
        """
        Parse Packages/manifest.json for local file: dependencies and resolve them to absolute paths.
        Returns a list of Paths that exist and are directories.
        """
        manifest_path = project_root / "Packages" / "manifest.json"
        try:
            stat = manifest_path.stat()
        except OSError:
            st.extra_roots = []
            st.manifest_last_mtime_ns = None
            return []

        mtime_ns = getattr(stat, "st_mtime_ns", int(
            stat.st_mtime * 1_000_000_000))
        if st.extra_roots is not None and st.manifest_last_mtime_ns == mtime_ns:
            return [Path(p) for p in st.extra_roots if p]

        try:
            raw = manifest_path.read_text(encoding="utf-8")
            doc = json.loads(raw)
        except Exception:
            st.extra_roots = []
            st.manifest_last_mtime_ns = mtime_ns
            return []

        deps = doc.get("dependencies") if isinstance(doc, dict) else None
        if not isinstance(deps, dict):
            st.extra_roots = []
            st.manifest_last_mtime_ns = mtime_ns
            return []

        roots: list[str] = []
        base_dir = manifest_path.parent

        for _, ver in deps.items():
            if not isinstance(ver, str):
                continue
            v = ver.strip()
            if not v.startswith("file:"):
                continue
            suffix = v[len("file:"):].strip()
            # Handle file:///abs/path or file:/abs/path
            if suffix.startswith("///"):
                candidate = Path("/" + suffix.lstrip("/"))
            elif suffix.startswith("/"):
                candidate = Path(suffix)
            else:
                candidate = (base_dir / suffix).resolve()
            try:
                if candidate.exists() and candidate.is_dir():
                    roots.append(str(candidate))
            except OSError:
                continue

        # De-dupe, preserve order
        deduped: list[str] = []
        seen = set()
        for r in roots:
            if r not in seen:
                seen.add(r)
                deduped.append(r)

        st.extra_roots = deduped
        st.manifest_last_mtime_ns = mtime_ns
        return [Path(p) for p in deduped if p]

    def update_and_get(
        self,
        instance_id: str,
        *,
        force: bool = False,
    ) -> dict[str, Any]:
        """
        Returns a small dict suitable for embedding in editor_state_v2.assets:
          - external_changes_dirty
          - external_script_changes_dirty
          - external_changes_last_seen_unix_ms
          - dirty_since_unix_ms
          - last_cleared_unix_ms
          - external_changed_paths (bounded, classified path details)
          - external_changed_paths_truncated
        """
        st = self._get_state(instance_id)

        if _in_pytest():
            return _snapshot(st)

        now = _now_unix_ms()
        if (not force
                and st.last_scan_unix_ms is not None
                and (now - st.last_scan_unix_ms) < self._scan_interval_ms):
            return _snapshot(st)

        st.last_scan_unix_ms = now

        project_root = st.project_root
        if not project_root:
            return _snapshot(st)

        root = Path(project_root)
        paths = [root / "Assets", root / "ProjectSettings", root / "Packages"]
        # Include any local package roots referenced by file: deps in Packages/manifest.json
        try:
            paths.extend(self._resolve_manifest_extra_roots(root, st))
        except Exception:
            pass
        scan = self._scan_paths_max_mtimes_ns(
            paths,
            project_root=root,
            changed_after_ns=st.last_seen_mtime_ns,
            script_changed_after_ns=st.last_seen_script_mtime_ns,
        )
        newest = scan.newest_mtime_ns
        newest_script = scan.newest_script_mtime_ns
        if (newest is None
                and not (
                    scan.complete
                    and (
                        st.unity_yaml_baseline_initialized
                        or bool(scan.unity_yaml_mtimes_ns)
                    )
                )):
            return _snapshot(st)

        has_baseline = st.last_seen_mtime_ns is not None
        if newest is not None:
            if not has_baseline:
                st.last_seen_mtime_ns = newest
                st.last_seen_script_mtime_ns = newest_script
            elif newest > st.last_seen_mtime_ns:
                st.last_seen_mtime_ns = newest
                st.external_changes_last_seen_unix_ms = now
                if not st.dirty:
                    st.dirty = True
                    st.dirty_since_unix_ms = now

        if has_baseline and newest_script is not None:
            if (st.last_seen_script_mtime_ns is None
                    or newest_script > st.last_seen_script_mtime_ns):
                st.last_seen_script_mtime_ns = newest_script
                st.script_dirty = True
                st.external_changes_last_seen_unix_ms = now
                if not st.dirty:
                    st.dirty = True
                    st.dirty_since_unix_ms = now

        yaml_changes: list[dict[str, Any]] = []
        if scan.complete:
            if st.unity_yaml_baseline_initialized:
                previous_yaml = st.unity_yaml_mtimes_ns
                current_yaml = scan.unity_yaml_mtimes_ns
                for key, current_mtime in current_yaml.items():
                    previous_mtime = previous_yaml.get(key)
                    if previous_mtime == current_mtime:
                        continue
                    path = Path(key)
                    if not path.is_absolute():
                        path = root / key
                    item = _classify_changed_path(path, root)
                    item["change_type"] = (
                        "added" if previous_mtime is None else "modified"
                    )
                    yaml_changes.append(item)
                for key in previous_yaml.keys() - current_yaml.keys():
                    path = Path(key)
                    if not path.is_absolute():
                        path = root / key
                    item = _classify_changed_path(path, root)
                    item["change_type"] = "deleted"
                    yaml_changes.append(item)

            st.unity_yaml_mtimes_ns = dict(scan.unity_yaml_mtimes_ns)
            st.unity_yaml_baseline_initialized = True

        if yaml_changes:
            st.external_changes_last_seen_unix_ms = now
            if not st.dirty:
                st.dirty = True
                st.dirty_since_unix_ms = now

        for item in scan.changed_paths + yaml_changes:
            key = str(item.get("path", "")).casefold()
            if not key:
                continue
            if (key not in st.changed_paths
                    and len(st.changed_paths) >= _MAX_REPORTED_CHANGED_PATHS):
                st.changed_paths_truncated = True
                break
            st.changed_paths[key] = item
        st.changed_paths_truncated = (
            st.changed_paths_truncated or scan.changed_paths_truncated
        )

        return _snapshot(st)


# Global singleton (simple, process-local)
external_changes_scanner = ExternalChangesScanner()
