# Implementation status — 0.1.0 safety preview

This is an installable .NET 10 Jellyfin Server plugin, **not a completed production deletion system**. Missing safety contracts are visible and block eligibility/execution; they are not bypassed by administrator authority or automatic-deletion opt-in.

## Implemented

| Area | Implementation |
| --- | --- |
| Domain rules | Authority inheritance/overrides; verified target input; canonical deduplication; owner-only pre-claim Undo; snapshot-bound consent; activity aging; active-session protection; fresh eligibility epochs; accumulated personal/default deadline floors. |
| Persistence | Plugin-owned SQLite schema 1; append-only AES-256-GCM encrypted authenticated snapshots with sequence/context/hash chaining; separate HMAC-protected local head seal; command-replay rejection; retained event history; exclusive writer lock and transactional commands. |
| Corruption | Missing keys/database, linked state, unexpected schema, invalid tags/chains/head seals and unknown versions fail closed. Read-only verification before writes; no key regeneration, reset, truncation or silent repair. A crash between DB commit and seal replacement freezes the store. |
| Jellyfin | Native plugin/service registration; native administrator page; bounded off-event-thread playback/interaction collection; contained worker/storage exceptions; continuous-observation warm-up after each restart; monotonic clock-discontinuity checks. |
| Seerr reads | Administrator-configured read-only adapter; bounded JSON; no redirects; live configured server association check; stable user-ID ownership checks for movies; standard/4K scope selection bound to Seerr's corresponding accessible Jellyfin media ID. A provider/title match alone cannot authorize another hidden edition. |
| API | Live current-account/admin checks; user-session-only access; API-key rejection; separate private/admin DTOs; explicit preference/subscription mutations; revision/command IDs; strict/duplicate-aware bounded JSON, concurrency/request budgets, same-origin mutation checks and no-store responses. |
| UI | Jellyfin-styled administrator/private user pages; installed Web theme/Branding CSS; Yes/No/Help/Undo, preview, preferences/subscriptions and private notification bell with explicit Mark read. No Web-file modifications or command replacement. |
| Execution domain | Tested durable claim → one dispatch → FilesDeleted → completion protocol; no resend after uncertainty, even after restart; explicit read-only reconciliation; Arr-removal cleanup recorded as not applicable. Only simulated gateways are used in tests. |
| Release infrastructure | Single-source identity/version/ABI metadata; numeric tag checks; Windows/Linux/ARM64 CI matrix; architecture-specific stable/preview catalogs; MD5/SHA-256 sidecars and provenance workflow; immutable assets and retained catalog history. See `docs/releases.md`; no release is published until a maintainer pushes its tag. |

## Explicitly unavailable / incomplete

1. **Production deletion transport:** the plugin never calls DELETE on Seerr, Arr or media files. The execution coordinator is not registered with a production gateway or scheduler. This is intentional until a Seerr-side contract can atomically reject changes to pinned service/item/edition/file-set identities.
2. **Independent rollback witness:** local seals reject tampering, internal omissions, journal suffix omission and mismatched partial restores. They cannot reject replay of an entire consistent old installation, including its keys. An independent monotonic witness is required before production authorization/execution certification. The UI reports this limitation; the local seal is not presented as that witness.
3. **Complete target inventories/history:** stock Seerr reads are sufficient to propose a managed title but not to pin its files. Target `FilesPinned` stays false. A complete, verified historical activity/related-item scan is not asserted by the production evaluator; new collection alone is not called historical proof.
4. **Whole-series Own requests:** partial requests cannot authorize whole-series deletion. Until complete Arr scope/ownership is verifiable, series proposals require Any accessible item; neither permission enables execution in this release.
5. **Universal native user navigation:** the native dashboard page is supported. Ordinary users use `/DeletionQueue/ui`; there is no client-menu/item-action injection and no TV/Roku/Swiftfin UI integration. The user page currently reads the existing Jellyfin Web `jellyfin_credentials` session format, subject to compatibility tests on each supported client.
6. **Notifications:** private in-app bell, coalesced notices, unread badge and explicit opt-in/subscriptions/Mark read exist on the private queue page. No global native-client bell, external push/toast delivery or personalized templates. No subscription is created implicitly.
7. **Migrations/key rotation:** current schema reopens without altering valid keys/seals/settings. Unknown versions are rejected; no legacy import, forward migration or key-rotation tooling is shipped yet. Historical data is never fabricated or resealed.
8. **Performance/operations:** authenticated snapshots are replay-validated on every access, including heartbeats. Explicit capacity/replay limits stop the plugin rather than exhaust host resources; no automatic compaction or archival migration exists. This is not production-scale storage optimization. Native playback progress is coalesced while active-session protection remains in place. Stale active-session reconciliation and clock-health recovery need verified explicit workflows before production eligibility is enabled.

## Test boundaries

The automated domain/storage/recovery suite runs without a live media server and inside a .NET 10 Docker container. Fault-injection tests use simulated pinned gateways: they do not certify stock Seerr removal semantics or real Sonarr/Radarr file deletion.

Validation on 2026-10-21: **72 tests passed on Windows and in a Linux .NET 10 Docker container**. This includes notice privacy/read ownership, legacy notice compatibility, capacity safety, service-address filtering, wrong-database/trigger rejection and bounded native stylesheet discovery. The plugin published successfully for Linux x64 with no build warnings; all three runtime packages and offline release/catalog recovery tests passed.

`test-infrastructure/compose.yaml` supplies a real isolated Jellyfin 12.2 instance, with no media or production credentials. Its smoke driver passed plugin loading, native page registration/resources, live user/admin authorization, API-key rejection, read purity, preferences/replay/origin checks and default-off/unavailable execution. Browser checks also passed native dashboard rendering and the existing Web-session bridge on the stock Jellyfin 12.2 Web client. Repeat those checks on other deployed clients before calling them supported.

The hardening smoke suite additionally rejects hostile/null/unknown/duplicate/oversized/deep JSON, header injection, invalid service URLs and denied/hidden target previews without unauthorized journal writes. Chromium verifies installed lazy control CSS, real light/dark theme stylesheets, custom-CSS opt-out, a populated private bell, explicit Mark read, mobile layout/Escape dismissal and logout clearing. See [the safety review](safety-review.md) and [repeatable fixture checks](../test-infrastructure/README.md) for exact boundaries; the bell fixture mocks notice transport rather than claiming real-media notification coverage.

The real native Web login/admin test also passed account-override saves and opt-in confirmation/cancellation. Corrupt-head-seal fault injection passed: the worker stopped safely, Jellyfin health and authenticated user APIs remained available before and after restart, and only the plugin was disabled with no silent repair. The disposable fixture and loopback proxy were removed/stopped afterward. This is fault-isolation evidence, **not a guarantee against every possible in-process/native host failure**.

Remaining integration and release gates in the implementation plan still apply. Do not interpret a green unit-test run as independent security review, full restore certification or permission to turn on an unsafe transport.
