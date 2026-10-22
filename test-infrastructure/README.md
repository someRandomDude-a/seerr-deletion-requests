# Disposable integration checks

Never mount media or production configuration. `compose.yaml` isolates Jellyfin with `network_mode: none`; the host smoke driver refuses an already-configured server. Use a fresh set of this project's volumes per run.

1. Publish the Linux x64 plugin as described in the root README and start the `jellyfin` service.
2. In a separate terminal, run `node test-infrastructure/browser-proxy.mjs`. It binds **only** `127.0.0.1:18496`, proxies the named fixture with `docker exec`, and is not a production proxy.
3. In PowerShell, create a synthetic browser-session file and run the checks:

   ```powershell
   $env:DQ_BROWSER_SESSION='.test-data/browser-session.json'
   node test-infrastructure/smoke.mjs
   Push-Location test-infrastructure/browser
   npm ci --ignore-scripts
   node node_modules/playwright/cli.js install chromium
   Pop-Location
    node test-infrastructure/browser/integrated-check.mjs
   node test-infrastructure/browser/native-check.mjs
   node test-infrastructure/fault-check.mjs
   ```

   On Linux use `export DQ_BROWSER_SESSION=.test-data/browser-session.json`, run npm/Playwright with `test-infrastructure/browser` as the working directory, and add Playwright's `--with-deps` installation option.

The integrated browser check uses real Jellyfin Web login, response injection, header navigation and native admin hosting. It mocks proposal/notice/administrator-approval transport and supplies synthetic media/Enhanced/action-sheet DOM contracts to exercise actions without a media library or Seerr. It checks mobile header visibility/overflow, custom CSS, Escape dismissal, explicit consent/read/Undo, revision-bound admin approval and text-only/session-clearing behavior. This is not a live Enhanced+Seerr+Arr deletion test. The native check exercises the user dropdown/reset, optional per-proposal approval setting and real-deletion opt-in/cancel dialog, then leaves automatic deletion and required admin approval off. Its policy changes affect only this isolated no-media fixture; Seerr's configured port is deliberately unreachable.

**Fault injection intentionally corrupts the fixture's plugin head seal.** The driver verifies the Compose project label, no-network configuration, exact mount destinations and server identity first. It checks host health/user APIs after a worker failure and restart; it must leave the corrupt seal untouched, with the plugin unavailable. It never edits Jellyfin's databases. Do not run this against production or substitute another container.

Screenshots and ephemeral fixture sessions stay under ignored `.test-data/`; do not publish the session file. Stop the loopback proxy when finished and remove **only** these disposable resources:

```powershell
docker compose -f test-infrastructure/compose.yaml down --volumes --remove-orphans
```

GitHub's compatibility workflow runs this sequence against the latest matching stable Server/Web release, recording its immutable image digest. It resolves the triggering plugin tag into `PluginVersion` and `PLUGIN_TEST_VERSION`; the mount directory and installed-plugin version check use that version. For a local tagged-build fixture, set both variables to the four-part version before publishing/starting/running the checks. The pinned Compose default remains a reproducible Jellyfin 12.2 baseline.
