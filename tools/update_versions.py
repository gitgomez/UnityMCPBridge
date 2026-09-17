#!/usr/bin/env python3
"""Synchronize UnityMCPBridge source-release metadata.

The fork publishes matched Unity-package and Python-server sources from one
stable ``vMAJOR.MINOR.PATCH`` tag. This script owns the version fields and
public install examples that must move together.

Usage:
    python tools/update_versions.py --version 10.2.0
    python tools/update_versions.py --check --version 10.2.0
    python tools/update_versions.py --dry-run --version 10.2.0
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Iterable


REPO_ROOT = Path(__file__).resolve().parents[1]
PACKAGE_JSON = REPO_ROOT / "MCPForUnity" / "package.json"
MANIFEST_JSON = REPO_ROOT / "manifest.json"
PYPROJECT_TOML = REPO_ROOT / "Server" / "pyproject.toml"
UV_LOCK = REPO_ROOT / "Server" / "uv.lock"
CLI_INIT = REPO_ROOT / "Server" / "src" / "cli" / "__init__.py"
SERVER_README = REPO_ROOT / "Server" / "README.md"
ROOT_README = REPO_ROOT / "README.md"
FORK_GUIDE = REPO_ROOT / "docs" / "getting-started" / "bridge-fork.md"
INSTALL_GUIDE = REPO_ROOT / "docs" / "getting-started" / "install.md"
RELEASE_GUIDE = REPO_ROOT / "docs" / "contributing" / "releases.md"

STABLE_VERSION_PATTERN = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")
VERSION_TOKEN = r"[0-9]+\.[0-9]+\.[0-9]+"


def relative(path: Path) -> str:
    return str(path.relative_to(REPO_ROOT))


def validate_version(version: str) -> None:
    if not STABLE_VERSION_PATTERN.fullmatch(version):
        raise ValueError(
            f"Release version must use stable MAJOR.MINOR.PATCH syntax; got {version!r}"
        )


def load_package_version() -> str:
    package_data = json.loads(PACKAGE_JSON.read_text(encoding="utf-8"))
    version = package_data.get("version")
    if not version:
        raise ValueError(f"No version found in {relative(PACKAGE_JSON)}")
    return str(version)


def write_json(path: Path, data: dict, dry_run: bool) -> None:
    if not dry_run:
        path.write_text(
            json.dumps(data, indent=2, ensure_ascii=False) + "\n",
            encoding="utf-8",
            newline="\n",
        )


def update_package_json(version: str, dry_run: bool) -> bool:
    data = json.loads(PACKAGE_JSON.read_text(encoding="utf-8"))
    if data.get("version") == version:
        return False
    print(f"Updating {relative(PACKAGE_JSON)}: {data.get('version')} -> {version}")
    data["version"] = version
    write_json(PACKAGE_JSON, data, dry_run)
    return True


def update_manifest_json(version: str, dry_run: bool) -> bool:
    data = json.loads(MANIFEST_JSON.read_text(encoding="utf-8"))
    expected_source = (
        "git+https://github.com/gitgomez/UnityMCPBridge.git"
        f"@v{version}#subdirectory=Server"
    )
    args = data.get("server", {}).get("mcp_config", {}).get("args", [])
    if len(args) < 2:
        raise ValueError(f"Missing server source args in {relative(MANIFEST_JSON)}")

    changed = data.get("version") != version or args[1] != expected_source
    if not changed:
        return False

    print(
        f"Updating {relative(MANIFEST_JSON)}: version and tagged server source -> v{version}"
    )
    data["version"] = version
    args[1] = expected_source
    write_json(MANIFEST_JSON, data, dry_run)
    return True


def replace_required(
    path: Path,
    replacements: Iterable[tuple[str, str]],
    dry_run: bool,
) -> bool:
    content = path.read_text(encoding="utf-8")
    updated = content
    for pattern, replacement in replacements:
        updated, count = re.subn(pattern, replacement, updated, flags=re.MULTILINE)
        if count == 0:
            raise ValueError(f"Expected release marker not found in {relative(path)}: {pattern}")

    if updated == content:
        return False

    print(f"Updating release references in {relative(path)}")
    if not dry_run:
        path.write_text(updated, encoding="utf-8", newline="\n")
    return True


def update_pyproject(version: str, dry_run: bool) -> bool:
    return replace_required(
        PYPROJECT_TOML,
        [(r'^version = "[^"]+"', f'version = "{version}"')],
        dry_run,
    )


def update_uv_lock(version: str, dry_run: bool) -> bool:
    return replace_required(
        UV_LOCK,
        [
            (
                r'(\[\[package\]\]\r?\nname = "mcpforunityserver"\r?\nversion = ")[^"]+("\r?\nsource = \{ editable = "\." \})',
                rf'\g<1>{version}\g<2>',
            )
        ],
        dry_run,
    )


def update_cli_version(version: str, dry_run: bool) -> bool:
    return replace_required(
        CLI_INIT,
        [(r'^__version__ = "[^"]+"', f'__version__ = "{version}"')],
        dry_run,
    )


def update_public_install_examples(version: str, dry_run: bool) -> list[str]:
    package_url = (
        "https://github.com/gitgomez/UnityMCPBridge.git"
        f"?path=/MCPForUnity#v{version}"
    )
    server_url = (
        "git+https://github.com/gitgomez/UnityMCPBridge.git"
        f"@v{version}#subdirectory=Server"
    )
    package_pattern = (
        r"https://github\.com/gitgomez/UnityMCPBridge\.git"
        rf"\?path=/MCPForUnity#v{VERSION_TOKEN}"
    )
    server_pattern = (
        r"git\+https://github\.com/gitgomez/UnityMCPBridge\.git"
        rf"@v{VERSION_TOKEN}#subdirectory=Server"
    )

    targets = [
        (ROOT_README, [(package_pattern, package_url), (server_pattern, server_url)]),
        (
            SERVER_README,
            [
                (server_pattern, server_url),
                (
                    rf"replace `v{VERSION_TOKEN}` with",
                    f"replace `v{version}` with",
                ),
            ],
        ),
        (
            FORK_GUIDE,
            [
                (package_pattern, package_url),
                (server_pattern, server_url),
                (
                    rf"default source for `v{VERSION_TOKEN}` is",
                    f"default source for `v{version}` is",
                ),
            ],
        ),
        (INSTALL_GUIDE, [(package_pattern, package_url), (server_pattern, server_url)]),
        (
            RELEASE_GUIDE,
            [
                (rf"v{VERSION_TOKEN}", f"v{version}"),
                (rf"(?<!v)\b{VERSION_TOKEN}\b", version),
            ],
        ),
    ]

    changed: list[str] = []
    for path, replacements in targets:
        if replace_required(path, replacements, dry_run):
            changed.append(relative(path))
    return changed


def synchronize(version: str, dry_run: bool) -> list[str]:
    validate_version(version)
    changed: list[str] = []

    operations = [
        (PACKAGE_JSON, update_package_json),
        (MANIFEST_JSON, update_manifest_json),
        (PYPROJECT_TOML, update_pyproject),
        (UV_LOCK, update_uv_lock),
        (CLI_INIT, update_cli_version),
    ]
    for path, operation in operations:
        if operation(version, dry_run):
            changed.append(relative(path))

    changed.extend(update_public_install_examples(version, dry_run))
    return changed


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--version",
        help="Stable MAJOR.MINOR.PATCH version (defaults to MCPForUnity/package.json)",
    )
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument(
        "--dry-run",
        action="store_true",
        help="Show required updates without writing files",
    )
    mode.add_argument(
        "--check",
        action="store_true",
        help="Fail if any release-owned file is not synchronized",
    )
    args = parser.parse_args()

    try:
        version = args.version or load_package_version()
        changed = synchronize(version, dry_run=args.dry_run or args.check)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        return 2

    if args.check:
        if changed:
            print("Release metadata is not synchronized:", file=sys.stderr)
            for path in changed:
                print(f"  - {path}", file=sys.stderr)
            return 1
        print(f"Release metadata is synchronized for v{version}.")
        return 0

    if args.dry_run:
        print("Dry run complete. No files were modified.")
    elif changed:
        print(f"Updated {len(changed)} files for v{version}.")
    else:
        print(f"All release metadata already matches v{version}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
