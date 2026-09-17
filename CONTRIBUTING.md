# Working on UnityMCPBridge

Thank you for considering a contribution. UnityMCPBridge is a personal,
needs-driven fork rather than a staffed product or a community roadmap. A
focused proposal with a clear reason, bounded implementation, and relevant
evidence is the easiest kind of contribution to review.

Before spending substantial time, check the existing issues and consider
opening a short proposal. Review and acceptance are never guaranteed; the
[support policy](SUPPORT.md) applies to contributions as well as bug reports.

## Choose the right project

This repository is built on
[CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp). CoplayDev and its
contributors own the upstream development line and deserve credit for the core
Unity package, Python server, transport foundation, and much of the tool set.

Send a change here when it concerns fork behavior, such as command receipts,
runtime UI interaction, Editor-readiness, fork contracts, or fork installation.
If a change is broadly useful and independent of this fork, consider proposing
it directly to CoplayDev instead. Do not expect either project to review or
merge a change on behalf of the other.

## Branch and pull-request flow

1. Fork the repository and fetch the current `optimize/bridge` branch.
2. Create a narrowly named branch from that exact revision.
3. Implement the smallest coherent change, including the layers it truly
   affects.
4. Run the relevant checks and record what was not run.
5. Open the pull request against `optimize/bridge`, not `main`.

```bash
git checkout optimize/bridge
git pull --ff-only origin optimize/bridge
git checkout -b fix/short-description
```

`main` is the stable release line and must match the latest release tag. Gomez
alone promotes releases and publishes version tags.

## Preserve the bridge contract

The Unity package and Python server are one versioned system. A change crossing
the bridge may require all of the following:

- the Python MCP tool or resource under `Server/src/`;
- the matching Unity handler under `MCPForUnity/`;
- CLI behavior where the capability is also exposed to operators;
- `Contracts/tool-contracts.v1.json` and generated agent references;
- focused Python or Unity regression coverage; and
- the smallest relevant hand-written documentation update.

Start with [AGENTS.md](AGENTS.md), then use the routing table there instead of
inventing a parallel abstraction or documentation owner. Development setup is
described in [docs/contributing/dev-setup.md](docs/contributing/dev-setup.md).

## Evidence expected with a change

Report the checks that demonstrate the changed behavior. Typical commands are:

```bash
# Python server tests
cd Server
uv run pytest tests/ -v

# Return to the repository root for contract and documentation checks
cd ..
uv run --project Server --extra dev python tools/tool_contracts.py --check
uv run --project Server --extra dev python tools/skill_contracts.py --check
uv run --project Server --extra dev python tools/generate_docs_reference.py --check
```

For Unity code, run the focused EditMode or PlayMode tests in
`TestProjects/UnityMCPTests`. Changes involving Editor state, runtime UI,
transport, or Play Mode also need a representative live-Editor check; a clean
compile alone does not prove those paths.

Use `tools/check-unity-versions.sh` when changing compatibility shims or
version-gated Unity APIs. Do not upload Unity credentials to GitHub for CI.

## Generated and hand-written documentation

Narrative documentation lives under `docs/`. Tool pages under
`docs/reference/` are generated from the live Python registry. Do not edit a
generated page outside its marked example block. Regenerate it through
`tools/generate_docs_reference.py` and review the resulting diff.

Repository documentation is maintained in English, with German allowed where
a separate translation is deliberately useful. Do not add additional language
variants without prior agreement.

## Pull-request notes

A useful pull request explains:

- the concrete problem and why it belongs in this fork;
- the behavior before and after the change;
- affected Python, Unity, contract, CLI, and documentation surfaces;
- tests and live checks actually performed; and
- known limitations or paths that remain unverified.

Keep unrelated formatting, generated churn, personal workspace files,
credentials, and machine-specific paths out of the change. External pull
requests receive read-only GitHub permissions and no maintainer secrets.

## Communication and safety

Use the issue templates for reproducible bugs and bounded feature proposals.
Report vulnerabilities privately according to [SECURITY.md](SECURITY.md). All
participation is subject to [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
