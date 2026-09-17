---
name: unity-mcp-orchestrator
description: Operate, diagnose, and extend Unity Editor through MCP for Unity. Use for Unity scene, asset, script, package, test, screenshot, Play Mode, uGUI, UI Toolkit, transport, instance-routing, tool-discovery, or Bridge capability work. Require live capability discovery, contract-aware mutations, and runtime verification.
---

# Unity MCP operator

Treat this directory as the canonical skill source for this repository. Do not
copy capability statements into another skill. Route repository-local agent
wrappers and installed client skills back to this directory.

## Establish the source of truth

Use this order when sources disagree:

1. Inspect the connected MCP server and selected Unity instance. Availability
   depends on the running server, enabled tool groups, installed Unity packages,
   current Editor state, and transport.
2. In a Bridge checkout, read `Contracts/tool-contracts.v1.json` for the
   normative built-in tool contract.
3. Read [capabilities-and-limitations.md](references/capabilities-and-limitations.md)
   before Play Mode automation, UI work, transport diagnosis, or any task that
   depends on a known boundary.
4. Read [tool-contract-index.md](references/tool-contract-index.md) for the
   generated tool inventory, mutation class, retry semantics, and declared
   action enums.
5. Use the hand-authored references for procedures and examples. Treat examples
   as templates until the active tool schema and runtime behavior confirm them.

Static documentation does not prove that a tool is exposed by the current MCP
session. Do not infer availability merely because a tool appears in a reference.

When working in this repository, run `python tools/skill_contracts.py --check`
after changing the Bridge contract or this skill. Use
`python tools/skill_contracts.py --check-installed` to detect an installed Codex
copy that has drifted from this source.

## Start every Unity task

1. Read `mcpforunity://instances`.
2. Select the intended `Name@hash` instance when more than one Editor is
   available. HTTP ports do not select an instance.
3. Read `mcpforunity://editor/state` and wait until `ready_for_tools` is true.
4. Read the smallest relevant project, scene, GameObject, component, or package
   resource before mutating state.
5. Use `manage_tools(action="find", ...)` or the live tool list when the needed
   capability is uncertain or its tool group may be disabled.
6. Execute the smallest bounded action.
7. Verify through the real Unity surface: state/resource reread, Console,
   screenshot, test result, or visible Play Mode behavior as appropriate.

Do not treat a successful transport response as proof that the requested Unity
state or gameplay effect occurred.

## Choose safe operations

- Read the generated contract index before unfamiliar or destructive tools.
- Respect `retry_semantics`. For `unsafe_without_receipt`, inspect resulting
  state before deciding whether a lost or timed-out mutation may be retried.
- Use `batch_execute` for independent discovery or bulk work. Do not assume it
  is transactional; earlier commands remain applied when a later command fails.
- Follow pagination until `next_cursor` is absent.
- Resolve ambiguous objects before mutation. Prefer stable instance IDs,
  handles, or explicit hierarchy paths over names.
- Never use reflection or direct handler invocation as completion evidence for
  a user-facing runtime path. Those are diagnostic tools only.

## Edit scripts and assets

After `create_script`, `script_apply_edits`, or another compilation-triggering
change:

1. Wait for `mcpforunity://editor/state` to report that compilation and domain
   reload are complete.
2. Read Console errors with stack traces.
3. Re-resolve scene objects and components; IDs and references can become stale
   across reloads.
4. Verify the resulting behavior in the real scene or Play Mode.

Do not call `refresh_unity` after every edit. Script tools already request the
required import and compilation; refresh only when the active workflow requires
it.

Do not externally edit a loaded Unity scene or an open Prefab Stage. A native
reload dialog can block Unity's main thread and therefore the MCP dispatcher.

## Automate Play Mode and UI

Read the Play Mode section of
[capabilities-and-limitations.md](references/capabilities-and-limitations.md)
and the `interact_play_mode` section in
[tools-reference.md](references/tools-reference.md#interact_play_mode) before
runtime interaction.

- Use `interact_play_mode(action="ping")` to inspect current uGUI and UI Toolkit
  support.
- Prefer `inspect_ui` and `wait_ui` before mutation, then verify state after the
  action.
- Select `ui_system="ui_toolkit"` explicitly for UI Toolkit; uGUI is the
  default.
- Treat clicks, submit, toggle, drag, scroll, hover, and key events as gameplay mutations that can
  trigger external side effects.
- For UI Toolkit, use `hover_ui` for a move without a click and `key_ui` for one
  named KeyDown/KeyUp pair through existing focus. Inspect actual `hovered` state
  and resulting UI behavior. Tab/Return do not automatically navigate or submit;
  no text, IME, or Input System device state is synthesized.
- Do not claim arbitrary keyboard, mouse, touch, native-dialog, or application
  input support. `interact_play_mode` dispatches bounded runtime UI events, not
  general operating-system or hardware input.
- Use screenshots for visual evidence, but combine them with structured state
  when correctness depends on identity, text, interactability, or selection.

## Diagnose failures

Separate these layers before editing code:

1. Client-to-MCP transport and session.
2. Selected Unity instance and heartbeat.
3. Editor readiness, compilation, reload, pause, or native modal state.
4. Tool-group visibility and optional package availability.
5. Target lookup, UI hierarchy, raycast, focus, or panel geometry.
6. Actual game or project behavior.

Use the stable error code and structured data when a tool provides them. Preserve
unexpected errors in Console and Bridge logs; do not normalize a partial or
failed result into success.

## Route to references

- **Capabilities, verified boundaries, and known defects:** read
  [capabilities-and-limitations.md](references/capabilities-and-limitations.md).
- **Complete generated built-in inventory:** read
  [tool-contract-index.md](references/tool-contract-index.md).
- **Detailed examples for commonly used tools:** search
  [tools-reference.md](references/tools-reference.md) for
  `^### <tool_name>$`. This file is large; do not load it wholesale unless the
  task spans many tool families.
- **Resources and URI payloads:** search
  [resources-reference.md](references/resources-reference.md) for the exact URI
  or resource family.
- **Long-form procedures:** search [workflows.md](references/workflows.md) for
  the task family, such as UI, scene building, testing, or Input System.
- **ProBuilder:** read [probuilder-guide.md](references/probuilder-guide.md) only
  when `com.unity.probuilder` is installed and the task needs editable geometry.

Keep project-specific architecture, authorization, test policy, and completion
criteria in that project's own agent guidance. This skill describes Bridge use;
it does not override repository-local rules.
