# Install UnityMCPBridge

UnityMCPBridge consists of a Unity package and a Python MCP server. Install both from the **same release tag**. A successful package import does not prove that the matching fork server is running.

## Prerequisites

- **Unity 6.0 or newer** — [Download Unity](https://unity.com/download)
- **Python 3.10+** with [`uv`](https://docs.astral.sh/uv/getting-started/installation/)
- **An MCP client** — Claude Desktop, Claude Code, Cursor, VS Code, Windsurf, Cline, Codex, or another MCP-capable client

> [!NOTE]
> **Unity compatibility:** The package minimum is Unity `6000.0`; pre-Unity-6
> editors are unsupported. The main Unity test project is authored with
> `6000.3.9f1`, while the primary CI compatibility gate uses `6000.0.75f1` and
> the extended matrix currently includes `6000.4.8f1`. Compatibility paths for
> later Unity 6 releases are maintained on a best-effort basis until those
> versions join the verified matrix.

## 1. Install the stable Unity package

In Unity, open **Window → Package Manager**, click **`+`**, choose **Add package from git URL...**, and paste:

```text
https://github.com/gitgomez/UnityMCPBridge.git?path=/MCPForUnity#v10.2.8
```

The tag makes the package install reproducible. If the repository is private, Git must already have credentials that allow Unity Package Manager to clone it. Never put a token in the URL or commit one to `Packages/manifest.json`.

## 2. Confirm the matching Python server

The package derives the matching server source from its stable version automatically:

```text
git+https://github.com/gitgomez/UnityMCPBridge.git@v10.2.8#subdirectory=Server
```

The tag must match the Unity package tag exactly. Leave **Server Source Override** empty for the release default. Use the override only for a local checkout or another intentional custom source. The public `mcpforunityserver` PyPI package and the upstream Docker image are upstream distributions; they do not contain UnityMCPBridge-only behavior.

For a local checkout, set **Server Source Override** to the absolute path to its `Server` directory. Enable **Dev Mode** only while deliberately iterating on local or moving-branch sources.

## 3. Configure and connect

After import, the setup wizard opens automatically.

1. Confirm Python and `uv` are available.
2. Select the MCP clients to configure.
3. Click **Configure Selected**.
4. Start the bridge in HTTP mode for shared multi-client use, or use stdio for a deliberately isolated single-client session.
5. Confirm the status panel reads `Connected`.

You can return to the window via **Window → MCP for Unity**.

### Manual client configuration

For a Unity-managed local HTTP server:

```json
{
  "mcpServers": {
    "unityMCP": {
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

For a direct stdio launch from the stable source tag:

```json
{
  "mcpServers": {
    "unityMCP": {
      "command": "uvx",
      "args": [
        "--from",
        "git+https://github.com/gitgomez/UnityMCPBridge.git@v10.2.8#subdirectory=Server",
        "mcp-for-unity",
        "--transport",
        "stdio"
      ]
    }
  }
}
```

On Windows, use the full `uvx.exe` path if `uvx` is not on `PATH`.

## Development snapshots

Use the moving development branch only when you intentionally want unreleased changes:

```text
https://github.com/gitgomez/UnityMCPBridge.git?path=/MCPForUnity#optimize/bridge
git+https://github.com/gitgomez/UnityMCPBridge.git@optimize/bridge#subdirectory=Server
```

Always switch both sides together. `optimize/bridge` can change without notice and is not a reproducible installation channel.

## Verify the installed pair

1. Query live MCP capabilities.
2. Select the intended Unity instance.
3. Confirm `mcpforunity://editor/state` reports `ready_for_tools` before mutation.
4. If a fork-only command is absent, recheck **Server Source Override**, then restart or reconfigure the MCP client.

Try a first prompt:

> Create a cube at the origin and add a Rigidbody.

For fork-specific capabilities, safe receipt recovery, and contributor setup, continue with the [UnityMCPBridge fork guide](./bridge-fork.md).

## Upstream distributions

The Unity Asset Store package, OpenUPM package, public `mcpforunityserver` PyPI package, and upstream Docker image belong to [CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp). Use upstream documentation when you intentionally choose that distribution. Do not mix those server binaries with the UnityMCPBridge package.

## Troubleshooting

- **Package clone fails** — verify Git credentials and the exact URL; do not embed a token.
- **Server does not start** — verify `uv --version`; if an override is present, verify that source as well.
- **Client does not connect** — confirm the local HTTP server is running on `localhost:8080`, or inspect the stdio command.
- **Fork command is missing** — package and server are probably from different revisions.

See the [troubleshooting guide](../guides/troubleshooting.md). Issues may be reported at [GitHub](https://github.com/gitgomez/UnityMCPBridge/issues), but they do not create a support or response-time commitment.
