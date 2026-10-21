import { chromium } from 'playwright';
import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';

const session = JSON.parse(await readFile(process.env.DQ_BROWSER_SESSION || '.test-data/browser-session.json', 'utf8'));
const base = 'http://127.0.0.1:18496';
for (let attempt = 0; attempt < 60; attempt++) {
    try {
        const response = await fetch(base + '/Users/Me', { headers: { Authorization: `MediaBrowser Token="${session.token}"` } });
        if (!response.ok) throw new Error('Fixture is not ready');
        break;
    } catch (e) { if (attempt === 59) throw e; await new Promise(resolve => setTimeout(resolve, 1000)); }
}
const browser = await chromium.launch({ headless: true });
try {
    const page = await browser.newPage();
    await page.goto(base + '/System/Info/Public');
    await page.evaluate(s => {
        localStorage.setItem('jellyfin_credentials', JSON.stringify({ Servers: [{ Id: s.server, UserId: s.user, AccessToken: s.token }] }));
        localStorage.setItem(s.user + '-appTheme', 'light');
        localStorage.setItem(s.user + '-customCss', '#deletionQueueUser .emby-button { color: rgb(13, 14, 15) !important; }');
    }, session);
    let read = false; const mutations = [];
    await page.route('**/DeletionQueue/me/notices', route => route.fulfill({ json: [{
        Id: '11111111111111111111111111111111', ProposalId: '22222222222222222222222222222222',
        Text: '<img src=x onerror="window.injectionRan=true"> Private fixture notice', IsRead: read
    }] }));
    await page.route('**/DeletionQueue/me/notices/*/read', route => {
        assert.equal(route.request().method(), 'POST'); mutations.push('read'); read = true; return route.fulfill({ status: 204 });
    });
    await page.goto(base + '/DeletionQueue/ui');
    await page.waitForFunction(() => document.getElementById('status').textContent.includes('private queue'));
    assert.equal(await page.locator('#error').textContent(), '');
    assert.equal(await page.locator('html').getAttribute('data-theme'), 'light');
    assert.equal(await page.locator('#refresh').evaluate(e => getComputedStyle(e).color), 'rgb(13, 14, 15)');
    assert.equal(await page.locator('#selectionType').evaluate(e => getComputedStyle(e).display), 'block');
    assert.equal(await page.locator('#noticeCount').evaluate(e => getComputedStyle(e).position), 'absolute');
    const lightBackground = await page.locator('html').evaluate(e => getComputedStyle(e).backgroundColor);
    await page.evaluate(s => {
        localStorage.setItem(s.user + '-appTheme', 'dark'); localStorage.setItem(s.user + '-disableCustomCss', 'true');
        window.dispatchEvent(new StorageEvent('storage', { key: s.user + '-appTheme' }));
    }, session);
    assert.equal(await page.locator('html').getAttribute('data-theme'), 'dark');
    assert.equal(await page.locator('#deletionQueueUserCss').textContent(), '');
    await page.waitForFunction(() => getComputedStyle(document.getElementById('refresh')).color !== 'rgb(13, 14, 15)');
    await page.waitForFunction(light => getComputedStyle(document.documentElement).backgroundColor !== light, lightBackground);
    await page.screenshot({ path: '.test-data/queue-dark.png', fullPage: true, animations: 'disabled' });
    await page.evaluate(s => {
        localStorage.setItem(s.user + '-appTheme', 'light'); localStorage.setItem(s.user + '-disableCustomCss', 'false');
        window.dispatchEvent(new StorageEvent('storage', { key: s.user + '-appTheme' }));
    }, session);
    await page.waitForFunction(light => getComputedStyle(document.documentElement).backgroundColor === light, lightBackground);
    assert.equal(await page.locator('#noticeCount').textContent(), '1');
    await page.screenshot({ path: '.test-data/queue-themed.png', fullPage: true });
    await page.locator('#notificationBell').click();
    await page.waitForFunction(() => document.getElementById('notificationDialog').open);
    assert.equal(await page.locator('#notificationDialog').evaluate(e => e.open), true);
    assert.equal(read, false); assert.deepEqual(mutations, []);
    assert.equal(await page.locator('#notices img').count(), 0);
    assert.equal(await page.evaluate(() => Boolean(window.injectionRan)), false);
    await page.screenshot({ path: '.test-data/queue-notifications.png', fullPage: true });
    await page.getByRole('button', { name: 'Mark read', exact: true }).click();
    await page.waitForFunction(() => document.getElementById('noticeCount').textContent === '0');
    assert.deepEqual(mutations, ['read']);
    await page.locator('#closeNotices').click();
    await page.locator('#helpToggle').click();
    assert.deepEqual(mutations, ['read']);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.reload();
    await page.waitForFunction(() => document.getElementById('status').textContent.includes('private queue'));
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
    await page.locator('#notificationBell').click();
    await page.waitForFunction(() => document.getElementById('notificationDialog').open);
    await page.keyboard.press('Escape');
    await page.waitForFunction(() => !document.getElementById('notificationDialog').open);
    await page.screenshot({ path: '.test-data/queue-mobile.png', fullPage: true });
    await page.evaluate(() => {
        localStorage.removeItem('jellyfin_credentials');
        window.dispatchEvent(new StorageEvent('storage', { key: 'jellyfin_credentials' }));
    });
    assert.equal(await page.locator('#notices').textContent(), '');
    assert.equal(await page.locator('#queue').textContent(), '');
    console.log('PASS: native CSS + custom light theme, notification bell, explicit read, text-only notices, session privacy.');
} finally { await browser.close(); }
