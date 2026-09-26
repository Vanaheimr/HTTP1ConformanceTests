// A7 — Node's node:http as a foreign *server* for Hermod's HTTP/1.x client.
//
//     node server.mjs          # picks a free port and prints it
//     node server.mjs 18081    # a fixed one
//
// Same five routes as server.go, framed by a second independent stack, so a
// client check that passes against one and fails against the other says
// something specific rather than "somebody is wrong".
//
// Prints "LISTENING <port>" on stdout once accepting.

import http from 'node:http';
import zlib from 'node:zlib';

const plainBody = 'hello from node\n';

const server = http.createServer((req, res) => {

    const path = new URL(req.url, 'http://localhost').pathname;

    switch (path) {

        // Content-Length framed.
        case '/plain':
            res.writeHead(200, {
                'Content-Type':   'text/plain',
                'Content-Length': Buffer.byteLength(plainBody)
            });
            res.end(plainBody);
            break;

        // Chunked: no Content-Length, so node:http frames it itself.
        case '/chunked':
            res.writeHead(200, { 'Content-Type': 'text/plain' });
            res.write('one\n');
            res.write('two\n');
            res.write('three\n');
            res.end();
            break;

        // Trailers: declared up front, added before end().
        case '/trailers':
            res.writeHead(200, { 'Content-Type': 'text/plain', 'Trailer': 'X-Peer-Checksum' });
            res.write('trailing body\n');
            res.addTrailers({ 'X-Peer-Checksum': 'c0ffee' });
            res.end();
            break;

        case '/gzip': {
            const body = zlib.gzipSync(Buffer.from('compressible line\n'.repeat(64)));
            res.writeHead(200, {
                'Content-Type':     'text/plain',
                'Content-Encoding': 'gzip',
                'Content-Length':   body.length
            });
            res.end(body);
            break;
        }

        case '/status/404':
            res.writeHead(404, { 'Content-Type': 'text/plain' });
            res.end('not here\n');
            break;

        default:
            res.writeHead(404, { 'Content-Type': 'text/plain' });
            res.end('unknown route\n');

    }

});

server.listen(Number(process.argv[2] ?? 0), '127.0.0.1', () => {
    console.log(`LISTENING ${server.address().port}`);
});
