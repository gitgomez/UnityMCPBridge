# CLI Reference

The `mcp-for-unity` CLI is a developer-facing terminal for the same Unity automations the MCP tools expose. Both invoke the same C# `HandleCommand` methods on the Unity side — see [Three-Layer Python Design](../architecture/python-layers.md) for why both layers exist.

## Invocation

```bash
# Run via uvx (no install)
uvx --from mcpforunityserver mcp-for-unity <command> [args]

# Run from a Server checkout
cd Server && uv run mcp-for-unity <command> [args]

# Run via the dedicated CLI entry point (alias)
uvx --from mcpforunityserver unity-mcp <command> [args]
```

## How it talks to Unity

The CLI uses **HTTP** to the Python server (default `http://127.0.0.1:8080`), regardless of how your MCP clients are configured. The Python server in turn talks to the connected Unity Editor via WebSocket. MCP tools take a similar path via WebSocket directly; CLI commands take HTTP.

## Global flags

| Flag | Default | Meaning |
|---|---|---|
| `--host` | `127.0.0.1` | Python server host to connect to |
| `--port` | `8080` | Python server port |
| `--instance` | (auto) | Target Unity instance (`Name@hash`, hash prefix, or port number) |
| `--format` | `text` | Output format: `text`, `json`, or `table` |
| `--timeout` | `30` | Command timeout in seconds |
| `--verbose / -v` | off | Print full request/response payloads |
| `--version` | — | Print CLI version and exit |
| `--help` | — | Show command help |

For multi-instance setups, see [Multi-Instance Routing](../guides/multi-instance.md).

## Command groups

The CLI mirrors the MCP tool catalog. Each command group wraps one or more `manage_*` tools.

| Group | What it does | Equivalent MCP tool |
|---|---|---|
| `mcp-for-unity instance` | List instances, check connection, set active | [`set_active_instance`](./tools/core/set_active_instance.md) |
| `mcp-for-unity scene` | Load/save/query/edit scenes | [`manage_scene`](./tools/core/manage_scene.md) |
| `mcp-for-unity gameobject` | Create/transform/delete GameObjects | [`manage_gameobject`](./tools/core/manage_gameobject.md) |
| `mcp-for-unity component` | Add/remove/configure components | [`manage_components`](./tools/core/manage_components.md) |
| `mcp-for-unity script` | Create/read/modify C# scripts | [`manage_script`](./tools/core/manage_script.md) |
| `mcp-for-unity asset` | Asset import/create/modify/search | [`manage_asset`](./tools/core/manage_asset.md) |
| `mcp-for-unity asset-gen` | Generate and import models, images, and audio through configured providers | [Asset generation tools](./tools/asset_gen/index.md) |
| `mcp-for-unity material` | Material CRUD + shader props | [`manage_material`](./tools/core/manage_material.md) |
| `mcp-for-unity prefab` | Prefab create/instantiate/unpack | [`manage_prefabs`](./tools/core/manage_prefabs.md) |
| `mcp-for-unity texture` | Texture create + patterns/gradients | [`manage_texture`](./tools/vfx/manage_texture.md) |
| `mcp-for-unity shader` | Shader CRUD | [`manage_shader`](./tools/vfx/manage_shader.md) |
| `mcp-for-unity vfx` | VFX, particle systems, trails | [`manage_vfx`](./tools/vfx/manage_vfx.md) |
| `mcp-for-unity camera` | Camera + Cinemachine presets | [`manage_camera`](./tools/core/manage_camera.md) |
| `mcp-for-unity graphics` | Volumes, post-processing, light bake | [`manage_graphics`](./tools/core/manage_graphics.md) |
| `mcp-for-unity lighting` | Lighting-specific operations | (subset of graphics) |
| `mcp-for-unity physics` | 3D + 2D physics, joints, queries | [`manage_physics`](./tools/core/manage_physics.md) |
| `mcp-for-unity audio` | Audio operations | (subset of asset) |
| `mcp-for-unity animation` | Animator + AnimationClip | [`manage_animation`](./tools/animation/manage_animation.md) |
| `mcp-for-unity ui` | UI Toolkit — UXML/USS/UIDocument | [`manage_ui`](./tools/ui/manage_ui.md) |
| `mcp-for-unity build` | Player builds across platforms | [`manage_build`](./tools/core/manage_build.md) |
| `mcp-for-unity editor` | Editor state, Play Mode, runtime UI interaction, undo/redo | [`manage_editor`](./tools/core/manage_editor.md), [`interact_play_mode`](./tools/core/interact_play_mode.md) |
| `mcp-for-unity packages` | UPM install/remove/embed | [`manage_packages`](./tools/core/manage_packages.md) |
| `mcp-for-unity probuilder` | ProBuilder meshes | [`manage_probuilder`](./tools/probuilder/manage_probuilder.md) |
| `mcp-for-unity profiler` | Profiler session + counters + snapshots | [`manage_profiler`](./tools/profiling/manage_profiler.md) |
| `mcp-for-unity code` | Execute arbitrary C# in the Editor | [`execute_code`](./tools/scripting_ext/execute_code.md) |
| `mcp-for-unity batch` | Run multiple operations efficiently; execution is not transactional | [`batch_execute`](./tools/core/batch_execute.md) |
| `mcp-for-unity receipts` | Diagnose and safely clean the command-receipt ledger | Administrative runtime path |
| `mcp-for-unity tool` | Activate/deactivate tool groups | [`manage_tools`](./tools/core/manage_tools.md) |
| `mcp-for-unity custom_tool` | Alias for custom-tool discovery | [`execute_custom_tool`](./tools/core/execute_custom_tool.md) |
| `mcp-for-unity raw` | Send a named Unity command with an explicit JSON payload | Advanced escape hatch |
| `mcp-for-unity reflect` | Inspect Unity APIs via reflection | [`unity_reflect`](./tools/docs/unity_reflect.md) |
| `mcp-for-unity docs` | Fetch Unity docs (ScriptReference, Manual) | [`unity_docs`](./tools/docs/unity_docs.md) |

## Discovering subcommands and flags

Every group supports `--help`:

```bash
mcp-for-unity scene --help
mcp-for-unity scene load --help
```

The help text is the authoritative per-command reference — flags, choices, and defaults all live there because the CLI is built on Click and self-describes.

## Runtime UI commands

The `editor` group exposes deterministic Play Mode interaction for both uGUI and UI Toolkit:

```bash
mcp-for-unity editor play-ui-status
mcp-for-unity editor inspect-ui --help
mcp-for-unity editor wait-ui --help
mcp-for-unity editor click-ui --help
mcp-for-unity editor drag-ui --help
mcp-for-unity editor scroll-ui --help
mcp-for-unity editor set-ui-text --help
mcp-for-unity editor set-ui-toggle --help
mcp-for-unity editor hover-ui --help
mcp-for-unity editor key-ui --help
```

Inspect and wait before mutating. UI commands require Play Mode and a live target; successful transport alone does not prove the intended gameplay result.

## Administrative receipt recovery

The `receipts` group remains reachable when normal receipt admission is unavailable:

```bash
mcp-for-unity receipts status
mcp-for-unity receipts cleanup
mcp-for-unity receipts cleanup --all-terminal
mcp-for-unity receipts cleanup --confirm-outcome-unknown
```

Default cleanup removes only expired terminal receipts. Active `accepted`, `queued`, and `executing` receipts are never candidates. `outcome_unknown` is counted separately and requires `--confirm-outcome-unknown` because side effects may already have occurred. If cleanup reports `RECEIPT_CLEANUP_PERSIST_FAILED`, in-memory state was not changed; repair persistence, inspect status, and retry. See the [fork recovery guide](../getting-started/bridge-fork.md#safe-receipt-ledger-recovery) for the complete safe path.

## Examples

See [CLI Examples](../guides/cli-examples.md) for end-to-end walkthroughs and the [CLI Usage Guide](../guides/cli.md) for narrative context (when to use the CLI vs an MCP client).

## Source

CLI command definitions: [`Server/src/cli/commands/`](https://github.com/gitgomez/UnityMCPBridge/tree/main/Server/src/cli/commands). Entry point: [`Server/src/cli/main.py`](https://github.com/gitgomez/UnityMCPBridge/blob/main/Server/src/cli/main.py).
