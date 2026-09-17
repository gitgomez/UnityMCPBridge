# UnityMCPBridge Repository Instructions

Version: 1.1
Updated: 2026-09-17
Status: active
Document type: normative router
Owner: UnityMCPBridge maintainers

## Purpose

This repository is a maintained fork of `CoplayDev/unity-mcp`. These instructions route development and maintenance work to the canonical sources. They are not an end-user manual; installation and operation belong in `README.md` and `docs/`.

## Before Any Change

1. Run `git status --short --branch` and inspect affected diffs.
2. Read `CLAUDE.md` and the smallest relevant sources from the routing table below.
3. Verify the current branch, remotes, and write permissions. Do not assume a previous Bridge, Unity, transport, server, or Editor session still exists.
4. Treat existing tracked or untracked changes as operator-owned. Do not clean, overwrite, stage, or absorb them unless the task explicitly includes them.
5. Find the existing cross-layer connection before adding a new command, abstraction, recovery path, or documentation owner.

The normal fork development line is `optimize/bridge`. The stable public branch is `main` and must match the latest release tag; promote tested release commits from `optimize/bridge` rather than developing directly on `main`. The `upstream` remote is reference material; use its `beta` workflow only for an explicitly upstream-bound contribution.

## Source Hierarchy

When sources disagree, use this order:

1. Live behavior and the actual checked-out code establish what is implemented.
2. `Contracts/tool-contracts.v1.json` owns the machine-readable tool contract.
3. Live capability discovery owns what the connected runtime currently exposes.
4. `unity-mcp-skill/SKILL.md` owns safe operation and verification guidance.
5. Generated tool references describe contract output; they are not independent authorities.
6. Hand-written Markdown, README, and design documents explain the system and must link to the canonical owner rather than redefine it.

Do not invent a missing router, command, fallback, or compatibility promise. Resolve structurally relevant contradictions before implementation.

## Repository Boundaries

The Bridge is one versioned system with multiple surfaces:

- `MCPForUnity/`: Unity Editor package and C# command handlers.
- `Server/`: Python MCP server, transport, CLI, and receipt administration.
- `Contracts/`: canonical tool contracts.
- `unity-mcp-skill/`: runtime-safe agent guidance and generated capability/reference files.
- `docs/`: public installation, operation, troubleshooting, and reference documentation.
- `docs/development/`: implementation design and runtime invariants.
- `TestProjects/UnityMCPTests/` and `Server/tests/`: Unity and Python regression coverage.

Fork-only behavior usually requires a matched Unity package and Python server revision. Installing the package from this repository while using the published upstream Python server is not a valid fork verification.

## Task Routing

| Work area | Read before changing |
| --- | --- |
| Tool signature, result shape, or capability | `Contracts/tool-contracts.v1.json`, `unity-mcp-skill/SKILL.md`, relevant Python and C# handlers |
| Transport, routing, instance selection, or readiness | `unity-mcp-skill/SKILL.md`, `docs/development/`, relevant `Server/src/` and `MCPForUnity/Editor/` sources |
| Receipts, retry, reload, handles, or ledger recovery | `docs/development/COMMAND_RUNTIME_V1.md`, receipt implementation, CLI commands, targeted tests |
| Runtime uGUI or UI Toolkit interaction | `unity-mcp-skill/SKILL.md`, `interact_play_mode` contract, Python tool/CLI, Unity handler, UI tests |
| Package installation or client setup | `README.md`, `docs/getting-started/install.md`, `docs/getting-started/bridge-fork.md` |
| Versioning, release preparation, or tags | `docs/contributing/releases.md`, `tools/update_versions.py`, `.github/workflows/release.yml` |
| CLI behavior | live `--help`, `Server/src/cli/commands/`, `docs/reference/cli.md`, `Server/src/cli/CLI_USAGE_GUIDE.md` |
| Generated tool documentation | generator source plus `tools/generate_docs_reference.py`; do not hand-edit generated pages |
| Unity version compatibility | `MCPForUnity/Runtime/Helpers/UnityCompatShims.cs`, `tools/unity-versions.json`, relevant upgrade guidance |

## Development Guardrails

- Keep Python MCP tools, CLI commands, C# handlers, contracts, tests, and documentation synchronized where a feature crosses those boundaries.
- Preserve receipt cleanup invariants: `accepted`, `queued`, and `executing` are never deleted; `outcome_unknown` is separate and requires explicit confirmation; persistence succeeds before in-memory replacement.
- Do not call batch execution transactional. It is an efficient grouped execution path without rollback semantics.
- Use live capability discovery before operating a connected Unity Editor. Tool availability in source does not prove the active server exposes it.
- Do not treat a transport success, clean build, or tool response as proof of final Editor or gameplay behavior.
- Avoid broad refactors, speculative compatibility shims, and new central abstractions outside the requested feature.
- Never commit credentials, tokens, machine-specific local paths outside documented examples, generated caches, local workspaces, or operator handover notes.

## Generated Documentation

Public repository documentation is maintained in English. A separate German
version may be added when deliberately requested; do not add other language
variants or mirrored translations.

Run these from the repository root after contract or tool-surface changes:

```powershell
Server\.venv\Scripts\python.exe tools\skill_contracts.py --check
Server\.venv\Scripts\python.exe tools\generate_docs_reference.py --check
```

If the second check reports stale output, run the generator without `--check`, review the exact generated diff, and rerun the check. Do not patch a generated tool page directly.

The repository-local skill is canonical for this checkout. An installed user copy may be stale; compare or refresh it deliberately rather than silently treating it as current.

## Verification

Use the smallest change-related verification set:

1. Contract and generated-reference checks for tool-surface changes.
2. Targeted Python tests for server, CLI, transport, or receipt behavior.
3. Targeted Unity EditMode/PlayMode tests for C# and Editor behavior.
4. A representative live Unity path for Editor state, Play Mode, UI, transport, or routing changes.
5. A documentation-link and command check when installation, recovery, or CLI guidance changes.

Do not start or mutate Unity, servers, packages, test projects, or transports during a documentation-only task unless the task explicitly authorizes it.

## Completion

- Recheck `git status --short --branch` and review the complete diff.
- Report checks actually run, checks not run, and any runtime path still unverified.
- Keep unrelated and local-only files out of commits.
- Commit only when requested, using a message that describes the actual scoped change.
