# Host isolation, hostile input and themed notifications

## Safety boundary

The plugin uses Jellyfin's library/user/session APIs for **reads and event subscriptions only**. It does not open Jellyfin databases, change library records, save native user settings or modify Web files. Queue writes are confined to `DataPath/deletion-queue`. A registered gateway can issue one stock-Seerr file-deletion request after explicit new opt-in, current consent, full activity coverage/scans and delay. Seerr controls Arr targeting/files and its reported outcome is trusted. Local seals are not an independent whole-backup rollback witness; that limitation is explicitly accepted.

This is an **in-process plugin, not a sandbox**. No honest review can guarantee that a plugin will *never* crash its host: runtime/native-library faults, out-of-memory termination, disk exhaustion and server defects remain possible. Use filesystem/container limits and tested stopped backups. These safeguards reduce risk and make ordinary exceptions disable the plugin rather than stop Jellyfin; they are not production deletion certification.

## Hardening

- Playback/user-data handlers copy bounded identifiers into a 256-event channel. They never resolve library targets or perform journal I/O on Jellyfin's event-dispatch thread. Overload, missing evidence or collector failure invalidates coverage; events are not silently discarded while claiming complete coverage.
- The hosted worker catches operational exceptions, detaches handlers, and stops its own collection. Storage failures latch the runtime unavailable until a reviewed restart; they do not regenerate keys, repair seals or reset state. Clock checks use monotonic scheduling so a backwards wall-clock jump cannot postpone the next check.
- Existing journals are opened read-only and authenticated before a writable connection is opened. Unexpected SQLite schema, linked state files, invalid encryption/chaining/seals and inconsistent partial restores are rejected. SQL uses parameters and trusted-schema execution is disabled. This is not protection from an attacker with full filesystem/process control, nor an independent whole-installation rollback witness.
- Limits include 32 KiB mutation bodies, JSON depth 16, case-insensitive duplicate-member rejection (including escaped names), unknown-member rejection, strict IDs/enums/ranges/string lengths, a five-second body-read budget, four concurrent plugin requests and per-account request budgets. Errors return stable codes rather than credentials, paths or upstream response bodies.
- Collection/state/journal/replay limits fail closed instead of growing indefinitely. The current preview limits proposals to 1,000, activity records/notices to 10,000, snapshots to 4 MiB, each journal file to 256 MiB and replay to two seconds. These are **preview capacity limits**, not an archival/compaction policy. State is retained rather than truncated; hitting a storage limit requires operator review. Admin monitoring returns bounded slices and reports their limits.
- All commands still require live native user/admin authorization, revision checks and unique command IDs. No request-provided user ID, API key or administrator ID can impersonate another account. Mutation Origin checks supplement header-token authentication.
- The Seerr client disables redirects/proxies, bounds headers/body/time and uses validated DNS results directly for connections. Only the fixed file endpoint permits deletion, with no application retry loop or history/Arr/file writes. Unspecified, multicast and link-local destinations are rejected; explicit LAN/loopback services remain supported. This is not a general firewall. Enhanced settings are read server-side from the installed enabled plugin; imports require an admin command and one unambiguous URL. Keys never pass through the browser.

## UI and notification bell

The admin workspace uses Jellyfin's native Web host and `emby-*` controls. Integrated dialogs/actions run within the existing Web theme/Branding CSS and use its control/list/dialog/icon classes. Scoped plugin CSS supplies layout; it does not save native preferences or Branding. Standalone UI resources, redirect, theme loader and lazy stylesheet-discovery code are removed. Arbitrary third-party CSS/DOM layouts cannot be guaranteed compatible.

The bell is in Jellyfin Web's global header, outside Enhanced's overflow tray. Original ASP.NET startup middleware rewrites only successful UTF-8 Web index responses, preserving existing scripts. It buffers at most 2 MiB before exact passthrough, restores request headers/streams, drops stale validators after rewriting and never writes Web files. CSP is not weakened. Unsupported/encoded responses pass through. Native TV/mobile clients not using Web are untouched. Only the backing API remains under `/DeletionQueue/...`; there is no standalone page.

Per-proposal administrator approval is optional and defaults off. When required, an explicit revision/command-protected admin POST binds approval to the proposal target, policy revision and activity revisions. Current enabled admin role and target visibility are checked again at dispatch. Approval never supplies viewer consent; stale approvals and reapprovals cannot advance deadline floors. Approver identities are absent from ordinary-user DTOs.

- Owners and required recent/playing viewers receive notices automatically unless opted out; explicit extra subscriptions remain optional. Resets restore notification defaults without deleting retained notice history.
- Proposal changes and coalesced notices commit together, including activity, policy and deadline changes. Notices contain generic text only; reads recheck the caller and target visibility.
- The bell shows an unread badge and an accessible keyboard-dismissible dialog. Opening it, polling while visible, reading Help, and refreshing are GET-only and never record activity, approval or read acknowledgements.
- **Mark read** is a separate replay-protected command bound to the caller's own visible notice. It does not approve deletion. Old authenticated notice records deserialize as unread without rewriting historical events.
- Notification capacity cannot block owner Undo or consent: it records incomplete delivery and tells the user to review the queue directly. It never deletes retained notice history to make space.
- Untrusted titles/notices/account names use text nodes. Existing Web credentials are reused only for this server, never collected by a new login form, and private DOM content is cleared on logout/account changes or authorization failure.

## Repeatable checks

`.github/workflows/jellyfin-compatibility.yml` resolves the latest stable **Server and Web** releases, records the image digest used for that run, and exercises an isolated no-network/no-media Jellyfin instance on pull requests, pushes, weekly schedules and manual invocation. Releases require that workflow as well as the cross-platform safety/package tests. An SDK/ABI upgrade remains a reviewed metadata change; CI does not silently change the release ABI.

The API smoke checks reject malformed/oversized/deep/ambiguous JSON and adversarial settings without journal mutations. Chromium checks native CSS/custom theme overrides, the bell, explicit read acknowledgement, text-only rendering and logout privacy. Notice responses are mocked only in the browser fixture because this no-media fixture has no real proposals; domain tests cover persisted notice rules separately.

`test-infrastructure/fault-check.mjs` verifies the exact disposable Compose project/network/mounts and server identity before corrupting **only its plugin head seal**. It verifies that Jellyfin health and authenticated user APIs survive both the worker's failed write and a restart, that only the plugin becomes unavailable, and that the corrupt seal is not silently repaired. Never run fault injection against production or reuse the disposable volumes.
