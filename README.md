# Seerr deletion requests for Jellyfin 12.x

A native Jellyfin Server plugin with a private deletion queue and native administrator workspace.

> **Safety-preview release, not a production automatic-deletion release.** Proposals, consent, permissions and history are implemented. **No destructive Seerr request is sent**, even after administrator opt-in: stock Seerr does not provide the required atomic file-set preconditions, and an independent rollback witness is not yet integrated. See [implementation status](docs/implementation-status.md).

## Included

- Global/per-account Denied / Own requests / Any accessible item permissions.
- Canonical movie/whole-series proposals; duplicate submissions approve the existing proposal without changing its owner.
- Snapshot-bound Yes/No, owner-only Undo, 14-day activity protection and a fresh minimum 48-hour eligibility delay.
- Personal delay floors, explicit preferences/subscriptions and a private notification bell with unread counts and explicit Mark read.
- Bounded off-event-thread activity collection and fail-closed evidence/coverage diagnostics; plugin failures are contained rather than stopping the server.
- Encrypted, authenticated SQLite journal, retained history, tamper/replay detection and no silent state reset.
- Live session/admin authorization, anonymous ordinary-user DTOs and a separate native admin page.
- Tested one-shot execution state machine with durable claims, uncertainty and a separate FilesDeleted checkpoint; **not connected to stock Seerr deletion**.

## Build and test

Requires the **.NET 10 SDK**. The plugin currently targets **Jellyfin Server/Web 12.2**; other 12.x releases are not yet certified.

```powershell
dotnet build DeletionQueue.slnx -c Release
dotnet test DeletionQueue.slnx -c Release
./scripts/package.ps1 -Runtime linux-x64
```

The package script creates `artifacts/DeletionQueue-0.1.0.0-linux-x64.zip` with MD5/SHA-256 sidecars, a package descriptor and installed `meta.json`. Existing packages are not overwritten; choose a fresh `-OutputDirectory` when rebuilding. Extract into a new Jellyfin plugin directory and restart. Back up existing plugin state first; do not overwrite another plugin's directory.

## Releases and repository installation

Tagged releases are built and verified by GitHub Actions. The exact triggering tag supplies the release version: `v0.1.0.1` builds version `0.1.0.1` throughout the DLLs, installed manifest and repository catalogs, without editing a version file. `build/Plugin.props` keeps the untagged-build default, ABI, channel and immutable plugin identity. Tags must use four numeric components; see [release setup](docs/releases.md).

After the first published release, register **one** platform/channel JSON URL in Jellyfin's plugin repositories. For the current preview on standard Linux x64 Docker:

```text
https://raw.githubusercontent.com/someRandomDude-a/seerr-deletion-requests/manifests/preview/manifest-linux-x64.json
```

Linux ARM64 and Windows x64 have separate catalogs because Jellyfin cannot choose an architecture from one manifest. Stable subscribers never receive preview versions. See [release setup, all catalog URLs and publishing/recovery instructions](docs/releases.md). These URLs are created by the first successful release workflow; adding the workflow does not publish or enable deletion.

## Use

1. Open **Dashboard → Plugins → Deletion queue**. This is a native Jellyfin plugin page, not a patched Web interface.
2. Save the Seerr URL/key. Seerr's configured Jellyfin server ID must match this server; credentials are encrypted and not returned by monitoring reads.
3. Configure proposal authority. Own-request movie ownership is verified through stable Jellyfin/Seerr identities. Whole-series own-request authorization remains blocked until complete destructive-scope ownership can be verified; Any accessible item does not bypass execution safety checks.
4. Open the **private queue** link, or `/DeletionQueue/ui` under your Jellyfin base URL. Sign in to Jellyfin Web on the same origin first. The private page reuses that existing session; it never asks for passwords or API keys.
5. Verify a movie/series item ID (or Seerr request ID), explicitly propose/approve, and use **Allow deletion? / Yes / No / Help**. Viewing anything does not approve or record activity.

The ordinary-user page is plugin-owned and same-origin, using the installed Jellyfin Web styles, theme and Branding CSS. Its bell never marks notices read or approves deletion just by opening it. Jellyfin 12.2's stock native configuration-page host is administrator-only; universal native-client menu/item-action integration or a global client-header bell is **not** implemented. No persistent Web changes or native command replacements are made.

Latest-stable Server/Web compatibility, hostile-input and browser/fault-isolation tests run in a separate scheduled/PR workflow and gate releases. An in-process plugin cannot promise immunity from all host crashes; see the [safety review and limits](docs/safety-review.md).

## Isolated Docker integration tests

Never mount a real media library or use production credentials. These tests configure a fresh synthetic server and refuse an already-configured instance.

```powershell
dotnet publish src/Jellyfin.Plugin.DeletionQueue/Jellyfin.Plugin.DeletionQueue.csproj -c Release -r linux-x64 --self-contained false -o artifacts/plugin/linux-x64
docker compose -f test-infrastructure/compose.yaml up -d jellyfin
docker compose -f test-infrastructure/compose.yaml run --rm smoke
```

The fixture uses `network_mode: none` and no media mounts. If Docker cannot start a shared network namespace, run `node test-infrastructure/browser-proxy.mjs` in one terminal and `node test-infrastructure/smoke.mjs` in another. The proxy exposes only the synthetic fixture at `127.0.0.1:18496`, supports HTTP but not WebSockets, and must not be used for production.

See [repeatable browser and fault-isolation checks](test-infrastructure/README.md) for the themed bell, native admin page and corrupt-plugin-storage survival tests.

After stopping the test proxy, remove **only this test project's disposable containers/volumes** with `docker compose -f test-infrastructure/compose.yaml down --volumes`. Do not reuse its volumes for another test run: the smoke driver intentionally refuses to reconfigure an existing server.

## Design and operations

- [Implementation plan](docs/implementation-plan.md)
- [Implemented behavior and remaining gates](docs/implementation-status.md)
- [Native-page verification](docs/jellyfin-12-native-pages.md)
- [Recovery and stopped backups](docs/recovery.md)
- [Host isolation, hostile input and themed notifications](docs/safety-review.md)
