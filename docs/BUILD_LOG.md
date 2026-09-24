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

## Next

**A5–A8, the remaining third-party suites** — intermediary interop, request
smuggling / differential fuzzing, non-.NET reference peers, browsers. Each brings
its own nightly job; the workflow has room for them and the demo already binds
`0.0.0.0` on demand, which is what A5 and A6 were waiting for. Then A9
(benchmarks) and A10 (parser fuzzing).

**Track B: 25 findings, 2 fixed upstream.** H-1 whole; H-2 in the half that was
actually missing, with the client's `Accept-Encoding` and the streamed decode
still open. Two of the 25 are new from this week — **H-24**, six reason phrases
that predate RFC 9110 and are a decision rather than a fix, and **H-25**, the
12.4.18 drop.

**H-25 is the one waiting on an event rather than on effort.** It has passed 14
times since the single failure, so the next red nightly is the measurement; the
instrument is in place and verified on both machines. Until then it is neither
diagnosed nor papered over, and the Autobahn server gate reads 🔶 rather than ✅
for that reason.
