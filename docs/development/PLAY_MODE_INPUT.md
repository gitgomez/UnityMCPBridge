# Bounded gameplay input

Available starting with v10.3.0; use matching Unity package and server versions.

`input_play_mode` complements `interact_play_mode`: it queues device state through
the optional Unity Input System, not UI events, application callbacks or OS input.
`manage_input` remains the input-asset authoring tool. No package is installed by
this tool. The initial verification target is Unity 6000.3 with Input System 1.18.

## Contract and implementation plan

- `status`: readiness, backend/update mode, device identities and the current
  Bridge operation; never returns physically pressed keys or captured text.
- `key`: 1-8 distinct named keys held together for 0.05-5 seconds (default 0.1).
- `move`: one absolute normalized Game View position (top-left), or a relative
  pixel delta (positive Y upward, maximum 4096 per axis).
- `click`: left/right/middle at an explicit normalized position; optional keys.
- `drag`: explicit start/end positions, 1-64 movement samples (default 8),
  spread over the bounded duration, with a button held; optional keys.
- `scroll`: one wheel delta (maximum 100 per axis, positive Y upward) at an
  explicit position; optional keys. Values are Input System scroll units, not
  a promise about application distance or a number of OS wheel detents.
- `cancel`: requires the exact active `operation_id` returned by status. A stale
  ID cannot cancel a newer operation. Already-dispatched gameplay is not undone.

All mutation is unsafe to replay without its Runtime-v1 receipt. A gesture owns
one asynchronous command/receipt; frames do not allocate additional receipts.
Cancellation is a separate bounded command, not a transaction rollback.
`status`/`cancel` can run while a gesture is executing. A successful cancel response
confirms cleanup, while the original gesture reports `input_cancelled` (a failed
receipt with partial effects, not a claim that execution never started). Query
status and inspect gameplay after a lost result rather than repeating input.

## Ownership and lifecycle

Use temporary virtual Keyboard/Mouse devices, only when needed by the gesture.
Never reset, disable, warp or write to physical devices. Input reaches ordinary
`Keyboard.current` / `Mouse.current` polling and compatible unpaired InputActions.
Device addition/removal is observable. Device-bound actions are not automatically
rebound. InputUser/PlayerInput configurations are rejected, since pairing or
automatic scheme switching would require a different explicit contract.

Only stable, unpaused Play Mode with Dynamic Input System updates is supported.
Fixed-only/manual updates and the legacy Input Manager are unsupported. The tool
does not change update settings, focus, cursor lock, bindings or control schemes.
Pointer input requires an unlocked cursor and primary Game View dimensions.
The Game View must receive input under the project's existing focus/background
policy. Focus the Game View for ordinary defaults; the tool never changes that
policy. An unavailable/disabled virtual device is an explicit admission failure.
Physical input must be idle; a switch away from a Bridge-owned current device
interrupts the operation. This is automation, not a way to combine a human and
an agent's simultaneous controls.

One gesture per Editor. State is queued, consumed by a normal Dynamic update,
and retained through at least one subsequent player frame before another phase.
Normal completion queues release and waits for gameplay to observe it before
removing the virtual devices. No manual `InputSystem.Update` call is permitted.
The actual owned-device state is checked after Dynamic processing; an ignored
event returns `input_state_not_observed`, not an inferred success.
A wall-clock deadline of 10 seconds includes frame stalls and release observation;
requested duration is a minimum, not a precise real-time timing guarantee.
Deadlines are checked on Editor ticks; a completely stalled Editor cannot run
cleanup until its main thread resumes.

Cancellation, pause, Play Mode exit, compilation, reload, quit or loss of the
transport connected at admission cleans up only the owned devices. Emergency
cleanup resets and removes them immediately; it cannot promise a release polling
frame, though InputActions receive normal device cancellation. Results report
partial dispatch and cleanup failures. An interrupted gesture is never resumed
automatically after reload. Runtime-v1 remains the sole persistent receipt owner.

## Verification plan

The existing test project requires Active Input Handling set to Input System or
Both before launching the Editor (restart if changed). The local ProjectSettings
asset is ignored by Git. The test assembly references the already installed
Input System package. Batch fixtures temporarily select IgnoreFocus and
AllDeviceInputAlwaysGoesToGameView and restore them; production does not.

Validate MCP/CLI parameter parity, strict C# admission, busy/stale cancellation,
key/chord edges across real player frames, mouse click/drag/delta/wheel, cleanup
and physical-device preservation in the existing test project. Then exercise a
representative key and pointer path through actual MCP, reading a normal Update
observer afterwards. Handler-only tests are not transport acceptance. Record
unverified platforms/configurations separately from the verified paths.

### Verified local result (2026-09-27)

- 384 targeted Python/CLI/contract tests passed, including 51 input-specific
  cases and rejection of numeric booleans through the actual MCP schema.
- 20 EditMode and six PlayMode input tests passed on Unity 6000.3.9f1 with
  Input System 1.18.0. These cover normal polling/action edges, chord, three mouse
  buttons, drag samples, relative movement, wheel, busy/stale cancellation,
  pause cleanup and preservation of another device's pressed key.
- An actual MCP client called the published `input_play_mode` endpoint in the
  isolated test Editor: key, move, click, drag, wheel, status and concurrent
  cancellation. A normal gameplay `Update` observer saw 80 chord-held frames,
  five movement frames, wheel Y=2 and two InputAction perform/cancel pairs.
  Each gesture returned its own Runtime-v1 receipt; cancellation succeeded while
  the original hold correctly returned `input_cancelled`. No owned devices were
  left. Console errors/warnings: zero.
- Contract/reference checks passed for the v10.3.0 implementation.
  No Mac/Linux verification or injected reload/disconnect/quit failure was run;
  those cleanup hooks are implemented but not claimed as separately exercised.

`status.ready` is configuration preflight, not a promise that device allocation,
focus-dependent delivery or application behavior will succeed. Always check the
gesture result and application state.

## Recovery

Use `status`, then `cancel` with its active ID. If transport is unavailable,
the watchdog releases owned devices; pause or leave Play Mode also releases them.
After reload, inspect gameplay and the original receipt before retrying. Do not
reset the physical keyboard/mouse or delete the receipt ledger to recover input.
