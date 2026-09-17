# `audit_project`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.audit_project`

## Description

Run read-only Unity project health checks and return a structured, paginated report. Checks cover compilation state, build scenes, missing serialized references/scripts, duplicate GUIDs, orphaned meta files, package resolution, and loaded-scene structure. Use list_checks to discover check names and run with an explicit subset for fast audits.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['ping', 'list_checks', 'run']` | yes | Audit action: ping, list_checks, or run. |
| `checks` | `list[str] \| None` | — | Optional check names. Omit to run all checks returned by list_checks. |
| `search_root` | `str \| None` | — | Project-relative Assets folder scanned by asset/meta checks. Defaults to Assets. |
| `include_scenes` | `bool \| None` | — | Open and inspect scene assets during missing-reference scanning. Defaults to false; loaded scenes are checked separately. |
| `scan_limit` | `int \| None` | — | Maximum assets or meta files scanned per applicable check. Defaults to 5000, capped at 50000. |
| `minimum_severity` | `Literal['info', 'warning', 'error'] \| None` | — | Minimum issue severity returned. Defaults to warning. |
| `page_size` | `int \| None` | — | Maximum issues per page. Defaults to 50, capped at 500. |
| `cursor` | `int \| None` | — | Zero-based issue pagination cursor. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->
