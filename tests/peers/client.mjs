// A7 — Node's node:http as a foreign client of the demo host.
//
//     node client.mjs http://127.0.0.1:8080
//
// node:http is the lowest-level of the four peers: it exposes the trailer
// section (which java.net.http does not) and follows no redirects at all
// (which the other three do). Both differences are reported rather than
// papered over - a SKIP with a reason is worth more than a check that quietly
// measures something else.

import http from 'node:http';
import zlib from 'node:zlib';

let failed = 0;

const pass  = (label)          => console.log(`PASS\t${label}`);
const fail  = (label, detail)  => { failed++; console.log(`FAIL\t${label}\t${detail}`); };
const skip  = (label, reason)  => console.log(`SKIP\t${label}\t${reason}`);
const check = (label, ok, detail) => ok ? pass(label) : fail(label, detail);

const quote = (text) => `"${(text.length > 80 ? text.slice(0, 80) + '…' : text).replace(/\n/g, '\\n')}"`;

const base = (process.argv[2] ?? '').replace(/\/+$/, '');

if (!base) {
    console.error('usage: client.mjs <base-url>');
    process.exit(2);
}

// One agent for everything, so the connection pool is exercised rather than
// sidestepped.
const agent = new http.Agent({ keepAlive: true, maxSockets: 4 });

// One WebSocket, with its messages queued so that a check can await the next
// one. Every wait has a deadline: a server that never answers is a failure
// with a name, not a hung peer.
function openWebSocket(path, protocols) {
    return new Promise((resolve, reject) => {

        const url    = base.replace(/^http/, 'ws') + path;
        const socket = new WebSocket(url, protocols);
        const queue  = [];
        const waits  = [];
        let   closed = null;

        socket.binaryType = 'arraybuffer';

        const deliver = () => {
            while (waits.length > 0 && (queue.length > 0 || closed)) {
                const wait = waits.shift();
                clearTimeout(wait.timer);
                queue.length > 0 ? wait.resolve(queue.shift()) : wait.reject(new Error(`closed ${closed.code}`));
            }
        };

        socket.onmessage = (event) => { queue.push(event.data); deliver(); };
        socket.onclose   = (event) => { closed = { code: event.code, reason: event.reason, wasClean: event.wasClean }; deliver(); };
        socket.onerror   = ()      => reject(new Error(`could not open ${url}`));
        socket.onopen    = ()      => resolve({
            socket,
            next: () => new Promise((res, rej) => {
                const wait = { resolve: res, reject: rej };
                wait.timer = setTimeout(() => { waits.splice(waits.indexOf(wait), 1); rej(new Error('no message within 20 s')); }, 20000);
                waits.push(wait);
                deliver();
            }),
            closed: () => new Promise((res) => {
                const poll = setInterval(() => { if (closed) { clearInterval(poll); res(closed); } }, 20);
                setTimeout(() => { clearInterval(poll); res(closed ?? { code: 'none within 20 s' }); }, 20000);
            })
        });

    });
}

async function webSocketChecks() {

    const ws = await openWebSocket('/ws', ['echo']);

    check('ws/subprotocol', ws.socket.protocol === 'echo', `negotiated ${quote(ws.socket.protocol)}`);

    ws.socket.send('hello over the Upgrade');
    const text = await ws.next();
    check('ws/echo-text', text === 'hello over the Upgrade', `got ${quote(String(text))}`);

    // Every UTF-8 length at once: two, three and four octets.
    const utf8 = 'Grüße — ✓ 🌍';
    ws.socket.send(utf8);
    const utf8Echo = await ws.next();
    check('ws/echo-utf8', utf8Echo === utf8, `got ${quote(String(utf8Echo))}`);

    const octets = new Uint8Array(256).map((_, i) => i);
    ws.socket.send(octets);
    const binary = await ws.next();
    check('ws/echo-binary',
          binary instanceof ArrayBuffer && Buffer.from(binary).equals(Buffer.from(octets)),
          `got ${binary instanceof ArrayBuffer ? binary.byteLength + ' octets' : typeof binary}`);

    // Past 65535 octets the payload length takes the 64-bit form (RFC 6455
    // §5.2), in both directions.
    const large = 'abcdefghij'.repeat(30000);
    ws.socket.send(large);
    const largeEcho = await ws.next();
    check('ws/echo-large', largeEcho === large,
          `sent ${large.length} characters, got ${typeof largeEcho === 'string' ? largeEcho.length : typeof largeEcho}`);

    // What undici offered and the server accepted, recorded rather than
    // asserted: the client's offer is undici's to make.
    console.log(`INFO\tws/extensions\t${quote(ws.socket.extensions || '(none)')}`);

    ws.socket.close(1000, 'done');
    const closed = await ws.closed();
    check('ws/close', closed.code === 1000 && closed.wasClean === true,
          `code ${closed.code}, clean ${closed.wasClean}, reason ${quote(String(closed.reason ?? ''))}`);

}

function request(method, path, headers = {}) {
    return new Promise((resolve, reject) => {

        const url     = new URL(base + path);
        const interim = [];
        const req = http.request({
            agent,
            method,
            host:    url.hostname,
            port:    url.port,
            path:    url.pathname + url.search,
            headers,
            timeout: 20000
        }, (res) => {

            const chunks = [];
            res.on('data', (chunk) => chunks.push(chunk));
            res.on('end',  ()      => resolve({
                status:   res.statusCode,
                headers:  res.headers,
                trailers: res.trailers,
                interim,
                body:     Buffer.concat(chunks)
            }));

        });

        // Every 1xx except 101, which node:http reads past on its own.
        req.on('information', (info) => interim.push(`${info.statusCode} ${info.headers.link ?? ''}`));
        req.on('timeout', () => { req.destroy(new Error('timeout')); });
        req.on('error',   reject);
        req.end();

    });
}

try {

    // ----------------------------------------------------------- baseline

    const root = await request('GET', '/');
    check('baseline', root.status === 200 && root.body.length > 0,
          `status ${root.status}, ${root.body.length} bytes`);

    // ------------------------------------------------------------ chunked

    const chunked = await request('GET', '/chunked');
    check('chunked',
          chunked.status === 200 && chunked.body.toString() === 'chunk-one\nchunk-two\nchunk-three\n',
          `status ${chunked.status}, body ${quote(chunked.body.toString())}`);

    // ----------------------------------------------------------- trailers

    const trailers = await request('GET', '/trailers');
    check('trailers-body',
          trailers.status === 200 && trailers.body.toString() === 'body with trailers\n',
          `status ${trailers.status}, body ${quote(trailers.body.toString())}`);
    check('trailers',
          trailers.trailers['x-demo-checksum'] === 'deadbeef',
          `x-demo-checksum ${quote(String(trailers.trailers['x-demo-checksum']))}`);

    // --------------------------------------------------------------- gzip

    const identity = await request('GET', '/prose');
    const encoded  = await request('GET', '/prose', { 'Accept-Encoding': 'gzip' });

    if (encoded.headers['content-encoding'] !== 'gzip')
        fail('gzip', `Content-Encoding ${quote(String(encoded.headers['content-encoding']))}`);

    else if (!String(encoded.headers['vary'] ?? '').toLowerCase().includes('accept-encoding'))
        fail('gzip', `Vary ${quote(String(encoded.headers['vary']))}`);

    else {
        const decoded = zlib.gunzipSync(encoded.body);
        check('gzip', decoded.equals(identity.body),
              `${decoded.length} decoded vs ${identity.body.length} identity bytes`);
    }

    // ---------------------------------------------------- HEAD matches GET

    const getFile  = await request('GET',  '/files/resource.txt');
    const headFile = await request('HEAD', '/files/resource.txt');

    check('head-matches-get',
          headFile.status === 200 &&
          headFile.body.length === 0 &&
          Number(headFile.headers['content-length']) === getFile.body.length &&
          headFile.headers['etag'] === getFile.headers['etag'],
          `status ${headFile.status}, ${headFile.body.length} body bytes, ` +
          `Content-Length ${headFile.headers['content-length']} vs GET ${getFile.body.length}`);

    // -------------------------------------------------------------- range

    const ranged = await request('GET', '/files/resource.txt', { Range: 'bytes=0-9' });
    check('range',
          ranged.status === 206 &&
          ranged.body.length === 10 &&
          String(ranged.headers['content-range'] ?? '').startsWith('bytes 0-9/'),
          `status ${ranged.status}, ${ranged.body.length} bytes, Content-Range ${quote(String(ranged.headers['content-range']))}`);

    // ------------------------------------------------------ accept-ranges

    check('accept-ranges', getFile.headers['accept-ranges'] === 'bytes',
          `Accept-Ranges ${quote(String(getFile.headers['accept-ranges']))}`);

    // --------------------------------------------------------- status 404

    const notFound = await request('GET', '/status/404');
    check('status-404', notFound.status === 404, `status ${notFound.status}`);

    // ----------------------------------------------------------- redirect

    const redirect = await request('GET', '/redirect/302');
    check('redirect-response',
          redirect.status === 302 && typeof redirect.headers['location'] === 'string',
          `status ${redirect.status}, Location ${quote(String(redirect.headers['location']))}`);
    skip('redirect', 'node:http follows no redirects; the 302 itself is checked instead');

    // -------------------------------------------------------------- reuse

    const again1 = await request('GET', '/');
    const again2 = await request('GET', '/');
    check('reuse', again1.status === 200 && again2.status === 200,
          `${again1.status} / ${again2.status}`);

    // -------------------------------------------------------- early hints
    //
    // /early-hints sends a 103 with a preload Link and then the 200.
    // node:http emits each 1xx as 'information' and reads on. The request
    // after it has to get its own response: RFC 8297's security
    // considerations are about a client that takes the 103 for the final
    // response and misreads everything after it on the connection.

    const hinted = await request('GET', '/early-hints');
    const after  = await request('GET', '/');
    check('early-hints',
          hinted.status === 200 && hinted.body.toString() === 'a 103 went first\n' &&
          hinted.interim.length === 1 && hinted.interim[0] === '103 </prose>; rel="preload"; as=fetch' &&
          after.body.toString() === 'Hermod HTTP/1.1 demo host\n',
          `status ${hinted.status}, 1xx ${JSON.stringify(hinted.interim)}, ` +
          `body ${quote(hinted.body.toString())}, then ${quote(after.body.toString())}`);

    // -------------------------------------------------------- WebSocket
    //
    // Node's own WebSocket (undici, global since Node 22): a WebSocket client
    // that is neither a browser nor Autobahn nor ours, against /ws on the HTTP
    // port, the RFC 9110 §7.8 Upgrade path rather than the :8081 listener.

    if (typeof WebSocket !== 'function') {
        skip('ws', `node ${process.version} has no global WebSocket (Node 22 or later has)`);
    } else {
        await webSocketChecks();
    }

}
catch (error) {
    fail('harness', String(error && error.message ? error.message : error));
}

agent.destroy();

process.exit(failed > 0 ? 1 : 0);
