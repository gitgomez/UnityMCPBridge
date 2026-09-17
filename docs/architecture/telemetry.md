# UnityMCPBridge telemetry

UnityMCPBridge does **not** transmit telemetry by default. The fork has no
built-in analytics destination, and it does not send data to the upstream
project's telemetry service.

## Default behavior

- Python server telemetry is disabled.
- No telemetry endpoint is configured.
- Unity-side event collection is disabled unless explicitly enabled.
- Existing disable environment variables remain authoritative.
- Telemetry failures never affect normal Bridge operation.

The Python telemetry configuration may prepare its local application-data
directory when the server starts. No network request is made unless an operator
explicitly supplies a valid endpoint.

## Explicit opt-in

An operator can opt in for a server process by setting an endpoint they control:

```bash
export UNITY_MCP_TELEMETRY_ENDPOINT=https://telemetry.example.com/events
```

Supplying this variable is an explicit opt-in for that process. Invalid,
empty, loopback, or unsupported endpoint URLs disable transmission; they never
fall back to a third-party service.

Application integrators can alternatively set both `telemetry_enabled=True`
and a valid `telemetry_endpoint` in `ServerConfig`. The repository defaults set
these to `False` and `None`.

Any of these environment variables disables telemetry even when an endpoint was
configured:

```bash
export DISABLE_TELEMETRY=true
export UNITY_MCP_DISABLE_TELEMETRY=true
export MCP_DISABLE_TELEMETRY=true
```

## Data produced after opt-in

When explicitly enabled, the existing collector can produce:

- tool and resource names, durations, and success or failure state;
- connection and startup events;
- platform, Python version, package version, and session identifiers;
- first-use milestones and a randomly generated installation UUID.

Free-form error messages and caller-supplied metadata are not transmitted. The
collector is designed not to include source-code contents, project names,
filenames, or project paths. Operators of a custom endpoint are responsible for
their own privacy notice, retention policy, access control, and applicable legal
duties.

## Local files

Telemetry helper state uses the following application-data directory:

- Windows: `%APPDATA%\UnityMCP\`
- macOS: `~/Library/Application Support/UnityMCP/`
- Linux: `~/.local/share/UnityMCP/`

Depending on prior use and configuration, it may contain
`customer_uuid.txt` and `milestones.json`. Disabling telemetry stops future
collection and transmission but does not automatically delete existing local
files.

## Verification

The server regression suite verifies that the default configuration has no
endpoint and cannot open an HTTP telemetry client. Endpoint validation tests
also verify that invalid explicit endpoints remain disabled instead of falling
back to another service.

For privacy concerns, use the
[UnityMCPBridge issue tracker](https://github.com/gitgomez/UnityMCPBridge/issues)
without including confidential information. Security-sensitive findings belong
in the [private reporting channel](https://github.com/gitgomez/UnityMCPBridge/security/advisories/new).
