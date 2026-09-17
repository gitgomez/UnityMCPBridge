# Testing

Three test suites cover MCP for Unity: Python unit tests, Unity EditMode/PlayMode tests, and a multi-version Unity compile matrix. GitHub runs the credential-free Python and documentation checks. Unity tests use an already activated local Editor; the hosted workflow reports a non-blocking skip because this repository does not store Unity credentials in GitHub.

## Python tests

Location: `Server/tests/`

```bash
# All tests
cd Server && uv run pytest tests/ -v

# Single file
cd Server && uv run pytest tests/test_manage_material.py -v

# Single test by name pattern
cd Server && uv run pytest tests/ -k "test_create_material" -v
```

CI workflow: `.github/workflows/python-tests.yml`. Coverage is uploaded to Codecov on every run.

### Adding a Python test

For a new tool `manage_<domain>`, add `Server/tests/test_manage_<domain>.py`. Existing tests are the best template — most are integration-style: they spin up a fake Unity bridge, call the tool, and assert on the dispatched payload.

## Unity tests

Location: `TestProjects/UnityMCPTests/Assets/Tests/`

- **EditMode** (60+ files): tool validation, parser edge cases, scene paging, domain reload resilience, batch execution, AI property matching, scriptable objects, animation, physics, gameobject lifecycle
- **PlayMode**: basic integration smoke tests

To run locally, open `TestProjects/UnityMCPTests` in Unity, then **Window → General → Test Runner**.

`.github/workflows/unity-tests.yml` exposes the optional hosted matrix but does
not receive Unity credentials in this repository. It records a non-blocking
skip; local execution is the authoritative Unity verification path.

### Local headless test harness

One command boots a headless Hub-licensed Editor against `TestProjects/UnityMCPTests` and runs the smoke + EditMode + PlayMode legs over the bridge, then tears down:

```bash
python tools/local_harness.py
```

This is the same entrypoint described by `.github/workflows/e2e-bridge.yml`.
The GitHub workflow remains credential-free and skips the Editor boot; run the
harness locally with a normally Hub-activated Editor for real evidence.

Key flags:

- `--legs smoke,editmode,playmode` — subset of legs to run.
- `--project-path TestProjects/UnityMCPTests` — Unity project to boot (repo-relative or absolute).
- `--reuse` — attach to an already-resident bridge instead of booting one.
- `--keep-alive` — leave the Editor running after the legs (no teardown).
- `--no-warmup` — skip the warm-up import phase.

Exit-code contract: `0` all blocking legs passed, `1` a blocking-leg regression, `2` bridge unreachable / setup failure, `3` project does not compile, `4` no Unity license / Hub seat, `5` Editor binary/version not found.

It needs a Hub-activated Editor locally (no ULF/serial); none of the CI license staging applies.

### Adding a Unity test

Mirror the C# tool you're adding. For `ManageNavigation.cs` in `MCPForUnity/Editor/Tools/`, create `TestProjects/UnityMCPTests/Assets/Tests/EditMode/Editor/ManageNavigationTests.cs`. Use the existing assembly definition (`MCPForUnityTests.Editor.asmdef`) so the suite picks it up automatically.

## Multi-version compile matrix

This is the most common pre-push surprise: code that builds on your Unity version fails on another supported version because of an API rename. The local matrix check prevents that.

```bash
tools/check-unity-versions.sh           # compile-only across installed Unity Hub editors
tools/check-unity-versions.sh --full    # full EditMode test run on each version
```

Full mode lets Unity's command-line test runner own Editor shutdown; it does
not combine `-runTests` with an eager `-quit`. Every checked version must write
a parseable, non-empty, passing XML report under `tools/unity-check-results/`.
A missing report, a zero-test run, or failed results fail the matrix even when
the Unity process itself returns exit code `0`.

The matrix is `tools/unity-versions.json`. The script discovers Unity installations via Unity Hub's standard locations on macOS, Windows, and Linux.

When you touch anything in `MCPForUnity/Runtime/Helpers/Unity*Compat.cs` or any `#if UNITY_*_OR_NEWER` block, run this. The [Unity Compat Shims](../architecture/unity-compat.md) document explains the policy.

## Pre-push hook

`tools/install-hooks.sh` installs a pre-push hook that runs `check-unity-versions.sh` in compile-only mode when your push touches Unity-relevant paths. One-time setup:

```bash
tools/install-hooks.sh
```

To bypass for a single push: `git push --no-verify`. Use this when you're pushing docs-only or pure Python changes.

## Pre-commit hook (docs reference)

The same install script wires a pre-commit hook that regenerates `docs/reference/` whenever you stage a change under `Server/src/services/{tools,resources,registry}/`. CI fails if you skip this and the committed reference drifts — see `.github/workflows/docs-generate.yml`.

## Stress / load testing

Two scripts under `tools/`:

- `stress_mcp.py` — concurrent MCP tool calls; surfaces middleware contention
- `stress_editor_state.py` — hammers the `editor_state` resource; surfaces serialization hotspots

These are not part of CI; run them when you change transport, middleware, or hot-path serialization.

## What CI actually runs on every PR

| Workflow | Trigger | Duration | What it asserts |
|---|---|---|---|
| `python-tests.yml` | `Server/**` changes | ~2 min | `pytest` clean, coverage uploaded |
| `unity-tests.yml` | `MCPForUnity/**` / `TestProjects/**` changes | ~15 min × N versions | EditMode + PlayMode tests clean across the matrix |
| `docs-generate.yml` | tool/resource registry or `docs/reference/**` changes | ~1 min | Reference Markdown is not stale; decorator count matches page count |

Skip-equivalent: if the only files you changed are README, governance, or unrelated metadata, only the relevant subset of these fires.
