# Unity MCP Capabilities and Limitations

This document is the concise human guide to the capability boundaries of this
Bridge fork. It complements, but does not replace, the machine-readable contract
in `Contracts/tool-contracts.v1.json` or live MCP discovery.

## Table of contents

- [Authority and discovery](#authority-and-discovery)
- [Connection and instance routing](#connection-and-instance-routing)
- [Editor and project operations](#editor-and-project-operations)
- [Play Mode UI automation](#play-mode-ui-automation)
- [Screenshots and visual evidence](#screenshots-and-visual-evidence)
- [Console interpretation](#console-interpretation)
- [Native dialogs and loaded YAML](#native-dialogs-and-loaded-yaml)
- [Known current defects](#known-current-defects)
- [Verification standard](#verification-standard)

## Authority and discovery

Use four distinct sources for four distinct questions:

| Question | Source |
| --- | --- |
| What is connected and exposed now? | Live MCP tool list, `manage_tools`, `mcpforunity://instances`, and `mcpforunity://editor/state` |
| What does a built-in tool contractually accept? | `Contracts/tool-contracts.v1.json` |
| What is the concise built-in inventory? | Generated `tool-contract-index.md` |
| How should an agent operate the tools? | `SKILL.md` and the hand-authored references |

Tool presence in Markdown is not live availability. Tool groups may be disabled,
optional Unity packages may be absent, the wrong Editor instance may be selected,
or the connected server may be from another checkout or revision.

Use `manage_tools(action="find", ...)` when a capability is uncertain. It can
discover registered tools even when their group is currently hidden and can
report how to enable the relevant group.

## Connection and instance routing

- Treat the Unity instance identity (`Name@hash`) as the routing key.
- List `mcpforunity://instances` and call `set_active_instance` when multiple
  Editors are registered.
- Do not use an HTTP port as an instance selector. One MCP server can route to
  multiple Unity Editors.
- The `unity-mcp` CLI keeps the active stdout/stderr encoding. Status markers
  fall back to readable ASCII when that encoding cannot represent their Unicode
  form, and other unsupported output characters use backslash escapes instead
  of terminating the command. A separate `PYTHONUTF8` override is not required.
- Read `mcpforunity://editor/state` before complex work. Respect compilation,
  domain-reload, update, pause, and `ready_for_tools` state.
- Expect a short reconnect window after domain reload. Re-read state and targets
  rather than immediately repeating a mutation.

Connection, readiness, and gameplay success are separate facts. A listening
port proves only that something accepted the connection.

## Editor and project operations

The Bridge exposes bounded tools for scenes, GameObjects, components, assets,
scripts, packages, builds, graphics, physics, animation, VFX, profiling,
testing, screenshots, documentation lookup, and related Editor workflows. Read
the generated contract index for the exact built-in inventory of this checkout.

Important boundaries:

- Availability can depend on a Unity package, render pipeline, tool group, Play
  Mode, Edit Mode, or active scene state.
- `batch_execute` reduces transport overhead but is not transactional.
- Domain reload can invalidate transient Unity object identities. Stable handles
  help only where a tool explicitly supports them.
- Script and asset mutations can trigger import, compilation, domain reload, or
  gameplay side effects. Verify the final state after those transitions.
- Direct reflection and handler invocation can isolate a defect but do not prove
  the real input, UI, transport, or player path.

## Play Mode UI automation

`interact_play_mode` is the bounded runtime UI automation surface. Its declared
actions are:

| Action | Purpose |
| --- | --- |
| `ping` | Report Play Mode state plus uGUI and UI Toolkit support. |
| `inspect_ui` | Read bounded state for one runtime UI target. |
| `wait_ui` | Poll an inspection condition frame-by-frame inside Unity as one bounded runtime command. |
| `click_ui` | Dispatch one left pointer click. |
| `set_text` | Set a supported input field and optionally submit without echoing sensitive text. |
| `set_toggle` | Idempotently set a supported toggle. |
| `drag_ui` | Dispatch a bounded synchronous pointer drag. |
| `scroll_ui` | Dispatch a bounded two-axis wheel delta to the associated scroll container. |
| `hover_ui` | UI Toolkit only: dispatch one mouse pointer move with no press/release. |
| `key_ui` | UI Toolkit only: dispatch one named KeyDown/KeyUp pair through the current focus or document root. |

One `wait_ui` call owns exactly one Runtime-v1 request and one durable receipt.
Its internal frame polls do not create additional transport commands or receipt
entries.

### uGUI

- uGUI is the default `ui_system`.
- Resolve targets by name, hierarchy path, instance ID, or supported stable
  handle where the active schema permits it.
- Pointer actions accept a target or normalized Game View coordinates with a
  top-left origin.
- Target clicks validate layout, raycast result, visibility, occlusion, and an
  applicable event handler.
- `inspect_ui` can inspect active and inactive targets. Mutating actions require
  a usable runtime target and stable, unpaused Play Mode.

### UI Toolkit

- Select `ui_system="ui_toolkit"` explicitly.
- Resolve a runtime `UIDocument`, including supported `DontDestroyOnLoad`
  documents, and provide a bounded query by element name, USS class, type, and
  optional index.
- Element-based inspect, wait, text, toggle, click, drag, and scroll actions use
  the runtime panel and VisualElement event system.
- Element queries traverse the physical VisualElement hierarchy, including
  currently realized `ListView` and `TreeView` template elements.
- Pure normalized coordinates are supported only when a Screen Space panel has
  an unambiguous Game View-to-panel mapping. RenderTexture and World Space panel
  coordinates require an explicit mapping strategy and are not inferred.
- Items that have not been realized by a virtualized collection are not present
  in the Visual Tree and cannot be addressed by an element query. Item-index or
  stable-item-ID collection addressing is not currently implemented.

### UI Toolkit hover and keys

- `hover_ui` uses the same document plus element query or normalized position as
  other pointer actions. It sends a `PointerMoveEvent` through the panel dispatcher,
  which owns picking and enter/leave/hover transitions. Pressed mouse buttons or
  existing pointer capture return `pointer_busy`; no click is generated.
- `inspect_ui` exposes `hovered` and `hoverSupported`. This reads Unity's actual
  hover pseudo-state, not a remembered command position or a geometric prediction.
  An unavailable engine property produces `hovered=null`; `wait_ui` then reports
  that its condition is unavailable. `condition="hovered"` is UI Toolkit only.
- `key_ui` requires only a document address, `key_code`, and optional `modifiers`.
  It does not accept an element query, position, or explicit focus change. It uses
  the current panel focus within that document, or its root when nothing is focused.
  Focus in another UIDocument is rejected. Detachment or cross-document focus
  changes after KeyDown produce an explicit partial-dispatch error.
- Named keys are `Escape`, `Tab`, `Return`, `Space`, `LeftArrow`, `RightArrow`,
  `UpArrow`, `DownArrow`, `Backspace`, `Delete`, `Home`, `End`, `PageUp`, and
  `PageDown`. Modifiers are distinct `Shift`, `Control`, `Alt`, and `Command`
  values, with at most four entries. `ping` reports the supported names.
- Key actions send KeyDown and KeyUp synchronously with a zero character. They do
  not synthesize text or IME events, navigation events, or device state. In particular,
  Tab does not promise focus traversal and Return does not promise button submit.
  Product KeyDown/KeyUp callbacks can still act on these keys.
- Verify effects after normal player frames with `inspect_ui`/`wait_ui`, Console,
  and composited Game View capture. Dispatch success alone does not prove the
  visible hover or product keyboard behavior.

### Input boundary

Runtime UI interaction is not arbitrary input injection.

- Beyond the bounded UI Toolkit events above, the Bridge does not synthesize a general keyboard key, mouse button,
  touch contact, Input System device state, or operating-system event.
- A game gate that reads `Keyboard.current`, `Mouse.current`, or touchscreen
  state directly cannot be advanced merely by dispatching a UI click when no UI
  target handles that click.
- Native Unity dialogs are outside the runtime UI backends.

Treat all runtime UI mutations as `unsafe_without_receipt`: a click, submit,
toggle, drag, or scroll can trigger gameplay or an external side effect. After a
lost response or timeout, inspect the resulting state before considering a
retry.

## Screenshots and visual evidence

- Use composited Game View capture for player-visible runtime evidence.
- Use Scene View capture for Editor layout, gizmos, framing, and authoring
  evidence.
- The composited capture path temporarily enables background execution when
  needed and restores the previous state after success, failure, timeout,
  assembly reload, or Editor quit.
- Batchmode cannot produce the same composited Game View capture and must report
  that boundary explicitly.
- A screenshot proves appearance at one moment. Combine it with structured state
  for target identity, text, selection, interactability, persistence, or server
  effects.

## Console interpretation

Read Console errors and warnings after major changes, compilation, domain reload,
and runtime verification. Include stack traces when diagnosing failures.

`read_console` derives severity from the current Unity Editor's recognized
`LogEntry.mode` flags. It falls back to the message body only when Unity exposes
no recognized severity flag; attached stack-trace text never changes severity.

## Native dialogs and loaded YAML

A native Unity modal dialog can block the Editor main thread and therefore the
MCP dispatcher. An already-open native dialog cannot be reliably confirmed or
dismissed through the Bridge.

Do not externally modify a loaded `.unity` scene or an open `.prefab` stage.
Perform those changes through Unity/MCP, or close and save the affected context
before an unavoidable external edit.

## Known current defects

These are observed defects in this fork, not accepted behavior. Analyze and fix
the whole affected path rather than treating a partial result as success.

### Raw Play Mode input is absent

There is no bounded raw key or pointer-press primitive for gameplay code that
reads device state directly. This currently blocks autonomous continuation of a
non-UI intro gate.

## Verification standard

Match evidence to the claim:

| Claim | Required evidence |
| --- | --- |
| Tool is available | Live tool discovery plus correct instance selection |
| Editor mutation succeeded | Resource/state reread and relevant Console check |
| UI action succeeded | Real Play Mode interaction plus resulting UI/game state |
| Visual layout is correct | Game View or Scene View screenshot as appropriate |
| Persistence/server side effect succeeded | Authoritative server, database, file, or service evidence |
| Script change is usable | Compilation complete, Console clean for the change, and real behavior exercised |

Report any verification surface that was not exercised. Never replace missing
runtime proof with a direct private method call or a synthetic success response.
