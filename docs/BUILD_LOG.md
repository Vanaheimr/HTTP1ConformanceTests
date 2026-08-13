# HTTP/1.1 Conformance Tests — Build Log

The chronological working notes for this repository: every step, the reasoning
behind it, what was found along the way, and how each thing was verified. For
the architecture and conventions see [`../CLAUDE.md`](../CLAUDE.md); for the
reader-facing specification matrix see [`../README.md`](../README.md); for the
work plan see [`../PLAN.md`](../PLAN.md).

Unlike the sibling repositories, this log does **not** start with "wrote the
stack". Hermod's HTTP/1.x implementation predates this repository by years. It
starts with finding out what is actually in it.

---

## 2026-08-13 — State analysis (A0)

### The starting position

The repository existed as two submodule pointers and a `redo.sh`. The question
was not "what should we build" but "what is already true", and the honest answer
turned out to be more interesting than expected in both directions.

**More is implemented than the sibling repos' framing suggested.**
`Hermod/HTTP1/` is ~55 000 lines across 100 files — a complete client *and*
server, chunked transfer coding in both directions and both roles, SSE with
history and replay, a full RFC 6455/7692 WebSocket subsystem, and 440 NUnit
tests. Two READMEs already sit next to the code. This was never a greenfield.

**Less is verified than the test count suggests.** All 440 tests are .NET
against .NET — Hermod against Hermod, Hermod against Kestrel/`HttpClient`. Two
stacks sharing a runtime also share assumptions, so the count measures internal
consistency more than conformance. Nothing external has ever been pointed at
this stack, and the Autobahn results quoted in the WebSocket README (296 + 242 +
126 + 126, 0 failed) are not reproducible from a clean checkout — they are a
claim about a run that happened somewhere, once.

That is the gap this repository exists to close, and it set the shape of the
plan: the third-party tracks are not a nice-to-have appendix, they are the point.

### Counting honestly

The first attempt counted `[Test` per file with grep and produced 424 — wrong,
because the pattern also matches `[TestFixture]` and `[TestCase(...)]`, which
are a class attribute and a parameterized case respectively, not tests. The
numbers in the README come from `dotnet test --list-tests` against this checkout
instead:

| Filter | Tests |
|---|---:|
| `Tests.HTTP.*` | **440** |
| the HTTP/1.x protocol regression selection (the filter in Hermod's own README) | **300** |
| `Tests.HTTP.WebSockets` | **42** |

Hermod's `HTTP1/README.md` recorded 295 for the regression selection on
2026-07-18; it has since grown to 300. Also worth noting: a bare
`--filter "FullyQualifiedName~WebSocket"` returns 60, because it picks up the
HTTP/2 (RFC 8441) and HTTP/3 (RFC 9220) WebSocket tests too. The README uses the
namespace-qualified filter for that reason.

`dotnet build` on the submodule: 0 errors, 358 warnings (pre-existing, mostly
`CS8604` nullability in `HTTPExtAPI.cs`).

### What the source said that the docs did not

The per-RFC matrix was built by reading the source, not the two existing
READMEs — which turned out to matter, because four things were not in them:

- **No content coding at all.** `Content-Encoding` and `Accept-Encoding` are
  typed header fields with nothing behind them: neither the client nor the
  server compresses or decompresses anything. Meanwhile
  `HTTP2/Core/HTTPContentCoding.cs` implements `br`/`gzip`/`deflate` complete
  with a zlib-vs-raw-deflate sniffer. The capability exists in the same
  assembly, one namespace over. (H-2)
- **`HTTPDigestAuthentication` is not RFC 7616.** It parses and emits
  `Digest base64(username):base64(secret)` — no realm, nonce, qop, nc, cnonce or
  response. The class name promises an interoperable scheme and delivers a
  bespoke one; `curl --digest` will never talk to it. A stray comment at line 46
  (`realm="example", nonce="xyz", …`) suggests the real thing was intended.
  (H-3)
- **Status-code gaps, including one outright bug.** `308` is absent entirely.
  `425` exists but is named `NoCode` with the description "No code" — a leftover
  from the old WebDAV draft in which 425 was unassigned; RFC 8470 has since
  defined it as *Too Early*. Also missing: `103`, `421`, `451`, `511`, `208`,
  `508`. (H-1)
- **`Forwarded` (RFC 7239) is unimplemented** and already marked `//ToDo` at
  `HTTP1/Request/HTTPRequest.cs:1125` — only `X-Forwarded-For` is typed.  (H-4)

One finding went the other way and is worth recording as a *positive*: there is
no `Upgrade: h2c` support anywhere in the HTTP/1 server. That is correct — RFC
9113 removed h2c upgrade from the standard — and its absence is a defence
against a real attack class. `h2csmuggler` is therefore in the plan as a test
that must find **nothing**, pinned as a regression rather than left implicit.

### Why the matrix has six markers instead of a checkmark

Writing the first draft with implemented/missing produced a document that lied
in both directions. "Hermod has a `Range` header field" and "Hermod serves `206
Partial Content`" are not the same claim, and neither is "Hermod deliberately
leaves `206` to the handler because it is an origin server, not a framework".

So the matrix distinguishes: tested (✅), implemented but only indirectly
verified (🟢), **typed but no policy applied** (🟡), **deliberately the
handler's job** (🔵), genuinely absent (❌), and out of scope (⬜). The middle
two carry the actual information. Without them, every design decision in the
stack reads as a defect — and with ~55 specs in the table, that would have
produced a document nobody could act on.

This has a direct consequence for A2 that is easy to get wrong later:
`h1semantics` will find no automatic `206`/`304`, and that is not a bug. The
demo's handlers implement those, and the harness verifies **the demo**.

### Tooling reconnaissance

HTTP/1.1 has no h2spec. There is no single canonical conformance suite, so
coverage has to be assembled from independent real-world consumers — which is
arguably better, since each brings its own strictness. What is actually
available here:

- **Two curls, and they complement each other.** Windows ships 8.21 built
  against Schannel with *no* HTTP/2 at all; WSL/Debian has 8.14 with
  nghttp2/nghttp3. The first is a pure HTTP/1.1 witness that cannot accidentally
  upgrade. The second is the more interesting test: a client that *could*
  upgrade but does not proves ALPN negotiation in a way the first never can.
- **Docker is in WSL/Debian** (26.1.5), not Docker Desktop — so Autobahn, the
  proxy matrix and http-garden are all reachable. The daemon is not running
  after a reboot (WSL has no systemd), so the runner scripts must start it. And
  since the demo host runs on Windows, containers reach it via the host IP, not
  `localhost` — a detail that would otherwise cost an hour in A5.
- Python 3, Node, `gh`, and a WSL `dotnet` are all present.

### Repository setup

Scaffolding modelled on HTTP2ConformanceTests: `LICENSE`, `.gitattributes`
(`*.sh` forced to LF so scripts survive a clone on Linux), `.gitignore`,
`HTTP1.slnx` (dependencies only for now — builds clean in 1.9 s), `README.md`,
`PLAN.md`, `CLAUDE.md`, this log.

Remotes follow the house convention: `origin` → GitHub, plus `git1`/`git2` on
graphdefined.com, upstream tracking on `origin/master`. Initial commit `df70c43`,
signed, pushed to all three.

---

## 2026-08-13 — The demo host (A1)

Three listeners — cleartext `:8080`, TLS `:8443` with a certificate generated at
startup, WebSocket echo `:8081` — and fourteen routes, registered identically on
the cleartext and TLS APIs so a harness result never depends on which port it
happened to hit.

### Why the routes look the way they do

They deliberately mirror the HTTP/2 demo's surface (`/`, `/echo`, `/large`,
`/slow`, `/files/…`, `/secret`, `/search`, `/events`), so the two conformance
suites stay comparable, plus the HTTP/1-specific ones the h2 demo has no need
for: `/chunked`, `/trailers`, `/expect`, `/redirect/{code}`, `/status/{code}`.

`/files/resource.txt` serves fixed content with a **fixed** ETag and
`Last-Modified` (`"hermod-h1-demo-resource-v1"`, 2026-01-01). A demo whose
validator changes per run cannot be asserted against, and conditional-request
tests are exactly the ones that would silently start passing for the wrong
reason.

`308` is missing from `/redirect/{code}` — not an oversight, there is no
`HTTPStatusCode` for it (H-1). The route falls back to `302`. Once H-1 lands the
entry gets added and the harness picks it up.

### The handlers implement semantics on purpose

`ServeResource` does conditional evaluation and Range slicing by hand — 304 with
validators and no body, 206 with `Content-Range`, 416 with `bytes */42`,
including the suffix form `bytes=-N`. This looks like it belongs in the library
until you remember the design: Hermod exposes the fields and applies no policy,
because the resource decides what its own preconditions mean.

Recording it here because it is the thing most likely to be "fixed" by a future
reader: a harness reporting *no automatic 206* is reporting a design decision.
The demo is the thing under test there, not the stack.

### Two findings, both from being the first outside consumer

**H-22 — a chunked response can silently produce an empty body.** Setting
`TransferEncoding = "chunked"` and a `ChunkWorker` yields a response with
perfectly correct headers and *nothing after them*. No exception, no log line.
The server dispatches the worker on `httpResponse.HTTPBodyStream is
ChunkedTransferEncodingStream` (`AHTTPServer.cs:731`), so the header field alone
is not the trigger — you must also pass
`ContentStream = new ChunkedTransferEncodingStream(request.NetworkStream!, true)`.

The existing regression tests all set both, which is why nothing caught it: they
were written by someone who already knew. Silent-empty-body is the worst of the
three possible behaviours here (wire it up implicitly, or fail loudly, or this),
so it is filed rather than merely documented.

**H-21 — `Accept-Ranges` is on the wrong side of the request/response split.**
It is defined in `HTTPRequestHeaderField`, but RFC 9110 §14.3 makes it a
*response* field; `HTTPResponse.Builder` has no property for it. The demo falls
back to the generic `SetHeaderField("Accept-Ranges", "bytes")`.

Both are exactly the class of finding this repository exists to produce. The
demo is the first consumer of these APIs that is not also a test written by the
author of the API.

### Verified end to end

Not "it compiles" — every route driven with curl 8.21 over `--http1.1` and
`--http1.0`, plus a hand-written raw-socket RFC 6455 handshake for the
WebSocket. The full transcript is in [`../Demo/README.md`](../Demo/README.md).
The results worth calling out:

- **HTTP/1.0 gets `Connection: close`** and HTTP/1.1 does not — the version
  negotiation is real, not cosmetic.
- **chunk extensions reach the wire** as `A;kind=demo;flag`, i.e. a token
  extension and a valueless one in the same chunk header — the two shapes RFC
  9112 §7.1.1 allows and a parser is most likely to conflate.
- **trailers arrive after the terminal chunk**, `0\r\nX-Demo-Checksum: deadbeef`.
- **`Expect: 100-continue`** produces `100 Continue` and *then* `200 OK` — two
  status lines on one connection, which is where clients most often break.
- **keep-alive** — three requests, `num_connects` 1/0/0.
- **WebSocket** — `101`, a correct `Sec-WebSocket-Accept` (verified against a
  locally computed SHA-1 of key + GUID rather than trusting the server's word
  for it), subprotocol `echo` negotiated, and the payload echoed back.

Build: 0 warnings, 0 errors.

---

## Next

**A2 — the raw-wire harnesses.** `h1syntax`, `h1framing`, `h1conn`,
`h1semantics`, `h1attack`, `h1sse`, plus `tests/run-tests.ps1`. See
[`../PLAN.md`](../PLAN.md).
