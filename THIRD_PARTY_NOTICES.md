# Third-Party Notices

Last reviewed: 2026-09-17

This file records the principal third-party code and dependency-license
boundaries of UnityMCPBridge. It supplements, but does not replace, the license
text supplied by each third party. Exact dependency versions are recorded in
`Server/uv.lock`, `MCPForUnity/package.json`, and the pinned revisions in
`.github/workflows/`.

## Upstream project

UnityMCPBridge is a fork of
[`CoplayDev/unity-mcp`](https://github.com/CoplayDev/unity-mcp). The upstream
code was received under the MIT License. Its copyright notice remains in
[`LICENSE`](LICENSE), alongside the notice for fork-specific modifications.

## Source included in this repository

| Component | Location | License and attribution |
| --- | --- | --- |
| Tommy TOML parser | `MCPForUnity/Editor/External/Tommy.cs` | MIT License, copyright 2020 Denis Zhidkikh. The complete notice is retained at the top of the source file. |

The former Asset Store upload fixture and its embedded copy of Unity Asset
Store Tools are not part of this fork's release model and are not included in
the current repository tree.

## Unity Package Manager dependencies

`MCPForUnity/package.json` declares Unity registry packages, including Unity
modules, Newtonsoft Json for Unity, and the Unity Test Framework. Those
packages are resolved by Unity Package Manager; their source is not vendored in
the UnityMCPBridge package. Each resolved package remains governed by the
license and notices distributed with that package.

The installable Unity package contains its own copies of this project's
[`LICENSE.md`](MCPForUnity/LICENSE.md) and
[`THIRD_PARTY_NOTICES.md`](MCPForUnity/THIRD_PARTY_NOTICES.md), so attribution
does not depend on access to the repository root.

## Python server dependencies

The server is distributed as source and resolves its dependencies from
`Server/pyproject.toml` and `Server/uv.lock`. Its direct runtime dependencies
use permissive licenses:

| Dependency | Declared license |
| --- | --- |
| Click | BSD-3-Clause |
| FastAPI | MIT |
| FastMCP | Apache-2.0 |
| HTTPX | BSD-3-Clause |
| MCP Python SDK | MIT |
| Pydantic | MIT |
| tomli | MIT |
| Uvicorn | BSD-3-Clause |

Direct test dependencies use MIT or Apache-2.0. Notable transitive licenses in
the locked environment include MPL-2.0 (`certifi`), Apache-2.0/BSD
(`cryptography`), PSF licenses (`pywin32`, `typing_extensions`), and the
Unlicense (`email-validator`). The metadata installed with each dependency is
the authority for its exact license text.

The server source distribution and wheel include
`Server/THIRD_PARTY_NOTICES.md` beside the server's MIT license metadata.

## Repository automation

GitHub Actions referenced by this repository are pinned to immutable commits.
The actions currently used are MIT licensed except
`mikepenz/action-junit-report`, which is Apache-2.0. Actions execute in GitHub's
runner environment and are not bundled into UnityMCPBridge release artifacts.

## Names and trademarks

Unity, Unity Technologies, CoplayDev, Anthropic, OpenAI, Claude, Codex, and
other product or organization names may be trademarks of their respective
owners. Their appearance identifies compatible products, upstream provenance,
or development tooling and does not imply affiliation, sponsorship, support,
or endorsement.

When adding or updating a vendored source, runtime dependency, or release
action, update the relevant lockfile and this notice in the same change.
