# Editor diagnostics, compile evidence and shared-server safety

Available from `v10.4.0` with a matched Unity package and Python server.
Discover negotiated capabilities rather than inferring support from a version
string. The earlier `v10.3.0` tag does not include these additions.

## Independent read-only observations

Read `mcpforunity://editor/diagnostics`, or use the local HTTP CLI:

```bash
unity-mcp --instance MyProject@hash --format json editor diagnostics
```

The resource requires `control_path_v1`, `editor_diagnostics_v1` and
`compile_diagnostics_v1`. Legacy/stdio sessions explicitly report that this
capability is unavailable. The CLI uses `/api/editor-diagnostics`, not the
normal `/api/command` route. Both reach the WebSocket control path without
creating receipts, refreshing assets, compiling scripts or entering the Unity
command queue. A full queue or a ledger with exactly 512 terminal receipts does
not prevent these reads. A disconnected transport can still prevent them.

`editor_diagnostics@1` reports separate evidence:

| Section | Meaning and limits |
| --- | --- |
| `transport.control_reply_received` | The control path answered; this is not main-thread or tool readiness. |
| `main_thread` | Only actual `EditorApplication.update` callbacks advance the heartbeat. Age uses a monotonic clock; reads never freshen it. `unknown` means no tick was observed; `not_ticking` means the last tick is over five seconds old. Neither identifies the cause. |
| `editor_state` | A main-thread-published snapshot, sampled at most every 500 ms, with its own timestamp and staleness. A fresh response envelope does not make an old snapshot fresh. |
| `queue` | Queued/executing counts, admitted payload bytes and pump state. These do not prove a handler has completed. |
| `native_modal` | A bounded Windows native-window observation, restricted to the current Editor process. It identifies candidates, not their business meaning. |
| `compilation` | Event-captured compile cycles and refresh-request correlations, independent of Console clearing. |

Server enrichment requires a fresh usable Editor snapshot and a ticking main
thread before reporting `ready_for_tools=true`. An observed native modal
candidate also blocks readiness. Readiness, compilation success and the outcome
of a particular command remain distinct facts.

## Native modal diagnosis

Interactive Windows Editors sample visible Win32 dialog-class windows and
windows whose owner is disabled. The sampler uses no Unity APIs, scans for up
to 200 ms, retains at most four candidate windows and 16 visible child-control
texts per window, and bounds individual text reads to 20 ms / 256 characters.
Its result is sampled every 500 ms and becomes `stale` after two seconds.
Control responses are bounded by the negotiated 64 KiB message limit; truncation
is explicit. Oversized cached save receipts are omitted; if necessary the
cached Editor snapshot or request correlations are omitted too, retaining
unknown readiness/correlation rather than overflowing the control path.

`no_supported_modal_observed` means only that no candidate was found within
this probe's scope. It is not proof that no modal UI exists. `unsupported`,
`unknown`, `incomplete`, `stale` and `probe_failed` are not an all-clear. macOS,
Linux and batchmode do not have this native-window probe. Hidden, custom-drawn,
external-process and other unsupported dialogs can remain unobserved.

The Bridge never clicks, answers, dismisses or changes a dialog. Title/control
text can contain project information; diagnostic output is returned on request,
not sent to telemetry. Do not publish it without reviewing its contents.

## Compile-cycle evidence

Unity owns compile capture through `CompilationPipeline` start, per-assembly
finish and cycle finish events. Each cycle has an Editor epoch, counter and
unique ID; it carries total error/warning counts, bounded messages, capture
completeness and an explicit result. The last four cycles and eight refresh
request records survive domain reload in that Editor's `SessionState`, not
across a new Editor process. At most 16 messages are retained per cycle;
truncated message text does not erase the total counts. File names are bounded
and external absolute compiler paths are reduced to their basename.

The absence of Console errors is not compile success. A started cycle without
its matching finish event is incomplete (`interrupted_by_reload`, `superseded`
or `context_mismatch` as applicable), never silently successful. No captured
cycle means unknown, not zero-error success. Invalid retained state is discarded
as unknown rather than accepted as compile evidence.

`refresh_unity` captures a request baseline when Unity actually executes the
command, before refresh/compile calls. `request_returned=true` means the compile
request call returned; it does not mean compilation succeeded. An observed
subsequent cycle is correlated as `observed_not_exclusive`: Unity can coalesce
requests, so exclusive ownership is not claimed. An already-running cycle or
multiple candidates is reported as ambiguous, not promoted to confirmed success.

With negotiated capabilities and `wait_for_ready=true`, refresh uses one
overall 60-second budget including prechecks, command transport, reload recovery
and observation. It sends one logical mutation. After a disconnect it selects
the same project hash, reads diagnostics and the original receipt, and never
issues another refresh just to recover observation. A successful wait requires
the original request's success, current readiness, and, when compile was
requested, a matching complete error-free observed cycle. Compiler errors yield
`compilation_failed`; missing readiness/correlation yields
`editor_readiness_timeout`. `outcome_unknown` remains unknown even if Unity is
now ready and another compile succeeded.

External dirty state is acknowledged only after proven full-refresh readiness
and an unchanged complete scanner checkpoint. Newer external edits, incomplete
scans, partial refreshes and uncertain outcomes remain dirty. Legacy refresh
continues without the new compile-evidence guarantee and still requires
explicit readiness; connection loss is not converted to success.

## Managed local server ownership

Stopping a server requires both launch ownership and a current shared-use check:

1. Project-scoped `SessionState` holds the launch owner's private handshake.
   Reload retains it; restarting an Editor does not inherit it. Project-scoped
   `EditorPrefs` PID/port data is discovery information only. Old global keys,
   a matching port, process name or command line never confer stop authority.
2. The Editor authenticates the exact server PID over literal loopback at
   `/api/server-control`. This route requires the private launch token,
   loopback binding and an actual loopback request peer. Remote-hosted or
   unauthenticated servers cannot grant this authority.
3. The server checks connected foreign Editors, reload markers, recent foreign
   disconnects and in-flight commands while atomically closing new command and
   plugin-registration admission. A prepare grant lasts five seconds; failed
   termination cancels it where possible, and expiry reopens admission.
4. Only the authenticated PID can be terminated within that budget. There is no
   process-tree, shell-pattern, port-owner or legacy heuristic fallback.

Preflight allows a restart request to check its own current RPC, but actual
shutdown preparation must wait until all commands are out of flight. A peer
that connected after preflight blocks preparation or is refused registration
once preparation has closed admission. A normal foreign disconnect is guarded
for 60 seconds. An announced reload remains guarded until that project
reconnects; a dead reload marker is not automatically declared safe.

Start/reuse, explicit stop, planned restart and Editor-quit cleanup share this
boundary. `UNITY_MCP_ALLOW_BATCH=1` enables batch MCP, not inherited ownership.
Domain reload never stops the server. If the owner quits while another Editor
uses the server, it is intentionally retained. Ownership is not transferred;
the retained server may need an operator-managed shutdown later.

The **Stop Server** button requests guarded shutdown before ending its session.
A refusal leaves the session unchanged and displays an explanation in the
connection window. **Connected** confirms a session, not a new server process;
the button remains **Stop Server** while the listener is still reachable.

All sharing Editors must use the guarded package. This cannot prevent an older
package's legacy OS-level termination path or an external process from killing
the server directly; a server-only update does not fix those clients.

## Safe recovery

1. Stop sending mutations. Record the target `Name@hash` and original request
   ID, then read independent diagnostics and the original receipt where
   available. Diagnose ledger saturation with `unity-mcp receipts status`, not
   another normal command.
2. If the main thread is not ticking, inspect the Editor and reported native
   candidates. Let the operator handle a real dialog; do not infer a dialog
   from heartbeat age alone. If the transport itself is unavailable, fix that
   connection before attempting MCP work.
3. For compiler errors, correct the reported source and deliberately request
   one new compile. A readiness timeout is not permission to repeat an uncertain
   mutation. Reconcile its effects and receipt first.
4. If a stop/restart is refused, leave the server running. Check all connected
   Editors, reloads and work owners. A healthy listener is not evidence that
   it is unshared or that this Editor owns it.
5. For an orphaned owner, abandoned reload marker or older server without the
   managed-control capability, coordinate all users first. An operator may
   then stop the explicitly identified server through its original launch
   session/process and start a new managed server. Do not copy tokens, erase
   lifecycle evidence or kill a guessed process to bypass the guard.
   On Windows, compare the listener PID with the project's
   `Library/MCPForUnity/RunState/mcp_http_<port>.pid`, then verify that process's
   executable, launch command and creation time. Recheck immediately before
   stopping exactly that process; never terminate every Python/uv process or a
   process tree. Confirm the port is free, then use **Start Server** in the
   intended Editor. A damaged temporary uv environment needs a fresh launch;
   inventing missing environment files or reconnecting to the old listener
   does not repair it. Verify the new PID/start time, matched package/server
   versions and an actual MCP resource read after launch.
6. After recovery, select the intended instance and require actual
   `ready_for_tools=true`. Check compile evidence separately and preserve
   `outcome_unknown` until its effects have been reconciled. For ledger cleanup,
   follow [safe receipt recovery](../getting-started/bridge-fork.md#safe-receipt-ledger-recovery).

## Implementation owners

- Unity: `EditorDiagnostics`, `CompileDiagnostics`, the WebSocket control
  dispatcher, `PidFileManager`, `ServerManagementService` and quit cleanup.
- Server: the diagnostics resource/CLI route, refresh observation coordinator,
  `PluginRegistry` shutdown admission gate and authenticated managed-control
  route.
- Existing Python and Unity test suites cover bounded observations, compile
  uncertainty, checkpoint preservation, ownership isolation, reload guards,
  admission races and the full receipt ledger. Live verification must state
  separately whether it used real Editors, a protocol peer or mocked events.

Live checks on Windows / Unity `6000.3.9f1` exercised an unfocused Editor,
an intentionally stalled main thread, a real `EditorUtility.DisplayDialog`,
compile/reload evidence and a launch-owner restart. The shared-use refusal
check used one real Editor plus a registered WebSocket protocol peer; a
two-real-Editor shutdown sequence and other native-dialog platforms were not
claimed as verified.
