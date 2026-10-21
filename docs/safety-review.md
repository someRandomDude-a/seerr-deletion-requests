# Host isolation, hostile input and themed notifications

## Safety boundary

The plugin uses Jellyfin's library/user/session APIs for **reads and event subscriptions only**. It does not open Jellyfin databases, change library records, save native user settings, modify Web files, or register a destructive gateway. All queue writes are confined to `DataPath/deletion-queue`. Even administrator automatic-deletion opt-in cannot enable a missing transport.

This is an **in-process plugin, not a sandbox**. No honest review can guarantee that a plugin will *never* crash its host: runtime/native-library faults, out-of-memory termination, disk exhaustion and server defects remain possible. Use filesystem/container limits and tested stopped backups. These safeguards reduce risk and make ordinary exceptions disable the plugin rather than stop Jellyfin; they are not production deletion certification.

## Hardening

- Playback/user-data handlers copy bounded identifiers into a 256-event channel. They never resolve library targets or perform journal I/O on Jellyfin's event-dispatch thread. Overload, missing evidence or collector failure invalidates coverage; events are not silently discarded while claiming complete coverage.
- The hosted worker catches operational exceptions, detaches handlers, and stops its own collection. Storage failures latch the runtime unavailable until a reviewed restart; they do not regenerate keys, repair seals or reset state. Clock checks use monotonic scheduling so a backwards wall-clock jump cannot postpone the next check.
- Existing journals are opened read-only and authenticated before a writable connection is opened. Unexpected SQLite schema, linked state files, invalid encryption/chaining/seals and inconsistent partial restores are rejected. SQL uses parameters and trusted-schema execution is disabled. This is not protection from an attacker with full filesystem/process control, nor an independent whole-installation rollback witness.
- Limits include 32 KiB mutation bodies, JSON depth 16, case-insensitive duplicate-member rejection (including escaped names), unknown-member rejection, strict IDs/enums/ranges/string lengths, a five-second body-read budget, four concurrent plugin requests and per-account request budgets. Errors return stable codes rather than credentials, paths or upstream response bodies.
- Collection/state/journal/replay limits fail closed instead of growing indefinitely. The current preview limits proposals to 1,000, activity records/notices to 10,000, snapshots to 4 MiB, each journal file to 256 MiB and replay to two seconds. These are **preview capacity limits**, not an archival/compaction policy. State is retained rather than truncated; hitting a storage limit requires operator review. Admin monitoring returns bounded slices and reports their limits.
- All commands still require live native user/admin authorization, revision checks and unique command IDs. No request-provided user ID, API key or administrator ID can impersonate another account. Mutation Origin checks supplement header-token authentication.
- The read-only Seerr client disables redirects/proxies, bounds headers/body/time and uses validated DNS results directly for connection attempts. Unspecified, multicast and link-local destinations (including the common link-local metadata endpoint) are rejected. Explicit LAN/loopback services remain supported; this is not a general network firewall or an outbound allowlist. Treat configured service credentials as credentials for that service only.

## UI and notification bell

The admin workspace uses Jellyfin's native Web page host and `emby-*` controls. The private user page imports the installed same-origin Jellyfin Web stylesheets without executing its bootstrap scripts, applies the existing user's theme/custom-CSS settings and server Branding CSS, and uses the same control/list/dialog/icon classes. A cached, bounded **read-only** scan of the configured Web directory discovers lazy control stylesheet filenames (no file paths or executable bundles are exposed); an incompatible Web build disables the UI rather than introducing a custom replacement theme. `queue.css` contains only scoped layout rules, not a replacement palette or control theme. Native preferences and Branding are never saved by the plugin. Arbitrary third-party CSS selectors cannot be guaranteed compatible with a different page layout.

The bell is in **`/DeletionQueue/ui`**, not Jellyfin's global header or native TV/mobile apps. A server plugin cannot install universal client UI without client support; no Web-file patch or persistent injection is used.

- Notices require both explicit notification opt-in and an explicit proposal subscription. There is no automatic subscription.
- Proposal changes and coalesced notices commit together, including activity, policy and deadline changes. Notices contain generic text only; reads recheck the caller and target visibility.
- The bell shows an unread badge and an accessible keyboard-dismissible dialog. Opening it, polling while visible, reading Help, and refreshing are GET-only and never record activity, approval or read acknowledgements.
- **Mark read** is a separate replay-protected command bound to the caller's own visible notice. It does not approve deletion. Old authenticated notice records deserialize as unread without rewriting historical events.
- Notification capacity cannot block owner Undo or consent: it records incomplete delivery and tells the user to review the queue directly. It never deletes retained notice history to make space.
- Untrusted titles/notices/account names use text nodes. Existing Web credentials are reused only for this server, never collected by a new login form, and private DOM content is cleared on logout/account changes or authorization failure.

## Repeatable checks

`.github/workflows/jellyfin-compatibility.yml` resolves the latest stable **Server and Web** releases, records the image digest used for that run, and exercises an isolated no-network/no-media Jellyfin instance on pull requests, pushes, weekly schedules and manual invocation. Releases require that workflow as well as the cross-platform safety/package tests. An SDK/ABI upgrade remains a reviewed metadata change; CI does not silently change the release ABI.

The API smoke checks reject malformed/oversized/deep/ambiguous JSON and adversarial settings without journal mutations. Chromium checks native CSS/custom theme overrides, the bell, explicit read acknowledgement, text-only rendering and logout privacy. Notice responses are mocked only in the browser fixture because this no-media fixture has no real proposals; domain tests cover persisted notice rules separately.

`test-infrastructure/fault-check.mjs` verifies the exact disposable Compose project/network/mounts and server identity before corrupting **only its plugin head seal**. It verifies that Jellyfin health and authenticated user APIs survive both the worker's failed write and a restart, that only the plugin becomes unavailable, and that the corrupt seal is not silently repaired. Never run fault injection against production or reuse the disposable volumes.
