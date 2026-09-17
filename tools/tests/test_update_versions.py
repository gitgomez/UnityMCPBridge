import importlib.util
import json
from pathlib import Path

import pytest


SCRIPT_PATH = Path(__file__).resolve().parents[1] / "update_versions.py"
SPEC = importlib.util.spec_from_file_location("update_versions", SCRIPT_PATH)
update_versions = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(update_versions)


@pytest.fixture
def release_tree(tmp_path, monkeypatch):
    paths = {
        "PACKAGE_JSON": tmp_path / "MCPForUnity" / "package.json",
        "MANIFEST_JSON": tmp_path / "manifest.json",
        "PYPROJECT_TOML": tmp_path / "Server" / "pyproject.toml",
        "UV_LOCK": tmp_path / "Server" / "uv.lock",
        "CLI_INIT": tmp_path / "Server" / "src" / "cli" / "__init__.py",
        "SERVER_README": tmp_path / "Server" / "README.md",
        "ROOT_README": tmp_path / "README.md",
        "FORK_GUIDE": tmp_path / "docs" / "getting-started" / "bridge-fork.md",
        "INSTALL_GUIDE": tmp_path / "docs" / "getting-started" / "install.md",
        "RELEASE_GUIDE": tmp_path / "docs" / "contributing" / "releases.md",
    }
    for path in paths.values():
        path.parent.mkdir(parents=True, exist_ok=True)

    paths["PACKAGE_JSON"].write_text(
        json.dumps({"name": "com.coplaydev.unity-mcp", "version": "10.1.0"}),
        encoding="utf-8",
    )
    paths["MANIFEST_JSON"].write_text(
        json.dumps(
            {
                "version": "10.1.0",
                "server": {
                    "mcp_config": {
                        "args": [
                            "--from",
                            "git+https://github.com/gitgomez/UnityMCPBridge.git@v10.1.0#subdirectory=Server",
                            "mcp-for-unity",
                        ]
                    }
                },
            }
        ),
        encoding="utf-8",
    )
    paths["PYPROJECT_TOML"].write_text('version = "10.1.0"\n', encoding="utf-8")
    paths["UV_LOCK"].write_text(
        '[[package]]\nname = "mcpforunityserver"\nversion = "10.1.0"\nsource = { editable = "." }\n',
        encoding="utf-8",
    )
    paths["CLI_INIT"].write_text('__version__ = "1.0.0"\n', encoding="utf-8")

    package_url = "https://github.com/gitgomez/UnityMCPBridge.git?path=/MCPForUnity#v10.1.0"
    server_url = "git+https://github.com/gitgomez/UnityMCPBridge.git@v10.1.0#subdirectory=Server"
    paths["ROOT_README"].write_text(f"{package_url}\n{server_url}\n", encoding="utf-8")
    paths["SERVER_README"].write_text(
        f"{server_url}\nreplace `v10.1.0` with `optimize/bridge`\n",
        encoding="utf-8",
    )
    paths["FORK_GUIDE"].write_text(
        f"{package_url}\n{server_url}\nThe default source for `v10.1.0` is:\n",
        encoding="utf-8",
    )
    paths["INSTALL_GUIDE"].write_text(f"{package_url}\n{server_url}\n", encoding="utf-8")
    paths["RELEASE_GUIDE"].write_text(
        "python tools/update_versions.py --version 10.1.0\ngit tag -a v10.1.0\n",
        encoding="utf-8",
    )

    monkeypatch.setattr(update_versions, "REPO_ROOT", tmp_path)
    for name, path in paths.items():
        monkeypatch.setattr(update_versions, name, path)
    return paths


def test_synchronize_updates_all_release_owned_surfaces(release_tree):
    changed = update_versions.synchronize("10.2.0", dry_run=False)

    assert len(changed) == 10
    assert json.loads(release_tree["PACKAGE_JSON"].read_text())["version"] == "10.2.0"
    manifest = json.loads(release_tree["MANIFEST_JSON"].read_text())
    assert manifest["version"] == "10.2.0"
    assert "@v10.2.0#subdirectory=Server" in manifest["server"]["mcp_config"]["args"][1]
    assert 'version = "10.2.0"' in release_tree["PYPROJECT_TOML"].read_text()
    assert 'version = "10.2.0"' in release_tree["UV_LOCK"].read_text()
    assert '__version__ = "10.2.0"' in release_tree["CLI_INIT"].read_text()

    for name in (
        "SERVER_README",
        "ROOT_README",
        "FORK_GUIDE",
        "INSTALL_GUIDE",
        "RELEASE_GUIDE",
    ):
        content = release_tree[name].read_text(encoding="utf-8")
        assert "10.1.0" not in content
        assert "10.2.0" in content

    assert update_versions.synchronize("10.2.0", dry_run=True) == []


def test_dry_run_reports_drift_without_writing(release_tree):
    before = release_tree["PACKAGE_JSON"].read_text(encoding="utf-8")

    changed = update_versions.synchronize("10.2.0", dry_run=True)

    assert len(changed) == 10
    assert release_tree["PACKAGE_JSON"].read_text(encoding="utf-8") == before


@pytest.mark.parametrize("version", ["10.2", "v10.2.0", "10.2.0-beta.1", "10.2.0+fork"])
def test_release_version_must_be_stable_semver(version):
    with pytest.raises(ValueError, match="MAJOR.MINOR.PATCH"):
        update_versions.validate_version(version)
