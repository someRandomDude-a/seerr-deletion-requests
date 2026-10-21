# Changelog

## [Unreleased]

- Derive DLL, installed manifest, package and repository versions from the triggering Git tag without a source-version bump.
- Preserve earlier catalog entries and immutable assets while advertising newer compatible versions to existing Jellyfin installations.
- Verify tag/ref consistency and two-version upgrade history in release tests.

**Automatic deletion remains unavailable** in this safety preview; versioning and updates do not bypass execution safety gates.

## [0.1.0.0]

Initial **safety-preview** release for Jellyfin 12.2 / .NET 10.

- Native administrator workspace and private, same-origin deletion queue.
- Snapshot-bound consent, permission overrides, protected activity and non-shortening delays.
- Encrypted authenticated journal and tested one-shot execution/recovery domain.
- Architecture-specific packages and Jellyfin repository manifests with retained version history.
- Bounded background collection, strict hostile-input validation and storage/worker fault isolation.
- Pin the SQLitePCLRaw bundle to 2.1.14 to remove the vulnerable native SQLite dependency (CVE-2025-6965); NuGet audit remains enabled.
- Installed Jellyfin theme/control CSS and a private notification bell with explicit subscriptions/read acknowledgements.
- Latest-stable Server/Web compatibility, browser and corrupt-storage survival checks, also gating releases.

**Automatic deletion is unavailable**, even after opt-in, until atomic Seerr target-pinning and independent rollback protection are integrated. This release must not be described as a production automatic-deletion system. See `docs/implementation-status.md`.
