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

## 2026-08-13 — The raw-wire harnesses (A2)

Six harnesses plus a diagnostic, on a shared `H1Core`, driven by
`tests/run-tests.sh`. **199/199 checks pass over both transports** — ~97 s
cleartext, ~300 s over TLS.

The runner is bash rather than PowerShell: CI runs on Linux, and Git Bash makes
the same script work on Windows. One script, no drift between two copies. (The
HTTP/2 repo maintains both variants and says so in its conventions; this repo
does not repeat that.)

`H1Core` exists because Hermod's own `HTTPRawSocketClient` is `internal` to
`HermodTests` and cannot be referenced from here — and because six copies of a
raw-socket client is how six subtly different raw-socket clients happen.

### The first run: 192/199, and none of the seven were what they looked like

Every one of the seven failures needed a decision — real finding, or harness
bug? Getting that wrong in either direction is expensive: filing a harness bug
upstream wastes someone's afternoon, and dismissing a real finding as "my test
is wrong" is worse. So each was reproduced by hand with `h1raw` before being
classified. The tally came out four harness bugs, two real findings, one demo
gap — which is roughly the ratio to expect when a harness is younger than the
thing it tests.

**Not a smuggling vulnerability (harness bug).** Two `h1attack` checks failed
with "smuggled request executed": a duplicate `Transfer-Encoding: chunked`
followed by `0\r\n\r\nGET /status/418`, and the 418 came back. It looks alarming
until you read the bytes: after a well-formed terminal chunk, those trailing
bytes *are* a legitimate pipelined request, and answering them is correct
HTTP/1.1. My assertion conflated "the bytes after the body were parsed as a
request" with "a desync occurred".

The deeper point is that a single origin server **cannot** exhibit a TE.TE
desync at all — smuggling is a disagreement between two parsers, and there is
only one here. What one server can be held to is that the boundary is
*deterministic*: refuse the message, or read it as chunked and treat the rest as
exactly one pipelined request. Never a third answer. The checks now assert that,
with a comment pointing at http-garden (A6) for the real differential.

The CL.TE and TE.CL checks stay as they were, and they are meaningful, because
RFC 9112 §6.1 forbids that combination outright: a smuggled request answered
*there* would prove the server picked one field and ignored the other.

**408 after 30 s (harness bug).** Two truncated-body checks failed because the
server was right and the harness impatient — a truncated body is not malformed,
the sender has merely stopped, so the only correct response is to wait out the
read deadline and answer 408. It did, at 30.0 s; the harness gave up at 3.

Rather than widen the windows and accept a two-minute suite, the demo gained
`--fast-timeouts` (3 s instead of 30 s), which the runner passes. That changes
how long the harness waits, not what it asserts: the claim is "an incomplete
message eventually yields 408", never a particular number of seconds. The
default run keeps Hermod's real defaults, so the demo stays representative.

**HTTP/1.0 keep-alive (demo gap, and a documentation lesson).** A `GET / HTTP/1.0`
with `Connection: keep-alive` was answered `Connection: close`. Hermod's README
says keep-alive is honoured "only when it is explicitly negotiated in both
directions", and `HTTP_1_0_KeepAlive_Is_Honoured_When_Negotiated_In_Both_Directions`
turns out to construct its server with `ConnectionType.KeepAlive` — so "both
directions" means the *response* has to opt in too, per handler. Defensible, and
security-conservative: HTTP/1.0 persistence is off unless the application asks.
The demo's `/` handler now asks. Note what did *not* change: the HTTP/1.0
request without a `Connection` field still gets `close`, which is the check that
proves the version logic is real rather than a blanket setting.

**HEAD is not derived from GET (H-23, real).** `HEAD /` returned `405` with
`Allow: GET`. RFC 9110 §9.3.2: "a server SHOULD support HEAD for any resource it
supports GET for". Worse than the 405 is the `Allow` — a client that consults it
to find out what *is* supported is told `GET` and not `HEAD`, which is the one
field that exists to prevent exactly that confusion. Filed upstream; meanwhile
every GET route in the demo registers `HEAD` by hand.

### Then the TLS run hung, which was the best bug of the day

With cleartext green, `--tls` never finished. `h1attack` sat there. The cause was
in the harness, and it is the kind that only shows up on one transport:

`SendAsync` had no deadline. These harnesses deliberately send payloads the
server is supposed to reject — and a server that rejects a large body *stops
reading it*. Over cleartext the socket then errors out almost immediately and
the write fails fast, hiding the problem entirely. Over TLS the extra buffering
means the write simply blocks once the send buffer fills, forever.

So the same code was correct-looking and broken depending on the transport,
which is a good argument for running the suite over both. `SendAsync` is now
bounded and treats a refused write as *data* (`WriteWasRefused`) rather than an
error — because for most of these checks, "the server stopped listening" is the
pass condition.

### And then the suite was too slow, for a reason worth writing down

At 236 s cleartext it was already tedious; TLS blew past ten minutes. The cause
was not TLS and not the server:

**HTTP/1.1 connections are persistent, so the server does not close after
answering.** A read that waits for the peer to close therefore waits out its
entire window on *every single check*. With ~200 checks and a 3 s default, that
is up to ten minutes of pure harness delay, and none of it measuring anything.

The fix is a real response reader — `ReadResponseAsync` returns as soon as the
response is complete *by its own framing*: bodyless statuses (1xx/204/304) and
HEAD replies immediately, chunked bodies at the terminal chunk, otherwise
`Content-Length` bytes, falling back to close-delimited. 236 s → 97 s, with TLS
finishing at 300 s.

HEAD needed an explicit flag: the reply carries a `Content-Length` describing
content it must not send, so a reader that trusts the field waits for bytes that
will never arrive. The response alone cannot tell you which method produced it —
only the caller knows.

One more measurement bug fell out of the same area: a check asserting "oversized
`Content-Length` is rejected without reading the body" was timing `ReadAsync`,
which meant it measured the harness's own window rather than the server's
latency, and reported exactly 10.0 s of a 10 s window. It now sends headers
only — not one byte of the declared 10 GiB — and measures with
`ReadHeadersAsync`, which returns on the header terminator.

### What the harnesses establish, and what they do not

Recorded in `tests/README.md` too, because it is the thing most likely to be
misread six months from now:

`h1semantics` verifies **the demo** as much as the library. The 304s, 206s and
negotiated variants come from `Demo/Program.cs`, because Hermod applies no
resource policy of its own by design. What these 63 checks can honestly
establish is that an origin server built on this library *can* implement RFC
9110 correctly — not that the library does it for you.

Build: 0 warnings, 0 errors. Both transports green.

---

## 2026-08-13 — The curl matrix (A3)

58 checks in `tests/curl-matrix.sh`, wired into the runner. The gate now stands
at **257/257 over both transports** — ~103 s cleartext, ~270 s TLS.

This is the first thing here that is not our own code. Everything before it
establishes something about an implementation written in this repository; curl
establishes that an independent one agrees.

### The checks that only a real client can make

Most of the matrix restates what the C# harnesses already cover, which is the
point — restated by a different implementation. Three go further:

- **`--anyauth`** makes curl probe, parse `WWW-Authenticate`, and choose a
  scheme. Passing means the challenge is not merely present but *parseable* by
  something that did not write it.
- **`--etag-save` / `--etag-compare`** round-trips the validator through curl's
  own store instead of a string we constructed and handed back to ourselves.
- **`-T -`** makes curl chunk the *request*: with no length known up front it
  must reach for `Transfer-Encoding`. A real client emitting real chunks is a
  different claim from our harness emitting hand-written ones.

One check is a **pinned expected failure**: `--digest` returns 401, because
`HTTPDigestAuthentication` is not RFC 7616 (H-3). Asserting the 401 rather than
skipping it means the day H-3 is fixed, the check turns red and says so. An
expected failure that is not asserted is just a gap nobody wrote down.

Also worth recording as a *negative* result: `--compressed` succeeds and returns
identity content with no `Content-Encoding`. The server has no codec at all
(H-2), and degrading cleanly rather than claiming a coding it cannot produce is
the correct behaviour for that gap.

### Three curl quirks, none of them ours

Each cost a failing check before being understood, and all three are curl's own
surface rather than anything on the wire:

- `%{http_version}` reports HTTP/1.0 as **`1`**, not `1.0`.
- `-o` applies **per URL**. With three URLs and one `-o /dev/null`, the second
  and third bodies land on stdout and contaminate `--write-out`.
- `--write-out` is emitted once per URL **with no separator of its own**, so a
  multi-URL format string has to supply one. The connection-reuse check now
  asks for `%{num_connects},` and expects `1,0,0,` — one connection, two reuses.

The Windows build also terminates `--write-out` newlines with CRLF, which would
make any multi-line expectation match on Linux and fail on Windows; the helper
strips CRs once so the checks stay identical across builds.

### The 41-minute hang, and what it was not

The TLS run appeared to hang and was left running far too long. It looked like
the A2 pattern — something that fails fast over cleartext and blocks over TLS —
and it was not. It was a single curl with no `--max-time`, connecting to a
listener whose process had died: the socket was still in LISTEN, owned by a PID
that no longer existed, so connections were accepted and then went nowhere.

Two lessons, both already learned once in A2 and not carried across the language
boundary:

- **Every request needs a deadline.** `curl-matrix.sh` now sets `--max-time` and
  `--connect-timeout` once in `$TIMEOUTS`, applied by every helper. With that in
  place the TLS run finishes in **26 s** — the same work that appeared to hang
  forever.
- **A wait loop needs a sleep and a bound.** An ad-hoc `until grep -q Ready; do
  :; done` spun a core for 53 minutes when the demo failed to start at all. The
  runner already does this properly (60 attempts, 0.5 s apart, checking the
  process is still alive); the shell one-liner did not.

### The bug the curl work uncovered in A2

Wiring the matrix into the runner turned `h1attack` red at 15/17 — two TE.TE
checks that had passed the day before, with nothing between them but the A2
performance work.

The cause was `ReadResponseAsync`, introduced to stop every check waiting out
its window: it returns at the end of the **first** complete response. That is
right for almost everything here and exactly wrong for the smuggling checks,
whose entire question is *did a second response appear*. A reader that stops
after the first can never see one.

So the two checks that still counted responses failed honestly — and the ones
asserting `DoesNotContain("418")` had quietly become **tautologies that pass
whatever the server does**. That is the worse half: a check that breaks tells
you something, a check that silently stops testing does not. Those now use an
explicit `ReadEverythingAsync`, with the reasoning written next to it, because
the fast reader will look like an obvious cleanup to someone in six months.

### The Debian curl leg is skipped, deliberately visibly

Debian's curl 8.14 has nghttp2/nghttp3 and is the more interesting witness: a
client that *could* upgrade and does not proves ALPN negotiation in a way the
Windows build (no HTTP/2 at all) structurally cannot.

It does not run, because the demo binds loopback only and the WSL VM has no
route to it. The runner prints `SKIP … (loopback-only bind)` rather than
omitting it, since a silent skip is indistinguishable from a pass. Fixing it
needs the demo bound to all interfaces plus a firewall rule — not done on my own
initiative, since it opens a listener to the LAN. **A5 and A6 need the same
thing**, so it is one decision rather than three.

---

## Next

**A4 — Autobahn.** Hermod's WebSocket README already claims 296 + 242 + 126 +
126 cases with zero failures; this is where those numbers become reproducible
from a clean checkout. See [`../PLAN.md`](../PLAN.md).
