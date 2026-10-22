# Jellyfin 12.x deletion queue: implementation plan

Historical design: see [current implementation status](implementation-status.md) for actual behavior. The user subsequently accepted stock Seerr as the authority for Arr targeting/files and accepted local authenticated journal/seals without an external rollback witness. Those decisions supersede the atomic file-pinning and independent-witness gates below. Integrated Web actions/notifications also supersede standalone identifier-entry UI and opt-in-plus-subscription notices. The remaining consent/activity/delay/one-shot requirements still apply. This document is not a claim that every original acceptance gate has been certified.

## 1. Objective and non-negotiable boundaries

Build a native Jellyfin Server plugin that lets users propose deletion, protects recent viewers through snapshot-bound consent, and delegates approved deletion exclusively to Seerr. Provide an anonymous user workspace and a separate elevated administrator workspace.

Defaults:

- Proposal authority: **Own requests**, with global and per-account **Denied / Own requests / Any accessible item** settings.
- Recent activity: **14 days**.
- Delay: **48 continuous hours after eligibility**, not after proposal creation.
- Automatic deletion: **off**, enabled only by an explicit, authenticated administrator action.
- Unknown permissions, identity, target, activity coverage or integrity: **block deletion**.

The plugin must never directly delete media files, Arr records, folders or Seerr request history; write Jellyfin databases; replace native commands; or persistently modify Jellyfin Web. Administrator authority does not waive viewer protection or target verification.

## 2. Feasibility findings and release gates

Source inspection on 2026-10-21 identified Jellyfin **12.2**, targeting **.NET 10**, and Seerr **3.5.0** as initial test baselines. Pin release tags, package versions and container digests during implementation; do not assume every 12.x release has the same API or UI hooks.

### Decision A (confirmed): remove from Radarr/Sonarr through Seerr

Seerr 3.5.0 exposes:

```http
DELETE /api/v1/media/{seerrMediaId}/file?is4k=false
```

Its server implementation calls Radarr's movie deletion or Sonarr's series deletion with `deleteFiles=true`. It preserves the Seerr media row and requests, updates availability, and clears service references. **It removes the Arr movie/series record; it is not simply an unmonitor operation.** This is the confirmed intended behavior: remove the item from Arr so it is no longer monitored there, delete its files, and retain Seerr requests.

Therefore:

1. Use Seerr's existing removal semantics; retaining the Arr movie/series record is **not** a requirement. This decision is no longer a blocker.
2. Do not add a separate unmonitor call for an Arr record Seerr has removed. Any future monitoring-only cleanup must still go through a supported Seerr operation.
3. Never call `DELETE /media/{id}` or `DELETE /request/{id}`. Do not reproduce Seerr's UI sequence without auditing it: UI behavior can include an additional media-data deletion.
4. Verify item-folder behavior in integration tests; no library root or unrelated content may be removed.

The plugin itself must have no direct Arr-delete or media-filesystem-delete capability. Remaining execution gates below still apply.

### Decision B (partly verified): native plugin pages, with separate user entry-point validation

**Native plugin pages are supported in Jellyfin 12.2.** Use them for the administrator workspace, not a standalone external dashboard. The verified mechanism is `IHasWebPages.GetPages()` returning embedded `PluginPageInfo` resources, with `DisplayName`, `EnableInMainMenu` and `MenuIcon`. Jellyfin Web's `ServerContentPage` loads these resources into its existing page/view manager, without modifying Web files.

The verified limitation is more specific: the stock `#/configurationpage?name=...` Web route is nested under `ConnectionRequired level='admin'`, and `GET /web/ConfigurationPages` requires elevation. Consequently, native **admin pages are confirmed**, but registering those pages does not by itself provide ordinary-user access or item actions. Serving an HTML resource directly is not proof that the stock user-facing router will host it.

See [the source-verification notes](jellyfin-12-native-pages.md) for exact release-tagged evidence and the remaining user-account smoke test.

Prototype the exact server/web release before committing to item-menu placement:

- Prefer a supported runtime extension point, preserving all native actions.
- Serve a plugin-owned, same-origin user workspace with Jellyfin authentication and theme conventions. A public static shell is acceptable only if it embeds no data, secrets or authorization decisions.
- If no supported user-menu hook exists, record that limitation. Any optional request-time loader/middleware must be a separately reviewed compatibility choice: plugin-owned code only, no on-disk Web changes, no command interception, reversible on uninstall.
- Do not promise UI inside Android TV, Roku, Swiftfin or other independently implemented native clients. Those require client support; the server plugin alone cannot add controls there.

Keep the native administrator page as the selected design. Only ordinary-user navigation/hosting remains an integration gate. If “native” requires new controls in every official client, client/upstream work is a release dependency. Do not silently replace that requirement with a Web-only injection.

### Gate C: verify the remote safety contract

Seerr's current removal endpoint resolves its target at execution time. A local file fingerprint cannot prevent a concurrent remap, reimport or new episode from expanding that remote deletion scope.

Require a verified contract that binds execution to the pinned service, item, edition and file-set revision, with a precondition rejection before any destructive work. If the deployed Seerr API lacks this contract, a Seerr-side extension is required for strict pinning. The extension must also distinguish a precondition rejection from a possibly executed operation; the plugin still never automatically resends deletion.

Do not claim that “GET, compare, DELETE” provides atomic target verification. Likewise, absent a Seerr API for narrowly scoped monitoring cleanup, keep that cleanup capability disabled rather than using direct Arr writes.

### Gate D: establish an independent rollback anchor

Authenticated local records detect modification and internal gaps, but cannot detect replacement of the **entire** database, keys and journal with a valid older copy. Rejecting replay/rollback requires an independently protected monotonic checkpoint or append-only witness outside the plugin backup set.

Select and test that mechanism before execution is enabled. If it is unavailable or disagrees with local state, fail closed. The security boundary excludes a hostile Jellyfin process or host administrator able to replace the plugin and all trust infrastructure; do not promise protection against that adversary.

## 3. Architecture and repository layout

Use C#, .NET 10, ASP.NET controllers and Jellyfin-supported service interfaces. Keep Jellyfin APIs and Seerr protocols behind adapters so domain rules can be tested without a running server.

```text
src/
  Jellyfin.Plugin.DeletionQueue/
    Plugin.cs, Configuration/, Api/, Services/, Web/
  DeletionQueue.Core/
    Authorization/, Targets/, Activity/, Consent/, Scheduling/, Execution/
  DeletionQueue.Infrastructure/
    Jellyfin/, Seerr/, Persistence/, Cryptography/, Notifications/
tests/
  DeletionQueue.UnitTests/
  DeletionQueue.IntegrationTests/
  DeletionQueue.SecurityTests/
  DeletionQueue.UiTests/
test-infrastructure/
  compose.yaml, fixtures/, fault-proxy/
docs/
  implementation-plan.md, threat-model.md, recovery.md, compatibility.md
```

Responsibilities:

| Component | Responsibility |
| --- | --- |
| Live authorization | Resolve the authenticated Jellyfin user, current account policy, access and proposal authority. |
| Target resolver | Verify ownership and canonical identity; enumerate exact destructive scope and related activity scope. |
| Activity collector | Record native playback and explicitly defined interactions; maintain coverage evidence and continuity. |
| Consent evaluator | Bind each caller's Yes/No to their current activity snapshot and proposal revision. |
| Scheduler | Evaluate eligibility, retain deadline floors, and request claims without bypasses. |
| Execution coordinator | Authenticate/persist a claim, send once through Seerr, and checkpoint outcomes. |
| Integrity store | Store domain state and encrypted authenticated events atomically; validate the independent witness. |
| Notification service | Deliver opted-in notices without changing activity, consent or subscription state. |
| User/admin API | Separate privacy projections and live elevated access checks. |

Use a plugin-owned SQLite database under the plugin data directory, not Jellyfin's database. Use transactional constraints and one logical execution writer. An in-memory lock is not a substitute for database uniqueness and compare-and-swap revisions.

## 4. Identity, permission and target rules

### Identity and authority

- Derive the caller from a validated Jellyfin user session; reject anonymous requests, service/API-key-only identities and caller-supplied user/admin IDs for user or admin actions.
- Check current account existence, disabled/restricted state and relevant Jellyfin policy on each command and immediately before execution.
- A per-account override replaces the global default; an absent override inherits it. Show the effective result explicitly in the admin workspace.
- **Denied** prevents proposing; it must not prevent a protected viewer from answering consent if the account can authenticate. Disabled viewers remain protected even when they cannot respond.
- **Own requests** requires a verified Seerr identity association and request ownership covering the entire destructive scope. Match stable Jellyfin user IDs within the configured server association, never names or email guesses.
- A partial-season request alone does not authorize whole-series deletion. Shared ownership must not become ownership of unrelated seasons/editions. Require Any accessible item if verified own-request coverage is insufficient.
- **Any accessible item** permits only movies or whole series that the caller can currently access. Episodes, seasons, arbitrary paths, unsupported media types and ambiguous targets are rejected.
- An accessible native item must still resolve to a verified, Seerr-managed destructive target. No request-free direct-filesystem fallback.

### Canonical target and deduplication

Resolve both request-based and native-item proposals into the same identity model:

```text
Seerr instance + media type + verified provider identity
  + Arr service identity + whole-item scope + edition/quality scope
```

Persist the Seerr media ID, verified request associations, provider IDs, Arr item/service identity, Jellyfin item aliases, exact file inventory and verification revision as private evidence. Client input selects an item; it never supplies trusted paths, ownership or mappings.

- Destructive scope is explicit. Related editions contribute protective activity without silently authorizing deletion of every edition.
- Prevent concurrent active proposals with intersecting destructive file/item scopes, including alternate identifiers and request/native entry paths. A unique canonical key plus transactional overlap checks enforce this.
- A duplicate explicit proposal action does **not** create a second proposal: atomically record that user's Yes against the existing proposal's current activity snapshot, if their proposal authority and access are valid.
- A stale revision/snapshot returns a conflict requiring fresh confirmation; never transfer approval to a changed target. Concurrent submissions use optimistic concurrency plus a single canonical owner.
- The original proposal owner is immutable. Joining does not transfer ownership or restart an already valid delay.
- Only that owner can **Undo**, only before a claim exists. Admin settings and monitoring do not provide an alternative Undo identity. Administrative suspension is a distinct, audited safety action, not owner cancellation.
- A new target, file-set or service mapping revision requires reverification and fresh consent/delay. Do not silently absorb newly imported episodes or editions into an existing authorization.
- Retain active uncertainty/cleanup locks; do not create a second proposal to work around an unfinished operation.

## 5. Activity and consent specification

### Activity sources and coverage

Subscribe through supported Jellyfin playback/user-data interfaces; verify event APIs against the pinned 12.x SDK. Record playback start, meaningful progress and stop, including native-client playback. Keep active sessions protective even if playback lasts beyond the activity window.

Define and test an allowlist of explicit, meaningful interactions, such as a user changing watched/favorite state. Distinguish user-originated changes from scans, aggregate updates, automated sync and the plugin's own writes. A consent answer is not viewing activity.

Do **not** infer activity from reading the queue, opening a notice, viewing Help, polling, rendering posters or administrator monitoring. Do not instrument generic GET requests as activity.

For each candidate, resolve all related editions, duplicate library representations and series children, including specials and episode-level activity. Include records for accounts that are disabled, deleted, restricted or no longer able to see the target. Identity tombstones preserve protection without exposing it publicly.

Coverage evidence must include source health, collection interval, continuity/gaps, pagination completion, target revision and all relevant identities/items. Aggregate last-played state alone must not be advertised as complete historical playback evidence.

- Backfill only from verified supported historical sources. Never query or modify Jellyfin's database directly.
- On first installation without complete historical evidence, collect an uninterrupted full activity window before eligibility. Unknown recent history blocks deletion during that warm-up.
- After a collection gap, recover verified coverage or wait until that entire gap is outside the configured window, with continuous collection since then. Increasing the window may reintroduce an evidence gap.
- A failed page, unavailable source, missing child/edition mapping, dropped-event backlog or unverified scan blocks eligibility. A successful scan with zero results is different from a failed scan.
- Startup subscriptions plus backfill must close the subscribe/scan race. Persist ingestion watermarks and detect queue overflow; do not assume missed events are impossible.

### Snapshot-bound answers

For each protected viewer, compute an authenticated snapshot revision from their relevant activity and current target scope. A Yes/No command includes the proposal revision and caller's snapshot revision. The server recomputes both before committing.

- Every recent viewer must have **Yes for their current snapshot**.
- Silence or No blocks while that viewer remains in the window. They need not be able to authenticate for their protection to remain in force.
- New activity invalidates that viewer's prior answer and breaks eligibility; other viewers' unchanged answers can remain, but the full delay must restart after eligibility returns.
- Yes does not erase activity; No is not an indefinite veto after activity has genuinely aged out of a completely verified window.
- An explicit proposal, including a duplicate proposal, records the proposer/joiner's own Yes only. It never approves for another viewer.
- A user can change their answer before claim, using a fresh revision. Once claimed, the UI must not promise cancellation or effective consent changes.
- With no protected viewers, eligibility still requires complete coverage, target verification and all other checks; a lack of known viewers is not proof of safety.

### Eligibility and non-shortening deadlines

```text
eligible = live owner authority and scope ownership/access are valid
           AND target mapping and file scope are verified
           AND complete activity coverage and no active playback
           AND every protected viewer has current Yes
           AND integrity and independent checkpoint are valid
           AND no administrative safety suspension

effectiveDelay = max(48h default or configured base,
                     owner/joiner delays and protected viewers' applicable delays)

candidateDeadline = eligibilityEpochStart + effectiveDelay
notBefore = max(previous persisted deadline floor, candidateDeadline)
```

Capture relevant personal delays as authenticated obligations for that proposal/eligibility epoch. Once an extension has applied, a later preference reduction, account restriction, participant aging out or configuration reduction cannot pull its deadline forward.

- Start the epoch only when **all** eligibility conditions become true, including when an unanswered viewer ages out.
- Any loss of eligibility clears the active epoch. Renewed eligibility starts a fresh full delay, retaining any later existing floor.
- Delay increases extend the deadline immediately; decreases only affect future proposals where no later floor exists. A window change that changes protection requires reevaluation; a shorter window cannot advance an existing deadline.
- A temporary scan outage or relevant permission/access loss breaks eligibility, not just the scheduler tick. Restoring it starts a fresh epoch.
- Persist UTC instants and clock-health evidence; test exact boundaries, daylight-saving changes and restarts. Backward jumps suspend scheduling; suspicious forward jumps must not make a two-day wait disappear.
- The scheduler never converts “deadline reached” directly into authorization: repeat all live checks immediately before claim and dispatch.
- Automatic-execution opt-in is a separate dispatch gate, not consent and not a command to delete immediately. Disabling it blocks unclaimed dispatch; it cannot recall a destructive request already sent.

## 6. State machine and one-shot execution

```text
Proposed/Blocked <-> EligibleWaiting -> Ready
      |                    |            |
      +---- owner Undo ----+------------+--> Cancelled
                                        |
                                  durable claim
                                        v
                                     Claimed
                                        |
                                 one Seerr request
                               /                  \
                   confirmed evidence          ambiguity/crash
                           |                         |
                     FilesDeleted                Uncertain
                           ^                         |
                           +-- explicit read-only reconciliation
                           |
                      CleanupPending -> Completed
```

`Ready` is a computed eligible/due state, not permission to skip preflight. Keep completed/cancelled proposals and all execution attempts permanently unless a separately designed retention policy preserves the required authenticated history.

### Dispatch protocol

1. Acquire the target execution lock and a fresh, complete authorization/activity/target snapshot. Check auto-execution opt-in and claim absence.
2. In one durable transaction, append an authenticated claim event and persist its projection. Bind claim ID, actor/service principal, owner's live authority, consent/coverage/config revisions, target fingerprint, exact allowed Seerr operation, deadline and execution generation.
3. Durably acknowledge the claim to the independent checkpoint mechanism before network dispatch. If this fails or is ambiguous, do not send. Recheck invalidation/version signals immediately before dispatch; abort safely if anything changed.
4. Send the permitted Seerr destructive request **once**, using the verified remote target-precondition contract. Disable HTTP retry policies, redirects and proxy retries for this operation; never log credentials.
5. Validate the response and obtain trustworthy evidence about the pinned files. A 2xx alone, a missing Arr row, an offline mount or Seerr's unavailable flag is not sufficient proof that those files were deleted.
6. Persist and authenticate a separate **FilesDeleted** checkpoint, including the pinned-inventory evidence, before any monitoring cleanup.
7. Perform only verified, narrowly scoped Seerr monitoring cleanup, if supported and still necessary. Persist each cleanup outcome; then complete.

No claim expiry or lease takeover authorizes another delete. A crash after claim but before the network call can strand an operation as uncertain; that conservative outcome is intentional. This is an at-most-once dispatch policy, not an exactly-once guarantee across independent services.

### Recovery and cleanup

- Any timeout, connection loss after claim, crash, unexpected response or persistence failure after dispatch means **Uncertain**. Do not automatically resend, including for an HTTP error that might have followed a successful remote delete.
- Monitoring reads only display stored evidence and checkpoints. An explicit authenticated admin reconciliation action may perform read-only inspection and append its findings; it never repeats deletion.
- Recovery may verify absence of the pinned files using trustworthy Seerr-side evidence or a narrowly scoped read-only verifier. No filesystem write capability is granted. If evidence is inaccessible or a path has changed/reappeared, remain uncertain.
- If absence is established, persist FilesDeleted, then allow cleanup. If files remain, display the evidence and keep the destructive claim consumed; do not relabel that as a retryable delete.
- A definitive remote precondition rejection may be recorded as not executed only under the tested API contract. Any later attempt requires new authorization and a new full proposal lifecycle, not reuse of the old claim.
- Cleanup retries require FilesDeleted, the same verified target and an allowlisted monitoring-only operation through Seerr. Do not overwrite quality profiles, root folders, tags, season lists or unrelated configuration via a read-modify-write payload.
- Under the confirmed removal behavior, Seerr removes the Arr record: monitoring cleanup is explicitly “not applicable,” not an attempt to recreate or modify that record. Still persist FilesDeleted as its own checkpoint before recording cleanup as not applicable and completing.
- Automatic cleanup retries, if enabled, are separate worker commands with fresh scope/integrity checks. GET endpoints never trigger them.

## 7. Persistence, authentication and migration

### Data model

Use additive versioned tables for:

- Proposals, canonical targets/aliases and active-scope locks.
- Verified ownership/mappings and pinned target inventories.
- Activity records, account tombstones, coverage intervals and ingestion watermarks.
- Consent snapshots, authenticated answers and invalidations.
- Policy/preference revisions, accumulated delay obligations and deadline floors.
- Execution claims, dispatch outcomes, FilesDeleted and monitoring-cleanup checkpoints.
- Notification preferences, explicit subscriptions and delivery outbox entries.
- Encrypted journal events, projection seals, migration manifests and witness receipts.

Retain history and stable identifiers. Raw target paths and Seerr request IDs stay private, encrypted where retained, and absent from ordinary responses/logs.

### Journal and tamper/omission/replay defenses

- Use an audited AEAD primitive such as AES-256-GCM with unique nonces and versioned keys. Bind stream identity, installation identity, sequence, event ID, schema version and previous-event digest as authenticated context.
- Every authorization, preference, consent, transition and execution checkpoint has an actor, command ID and revision. Reject duplicate command replay, invalid sequence/links, changed ciphertext/context and cross-installation records.
- Append domain events and update projections in one transaction. Validate projections against authenticated events/seals before using them for authorization; a mutable SQL row is not authority by itself.
- Detect deletion of middle events through sequence/hash continuity and deletion of a journal suffix through the independent committed high-water mark. A hash chain without that anchor is insufficient.
- Use a tested crash-consistent witness protocol. A recoverable journal/witness mismatch blocks all authorization until reconciled; never choose the lower sequence automatically.
- Separate encryption/authentication key purposes, protect secrets from ordinary accounts, support forward key rotation without invalidating existing valid seals, and fail closed on missing keys.
- Freeze authorization and execution on integrity failure. Preserve the corrupt bytes and expose a bounded integrity diagnostic; do not truncate, regenerate keys, reseal altered state or reset the database.

### Additive upgrades and honest legacy history

At planning time the repository contained only a README and license. The initial implementation now uses authenticated schema 1; there is no older released plugin schema to migrate. Preserve state and add upgrade fixtures as schemas evolve.

- Preserve existing configuration values, plugin identity, keys, database rows and valid seals. Add new settings with safe defaults; never turn execution on during an upgrade.
- Do not move security-sensitive settings outside the authenticated revision pipeline. All supported configuration save paths, including Jellyfin's normal plugin configuration route, must be validated or prevented from changing execution authority.
- Back up the stopped, consistent state before migrations; migrations are transactional where possible, versioned and restart-tested. Refuse a partial/unknown migration instead of creating a replacement store.
- Historical records predating the journal are explicitly labeled **legacy/unverified**. Preserve valid old seals under their original verification rules; do not manufacture retroactive authentication.
- Require fresh live evidence before legacy proposals become executable. Invalid authentication is not a historical gap that can be waived.
- Never modify Jellyfin databases or silently “repair” an installation during a read or upgrade.

### Backups and restore

Stop Jellyfin and workers, ensure no request is in flight, then back up the plugin database/WAL state, settings, key ring, journal/seals and a consistency manifest as one stopped set. Include required Jellyfin configuration through Jellyfin's supported backup procedure, not plugin SQL.

Restore only a complete consistent set. Reconcile it with the independent latest witness before allowing commands. Restoring older claims/authorization checkpoints or individual keys must be rejected; an older backup is not permission to execute again. If the independent journal cannot bring the restored state forward consistently, preserve it for investigation and remain blocked. In-flight uncertainty survives restore.

## 8. User privacy and interface

### Ordinary-user workspace

Use compact, accessible Jellyfin-themed controls:

```text
[Poster] Title                  Yours | Anonymous
Status: Waiting / Scheduled / Blocked / Uncertain / Completed
Allow deletion?   [ Yes ] [ No ] [ Help ]
Your action: Approve your latest activity / No action needed
Deletion: Not before <deadline>, subject to rechecks
[ Undo ]  -- original owner only, before claim
```

Each caller may receive only:

- Visible target display metadata and an opaque proposal identifier.
- **Yours / Anonymous** ownership label, lifecycle status and safe reason code.
- Caller-private required action, own consent/snapshot/activity information and applicable deadline.
- Server-computed capabilities such as `canUndo` and `canAnswer`, not identities of other participants.

Do not expose other viewers' names/IDs, votes, activity times, exact activity-derived unblock times, counts that identify participants, Seerr request IDs, target paths or raw diagnostic evidence. Restrict joins, errors, pagination, notifications, images, websocket messages and exports to the same privacy rules. Describe this as queue anonymity, not a promise that aggregate status can never permit inference.

Only currently accessible targets appear in ordinary listings; recheck access on item/detail endpoints. Use separate DTOs/serializers, private/no-store caching and opaque IDs. Never serialize an administrator/domain entity and remove a few fields afterward.

Help explains the 14-day window, current-snapshot consent, renewed full delay, owner-only Undo, and uncertainty. Reading it has no mutation side effects. Personalized notice templates are escaped presentation, never executable rules or authorization inputs.

### Preferences and notifications

- Notification opt-in, per-proposal subscription and personal-delay changes require explicit authenticated POST/PATCH commands and revision checks.
- Do not subscribe a user because they viewed a page, opened a notification or answered consent unless they separately chose subscription.
- Notification opening is read-only; Yes/No requires an authenticated explicit action with the current snapshot. No approving GET links or bearer approval URLs.
- Use generic notices where notification surfaces are shared; detailed content is caller-private and access-checked. Notifications never create playback activity.

### Administrator workspace

Provide a distinct native plugin dashboard for effective permissions, execution opt-in, backlog, coverage gaps, target verification, evidence, uncertainty, cleanup status and retained history.

- Require a live enabled Jellyfin user session and current administrator/elevated policy on every endpoint, export and long-lived stream reconnect/message. A service API key or supplied administrator ID is insufficient.
- Never trust an elevation claim cached before account demotion; re-resolve current user policy.
- Separate monitoring GETs from explicit policy, suspension, reconciliation and cleanup commands. Opening, refreshing or exporting the workspace does not approve, claim, execute, retry or repair.
- Audit every explicit administrative mutation. Show historic gaps and integrity failures visibly; do not offer a “trust/reset everything” button.
- Administrative execution controls cannot override consent, delays, visibility, ownership or uncertainty.

## 9. Proposed API boundary

All names below are plugin routes, not claims about existing Jellyfin or Seerr endpoints.

| Route | Behavior |
| --- | --- |
| `GET /DeletionQueue/proposals` | Caller-filtered anonymous queue; pure read. |
| `GET /DeletionQueue/proposals/{id}` | Caller-private status and own snapshot; pure read. |
| `POST /DeletionQueue/proposals` | Verify request/item target; create or atomically join existing proposal with explicit own Yes. |
| `POST /DeletionQueue/proposals/{id}/consent` | Yes/No for caller's current snapshot and proposal revision. |
| `POST /DeletionQueue/proposals/{id}/undo` | Owner-only compare-and-swap before claim. |
| `GET/PATCH /DeletionQueue/me/preferences` | Read or explicitly update own delays/notification choices. |
| `POST/DELETE /DeletionQueue/proposals/{id}/subscription` | Explicit own subscription changes. |
| `GET /DeletionQueue/admin/...` | Live-elevated monitoring, evidence and history only. |
| `POST/PATCH /DeletionQueue/admin/...` | Explicit policy/opt-in/suspension/reconciliation/monitoring-cleanup commands. |

Use authenticated session identity, strict input schemas, request size/rate limits, origin/CSRF protection appropriate to Jellyfin's authentication mode, expected revisions and idempotent non-destructive command IDs. Destructive transport must have no automatic retry middleware. Restrict backend URLs to administrator-configured services with TLS verification, redirect rejection and no client-controlled destination/path.

## 10. Docker-backed verification plan

Docker is available for tests, but this planning change does not start containers or touch running services. Implementation tests must use a separate Compose project, isolated networks, ephemeral volumes and synthetic media; never mount a real Jellyfin library or reuse production API keys.

Test stack:

- Pinned Jellyfin 12.x server/Web images with the compiled plugin.
- Pinned Seerr plus Sonarr/Radarr with tiny generated movie/episode fixtures and retained request fixtures.
- A contract-faithful Seerr simulator and fault proxy for deterministic races, timeouts and partial failures. Simulator success does not replace tests against real Seerr.
- The chosen independent checkpoint/witness test backend.
- Browser tests for the supported Web integration; native-client playback/API tests for server event collection.

Use a fake clock for exhaustive timing tests and real-clock restart tests for persistence/clock handling. Run builds and security/unit tests without Docker as well.

### Required acceptance matrix

| Area | Required checks |
| --- | --- |
| Permissions | Global/account inheritance; live disable/demotion/restriction; ownership mismatch; partial-request scope; inaccessible targets; API-key/admin-ID spoofing rejected. |
| Targets | Movie/whole-series only; standard/4K/related editions; aliases; multiple Arr services; same title with different provider IDs; shared/overlapping scope; root and unrelated files protected. |
| Duplicates | Concurrent request/native proposals yield one active row and separate own approvals; joining cannot steal owner or shorten deadline; stale consent rejected. |
| Activity | Native playback, active sessions, explicit interactions, episodes/specials/editions; disabled/deleted users retained; no queue/help/notification activity. |
| Coverage | Warm-up; missed events; restart gaps; partial pagination; unverified history; unavailable sources; changed window; empty success versus failed scan. |
| Consent/timing | Silence/No blocks; new activity invalidates; fresh 48h after renewed eligibility; two full days after last required Yes; personal extensions; no deadline advancement on any setting change. |
| Races | Undo versus claim; No/activity versus claim; owner access revoked; import/remap between preflight and dispatch; remote precondition refusal; two workers/processes. |
| Execution | Default-off; explicit opt-in; exactly one outbound destructive attempt per claim; only allowed Seerr endpoint; requests/history retained; accepted Arr semantics verified. |
| Recovery | Kill before/after every persistence/network boundary; timeout after remote success; 4xx/5xx ambiguity; offline mount; files reappear; missing Arr record; never resend delete. |
| Cleanup | FilesDeleted required first; only narrow monitoring changes; repeated cleanup never deletes or changes unrelated Arr fields; no unsupported direct Arr fallback. |
| Privacy | Snapshot/JSON/error/log/export/push assertions; non-owner identity and votes absent; own information present; cross-user/cache/ID enumeration and XSS/CSRF checks. |
| Integrity | Alter, omit, reorder, duplicate or replay each authorization/history event; projection tampering; journal suffix truncation; entire-store rollback; missing keys/witness; no silent reset. |
| Upgrades/restore | Preserve configuration, keys, database and valid seals across every released schema; honest legacy gaps; stopped complete backups only; reject partial key/old-checkpoint restore. |
| Read purity/UI | GETs, admin polling, Help and notice opens produce no activity, consent, subscriptions, execution or repairs; native commands/themes preserved; no Web file changes. |

Record outbound calls at the fault proxy and assert destructive-call counts, not just UI status. For recovery assertions, verify both persisted authenticated checkpoints and the actual synthetic files/request records.

## 11. Delivery sequence and exit criteria

### Milestone 0 — Compatibility and safety spikes

- Record confirmed Decisions A/B and resolve the remaining user-page, remote-pinning and rollback-anchor gates in short architecture decision records.
- Build/load a .NET 10 plugin on the exact Jellyfin 12.x baseline; verify session authentication, elevation, item visibility and activity event hooks.
- Implement the verified native admin-page mechanism; prototype ordinary-user entry points without persistent Web changes and test with a genuinely non-admin account.
- Capture real Seerr contract traces for movie/series and standard/4K removal; verify request retention and remote pinning requirements.

Exit: supported version/client matrix, accepted removal semantics, integrity trust model and executable API contract. Unresolved gates remain visibly blocking; do not invent API support.

### Milestone 1 — Authenticated persistence and pure domain rules

- Implement versioned schema, encrypted journal, witness protocol and corruption behavior first.
- Implement authority, canonical targets, deduplication, consent and timing with property-based and state-machine tests.
- Create migration and crash-recovery fixtures before first persistent release.

Exit: tampering/replay is rejected, concurrent duplicates collapse safely, and no command can shorten an established deadline.

### Milestone 2 — Read-only evidence and activity collection

- Add Jellyfin/Seerr read adapters, verified ownership and scope mapping, activity subscriptions/backfill and coverage tracking.
- Ship diagnostics in a live-elevated, read-only administrator workspace.

Exit: unsupported, ambiguous or incomplete evidence blocks eligibility; disabled/restricted viewers stay protected.

### Milestone 3 — User workflow with execution unavailable

- Add anonymous queue, explicit proposals/joins, current-snapshot Yes/No, owner Undo, preferences and opted-in notifications.
- Add privacy-safe dry-run explanations and deadline predictions.

Exit: browser/API privacy tests and read-purity tests pass; no destructive operation can be dispatched.

### Milestone 4 — One-shot execution in isolated Docker fixtures

- Add durable claims, remote preconditions, single dispatch, FilesDeleted evidence, uncertainty and monitoring-only cleanup.
- Exercise every crash/timeout boundary and show retained Seerr requests afterward.

Exit: all deletion/recovery acceptance tests pass against the supported real-service contract. A queue-only release is preferable to bypassing an unresolved Seerr capability.

### Milestone 5 — Controlled opt-in release

- Package the plugin manifest/archive/checksum, compatibility limits, recovery runbook and stopped-backup procedure.
- Complete independent security review of authorization, replay protection and destructive dispatch.
- Release with automatic deletion off and an explicit administrator warning describing exactly which service records/files Seerr removes.

Exit: the full acceptance matrix passes, operational gaps are visible, and no claimed or uncertain operation can become an automatic second delete after restart, upgrade or restore.

## 12. Primary references

These references establish the inspected baseline; implementation must revalidate its pinned deployed versions.

- [Jellyfin 12.2 release](https://github.com/jellyfin/jellyfin/releases/tag/v12.2).
- [Jellyfin 12.2 server framework](https://github.com/jellyfin/jellyfin/blob/v12.2/Jellyfin.Server/Jellyfin.Server.csproj).
- [Jellyfin 12.2 configuration-page controller and elevation policy](https://github.com/jellyfin/jellyfin/blob/v12.2/Jellyfin.Api/Controllers/DashboardController.cs).
- [Jellyfin 12.2 native plugin-page metadata](https://github.com/jellyfin/jellyfin/blob/v12.2/MediaBrowser.Model/Plugins/PluginPageInfo.cs).
- [Jellyfin Web 12.2 native configuration-page route and admin guard](https://github.com/jellyfin/jellyfin-web/blob/v12.2/src/apps/dashboard/routes/routes.tsx).
- [Jellyfin Web 12.2 embedded server-content page host](https://github.com/jellyfin/jellyfin-web/blob/v12.2/src/components/ServerContentPage.tsx).
- [Seerr 3.5.0 release](https://github.com/seerr-team/seerr/releases/tag/v3.5.0).
- [Seerr 3.5.0 media-removal route](https://github.com/seerr-team/seerr/blob/v3.5.0/server/routes/media.ts).
- [Seerr 3.5.0 Radarr removal implementation](https://github.com/seerr-team/seerr/blob/v3.5.0/server/api/servarr/radarr.ts).
- [Seerr 3.5.0 Sonarr removal implementation](https://github.com/seerr-team/seerr/blob/v3.5.0/server/api/servarr/sonarr.ts).
- [Seerr 3.5.0 stable Jellyfin user association](https://github.com/seerr-team/seerr/blob/v3.5.0/server/entity/User.ts).
- [Seerr file-removal API documentation](https://docs.seerr.dev/api/delete-media-file/) — inspect implementation as well; documentation and route permission labels may differ.
