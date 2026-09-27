# `input_play_mode`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.input_play_mode`

## Description

Bounded gameplay keyboard/mouse input through optional Unity Input System virtual devices. status is read-only. key holds a chord; move uses absolute Game View coordinates or relative pixels; click/drag/scroll use an explicit position. Stable unpaused Play Mode and Dynamic updates required; paired InputUser/PlayerInput, legacy input and locked cursor are unsupported. No OS input, rebinding or settings changes. One gesture per Editor, normal player frames, at most 10 seconds including cleanup. cancel requires the active operation_id from status and releases only Bridge-owned devices. Input may cause irreversible gameplay; inspect after timeout, never blindly replay.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['status', 'key', 'move', 'click', 'drag', 'scroll', 'cancel']` | yes | Bounded input action. |
| `keys` | `list[str] \| None` | — | 1-8 distinct Input System names: A-Z, Digit0-9, F1-12, Space/Enter/Tab/Escape, editing/navigation keys, Left/Right Shift/Ctrl/Alt/Meta. No text or IME. |
| `position` | `list[float] \| None` | — | Absolute Game View [x,y], normalized 0-1, top-left origin. |
| `end_position` | `list[float] \| None` | — | Drag endpoint, same normalized space. |
| `delta` | `list[float] \| None` | — | Relative mouse pixels, positive Y up, each axis -4096..4096; move only. |
| `scroll_delta` | `list[float] \| None` | — | Nonzero wheel delta in Input System units, each axis -100..100, positive Y up. |
| `button` | `Literal['left', 'right', 'middle'] \| None` | — | Click/drag button; default left. |
| `duration_seconds` | `float \| None` | — | Key/click/drag minimum duration 0.05-5 seconds, default 0.1. Frame scheduling can extend it. |
| `steps` | `int \| None` | — | Drag movement samples 1-64, default 8; each survives a normal player frame. |
| `operation_id` | `str \| None` | — | Exact active operation ID from status; cancel only. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->
