# Third-Party Notices for the Python Server

This server is part of UnityMCPBridge, a fork of
[`CoplayDev/unity-mcp`](https://github.com/CoplayDev/unity-mcp). The upstream
project and fork modifications are distributed under the MIT License in
[`LICENSE`](LICENSE).

Exact dependency versions are recorded in `uv.lock`. Direct runtime
dependencies and their declared licenses are:

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
Unlicense (`email-validator`). Dependencies are resolved separately and are not
vendored into the server source. The metadata installed with each dependency
is the authority for its exact license text.
