# Demo host

The conformance target every harness in this repository drives against.

```bash
dotnet run --project Demo/HTTP1.Demo.csproj
```

| Listener | Port | |
|---|---|---|
| cleartext HTTP | `:8080` | the main conformance target |
| TLS | `:8443` | self-signed cert generated at startup — use `curl -k` |
| WebSocket | `:8081` | echo server, the Autobahn `fuzzingclient` target |

The WebSocket server is a **separate listener** rather than a route on `:8080`,
because Hermod's HTTP/1.x server has no `Upgrade` dispatch today (PLAN.md,
**H-16**). When that lands, `/ws` moves onto the main port.

## Routes

| Route | Methods | Exercises |
|---|---|---|
| `/` | GET | baseline, `Content-Length` framing |
| `/echo` | POST PUT PATCH | request-body round-trip |
| `/large` | GET | 128 KiB fixed-length body |
| `/slow` | GET | 2 s handler — client patience, reuse afterwards |
| `/chunked` | GET | `Transfer-Encoding: chunked` + chunk extensions (token, valueless) |
| `/trailers` | GET | chunked + trailer fields after the terminal chunk |
| `/files/resource.txt` | GET HEAD OPTIONS | `ETag`/`Last-Modified`, conditional requests, `Range` → `206`/`416` |
| `/files/greeting` | GET | proactive negotiation (`Accept`, `Accept-Language`) + `Vary` |
| `/secret` | GET | `401` + `WWW-Authenticate`, Basic (`alice:secret`) and Bearer (`valid-token-123`) |
| `/search` | GET QUERY | RFC 10008 QUERY + `Content-Location` |
| `/expect` | POST | `Expect: 100-continue` |
| `/redirect/{code}` | GET | `301` `302` `303` `307` |
| `/status/{code}` | GET | arbitrary status codes, for the drivers |
| `/events` | GET | SSE — `retry`, `id`, `event`, a 2 s ticker |

## Why the handlers implement semantics themselves

Several routes do work a framework might do for you — conditional requests,
`Range`, negotiation. That is not a workaround. Hermod exposes `If-None-Match`,
`Range` and friends as typed fields and applies **no policy of its own**,
because it is an origin server rather than a framework: the resource decides
what its own preconditions mean.

So `ServeResource` in [`Program.cs`](Program.cs) is the demo doing its job, and
the `h1semantics` harness will be verifying *that method* as much as the library
underneath it. A harness reporting "no automatic 206" would be reporting a
design decision, not a defect.

## Verified

Every route below was driven with curl 8.21 (`--http1.1` and `--http1.0`) and,
for the WebSocket, a raw-socket RFC 6455 handshake:

```
/ HTTP/1.1 → 200 + Content-Length          / HTTP/1.0 → 200 + Connection: close
/chunked   → A / A;kind=demo;flag / C / 0  (extensions on the wire)
/trailers  → 0 + X-Demo-Checksum: deadbeef
conditional→ If-None-Match → 304 + ETag
Range      → bytes=0-4 → 206 "bytes 0-4/42"; bytes=-6 → 206 "bytes 36-41/42";
             bytes=9999- → 416 "bytes */42"
OPTIONS    → 204 + Allow: GET, HEAD, OPTIONS
negotiation→ default "Hello World" / de "Hallo Welt" / json {"greeting":…}
             + Vary: Accept, Accept-Language
auth       → anon 401 + WWW-Authenticate; basic ok; bearer ok; wrong pw 401
QUERY      → 'ap' → apple apricot + Content-Location: /search?q=ap
expect     → 100 Continue, then 200 OK
keep-alive → 3 requests, num_connects 1/0/0
SSE        → retry: 5000, event: tick, id: 1, data: tick 1
TLS :8443  → 200
WebSocket  → 101, correct Sec-WebSocket-Accept, subprotocol "echo", echo back
```

## One API sharp edge worth knowing

A chunked response needs **both** `TransferEncoding = "chunked"` *and*
`ContentStream = new ChunkedTransferEncodingStream(request.NetworkStream!, true)`.
The server dispatches the worker on the *stream type*, not on the header field —
so setting only the header yields correct headers and a silently empty body, no
error anywhere. That cost a debugging round here and is filed as **H-22**.
