# UnityMCPBridge Fork

This repository is a maintained fork of [CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp). Upstream documentation remains useful for shared behavior, but the fork adds capabilities on both sides of the bridge. The Unity package and Python server must therefore come from the **same fork revision**.

## Maintainer note

UnityMCPBridge is independently and personally maintained by **Gomez** to serve practical development needs as they arise. Fork-specific development is carried out with substantial assistance from OpenAI Codex and GPT-based tools. Gomez reviews the resulting changes and remains responsible for the project; OpenAI does not sponsor, endorse, maintain, or support this fork.

Development priorities follow the maintainer's own use cases and available time. The project has no guaranteed support, response time, maintenance schedule, compatibility promise, release cadence, or obligation to implement or fix a requested change. Issues and pull requests may be considered when they align with those priorities. The complete policy is available in [`SUPPORT.md`](https://github.com/gitgomez/UnityMCPBridge/blob/main/SUPPORT.md).

This fork is not affiliated with, sponsored by, endorsed by, or supported by Unity Technologies, CoplayDev, Anthropic, or OpenAI. Their names and logos remain the property of their respective owners. Upstream and third-party attribution is documented in [`THIRD_PARTY_NOTICES.md`](https://github.com/gitgomez/UnityMCPBridge/blob/main/THIRD_PARTY_NOTICES.md).

## Install the Unity package

In Unity, open **Window → Package Manager**, select **Add package from git URL...**, and use:

```text
https://github.com/gitgomez/UnityMCPBridge.git?path=/MCPForUnity#v10.2.6
```

The release tag is immutable and is the public installation contract. Do not replace it with `main` if you need a reproducible installation.

If the repository is private, Git must already have credentials that allow Unity Package Manager to clone it. Do not put access tokens in the package URL or commit them to `Packages/manifest.json`.

## Install the matching Python server

Installing the Unity package alone is not enough, but the fork package now derives its matching GitHub server source automatically. It never falls back to the published upstream Python package, which does not contain fork-only commands.

Leave **Server Source Override** empty for a stable release. The default source for `v10.2.6` is:

```text
git+https://github.com/gitgomez/UnityMCPBridge.git@v10.2.6#subdirectory=Server
```

For a local development checkout, set **Server Source Override** to:

```text
E:\UnityMCPBridge\Server
```

The tag in the server source must exactly match the tag in the Unity package URL. Mixing the fork package with the public upstream `mcpforunityserver` package is unsupported because the two sides expose different capabilities.

## Development snapshots

Contributors who deliberately want the current integration state may install the moving development branch instead:

```text
https://github.com/gitgomez/UnityMCPBridge.git?path=/MCPForUnity#optimize/bridge
git+https://github.com/gitgomez/UnityMCPBridge.git@optimize/bridge#subdirectory=Server
```

Use both development URLs together. `optimize/bridge` can move at any time and is not a reproducible or stable installation channel. Enable **Dev Mode** while iterating locally, then configure the required MCP clients from the editor window.

## Verify the pair

After configuration:

1. Start the bridge in **HTTP** mode for shared multi-client use, or stdio only for a deliberately isolated single-client session.
2. Query live capabilities before relying on a command. Fork capabilities include runtime UI inspection/input and command-receipt administration.
3. Confirm the connected Unity instance and editor readiness before mutations.
4. If a fork-only command is missing, first check the Server Source Override and restart/reconfigure the MCP client after correcting it.

## Fork capability map

The fork currently adds or strengthens these areas:

- durable command receipts, retry/reload behavior, stable handles, and explicit `outcome_unknown` handling;
- administrative receipt-ledger diagnosis and safe cleanup outside normal receipt admission;
- runtime uGUI and UI Toolkit inspection, waiting, clicking, dragging, scrolling, text/toggle input, hover, and key events;
- editor-readiness and bounded wait paths for script reloads and Play Mode transitions;
- guarded external YAML changes and save receipts;
- composited screenshot capture and matching CLI entry points.

The complete machine-readable surface is owned by [`Contracts/tool-contracts.v1.json`](https://github.com/gitgomez/UnityMCPBridge/blob/main/Contracts/tool-contracts.v1.json). Generated tool pages and hand-written summaries must not override that contract.

## Safe receipt-ledger recovery

Use the administrative CLI path when normal command admission is blocked or when ledger growth needs diagnosis:

```bash
unity-mcp receipts status
unity-mcp receipts cleanup
```

The default cleanup removes only **expired terminal** receipts. It never deletes `accepted`, `queued`, or `executing` receipts. To remove all known terminal receipts regardless of age, select it explicitly:

```bash
unity-mcp receipts cleanup --all-terminal
```

`outcome_unknown` is reported separately because the command's side effects may already have occurred. It is retained unless separately confirmed:

```bash
unity-mcp receipts cleanup --confirm-outcome-unknown
unity-mcp receipts cleanup --all-terminal --confirm-outcome-unknown
```

Cleanup persists the retained snapshot before changing in-memory state. If persistence fails with `RECEIPT_CLEANUP_PERSIST_FAILED`, memory remains unchanged; resolve the storage problem and inspect status again before retrying. The detailed design and invariants live in [`docs/development/COMMAND_RUNTIME_V1.md`](https://github.com/gitgomez/UnityMCPBridge/blob/main/docs/development/COMMAND_RUNTIME_V1.md#administrative-ledger-diagnosis-and-recovery).

## Working on the fork

Start with these sources, in order:

1. [`AGENTS.md`](https://github.com/gitgomez/UnityMCPBridge/blob/main/AGENTS.md) for repository routing and guardrails.
2. [`CLAUDE.md`](https://github.com/gitgomez/UnityMCPBridge/blob/main/CLAUDE.md) for architecture and maintenance patterns.
3. [`unity-mcp-skill/SKILL.md`](https://github.com/gitgomez/UnityMCPBridge/blob/main/unity-mcp-skill/SKILL.md) for runtime-safe operation and live capability discovery.
4. [`Contracts/tool-contracts.v1.json`](https://github.com/gitgomez/UnityMCPBridge/blob/main/Contracts/tool-contracts.v1.json) for the canonical tool contract.
5. [`docs/development/COMMAND_RUNTIME_V1.md`](https://github.com/gitgomez/UnityMCPBridge/blob/main/docs/development/COMMAND_RUNTIME_V1.md) for receipt and command-runtime semantics.

Generated references must be refreshed from their owners rather than hand-edited:

```powershell
Server\.venv\Scripts\python.exe tools\skill_contracts.py --check
Server\.venv\Scripts\python.exe tools\generate_docs_reference.py --check
```

Run the smallest relevant test set for a change. A clean build or tool response alone is not proof of runtime interaction; verify behavior in a connected Unity Editor when the change affects editor state, Play Mode, UI, transport, or routing.

The stable public branch is `main`; it matches the latest release tag. Fork development and pull requests target `optimize/bridge`, which is promoted to `main` only after the release checks pass. There is no fork `beta` branch. The `upstream` remote is reference material; follow upstream's `beta` workflow only when preparing an upstream contribution.
