# `manage_tools`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_tools`

## Description

Manage which tool groups are visible in this session. Actions: find (search the complete tool catalog by capability), list_groups (show all groups and their status), activate (enable a group), deactivate (disable a group), sync (refresh visibility from Unity Editor's toggle states), reset (restore defaults). Activating a group makes its tools appear; deactivating hides them. Use find when you know the task but not the tool name, and sync after toggling tools in the Unity Editor GUI.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['find', 'list_groups', 'activate', 'deactivate', 'sync', 'reset']` | yes | Action to perform. |
| `group` | `str \| None` | — | Group name (required for activate / deactivate). Valid groups: animation, asset_gen, core, docs, probuilder, profiling, scripting_ext, testing, ui, vfx |
| `query` | `str \| None` | — | Capability search text for action='find', e.g. 'screenshot camera' or 'profiler memory'. |
| `limit` | `int` | — | Maximum matches for action='find' (1-20, default 8). |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->
