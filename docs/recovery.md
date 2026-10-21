# State, integrity failures and backups

## Storage

The plugin stores its state under `IApplicationPaths.DataPath/deletion-queue` (usually `/config/data/deletion-queue` in Docker). This is independent of the versioned plugin binary directory.

- `queue.sqlite3` and any SQLite WAL/SHM files: append-only encrypted authenticated events and snapshots.
- `keys.json`: installation identity and master key; keep confidential. Purpose-specific encryption/seal keys are derived from it.
- `head.seal`: authenticated committed local journal head.
- Initialization markers and `writer.lock`: installation/one-writer safeguards.

All plugin settings, including the Seerr key, live inside encrypted state. The standard Jellyfin plugin-configuration loader is deliberately not used because its reset-on-deserialization-error behavior is inappropriate for authorization state.

## On integrity failure

1. Stop Jellyfin and any test/proxy process using this instance.
2. Preserve the entire state directory and server logs, including seemingly corrupt or temporary files. Do not edit SQL, overwrite seals, regenerate keys or truncate the journal.
3. Keep execution unavailable. An authenticated DB commit with an older/missing head seal is still a mismatch; the plugin does not silently pick a side.
4. Investigate using copies. This release deliberately provides no “repair/reset/trust everything” button or generic recovery mutation API.

Missing keys or database files in an initialized installation are not a fresh installation. Startup will not recreate them. Uninstalling the plugin does not remove its retained state.

## Stopped, consistent backups only

1. Stop Jellyfin completely; ensure no worker/request remains in flight.
2. Back up the **entire** plugin state directory, plugin binaries/configuration and a manifest of the stopped set. Use Jellyfin's supported procedure for any separate Jellyfin configuration backup; the plugin never writes Jellyfin databases.
3. Keep the backup/key material confidential and access-restricted.
4. Restore only a complete consistent set while stopped. Do not restore an individual key, a single DB file or an older head checkpoint into newer authorization history.
5. Restart and inspect the elevated integrity/coverage status. A collection gap starts warm-up again; it is never treated as no viewer activity.

**Current limitation:** the local journal/head pair cannot detect a wholesale rollback of a complete consistent backup. Consequently this safety preview has no production destructive transport. A future production release must compare the installation with an independently protected latest checkpoint and reject older authorization/claims before permitting commands. Do not claim the existing local seal provides this protection.

## Claimed/uncertain outcomes

The tested execution domain consumes a claim before sending a destructive request. A timeout/crash/ambiguous response never releases that claim for another deletion. Recovery may inspect the pinned files only; file absence must be verified and persisted as a separate FilesDeleted checkpoint before monitoring cleanup or completion.

No production execution/reconciliation endpoint is exposed in this preview. Never work around uncertainty by removing a claim, deleting proposal history or submitting another overlapping proposal.
