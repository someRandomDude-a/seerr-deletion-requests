// Loopback-only proxy for the isolated network_mode:none Jellyfin fixture.
// No Web files are patched. WebSocket upgrades are intentionally not supported by this test utility.
import http from 'node:http';
import { spawn } from 'node:child_process';

const container = 'deletion-queue-tests-jellyfin-1';
const server = http.createServer(async (request, response) => {
    const chunks = [];
    let size = 0;
    for await (const chunk of request) {
        size += chunk.length;
        if (size > 1024 * 1024) { response.writeHead(413).end(); return; }
        chunks.push(chunk);
    }
    const args = ['exec', '-i', container, 'curl', '--silent', '--show-error', '--include', '--http1.1', '--max-time', '30',
        '--request', request.method, '--header', 'Expect:'];
    for (const [key, val] of Object.entries(request.headers)) {
        if (['connection', 'content-length', 'transfer-encoding'].includes(key)) continue;
        args.push('--header', `${key}: ${Array.isArray(val) ? val.join(', ') : val}`);
    }
    if (size) args.push('--data-binary', '@-');
    args.push(`http://127.0.0.1:8096${request.url}`);
    const child = spawn('docker', args, { stdio: ['pipe', 'pipe', 'pipe'] });
    const output = [];
    child.stdout.on('data', chunk => output.push(chunk));
    child.stderr.on('data', () => {}); // Never log request tokens or returned content.
    child.on('error', () => { if (!response.headersSent) response.writeHead(502).end('Fixture unavailable'); });
    child.on('close', code => {
        if (response.writableEnded) return;
        const data = Buffer.concat(output);
        const split = data.indexOf('\r\n\r\n');
        if (code || split < 0) { response.writeHead(502).end('Fixture unavailable'); return; }
        const headers = data.subarray(0, split).toString().split('\r\n');
        const status = Number(headers.shift().split(' ')[1]);
        const body = data.subarray(split + 4);
        for (const header of headers) {
            const separator = header.indexOf(':');
            if (separator < 0) continue;
            const key = header.slice(0, separator);
            if (['connection', 'transfer-encoding', 'content-length'].includes(key.toLowerCase())) continue;
            response.setHeader(key, header.slice(separator + 1).trim());
        }
        response.setHeader('Content-Length', body.length);
        response.writeHead(status).end(body);
    });
    child.stdin.end(Buffer.concat(chunks));
});
server.on('upgrade', (_request, socket) => socket.end('HTTP/1.1 501 Not Implemented\r\n\r\n'));
server.listen(18496, '127.0.0.1', () => console.log('Isolated Jellyfin fixture: http://127.0.0.1:18496/web/'));
