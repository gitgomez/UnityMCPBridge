# Project direction

UnityMCPBridge is a personally maintained, needs-driven fork. Gomez develops it
for practical use with substantial assistance from Codex and GPT-based tools.
Priorities follow the maintainer's current use cases and available time; this
page is not a delivery schedule or compatibility commitment.

## Current direction

The fork currently concentrates on:

- deterministic runtime uGUI and UI Toolkit inspection and interaction;
- durable command receipts, retry and reload behavior, and safe ledger recovery;
- explicit Editor readiness and bounded wait paths;
- guarded external changes and verifiable save results;
- matched Unity-package, Python-server, CLI, contract, test, and documentation
  behavior.

Implemented behavior is documented in the
[UnityMCPBridge fork guide](../getting-started/bridge-fork.md) and the generated
[tool reference](../reference/tools/index.md). Those sources describe the current product;
this direction page does not override them.

## Requests and contributions

Fork-specific bugs and focused proposals can be submitted through
[GitHub Issues](https://github.com/gitgomez/UnityMCPBridge/issues). Proposed
changes can be submitted through
[Pull Requests](https://github.com/gitgomez/UnityMCPBridge/pulls) following the
[contribution guide](https://github.com/gitgomez/UnityMCPBridge/blob/main/CONTRIBUTING.md).

Issues and pull requests may be reviewed when they align with the maintainer's
needs and available time. They do not create a guaranteed response, roadmap,
implementation, or release obligation. See the
[support policy](https://github.com/gitgomez/UnityMCPBridge/blob/main/SUPPORT.md).

## Upstream relationship

Historical release notes, migration guides, issue links, and author credits may
refer to [`CoplayDev/unity-mcp`](https://github.com/CoplayDev/unity-mcp). Those
references document the upstream origin and do not transfer upstream plans,
support channels, or commitments to this fork.
