# `manage_input`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_input`

## Description

Create, inspect, validate, and modify Unity Input System .inputactions assets. Manage action maps, actions, bindings, and control schemes; configure generated C# wrappers; and assign an action asset to a PlayerInput component. The tool has no compile-time dependency on com.unity.inputsystem and reports a clear unavailable response when the optional package is not installed.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['ping', 'list_assets', 'get', 'create', 'delete', 'add_action_map', 'remove_action_map', 'add_action', 'remove_action', 'add_binding', 'remove_binding', 'add_control_scheme', 'remove_control_scheme', 'generate_csharp', 'assign_player_input', 'validate']` | yes | Input System action to perform. |
| `path` | `str \| None` | — | Assets-relative .inputactions asset path. |
| `name` | `str \| None` | — | Asset name for create when it cannot be derived from path. |
| `map_name` | `str \| None` | — | Input Action Map name. |
| `action_name` | `str \| None` | — | Input Action name. |
| `action_type` | `Literal['Button', 'Value', 'PassThrough'] \| None` | — | Input Action type. Defaults to Button. |
| `expected_control_type` | `str \| None` | — | Expected control layout, such as Button, Axis, Vector2, or Vector3. |
| `binding_path` | `str \| None` | — | Input control path, such as <Keyboard>/space. |
| `interactions` | `str \| None` | — | Binding/action interactions string. |
| `processors` | `str \| None` | — | Binding/action processors string. |
| `groups` | `str \| None` | — | Semicolon-separated binding groups/control schemes. |
| `binding_name` | `str \| None` | — | Optional binding or composite-part name. |
| `binding_index` | `int \| None` | — | Zero-based binding index within the selected action. |
| `is_composite` | `bool \| None` | — | Whether a new binding is a composite. |
| `is_part_of_composite` | `bool \| None` | — | Whether a new binding is a composite part. |
| `scheme_name` | `str \| None` | — | Control scheme name. |
| `binding_group` | `str \| None` | — | Control scheme binding group. Defaults to scheme_name. |
| `devices` | `list[dict[str, Any]] \| None` | — | Control scheme devices as {device_path, optional?, or?} objects. |
| `output_path` | `str \| None` | — | Generated C# wrapper path, relative to Assets or Assets-relative. |
| `class_name` | `str \| None` | — | Generated C# wrapper class name. |
| `namespace` | `str \| None` | — | Generated C# wrapper namespace. |
| `target` | `str \| None` | — | GameObject name, hierarchy path, or instance ID for assign_player_input. |
| `default_map` | `str \| None` | — | PlayerInput default action map. |
| `default_scheme` | `str \| None` | — | PlayerInput default control scheme. |
| `notification_behavior` | `Literal['SendMessages', 'BroadcastMessages', 'InvokeUnityEvents', 'InvokeCSharpEvents'] \| None` | — | PlayerInput notification behavior. Defaults to SendMessages. |
| `include_json` | `bool \| None` | — | Include the complete .inputactions JSON in get responses. |
| `page_size` | `int \| None` | — | Asset list page size. |
| `cursor` | `int \| None` | — | Zero-based asset list cursor. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->
