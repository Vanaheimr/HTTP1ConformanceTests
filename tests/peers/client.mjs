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

function request(method, path, headers = {}) {
    return new Promise((resolve, reject) => {

        const url = new URL(base + path);
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
                body:     Buffer.concat(chunks)
            }));

        });

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

}
catch (error) {
    fail('harness', String(error && error.message ? error.message : error));
}

agent.destroy();

process.exit(failed > 0 ? 1 : 0);
