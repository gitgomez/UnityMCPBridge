from __future__ import annotations

from pathlib import Path
import re

import pytest

from tools.validate_unity_test_results import (
    UnityResultValidationError,
    main,
    validate_test_results,
)


REPO_ROOT = Path(__file__).resolve().parents[2]


def _write_results(path: Path, **overrides: str) -> Path:
    attributes = {
        "result": "Passed",
        "total": "1",
        "passed": "1",
        "failed": "0",
        "inconclusive": "0",
        "skipped": "0",
    }
    attributes.update(overrides)
    serialized = " ".join(f'{key}="{value}"' for key, value in attributes.items())
    path.write_text(f"<test-run {serialized}></test-run>", encoding="utf-8")
    return path


def test_validate_test_results_accepts_real_passing_run(tmp_path):
    summary = validate_test_results(_write_results(tmp_path / "results.xml"))

    assert summary.total == 1
    assert summary.passed == 1
    assert summary.failed == 0
    assert summary.result == "Passed"


def test_validate_test_results_accepts_ignored_cases_when_others_pass(tmp_path):
    results = _write_results(
        tmp_path / "results.xml",
        result="Skipped:Ignored",
        total="2",
        passed="1",
        skipped="1",
    )

    summary = validate_test_results(results)

    assert summary.passed == 1
    assert summary.skipped == 1


@pytest.mark.parametrize(
    ("overrides", "message"),
    [
        ({"total": "0", "passed": "0"}, "zero executed tests"),
        (
            {
                "result": "Skipped:Ignored",
                "passed": "0",
                "skipped": "1",
            },
            "zero passed tests",
        ),
        ({"result": "Failed", "failed": "1", "passed": "0"}, "1 of 1"),
        ({"result": "Inconclusive"}, "did not report a passing"),
    ],
)
def test_validate_test_results_rejects_non_passing_runs(
    tmp_path,
    overrides,
    message,
):
    results = _write_results(tmp_path / "results.xml", **overrides)

    with pytest.raises(UnityResultValidationError, match=message):
        validate_test_results(results)


def test_validate_test_results_rejects_missing_and_malformed_xml(tmp_path):
    with pytest.raises(UnityResultValidationError, match="was not created"):
        validate_test_results(tmp_path / "missing.xml")

    malformed = tmp_path / "malformed.xml"
    malformed.write_text("<test-run", encoding="utf-8")
    with pytest.raises(UnityResultValidationError, match="cannot be parsed"):
        validate_test_results(malformed)


def test_validator_cli_reports_summary_and_failure(tmp_path, capsys):
    passing = _write_results(tmp_path / "passing.xml")
    assert main([str(passing)]) == 0
    assert "1 passed" in capsys.readouterr().out

    assert main([str(tmp_path / "missing.xml")]) == 1
    assert "was not created" in capsys.readouterr().err


def test_windows_full_runner_owns_results_and_omits_quit():
    script = (REPO_ROOT / "tools" / "check-unity-versions.ps1").read_text(
        encoding="utf-8"
    )
    local_runner = re.search(
        r"function Invoke-LocalUnity.*?function Invoke-DockerUnity",
        script,
        re.DOTALL,
    )
    assert local_runner is not None
    full_args = re.search(
        r"if \(\$Full\) \{.*?\$unityArgs = @\((.*?)\)\s*\} else",
        local_runner.group(0),
        re.DOTALL,
    )
    assert full_args is not None
    assert '"-runTests"' in full_args.group(1)
    assert '"-testResults", $resultFile' in full_args.group(1)
    assert '"-quit"' not in full_args.group(1)
    assert "Invoke-TestResultValidator" in script


def test_shell_full_runners_own_results_and_omit_quit():
    script = (REPO_ROOT / "tools" / "check-unity-versions.sh").read_text(
        encoding="utf-8"
    )
    local_runner = re.search(r"run_local\(\).*?run_docker\(\)", script, re.DOTALL)
    assert local_runner is not None
    local_args = re.search(
        r"if \[\[ \$FULL -eq 1 \]\]; then.*?"
        r"args=\((.*?)\)\s*else",
        local_runner.group(0),
        re.DOTALL,
    )
    assert local_args is not None
    assert "-runTests" in local_args.group(1)
    assert '-testResults "$result_file"' in local_args.group(1)
    assert "-quit" not in local_args.group(1)

    docker_runner = re.search(
        r"run_docker\(\).*?# ---- main loop",
        script,
        re.DOTALL,
    )
    assert docker_runner is not None
    full_extra = re.search(
        r"if \[\[ \$FULL -eq 1 \]\]; then.*?"
        r"unity_extra=\"(.*?)\"\s*else",
        docker_runner.group(0),
        re.DOTALL,
    )
    assert full_extra is not None
    assert "-runTests" in full_extra.group(1)
    assert "-testResults" in full_extra.group(1)
    assert "-quit" not in full_extra.group(1)
    assert "validate_results" in script
