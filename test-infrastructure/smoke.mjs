// Host-side equivalent of smoke.py for Docker installations that cannot start shared network namespaces.
// Run browser-proxy.mjs first. Refuses an already-configured fixture.
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

const base = 'http://127.0.0.1:18496';
const authorization = 'MediaBrowser Client="DeletionQueueSmoke", Device="IsolatedFixture", DeviceId="dq-smoke", Version="0.1"';
const value = (o, key) => Object.entries(o).find(([k]) => k.toLowerCase() === key.toLowerCase())?.[1];
async function call(method, path, data, token, expected = 200, origin) {
    const headers = { Authorization: authorization + (token ? ', Token="' + encodeURIComponent(token) + '"' : '') };
    if (origin) headers.Origin = origin;
    if (data !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetch(base + path, { method, headers, body: data === undefined ? undefined : JSON.stringify(data) });
    const text = await response.text();
    assert.ok((Array.isArray(expected) ? expected : [expected]).includes(response.status), `${method} ${path}: ${response.status} ${text.slice(0, 200)}`);
    try { return JSON.parse(text); } catch { return text; }
}
let info;
for (let attempt = 0; attempt < 60; attempt++) {
    try { await call('GET', '/health'); info = await call('GET', '/System/Info/Public'); await call('GET', '/Startup/Configuration'); break; }
    catch (error) { if (attempt === 59) throw error; await new Promise(resolve => setTimeout(resolve, 1000)); }
}
assert.equal(value(info, 'StartupWizardCompleted'), false, 'Refusing an already-configured instance');
await call('POST', '/Startup/Configuration', { UICulture: 'en-US', MetadataCountryCode: 'US', PreferredMetadataLanguage: 'en' }, undefined, 204);
await call('GET', '/Startup/User');
await call('POST', '/Startup/User', { Name: 'dq-admin', Password: 'SyntheticFixture123!' }, undefined, 204);
await call('POST', '/Startup/RemoteAccess', { EnableRemoteAccess: false, EnableAutomaticPortMapping: false }, undefined, 204);
await call('POST', '/Startup/Complete', undefined, undefined, 204);
const login = await call('POST', '/Users/AuthenticateByName', { Username: 'dq-admin', Pw: 'SyntheticFixture123!' });
const admin = value(login, 'AccessToken'), adminId = value(value(login, 'User'), 'Id');
await call('GET', '/DeletionQueue/proposals', undefined, undefined, [401, 403]);
const status = await call('GET', '/DeletionQueue/admin/status', undefined, admin);
assert.equal(value(status, 'StorageAvailable'), true);
assert.equal(value(status, 'ExecutionAvailable'), false);
assert.equal(value(status, 'IndependentRollbackProtection'), false);
assert.equal(value(value(status, 'Policy'), 'AutomaticDeletion'), false);
const plugins = await call('GET', '/Plugins', undefined, admin);
const plugin = plugins.find(p => String(value(p, 'Id')).replaceAll('-', '').toLowerCase() === '3de88ba16d5c49f5854b65a72f473f21');
assert.ok(plugin, 'Installed deletion queue plugin is missing');
assert.equal(String(value(plugin, 'Version')), process.env.PLUGIN_TEST_VERSION || '0.1.0.0', 'Installed plugin version does not match the build/tag');
console.log('PASS: plugin loads, encrypted store opens, automatic execution defaults off');
const pages = await call('GET', '/web/ConfigurationPages?enableInMainMenu=true', undefined, admin);
assert.ok(pages.some(p => value(p, 'Name') === 'DeletionQueueAdmin'));
const html = await call('GET', '/web/ConfigurationPage?name=DeletionQueueAdmin', undefined, admin);
assert.ok(html.includes('Authenticated transition journal'));
assert.ok(!html.includes('dq-admin'));
assert.ok((await call('GET', '/DeletionQueue/ui')).includes('Safety preview'));
console.log('PASS: native page registration and resources; ordinary shell contains no private state');
const viewer = await call('POST', '/Users/New', { Name: 'dq-viewer' }, admin);
const viewerId = value(viewer, 'Id');
const viewerPolicyId = viewerId.replaceAll('-', '').replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, '$1-$2-$3-$4-$5');
const viewerLogin = await call('POST', '/Users/AuthenticateByName', { Username: 'dq-viewer', Pw: '' });
const viewerToken = value(viewerLogin, 'AccessToken');
await call('GET', '/DeletionQueue/proposals', undefined, viewerToken);
await call('GET', '/DeletionQueue/admin/status', undefined, viewerToken, [401, 403]);
await call('GET', '/web/ConfigurationPages', undefined, viewerToken, [401, 403]);
console.log('PASS: ordinary-user queue access, live elevation on admin evidence and discovery');
const preferences = await call('GET', '/DeletionQueue/me/preferences', undefined, viewerToken);
const body = { commandId: randomUUID(), revision: value(preferences, 'Revision'), delayHours: 96, notifications: false };
const saved = await call('PATCH', '/DeletionQueue/me/preferences', body, viewerToken);
assert.equal(value(saved, 'DelayHours'), 96);
await call('PATCH', '/DeletionQueue/me/preferences', body, viewerToken, 409);
await call('PATCH', '/DeletionQueue/me/preferences', { ...body, commandId: randomUUID(), revision: value(saved, 'Revision') }, viewerToken, 403, 'https://attacker.invalid');
console.log('PASS: explicit preferences, command replay rejection, origin protection');
const secret = 'SYNTHETIC-SEERR-KEY-DO-NOT-LOG';
await call('PATCH', '/DeletionQueue/admin/seerr', { commandId: randomUUID(), revision: value(value(status, 'Seerr'), 'Revision'), baseUrl: 'http://127.0.0.1:59999', apiKey: secret }, admin, 204);
const policy = value(status, 'Policy');
const autoProperty = Object.keys(policy).find(k => k.toLowerCase() === 'automaticdeletion');
policy[autoProperty] = true;
await call('PATCH', '/DeletionQueue/admin/policy', { commandId: randomUUID(), revision: value(policy, 'Revision'), policy }, admin, 204);
const updated = await call('GET', '/DeletionQueue/admin/status', undefined, admin);
assert.equal(value(value(updated, 'Policy'), 'AutomaticDeletion'), true);
assert.equal(value(updated, 'ExecutionAvailable'), false);
assert.ok(!JSON.stringify(updated).includes(secret));
await call('POST', '/DeletionQueue/admin/execute', {}, admin, 404);
console.log('PASS: opt-in cannot bypass absent safety contracts; private credentials never returned');
for (const path of ['/DeletionQueue/proposals', '/DeletionQueue/me/preferences', '/DeletionQueue/me/notices', '/DeletionQueue/ui'])
    await call('GET', path, undefined, viewerToken);
const after = await call('GET', '/DeletionQueue/admin/status', undefined, admin);
const mutations = events => events.filter(e => !['collector', 'jellyfin-activity'].includes(value(e, 'Actor')));
assert.deepEqual(mutations(value(updated, 'Journal')), mutations(value(after, 'Journal')));
console.log('PASS: monitoring, notices, Help shell and queue reads do not mutate authorization');
await call('POST', '/Auth/Keys?app=DeletionQueueSmoke', undefined, admin, 204);
const keys = await call('GET', '/Auth/Keys', undefined, admin);
const keyItems = Array.isArray(keys) ? keys : value(keys, 'Items');
const apiKey = value(keyItems.find(k => value(k, 'AppName') === 'DeletionQueueSmoke'), 'AccessToken');
await call('GET', '/DeletionQueue/admin/status', undefined, apiKey, [401, 403]);
await call('GET', '/DeletionQueue/proposals', undefined, apiKey, [401, 403]);
console.log('PASS: service API keys cannot impersonate users/admins');
const viewerRecord = await call('GET', '/Users/' + viewerId, undefined, admin);
const viewerPolicy = value(viewerRecord, 'Policy'); viewerPolicy.IsAdministrator = true;
await call('POST', '/Users/' + viewerId + '/Policy', viewerPolicy, admin, 204);
const adminRecord = await call('GET', '/Users/' + adminId, undefined, viewerToken);
const adminPolicy = value(adminRecord, 'Policy'); adminPolicy.IsAdministrator = false;
await call('POST', '/Users/' + adminId + '/Policy', adminPolicy, viewerToken, 204);
await call('GET', '/DeletionQueue/admin/status', undefined, admin, [401, 403]);
console.log('PASS: existing sessions lose admin access immediately after live demotion');
const beforeHostile = await call('GET', '/DeletionQueue/admin/status', undefined, viewerToken);
for (const [path, payload, expected] of [
    ['/DeletionQueue/admin/policy', { commandId: randomUUID(), revision: 1, policy: null }, 400],
    ['/DeletionQueue/admin/policy', { commandId: randomUUID(), revision: 1, policy: { Accounts: null } }, 400],
    ['/DeletionQueue/me/preferences', { commandId: randomUUID(), revision: 1, delayHours: -100, notifications: false }, 400],
    ['/DeletionQueue/me/preferences', { commandId: randomUUID(), revision: 1, delayHours: 48, notifications: false, userId: adminId }, 400],
    ['/DeletionQueue/admin/seerr', { commandId: randomUUID(), revision: 1, baseUrl: 'file:///etc/passwd', apiKey: 'x' }, 409],
    ['/DeletionQueue/admin/seerr', { commandId: randomUUID(), revision: 1, baseUrl: 'http://127.0.0.1', apiKey: 'x\r\nInjected: yes' }, 409]
]) await call('PATCH', path, payload, viewerToken, expected);
for (const [path, raw, expected] of [
    ['/DeletionQueue/me/preferences', '{"revision":1,"Revision":0}', 400],
    ['/DeletionQueue/me/preferences', '{"revision":1,"\\u0072evision":0}', 400],
    ['/DeletionQueue/me/preferences', '{"revision":', 400],
    ['/DeletionQueue/me/preferences', 'null', 400],
    ['/DeletionQueue/admin/policy', JSON.stringify({ commandId: randomUUID(), revision: 1, policy: { accounts: {}, surprise: true } }), 400],
    ['/DeletionQueue/me/preferences', JSON.stringify({ commandId: randomUUID(), revision: 1, delayHours: 48, notifications: false, ignored: 'x'.repeat(40000) }), 413],
    ['/DeletionQueue/me/preferences', '{"nested":' + '['.repeat(20) + '0' + ']'.repeat(20) + '}', 400]
]) {
    const response = await fetch(base + path, { method: 'PATCH', headers: { Authorization: authorization + ', Token="' + viewerToken + '"', 'Content-Type': 'application/json' }, body: raw });
    const text = await response.text(); assert.equal(response.status, expected, `Hostile JSON: ${response.status} ${text.slice(0, 200)}`);
}
await call('POST', '/DeletionQueue/targets/preview', { itemId: null, requestId: null, is4K: false }, viewerToken, 400);
await call('POST', '/DeletionQueue/proposals', { commandId: randomUUID(), selection: null, fingerprint: 'A'.repeat(64), snapshot: 'B'.repeat(64) }, viewerToken, 400);
await call('POST', '/DeletionQueue/proposals', { commandId: '00000000-0000-0000-0000-000000000000', selection: { itemId: randomUUID(), requestId: null, is4K: false }, fingerprint: 'A'.repeat(64), snapshot: 'B'.repeat(64) }, viewerToken, 409);
await call('POST', '/DeletionQueue/targets/preview', { itemId: randomUUID(), requestId: null, is4K: false }, viewerToken, 404);
const afterHostile = await call('GET', '/DeletionQueue/admin/status', undefined, viewerToken);
assert.deepEqual(mutations(value(beforeHostile, 'Journal')), mutations(value(afterHostile, 'Journal')));
const deniedPolicy = value(afterHostile, 'Policy');
const accountKey = Object.keys(deniedPolicy).find(k => k.toLowerCase() === 'accounts');
const policyRevision = Object.keys(deniedPolicy).find(k => k.toLowerCase() === 'revision');
deniedPolicy[accountKey][viewerPolicyId] = 0;
await call('PATCH', '/DeletionQueue/admin/policy', { commandId: randomUUID(), revision: deniedPolicy[policyRevision], policy: deniedPolicy }, viewerToken, 204);
// The fixture's configured Seerr port is deliberately unreachable. Denied authority must reject before contacting it.
await call('POST', '/DeletionQueue/targets/preview', { itemId: null, requestId: 1, is4K: false }, viewerToken, 403);
delete deniedPolicy[accountKey][viewerPolicyId];
await call('PATCH', '/DeletionQueue/admin/policy', { commandId: randomUUID(), revision: deniedPolicy[policyRevision] + 1, policy: deniedPolicy }, viewerToken, 204);
await call('GET', '/health', undefined, undefined);
assert.equal(value(await call('GET', '/DeletionQueue/admin/status', undefined, viewerToken), 'StorageAvailable'), true);
console.log('PASS: adversarial user/admin inputs are rejected; plugin state and Jellyfin health survive');
if (process.env.DQ_BROWSER_SESSION) {
    await mkdir(dirname(process.env.DQ_BROWSER_SESSION), { recursive: true });
    await writeFile(process.env.DQ_BROWSER_SESSION, JSON.stringify({ server: value(info, 'Id'), user: viewerId, token: viewerToken }));
}
console.log('All Jellyfin smoke checks passed. No media mounted; no deletion attempted.');
