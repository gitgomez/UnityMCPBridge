"""Regression checks for the repository's public license boundary."""

from __future__ import annotations

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


def read(relative_path: str) -> str:
    return (ROOT / relative_path).read_text(encoding="utf-8")


def test_distributable_surfaces_carry_the_project_license() -> None:
    root_license = read("LICENSE")

    assert "Copyright (c) 2025 CoplayDev" in root_license
    assert "Copyright (c) 2026 Gomez" in root_license
    assert read("Server/LICENSE") == root_license
    assert read("MCPForUnity/LICENSE.md") == root_license
    assert "Python Server" in read("Server/THIRD_PARTY_NOTICES.md")
    assert "LICENSE.md" in read("MCPForUnity/README.md")
    assert "THIRD_PARTY_NOTICES.md" in read("MCPForUnity/README.md")


def test_third_party_notices_cover_included_source() -> None:
    root_notice = read("THIRD_PARTY_NOTICES.md")
    package_notice = read("MCPForUnity/THIRD_PARTY_NOTICES.md")
    tommy_header = "\n".join(read("MCPForUnity/Editor/External/Tommy.cs").splitlines()[:28])

    assert "Tommy" in root_notice
    assert "Denis Zhidkikh" in root_notice
    assert "Tommy" in package_notice
    assert "MIT License" in tommy_header
    assert "Copyright (c) 2020 Denis Zhidkikh" in tommy_header


def test_package_metadata_points_to_license_information() -> None:
    unity_package = json.loads(read("MCPForUnity/package.json"))
    server_project = read("Server/pyproject.toml")

    assert unity_package["licensesUrl"].endswith("/LICENSE")
    assert 'license = "MIT"' in server_project
    assert 'license-files = ["LICENSE", "THIRD_PARTY_NOTICES.md"]' in server_project


def test_obsolete_asset_store_distribution_is_absent() -> None:
    assert not (ROOT / "TestProjects/AssetStoreUploads").exists()
    assert not (ROOT / "tools/prepare_unity_asset_store_release.py").exists()
    assert not (ROOT / "docs/images/coplay-logo.png").exists()
    assert not (ROOT / "website").exists()


def test_legacy_non_english_documentation_variants_are_absent() -> None:
    assert not (ROOT / "docs/i18n/README-zh.md").exists()
    assert not (ROOT / "docs/development/README-DEV-zh.md").exists()

    public_markdown = [
        ROOT / "README.md",
        ROOT / "CONTRIBUTING.md",
        ROOT / "CODE_OF_CONDUCT.md",
        ROOT / "SUPPORT.md",
        *sorted((ROOT / "docs").rglob("*.md")),
    ]
    tracked_markdown = "\n".join(
        path.read_text(encoding="utf-8") for path in public_markdown
    )
    assert "README-zh" not in tracked_markdown
    assert "README-DEV-zh" not in tracked_markdown


def test_public_entry_points_link_the_notice() -> None:
    assert "THIRD_PARTY_NOTICES.md" in read("README.md")
    assert "THIRD_PARTY_NOTICES.md" in read("Server/README.md")
