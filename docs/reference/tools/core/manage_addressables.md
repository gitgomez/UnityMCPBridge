# `manage_addressables`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_addressables`

## Description

Initialize, inspect, validate, author, and build Unity Addressables content. Manage groups, entries, addresses, labels, and profile values without a hard compile-time dependency on com.unity.addressables. Use ping before authoring in projects where the optional package may not be installed. The build action queues a job; poll build_status with its returned job_id until it completes.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['ping', 'initialize', 'list_groups', 'create_group', 'set_default_group', 'remove_group', 'list_entries', 'add_entry', 'remove_entry', 'set_address', 'list_labels', 'add_label', 'remove_label', 'set_label', 'get_profiles', 'set_profile_value', 'validate', 'build', 'build_status']` | yes | Addressables action to perform. |
| `group_name` | `str \| None` | — | Addressables group name. |
| `set_default` | `bool \| None` | — | Set a newly created group as the default group. |
| `force` | `bool \| None` | — | Allow destructive removal of non-empty groups or labels in use. |
| `asset_path` | `str \| None` | — | Assets-relative asset or folder path for an entry. |
| `guid` | `str \| None` | — | Asset GUID used to identify an entry. |
| `address` | `str \| None` | — | Runtime address. For set_address this is the new value. |
| `labels` | `list[str] \| None` | — | Labels applied when adding an entry. |
| `label` | `str \| None` | — | Single Addressables label name. |
| `enabled` | `bool \| None` | — | Whether set_label enables or disables the label. |
| `profile_name` | `str \| None` | — | Profile name. Omit to use the active profile. |
| `variable_name` | `str \| None` | — | Addressables profile variable name. |
| `value` | `str \| None` | — | New Addressables profile variable value. |
| `create_variable` | `bool \| None` | — | Create the profile variable if it does not exist. |
| `job_id` | `str \| None` | — | Build job identifier returned by the build action. |
| `search` | `str \| None` | — | Case-insensitive entry path, address, GUID, or label filter. |
| `page_size` | `int \| None` | — | Result page size. |
| `cursor` | `int \| None` | — | Zero-based pagination cursor. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->
