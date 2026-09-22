from __future__ import annotations

import re
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
GITHUB_DIR = REPO_ROOT / ".github"
WORKFLOW_DIR = GITHUB_DIR / "workflows"
PINNED_SHA = re.compile(r"[0-9a-f]{40}")


def _workflow_text(name: str) -> str:
    return (WORKFLOW_DIR / name).read_text(encoding="utf-8")


def test_external_actions_are_pinned_to_full_commit_shas() -> None:
    for workflow in GITHUB_DIR.rglob("*.yml"):
        for line_number, line in enumerate(
            workflow.read_text(encoding="utf-8").splitlines(), start=1
        ):
            stripped = line.strip()
            if not stripped.startswith("uses:") and not stripped.startswith("- uses:"):
                continue

            action = stripped.split("uses:", 1)[1].split("#", 1)[0].strip()
            if action.startswith("./"):
                continue

            ref = action.rsplit("@", 1)[-1]
            assert PINNED_SHA.fullmatch(ref), (
                f"{workflow.name}:{line_number} must pin {action!r} to a full commit SHA"
            )


def test_fork_pull_requests_have_no_privileged_target_trigger() -> None:
    workflows = "\n".join(
        workflow.read_text(encoding="utf-8")
        for workflow in WORKFLOW_DIR.glob("*.yml")
    )

    assert "pull_request_target:" not in workflows
    assert "safe-to-test" not in workflows


def test_release_is_restricted_to_gomez() -> None:
    workflow = _workflow_text("release.yml")

    assert 'RELEASE_ACTOR: ${{ github.actor }}' in workflow
    assert '[[ "$RELEASE_ACTOR" != "gitgomez" ]]' in workflow
    assert "contents: write" in workflow
    assert 'gh release create "$GITHUB_REF_NAME"' in workflow
    assert "softprops/action-gh-release" not in workflow


def test_repository_analytics_workflows_are_not_shipped() -> None:
    assert not (WORKFLOW_DIR / "stats.yml").exists()
    assert not (WORKFLOW_DIR / "github-repo-stats.yml").exists()


def test_public_contribution_policy_keeps_unity_credentials_local() -> None:
    contributing = (REPO_ROOT / "CONTRIBUTING.md").read_text(encoding="utf-8")
    releases = (REPO_ROOT / "docs" / "contributing" / "releases.md").read_text(
        encoding="utf-8"
    )

    assert "Open the pull request against `optimize/bridge`" in contributing
    assert "Do not upload Unity credentials to GitHub for CI." in contributing
    assert "publishes version tags." in contributing
    assert "does not store Unity account or license credentials in GitHub" in releases


def test_release_promotion_preserves_verified_noreply_commits() -> None:
    releases = (REPO_ROOT / "docs" / "contributing" / "releases.md").read_text(
        encoding="utf-8"
    )

    assert "do not use the GitHub merge" in releases
    assert "git push origin <release-commit>:main" in releases
    assert "committer:%cn <%ce>" in releases
