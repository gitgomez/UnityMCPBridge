# Releasing UnityMCPBridge

UnityMCPBridge uses a two-branch, source-only release model:

- `optimize/bridge` is the development and integration branch.
- `main` is the stable public branch and must match the latest release tag.

There is no fork `beta` branch. The upstream project's `beta` branch is reference material only.

## Distribution boundary

Fork releases publish Git source and a GitHub Release record only. They do **not** publish:

- the `mcpforunityserver` name on PyPI;
- a Docker image;
- an MCPB bundle;
- an Asset Store or OpenUPM package.

Those public distribution names currently refer to upstream artifacts. The supported fork installation uses one immutable Git tag for both the Unity package and Python server.

## Unity CI boundary

The repository does not store Unity account or license credentials in GitHub.
The optional hosted-runner workflow therefore records an explicit skip and
continues. This is not a request for contributors or maintainers to upload a
personal Unity account, and it does not block source publication, repository
visibility changes, or installation from a Git tag.

Local Unity verification is separate and can use an Editor that is already
activated normally through Unity Hub. Changing this credential-free CI policy
requires a separate, explicit maintainer decision.

## Release authority

Only Gomez (`gitgomez`) may create UnityMCPBridge version tags and publish
GitHub Releases. The release workflow verifies the triggering actor in addition
to validating the tag, version metadata, and `main` commit. External
contributors target `optimize/bridge`; accepting a contribution does not grant
release authority.

## Release checklist

### 1. Prepare the release on `optimize/bridge`

1. Start from a clean, current checkout.
2. Select the stable `MAJOR.MINOR.PATCH` version.
3. Synchronize every release-owned version and install URL:

   ```bash
   python tools/update_versions.py --version 10.2.7
   python tools/update_versions.py --check --version 10.2.7
   ```

4. Review the complete diff and run the smallest applicable local checks, including Python tests, release-tool tests, documentation build, and Unity tests when Unity code changed.
5. For maintainer-authored release commits, verify that Git uses the GitHub
   noreply identity rather than a private address.
6. Commit and push the verified `optimize/bridge` state.

### 2. Promote the verified commit to `main`

Create a pull request from `optimize/bridge` to `main`. Do not add version changes during promotion. Use the rebase merge strategy rather than a GitHub-generated merge or squash commit; this preserves the reviewed commit authors and avoids adding a separate maintainer identity to the release history. The tree that reaches `main` must be the same tested bridge revision on both sides of the package/server boundary.

After merge, verify:

```bash
git fetch origin main
git diff --exit-code origin/optimize/bridge origin/main
git log --format='%h %an <%ae>' <previous-release>..origin/main
```

Confirm that maintainer-authored commits use the approved GitHub noreply
address. If development has already continued on `optimize/bridge`, compare
`main` with the exact release commit instead of the branch tip.

### 3. Create and push the annotated tag

Tag the promoted `main` commit, not a later development commit:

```bash
git tag -a v10.2.7 <release-commit> -m "UnityMCPBridge v10.2.7"
git show --no-patch --decorate v10.2.7
git push origin v10.2.7
```

Pushing the tag starts `.github/workflows/release.yml`. The workflow rejects:

- tags that are not exactly `vMAJOR.MINOR.PATCH`;
- tagged commits that are not exactly the current `origin/main` tip;
- mismatched package, server, manifest, lockfile, documentation, or install-URL versions.

It then runs the Python test gates and the optional hosted-runner Unity
verification. Under the credential-free CI policy, that verification is
reported as an explicit non-blocking skip. The workflow creates a GitHub source
release and has no PyPI, Docker, or MCPB publishing job.

### 4. Verify the public installation

Confirm the tag and GitHub Release exist, then verify both immutable sources resolve:

```text
https://github.com/gitgomez/UnityMCPBridge.git?path=/MCPForUnity#v10.2.7
git+https://github.com/gitgomez/UnityMCPBridge.git@v10.2.7#subdirectory=Server
```

Import the Unity package through Package Manager and verify that the default server source resolves to the matching tag without an override. Live capability discovery and a representative connected-Editor path are the final runtime proof; a successful clone or workflow alone is not.

## Development snapshots

The moving `optimize/bridge` branch may be used by contributors who explicitly want unreleased work. Both package and server must use that branch together. It is not the stable install channel and must not be presented as reproducible.

## Failure recovery

### The tag is wrong but has not been pushed

Delete it locally and recreate it on the intended commit:

```bash
git tag -d v10.2.7
```

### The tag was pushed or a GitHub Release exists

Do not silently move the public tag. Mark the release as withdrawn, correct the source on `optimize/bridge`, promote a new verified commit, and publish a new patch version.

### The workflow fails before creating the release

Keep the tag in place while diagnosing the failed gate. If the source itself is wrong, treat it as a published-tag error and use a new patch version. Do not activate the old upstream publishing workflow as a workaround.
