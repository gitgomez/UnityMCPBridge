# Documentation update checklist

Use this checklist after adding, removing, or changing an MCP tool or resource.
It is written so a maintainer or coding agent can follow the same ownership
rules without copying stale inventories between files.

## Establish the source

1. Inspect the relevant implementation under `Server/src/services/tools/` or
   `Server/src/services/resources/`.
2. Inspect the matching Unity handler or resource under `MCPForUnity/`.
3. Update `Contracts/tool-contracts.v1.json` when the public built-in contract
   changes.
4. Treat `unity-mcp-skill/SKILL.md` as the canonical agent router. Its generated
   contract index is output, not an independent source.

## Refresh owned outputs

- Keep `manifest.json` synchronized with the distributable tool inventory.
- Generate reference Markdown from the live Python registry; do not hand-edit
  generated pages outside their marked example blocks.
- Update hand-written guides only where setup, behavior, prerequisites,
  recovery, or known limitations actually changed.
- Update `README.md` only when the public project summary, installation path,
  compatibility statement, or highlighted fork capability changed.
- Maintain public documentation in English. A separate German translation is
  allowed when deliberately requested; do not add other language variants.

## Validate

Run from the repository root:

```bash
uv run --project Server --extra dev python tools/tool_contracts.py --check
uv run --project Server --extra dev python tools/skill_contracts.py --check
uv run --project Server --extra dev python tools/generate_docs_reference.py --check
```

Review the complete diff. A generated-documentation check does not replace a
focused runtime or Unity test when behavior changed.
