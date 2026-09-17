#!/usr/bin/env python3
"""Validate that a Unity Test Framework XML represents a real passing run."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


class UnityResultValidationError(ValueError):
    """Raised when a Unity test result cannot prove a passing test run."""


@dataclass(frozen=True)
class TestResultSummary:
    total: int
    passed: int
    failed: int
    inconclusive: int
    skipped: int
    result: str


def _required_count(root: ET.Element, name: str) -> int:
    value = root.attrib.get(name)
    if value is None:
        raise UnityResultValidationError(
            f"test results XML is missing the '{name}' attribute"
        )

    try:
        count = int(value)
    except ValueError as exc:
        raise UnityResultValidationError(
            f"test results XML has a non-integer '{name}' value: {value!r}"
        ) from exc

    if count < 0:
        raise UnityResultValidationError(
            f"test results XML has a negative '{name}' value: {count}"
        )
    return count


def validate_test_results(path: Path) -> TestResultSummary:
    """Return the parsed summary or reject missing, empty, failed, or zero-test XML."""

    if not path.is_file():
        raise UnityResultValidationError(f"test results XML was not created: {path}")
    if path.stat().st_size == 0:
        raise UnityResultValidationError(f"test results XML is empty: {path}")

    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as exc:
        raise UnityResultValidationError(
            f"test results XML cannot be parsed: {path}: {exc}"
        ) from exc

    tag = root.tag.rsplit("}", 1)[-1]
    if tag != "test-run":
        raise UnityResultValidationError(
            f"unexpected test results root element {tag!r}: {path}"
        )

    summary = TestResultSummary(
        total=_required_count(root, "total"),
        passed=_required_count(root, "passed"),
        failed=_required_count(root, "failed"),
        inconclusive=_required_count(root, "inconclusive"),
        skipped=_required_count(root, "skipped"),
        result=root.attrib.get("result", ""),
    )

    if summary.total == 0:
        raise UnityResultValidationError(
            f"test results XML contains zero executed tests: {path}"
        )
    if summary.failed > 0:
        raise UnityResultValidationError(
            f"Unity test run failed: {summary.failed} of {summary.total} tests failed"
        )
    if summary.passed == 0:
        raise UnityResultValidationError(
            f"test results XML contains zero passed tests: {path}"
        )
    normalized_result = summary.result.casefold()
    if normalized_result != "passed" and not normalized_result.startswith("skipped"):
        raise UnityResultValidationError(
            "Unity test run did not report a passing or skipped-only summary: "
            f"{summary.result or '<missing>'}"
        )

    return summary


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results_xml", type=Path)
    args = parser.parse_args(argv)

    try:
        summary = validate_test_results(args.results_xml)
    except UnityResultValidationError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    print(
        "Unity test results: "
        f"{summary.passed} passed, {summary.failed} failed, "
        f"{summary.inconclusive} inconclusive, {summary.skipped} skipped "
        f"(total: {summary.total})"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
