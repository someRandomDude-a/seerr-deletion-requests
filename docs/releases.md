# Releases and Jellyfin plugin repositories

## Install through Jellyfin

After the first successful tag workflow, add **one** URL under **Dashboard → Plugins → Repositories**, then install **Deletion queue** from the catalog and restart Jellyfin.

| Server platform | Stable repository | Preview repository |
| --- | --- | --- |
| Linux x64 / standard AMD64 Jellyfin Docker | [stable Linux x64](https://raw.githubusercontent.com/someRandomDude-a/seerr-deletion-requests/manifests/stable/manifest-linux-x64.json) | [preview Linux x64](https://raw.githubusercontent.com/someRandomDude-a/seerr-deletion-requests/manifests/preview/manifest-linux-x64.json) |
| Linux ARM64 / standard ARM64 Jellyfin Docker | [stable Linux ARM64](https://raw.githubusercontent.com/someRandomDude-a/seerr-deletion-requests/manifests/stable/manifest-linux-arm64.json) | [preview Linux ARM64](https://raw.githubusercontent.com/someRandomDude-a/seerr-deletion-requests/manifests/preview/manifest-linux-arm64.json) |
| Windows x64 | [stable Windows x64](https://raw.githubusercontent.com/someRandomDude-a/seerr-deletion-requests/manifests/stable/manifest-win-x64.json) | [preview Windows x64](https://raw.githubusercontent.com/someRandomDude-a/seerr-deletion-requests/manifests/preview/manifest-win-x64.json) |

Copy the raw JSON link, not this documentation page. No repository URL is live until the workflow publishes the `manifests` branch. The current version is **preview**, so stable catalogs intentionally have no current release. Preview catalogs include stable releases as well.

Jellyfin 12.2's `VersionInfo` supports neither architecture selection nor prerelease labels. Do not register multiple runtime feeds, both channels, or an invented `platform` field: Jellyfin may otherwise select an incompatible native SQLite library. ZIPs are architecture-specific and contain the appropriate native SQLite binary. Linux musl/Alpine, macOS, 32-bit servers and other runtimes are **not supported** by these catalogs. ARM64 and Windows have native CI coverage; real-server/client certification remains subject to the implementation-status document.

`targetAbi` is Jellyfin's minimum compatibility filter, not a maximum supported version or a guarantee for every later 12.x/13.x release. Builds currently target Server/Web **12.2** and .NET 10. The plugin GUID and display name remain fixed so existing installations upgrade rather than creating a second plugin. Packaging and publishing never touch plugin settings, keys, database or valid seals.

## Single source of version and identity

Edit `build/Plugin.props`:

- `PluginVersion`: **four numeric components**, each 0–65534, for example `0.1.0.0`.
- `PluginReleaseChannel`: `preview` or `stable`.
- `PluginTargetAbi` and `PluginSdkVersion`: matching minimum ABI and Jellyfin SDK.
- Presentation fields: description/overview/owner/category.

Do not change `PluginGuid` or `PluginDisplayName`. The build generates assembly GUID/title/description attributes and all plugin-owned assembly versions from this file. Both installed `meta.json` and repository manifests use the same metadata.

Tags must exactly match **`v<PluginVersion>`**, for example `v0.1.0.0`. Do not use `-rc`/`-preview` SemVer suffixes: Jellyfin parses a numeric `System.Version`. Instead use a strictly increasing numeric version and the separate channel. A stable promotion must use a **higher numeric version** than its preview; never retag or republish the same version into another channel.

Releasing or selecting `stable` does **not** enable automatic deletion or waive safety requirements. This project's present safety-preview limits must remain prominent in release notes until those capabilities actually exist and are verified.

## Maintainer setup

1. Push these workflows to the GitHub repository. No release is published just by merging them.
2. Ensure GitHub Actions is enabled. The publish job requests `contents: write`, `id-token: write` and `attestations: write`; no PAT or custom long-lived secret is needed. Artifact attestations require GitHub's supported repository/plan entitlement (public repositories are supported).
3. Permit the repository's Actions token to create/update the generated **`manifests`** branch. Protect it from manual deletion/force-push while allowing this workflow's fast-forward updates; do not put code or unrelated work on it.
4. Protect release tags (`v*`) with repository rulesets so only authorized maintainers can create them, and prevent updates/deletion of published tags. Workflow checks also verify the local, event and remote tag commits agree.
5. If desired, protect release publication with a GitHub environment/required reviewers on the `publish` job. Do not grant write permissions to pull-request tests.
6. For a fork, update the repository links in `README.md`, `manifests/README.md` and this document. The workflow itself derives download URLs from `github.repository`.

GitHub Pages is not required. The catalog is served from raw HTTPS URLs on the generated `manifests` branch.

These Jellyfin installer URLs require a **public** repository and public release assets. Private-repository release builds can authenticate through Actions, but Jellyfin cannot use unauthenticated raw/private GitHub URLs; private distribution would require a separately designed authenticated/public mirror.

## Publish a version

1. Bump `build/Plugin.props` and add a matching `## [<PluginVersion>]` section to `CHANGELOG.md`.
2. Review compatibility, upgrade preservation, release notes and safety status. Merge the changes and let CI pass.
3. Create and push the matching tag, **one release at a time**:

   ```shell
   git tag -a v0.1.0.0 -m "Deletion queue 0.1.0.0 safety preview"
   git push origin v0.1.0.0
   ```

4. The tag workflow runs the reusable Windows x64/Linux x64/Linux ARM64 test matrix, builds all three packages, validates archive contents/assembly versions/native architectures/checksums, and creates GitHub build provenance.
5. Publication creates a **draft** release, uploads and verifies all assets, publishes it (GitHub prerelease for preview), then fast-forwards the catalogs. No version is indexed while its assets are missing or private.

Release assets include three ZIPs, MD5/SHA-256 sidecars, package descriptors and per-runtime single-version manifest snapshots. The mutable `manifests` branch retains the complete version history, sorted by numeric version descending. Stable versions enter both channel feeds; previews never enter stable.

Jellyfin checks **MD5 of the exact ZIP bytes**; that field is not SHA-256. SHA-256 sidecars and GitHub attestations provide additional verification, but neither makes MD5 cryptographically secure or replaces repository trust. Verify provenance with GitHub CLI when needed:

```shell
gh attestation verify DeletionQueue-0.1.0.0-linux-x64.zip --repo someRandomDude-a/seerr-deletion-requests
```

## Build and validate locally

PowerShell 7 is used on Actions; packaging/semantic checks also work with Windows PowerShell 5.1. A .NET 10 SDK is required.

```powershell
./scripts/package.ps1 -Runtime linux-x64 -OutputDirectory artifacts/my-release
./scripts/test-release.ps1 -Runtime all
```

Use a **fresh output directory** for every package build. Existing files are never overwritten, and reused staging directories cannot contribute stale binaries, plugin settings or keys. Packaging requires every declared dependency, checks ELF/PE native architecture and ensures plugin-owned assembly versions match the metadata.

`test-release.ps1` exercises all runtime packages, malformed tags/versions, checksum tampering, forbidden ZIP entries, channel isolation, history retention, immutable-version conflicts and idempotent retries. On PowerShell 7 it also checks the generated catalogs against `manifests/repository.schema.json`. Its offline publisher contract tests shadow both `git` and `gh` and exercise upload failures, post-publication catalog failures, retries and immutable-asset conflicts without making any GitHub request. Outside that fixture, `publish-release.ps1` refuses to run outside the authenticated GitHub Actions context.

## Failed runs and corrections

- **Before publication:** drafts and partial uploads remain unindexed. Re-run failed jobs in the same workflow run to reuse the original artifacts; identical existing assets are verified rather than overwritten. Do not use `--clobber`.
- **After release publication, before catalog update:** the previous catalog remains valid. Re-run the failed publish job with its original artifacts. The script compares uploaded bytes, retains existing history and retries a non-forced branch update.
- **Concurrent ref update:** the GitHub API refuses a non-fast-forward update. Retry using the latest catalog head; never force-push a stale manifest. The publish jobs are serialized, but multiple pending jobs can still be superseded by GitHub concurrency scheduling; avoid batch-pushing release tags and rerun a skipped publication explicitly.
- **Mismatched existing asset/version:** publication fails. Never replace published ZIPs, checksums, tag commits or numeric version identities. Correct the source and release a higher version. Switching channels also requires a new version.
- **Artifact retention expired:** do not replace an existing published release using an arbitrary rebuild. Prefer a new version; recovery must preserve the exact original bytes and original source/tag identity.
- **Withdraw a release:** explicitly remove its entries from both channel catalogs, with a reviewed `manifests` branch commit, before deleting/unpublishing assets. Keep an auditable withdrawal record; reusing that version is prohibited. No automated withdrawal/reset is performed by monitoring or this plugin.

This workflow has not published anything until a maintainer pushes a release tag. Local validation is not a claim that GitHub credentials, rulesets, ARM runners or hosted attestations have already been exercised.
