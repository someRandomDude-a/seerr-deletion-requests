# Changelog

## [0.1.0.0]

Initial **safety-preview** release for Jellyfin 12.2 / .NET 10.

- Native administrator workspace and private, same-origin deletion queue.
- Snapshot-bound consent, permission overrides, protected activity and non-shortening delays.
- Encrypted authenticated journal and tested one-shot execution/recovery domain.
- Architecture-specific packages and Jellyfin repository manifests with retained version history.
- Bounded background collection, strict hostile-input validation and storage/worker fault isolation.
- Installed Jellyfin theme/control CSS and a private notification bell with explicit subscriptions/read acknowledgements.
- Latest-stable Server/Web compatibility, browser and corrupt-storage survival checks, also gating releases.

**Automatic deletion is unavailable**, even after opt-in, until atomic Seerr target-pinning and independent rollback protection are integrated. This release must not be described as a production automatic-deletion system. See `docs/implementation-status.md`.
