// Destructive ONLY to the disposable fixture's plugin head seal; never to Jellyfin's databases.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
const s = JSON.parse(await readFile(process.env.DQ_BROWSER_SESSION || '.test-data/browser-session.json', 'utf8'));
const fixture = JSON.parse(execFileSync('docker', ['inspect', 'deletion-queue-tests-jellyfin-1'], { encoding: 'utf8' }))[0];
assert.equal(fixture.Config.Labels['com.docker.compose.project'], 'deletion-queue-tests');
assert.equal(fixture.HostConfig.NetworkMode, 'none');
const pluginVersion = process.env.PLUGIN_TEST_VERSION || '0.1.0.0';
assert.match(pluginVersion, /^\d+\.\d+\.\d+\.\d+$/);
assert.deepEqual(fixture.Mounts.map(m => m.Destination).sort(), ['/cache', '/config', '/config/plugins/DeletionQueue_' + pluginVersion]);
async function get(path, expected = 200) {
    const response = await fetch('http://127.0.0.1:18496' + path, { headers: { Authorization: `MediaBrowser Token="${s.token}"` } });
    assert.equal(response.status, expected, path); const text = await response.text(); try { return JSON.parse(text); } catch { return text; }
}
const field = (o, key) => Object.entries(o).find(([k]) => k.toLowerCase() === key.toLowerCase())?.[1];
assert.equal(field(await get('/System/Info/Public'), 'Id'), s.server);
assert.equal(field(await get('/DeletionQueue/admin/status'), 'StorageAvailable'), true);
execFileSync('docker', ['exec', 'deletion-queue-tests-jellyfin-1', 'sh', '-c', 'test -f /config/data/deletion-queue/initialized && printf corrupt > /config/data/deletion-queue/head.seal']);
await get('/DeletionQueue/proposals', 503);
assert.equal(field(await get('/DeletionQueue/admin/status'), 'StorageAvailable'), false);
await new Promise(resolve => setTimeout(resolve, 32000)); // Exercise the hosted worker's next failed write.
await get('/health'); await get('/Users/Me');
execFileSync('docker', ['restart', 'deletion-queue-tests-jellyfin-1'], { stdio: 'ignore' });
for (let attempt = 0; attempt < 60; attempt++) {
    try { await get('/health'); await get('/Users/Me'); break; } catch (e) { if (attempt === 59) throw e; await new Promise(resolve => setTimeout(resolve, 1000)); }
}
await get('/Users/Me');
assert.equal(field(await get('/DeletionQueue/admin/status'), 'StorageAvailable'), false);
assert.equal(execFileSync('docker', ['exec', 'deletion-queue-tests-jellyfin-1', 'cat', '/config/data/deletion-queue/head.seal'], { encoding: 'utf8' }), 'corrupt');
console.log('PASS: corrupt plugin state disables only the plugin; Jellyfin survives worker failure and restart; no silent repair.');
