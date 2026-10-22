// Real Jellyfin Web host/login/injection, with bounded media/Enhanced DOM + proposal/notice API contract fixtures.
// No real Seerr or media/files are mounted. Destructive transport is tested separately, never against a live library.
import { chromium } from 'playwright';
import assert from 'node:assert/strict';

const base = 'http://127.0.0.1:18496';
const id = '11111111111111111111111111111111';
const proposalId = '22222222222222222222222222222222';
const browser = await chromium.launch({ headless: true });
let page;
try {
    page = await browser.newPage();
    const mutations = []; let read = false;
    await page.route('**/DeletionQueue/me/notices', r => r.fulfill({ json: [{ id, proposalId,
        text: '<img src=x onerror="window.injectionRan=true"> Private contract notice', isRead: read }] }));
    await page.route('**/DeletionQueue/me/notices/*/read', r => {
        assert.equal(r.request().method(), 'POST'); mutations.push('read'); read = true; return r.fulfill({ status: 204 });
    });
    await page.route('**/DeletionQueue/targets/items/*/preview', r => {
        assert.equal(r.request().method(), 'POST');
        return r.fulfill({ json: { title: 'Contract movie', kind: 0, is4K: false, executionAvailable: true,
            selection: { itemId: id, requestId: null, is4K: false }, fingerprint: 'A'.repeat(64), snapshot: 'B'.repeat(64) } });
    });
    await page.route('**/DeletionQueue/targets/items/*/capabilities', r => r.fulfill({ json: { canPropose: true } }));
    const p = { id: proposalId, title: 'Contract movie', ownership: 'Yours', status: 'Blocked', reason: 'ConsentRequired',
        revision: 3, canUndo: true, ownSnapshot: 'B'.repeat(64), ownAnswer: null, requiredAction: 'ApproveCurrentSnapshot' };
    await page.route('**/DeletionQueue/proposals', r => {
        if (r.request().method() === 'POST') { mutations.push('propose'); assert.equal(r.request().postDataJSON().selection.itemId, id); }
        return r.fulfill({ json: p });
    });
    await page.route('**/DeletionQueue/proposals/' + proposalId, r => r.fulfill({ json: p }));
    await page.route('**/DeletionQueue/proposals/*/consent', r => {
        mutations.push('consent'); assert.equal(r.request().postDataJSON().snapshot, 'B'.repeat(64)); return r.fulfill({ json: p });
    });
    await page.route('**/DeletionQueue/proposals/*/undo', r => { mutations.push('undo'); return r.fulfill({ status: 204 }); });
    const html = await page.request.get(base + '/web/index.html', { headers: { 'Accept-Encoding': 'gzip', 'If-None-Match': 'fixture' } });
    assert.equal(html.status(), 200);
    assert.equal((await html.text()).split('id="dq-web-client"').length - 1, 1);
    assert.equal(html.headers()['cache-control'], 'no-store');
    await page.goto(base + '/web/#/login');
    await page.locator('#txtManualName').fill('dq-viewer');
    await page.getByRole('button', { name: 'Sign In', exact: true }).click();
    await page.waitForFunction(() => !location.hash.startsWith('#/login'));
    await page.locator('.dq-bell:visible').first().waitFor();
    await page.waitForFunction(() => document.querySelector('.dq-badge')?.textContent === '1');
    assert.deepEqual(mutations, []);
    await page.locator('.dq-bell:visible').first().click();
    await page.getByRole('dialog', { name: 'Notifications' }).waitFor();
    assert.equal(await page.locator('dialog.dq-panel img').count(), 0);
    assert.equal(await page.evaluate(() => Boolean(window.injectionRan)), false);
    assert.deepEqual(mutations, []);
    await page.getByRole('button', { name: 'Review proposal', exact: true }).click();
    await page.getByRole('button', { name: 'Allow removal', exact: true }).waitFor();
    assert.deepEqual(mutations, []); // Review/Help/status reads are never consent.
    await page.getByRole('button', { name: 'Allow removal', exact: true }).click();
    await page.getByRole('button', { name: 'Undo proposal', exact: true }).waitFor();
    assert.deepEqual(mutations, ['consent']);
    await page.getByRole('button', { name: 'Undo proposal', exact: true }).click();
    await page.getByRole('button', { name: 'Close', exact: true }).click();
    await page.locator('.dq-bell:visible').first().click();
    await page.getByRole('button', { name: 'Mark read', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.dq-badge')?.hidden);
    assert.deepEqual(mutations, ['consent', 'undo', 'read']);
    await page.keyboard.press('Escape');

    // Enhanced available-request renderer contract at e46b1129; no copying or monkey-patching its implementation.
    await page.evaluate(itemId => {
        const fixture = document.createElement('section'); fixture.id = 'dq-contract-fixture';
        fixture.innerHTML = `<div class="card" data-type="Movie" data-id="${itemId}"><div class="cardScalable" style="height:100px;width:150px;position:relative">Contract movie</div></div>
            <div class="je-request-card"><div class="je-request-info"><div class="je-request-header"><span class="je-request-title" data-media-id="${itemId}">Available request</span></div><div class="je-request-actions"></div></div></div>
            <div class="je-request-card"><div class="je-request-info">Unavailable request</div></div>`;
        document.body.append(fixture);
    }, id);
    await page.locator('.dq-request-action').first().waitFor();
    assert.equal(await page.locator('.dq-request-action:disabled').count(), 1);
    await page.locator('.dq-request-action:not(:disabled)').click();
    await page.getByRole('heading', { name: 'Contract movie', exact: true }).waitFor();
    assert.deepEqual(mutations, ['consent', 'undo', 'read']); // Preview alone is not approval.
    await page.getByRole('button', { name: 'Propose removal / approve existing', exact: true }).click();
    await page.getByRole('dialog', { name: 'Removal proposal' }).waitFor();
    assert.deepEqual(mutations, ['consent', 'undo', 'read', 'propose']);
    await page.keyboard.press('Escape');
    await page.locator('.dq-card-action').click();
    await page.getByRole('heading', { name: 'Contract movie', exact: true }).waitFor();
    await page.keyboard.press('Escape');

    // Append a native action-sheet contract after a right click. Jellyfin owns dismissal; no native Delete ID is used.
    await page.locator('.card[data-id="' + id + '"]').dispatchEvent('contextmenu');
    await page.evaluate(() => {
        const menu = document.createElement('div'); menu.className = 'actionSheet';
        menu.innerHTML = '<div class="actionSheetScroller"></div>';
        menu.addEventListener('click', e => {
            if (!e.target.closest('.actionSheetMenuItem')) return;
            window.dqNativeCommand = e.target.closest('.actionSheetMenuItem').getAttribute('data-id');
            menu.dispatchEvent(new Event('close')); menu.remove();
        });
        document.body.append(menu);
    });
    await page.locator('.dq-context-action').click();
    await page.getByRole('heading', { name: 'Contract movie', exact: true }).waitFor();
    assert.equal(await page.evaluate(() => window.dqNativeCommand), null);
    await page.keyboard.press('Escape');

    // Header stays available after real native navigation and in the narrow layout.
    await page.goto(base + '/web/#/configurationpage?name=DeletionQueueAdmin');
    await page.locator('#dqUsers option').first().waitFor({ state: 'attached' });
    await page.locator('.dq-bell:visible').first().waitFor();
    const adminStatus = await page.evaluate(() => ApiClient.ajax({ url: ApiClient.getUrl('DeletionQueue/admin/status'), type: 'GET', dataType: 'json' }));
    const set = (o, name, v) => { o[Object.keys(o).find(k => k.toLowerCase() === name.toLowerCase()) || name] = v; };
    const policy = adminStatus.policy || adminStatus.Policy;
    set(policy, 'RequireAdministratorApproval', true);
    set(adminStatus, 'Proposals', [{ Id: proposalId, Target: { Title: 'Admin approval fixture' }, Revision: 3,
        Status: 0, Reason: 'AdministratorApprovalRequired', Terminal: false, Claim: null }]);
    set(adminStatus, 'AdministratorApprovalSnapshots', { [proposalId]: 'C'.repeat(64) });
    await page.route('**/DeletionQueue/admin/status', r => r.fulfill({ json: adminStatus }));
    await page.route('**/DeletionQueue/admin/proposals/*/approval', r => {
        const body = r.request().postDataJSON();
        assert.equal(body.snapshot, 'C'.repeat(64)); assert.equal(body.revision, 3); assert.equal(body.approved, true);
        assert.equal(body.answer, undefined); mutations.push('administrator-approval'); return r.fulfill({ status: 204 });
    });
    await page.locator('#dqRefresh').click();
    await page.locator('#dqBacklog summary').click();
    await page.getByRole('button', { name: 'Approve removal', exact: true }).click();
    await page.waitForFunction(() => document.getElementById('deletionQueueAdmin').getAttribute('aria-busy') === 'false');
    assert.equal(mutations.at(-1), 'administrator-approval');
    assert.equal(await page.locator('#dqRequireAdminApproval').isChecked(), true);
    await page.setViewportSize({ width: 390, height: 844 });
    const box = await page.locator('.dq-bell:visible').first().boundingBox();
    assert.ok(box && box.x >= 0 && box.x + box.width <= 391);
    await page.locator('.dq-bell:visible').first().click();
    await page.getByRole('dialog', { name: 'Notifications' }).waitFor();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
    await page.addStyleTag({ content: '.dq-panel { background-color: rgb(230, 231, 232) !important; color: rgb(13, 14, 15) !important; }' });
    assert.equal(await page.locator('dialog.dq-panel').evaluate(n => getComputedStyle(n).color), 'rgb(13, 14, 15)');
    await page.screenshot({ path: '.test-data/integrated-mobile.png', fullPage: true });
    await page.keyboard.press('Escape');
    await page.locator('dialog.dq-panel').waitFor({ state: 'detached' });
    assert.equal(await page.locator('dialog.dq-panel').count(), 0);
    await page.locator('.dq-bell:visible').first().click();
    await page.getByRole('dialog', { name: 'Notifications' }).waitFor();
    await page.evaluate(() => { window.ApiClient = null; window.dispatchEvent(new StorageEvent('storage', { key: 'jellyfin_credentials' })); });
    await page.waitForFunction(() => !document.querySelector('.dq-web-owned'));
    console.log('PASS: real Web injection/login/navigation, mobile pinned bell, text-only notification center, explicit reads/consent/Undo, media + Enhanced + context-menu contracts, session clearing.');
} catch (err) {
    if (page) {
        console.error('Integrated fixture page:', page.url(), (await page.locator('body').innerText()).slice(0, 1200));
        await page.screenshot({ path: '.test-data/integrated-failure.png', fullPage: true });
    }
    throw err;
} finally { await browser.close(); }
