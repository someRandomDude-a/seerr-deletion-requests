# Seerr deletion requests for Jellyfin 12.x

A Jellyfin Server plugin with integrated media/request removal actions, a global Web notification center and native administrator settings.

> **Real deletion requires a new explicit administrator opt-in.** Stock Seerr is trusted to choose the Arr scope and remove its record/files, retaining requests. The plugin enforces current consent, complete activity coverage and delay, persists a claim before dispatch and never automatically resends uncertain attempts. Local authenticated seals do **not** prevent restoring the database, keys and seals together to an older valid state. See [implementation status and test boundaries](docs/implementation-status.md).

## Included

- Global/per-account Denied / Own requests / Any accessible item permissions.
- Canonical movie/whole-series proposals; duplicate submissions approve the existing proposal without changing its owner.
- Snapshot-bound Yes/No, owner-only Undo, 14-day activity protection and a fresh minimum 48-hour eligibility delay.
- Media/detail/right-click removal actions and available Jellyfin-Enhanced Requests actions; no identifier entry or standalone queue page.
- Personal delay floors and automatic owner/required-viewer notices with opt-out, a top-bar unseen badge and explicit Mark read.
- User dropdown/reset controls and server-side, one-click import of an unambiguous Enhanced Seerr connection without exposing its key.
- Optional per-proposal administrator approval, off by default; it never replaces viewer consent or shortens delays.
- Bounded off-event-thread activity collection and fail-closed evidence/coverage diagnostics; plugin failures are contained rather than stopping the server.
- Encrypted, authenticated SQLite journal, retained history, tamper/replay detection and no silent state reset.
- Live session/admin authorization, anonymous ordinary-user DTOs and a separate native admin page.
- One-shot stock-Seerr execution with durable claims, uncertainty and a separate FilesDeleted checkpoint based on Seerr's deletion report.

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

After publication, register **one** platform/channel JSON URL in Jellyfin's plugin repositories. For stable releases on standard Linux x64 Docker:

```text
https://raw.githubusercontent.com/someRandomDude-a/seerr-deletion-requests/manifests/stable/manifest-linux-x64.json
```

Linux ARM64 and Windows x64 have separate catalogs because Jellyfin cannot choose an architecture from one manifest. Stable subscribers never receive preview versions. See [release setup, all catalog URLs and publishing/recovery instructions](docs/releases.md). These URLs are created by the first successful release workflow; adding the workflow does not publish or enable deletion.

## Use

1. Open **Dashboard → Plugins → Deletion queue**. Save the Seerr URL/key, or select **Use Jellyfin-Enhanced connection**. Only one enabled Enhanced Seerr URL is imported; ambiguous/multiple endpoints require manual configuration. Later Enhanced changes require a fresh import. The configured Jellyfin server ID must match this server.
2. Set global/per-user proposal permission using the user dropdown. **Reset selected user** restores inherited permission and default personal preferences, preserving proposals, consent, history and existing deadline floors. Optionally enable **Require administrator approval for each proposal** and approve/revoke in the backlog. Approvals are bound to the target, activity and policy; the approver's current admin role and visibility are rechecked before dispatch.
3. Use **Propose removal** on a movie/whole-series detail page, media card, right-click menu or available Enhanced request. Review the named title/quality, then explicitly confirm. Duplicate submissions approve the existing proposal, retaining its original owner.
4. Open the **notification bell in Jellyfin's top bar** to review proposals, explicitly allow/keep media, Undo your own unclaimed proposal, mark notices read or change preferences. Viewing/Help/notification polling never records activity or consent.
5. If desired, explicitly enable **automatic Seerr-managed deletion** and confirm the real-deletion warning. Old preview-only opt-ins cannot activate this transport. Every restart requires a fresh continuous activity window (default 14 days), then a full minimum two-day eligible delay. Incomplete inventory/activity evidence blocks removal.

An original ASP.NET startup filter injects same-origin resources into the Web index response, without modifying Web files. Enhanced uses that same server extension point; it is optional, not an injection dependency. The bell stays outside its overflow tray. There is no standalone queue page or redirect; the old `/DeletionQueue/ui` route and its resources are removed. `/DeletionQueue/...` remains the API used by the integrated controls, not a user-facing page. Native TV/mobile clients that do not load Jellyfin Web are not modified. Unsupported Web/Enhanced markup may omit controls rather than replacing native commands; see the tested compatibility boundary.

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
