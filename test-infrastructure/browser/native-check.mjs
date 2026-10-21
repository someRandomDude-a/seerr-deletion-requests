// Exercise the real, unmodified Jellyfin Web login and native plugin-page host.
import { chromium } from 'playwright';
import assert from 'node:assert/strict';

const base = 'http://127.0.0.1:18496';
const browser = await chromium.launch({ headless: true });
try {
    const page = await browser.newPage();
    await page.goto(base + '/web/#/login');
    await page.locator('#txtManualName').fill('dq-viewer');
    await page.getByRole('button', { name: 'Sign In', exact: true }).click();
    await page.waitForFunction(() => !location.hash.startsWith('#/login'));
    await page.goto(base + '/web/#/configurationpage?name=DeletionQueueAdmin');
    try { await page.waitForSelector('#dqAccounts select'); }
    catch (e) {
        console.error('Native page fixture:', page.url(), (await page.locator('body').innerText()).slice(0, 3000));
        await page.screenshot({ path: '.test-data/native-failure.png', fullPage: true });
        throw e;
    }
    await page.waitForFunction(() => document.getElementById('deletionQueueAdmin').getAttribute('aria-busy') === 'false');
    assert.equal(await page.locator('#dqError').textContent(), '');
    assert.equal(await page.locator('#dqAccounts select').first().evaluate(e => getComputedStyle(e).display), 'block');
    assert.ok((await page.locator('#dqCoverage').textContent()).includes('localIntegrityVerified'));
    await page.screenshot({ path: '.test-data/native-admin.png', fullPage: true });

    // Revoke the fixture's earlier opt-in so the confirmation can be exercised without any deletion transport.
    if (await page.locator('#dqAuto').isChecked()) await page.locator('label:has(#dqAuto)').click();
    assert.equal(await page.locator('#dqAuto').isChecked(), false);
    await page.getByLabel('dq-viewer', { exact: true }).selectOption('2');
    const revoked = page.waitForResponse(r => r.url().endsWith('/DeletionQueue/admin/policy') && r.request().method() === 'PATCH');
    const refreshed = page.waitForResponse(r => r.url().endsWith('/DeletionQueue/admin/status'));
    await page.locator('#dqPolicy button[type=submit]').click();
    assert.equal((await revoked).status(), 204);
    await refreshed;
    await page.waitForFunction(() => document.getElementById('deletionQueueAdmin').getAttribute('aria-busy') === 'false');
    await page.waitForFunction(() => Array.from(document.querySelectorAll('#dqAccounts select')).some(s => s.value === '2'));
    let writes = 0;
    page.on('request', r => { if (r.url().endsWith('/DeletionQueue/admin/policy') && r.method() === 'PATCH') writes++; });
    await page.locator('label:has(#dqAuto)').click();
    assert.equal(await page.locator('#dqAuto').isChecked(), true);
    await page.locator('#dqPolicy button[type=submit]').click();
    await page.waitForFunction(() => document.getElementById('dqOptInDialog').open);
    await page.screenshot({ path: '.test-data/native-optin.png', fullPage: true });
    assert.equal(writes, 0);
    await page.keyboard.press('Escape');
    await page.waitForFunction(() => !document.getElementById('dqOptInDialog').open);
    assert.equal(writes, 0);
    await page.locator('#dqPolicy button[type=submit]').click();
    await page.waitForFunction(() => document.getElementById('dqOptInDialog').open);
    const confirmed = page.waitForResponse(r => r.url().endsWith('/DeletionQueue/admin/policy') && r.request().method() === 'PATCH');
    await page.locator('#dqOptInYes').click();
    assert.equal((await confirmed).status(), 204);
    assert.equal(writes, 1);
    console.log('PASS: real native Web login/admin rendering, themed controls, explicit opt-in dialog and cancel safety.');
} finally { await browser.close(); }
