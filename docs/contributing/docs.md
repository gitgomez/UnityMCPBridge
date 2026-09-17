# Documentation workflow

UnityMCPBridge documentation is ordinary GitHub-flavored Markdown stored under
`docs/`. It is readable directly in the repository and has no separate website
runtime, Node.js dependency tree, analytics integration, or deployment step.

## Hand-written and generated documentation

| Section | Source | Maintenance |
|---|---|---|
| Getting started, guides, architecture, contributing, and migrations | `docs/<section>/*.md` | Hand-written |
| Tool reference | `docs/reference/tools/` | Generated from `Server/src/services/tools/` |
| Resource reference | `docs/reference/resources/` | Generated from `Server/src/services/resources/` |

Generated reference pages contain an examples block between
`<!-- examples:start -->` and `<!-- examples:end -->`. Content inside that block
is preserved when the reference is regenerated.

## Editing a hand-written page

1. Edit the relevant `.md` file under `docs/`.
2. Use relative links that include the `.md` extension.
3. Store repository-owned images under `docs/images/` and reference them with a
   relative path.
4. Review the rendered Markdown in GitHub or another CommonMark-compatible
   preview.

Each page starts with a normal level-one heading. Do not add site-generator
front matter, JavaScript components, analytics snippets, or hosted-site-only
links.

## Updating generated references

When the Python tool or resource registry changes, run:

```bash
cd Server
uv run python ../tools/generate_docs_reference.py
```

The optional repository hook installed by `tools/install-hooks.sh` performs the
same regeneration for staged registry changes. CI runs
`.github/workflows/docs-generate.yml` to verify that the committed Markdown
matches the live registry.

To add an example to a generated tool page, edit only the text between the
example markers and then regenerate once to verify that it is preserved.

## Release notes

UnityMCPBridge release history belongs in the repository's GitHub Releases.
The README links directly to the upstream CoplayDev release history instead of
copying it into this repository. Do not maintain a second hand-written or
generated release-history mirror under `docs/`.
