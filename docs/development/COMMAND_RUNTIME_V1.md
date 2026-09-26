# Command Runtime v1

Status: Implemented and verified on `optimize/bridge`
Development branch: `optimize/bridge`
Scope: Python MCP server, Unity Editor package, built-in and custom tool contracts
Compatibility requirement: no behavior change for legacy package/server pairs

## Summary

Command Runtime v1 is implemented in this fork. It adds a shared execution contract
across the Python server and the Unity Editor package, addressing the timeout,
retry-identity, queue-bound, and response-format gaps of the historical upstream
baseline described below. This document records the delivered runtime design;
the baseline observations are not current defects of the fork.

Command Runtime v1 introduces an optional, negotiated runtime layer with:

- protocol and capability negotiation;
- a versioned tool-contract manifest and deterministic schema hash;
- stable request IDs and bounded idempotency receipts;
- explicit command lifecycle states, including `outcome_unknown`;
- bounded data and command queues with a separate control path;
- mutation classes, permission profiles, and optimistic state preconditions;
- stable Unity object handles and editor-state revisions;
- one versioned response envelope; and
- stronger batch preflight, Undo grouping, and truthful rollback semantics.

The runtime is additive. A new server must continue to operate with an old Unity
package, and a new Unity package must continue to operate with an old server. Runtime
v1 behavior is enabled only when both ends advertise the required capability.

## Historical upstream baseline

The original design was based on `10.1.1-beta.1` (`upstream/beta` at `bd72241a`).
The following behaviors were verified directly in that historical source, before
the runtime implementation:

- `register` includes project and Unity identity, but no protocol version, package
  version, schema version, or schema hash.
- Python MCP tools, CLI commands, Unity handlers, and resources remain separate
  surfaces without a common machine-checked contract.
- custom-tool discovery reads attributed properties only, although
  `ToolParameterAttribute` also targets fields; complex types collapse to `array` or
  `object`.
- the Python server generates a new WebSocket command ID for each send; Unity has no
  logical request ledger shared across retries.
- `TransportCommandDispatcher.Pending` has no admission limit and dispatches every
  ready item in one editor update.
- the Unity WebSocket receiver accumulates a complete message in a `MemoryStream`
  without a message-size ceiling and awaits command execution inside the receive
  loop.
- GameObject serialization primarily exposes session-local instance IDs.
- tool responses mix transport `status`, tool `success`, `_mcp_status`, `error`, and
  `code` fields.

Two earlier concerns were already partially mitigated in that upstream snapshot:

- `batch_execute` limited batches to 25 commands by default and 100 at the hard
  ceiling; and
- HTTP session cleanup used ping-based eviction, replacement of sessions for the
  same project hash, and weak references for MCP client-session notifications.

Neither mitigation provides the command-level execution guarantees defined here.

## Goals

1. Prevent an automatic retry from executing the same logical mutation twice within
   a bounded retention window.
2. Distinguish "failed before execution" from "execution may have happened".
3. Keep ping, cancellation, and status queries responsive while Unity is processing
   normal commands.
4. Apply explicit memory, queue, payload, response, and per-frame limits.
5. Detect contract drift between Python, CLI, and C# before release.
6. Let agents reason about mutation risk, Undo support, compilation, and editor-mode
   requirements before calling a tool.
7. Detect stale scene/asset assumptions before applying a mutation.
8. Preserve current MCP-facing behavior until a deliberate public migration.

## Non-goals

- Exactly-once execution across arbitrary machine loss. The runtime provides bounded
  deduplication and explicit unknown outcomes, not a distributed transaction system.
- Preempting arbitrary synchronous Unity APIs. Cancellation of an executing
  `BuildPipeline.BuildPlayer`, menu item, or other blocking API may be impossible.
- Pretending Unity Undo is a general asset rollback system.
- Running Unity API calls concurrently on background threads.
- Replacing the existing FastMCP, CLI, or C# implementation style with generated code
  in the first phase.
- Changing any consumer project during development of this runtime.

## Design principles

- **The Unity process is the execution authority.** Only Unity can determine whether
  a command entered the main-thread execution phase.
- **Timeout is not cancellation.** A timeout describes the caller's wait, not the
  absence of side effects.
- **Unknown is a first-class outcome.** The runtime must never convert uncertainty
  into a safe-to-retry failure.
- **Control traffic cannot wait behind command traffic.** Ping, receipt lookup, and
  queued-command cancellation use an independent bounded path.
- **Capabilities, not package strings, enable behavior.** Package versions are useful
  diagnostics but are not a substitute for negotiation.
- **Limits fail closed and visibly.** The runtime returns a stable error code and
  retry guidance instead of silently dropping, truncating, or accepting unbounded
  work.
- **Mutation metadata is enforced twice.** The server provides early feedback; Unity
  remains the final authority.

## Logical architecture

```text
MCP client
    |
    | MCP tool call
    v
Python tool adapter
    |
    | validate contract + profile + state preconditions
    | allocate stable request_id
    v
Python command coordinator
    |                         control path
    | execute attempt       +--------------------+
    v                       | receipt / cancel   |
WebSocket transport --------+ ping / capabilities|
    |
    v
Unity admission controller
    |
    +--> receipt ledger: duplicate? conflict? known result?
    |
    +--> bounded command queue
             |
             v
        main-thread dispatcher
             |
             v
        C# tool handler
             |
             v
        response envelope + receipt update
```

## Terminology

| Term | Meaning |
|---|---|
| Logical request | One intended tool operation, independent of transport retries. |
| `request_id` | Stable identifier for a logical request. |
| `transport_id` | Identifier for one WebSocket send/response attempt. The current `id` field serves this role. |
| Receipt | Unity-owned record of a logical request and its execution state. |
| Payload hash | Hash of tool name, normalized parameters, target instance, and relevant preconditions. |
| Control message | Runtime message such as ping, query receipt, cancel queued request, or capability query. |
| Contract manifest | Versioned description of tool inputs, outputs, mutations, timeouts, and retry behavior. |
| State epoch | Unique identifier for one continuous editor/runtime state lineage. |
| State revision | Monotonic counter within an epoch. |

## Capability negotiation

### Unity registration extension

The Unity package adds an optional `runtime` object to the existing `register`
message. Existing servers ignore unknown fields.

```json
{
  "type": "register",
  "project_name": "ExampleProject",
  "project_hash": "abc123",
  "unity_version": "6000.4.3f1",
  "project_path": "D:/ExampleProject",
  "runtime": {
    "protocol": "command-runtime",
    "major": 1,
    "minor": 0,
    "package_version": "10.2.8",
    "contract_version": 1,
    "built_in_schema_hash": "sha256:...",
    "capabilities": [
      "capability_negotiation_v1",
      "contract_manifest_v1",
      "request_receipts_v1",
      "response_envelope_v1",
      "bounded_command_queue_v1",
      "control_path_v1",
      "mutation_contracts_v1",
      "stable_handles_v1",
      "state_revisions_v1",
      "batch_semantics_v1",
      "reload_lifecycle_v1"
    ],
    "limits": {
      "max_command_message_bytes": 8388608,
      "max_response_message_bytes": 16777216,
      "max_queued_commands": 64,
      "max_queued_payload_bytes": 16777216,
      "max_control_message_bytes": 65536,
      "max_receipts": 512,
      "receipt_retention_seconds": 1800,
      "max_persisted_receipt_bytes": 8388608,
      "max_cached_result_bytes": 262144
    }
  }
}
```

### Server welcome extension

The server adds the same optional `runtime` object to `welcome`. The current Unity
client already ignores unrecognized fields.

```json
{
  "type": "welcome",
  "serverTimeout": 30,
  "keepAliveInterval": 15,
  "runtime": {
    "protocol": "command-runtime",
    "major": 1,
    "minor": 0,
    "server_version": "10.1.1",
    "contract_version": 1,
    "built_in_schema_hash": "sha256:...",
    "capabilities": [
      "capability_negotiation_v1",
      "contract_manifest_v1",
      "bounded_command_queue_v1",
      "control_path_v1",
      "request_receipts_v1",
      "response_envelope_v1",
      "mutation_contracts_v1",
      "stable_handles_v1",
      "state_revisions_v1",
      "batch_semantics_v1",
      "reload_lifecycle_v1"
    ]
  }
}
```

### Negotiation result

Each connection is classified as one of:

| Mode | Condition | Behavior |
|---|---|---|
| `legacy` | Either side omits `runtime`. | Preserve current messages and responses. |
| `runtime_v1` | Major versions match and required capabilities intersect. | Enable only negotiated features. |
| `degraded` | Major versions match but schema hashes or optional capabilities differ. | Continue with the safe capability intersection and expose diagnostics. |
| `incompatible` | Both sides require different major versions and neither advertises a legacy path. | Refuse mutations; keep health diagnostics available. |

A schema-hash mismatch is not by itself a connection failure. It becomes a mutation
failure only when a tool contract required for that call is absent or incompatible.

During phased delivery, each endpoint advertises only capabilities whose behavior and
tests have landed. Phase 1a therefore starts with `capability_negotiation_v1`, and
Phase 1b adds `contract_manifest_v1`. Phase 2 adds
`bounded_command_queue_v1` and `control_path_v1`. Phase 3 adds
`request_receipts_v1`. Phase 4 adds `response_envelope_v1` and
`mutation_contracts_v1`. Phase 5 adds `stable_handles_v1`, `state_revisions_v1`,
and `batch_semantics_v1`. Post-phase reload hardening adds
`reload_lifecycle_v1`.

## Contract manifest

### Source of truth

Runtime v1 uses the checked-in manifest at
`Contracts/tool-contracts.v1.json`. It is normative for built-in tools. The existing
Python, CLI, and C# layers remain hand-written initially, but CI extracts their public
surfaces and validates them against the manifest.

This preserves the repository's current preference for ergonomic, independent layers
without accepting silent drift.

### Tool entry

```json
{
  "name": "manage_gameobject",
  "contract_version": 1,
  "group": "core",
  "input_schema": { "type": "object" },
  "output_schema": { "type": "object" },
  "mutation": {
    "class": "scene",
    "destructive": false,
    "triggers_compilation": false,
    "requires_edit_mode": false,
    "undoable": true
  },
  "execution": {
    "retry_semantics": "request_deduplicated",
    "default_timeout_ms": 30000,
    "max_timeout_ms": 3600000,
    "polling": false
  },
  "preconditions": {
    "supports_if_match": true
  }
}
```

### Required contract fields

- stable tool name and contract version;
- complete input JSON Schema, including enum values, array item types, nested objects,
  required fields, aliases, and defaults;
- output JSON Schema;
- tool group and availability requirements;
- mutation class and risk flags;
- retry and timeout semantics;
- polling semantics;
- Edit/Play mode and compilation constraints;
- Undo and precondition support; and
- deprecation/replacement information when applicable.

### Schema hashing

The built-in schema hash is SHA-256 over a deterministic UTF-8 serialization of the
manifest with:

- object keys sorted lexicographically;
- no insignificant whitespace;
- normalized JSON primitives;
- entries sorted by tool name; and
- non-semantic fields such as generation timestamps excluded.

CI computes the hash independently in Python and C#. A release fails if the values
differ.

### Custom tools

Custom tools cannot be listed in the built-in manifest. Runtime v1 therefore allows a
custom tool to supply a full JSON Schema explicitly. Attribute-based discovery remains
available but must:

- read both fields and properties;
- honor an explicitly supplied parameter name;
- describe enums, nullable types, array/list item types, and nested objects; and
- reject unsupported recursive or ambiguous shapes with a useful diagnostic.

The registration message includes a project-specific `tool_catalog_hash` after custom
tools are discovered.

## Command envelope

Runtime v1 extends the existing execute message without removing `id`:

```json
{
  "type": "execute",
  "id": "transport-attempt-id",
  "name": "manage_gameobject",
  "params": {},
  "timeout": 30,
  "runtime": {
    "version": 1,
    "request_id": "logical-request-id",
    "attempt": 1,
    "payload_hash": "sha256:...",
    "deadline_unix_ms": 1784370000000,
    "contract_version": 1,
    "tool_name": "manage_gameobject",
    "profile": "unrestricted",
    "if_match": {
      "epoch": "editor-epoch-id",
      "scene_revision": 42
    }
  }
}
```

Rules:

1. Python allocates `request_id` once per logical MCP tool call and reuses it for every
   internal transport retry.
2. `id` remains unique per transport attempt so old correlation behavior continues.
3. `payload_hash` covers the tool name, canonical parameters, target project hash,
   contract version, profile, and state preconditions.
4. Reusing a request ID with a different payload hash returns
   `REQUEST_ID_CONFLICT` and never executes.
5. The server never automatically changes `request_id` merely to escape an uncertain
   outcome.
6. A retry with `attempt > 1` and no retained receipt returns
   `RECEIPT_NOT_AVAILABLE`; it does not execute as a new command.

## Receipt lifecycle and idempotency

### States

```text
accepted -> queued -> executing -> succeeded
                              \-> failed
                              \-> cancelled
                              \-> outcome_unknown

accepted/queued -------------> cancelled
terminal state --------------> expired (tombstone or eviction)
```

`succeeded`, `failed`, and `cancelled-before-execution` are known outcomes.
`outcome_unknown` means side effects may have occurred and an automatic retry with a
new request ID is unsafe.

### Unity-owned receipt ledger

Unity owns the authoritative ledger because it controls admission to the main thread.
The ledger is bounded by count, age, and bytes. Proposed initial defaults are:

| Limit | Default | Hard ceiling |
|---|---:|---:|
| Receipt retention | 30 minutes | 24 hours |
| Receipt count | 512 | 4096 |
| Persisted receipt bytes | 8 MiB | 64 MiB |
| Cached result per receipt | 256 KiB | 1 MiB |

Only payload hashes, state, timing, stable error data, and bounded results are stored.
Raw command parameters are not persisted.

### Composite waits and receipt budget

One logical tool request owns at most one durable command receipt. Internal
polling that belongs to that request must not be expressed as repeated Unity
transport commands because every such command would allocate another retained
receipt.

`interact_play_mode(wait_ui)` therefore enters Unity once and performs its
bounded inspection loop from `EditorApplication.update`. The Python MCP tool and
CLI forward the wait parameters unchanged; they do not synthesize repeated
`inspect_ui` requests. This preserves the receipt ledger's retry guarantee while
keeping poll count independent from receipt count.

The implemented persistence location is project-local and unversioned:
`Library/MCPForUnity/RunState/command-receipts-v1.json`. Writes use an atomic temporary
file plus replace operation.

### Duplicate behavior

- Existing terminal receipt with cached result: return the same envelope.
- Existing terminal receipt whose result was evicted: return the terminal state,
  result digest, and `RESULT_EVICTED` diagnostic; do not execute again.
- Existing queued/executing receipt: return current state and polling guidance.
- Same ID with different payload hash: return `REQUEST_ID_CONFLICT`.
- Retry attempt with no receipt: return `RECEIPT_NOT_AVAILABLE` and
  `outcome_unknown` guidance.

### Reload and process-loss behavior

Peers that negotiate `reload_lifecycle_v1` use a control message before Unity tears
down its WebSocket for an intentional domain reload:

```json
{
  "type": "lifecycle",
  "state": "reloading",
  "session_id": "...",
  "reason": "assembly_reload"
}
```

The server retains this marker after the old session disconnects and waits on a
registry event for the same project hash to register again. A reload-aware connection
that disappears without the marker fails immediately as `unity_disconnected`; an
announced reload that exceeds its original bounded wait reports `reload_timeout`.
The wait budget starts at the lifecycle signal and is never restarted by later calls.
Legacy peers retain the previous bounded reconnect wait.

The local CLI REST route uses the same instance-aware Runtime-v1 dispatcher as MCP
tools. It must not snapshot a WebSocket session and send directly to that transient
session ID: during an announced reload, dependent CLI commands wait for the replacement
session, while an admitted command that loses its response is recovered with the same
logical request ID and receipt. This preserves the one-execution guarantee across the
HTTP-to-WebSocket boundary.

- Persisted `accepted` or `queued` receipts restored after reload become `cancelled`
  with code `RELOAD_BEFORE_EXECUTION`; Unity knows they never entered execution.
- Persisted `executing` receipts restored after reload become `outcome_unknown`.
- Terminal receipts remain terminal until expiration.
- A clean shutdown may mark non-executing queued work cancelled, but it cannot declare
  an executing synchronous operation safely cancelled.

### Cancellation

Cancellation is guaranteed only before `executing`. For an executing command, the
runtime records `cancellation_requested` and invokes a handler cancellation token when
supported. If the handler cannot prove cancellation before side effects, the final
state remains its actual result or `outcome_unknown`.

### Administrative ledger diagnosis and recovery

Peers advertise `receipt_ledger_admin_v1` when the local administrative control
path is available. It is deliberately separate from normal command admission, so a
full receipt ledger cannot block its own diagnosis or cleanup. The server exposes the
path only through its local CLI/REST surface, not as an MCP tool and not in remote
hosted mode.

From `Server/`, select the exact Unity instance when more than one Editor is connected:

```text
uv run unity-mcp --instance "Name@hash" --format json receipts status
uv run unity-mcp --instance "Name@hash" --format json receipts cleanup
```

`status` is read-only. It reports `total`, aggregate active/terminal counts, the
retention boundary, and a separate count for each state: `accepted`, `queued`,
`executing`, `succeeded`, `failed`, `cancelled`, and `outcome_unknown`.

The safe recovery order is:

1. Capture `receipts status` before changing the ledger.
2. Run `receipts cleanup` first. It removes only expired `succeeded`, `failed`, and
   `cancelled` receipts. `accepted`, `queued`, and `executing` are never cleanup
   candidates.
3. Re-run `receipts status`. If retained known terminal receipts still fill the
   ledger and their retry window may be discarded, explicitly select
   `receipts cleanup --all-terminal`.
4. Treat `outcome_unknown` separately. A command may already have produced side
   effects, so correlate the Unity/project result before using the additional
   `--confirm-outcome-unknown` flag. Removing that receipt does not make a retry safe;
   it removes the evidence used to detect the uncertainty.

To remove every terminal receipt, including reviewed `outcome_unknown` entries, both
selections are required:

```text
uv run unity-mcp --instance "Name@hash" --format json receipts cleanup --all-terminal --confirm-outcome-unknown
```

Cleanup persists the complete retained ledger snapshot atomically before changing the
in-memory dictionary. A persistence failure removes nothing and returns
`RECEIPT_CLEANUP_PERSIST_FAILED`; inspect the reported `ledger_path`, restore normal
filesystem access, and repeat `receipts status` before another cleanup attempt. Do not
manually edit or delete `command-receipts-v1.json` while Unity owns the ledger.

## Bounded queues and control path

### Separate paths

The WebSocket receive loop must parse and admit messages without awaiting normal
command completion. It routes messages to:

1. a small control path for ping/pong, capability queries, receipt status, and queued
   cancellation; or
2. the bounded command admission controller.

Control work may not invoke arbitrary Unity APIs. Any control operation that needs the
main thread receives its own small, bounded budget.

### Proposed initial limits

| Limit | Default | Behavior when exceeded |
|---|---:|---|
| Inbound command message | 8 MiB | Close/reject with `MESSAGE_TOO_LARGE`. |
| Outbound response message | 16 MiB | Return `RESPONSE_TOO_LARGE`; never emit invalid/truncated JSON. |
| Control message | 64 KiB | Reject malformed/oversized control traffic. |
| Queued commands | 64 | Return `QUEUE_FULL` plus `retry_after_ms`. |
| Total queued payload bytes | 16 MiB | Return `QUEUE_BYTES_EXCEEDED`. |
| Commands started per editor frame | 4 | Defer remaining commands. |
| Admission/dispatch frame budget | 4 ms | Defer when budget is consumed. |
| Diagnostic text in a response | 32 KiB | Redact and cap with an explicit truncation flag. |

All defaults are configurable within hard ceilings. Changing a limit is observable in
the negotiated `limits` object.

The per-frame budget controls queue/dispatch overhead. It cannot preempt a synchronous
handler that blocks the Unity main thread after it starts.

### Backpressure response

```json
{
  "runtime_version": 1,
  "request_id": "...",
  "status": "failed",
  "code": "QUEUE_FULL",
  "message": "Unity command queue is at capacity.",
  "data": {
    "retry_safe": true,
    "retry_after_ms": 250,
    "queue_depth": 64,
    "queue_limit": 64
  }
}
```

This response is safe to retry with the same request ID because no receipt entered
`executing`.

## Mutation model and permission profiles

### Mutation classes

Every contract declares exactly one primary class:

- `read_only`
- `editor_state`
- `scene`
- `asset`
- `project_settings`
- `package`
- `build`
- `external_side_effect`

Additional flags describe whether the call is destructive, triggers compilation,
requires Edit mode, enters Play mode, is Undoable, uses external services, or may write
outside `Assets/`.

### Profiles

| Profile | Allowed behavior |
|---|---|
| `read_only` | Read-only resources and tools only. |
| `standard` | Non-destructive scene/asset editing with declared safeguards. |
| `destructive` | Destructive scene/asset/project mutations after explicit enablement. |
| `unrestricted` | Package, build, external, and code-execution capabilities. |

The Python server rejects disallowed calls before transport. Unity repeats the check
against its locally configured policy so a compromised or outdated server cannot
bypass it.

The compatibility default is `unrestricted`: negotiating Runtime v1 does not silently
remove capabilities from an existing project. The server profile is configured with
`UNITY_MCP_MUTATION_PROFILE`; Unity's local ceiling uses the project-scoped EditorPrefs
key `MCPForUnity.CommandRuntime.MutationProfile.<project-hash>`. Both must allow a command. An invalid
profile returns `INVALID_MUTATION_PROFILE`, and an unknown/custom command under a
restrictive profile returns `TOOL_CONTRACT_NOT_FOUND`. Unknown/custom commands remain
available when both sides are `unrestricted`.

The generated policy is intentionally conservative at tool granularity. `standard`
allows non-destructive `read_only`, `editor_state`, `scene`, and `asset` contracts.
`destructive` additionally permits destructive operations and `project_settings`, but
still excludes `package`, `build`, and `external_side_effect`. The shared
`manage_script` Unity handler is resolved by action so `read`, `validate`, and
`get_sha` retain their read-only contracts.

## Stable handles and state revisions

### Scene object handle

```json
{
  "kind": "scene_object",
  "global_object_id": "GlobalObjectId_V1-...",
  "scene_guid": "...",
  "hierarchy_path": "/Environment/Props/Crate",
  "instance_id": 12345,
  "epoch": "...",
  "revision": 42
}
```

Resolution order is `GlobalObjectId`, scene GUID plus exact hierarchy path, then
instance ID only when the epoch matches. Unsaved/transient objects receive
`session_local: true`; their handle resolves through the instance ID only within the
same editor epoch. `find_gameobjects` returns these handles beside its legacy
`instanceIDs`, and central GameObject serialization adds a `handle` field without
removing existing fields. `manage_gameobject` and `manage_components` accept either
their existing string/ID targets or a handle object. Component object-reference
properties accept the same handle shape, including arrays and `{ "handle": ... }`
wrappers.

The GameObject, component-list, and single-component resources return the owning
scene-object handle alongside legacy IDs. Their existing `{instance_id}` URI segment
also accepts the handle's `global_object_id`, so callers can keep reading the same
object after a scene or domain reload without introducing a parallel resource family.

### Asset handle

Asset handles use asset GUID plus local file ID, with asset path as a diagnostic and
human-readable fallback. The shared resolver is implemented for progressive adoption
by asset tools; scene-object output and input are the initial public integration.

### Revisions

The implemented runtime maintains:

- an editor `epoch` in Unity `SessionState`, retained across domain reload and
  regenerated after a full Editor restart;
- a monotonic global state revision;
- a coarse scene revision driven by hierarchy, scene, Undo, and command events; and
- a coarse asset revision driven by project and command events.

`manage_gameobject` initially declares and accepts `if_match`. The condition is checked
on Unity's main thread immediately before the handler starts, not merely at WebSocket
admission. A mismatch returns `STATE_CONFLICT` with the current state and a refresh
hint; use on an undeclared tool returns `PRECONDITION_NOT_SUPPORTED`. Every negotiated
response reports the epoch and before/after global, scene, and asset revisions.

## Response envelope

When `response_envelope_v1` is negotiated, Unity returns one wire-level envelope:

```json
{
  "runtime_version": 1,
  "request_id": "...",
  "status": "succeeded",
  "code": "OK",
  "message": "GameObject updated.",
  "data": {},
  "changes": {
    "objects": [],
    "assets": [],
    "scenes": []
  },
  "diagnostics": [],
  "timing": {
    "queued_ms": 3,
    "execution_ms": 12,
    "total_ms": 15
  },
  "state": {
    "epoch": "...",
    "before_revision": 42,
    "after_revision": 43
  },
  "receipt": {
    "retained_until_unix_ms": 1784371800000,
    "result_digest": "sha256:..."
  }
}
```

Allowed statuses are `accepted`, `queued`, `executing`, `succeeded`, `failed`,
`cancelled`, and `outcome_unknown`. Error `code` values are stable machine tokens;
`message` is human-readable and may change without a protocol bump.

Stack traces, raw payload excerpts, and sensitive paths are diagnostics, not normal
errors. They are redacted, size-limited, and returned only when debug diagnostics were
explicitly negotiated and enabled.

During migration, the Python adapter maps this envelope to the existing MCP-facing
`success`, `message`, `error`, and `data` shape. Legacy connections continue to receive
the current Unity wire response. This avoids changing every MCP client at once.

## Batch execution

Runtime v1 extends `batch_execute` with:

- `dry_run`: resolve tools, contracts, permissions, handles, and preconditions without
  mutating;
- full-batch preflight before the first command starts;
- one stable outer request ID and stable child request IDs;
- optional Unity Undo grouping for eligible scene operations;
- `atomicity: "none" | "undo_group"`;
- `rollback_on_failure` only when every admitted command declares a supported rollback
  strategy;
- exact reporting of started, skipped, reverted, and unknown child outcomes; and
- aggregated changed-object/asset/scene handles.

`rollback_on_failure` must be rejected before execution when the batch includes asset
imports, package changes, compilation, builds, external side effects, or another
operation whose rollback cannot be proven. `fail_fast` only stops later commands; it
does not imply rollback.

The implemented `atomicity` values are `none` and `undo_group`. A requested Undo group
is accepted only when every mutating child has a generated scene contract marked
Undoable and non-compiling; read-only children may participate. Full preflight checks
all child entries, registration, enablement, recursion, parameter shape, and rollback
eligibility before the first invocation. Results distinguish `validated`, `succeeded`,
`failed`, `skipped`, and `reverted`, with exact counters and `rollback_applied`.

## Observability and diagnostics

The runtime records bounded structured events for:

- request admission and rejection;
- queue wait and execution duration;
- lifecycle state transitions;
- dedup hits and ID conflicts;
- cancellation requests and outcomes;
- outcome-unknown transitions;
- queue depth/bytes high-water marks;
- message/response limit violations; and
- negotiated compatibility mode and schema mismatch.

Logs use request ID and stable error code, never full command parameters by default.
Metrics and telemetry must not include project paths, source text, asset contents, API
keys, or cached results.

## Compatibility matrix

| Server | Unity package | Expected mode |
|---|---|---|
| Legacy | Legacy | Existing behavior. |
| Runtime v1 | Legacy | `legacy`; new server uses existing message/response adapters. |
| Legacy | Runtime v1 | `legacy`; new Unity package omits runtime-only enforcement for legacy commands. |
| Runtime v1 | Runtime v1, matching capabilities | `runtime_v1`. |
| Runtime v1 | Runtime v1, schema mismatch | `degraded`; affected mutations fail only when contract compatibility cannot be proven. |

No release may require a synchronized package/server update merely to preserve current
legacy behavior. Features that need runtime guarantees remain disabled until both ends
negotiate them.

## Delivery phases

### Phase 0: characterization

- Capture current timeout, retry, queue, response, reload, and custom-schema behavior.
- Add regression tests without changing runtime behavior.
- Add stress-test baselines and record queue/latency measurements.

### Phase 1: negotiation and contracts

- Add optional handshake metadata and compatibility classification.
- Introduce the built-in manifest and CI drift checker.
- Improve custom-tool schema discovery.
- Add diagnostics only; retain legacy execution.

### Phase 2: bounded transport and control path

- Enforce message and response limits.
- Decouple WebSocket receive from command completion.
- Add bounded admission, control messages, backpressure, and per-frame budgets.

In the Phase 2 implementation, `control_path_v1` guarantees that WebSocket receive,
ping/pong, and admission errors do not wait for command completion. Receipt lookup and
queued cancellation join this path in Phase 3 together with their stable request IDs;
they are not advertised separately before then.

### Phase 3: receipts and retry safety

- Add stable request IDs, payload hashes, receipt ledger, status lookup, and queued
  cancellation.
- Convert reload-time executing receipts to `outcome_unknown`.
- Route all automatic mutation retries through the receipt protocol.

### Phase 4: response and mutation contracts

- Add the v1 response envelope and Python legacy adapter.
- Enforce mutation profiles on both sides.
- Add structured changes, timing, and stable error codes.

### Phase 5: handles, revisions, and batch semantics

- Add stable handles and state epochs/revisions.
- Add `if_match`, batch dry-run/preflight, Undo grouping, and truthful rollback gates.

Each phase is independently reviewable and must preserve the compatibility matrix.

All five phases above are implemented in the current branch. Runtime-only behavior is
still capability-gated, while legacy connections keep their existing transport and
response shapes.

## Verification snapshot

Verified locally on 2026-07-18 with Unity `6000.3.9f1`:

- canonical contract drift check: 48 tools,
  `sha256:da82972704360337d476935e752392434187796b9e8bad60c2dc13943770bb83`;
- Python server suite: 1,388 passed, 3 skipped;
- targeted Phase-5 Unity tests: 44 passed;
- dispatcher precondition tests: 7 passed; and
- complete Unity EditMode suite: 1,222 total, 1,145 passed, 58 skipped,
  18 inconclusive, and one pre-existing German-locale failure in
  `ToolParamsTests.GetFloat_ValidFloat_ReturnsValue` (`2.5` parsed as `25.0`).

External consumer projects and their package/server configurations were outside the
isolated verification scope.

## Required tests and acceptance criteria

### Negotiation

- New server + old package and old server + new package pass existing smoke tests.
- Unknown optional fields are ignored by legacy peers.
- Schema mismatch produces deterministic degraded-mode diagnostics.

### Idempotency

- Sending the same request ID and payload twice invokes the handler exactly once.
- Sending the same request ID with a different payload invokes it zero additional
  times and returns `REQUEST_ID_CONFLICT`.
- A timeout after execution begins never causes automatic execution under a new ID.
- A retry with a retained terminal receipt returns the original terminal outcome.
- Domain reload changes an executing receipt to `outcome_unknown` and does not re-run
  it.

### Queue and control path

- The 65th command is rejected when the limit is 64, without growing queued storage.
- Total queued payload bytes cannot exceed the configured cap.
- Oversized fragmented WebSocket input is rejected before unbounded allocation.
- Ping, receipt lookup, and queued cancellation remain responsive while normal command
  work is queued.
- The dispatcher starts no more than the configured count/budget per frame.

### Responses and security

- Every runtime response validates against the response-envelope schema.
- Oversized results return valid `RESPONSE_TOO_LARGE` JSON.
- Stack traces and payload excerpts are absent unless debug diagnostics are enabled.
- Error messages and persisted receipts contain no API keys or raw tool parameters.

### State and mutation safety

- Stale `if_match` is rejected before the handler executes.
- Handles resolve across a domain reload when Unity provides a stable identity.
- Session-local handles fail clearly after their epoch changes.
- A batch that contains a non-rollbackable command rejects `rollback_on_failure`
  before executing its first child.

### Stress and integration

- Existing Python tests remain green.
- Unity EditMode/PlayMode tests pass on the supported version matrix.
- Existing `stress_mcp.py` and `stress_editor_state.py` baselines do not regress.
- A new retry/reload stress test proves one handler execution per logical request.

## Consumer isolation and rollout guard

Consumer projects may run a stable package/server pair on an operator-owned endpoint.
Runtime development must not alter that setup.

Development rules:

1. Do not edit a consumer project's `Assets`, `Packages`, or `ProjectSettings`.
2. Do not set a consumer project to a `file:` package dependency.
3. Do not set the user-wide Unity server-source or HTTP endpoint overrides while the
   production editor is running.
4. Run Python/unit/characterization tests in this repository.
5. Run Unity integration tests with the repository test project in an isolated test
   session or maintenance window.
6. Do not bind a development server to a consumer's active bridge endpoint.
7. Roll out only a tested, matched package/server pair pinned to a commit or release.
8. Preserve a known-good matched package/server pair as the immediate rollback target.

## Proposed implementation map

| Concern | Python | Unity |
|---|---|---|
| Negotiation models | `Server/src/transport/models.py` | `WebSocketTransportClient.cs` |
| Compatibility state | `plugin_hub.py`, registry session model | WebSocket transport state/details |
| Contract manifest | new `Contracts/` loader and CI checker | manifest loader/hash verifier |
| Custom schemas | Python tool-definition model | `ToolDiscoveryService.cs`, attributes |
| Request coordination | `plugin_hub.py`, `unity_transport.py` | admission controller + dispatcher |
| Receipt ledger | correlation/cache adapter | new project-local receipt service |
| Control messages | `plugin_hub.py` | WebSocket control dispatcher |
| Queue limits | server pending limits | admission controller + dispatcher |
| Response adapter | tool transport adapter | response envelope factory |
| Handles/revisions | schema models | new handle/revision services |
| Batch semantics | `batch_execute.py` | `BatchExecute.cs` |

## Open decisions

The following remain follow-up decisions rather than blockers for Runtime v1:

1. Whether the current queue, byte, and receipt limits should be tuned after
   representative large-project benchmarks.
2. Whether the public MCP surface should eventually expose caller-supplied request IDs
   or keep them internal to the server coordinator.
3. Whether coarse revision hooks should grow per-scene/per-asset identifiers without
   excessive Editor overhead.
4. How broadly stable asset handles and `if_match` should be adopted across the
   remaining public tool signatures.
5. Which built-in tools can truthfully support rollback beyond Unity Undo.

These decisions do not block the additive, negotiated implementation delivered here.
