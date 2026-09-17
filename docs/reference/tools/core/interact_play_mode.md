# `interact_play_mode`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.interact_play_mode`

## Description

Inspect, wait for, and interact with runtime uGUI or UI Toolkit in Play Mode without operating the OS mouse. uGUI remains the default; select ui_system='ui_toolkit' and provide a UIDocument plus a bounded element query for UI Toolkit. ping reports support and Play Mode state. click_ui dispatches a left pointer click. inspect_ui reports bounded runtime state for one active or inactive UI target. wait_ui performs bounded frame-driven inspection in Unity as one logical runtime command without blocking the main thread. set_text updates a TMP or legacy uGUI input field, optionally dispatching submit; its text is never echoed in the mutation response or Unity command log. set_toggle idempotently sets a Toggle. drag_ui dispatches a bounded synchronous pointer sequence. scroll_ui dispatches a bounded two-axis wheel delta to the associated scroll container. UI Toolkit hover_ui sends one pointer move without a press or release; inspect_ui/wait_ui can observe the actual hovered state. UI Toolkit key_ui sends one KeyDown/KeyUp pair to the current focus or document root, without changing focus, generating navigation events, typing text, or changing device state. Tab/Return do not imply navigation or submit. uGUI pointer actions accept a GameObject target or normalized Game View coordinates. UI Toolkit pointer actions accept an element query or normalized screen-space panel coordinates. Coordinates use a top-left origin. Mutating actions can cause gameplay or external side effects.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['ping', 'click_ui', 'inspect_ui', 'wait_ui', 'set_text', 'set_toggle', 'drag_ui', 'scroll_ui', 'hover_ui', 'key_ui']` | yes | Play Mode UI action. |
| `ui_system` | `Literal['ugui', 'ui_toolkit']` | — | Runtime UI system. Defaults to uGUI for backward compatibility. |
| `target` | `str \| None` | — | uGUI GameObject name, hierarchy path, or instance ID. |
| `position` | `list[float] \| None` | — | Normalized top-left-origin [x,y] position for pointer actions, including UI Toolkit hover_ui. |
| `search_method` | `Literal['by_id', 'by_name', 'by_path'] \| None` | — | Optional uGUI target lookup mode; inferred from target when omitted. |
| `document` | `str \| None` | — | UI Toolkit UIDocument GameObject name, hierarchy path, or instance ID. |
| `document_search_method` | `Literal['by_id', 'by_name', 'by_path'] \| None` | — | Optional UI Toolkit UIDocument lookup mode; inferred when omitted. |
| `element_name` | `str \| None` | — | Exact UI Toolkit VisualElement name. |
| `element_class` | `str \| None` | — | Required UI Toolkit USS class on the target element. |
| `element_type` | `str \| None` | — | Exact UI Toolkit element type name, simple or fully qualified. |
| `element_index` | `int \| None` | — | Zero-based match index for an otherwise ambiguous UI Toolkit query (0-1023). |
| `include_text` | `bool` | — | Include non-password UI text in inspect_ui and wait_ui state. |
| `condition` | `Literal['exists', 'active', 'visible', 'interactable', 'selected', 'text_equals', 'text_contains', 'toggle_equals', 'hovered'] \| None` | — | Condition for wait_ui. |
| `expected` | `bool \| str \| None` | — | Expected bool or string for wait_ui; boolean conditions default to true. |
| `timeout_seconds` | `float` | — | Bounded wait_ui timeout in seconds (0.1-30). |
| `poll_interval_seconds` | `float` | — | wait_ui polling interval in seconds (0.05-1). |
| `text` | `str \| None` | — | New input-field value for set_text. Never returned or written to Unity's command log. |
| `submit` | `bool` | — | Dispatch the uGUI submit event after set_text. |
| `sensitive` | `bool` | — | Treat set_text as sensitive even if the input field is not password-typed. |
| `value` | `bool \| None` | — | Desired Toggle state for set_toggle. |
| `end_position` | `list[float] \| None` | — | Required normalized top-left-origin [x,y] destination for drag_ui. |
| `steps` | `int` | — | Number of synchronous drag_ui movement steps (1-64). |
| `scroll_delta` | `list[float] \| None` | — | Required [x,y] Unity scroll units for scroll_ui; positive y scrolls up. |
| `key_code` | `Literal['Escape', 'Tab', 'Return', 'Space', 'LeftArrow', 'RightArrow', 'UpArrow', 'DownArrow', 'Backspace', 'Delete', 'Home', 'End', 'PageUp', 'PageDown'] \| None` | — | Named UI key for key_ui. Sends KeyDown/KeyUp only; Tab/Return do not generate navigation/submit. |
| `modifiers` | `list[Literal['Shift', 'Control', 'Alt', 'Command']] \| None` | — | Distinct UI key modifiers for key_ui (at most four). Does not press physical modifier keys. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->
