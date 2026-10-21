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
   node test-infrastructure/browser/check.mjs
   node test-infrastructure/browser/native-check.mjs
   node test-infrastructure/fault-check.mjs
   ```

   On Linux use `export DQ_BROWSER_SESSION=.test-data/browser-session.json`, run npm/Playwright with `test-infrastructure/browser` as the working directory, and add Playwright's `--with-deps` installation option.

The first browser check mocks notice/read responses to exercise a populated bell without mounting media. It uses the actual plugin page and installed Jellyfin CSS, switches light/dark/custom themes, checks mobile overflow and Escape dismissal, and verifies text-only rendering/logout privacy. Domain tests separately cover persisted notice rules and read ownership. The native check logs in through unmodified Jellyfin Web and exercises the real native administrator page and explicit opt-in/cancel dialog. Its policy changes affect only this disposable fixture, which has no destructive transport.

**Fault injection intentionally corrupts the fixture's plugin head seal.** The driver verifies the Compose project label, no-network configuration, exact mount destinations and server identity first. It checks host health/user APIs after a worker failure and restart; it must leave the corrupt seal untouched, with the plugin unavailable. It never edits Jellyfin's databases. Do not run this against production or substitute another container.

Screenshots and ephemeral fixture sessions stay under ignored `.test-data/`; do not publish the session file. Stop the loopback proxy when finished and remove **only** these disposable resources:

```powershell
docker compose -f test-infrastructure/compose.yaml down --volumes --remove-orphans
```

GitHub's compatibility workflow runs this sequence against the latest matching stable Server/Web release, recording its immutable image digest. The pinned Compose default remains a reproducible Jellyfin 12.2 baseline.
