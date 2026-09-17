# `inspect_dependencies`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.inspect_dependencies`

## Description

Inspect Unity asset dependencies without modifying the project. Use dependencies for forward references, dependents for reverse references, impact before moving or deleting an asset, missing_references for broken serialized references and missing scripts, and cycles for circular asset dependency chains. Results are paginated and package assets are excluded by default.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['ping', 'dependencies', 'dependents', 'impact', 'missing_references', 'cycles']` | yes | Dependency inspection action. |
| `target` | `str \| None` | — | Asset path or GUID. Required for dependencies, dependents, impact, and missing_references; optional for cycles. |
| `recursive` | `bool \| None` | — | Include transitive dependencies/dependents. Defaults to true for dependencies and false for dependents. |
| `include_packages` | `bool \| None` | — | Include assets under Packages/. Defaults to false. |
| `search_root` | `str \| None` | — | Limit reverse-dependency or cycle scanning to this project-relative folder. Defaults to Assets. |
| `asset_type` | `str \| None` | — | Optional Unity asset type filter for returned assets, such as Prefab, Material, or SceneAsset. |
| `page_size` | `int \| None` | — | Maximum results per page. Defaults to 50 and is capped at 500. |
| `cursor` | `int \| None` | — | Zero-based pagination cursor. |
| `scan_limit` | `int \| None` | — | Maximum candidate assets scanned for dependents or cycles. Defaults to 20000 and is capped at 100000. |
| `max_results` | `int \| None` | — | Maximum dependency lists or cycles returned by impact/cycles. Defaults to 100. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->
