// A8 — real browsers against the Hermod HTTP/1.1 demo host.
//
// Everything else in this repository judges the demo with a client somebody
// wrote deliberately: our harnesses, curl, five foreign stdlib clients, five
// reverse proxies. A browser is the one consumer that was not written to be
// a test, and it is the least forgiving one in daily use — it enforces CORS,
// it implements EventSource's reconnection rather than just reading the
// stream, and it will not be told which protocol it spoke.
//
// THE BATTERY RUNS IN THE PAGE
//
// Not here. The driver navigates to the demo's own "/" document and evaluates
// the battery inside it, so every fetch, EventSource and WebSocket below is a
// same-origin request from a real document — which is the only arrangement in
// which CORS, connection reuse and the Resource Timing entries mean anything.
// No page is served for the occasion: A8 tests the demo as it is, and adding
// a /browser route to make the test convenient would have measured that route.
//
//     node interop.mjs --base http://127.0.0.1:8080
//     node interop.mjs --browser webkit --headed
//
// Playwright rather than an installed Chrome: three engines, and WebKit is
// the one nobody tests. It is a large download, once; tests/browser.sh does
// it on demand and skips with a reason if it cannot.

import { chromium, firefox, webkit } from 'playwright';

const ENGINES = { chromium, firefox, webkit };

function arg(name, fallback) {
    const i = process.argv.indexOf(name);
    return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

const base    = arg('--base', 'http://127.0.0.1:8080');
const want    = arg('--browser', 'all');
const headed  = process.argv.includes('--headed');
const only    = arg('--only', null);

// The cross-origin twin. To a browser 127.0.0.1 and localhost are different
// origins however identical they look from a shell, which is what makes a
// preflight reachable without a second listener.
const crossOrigin = base.includes('127.0.0.1')
                        ? base.replace('127.0.0.1', 'localhost')
                        : base.replace('localhost', '127.0.0.1');

const GREEN = '\x1b[32m', RED = '\x1b[31m', YELLOW = '\x1b[33m', DIM = '\x1b[2m', OFF = '\x1b[0m';
const colour = process.stdout.isTTY;
const c = (code, s) => colour ? code + s + OFF : s;


// ---------------------------------------------------------------------------
// The battery. Serialised into the page, so it may not close over anything
// here - everything it needs arrives as its one argument.
// ---------------------------------------------------------------------------

async function battery({ base, crossOrigin }) {

    const results = [];
    const add = (name, ok, detail, skipped) =>
        results.push({ name, ok: !!ok, detail: detail ?? '', skipped: !!skipped });

    const withTimeout = (promise, ms, what) => Promise.race([
        promise,
        new Promise((_, reject) => setTimeout(() => reject(new Error('timed out after ' + ms + 'ms: ' + what)), ms))
    ]);

    // --- the browser's own verdict on the protocol -------------------------
    //
    // Everything else here is us asserting we spoke HTTP/1.1. This is the
    // browser saying it, and it is the one check no client we wrote can
    // stand in for.
    try {
        const response = await fetch(base + '/?nhp=' + Date.now(), { cache: 'no-store' });
        await response.text();
        const entry = performance.getEntriesByType('resource')
                                 .filter(e => e.name.includes('nhp='))
                                 .pop();
        const protocol = entry ? entry.nextHopProtocol : '(no timing entry)';
        add('the browser says it spoke http/1.1', protocol === 'http/1.1', 'nextHopProtocol = ' + protocol);
    } catch (e) {
        add('the browser says it spoke http/1.1', false, String(e));
    }

    // --- a chunked body arrives whole --------------------------------------
    try {
        const body = await (await fetch(base + '/chunked', { cache: 'no-store' })).text();
        // Measured rather than assumed: 33 octets, trailing newline included.
        // tests/interop.sh compares through a $(...) which strips it on both
        // sides and so never had to know; a string comparison in JS does.
        add('a chunked response is reassembled', body === 'chunk-one\nchunk-two\nchunk-three\n',
            JSON.stringify(body.slice(0, 60)));
    } catch (e) {
        add('a chunked response is reassembled', false, String(e));
    }

    // --- the browser's own content coding ----------------------------------
    //
    // The browser chooses its own Accept-Encoding and decodes without being
    // asked; all we can check is that what comes out is the text.
    try {
        const body = await (await fetch(base + '/prose', { cache: 'no-store' })).text();
        add('a negotiated content coding is decoded', body.length > 1000 && !body.includes('�'),
            body.length + ' chars');
    } catch (e) {
        add('a negotiated content coding is decoded', false, String(e));
    }

    // --- Range, and the 206 a browser expects for media --------------------
    try {
        // /files/resource.txt, not /large: the large one is deliberately a
        // plain octet-stream with no conditional or range handling, so
        // pointing this at it measured that choice and not Range support.
        const response = await fetch(base + '/files/resource.txt',
                                     { headers: { Range: 'bytes=0-9' }, cache: 'no-store' });
        const body = await response.arrayBuffer();
        add('a Range request is answered 206 with the slice',
            response.status === 206 && body.byteLength === 10,
            'status ' + response.status + ', ' + body.byteLength + ' bytes');
    } catch (e) {
        add('a Range request is answered 206 with the slice', false, String(e));
    }

    // --- connection reuse, as the browser sees it --------------------------
    //
    // Resource Timing reports connectStart === connectEnd for a request that
    // did not open a connection. That is the browser's own account of
    // keep-alive, rather than ours.
    try {
        const tag = 'reuse=' + Date.now();
        await (await fetch(base + '/?' + tag + '&a', { cache: 'no-store' })).text();
        await (await fetch(base + '/?' + tag + '&b', { cache: 'no-store' })).text();
        const entries = performance.getEntriesByType('resource').filter(e => e.name.includes(tag));
        const second  = entries[entries.length - 1];
        const reused  = second && second.connectStart === second.connectEnd;
        add('the second request reuses the connection', reused,
            second ? 'connectStart ' + second.connectStart.toFixed(2) + ', connectEnd ' + second.connectEnd.toFixed(2)
                   : 'no timing entry');
    } catch (e) {
        add('the second request reuses the connection', false, String(e));
    }

    // --- EventSource, which is more than reading the stream ----------------
    //
    // curl can read an SSE body. Only an EventSource implements the client
    // half of RFC 8895-style reconnection, and only a browser has one.
    try {
        const events = await withTimeout(new Promise((resolve, reject) => {
            const source = new EventSource(base + '/events');
            const seen = [];
            // The demo sends "event: tick", and onmessage fires only for
            // UNNAMED events - so the first version of this waited twenty
            // seconds for a message type the server never sends. Both are
            // listened for, because which of the two a route uses is the
            // route's business rather than this check's.
            const collect = e => {
                seen.push(e.data);
                if (seen.length >= 2) { source.close(); resolve(seen); }
            };
            source.onmessage = collect;
            source.addEventListener('tick', collect);
            source.onerror = () => {
                if (source.readyState === EventSource.CLOSED) { reject(new Error('the stream closed')); }
            };
        }), 20000, 'two server-sent events');
        add('EventSource receives events', events.length >= 2, events.length + ' events');
    } catch (e) {
        add('EventSource receives events', false, String(e));
    }

    // --- WebSocket, handshaken by the browser itself -----------------------
    //
    // Autobahn already judges the framing. What this adds is the handshake
    // made by a browser from a real document - Origin header and all.
    try {
        const echoed = await withTimeout(new Promise((resolve, reject) => {
            const socket = new WebSocket(base.replace(/^http/, 'ws') + '/ws');
            socket.onopen    = () => socket.send('browser-hello');
            socket.onmessage = e => { socket.close(); resolve(e.data); };
            socket.onerror   = () => reject(new Error('the socket errored'));
        }), 15000, 'a websocket echo');
        add('WebSocket handshakes and echoes', echoed === 'browser-hello', JSON.stringify(echoed));
    } catch (e) {
        add('WebSocket handshakes and echoes', false, String(e));
    }

    // --- CORS, which no other client in this repository can test -----------
    //
    // A simple cross-origin GET needs no preflight; only the response's
    // Access-Control-Allow-Origin decides whether the page may read it.
    try {
        const response = await fetch(crossOrigin + '/cors', { cache: 'no-store', mode: 'cors' });
        const body = await response.text();
        add('a simple cross-origin GET is readable', body.startsWith('cors GET'),
            'Access-Control-Allow-Origin was honoured');
    } catch (e) {
        add('a simple cross-origin GET is readable', false,
            'blocked: ' + String(e).slice(0, 90));
    }

    // A custom request header makes it non-simple, and the browser sends an
    // OPTIONS preflight first. This is the check that cannot be made with
    // curl at all - curl would send the request and be answered.
    try {
        await fetch(crossOrigin + '/cors', {
            method:  'POST',
            mode:    'cors',
            headers: { 'X-Demo-Preflight': '1', 'Content-Type': 'application/json' },
            body:    '{}'
        });
        add('a preflighted cross-origin POST succeeds', true, 'OPTIONS answered');
    } catch (e) {
        add('a preflighted cross-origin POST succeeds', false,
            'blocked: ' + String(e).slice(0, 90));
    }

    return results;

}


// ---------------------------------------------------------------------------

const engines = want === 'all' ? Object.keys(ENGINES) : [want];

if (engines.some(name => !ENGINES[name])) {
    console.error('unknown browser: ' + want + ' (chromium, firefox, webkit, all)');
    process.exit(2);
}

console.log('');
console.log('  browser interop — A8');
console.log(c(DIM, '  base:       ' + base));
console.log(c(DIM, '  cross:      ' + crossOrigin + '  (a different origin, to a browser)'));

let total = 0, failed = 0;
const rows = [];

for (const name of engines) {

    let browser;

    try {
        browser = await ENGINES[name].launch({ headless: !headed });
    } catch (e) {
        console.log('    ' + c(YELLOW, '~') + ' ' + name + ' ' + c(DIM, '(' + String(e).split('\n')[0].slice(0, 90) + ')'));
        continue;
    }

    const version = browser.version();
    console.log('');
    console.log('  -- ' + name + ' ' + version + ' --');

    const page = await browser.newPage();
    let results;

    try {
        await page.goto(base + '/', { waitUntil: 'load', timeout: 15000 });
        results = await page.evaluate(battery, { base, crossOrigin });
    } catch (e) {
        console.log('    ' + c(RED, '✗') + ' ' + name + ' could not run the battery');
        console.log('        ' + c(DIM, String(e).split('\n')[0].slice(0, 120)));
        total += 1; failed += 1;
        await browser.close();
        continue;
    }

    for (const r of results) {
        if (only && r.name !== only) continue;
        total += 1;
        if (r.ok) {
            console.log('    ' + c(GREEN, '✓') + ' ' + r.name + ' ' + c(DIM, r.detail));
        } else {
            failed += 1;
            console.log('    ' + c(RED, '✗') + ' ' + r.name);
            console.log('        ' + c(DIM, r.detail));
        }
        rows.push({ engine: name, name: r.name, ok: r.ok, detail: r.detail });
    }

    await browser.close();

}

// One line per failing check, in a shape tests/browser.sh can join against
// its known file: engine TAB check.
for (const r of rows.filter(r => !r.ok)) {
    console.log('DIFF\t' + r.engine + '\t' + r.name + '\t' + r.detail.replace(/\s+/g, ' ').slice(0, 100));
}

console.log('');
const verdict = '  browser: ' + (total - failed) + '/' + total + ' checks passed';
console.log(failed === 0 ? c(GREEN, verdict) : c(RED, verdict));

process.exit(failed === 0 ? 0 : 1);
