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

*(H-1 landed 2026-09-23. The entry is in, and the harness did pick it up: two
checks in `h1semantics` and two in the curl matrix, which is where 257 became
261. See the entry of that date on the bookkeeping.)*

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

### The second curl, after all — and no firewall rule

The section below was written when the Debian leg was still skipped. It is kept
because the reasoning still holds for *why* it is opt-in; what changed is that
enabling it turned out to cost far less than expected.

Binding the demo to `0.0.0.0` (`--bind-any`, opt-in, default stays loopback) was
enough on its own: WSL's vSwitch reaches the host at `172.23.32.1` without any
firewall rule. So the system-settings change I had proposed — and asked for
sign-off on, since it opens a listener — was simply not needed. Worth recording
as a reminder to measure the actual barrier before negotiating about it.

`tests/run-tests.sh --wsl` now runs both curl builds: **315/315**.

Getting there cost three failures on the Debian leg, and **none of them were the
server**. All three came from running a Linux binary through Git Bash and
`wsl.exe`, which mangle arguments in two different ways:

- **MSYS path rewriting.** Git Bash turns POSIX-looking arguments into Windows
  paths — exactly right for the Windows curl (`$TMP/etag` must become
  `C:/Users/…/etag`) and fatal for the Linux one, which then wrote its ETag to a
  path that does not exist inside WSL and *silently saved nothing*.
  `MSYS2_ARG_CONV_EXCL='*'` switches it off for that leg.
- **Glob expansion on the far side.** `wsl.exe` hands its arguments to a shell,
  which expands them: `wsl -d Debian -- echo '*'` prints the repo's directory
  listing. So `--request-target '*'` became a list of filenames, and curl dialled
  out to whatever resolved — the transcript contains 400s from two *different*
  nginx versions that are not ours. Escaping it (`\*`) survives the expansion.

Both are needed together; either alone leaves the other. Stdin redirection is
untouched by both, which is why the `-T -` chunked-upload check kept working —
a pipe is not a path.

The same mangling had already left a landmine: a file literally named `nul` in
the repository root, 26 bytes containing a demo response body, created when
`-o /dev/null` was rewritten to `nul` and landed as a real file rather than the
null device. git cannot index it at all (`short read while indexing nul`), so it
broke `git add -A` outright. Deleting it needs the `\\?\` device prefix, and it
is now in `.gitignore` so an accidental one cannot break staging again.

None of this is conformance work. It is recorded because every one of these
presented as a failing conformance check first, and the cost of misreading one
as a server finding is an afternoon spent in the wrong repository.

### (historic) The Debian curl leg is skipped, deliberately visibly

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

## 2026-09-22 — A gate, at last (A11)

Until this day nothing here ran automatically — no `.github` at all. The seven
harnesses and 257 checks happened when somebody typed the command, and the
HTTP/1.x tests that ship with Hermod were run by nothing in this repository:
`CLAUDE.md` documented the filter, and that was the whole of it.

Two legs, matching the siblings: `windows-latest`, and Debian 13 in a container
on an Ubuntu runner. `fail-fast` off, because "red on exactly one platform" is
the most valuable signal a two-leg matrix produces — in the HTTP/2 sibling that
signal turned out to be a real server bug rather than a platform quirk.

### The trailing dot in the filter is not cosmetic

Measured rather than assumed:

```
FullyQualifiedName~Hermod.Tests.HTTP     →  910 tests
FullyQualifiedName~Hermod.Tests.HTTP.    →  449
  … plus Tests.HTTPS.                    →  450 listed, 447 run
```

The obvious shorthand swallows `Hermod.Tests.HTTP2` and `Hermod.Tests.HTTP3` as
substrings and would gate the other two protocols under this repository's name.
`HTTPS` needs naming separately because `Tests.HTTPS.` does not contain
`Tests.HTTP.`. The HTTP/2 sibling learned this the expensive way, with a filter
that matched all 2085 tests in the assembly.

One runner for both legs. `run-tests.sh` is bash and runs under the Git Bash on
GitHub's Windows images, so there is no second implementation to drift.

### The step name counts harnesses, not checks

Deliberately, and it paid off the next day: the total is platform-dependent. The
curl matrix reported 58 checks against the Windows curl and 59 against Debian's,
so a hard-coded 257 would have been wrong on one leg immediately.

*Why* they differ went unexamined, and this comment is the only place the
asymmetry was recorded at all. Not chasing it down cost an hour the next day,
when a published figure had to be corrected twice — see the entry on the counts
below.

---

## 2026-09-22 — Autobahn against the server (A4, first half)

**481 / 517**, 36 declined, zero hard failures, against the demo host's
WebSocket echo server on `:8081`.

The widely-quoted "Autobahn 517/517" belongs to the HTTP/2 sibling, and what it
certifies there is `HTTP2/WebSocket` — six files, ~860 lines — driven through a
plain-TCP tunnel behind an HTTP/1.1 handshake, because Autobahn does not speak
RFC 8441. Hermod carries three separate WebSocket implementations and the
largest by far is this one: `HTTP1/WebSocket`, 24 files and ~12 000 lines, with
its own frame type and its own permessage-deflate. No foreign suite had ever
touched it.

The target needed no building. `Demo/Program.cs` has had that echo server all
along, with the comment "the Autobahn fuzzingclient target". It was never driven.

### What the first run found

All 301 non-compression cases passed before anything was changed, which is a
real result for code this size that nothing external had ever exercised.

Sections 12 and 13 — all 216 cases — came back `UNIMPLEMENTED`, because the demo
never offered permessage-deflate. `AWebSocketServer.EnablePerMessageDeflate`
exists and is documented "Disabled by default": the right default for a library
and the wrong one for a conformance target. One line in the demo took the score
from 301 to 481, and the existing suite was still 7/7 afterwards.

### The parameter I named wrong, and the conclusion I drew from it

The first write-up said `client_max_window_bits`. It is `server_max_window_bits`,
read off the wire from the `httpRequest` recorded in each case report rather than
inferred from Autobahn's case description, which calls it only
"requestMaxWindowBits":

```
13.3.1   permessage-deflate; … server_max_window_bits=9     UNIMPLEMENTED
13.5.1   permessage-deflate; … server_max_window_bits=9     UNIMPLEMENTED
13.4.1   permessage-deflate; … server_max_window_bits=15    OK
```

`server_max_window_bits=N` is the client capping the *server's* compression
window. `DeflateStream` exposes no `windowBits` control, so the server cannot
comply with 9, and RFC 7692 §7.1.2.1 *requires* declining an offer that cannot be
satisfied. `UNIMPLEMENTED` is Autobahn's word for "declined", not for "broken".

The comparison that went with it was backwards too. I had written that the HTTP/2
sibling "does accept 9", as though that were the better result. It accepted
anything whose value merely contained the string `permessage-deflate`, never
parsed the parameters, and would then compress with a 15-bit window against a
peer that had allocated a 9-bit inflate window. Autobahn does not catch it
because Python's zlib inflates with a large window regardless. 481/517 was the
stricter of the two results, not the weaker one.

That has since closed from the other side: the sibling was taught to parse the
offer, and its run now reports 481/517 with a breakdown identical to this one to
the digit — 476 `OK`, 3 `INFORMATIONAL`, 2 `NON-STRICT`, 36 `UNIMPLEMENTED`. Two
implementations written independently, 24 files against six, sharing no code,
converging on the same number against the same 517 cases. That says more than
either of them scoring 517 ever did.

### A floor, not a checkmark

Three buckets, because "not passing" is not one thing here:

```
passing    OK, NON-STRICT, INFORMATIONAL
declined   UNIMPLEMENTED — a refusal RFC 7692 requires rather than permits
hard       FAILED, WRONG CODE, UNCLEAN
```

The run fails on a single hard failure whatever the count says, or if passing
drops below the floor. So the floor can absorb a change in how many offers we
decline and can never launder a real failure into a pass. Excluding sections 13.3
and 13.5 to buy a green badge was the other option and is the worse one: an
exclusion hides the cases, a floor keeps counting them. The script also says so
when the number goes *up* — a floor nobody raises is a ratchet that has rusted.

### Diagnostics carried over rather than rediscovered

Four, taken from what the HTTP/2 sibling had to learn the hard way:
`PYTHONUNBUFFERED` so a hang names the real case instead of a flush boundary; a
run cap so a hang fails inside the script instead of eating the caller's whole
budget; `docker inspect` on the container's corpse to separate a kernel OOM kill
from anything else; and the demo host's own log kept beside the report.

Three of the four earned their keep. The fourth is the subject of the 12.4.18
entry below, because a log that is kept and empty is not a diagnostic.

*(The first nightly then died on line 3: the script had been committed mode
100644. Recorded because the failure looked like a harness bug for a minute and
was a file mode.)*

---

## 2026-09-23 — Autobahn against the client (A4, second half)

**445 / 517**, 72 declined, **zero hard failures**, on the first run this client
was ever pointed at the suite, with no change to the library.

The topology is the mirror of the server driver. There the suite connects to our
echo server; here the suite **listens** and `tests/autobahn-client/` — a driver
over Hermod's `WebSocketClient` — connects into it. The fuzzingserver protocol is
three kinds of connection: `/getCaseCount`, `/runCase?case=N&agent=A`, and
`/updateReports?agent=A`. A case is one connection, and "the case ended" is "the
server hung up".

### A4 was half done, and I called it done

Recorded because it is the failure this repository keeps catching in its own
numbers. Earlier that day I marked A4 complete in `CLAUDE.md` on the strength of
the server half alone, and dropped "server & client suites" from the README's
coverage row while replacing its stale figures. Two places then claimed a
finished track of which half had never been started, while `PLAN.md` still said
"A4 next" in two other places — the same repository disagreeing with itself in
both directions at once.

The half that was missing was the more interesting one, not the leftover:
481/517 certifies `WebSocketServer`, and `WebSocketClient` is part of the same
24-file subsystem, is not exercised by this repository's demo either, and was
therefore covered by no foreign suite at all.

### The 72 declines are not the server's 36

They are sections **13.3, 13.4, 13.5 and 13.6**, eighteen cases each, and they
are declined by the **suite**, not by us. Those sections expect a client offer
carrying `server_max_window_bits`; ours is the fixed constant
`WebSocketPerMessageDeflate.ClientOfferHeader`, which never carries it, so the
suite answers with no `Sec-WebSocket-Extensions` at all and the compression they
wanted to exercise is never negotiated. 13.1 and 13.2 expect no such parameter
and pass; 13.7 offers a list with one entry that carries none, and that entry
matches.

Nothing is wrong on the wire. The server's 36 and the client's 72 are the same
fact — `DeflateStream` exposes no window-size control — seen from its two
opposite ends: a refusal the RFC requires, and an offer we do not know how to
make.

### Two false leads, both in the measuring apparatus

Worth the space, because neither was in the thing being measured:

- The driver reported **HTTP 408** on the very first handshake, which reads
  exactly like a client bug. With `-p`, `docker-proxy` binds the host port the
  moment the container is *created* — seconds before `wstest` has loaded its spec
  and started listening inside — so a plain TCP connect succeeds against nothing.
  The readiness probe now performs a real handshake and waits for a 101.
- That probe then reported "never came up" against a server whose own log said it
  was ready. Autobahn answers a `Host` header without a port with
  `400 missing port in HTTP Host header`, and a probe looking only for 101 reads
  that as not-ready until it gives up.

### `OK` and `NON-STRICT` trade places between runs

The first local measurement was 440 `OK` / 3 `INFORMATIONAL` / 2 `NON-STRICT`;
the nightly reported 430 / 3 / 12. Both sum to the same 445 passing. The split
moves with timing, the total does not — worth knowing before someone reads a drop
in `OK` as a regression.

---

## 2026-09-23 — The 12.4 stalls, and a fix I cannot claim

Two red nightlies in a row on the client job, neither of them about conformance.

### The deadline was tighter than the thing it was driving

The first: case 500 — 13.7.1, a thousand compressed messages — was still
connected after the driver's 30 s deadline, so the driver hung up on it. That
forced disconnect left the fuzzingserver unable to serve the seventeen cases
after it, `/updateReports` wrote nothing, and the run reported no result at all.
**One case over its deadline cost 517 cases their report.**

30 s was never defensible: every case in sections 12 and 13 says "Timeout case
after 60 secs" in its own description, so the driver's deadline was tighter than
the thing it was driving, and a slow case could only ever be reported as a stall.
It is 120 s now, bounded from below by the suite rather than picked. Locally all
517 had finished with zero stalls, which is why this reached CI at all: a deadline
that is wrong by construction still passes wherever it happens to be generous
enough.

Two smaller fixes went with it — a stalled case settles for two seconds before the
next one starts, and `--first`/`--last` clear the floor, since a slice cannot
reach a number that counts the whole suite.

### Then it failed at 120 s, which rules out the deadline

Cases 12.4.12, 12.4.14 and 12.4.17 each sat past 120 s. They take 1.1 s, 2.1 s and
3.5 s on this machine, and all eighteen cases of section 12.4 run in 37 s. A
hundredfold gap is not a slow runner; it is a hang that a fast machine races past.
The first failure stalled at 13.7.1 and the second at three cases in 12.4, so it
moves: timing, not a case.

### The mechanism, and what was not established

The echo no longer happens inside the receive handler. It goes into an unbounded
`Channel` drained by a task of its own.

The old shape could deadlock by construction, and the comment above it asserted
the opposite — that awaiting the send inside the handler was what kept ordering
right. The read loop awaits that handler, so a blocking send parks the reader.
With a thousand messages of up to 128 KiB in flight the peer's receive buffer
fills while it is still sending; our write blocks on a socket nobody drains, and
we cannot drain theirs because we are parked inside the handler. A channel keeps
the order the handler saw and takes the send off the read path. Unbounded is
deliberate: bounding it would push the blocking back into the handler and rebuild
the deadlock one layer up.

**It did not reproduce.** Section 12.4 was run pinned to a single core with
`taskset -c 0`, against both the new code and the old, and both passed 18/18 in
~38 s. So the deadlock is a mechanism that fits the evidence, not a diagnosis that
was demonstrated. Calling it "the fix" would claim more than was measured.

So the branch that gives up on a case now prints what it was holding:

```
case N: received 1000, echoed 1000, queued 0
```

`received` far ahead of `echoed` means the send side is wedged, which is the
hypothesis above. The two close together means we are waiting on a peer that
stopped talking, which is a different bug entirely. The next occurrence decides
it instead of leaving it open.

In CI the stalls went from three to zero and the floor held exactly at 445. That
is the outcome, and it is still not the proof.

---

## 2026-09-23 — H-1 and H-2, the first Track B fixes upstream

Two findings fixed in Hermod rather than merely reported, shipped as
[Hermod#29](https://github.com/Vanaheimr/Hermod/pull/29) and pinned here the same
day. Track B had stood at 23 findings and none fixed since it was written.

### H-1: a status line that would have read `425 No code`

`HTTPResponseBuilder` builds the status line as
`{ProtocolName}/{ProtocolVersion} {Code} {Name}`, so `Name` is the reason phrase
**on the wire**. 425 was defined as `NoCode = new (425, "No code")`.

RFC 8470 assigns 425 to Too Early, and Hermod's own HTTP/2 stack already answers
425 for exactly that reason — it simply never went through `HTTPStatusCode` to do
it, which is how the misnomer survived. Renamed; no use of `.NoCode` exists
anywhere under `D:\Coding\Vanaheimr`, checked before breaking the API.

Nine codes added — 102, 103, 208, 226, 308, 421, 451, 508, 511. The finding named
seven; 102 and 226 came along because the goal is testable only as a set. After
this, every code in the IANA registry resolves to a defined field rather than to
the synthesized fallback, and "seven of nine" would have meant a coverage test
with two hand-written exceptions in it.

And a second defect, found by reading the file rather than by looking for it:

```csharp
public Boolean IsNotSuccessful
    => Code < 200 && Code >= 300;
```

No number satisfies both, so the property was a constant `false` for every status
code, 404 and 500 included. Nothing in these repositories calls it, which is why
it survived.

### H-2: the finding was half wrong

It read "no content coding for HTTP/1 bodies — neither client nor server
compresses or decompresses". The first half is not true: `SinglePageAppHandler`
has compressed static files on the fly for a long time, with `Vary` and
coding-specific entity tags. What was genuinely missing was **decoding**, in
either role.

`HTTPContentCoding` moved from `HTTP2/Core/` to `HTTP/General/`. It was already
being used from the shared tree — `SinglePageAppHandler.cs` carried a
`using …Hermod.HTTP2;` for no reason other than to reach it, which is the shared
layer depending on one version's layer. It is now wired into `AHTTPPDU`, so a
gzipped request body a handler receives and a gzipped response body a client
receives take the same path.

Three decisions worth naming:

- The coding list is undone in **reverse**. `Content-Encoding: gzip, br` means
  gzip was applied first and Brotli to its output (RFC 9110 §8.4). With one
  coding — every case in practice — the two orders are indistinguishable, which
  is why it is a test and not a comment.
- An unsupported coding is **refused** rather than passed through. Returning
  encoded octets as if they were the representation is the one answer that would
  actually be dangerous.
- The 64 MiB ceiling bites *during* decompression rather than after, or the
  memory is already gone by the time anyone looks at the size.

### Verified by breaking it, one assertion at a time

Fifteen tests. Reverting the `&&`, renaming Too Early back to "No code" and
moving 508 to 5080 fails six of the seven status-code tests; the seventh is the
duplicate-code test, which none of those three breakages should trip, and does
not. Walking the coding list forwards, dropping the `identity` filter and
ignoring the caller's ceiling each fail exactly one content-coding test and no
others. Restored: 1016/1016 across `Hermod.Tests.HTTP*`, which includes the
HTTP/2 tests that use the moved file.

What is still open is written down rather than left to be discovered: the client
offers no `Accept-Encoding` of its own and does not decode a *streamed* response
body, and there is no general server-side compression filter outside the SPA
handler. The streamed half belongs inside `HTTPBodyStream`, not after it, and
that path carries chunked, SSE, close-delimited and keep-alive cases.

---

## 2026-09-23 — 12.4.18: the server drops, and the log says nothing

The nightly's server job went red for the first time, on case **12.4.18** — "send
1000 compressed messages each of payload size 131072", the heaviest case in the
suite at 128 MiB of payload each way. Three other runs the same day passed it.

The report, read rather than summarised:

| | |
|---|---|
| `behavior` | `FAILED` |
| `duration` | 1504 ms — locally the same case passes in ~3700 ms |
| `txFrameStats` | `{"1": 717}` — the suite sent 717 text frames |
| `rxFrameStats` | `{"1": 716}` — it got 716 back, and **no** opcode 8 |
| `droppedByMe` | `false` |
| `wasNotCleanReason` | "peer dropped the TCP connection without previous WebSocket closing handshake" |

We echoed 716 of 717 messages correctly and the connection was then simply gone,
no close frame, about 40 % into a case that normally finishes. A drop, not a hang
— which is what rules out the read loop merely being slow.

### The instrument was there and switched off

`demo-host.log` was in the artifact. It is listed among the four diagnostics
carried over in the A4 entry above, and `tests/TestingAgainst_Autobahn.md` calls
it "the only view of a failure from our side of the wire". It contained the
startup banner and nothing else.

Not because nothing went wrong. Hermod's servers take an `ILoggerFactory` and
default it to `NullLoggerFactory.Instance`. Exactly two code paths can end that
read loop without a close frame — `AWebSocketServer.cs:1211` at Debug ("Read
error on WebSocket connection") and `:2213` at Error ("Exception in HTTP
WebSocket server connection loop") — and both formatted their record into a null
sink.

A file that exists, is collected, is named in the documentation and is empty of
everything that matters is worse than no file: it answers "did we keep evidence?"
with yes.

### Forty lines and a flag

`Demo/ConsoleLogger.cs` — an `ILoggerFactory` writing to the console, no package,
in the spirit of the HTTP/2 demo's `ConsoleEventListener`. `--log[=<level>]`
passes it to all three servers, and `tests/autobahn.sh` starts the demo with
`--log=debug`. Debug rather than Warning because the read-error record is a Debug
record, and it is cheap: the WebSocket server has two Debug statements in total
and logs nothing per frame. The demo prints one line stating that logging is on,
because "was the instrument even switched on" is a question a silent log cannot
answer.

### The proof that first looked like a failure

A two-case slice produced no records at all, which looked exactly like broken
wiring. It was not: the Debug line that seemed guaranteed — `RemoveConnection`,
"Removing HTTP WebSocket connection with …" — sits on a method **nothing calls**.
A passing case logs nothing because a passing case has nothing to say.

The real proof is a full 517-case run: **128 warning records** where there had
been none, every one a correct refusal of a protocol violation the suite commits
deliberately. CI agrees to the digit — the next nightly's artifact carries 151
lines against the failing run's 22, and the same 128 records. The same cases
provoke the same number of refusals on two different machines.

### Reproduction: 14 attempts, all green

Ten runs of section 12.4 with the demo host pinned to two CPUs, harsher than the
runner's four: 18/18 every time, not one record. Three full 517-case runs
likewise squeezed, of which one was spoiled by a port collision with a slice
started alongside it and is not counted. One full run unconstrained. The first
nightly with the instrument on.

12.4.18 is **not diagnosed**. What changed is that the next occurrence names
itself — the same move the HTTP/2 sibling made with `PYTHONUNBUFFERED` after two
wrong diagnoses drawn from a flush boundary, and for the same reason: an
intermittent failure is worth one instrument, not three hypotheses.

---

## 2026-09-23 — Bookkeeping that turned out to be work

Three corrections that read like tidying and were not.

### A11 had been finished for a day and marked open

Its section asked for "build + Hermod HTTP/WS suites + `run-tests.ps1` + curl
matrix", and I read the absence of a curl-matrix *step* in `ci.yml` as the matrix
not running. It runs: `run-tests.sh` calls `curl-matrix.sh` itself, three times,
and `ci.yml`'s own comment puts it at 61 of the Debian leg's 262. There was
nothing to build.

What that workflow does lack is the proxy, smuggling and browser jobs the section
listed — but those are A5, A6 and A8, which do not exist to be run. That is a
dependency, not unfinished CI work. It also asked for a `run-tests.ps1`, which
this repository has never had.

### Numbers that nothing checked

Every published NUnit figure had drifted, and for one reason: none of them said
which filter produced it, so none could be checked. Re-measured, each by running
the filter in its own row, then measured again as a check:

```
~Hermod.Tests.HTTP.                         536  →  551
~Hermod.Tests.HTTP. + ~Hermod.Tests.HTTPS.  537  →  552   (what CI gates on)
~Hermod.Tests.HTTPS.                          -  →    1
the regression selection                    299  →  319
~Hermod.Tests.HTTP.WebSockets                49  →   49
```

The single test in `Tests.HTTPS.` is the whole of the off-by-one that had made
this repository's table and a CI log disagree for what looked like no reason.

The regression selection is the interesting one: +20, of which only 15 are new
tests. The filter itself gained two files, one of which brought five pre-existing
tests into a selection they had never been in. Run the five-file filter the old
figure named and it still answers 299. The README says so in a paragraph rather
than letting a reader conclude that twenty tests were written.

The table gained a **"Measured by"** column. That is the actual fix; the numbers
are this week's value of it.

### 308, and a stale build that nearly sold a green

H-1 gave 308 a status code, so `/redirect/{code}` gained it. Worth being precise
about what that did: 307 and 308 are identical on the wire from a server's point
of view — both preserve the method and body — and differ only in permanence. The
pair that really differ in method handling are 301 and 308, and that difference
lives in the client that follows. So this adds a code for the drivers to see, not
behaviour.

Verified by deleting the mapping again and rebuilding: `h1semantics` 64/65, curl
59/60. Exactly one of each pair fails, which is the honest result — the `Location`
field and the `-L` follow both survive a fallback to 302, so those two checks
assert properties rather than the code.

That deliberate breakage produced a false green first. The rebuild was chained
behind `grep -c … &&`, grep found zero matches, returned 1, the build never ran,
and the harnesses passed against the old binary. Seventh time this pattern has
cost time in this project, and the reason it keeps working is that everything
about the output looks right.

### And then the curl matrix turned out to have three counts

An hour after publishing "60/60 per build", CI answered 61 on the Debian leg and
60 on Windows. Two of the matrix's checks are conditional, one per block:
`--http1.1 honoured by an HTTP/2-capable curl` needs a curl built with nghttp2,
and `curl stored the ETag` needs a local target so `--etag-save` writes somewhere
the script can read. Fifty-nine always run:

| Context | HTTP/2 | local target | curl | suite |
|---|---|---|---:|---:|
| Windows / Git Bash, and the CI Windows leg | no | yes | 60 | **261** |
| the Debian curl through WSL (`--wsl`) | yes | no | 60 | — |
| the CI `debian:13` container | yes | yes | 61 | **262** |

The first two agree at 60 **for opposite reasons**, each gaining one check and
losing the other. Two identical numbers that mean different things are exactly
what someone reconciles into one, and then the reconciliation is the bug.

There was no defect, and that is worth recording too. The suspicion was that
`HAS_H2` is detected before `--curl` is parsed, which would mean the WSL Debian
leg silently skipping the one check it exists to provide. Parsing is at line 24,
detection at line 49, and running the detection by hand against
`wsl -d Debian -- curl` answers yes. The harness was right; the documentation was
not. The one place that had recorded the asymmetry all along was `ci.yml`'s own
comment from 2026-09-22, and it is the only reason the mismatch was noticed.

---

## 2026-09-24 — The job that watches Hermod master, and what it is for

Both sibling repositories have carried an `against-hermod-master` nightly job for
a while; this one did not, and it is the repository where the gap costs most:
Track B's whole workflow ends in "bump the pin", and nothing here asked whether
the pin was safe to move onto.

The gap showed the same day. Hermod master had taken a breaking rename —
`HTTPStatusCode.NoCode` → `TooEarly` — and a type had moved namespace, both from
work driven out of *this* repository, and nothing here would have said a word if
either had broken it. What existed instead was me grepping for the old names by
hand.

One leg, `windows-latest`. The question is whether a library change breaks this
repository, which is not platform-specific, so a second leg buys a second copy of
the same answer; the only check the Debian leg has and this one does not measures
curl rather than Hermod.

### Not a copy of the sibling's job, and that is the interesting part

The HTTP/3 version builds `--configuration Release` and then runs its harnesses
with `--no-build`. That works there because its `tests/run-tests.sh` reads
`bin/Release`. This repository's runner reads `bin/Debug` (`run-tests.sh:106`), so
the same two steps would have failed with "Demo host not built" — a job red for a
reason that has nothing to do with Hermod, which is precisely what this job must
never be. Checked before copying rather than after the first red night.

Green on its first run, and the three nightlies that day (this one, HTTP/2's and
HTTP/3's) all passed `against-hermod-master`, which answered the question the
merge had opened.

---

## 2026-09-24 — RFC 7616 Digest (H-3), by moving the framework where it belonged

**H-3** read "`HTTPDigestAuthentication` is not RFC 7616 — it is
`Digest base64(user):base64(secret)`". True, and worse than it reads: a password
in the clear under the name of the one scheme whose entire purpose is not to send
one, and the type's own documentation called the second field "a time-based
one-time password", which it was not either.

It was also completely dead — referenced by nothing, not even the `Authorization`
dispatcher, in any of the ten Hermod checkouts on this machine.

### Why it was never just "write RFC 7616"

A correct implementation already existed, three directories away and unreachable.
`HTTP2/Auth/DigestAuthenticationScheme.cs` has done stateless signed nonces,
`qop=auth` with `nc`/`cnonce`, the legacy RFC 2069 form, `-sess`, SHA-256 and MD5
for a long time. What kept HTTP/1.x from it was *where it lived*.

So the RFC 9110 §11 framework moved to `HTTP/Authentication/` — the scheme
interface, four schemes, the authenticator, the credential parser, the identity.
It is version-independent by construction, and `HTTPAuthenticator`'s own summary
said so ("this is version-independent (RFC 9110), so it lives in the shared
library") while sitting under `HTTP2/`. One file stayed behind, the HTTP/2 wiring.
1016/1016 immediately after the move, before anything else was touched.

### Three things the demo's routes were forced into

curl 8.21 answers a `WWW-Authenticate` field only when it carries **exactly one
challenge**. Measured, all five combinations: `Digest(MD5)` alone → 200;
`Digest(SHA-256)` alone → 401 with no `Authorization` sent at all;
`Digest(SHA-256), Digest(MD5)` → 401; the same reversed → 401; `Digest(MD5),
Basic` → 401. RFC 9110 §11.6.1 permits several challenges per field and observes
in the same breath that parsing them is ambiguous, because auth-params are
comma-separated too.

So Digest could not join `/secret`: it would have broken the `--anyauth` check
that passes there today. It lives at `/secret/digest` (SHA-256) and
`/secret/digest-md5`, two routes because the algorithm is the point — publishing
one would hide either the capability or the gap.

And the gap is one build's, not ours: the Debian curl the `--wsl` leg drives is
8.14.1/OpenSSL and authenticates with **SHA-256**, 200. That is the load-bearing
measurement — a foreign implementation computes the RFC 7616 response and this
server recomputes it. The curl matrix's SHA-256 check is therefore a third
conditional, *predicted* from the TLS backend in the version banner and then
asserted both ways: a probe that read the outcome and asserted it would agree
with whatever happened, which is not a check.

### A test that could not fail

Three deliberate breakages should have failed three tests and failed two. The
nonce assertion passed against the broken code, because the nonce is
`base64(ticks:HMAC(secret,ticks))` and two calls inside the same tick return the
same value — "one nonce shared" and "two minted simultaneously" were
indistinguishable. A `TimeProvider` that advances one tick per read separates
them, and the third breakage then failed as it should.

---

## 2026-09-24 — H-25: the TCP Warden was killing connections that were alive

The nightly of 02:37 failed case **12.5.15** — not 12.4.18, the same signature to
the letter. And this time the log added the day before turned it into an answer.
Three lines, one socket:

```
02:34:56.847   case 12.5.15 starts
02:35:00.198   dbug  Read error on WebSocket connection 127.0.0.1:41346.
               System.ObjectDisposedException: NetworkStream
                 at WebSocketServerConnection.ReadAsync  :706
                 at AWebSocketServer.RunConnectionAsync  :1203
02:35:00.210   dbug  ATCPServer: Cleaned up stale client 127.0.0.1:41346.
```

`ATCPServer`'s Warden decided what to reap by asking
`TCPConnection.IsConnectionClosed()`:

```csharp
socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0
```

the textbook "has the peer vanished" idiom, and a race against the connection's
own reader. `Poll(SelectRead)` is true when the socket is readable — data arrived
*or* the peer closed — and `Available` separates the two. The read loop drains the
socket in between; the Warden sees "readable, nothing available" and closes a live
connection.

Every property followed and none had to be guessed: it needs a busy reader
(section 12 sends a thousand large compressed messages), it is the gap between two
statements, it lands mid-case because the Warden runs on its own timer, there is no
close frame because the socket is gone before the loop notices, and it was silent
because the record is a Debug one that used to go into a `NullLoggerFactory`.

**Wider than WebSocket:** `AHTTPServer : ATCPServer` too, so every long-lived
HTTP/1.1 connection was exposed — a large download, a keep-alive connection
mid-body, an SSE stream. Autobahn found it because section 12 keeps a reader
busier than anything else here runs.

### The fix asks something that cannot race

The Warden already held the answer and awaits it two lines further down: the
handler task. Completed means finished; running means owned, and the owner
notices a vanished peer — `AWebSocketServer` by ping, `AHTTPServer` by its idle
and Slowloris deadlines. Both know what their protocol expects; a timer looking at
a socket does not. The trade is in the safe direction.

### The verification that had to be thrown away, twice

Forcing the Warden to a one-second period — some five hundred chances per run
instead of eight — and running sections 12.4 and 12.5 gave **one** hard failure in
four runs of the old code and none in four of the new. One in four is the base rate
the nightly already had; four clean runs prove nothing against it.

The attempt before that was worse than inconclusive. The two variants were copied
from Git Bash's `/tmp` while the script ran in WSL, so every `cp` failed, six runs
of one build were labelled three-and-three, and all six came back clean. It was
caught only because `cp` printed its errors to the same log. The rerun prints the
md5 of the source file it installed on every line — a label that carries its
evidence rather than asserting it.

So the verification is a test that asks the predicate directly instead of hoping a
517-case suite trips it. `HermodTests/TCP/ConnectionLivenessTests.cs` stands up a
real `TCPEchoTestServer`, lets a peer flood it so the handler is genuinely reading,
and samples `IsConnectionClosed()` until it lies. **It lies in 160–250 ms, five runs
out of five** — and it asserts the peer was still connected, because a "closed"
reading on a connection that had really closed would prove nothing.

It asserts a defect on purpose, the way the curl matrix pins an expected failure.
If it ever fails, the predicate stopped lying.

### And two more in the same corner, deliberately left

One defect per commit. `ATCPServer` registers its check as `EveryMinutes(1, …)` and
ignores the `WardenCheckEvery` property it documents — which is exactly why the
reproduction above needed a source edit rather than a constructor argument — and
`Warden.EverySeconds(N, …)` tests `timestamp.Minute % N` rather than `Second`, so it
has never done what its name says. Its only caller was the line briefly written
during this work. **H-26.**

## 2026-09-24 — H-16, and a finding that outlived what it described

**H-16** read: "General HTTP server has no `Upgrade` dispatch — WebSocket is a
separate listener. Blocks a `/ws` route on the main demo port." The estimate was
M, and it sat at P2 for weeks.

It was already done. Hermod grew `WebSocketUpgrade.For(...)` on **2026-09-16**,
commit `3bc56fdb`, "A WebSocket can live on an HTTP path", complete with
`HermodTests/WebSocket/WebSocketOnAnHTTPPathTests.cs` and a usage example in its
own doc comment. The row described the state of a pin that had not moved since
2026-08-13; the bump of 2026-09-23 brought the fix in, and nobody re-read the
finding.

That is the third time this week — after A11, which had been complete for a day
while marked open, and after H-2, half of whose text was wrong about what the
code did. The lesson is cheap and worth writing down: **a pin bump is not
finished until the findings it might have closed have been re-read.** 182 commits
arrived on 2026-09-23 and the Track B table was not part of what that change
touched.

### What was actually left

The demo's own route, which is the A1 item the finding blocked. Three lines:

```csharp
API.AddHandler(HTTPMethod.GET,
               HTTPPath.Root + "ws",
               HTTPDelegate: WebSocketUpgrade.For(upgradeWebSocketServer));
```

The lent server is a second `WebSocketServer` instance with `AutoStart: false` —
not the one on `:8081`, because that one is started and the contract says the lent
one must not be. It never accepts anything; the HTTP server borrows its protocol
for one path, and the handshake itself stays in the WebSocket server's single
implementation of RFC 6455 §4.2.1. Two copies of a security negotiation is how the
older one ends up a version behind.

`ConfigureAPI` runs once per listener, so the upgrade is reachable over cleartext
and over TLS without saying so twice.

### Verified from outside, three ways

**The handshake, deterministically.** RFC 6455 §1.3 works its example through:
`base64(SHA-1(key + GUID))` for `dGhlIHNhbXBsZSBub25jZQ==` is
`s3pPLMBiTxaQ9kYGzzhZRbK+xOo=`. That exact value came back with the 101, which
makes it a check of the handshake rather than of the status line.

**The frames, by a foreign suite.** `tests/autobahn.sh` gained a `--ws-path`, and
`--ws-port 8080 --ws-path /ws` pointed the fuzzingclient at the upgraded path:
sections 1, 2 and 7 — framing, ping/pong, close handling — **64/64**. Real frames
through a connection that began as HTTP.

**The refusals.** A plain `GET /ws` is **426**, not 404: the resource is there and
the protocol is wrong (RFC 9110 §15.5.23). `Upgrade: websocket` *without* the
`Connection` token is also 426, which is the strict reading of RFC 6455 §4.1 and
the one that stops a stray header from switching protocols by accident.

Four of those are now curl-matrix checks, so they run on both transports and both
curl builds. Deleting the route again fails exactly those four and nothing else.

The gate goes 266 → **270**, `--wsl` 331 → **339**, curl 65 → **69** per build.

## 2026-09-24 — H-2, the other three quarters

H-2 was estimated **S** and read "no content coding for HTTP/1 bodies". The first
pass, on 2026-09-23, made `AHTTPPDU` decode a body it already held, and the row
was left at 🔶 with an honest note about what remained. What remained was three
more fixes, each larger than the one that had been done.

### Decoding a body that is not an array

`DecodeBody(...)` works on `HTTPBody`. A chunked, close-delimited or
event-stream body is not `HTTPBody` — it is a stream that has not finished
arriving, and buffering one in order to decode it undoes the reason it was
streamed. So `TryDecodeBodyStream(...)` puts the reversal *inside*
`HTTPBodyStream`, where everything downstream gets it for free.

Two field lines have to go with it, and the second is not a tidiness question:

- `Content-Encoding`, because the body is the identity representation from there
  on. What it arrived as is kept in the new `DecodedContentEncoding`, so nothing
  is lost.
- `Content-Length`, because it counted the **encoded** octets — and the
  buffering loop in `TryReadHTTPBodyStreamAsync` stops reading at it. Leave it in
  place and a gzip body is truncated at its compressed size: a silently short
  body, which is the worst failure mode on the menu. There is a test that builds
  64 KiB of text, checks that it really did compress by a factor of ten, and then
  requires all 64 KiB back.

`RawHTTPHeader` keeps both, deliberately. It is the record of what came off the
socket, which does not change because we decoded it — and since the parsed view
and the raw text now disagree on purpose, a test pins the disagreement rather
than leaving it to be discovered by somebody grepping a log.

Four smaller things surfaced while doing it, and each is the kind that hides:

- **Wrapping a stream hides what it is.** The buffering loop recognises a chunked
  body by the *type* of the stream in order to collect its trailers, so a decoder
  in front of it costs the trailers. The chunked stream is remembered before it
  disappears. Reverting that one line fails one test, and only that one.
- **`RemoveHeaderField` removed nothing.** It dropped the raw field and left the
  parsed copy in the cache that `GetHeaderField` consults first — so the field
  vanished for anything reading the header text and stayed for everything reading
  the typed property. It had had no callers until this.
- **`InvalidDataException` is not an `IOException`.** A body that is not valid for
  its declared coding reached the catch-all at the bottom of the read loop, and
  the caller got a null body with no reason for it.
- **The decoders disagree about their own exception.** gzip, zlib and deflate
  raise `InvalidDataException`; `BrotliStream` raises `InvalidOperationException`
  for the identical condition. `ContentDecodingStream` translates it, so "this
  body is not what it said it was" has one answer. That class also holds the
  deflate sniff — RFC 9110 names zlib, much of the web sends raw — which a stream
  cannot do at construction time, because the two bytes it needs have not
  necessarily arrived.

### A bound that a well-formed chain cannot justify

Every decoding step is bounded separately, not just the last one. That looks
redundant, and against an honest peer it is: `Content-Encoding: c0, c1` means the
intermediate is `c0` applied to the final output, so with a real compressor the
intermediate is never the bigger of the two. Nothing obliges a peer to be honest.

A gzip member made of empty stored deflate blocks (RFC 1951 §3.2.4 — five bytes
each, no output) is four megabytes that decode to nothing, and it compresses to
almost nothing itself. Wrapped in a second gzip layer it is a few kilobytes on
the wire, four megabytes in the middle, and zero bytes at the end: a ceiling that
watched only the final output would see an empty body and be satisfied. The test
builds exactly that member by hand, in a dozen lines, and bounding only the
outermost layer fails it and nothing else.

### The client asks

`AutomaticDecompression`, off by default — it changes what goes out on the wire
and what the caller gets back, which makes it the caller's decision. Named after
the HTTP/2 client's option, because it is the same decision. Never written over a
caller's own `Accept-Encoding`: `identity` is how compression is switched off for
one request, and a client that overwrote it would make that impossible to say.

It decodes in three places, because a body arrives in three shapes and only one
is an array. The ordinary case wraps the stream *before* the body is read, so the
buffered and the streamed path are one implementation. An event stream has no
alternative — it is not supposed to end, so there is no later. And a chunked body
consumed the moment it arrives was already an array before anything could wrap
it, so that one takes `TryDecodeBodyInPlace`, which corrects `Content-Length`
rather than dropping it, the length being known there. Two routes to one result
is the arrangement that drifts, so both are driven from outside — and the test
tells them apart by exactly that field.

The client tests run against a bare `TcpListener` rather than a Hermod server,
because what is under test is what the *client* does with a response, including
framings no well-behaved server would produce. A gzipped event stream is not
exotic: it is what nginx puts in front of one.

### The server offers

`AutomaticContentCompression`, off by default, on `AHTTPServer` — so every
handler in the process gets it without knowing about it. `SinglePageAppHandler`
has compressed static files all along and keeps doing it the better way,
compressing each once at startup rather than once per response; a response that
already carries a coding is left alone.

The list of things it declines to compress is longer than the compressing, and
every entry is a way to be **wrong** rather than merely slow: a body still being
streamed, a chunked response whose length must not be restated, a handler's own
coding, a `206` (a range is part of the selected representation; a coding applies
to the whole of it), an already-compressed media type, anything under a kilobyte,
a `q=0` refusal, and a result that came out no smaller.

Three fields follow the octets. `Vary` gains `Accept-Encoding`, merged rather
than overwritten, or a cache hands gzip to a client that never asked. A strong
`ETag` gains the coding, because encoded and identity are two representations and
a range request against a cached identity copy must not be answered out of the
compressed one. And `HEAD` is compressed exactly like `GET`: there is no body to
send, but §9.3.2 asks for the fields a `GET` would have sent, and
`Content-Length` is the field a `HEAD` is usually asked for.

It rebuilds the response rather than editing it. An `HTTPResponse` serialises
from the header text it was built with, so a field changed afterwards reaches the
log and not the wire — which makes "did everything else come along" the thing
most likely to break, so a header nothing else cares about is asserted on the far
side.

### The thing nineteen unit tests missed and six wire checks caught

The filter went in green: nineteen tests, and a falsification pass showing that
turning it off failed the seven asserting compression and none of the twelve
asserting restraint. Then `tests/run-tests.sh` reported **5/7**, with `/chunked`
and `/trailers` answering `200` with no `Transfer-Encoding`, no chunks and no
trailers.

`ShouldCompress` asked "is there a body worth compressing" before "is this
response still being written". Both answers were right. The first question
consumed the response.

`HTTPBody` is not a field. It is a property that *makes* the body an array if it
is not one yet, by draining `HTTPBodyStream` to the end and then running
`CloseActionAfterBodyWasRead`. For a live chunked response or an event source
that stream is the connection, and the worker that was about to write to it found
it gone.

So the order of the checks is correctness rather than arrangement now, and the
code says so: everything decidable from the header is decided first — the body
stream, `text/event-stream`, an upgrade worker, the status, chunked, an existing
coding, a `206`, the media type — and the body is not looked at until looking is
harmless. Two of those guards are new rather than moved: `text/event-stream` is
`text/*`, so the media-type test would have waved an event source through, and an
`UpgradeWorker` means what is on this connection has stopped being HTTP.

The interesting part is not the bug. It is that nineteen tests could not see it,
because every one of them handed the filter a response whose body was already an
array — which is the only shape you reach for when you are writing unit tests for
a compression filter. The harnesses drive a real demo over a real socket, and a
route that stops being chunked is visible there and nowhere else. The regression
test that now exists therefore asserts the thing that was wrong rather than the
verdict, which was right all along: it hands `ShouldCompress` a body stream that
counts its reads, and requires the count to stay at zero.

### Numbers

Hermod's gate filter 562 → **611**; `Tests.HTTP.` 561 → **610**; the HTTP/1
regression selection 329 → **372**, the three new fixtures joining the eight it
already named. Six of those are not this work: `SSEProxyTests` landed on Hermod
master while #32 was open, and the merge brought it along. Measured before the
merge the gate read 605, and publishing that would have been a figure that was
true of a branch and of nothing else. This repo: gate 270 → **279**, `--tls` **279**, `--wsl` 339 →
**357**, curl 69 → **78** per build (79 in the CI Debian container).

One number here was wrong before this touched it and is worth naming: the demo
was described as having "14 routes" in two places, and had eighteen. The `/ws`
route of 2026-09-23 had not moved it either. Nothing measures that one, so it is
corrected rather than claimed.

The demo gained `/prose`, because `/large` is `application/octet-stream` and
rightly stays identity however hard a client asks — the filter decides on the
media type, not on how well the bytes would happen to compress.

Track B: **26 findings, 5 fixed upstream** — and all five whole.

## Next

**A5–A8, the remaining third-party suites** — intermediary interop, request
smuggling / differential fuzzing, non-.NET reference peers, browsers. Each brings
its own nightly job; the workflow has room for them and the demo already binds
`0.0.0.0` on demand, which is what A5 and A6 were waiting for. Then A9
(benchmarks) and A10 (parser fuzzing).

**Track B: 26 findings, 5 fixed upstream, all of them whole.** H-1; H-2 as of
2026-09-24, which took four fixes against an estimate of one; H-3 whole, and it
moved the RFC 9110 §11 framework into the shared library on the way; H-16, which
had been fixed upstream for a week before anybody re-read the row; and H-25, the
Warden killing live connections.

What that leaves, roughly by value: **H-26** (the two ways the Warden's own
scheduling does not do what it says — `EveryMinutes(1, …)` ignores
`WardenCheckEvery`, and `EverySeconds` measures in minutes), **H-24** (six reason
phrases that predate RFC 9110, a decision rather than a fix), and nineteen more
Track B findings. Then A5–A10.

**Two lessons from this week are worth keeping in view, because both cost real
time and both are cheap to avoid.**

*A bump is not finished until the findings it might have closed have been
re-read.* H-16 read "the general HTTP server has no `Upgrade` dispatch" while
Hermod had grown exactly that on 2026-09-16, with its own tests. The row
described the state of a pin that had not moved since 2026-08-13; the bump of
2026-09-23 brought the fix in and nobody looked. Third time in a week a finding
outlived the thing it described, after A11 and after H-2's first half.

*Unit tests reach for the shape that is easy to construct.* Nineteen tests for
the server-side compression filter all handed it a response whose body was
already a byte array, which is the only shape you naturally build in a test —
and the defect was in the path where the body is still a stream. The wire
harnesses found it on the first run. That is what they are for, and it is an
argument for running them before a feature is called done rather than after.
