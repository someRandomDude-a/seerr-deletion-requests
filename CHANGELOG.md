# Changelog

## [Unreleased]

- Add optional per-proposal administrator approval (default off), with target/activity/policy snapshots and live role/visibility checks; viewer consent and deadline floors still apply.
- Remove the standalone queue route/redirect, old UI/theme assets, stylesheet scanner/service and obsolete standalone styling/tests. Retain the integrated backing API and authenticated historical records.

- Derive DLL, installed manifest, package and repository versions from the triggering Git tag without a source-version bump.
- Preserve earlier catalog entries and immutable assets while advertising newer compatible versions to existing Jellyfin installations.
- Verify tag/ref consistency and two-version upgrade history in release tests.

- Integrate original request-time Web injection, media/detail/context-menu actions and available Enhanced Requests removal actions; retire standalone identifier-entry UI.
- Add a top-bar notification center with unseen badges, automatic relevant notices/opt-out, explicit read acknowledgement and session clearing.
- Add user dropdown/reset and secure administrator import of Enhanced's Seerr settings.
- Connect one-shot Seerr-managed removal with bounded all-account/related-item activity scans and read-only uncertain-outcome reconciliation.
- Accept Seerr-controlled targeting/files and local authenticated seals as the configured trust boundary; no Seerr-side component or independent rollback witness.
- Require a new explicit real-deletion acknowledgement on upgrade; preserve older preview opt-ins without activating the new transport.

**Automatic deletion is available only after explicit acknowledgement, current consent, complete activity coverage and delay.** Seerr's reported result is trusted; a whole-installation consistent backup rollback is not detectable. Live Arr/media deletion is not certified by the no-media browser fixture.

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
