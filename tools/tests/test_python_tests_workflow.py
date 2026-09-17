from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = REPO_ROOT / ".github" / "workflows" / "python-tests.yml"
RELEASE_WORKFLOW = REPO_ROOT / ".github" / "workflows" / "release.yml"
UNITY_WORKFLOW = REPO_ROOT / ".github" / "workflows" / "unity-tests.yml"


def test_local_harness_tests_run_from_repository_root_with_server_environment():
    workflow = WORKFLOW.read_text(encoding="utf-8")

    assert "uv run --project Server python -m pytest tools/tests/" in workflow
    assert 'uv run python -m pytest "$GITHUB_WORKSPACE/tools/tests/"' not in workflow


def test_release_labels_unity_verification_as_optional_without_credentials():
    release_workflow = RELEASE_WORKFLOW.read_text(encoding="utf-8")
    unity_workflow = UNITY_WORKFLOW.read_text(encoding="utf-8")

    assert "Unity verification (optional hosted runner)" in release_workflow
    assert "No Unity credentials are required for source publication" in unity_workflow
    assert "Local Unity verification remains a separate maintainer workflow" in unity_workflow
