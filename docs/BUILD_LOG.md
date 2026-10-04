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

## 2026-09-25 — H-26, and a schedule that was three different numbers

H-26 was written down on 2026-09-24 as two things, while H-25 was being fixed:
`ATCPServer` registers its connection check as `EveryMinutes(1, …)` and ignores
the `WardenCheckEvery` property it documents, and `Warden.EverySeconds(N, …)`
tests `timestamp.Minute % N`. Estimated XS. It was three, and the third one was
the reason the other two had never been caught.

### AllWardenChecks returned itself

```csharp
public IEnumerable<IWardenCheck>  AllWardenChecks
    => AllWardenChecks;
```

No caller, no compiler warning. And no test can report it as a failure: a
`StackOverflow` cannot be caught, so reverting the line does not turn a test red
— it takes the test host down, "Testlauf abgebrochen" with NUnit's dispatcher
still on the stack. That is the falsification, and it is also the argument for
fixing it before something first enumerates the checks in anger.

It goes first because the other two defects are only testable by reading the
registered checks back and asking each one when it would run.

### EverySeconds measured minutes

Six of the eight overloads asked `timestamp.Minute % Seconds == Offset`. The
other two ask about `timestamp.Second`, which is what was meant and which is what
makes this a copy-and-paste slip rather than a design.

So `EverySeconds(30, …)` did not run every thirty seconds. It ran on every Warden
tick during minutes 0 and 30 of each hour, and never otherwise — a schedule that
depended on what time it happened to be. No callers, so it has never worked.

### The interval the server documents, and the two that it used instead

Two ways of not honouring `WardenCheckEvery`, in the same block.

**The defaults resolved twice.** The properties took `DefaultWardenCheckEvery`,
30 seconds. Then the Warden constructor resolved the *parameters* again, against
literals `FromMinutes(3)` and `FromMinutes(1)` sitting in the call. So
`WardenCheckEvery` read 30 s while the Warden ticked once a minute, and
`WardenInitialDelay` read 30 s while the first tick was three minutes away. The
public documented properties win now, and the constructor uses them rather than
re-deriving them.

**The check had an interval of its own.** `EveryMinutes(1, …)` reads like "once a
minute" and is two separate things: a predicate, `Minute % 1 == 0`, which is true
on every tick, and a one-minute `SleepTime`, which was the actual schedule — and
which no constructor argument can reach. Ask for a five-second Warden and you got
a five-second timer and a reaper still running once a minute. The reaper is a
plain check with no debounce now, so the tick is the interval.

### Two tests about things that were never broken

Ten tests, none of which waits for anything: the schedules are sampled over a
synthetic timeline, so a figure here is a statement about the predicate and not
about how long a test slept. Seven of them are the obvious ones — the slots of
each family, the offset, the answer not depending on which minute it is.

The two that are not about a defect are the ones worth having, because both were
load-bearing and written down nowhere:

- **`SleepTime` is what turns a slot into one run.** The predicate alone would
  fire on every tick inside a matching slot, and "minute 0" is sixty seconds
  wide. Four runs an hour instead of twenty-four is the debounce's doing, and the
  two halves live in different files.
- **The predicate is *sampled*.** A slot narrower than the Warden's own
  `CheckEvery` is a slot that can be missed: at seconds divisible by three there
  are twenty slots a minute, and a Warden ticking every ten seconds visits two.
  Not a defect — it is what "run the checks every `CheckEvery`" means — but it is
  why `EverySeconds` needs a Warden ticking at least that often, and why the
  minute- and hour-aligned schedules were never in danger.

### The same wall, hit from the other side, while this was open

Hermod master gained 28 commits between the branch point and the merge, and one
of them was `1ad3f3b8`, "A connection whose handler is still starting is not
reaped" — a second fix to the same reaper, found by running a real gateway with
"the Warden made to look every millisecond — **a source edit, the same in every
build**".

That is H-26's own symptom, written independently by someone who was not fixing
H-26. A finding confirmed from a second direction while it was being fixed is
worth more than the finding was.

It also means the merge mattered more than usual: a textually clean merge of a
change to the reaper's *schedule* into a change to the reaper's *criterion* is
exactly where a semantic conflict would sit, and neither PR's CI had seen the
other's code. So the suites were re-run against the merged tree rather than
against the branch — including master's own `AConnectionWhoseHandlerIsStillStartingIsNotReaped`,
which arranges for the Warden to look exactly once, a second after the server is
made, and would have noticed a schedule that now looks sooner or oftener. It
passes.

That is yesterday's lesson applied one day later: a number, or a green run, that
is true of a branch is not yet true of master.

### Numbers

Hermod's gate filter 611 → **657**; `Tests.HTTP.` 610 → **656**; WebSockets
49 → **65**; the HTTP/1 regression selection unchanged at **372**. This repo's own
gate is unchanged at **279/279**, 7/7, and Autobahn against the merged tree is
**481/517** with 0 hard failures — the same floor, with the reaper now looking
twice as often. That run happened to overlap a full NUnit suite on the same box,
which makes it a slightly harder test than the clean one it was meant to be.

**None of the +46 is this work.** The ten tests H-26 added live in
`Tests.Warden` and `Tests.TCP`, and the filter this repository gates on selects
`Tests.HTTP.` and `Tests.HTTPS.` — so they run in Hermod's CI and not in ours. The
whole +46 is Hermod master's own growth over one day.

That is worth writing down rather than quietly fixing, because it is a gap with an
argument on both sides. `AHTTPServer` derives from `ATCPServer`; "wider than
WebSocket" was the sharp end of H-25 precisely because of that inheritance. The
layer that argument was about is the layer this gate does not cover. Widening the
filter changes what CI means, so it belongs in a decision rather than in a commit
that was about something else.

Track B: **26 findings, 6 fixed upstream** — and all six whole.

## 2026-09-26 — Five findings at once, and two tests that could not fail

H-21, H-22, H-4, H-6 and H-8, one commit each on one branch
([Hermod#43](https://github.com/Vanaheimr/Hermod/pull/43)), in that order for a
reason: H-8 is a sweep over stale RFC citations, and doing it first would have
left it stale again by the time the other four landed.

**H-21 — `Accept-Ranges` on the wrong side of the request/response split.** The
finding was found while building A1, when the demo had to fall back to
`SetHeaderField("Accept-Ranges", …)`. The field was defined as a *request*
header field, and the summary on that very definition began "The Accept-Ranges
**response**-header field": the code had been documenting the mistake while
making it. The response side has it now; the three request-side members are
`[Obsolete]` rather than removed, because deleting them breaks every downstream
Vanaheimr project and a warning says the same thing without doing that. C#
suppresses obsolete warnings inside obsolete members, so Hermod's own build
gained none — measured, not assumed.

Reading the field next door turned up a second one. The builder's `AcceptPatch`
setter wrote `Allow`:

    set { SetHeaderField(HTTPResponseHeaderField.Allow, value); }

A handler advertising patch formats therefore replaced the set of methods it
claims to support, with media types. `SetHeaderField` takes an `Object`, so the
mismatch between `IEnumerable<HTTPContentType>` and `IEnumerable<HTTPMethod>`
never reaches the compiler, and `Allow: application/json` is something the wire
is perfectly happy to carry.

**H-22 — the chunked response that ended nowhere.** Announcing the coding took
two steps that look independent: `Transfer-Encoding` on the builder, and a
`ChunkedTransferEncodingStream` as the body. The dispatch keyed on the second.
A handler that wrote the first plus a `ChunkWorker` — which is what the API
reads as though it wants — got correct headers, no body, no terminating chunk
and no error, which a recipient can only diagnose as a timeout. The demo's
`/chunked` route carried a comment saying exactly this, and a line constructing
the stream from `request.NetworkStream` to work around it. Both are gone now.

The fix is that the layer which knows about the connection is the layer that can
supply the framing: the server builds the stream, and writes the terminal chunk
whether or not the worker did. The client had the mirror image in its request
path, found while fixing the response one.

What it deliberately does *not* do is frame everything that says "chunked". That
was the first attempt, and it failed 6 tests in the wider suite — correctly. A
byte array or plain stream on a chunked response is taken to be **framed
already**; `AutomaticallyChunkContent` is how a handler says otherwise, and
`Chunked_Response_Content_And_Stream_Must_Be_Sent` had been pinning that
contract since long before. Nineteen unit tests would not have found it; the
675-test selection did, on the first run. Same shape as the compression filter
two days earlier, and the fixture's summary now records the constraint so the
next reader does not repeat the attempt.

**H-4 — `Forwarded`, and the address `X-Forwarded-For` was throwing away.**
RFC 7239 is what the `X-Forwarded-For` family became when it was standardised,
and its advantage is not politeness: one element holds all four facts about one
hop, instead of four independent lists that can differ in length and then have
to be lined up by guesswork. `ForwardedElement` and `ForwardedNode` parse it,
serialize it, and keep `unknown` and obfuscated identifiers opaque rather than
inventing an address for a node that declined to give one.

Then the part that was already there. The server built its `HTTPSource` as

    new HTTPSource(HTTPRequest.RemoteSocket, httpSources.Skip(1))

which for `client, proxy1, proxy2` records the proxies and discards the client —
the one address the field exists to carry — with the socket it keeps being the
*immediate* peer rather than a stand-in for it. It reads like the leftover of a
different intent, in which the first entry was going to become the socket. That
intent would have been worse: `X-Forwarded-For` is client-controllable, so the
socket stays the socket and the header goes beside it.

**H-6 — Structured Fields, strictly.** RFC 9651 is the grammar most HTTP fields
defined since 2021 are written in, so the alternative to implementing it once is
hand-rolling `split(',')` per field, forever. Three top-level types over eight
bare types, Section 4.2 parsing one method per algorithm and in the
specification's order, Section 4.1 serialization, byte-for-byte round trips.

Being liberal is the temptation and the wrong one: two implementations that each
repair a different malformed field are two implementations that disagree about
what the field said. So a trailing comma fails, a sixteenth digit fails rather
than saturating, an upper-case key is a syntax error rather than something to
fold, `@1659578233.0` is not a date, and a display string that is not valid
UTF-8 fails rather than handing the caller U+FFFD where the sender wrote a byte.

**H-8 — 69 citations, 62 of them rewritten.** Mostly one line copied down a
file, `<seealso cref="http://tools.ietf.org/html/rfc2616"/>`, which names a
document that has not existed for eleven years and no section at all. Each is
now the field's own defining document and section, read off the IANA HTTP Field
Name registry rather than recalled: 45 lookups, where being confident about 44
is not the same as being right about all 45.

Seven are left on purpose. Six are RFC 4918 quoting RFC 2616 inside text this
codebase quotes in turn — rewriting those would misquote RFC 4918, which does
say `[RFC2616]` and cannot be made to say otherwise, so each of the three blocks
now carries a remark naming the current reference. The seventh is inside
commented-out code under `HTTP1/Server/URLMapping_old/`, where tidying a
citation would be tidying around the thing that actually needs deciding, which
is H-18.

### Two tests that could not fail

The falsification pass is the part of this worth keeping, because twice it said
"no test failed" and twice that was the interesting answer rather than a
formality.

*One rule, two guards.* The first version of H-22 had both a `Finished` property
on the chunked stream and an idempotence guard inside `Finish`. Reverting the
guard changed no test — the property was covering for it. Two mechanisms that
each make the other unobservable are two mechanisms neither of which is tested,
so the property went and the call sites now simply finish. The reversal then
failed three tests, as it should.

The same shape appeared in H-6, where relaxing `IsLowerHexDigit` to accept
upper-case percent escapes changed nothing: `HexValue` still computed nonsense
for `'C'`, and the strict UTF-8 decoder refused the result. One rule, two
guards again — and there the honest answer was to say so and relax both, rather
than to pretend a one-line reversal had demonstrated anything.

*And a measurement that was not a measurement.* Two early falsification runs
reported zero failures because the reversal did not compile — a name collision
between the `IPAddress` property and the `IPAddress` type — so `dotnet test`
failed the build and ran nothing, while a grep for failing test names found
none. A green number from a run that never happened is exactly the thing this
repository keeps paying for. The loop counts build errors now.

The claim that quote-aware parsing matters "because an IPv6 node is written
`for="[2001:db8:cafe::17]:4711"`" went the same way: reverting the
quote-awareness broke nothing, because an address contains neither a comma nor
a semicolon. The rule is real — a *quoted value* may contain both separators —
but the reason written in the comment was wrong, and only measuring it said so.

### Numbers

Gate filter 657 → **738**; `Tests.HTTP.` 656 → **737**; WebSockets 65 → **87**.
Of the 81 added, 59 are these five findings and 22 are Hermod master's own
WebSocket work, which arrived in [#44](https://github.com/Vanaheimr/Hermod/pull/44)
and [#45](https://github.com/Vanaheimr/Hermod/pull/45) plus the two commits the
previous pin was already behind. Nothing is unaccounted for this time — which is
worth saying, since the last two bumps each had a remainder that took an hour to
explain.

This repo's own gate is unchanged at **279/279**, 7/7 harnesses — including
after the demo's H-22 workaround was removed, which is the end-to-end proof that
the fix replaces it rather than merely coexisting with it.

The protocol regression selection stays at **372**: the four new fixtures are
not in its eleven-file filter. Adding them makes it **431**, measured. Whether
they belong there is a one-line change upstream and has not been made — noted
rather than done, because widening a documented filter silently is how a
published number stops meaning what its name says.

Three PRs were merged in one sitting, and none of their CI runs had seen the
others: #44 and #45 were both computed against a master without #43, and #45
did not know about #44, though the two touch the same six WebSocket files.
After the second merge GitHub put #45 back to `UNKNOWN` until it had recomputed.
The combination was therefore re-run locally before anything was pinned — 738
and 7/7 — rather than inferred from three green runs of three different trees.

Track B: **26 findings, 11 fixed upstream** — and all eleven whole.

## 2026-09-26 — A7 and A9: five foreign stacks, and a control

### A7 — the direction that had no witness

Every interop check in this repository before today was .NET against .NET, or
curl. curl is a good witness and it is one witness, which leaves the question
of whether the server matches HTTP/1.1 or matches curl. And the *client* had
nothing at all: it had only ever talked to a server from the same source tree,
so every wire-visible assumption the two share was invisible to both.

`tests/interop.sh` closes both halves. **58/58 checks, 3 skips.**

Five clients — Go `net/http`, Java `java.net.http`, Node `node:http`, Python
`http.client`, and wget — each running the *same* ten checks against the demo
host, so the matrix is comparable rather than a pile of anecdotes: baseline,
chunked body, trailers, gzip round-trip, HEAD-matches-GET, `Range` → 206,
`Accept-Ranges`, 404, redirect, reuse. Go passed all ten on the first run,
which is the answer one hopes for and not the interesting part; the interesting
part is that the checks can fail, which was measured rather than assumed.

Two peers earned a skip apiece and one earned two, and the skips are the honest
half of the table. `java.net.http` and `http.client` discard the trailer
section without exposing it; `node:http` follows no redirects. Those say SKIP
with the reason on the line, because a check quietly measuring something else
is worse than a check that is not there.

The gzip check decodes by hand in every language. Letting the transport do it
would have measured four different things: Go switches off its transparent
decompression precisely when the header is set by hand, so the four peers would
have disagreed about what was even being tested.

The second direction is `tests/h1peer` — our `HTTPClient` against
`tests/peers/server.go` and `server.mjs`. Their Content-Length framing, their
chunked framing, their trailer section collected by us, their gzip undone by
ours. 7/7 against each. Two foreign servers rather than three: Python's stdlib
cannot frame chunked itself, so a Python server would have been testing our
framing wearing a Python hat.

Everything is stdlib-only, deliberately. `go run`, `java Client.java`, `node`,
`python3` — no package fetch, no lockfile, no build step, so a clean checkout
needs the runtimes and nothing else. Rust's `hyper` is the one peer left out
for exactly this reason: it would need crates.io.

**Falsified.** Breaking the Go client's expected chunked body fails
`go/chunked` and takes the driver to 16/17 with exit 1; breaking the Go
server's trailer value fails `h1peer/go` at 6/7. Both were needed: the first
attempt at this changed nothing, because the `perl -pi` that was supposed to
break the expectation matched zero times and said nothing about it. An edit
that did not apply looks exactly like a change that had no effect — the third
time today that pattern cost a measurement.

### A9 — numbers, and something to compare them to

`tests/h1bench`, modelled on the HTTP/2 sibling's `h2bench`. Not a gate, not in
CI: the baseline an optimisation has to beat.

| | |
|---|---|
| request header parsing | 48,795 parses/s, **18,696 bytes allocated** per parse of a 376-byte header |
| chunked coding | 2,225 MiB/s encode, 1,921 MiB/s decode |
| small GET, one client | ~5,000 req/s at 1, 8 and 64 concurrent |
| small GET, a client each | 21,657 req/s at 8, 20,245 at 64 |
| 64 MiB download / upload | 987 / 958 MiB/s, 3.00× the payload allocated |
| kept-open connection | p50 0.197 ms |
| control: `HttpClient` → Hermod | p50 **0.240 ms** |
| control: `HttpClient` → Kestrel | p50 **0.252 ms** |

**The control is the point.** Our server is the marginally faster of the two on
the same loopback, in the same process, driven by the same client. Without that
column, 0.24 ms is a number with no scale, and this is the second Vanaheimr
stack where the honest reading turned out to be "latency is fine" while a
throughput shape looked alarming.

**A fresh client costs 39 ms and none of it is the connection.** This is the
finding A9 existed to produce. The first measurement said "fresh connection:
39.4 ms p50 against 0.20 ms kept open", which is a suspicious number on
loopback, where a TCP handshake is microseconds. Splitting it gave 38.3 ms in
the `HTTPClient` *constructor* and 1.06 ms in its first request — so it is not
the connection at all.

Naming the cause took one more measurement rather than a guess: the same loop
with a single shared `DNSClient` runs at **0.449 ms**, 86× faster. The line is
`ATCPClient.cs:319`, `DNSClient ?? new DNSClient(...)`, and that default
searches the machine's network configuration for resolvers — once per client,
including when the URL is a literal IP address that will never be resolved.
Filed as **H-27**, with `tests/h1bench -- connect` as its regression test.

**Flat throughput on one client is the protocol.** 64 callers on one connection
queue, because HTTP/1.1 has no multiplexing: req/s stays flat and latency rises
linearly. Give each caller its own client and the server does four times the
work. This is written down because the HTTP/2 sibling has a curve of the same
shape that *is* a defect, and the two must not be read as one finding.

18.7 KB allocated to parse a 376-byte header, and ~63 KB per small request, are
both high — the HTTP/2 sibling allocates 9.3–9.9 KiB per trivial request. Not
chased today; recorded so that the next person to look has a starting point
rather than an impression.

### Numbers

This repository's gate is unchanged at **279/279**, 7/7 harnesses — A7 is
nightly and A9 is not a gate at all. With `--wsl` the local run is now
**415/415** across **9/9** harnesses: 279, plus the Debian curl's 78, plus the
peers' 58.

Two repo-side follow-ups from yesterday's pin landed with this: the demo's
three `SetHeaderField("Accept-Ranges", …)` calls became the typed property that
H-21 added, and the comment saying the field "is only modeled as a *request*
field in Hermod" went with them.

## 2026-09-26 — A10: a fuzzer that is honest about being the weaker one

The plan asked for SharpFuzz driven by AFL++. That is not what landed, and the
reason is worth the paragraph rather than being discovered later from a diff.

AFL++ is a system install and Linux-only. A clean checkout on Windows could not
run it; CI would need a package step for a job measured in hours; and
coverage-guided fuzzing has no budget at which it is a *gate* — its findings
arrive days later, which is a fine thing to have and a useless thing to block a
push on.

`tests/h1fuzz` is the cheaper instrument, described as such: a deterministic
mutation fuzzer needing nothing installed, against three parsers.

| Target | Promise |
|---|---|
| `HTTPRequest.TryParse` | a Boolean — so *any* exception is a finding |
| `HTTPResponse.TryParse` | the same, on the client's side |
| `ChunkedTransferEncodingStream` | to refuse malformed framing *as* malformed framing |

Roughly one to two million inputs per target per minute. The mutators are
chosen for HTTP rather than for generality: bare CR and LF injection, digit
runs turned into enormous ones (which is where `Content-Length` and chunk-size
live), truncation, line duplication, and splicing two corpus entries — the
shape a smuggling bug has.

What makes it gateable is that it is reproducible. Every run is `--seed N`;
every finding prints the seed and the iteration that produced it, saves the
exact bytes, and `--replay <file>` reproduces the stack trace. In the gate the
seed is fixed and the budget is five seconds per target, so the same inputs run
every time and red means this change broke something — a fuzzer with a moving
seed in a push gate is a coin toss with a build attached. The nightly moves the
seed with the date and runs ten minutes per target.

### It found something in thirty seconds

`System.Exception: "Expected CRLF"`, out of the chunked decoder.

That is **H-28**, and the shape of it is the interesting part. The decoder has
its own `HTTPInvalidChunkException`, which derives from `FormatException`, and
uses it at ten of its eleven throw sites. The eleventh —
`ChunkedTransferEncodingStream.cs:665`, thirty lines below a sibling that
throws `HTTPInvalidChunkException` for the same condition — throws a bare
`System.Exception`. A caller wanting to tell "this input was malformed" from
"the decoder lost its footing" has to catch `Exception` and gets both.

The fuzzer isolated exactly the inconsistent one because the allowlist it
checks against is the promise, not the implementation: `FormatException` is
expected, so the ten correct throws passed silently and the one leak did not.
H-2's `ContentDecodingStream` exists for this exact reason — the stack already
decided elsewhere that callers should have one exception type to catch.

### Two mechanisms the first run demanded

**Deduplication.** The first run reported 42,472 findings, all the same defect.
A report nobody reads is not a report. Findings are now keyed by
`(kind, message)`, the first input producing each signature is the one saved —
usually the smallest — and the count goes on the line. The same run now says
"1 distinct finding from 386,214 inputs".

**A known-findings list.** With H-28 open, a fuzzer in the gate is permanently
red, which is how a suite stops being read. `tests/h1fuzz/known-findings.txt`
holds the signatures already filed: printed loudly with their count, not
failing the run. It is the same bargain `tests/autobahn.sh` strikes with its
floor, and deleting a line is how a fix gets verified.

Falsified all three: with H-28 listed the run exits 0; with the line removed it
exits 1 and says "1 NEW finding"; and `--replay` on the saved input reproduces
the exception with a stack trace naming line 665.

### What the numbers say in passing

The request parser runs ~10–34 k inputs/s against the response parser's
~62–98 k, and the slowest single input in a run has been 40–175 ms. For a
*parse* — no sockets, no I/O — that is a long time, and it is the same
neighbourhood as A9's 18.7 KB allocated per parse. Not chased today; recorded
so the next person starts from a number rather than an impression.

### Numbers

The gate is 279/279 checks over **8/8** harnesses now, the eighth being the
fuzzer's fixed-seed pass, at roughly 120 s. With `--wsl` it is 415/415 over
10/10.

Track A: A5, A6 and A8 remain. Track B: **28 findings, 11 fixed** — H-28 is
this entry's.

## 2026-09-27 — A6: a gadget needs two parsers, so measure two

`h1attack` has asked since A0 whether our server can be desynchronised, and
the answer has always been no. Today that turned out to be a smaller claim
than it reads as.

A single origin server cannot smuggle a request past itself. The attack *is* a
disagreement — one parser reads one message where the next reads two, and
nobody authorised the second one — so an instrument pointed at one
implementation can, in principle, never see one. RFC 9112 §11.2 says as much
in its first sentence, and it took building the thing to notice that the
section's own wording had been sitting in `h1attack`'s comments for weeks:
*"The real TE.TE differential needs two implementations disagreeing — that is
http-garden, PLAN.md A6."*

Four instruments, and the whole write-up is
[`TestingAgainst_Smuggling.md`](TestingAgainst_Smuggling.md).

### The line between asserting and observing

`tests/h1desync` sends 38 ambiguously framed messages. What makes it worth
having is not the count but the split.

**24 carry a normative sentence**, quoted in the source next to the payload it
governs. §6.3 item 4 gives a Transfer-Encoding whose final coding is not
chunked a MUST *and* a status code. §6.3 item 5 does the same for an invalid
Content-Length, §5.1 for whitespace before a colon, §7.1 for a chunk-size that
is not `1*HEXDIG`. Hermod: 24/24.

**14 do not**, and there the harness says nothing. §6.1:

> A server MAY reject a request that contains both Content-Length and
> Transfer-Encoding or process such a request in accordance with the
> Transfer-Encoding alone. Regardless, the server MUST close the connection
> after responding to such a request to avoid the potential attacks.

Asserting a preference on the first half would be this repository's taste
wearing conformance's clothes. What *is* asserted is the second half, the
close — and that is a requirement nothing here had been checking.

Three of the 24 run the other way on purpose. `te-ows-spaces`, `te-ows-htab`
and `te-mixed-case` are well-formed chunked requests in unusual but legal
clothing, and they **must** be decoded. A harness that only ever demands
rejection gives its best score to a server that rejects everything, which is
not conformance either.

### Ten disagreements

`tests/smuggle.sh` runs the same probes against Hermod, Go's `net/http` and
Node's `node:http` and joins on the probe id. 28 rows agree. The vocabulary is
deliberately coarse — how many responses, which codes, did it hang up —
because two servers never produce identical bytes and anything richer would
report a difference on every row.

**Six of the ten are Go's**, and two are a MUST violation rather than a
divergence. `net/http` answers the CL.TE and TE.CL shapes with 200, then
serves the hidden request, and **leaves the connection open**. Verified by
hand outside the harness against go1.24.4, because a claim about somebody
else's widely used code deserves more than a table cell:

```
HTTP/1.1 200 OK … Content-Length: 0
HTTP/1.1 404 Not Found … 404 page not found        ← the hidden request
(connection still open)
```

`chunk-bws` is the same door from another angle: Go accepts `5 ` as a
chunk-size where the grammar is `1*HEXDIG` and an extension must begin with
`;`. `lf-only-headers` is not a defect at all — §2.2 permits a bare LF — and
is exactly as useful for building a chain.

**Three are ours, and they are one finding.** `te-dup`, `te-obf-sp` and
`te-chunked-chunked` all reduce, via RFC 9110 §5.3's rule that repeated field
lines combine, to `Transfer-Encoding: chunked, chunked`, which §6.1 forbids a
sender to produce. `AHTTPPDU.cs:422` asks only whether the **last** coding is
chunked — right for `chunked, gzip`, where §6.3 item 4 then requires the 400
we give it, and silently dropping the duplicate here. Not a violation: §6.1
binds senders and item 4 does not fire. But it is accepting a framing no
conforming client may send, on the one field smuggling is made of, while both
peers refuse it. Filed as **H-29**.

### Two defects in our own instrument

A6 is the first thing here that needs the response count to be exactly right
rather than roughly right, and it found that it was neither.

`Checks.ResponseCount` counted occurrences of `"HTTP/1."`. The demo's error
responses carry `Server: Hermod HTTP/1.1 Demo`, so a single 400 counted as two
responses — and only on the code paths that set that field, which is why
nothing had noticed. Anchoring the match to the start of a line fixed that and
broke pipelining instead: a response whose predecessor's body does not end in
CRLF — `helloHTTP/1.1 404`, straight off Go's wire — became invisible, and
**six rows of the first differential table were wrong because of it**. It
parses the framing now: status line, header section, the body the framing
announces, on to the next, and it stops rather than guess. Falsified both
ways, which is the only reason to believe the third version: the substring
count fails 4 `h1desync` checks, the line-anchored count fails 2 `h1conn`
ones.

`RawConnection` could not tell "the peer hung up" from "the read window
expired" — which is the entire content of §6.1's close requirement.
`PeerClosed` says which.

### The Garden

`tests/http-garden/` is Hermod as a target in
[the HTTP Garden](https://github.com/narfindustries/http-garden): 45 HTTP
implementations in Docker, each answering with a JSON description of the
request **as it parsed it**, so a disagreement is visible field by field
rather than inferred from a response count. A Dockerfile on the Garden's own
pattern, and `HermodGarden`, which runs Hermod on `0.0.0.0:80` and answers in
that format.

It runs. Against `tornado`, the `chunked, chunked` payload produces two
clusters, and the parse tree shows the mechanism instead of implying it:

```
hermod: [ HTTPRequest(method=b'POST', uri=b'/echo', version=b'1.1',
                      headers=[(b'host', b'a'),
                               (b'transfer-encoding', b'chunked, chunked')],
                      body=b'') ]
tornado: [ HTTPResponse(version=b'1.1', method=b'400', reason=b'Bad Request') ]
    0. hermod
    1. tornado
```

That is H-29 from a third implementation, corroborating `smuggle.sh` from an
instrument that shares no code with it.

What is *not* automated is the other forty-four targets. The Garden builds
every one from source with clang and ASan; that is compiler-hours and
gigabytes, so `--build --with <target>` takes them one at a time and the thing
is run by hand. Three limitations of our target are written down rather than
left to be discovered later: Hermod's parsed headers are a case-insensitive
dictionary, so field order and duplicate field lines are not reportable; an
exotic method is answered 405 rather than described; and there is no ASan for
managed code.

### Three traps, since they each cost real time

**The URL in `PLAN.md` was wrong.** `narf-industries/http-garden` does not
exist; the organisation is `narfindustries`, no hyphen. GitHub answers 404 for
a missing repository the same way it answers for a private one, so `git clone`
sat waiting for credentials instead of failing — twenty minutes spent
diagnosing WSL networking for a typo.

**The checkout directory name is load-bearing.** `tools/targets.py` hardcodes
`_NETWORK_NAME = "http-garden_default"` and Compose derives the network from
the directory, so a checkout in `/tmp/hg` gives `hg_default`, the repl finds
no containers, prints one warning naming all fifty services, and then answers
every payload with nothing. An empty result that looks like a result.
`tests/http-garden.sh` refuses a directory that is not named `http-garden`.

**A tool that did not run reports no findings.** The first `tests/smuggler.sh`
printed a green "no CL.TE or TE.CL issue reported" for a run that had died on
its first line with `Cannot find config file` — `-c` takes a bare name,
because smuggler tests `configfile[1] == '/'` to decide whether a path is
absolute, which `D:/…` fails. The no-findings check is now gated on coverage,
and coverage is counted from the config file rather than assumed: 134/134 for
the default set, 966/966 for `--config doubles`. Pointed at a dead port it now
says "not established" twice in red.

### Numbers

The gate is **303/303 over 9/9 harnesses**, ~135 s. With `--wsl`, **477/477
over 12/12**. smuggler: 134/134 mutations, nothing found. h2csmuggler: no h2c
surface, now a pinned regression.

Track A: **A5 and A8 remain**. Track B: **29 findings, 11 fixed** — H-29 is
this entry's, and the Go one is not ours to fix.

## 2026-09-27 — H-29 fixed, and the two defects underneath it

A6 found it in the morning; it is pinned by the afternoon
([Hermod#54](https://github.com/Vanaheimr/Hermod/pull/54), two commits). The
short version is one predicate. The longer version is why fixing one predicate
took three files.

### The predicate

`IsChunkedTransferEncoding` asked whether the **last** transfer coding is
chunked. Right question for `chunked, gzip`, where RFC 9112 §6.3 item 4 then
requires the 400 we already gave it. Wrong question for `chunked, chunked`,
which it accepted and de-chunked once.

§6.1 forbids a *sender* to apply chunked more than once and states no
recipient rule, so accepting it violated nothing — which is exactly the
argument for refusing. No conforming client can produce the message; a
recipient that accepts it disagrees about where the body ends with every
recipient that does not; and that disagreement is the whole of what request
smuggling is. Go says 501, Node says 400.

The server's 400 is a check of its own, with its own message, because being
stricter than the RFC is a **choice** and putting it in the same clause as the
MUST above it would have hidden that. Same discipline as `h1desync`'s split
between the 24 probes that carry a rule and the 14 that do not.

### Three parse paths, three answers — H-30

Getting the two-line spellings (`Transfer-Encoding: chunked` twice, which RFC
9110 §5.3 makes identical to `chunked, chunked`) to reach that predicate meant
finding where repeated field lines are combined. The answer was: in one place
out of three.

Only `HTTPRequest.TryParse`'s server overload joined them. The public
`TryParse(text, out request)` and **every HTTP response** went through the
`AHTTPPDU` constructor, which kept them as a `String[]` — and
`GetHeaderField<String>` cannot cast a `String[]` to a `String`, so it
returned null:

> a message carrying `Transfer-Encoding: chunked` twice was read as declaring
> **no transfer coding whatsoever**.

Not an odd one. None. Measured across all three paths before and after, in a
throwaway program that printed the same eight inputs three times; that table
is the reason this was found at all, rather than the predicate being fixed on
its own and the two-line case quietly continuing to do something else.

### The client half: wrong, then right

The prediction was that the client would mis-frame such a response. It does
not: `TryValidateResponseFraming` reads the raw header lines and has always
refused a transfer coding it cannot frame. Measured on unmodified master, and
the prediction had been flagged as unverified precisely because it came from
reading code rather than running it.

What the measurement *did* show is a real defect one step further on: the
refusal **kept the connection**. The reason for refusing is that the body's
end is unknown, so the body was never consumed, and the next response read on
that connection begins inside it —

    response 1      : 0 - ClientError          (correctly refused)
    IsHTTPConnected : True                     (and kept)
    response 2      : 0 - ClientError - "Invalid HTTP response status line"

— because what it read was `5\r\nhello`. One line to fix.

It only happens when the body arrives in a **later TCP segment** than the
head. Written the obvious way, with the response in one write, the leftovers
land in the client's own buffer and are dropped with it: the first version of
the test was green before the fix. It flushes head and body separately now,
and that arrangement is most of what the test is.

### Verification, and one trap in it

17 tests, walking all three parse paths, with five legal single-chunked
spellings alongside — a rule that rejects too much is not an improvement on
one that accepts too much. Each change reverted on its own: the server check
costs 4 tests, the exactly-once rule 2, the line combining 2, the client line
1. 832/832 in `Tests.HTTP.` + `Tests.HTTPS.`

The trap was in the falsification harness rather than the code.
`shutil.copy2` preserves mtime, and MSBuild decides what to recompile from
mtime, so a restored file **older** than the assembly built from the reverted
one was silently not rebuilt — each case could have been measuring the one
before it. Re-run with an explicit `os.utime`; the figures above are from the
clean pass. Nothing about the earlier numbers changed, which is luck rather
than vindication.

### The outside witness

The differential that found this reported the fix without being asked:

```
no longer disagreeing — delete these lines from tests/smuggle-known.txt:
  te-dup              (was TWO,REJECT,REJECT, now all agree on REJECT)
  te-obf-sp           (was TWO,REJECT,REJECT, now all agree on REJECT)
  te-chunked-chunked  (was TWO,REJECT,REJECT, now all agree on REJECT)
```

**10 disagreements down to 7**, and the remaining seven are all Go's. That
second half of the known-file bargain — reporting a row that *stopped*
disagreeing — is the easy one to leave out, and it is the only thing that
keeps such a list from growing forever.

### And one more empty result that looked like a result

Re-running the differential against the patched build with the demo host down
produced a full table: every row disagreeing, ten NEW findings, "0 agree, 38
disagree". `h1desync` prints `OBS…ERROR` per probe when it cannot connect, so
the file is not empty — and "not empty" was the whole of the check. It counts
non-ERROR rows now and refuses outright.

That is the third time in two days: the empty `FAIL` line from
`tests/interop.sh`, `smuggler.sh` reporting no findings for a scan that never
ran, and this. The pattern is always the same — an instrument that cannot
reach its subject produces output shaped exactly like a measurement.

### Numbers

Pin: `c85de5a2` → `531c0ed5`. Hermod's gate filter **832** (was 738; 17 are
this fixture, the other 77 came with master). This repo: **303/303 over 9/9**,
and **477/477 over 12/12** with `--wsl`.

Track A: A5 and A8 remain. Track B: **30 findings, 13 fixed**.

## 2026-09-27 — A5: the chain, and a detector that has never fired

A6 ended with seven probes on which Hermod, Go and Node place the end of a
message differently, and with the observation that a disagreement is only an
attack when two implementations are chained. A5 builds the chain: nginx 1.27,
HAProxy 3.0, Caddy 2, Apache httpd 2.4 and Envoy 1.31, each reverse-proxying
the demo host. All pulled, none built — the deliberate opposite of the HTTP
Garden, which compiles every target from source with clang and ASan.

`tests/proxy.sh` measures four things and they are not equally strong. The
write-up is [`TestingAgainst_Proxies.md`](TestingAgainst_Proxies.md).

### The easy half: the demo still works behind all five

The whole 78-check curl matrix through each chain, against 78/78 direct —
which is what makes the column readable, since a difference is then the
proxy's doing and not a pre-existing failure. nginx 72, HAProxy 77, Caddy 75,
Apache 73, Envoy 71.

The 26 differences cluster into four recognisable behaviours: answering an
HTTP/1.0 request as 1.1, refusing or rewriting `OPTIONS *`, declining to
forward an `Upgrade` nobody configured them for, and — Envoy alone — refusing
an unregistered method, which takes out `QUERY`. None is a Hermod defect, and
each is recorded by name so that a *new* one fails.

### The informative half: what a proxy does with an ambiguous message

`h1desync --observe` through each chain against the same run made directly,
in A6's vocabulary. 40 rows differ across the five, and three are worth
keeping:

**`chunk-bws` goes through all five.** A chunk-size followed by a bare space —
`5 \r\n`, where the grammar is `1*HEXDIG` and an extension has to begin with
`;`. Hermod alone answers 400; every one of the five forwards it in a shape
that gets the hidden request executed. A6 had found Go accepting it and filed
it as Go's; it turns out to be near-universal.

**Caddy splits `cl-te` and `te-cl` into two requests.** Caddy is Go's
`net/http` underneath, and this is exactly the behaviour A6 measured in Go
directly — the same finding arriving through a different door, which is the
sort of corroboration a differential is for.

**Apache answers 500 where Hermod answers 400.** For the three chunked-twice
spellings Hermod refuses with 400 and closes, and Apache turns that upstream
close into a `500 Internal Server Error` of its own. Apache's translation, not
ours — and visible only *because* H-29 landed that morning: before it, Hermod
would have answered 200 and then served the hidden request.

### The part that matters: a detector with no evidence behind it

The response count on the attacking connection answers the wrong question
through a proxy. Two responses mean the *front end* saw two requests and
forwarded both — that is pipelining, and its policy engine saw both. The
attack is the front end seeing one where the back end sees two, and on the
attacking connection that is invisible.

Where it shows is the next connection: the extra response sits in the proxy's
pooled upstream connection and goes to whoever asks next. So
`tests/proxy-poison.py` sends the payload, then asks innocent questions on
fresh connections.

No chain poisons. That would have been a comfortable place to stop.

**Calibration.** A back end was built that answers one request with two
responses — verified on the wire, one request in and `200 OK` plus `418 I'm a
teapot` out, which is exactly the state a successful desync leaves behind —
and put behind each of the five proxies in turn.

    haproxy   clean - the proxy absorbed it
    nginx     clean - the proxy absorbed it
    caddy     clean - the proxy absorbed it
    httpd     clean - the proxy absorbed it
    envoy     clean - the proxy absorbed it

All five discard the upstream connection rather than pass the extra response
on. **The detector has never been seen to fire through any of them.** So
"clean" means "no chain here produced an attack, and these five would have
absorbed one in any case" — a real statement about the chain and a weak one
about the origin.

It stays in the suite. It is the right instrument, it costs seconds, and the
day a sixth proxy is added that does not absorb, it starts meaning something.
What changed is that the write-up says this rather than quoting 32/32 and
moving on.

The honest summary of A5 is that the chain is safe for two independent reasons
and neither was arranged: Hermod refuses the ambiguous framings and hangs up,
and the proxies would have absorbed the consequences if it had not.

### Two traps

**`MSYS_NO_PATHCONV=1` must not be exported.** It is needed so Git Bash does
not rewrite `/mnt/d/...` before `wsl.exe` sees it — the path arrived as
`C:/Program Files/Git/mnt/d/...`. Exported, it also stops `/dev/null` being
translated for native Windows binaries, so every `curl -s -o /dev/null` in the
driver began to fail. The first of those is the check for the demo host, which
duly reported the demo down while it was answering 200 in the next shell
along. Per-command now, the way `autobahn.sh` has always done it.

**And one I walked straight into.** The first `proxy-poison.py` reported the
status codes on the attacking connection by anchoring `HTTP/1.` to the start
of a line — the exact under-counting mistake diagnosed and rejected in
`Checks.ResponseCount` the same morning, where the other cheap trick,
counting substrings, over-counts instead. Doing it right means walking the
framing. The verdict needs only the first status of a single-response
follow-up, so the wrong thing was removed rather than the walker duplicated.

### What is not done, deliberately

The reverse direction from the original sketch — Hermod's `HTTPClient`
through a proxy to a foreign origin. `tests/interop.sh` already points that
client at Go's and Node's servers directly; putting a proxy in between
measures the proxy. Left out on purpose rather than forgotten.

### One run in five that nobody can explain

Four runs of 32/32 and one of 14/32 — everything from `poison/haproxy`
onwards. Not reproduced, and three explanations tested and discarded: not
Hermod under proxy load (the same workload passes repeatedly against the same
binary, and the demo was answering afterwards), not a stale upstream pool
(kill the demo all five are pooled against, start another, and all five
answer 200 at once), not the driver owning the demo's lifetime (re-run
exactly, 32/32). The failing run was the one whose predecessor had been killed
mid-flight, which is the likeliest story and is not evidence.

It is written down rather than rounded off. What was done about it is not a
fix: the eighteen crosses said nothing about their common cause, so the driver
now checks between sections that the demo still answers and stops with
"the demo host stopped answering on :8080 after the framing section",
keeping its log. The next occurrence names itself; the suite is no more
correct and a good deal easier to believe.

### Numbers

`tests/proxy.sh`: **32/32**, about ten minutes, nightly on `ubuntu-latest`.
63 recorded differences — 22 curl, 40 framing, 1 via (Apache drops the
trailer section, which is allowed).

Track A is **done but for A8**. Track B: 30 findings, 13 fixed.

## 2026-09-27 — A8: the one consumer that was not written to be a test

Everything else in this repository judges the demo with code somebody chose to
point at it: our harnesses, curl, five foreign stdlib clients, five reverse
proxies. A browser is not that, and it is the least forgiving HTTP/1.1
consumer in daily use.

Three engines through Playwright — Chromium, Firefox and WebKit. WebKit is the
reason for the dependency: the only way to reach Safari's engine from a
script, and the engine nobody tests. **27 checks, 24 pass, all three engines
agreeing on every one.** Write-up:
[`TestingAgainst_Browsers.md`](TestingAgainst_Browsers.md).

### The battery runs in the page

The driver navigates to the demo's own `/` document and evaluates the battery
inside it, so every `fetch`, `EventSource` and `WebSocket` is a same-origin
request from a real page — the only arrangement in which CORS, connection
reuse and Resource Timing mean anything. No page is served for the occasion.
A `/browser` route would have made this convenient and would have measured
that route.

Four of the nine checks are things nothing else here can establish:

| | |
|---|---|
| `performance.nextHopProtocol` | the browser saying it spoke `http/1.1`. Everything else is us asserting the protocol we believe we used |
| `EventSource` | curl can read an SSE body; only a browser has the client half — event-type dispatch, `id`, `retry` |
| `connectStart === connectEnd` | the browser's own account of keep-alive |
| CORS | curl cannot test it at all. It sends the request and is answered |

### The three failures are one finding

H-10, and it has sat in the table since A0 marked it "Browser-visible (A8)".

The demo grew a `/cors` route that sets `Access-Control-Allow-Origin`, so the
simple cross-origin GET works — Hermod can set the field. A POST carrying a
custom request header is not simple, so the browser sends a preflight:

```
> OPTIONS /cors          (the browser)      < 405 Method Not Allowed
                                            < Allow: GET, POST
> POST /cors             (the same, curl)   < 200 OK
```

Chromium says `Failed to fetch`, Firefox `NetworkError when attempting to
fetch resource.`, WebKit `Load failed`, and all three mean the preflight.

`OPTIONS` is deliberately not registered on that route. A handler there would
answer the preflight and hide the gap the route exists to show — the same
reasoning as the demo's hand-written `HEAD` registrations, which H-23 is
about.

### Three checks that were wrong before they were right

Each looked like a server defect for a few minutes, and each was mine:

- **The chunked body ends with a newline** — 33 octets, measured.
  `tests/interop.sh` compares through a `$(...)`, which strips trailing
  newlines on both sides and so never had to know. A string comparison in
  JavaScript does.
- **`/large` does not serve ranges.** It is deliberately a plain
  octet-stream, so a Range check pointed at it measured that choice.
  `/files/resource.txt` is the route that answers 206.
- **The demo's events are named.** It sends `event: tick`, and `onmessage`
  fires only for unnamed events — so the first version waited twenty seconds
  for a message type the server never sends.

Three for three: every failure in the first run was the harness, not the
server. That is the usual ratio for a new instrument and the reason none of
them was recorded before being chased.

### And one in the driver

`grep -E '^DIFF\t'` does not match a tab. GNU grep leaves `\t` undefined in
ERE and treats it as a literal `t`, so the join lines the runner emits for
`tests/browser-known.txt` would have passed straight through into the report.
Caught by testing the pattern rather than the script; `$(printf '\t')` now.

### A deviation from the plan, stated

The plan asked for `tools/browser-interop.ps1` on the HTTP/3 model. This is
bash plus a Node module, because this repository removed its PowerShell
runners on purpose: two implementations of one runner produce two numbers that
look like agreement, and the siblings paid for that twice — once with an
`$Args` parameter that silently never bound. What was taken from the HTTP/2
script is its good idea: the page runs the battery and reports a verdict,
rather than the driver scraping a DOM and guessing when the run finished.

### Numbers

`tests/browser.sh`: **24/27**, nightly on `ubuntu-latest`, no Docker and no
WSL. The demo stays on loopback — the cross-origin twin is `http://localhost`,
which reaches a loopback listener perfectly well, and a test run has no
business widening a listener it did not have to.

**Track A is complete.** Track B: 30 findings, 13 fixed.

## 2026-10-01 — Advance the pin, and a bodiless response that now states its length

Hermod `531c0ed5` → `5ab74d7f` (55 commits), Styx `67cc7495` → `ba317094` (5).
All three repositories in the family now pin the same pair; the HTTP/2 and
HTTP/3 siblings moved to it earlier the same day.

`against-hermod-master` had been green five nights running and tested exactly
this pair on `windows-latest`, build + the 877-test filter + the harness suite.
That covers the gate. What it does not run is the eight nightly-only suites, so
those were measured here before the push — **all of them**, which makes this the
best-evidenced bump of the three.

| | |
|---|---|
| build | 0 errors |
| in-process | **877** = 876 `Tests.HTTP.` + 1 `Tests.HTTPS.` (was 832) |
| ↳ WebSockets | **148** (was 87) |
| ↳ regression selection | **372**, unchanged |
| gate harnesses | **9/9** |
| `--wsl` | **12/12** — Debian curl 78/78, foreign peers 58/58, smuggling differential 38/38 |
| Autobahn, server | **481/517**, 36 declined, 0 hard failures — floor exactly |
| Autobahn, client | **445/517**, 72 declined, 0 hard failures — floor exactly |
| proxies (A5) | **32/32**, every difference already in `proxy-known.txt` |
| browsers (A8) | **24/27**, unchanged — still H-10 on all three engines |
| smuggler (A6) | **3/3**, 134/134 mutations, nothing found |
| fuzz (A10) | no findings, seed 20260926 |

Not one known-findings file changed: `smuggle-known.txt`, `proxy-known.txt` and
`browser-known.txt` all came back byte-identical, so nothing newly differs and
nothing stopped differing.

### What was actually in it for HTTP/1

Only six of the 55 commits touch code this repository exercises — the rest is
HTTP/2, DNS and Modbus. Two groups matter.

**`46d38b38`: a response without a body says that its length is 0.** Before it,
a bodiless 401, 404 or redirect stated neither `Content-Length` nor
`Transfer-Encoding`, so by RFC 9112 §6.3 its body ended when the connection
did — and a client on a kept-alive connection waited for the rest of an empty
body until the server gave up, thirty seconds a request. The carve-out is the
part worth reading: 1xx, 204 and 205 as before, plus 304 and the answer to a
HEAD, whose length would be that of a representation they do not carry, plus
event streams, plus anything with a `Transfer-Encoding`. That is exactly what
RFC 9110 §8.6 requires, and it is why this is wire-visible without being a
conformance change. The framing walk in `tests/H1Core/Checks.cs` and the three
known-findings files are the things that could have noticed; none did.

**The WebSocket trio** — `ab95dc28`, `4766b74e`, `079a74f5`: a refused upgrade
is one HTTP message and is closed without a close frame; no close frame on a
connection that was never upgraded; a handshake is parsed once its header has
ended rather than from its first read. `AWebSocketServer.cs` gained 250 lines
and the WebSocket test count went 87 → 148. Both Autobahn directions came back
at their floors with zero hard failures, which is the witness that matters for
a change to the server's close behaviour.

### One failure, and it is not this bump

`HTTPServerSocketRegressionTests.Slow_TLS_Handshake_Does_Not_Block_Following_Accepts`
failed in the full run. Its second assertion gives a loopback accept **500 ms**
while a half-open TLS handshake is pending, and the fixture was run three times
at each pin: **1 of 3 failed at `531c0ed5`, 2 of 3 at `5ab74d7f`.** Flaky at
both. No production code on the TLS/TCP accept path changed in the range, the
test file is byte-identical across it, and the nightly reported 877/877 for this
pair on a GitHub `windows-latest` runner the same morning. Alone it passes in a
second.

The first reading of this was wrong and is worth recording as such: a single
run at the old pin came back 832/832, which looked like the bump having broken
something. It was luck. Three runs a side is what turned a suspected regression
into a measured pre-existing flake — the same lesson as the `h2priority`
harnesses in the HTTP/2 repository, where an assertion that depended on the
scheduler being quick was rewritten to depend on an ordering instead. Filed
upstream; it is `HermodTests` code, not ours.

Worth knowing about the box it was measured on: it was running test suites from
two unrelated repositories at the same time, under other sessions. A 500 ms
budget is not a property of the server.

### Two stale claims, neither caused by the pin

`CLAUDE.md` said the differential had **10** pinned rows and 28 of 38 agreeing.
`smuggle-known.txt` has held **7** since `71b0633` let the differential delete
its own lines — the file was right, the prose was three commits behind its own
commit. And the counts appeared in two places, only one of which anyone had been
updating. Both fixed, and the coverage table in `README.md` now names the
revision it was measured against rather than a date alone.

## 2026-10-02 — Advance the pin to bd34db7c: the accept loop moves to the thread pool, and the header timeout covers the whole header

Hermod `5ab74d7f` → `bd34db7c`, the merge of Hermod PR #79. Styx stays at
`ba317094`. All three repositories in the family pin that pair again: the HTTP/2
sibling moved to it in `a30a3eb`, the HTTP/3 sibling in `e758979`.

**Two pins, one measurement — and why that is honest here.** The suites were
started at `65b26095`, the commit both siblings pinned when this bump began.
While they ran, the HTTP/2 sibling moved on to `bd34db7c`, which settles a
deadlock in Hermod's HTTP/2 client tests (#69 × #72; see that repository's log).
Outside `Hermod/HTTP2` and `HermodTests/HTTP2`, `65b26095..bd34db7c` touches
four test files only — test-CA cleanup in `HermodTests/Helpers`, `Modbus`,
`PKI` and `TCP`, none in this repository's filters — and no product code. So
for HTTP/1 the two pins compile the same code, and the split below says which
pin each figure was measured at rather than pretending all of it ran twice.

| | | at |
|---|---|---|
| build | 0 errors | both |
| in-process, the gate's filter | **896/896** = 895 `Tests.HTTP.` + 1 `Tests.HTTPS.` (was 877) | both, identical |
| ↳ WebSockets | **149/149** (was 148) | both, identical |
| ↳ regression selection | **374/374** (was 372) | both, identical |
| harnesses, `--wsl` | **12/12** — curl 78/78 on both builds, foreign peers 58/58, smuggling differential 38/38, `h1fuzz` deterministic | both, identical |
| Autobahn, server | **481/517**, 36 declined, 0 hard — floor exactly | `65b26095` |
| Autobahn, client | **445/517**, 72 declined, 0 hard — floor exactly | `65b26095` |
| proxies (A5) | **32/32** | `bd34db7c` |
| smuggler (A6) | **3/3**, 134/134 mutations, nothing found | `bd34db7c` |
| browsers (A8) | **24/27**, unchanged — still H-10 on all three engines | `65b26095` |
| fuzz (A10), exploring | **not run**: no commit in the range touches the request, response or chunked parsers | — |

Not one known-findings file changed: `smuggle-known.txt`, `proxy-known.txt` and `browser-known.txt` came back byte-identical, in the Windows checkout and in the WSL clone the container suites ran from (a Linux-side clone, so that a WSL build could not overwrite the `bin/` the Windows runs were using).

**What was in it for HTTP/1.** Unlike the last two sibling bumps, this one is
not shared code only. Of the commits outside `HTTP2/`, the ones this repository
exercises:

- `e5a19bc7` — the TCP server builds and starts a new connection on the thread
  pool, not on the accept loop (`ATCPServer.cs` +481/−). A slow constructor or
  TLS setup no longer delays the next accept, which is exactly what
  `HTTPServerSocketRegressionTests.Slow_TLS_Handshake_Does_Not_Block_Following_Accepts`
  — the test the last entry recorded as flaky at both pins — asserts.
- `7665292c` — `AHTTPServer`'s `HeaderReadTimeout` bounds a request's header
  section as a whole, from when the server starts waiting for it, rather than
  each read. A client trickling one byte per read used to reset the clock on
  every byte; that is the Slowloris shape, and `h1attack` and `h1conn` came back
  unchanged.
- `8d8823b2` — a server with open event streams stops at once (`SSEServerStopTests`, new).
- `1781a088` — an HTTPS client made from an IP address, a port or a DNS name
  speaks TLS (`HTTPSClientTLSByDefaultTests`, new).
- `f3b1e2fe`, `c515e3b0` — the WebSocket and TCP servers report each closed
  connection once, and say who closed it.

**895 / 374 / 149.** `Tests.HTTP.` went 876 → 895 and the gate's filter
877 → 896 (`Tests.HTTPS.` is still one test, counted on its own). The
regression selection went 372 → 374 because `HTTPServerSocketRegressionTests`,
one of its eleven files, gained the accept-loop and header-timeout cases; its
filter in `HTTP1/README.md` did not change. WebSockets 148 → 149. The "431"
the README gave for the selection plus the four 2026-09-26 fixtures was measured
against `5ab74d7f` and is now labelled as such rather than re-measured.

## Next

**Track A is complete.** A8 landed on 2026-09-27, and with it every
third-party suite the plan asked for: curl, Autobahn both directions, five
foreign stdlib peers, five reverse proxies, two smuggling scanners, the HTTP
Garden, a parser fuzzer and three browser engines.

A5 and A6 landed the same day and are worth reading together: A6 found seven
probes on which Hermod, Go and Node place the end of a message differently,
and A5 took them into a chain, which is the only shape in which they are
attacks. None of them is, through those five proxies — with the caveat that
the detector for the final step has never been seen to fire, because all five
absorb a poisoned upstream connection whatever the origin does.

**What is left is Track B**: 15 open findings — this paragraph said 17 and
named H-27 and H-23 as the two with the widest reach; H-27 closed on
2026-10-03, and H-10, which this paragraph said a browser now demonstrated
rather than described, closed the day before. H-23 (`HEAD` is not derived from
`GET`) is the one of the two still open.

The pin moved to Hermod `5ab74d7f` on 2026-10-01, measured against every suite
this repository has rather than only the gate's — see the entry above.

**A7, A9 and A10 landed on 2026-09-26, A6 on 2026-09-27.** A7 is five
foreign clients and two foreign servers, 58/58, nightly on `ubuntu-latest`. A9
is `tests/h1bench`, not a gate, whose control column says our per-request
latency beats Kestrel's on the same loopback and whose `connect` scenario
produced finding **H-27**. A10 is `tests/h1fuzz`, deterministic in the gate
and exploring nightly, which produced **H-28** thirty seconds into its first
run. A6 is the smuggling differential — 24 assertions in the gate, three
implementations compared nightly, two third-party suites, and Hermod added to
the HTTP Garden — which produced **H-29** and a MUST violation in Go's
`net/http`.

**Track B: 30 findings, 13 fixed upstream, all of them whole.** H-1; H-2 as of
2026-09-24, which took four fixes against an estimate of one; H-3, which moved the
RFC 9110 §11 framework into the shared library on the way; H-16, which had been
fixed upstream for a week before anybody re-read the row; H-25, the Warden killing
live connections; H-26, its scheduling, which was three defects against an
estimate of two; and on 2026-09-26 the five of [#43](https://github.com/Vanaheimr/Hermod/pull/43)
— H-21, H-22, H-4, H-6 and H-8 — of which three turned up a second defect
sitting next to the one they were about.

What that leaves, roughly by value: **H-23** (`HEAD` not derived from `GET`),
**H-24** (six reason phrases that predate RFC 9110 — a decision rather than a
fix), **H-5** (the RFC 9111 cache, the one genuinely large item), **H-7**
(`Alt-Svc`, the bridge to the h2/h3 stacks), **H-10** (CORS preflight, which A8
would see) and nine more. **H-13** is newly unblocked: the structured-fields
parser a `Content-Digest` needs now exists. Then A5–A10.

One decision is still open and is not a finding: the CI gate here selects
`Tests.HTTP.` and `Tests.HTTPS.`, and reaches neither `Tests.TCP` nor
`Tests.Warden` — the layer `AHTTPServer` is built on, and the layer H-25 and
H-26 were about. Widening the filter changes what CI means, so it belongs in a
decision rather than in a commit that was about something else.

**Three lessons from these two weeks are worth keeping in view. All three cost
real time and all three are cheap to avoid.**

*A bump is not finished until the findings it might have closed have been
re-read.* H-16 read "the general HTTP server has no `Upgrade` dispatch" while
Hermod had grown exactly that on 2026-09-16, with its own tests. The row described
the state of a pin that had not moved since 2026-08-13; the bump of 2026-09-23
brought the fix in and nobody looked. Third time in a week a finding outlived the
thing it described, after A11 and after H-2's first half.

*Unit tests reach for the shape that is easy to construct.* Nineteen tests for the
server-side compression filter all handed it a response whose body was already a
byte array, which is the only shape you naturally build in a test — and the defect
was in the path where the body is still a stream. The wire harnesses found it on
the first run.

*A number that is true of a branch is not yet true of master.* The H-2 commit
published a gate of 605, measured before the merge; CI printed 611, and the six
were a commit that had landed on Hermod master while the PR was open. A day later
the same thing happened with code rather than a count: 28 commits arrived under
the H-26 branch, one of them a second fix to the very reaper H-26 reschedules. The
merge was textually clean, which is not the same as correct, so everything was
re-run against the merged tree — including master's own test for that other fix.

---

## 2026-10-02 — Dependencies, and what the bump found

A maintenance pass, with one result worth more than the housekeeping.

### The pin

Hermod `bd34db7c` → **`0a3f2b8f`**, three commits, all already in master. The one
that matters here is *"the TLS certificate context is built when the server
starts, not by its first client"* — it touches `ATCPServer`, which is what both
our listeners stand on. **895/895** under `Tests.HTTP.` and the gate at
**303/303**; Styx needed no bump, it was already at master tip.

### The proxies got stricter, and that is the finding

nginx 1.27→1.31, HAProxy 3.0→3.4, Envoy v1.31→v1.39. Caddy and httpd float on
their major tags and were current already.

A5 still reports **32/32** — but three recorded differences *disappeared*, and
the script said so itself rather than leaving them to rot:

```
no longer differing — delete these lines from tests/proxy-known.txt:
    framing	envoy	chunk-lf-only
    framing	haproxy	cl-list-diff
    framing	nginx	chunk-lf-only
```

Both `chunk-lf-only` rows were `direct=REJECT chain=TWO`: Hermod refused the
message, and the chain turned it into **two requests**. That is the shape a
smuggling gadget actually takes, and nginx and Envoy have stopped producing it.
Caddy still does (`chain=ONE`), on an image that was not bumped — which is the
control that makes the other three mean something.

The three lines are deleted. A future version that reintroduces one now fails
the run instead of matching a stale entry.

### Two environment traps, neither of them a conformance result

Both presented as failures and cost time before being understood, which is the
only reason they are written down.

**All five proxies "never answered".** Including the two whose images had not
changed — which is what identified it as the environment rather than the bump.
WSL2's localhost relay had gone stale: containers answered inside WSL and not
from Windows, and `tests/proxy.sh` probes `127.0.0.1` from the Windows side.
`wsl --shutdown` fixed it. The daemon then has to be restarted by hand, and
`sudo service docker start` is the wrong recipe here — it wants a password and
hangs with no tty. `wsl -d Debian -u root service docker start` needs none.
CLAUDE.md said the runner scripts start the daemon themselves; **no script
does**, and that claim is now corrected rather than left to mislead the next
reader.

**`✗ poison/httpd`, "Python wurde nicht gefunden".** The Windows Store alias
stub intercepting `python` for exactly one probe while the other four in the
same run were fine. Clean on re-run. Worth noting only because a red line on the
*poison* detector is the one result here nobody should wave through.

### Stale numbers, found by reading rather than by failing

Nothing caught these, which is the point of recording them:

- **H-29 appeared twice in the findings table** — once `⬜` as originally
  reported, once `✅` as fixed. The fix added a row instead of updating one, so
  the plan claimed an open finding that had been closed five days earlier.
- `CLAUDE.md` said **561** NUnit tests in its header and **895** thirty lines
  later; the gate was **279/279** in one place and **303/303** in another; the
  findings were "tracked as H-1…H-26" when they run to H-30.
- `PLAN.md`'s sequence diagram still showed `⬜A5` and `⬜A8` after both closed,
  and its second milestone read "two of three" when it was three.

The headings were kept current and the prose underneath was not. Markers make
staleness *visible*, not impossible.

### Also current, for the record

`actions/setup-node` v4→v7 (the only Action behind; checkout, setup-dotnet and
upload-artifact were current), Node 22→24 in the nightly, Playwright's floor
raised to `^1.63.0` to match what the lockfile already resolves.

**Not done, deliberately:** NUnit **4.6.1 → 5.0.0** in `HermodTests` and
`StyxTests`. A major version across two foreign repositories and ~1 500 tests is
not a maintenance pass, and this repository has no NuGet dependencies of its own
to update — every package here lives in a submodule. `Grpc.Net.Client`
2.83→2.84 and `Microsoft.NET.Test.Sdk` 18.10.0→18.10.1 are likewise upstream.

### The known-file bargain, and a hole in it

A peer session reviewing the above made the sharper point: three rows left
`tests/proxy-known.txt` for a reason outside this repository, and **the file
alone cannot tell "we got stricter" from "they got stricter."** It recorded a
date and not a version, so a reader six months from now sees fewer lines and no
way to attribute them.

So the file now carries the images it was measured against, `--update` writes
them from the compose file rather than leaving it to whoever remembers, and this
particular prune says in the header that it was them: nginx and Envoy stopped
splitting `chunk-lf-only` downstream while an unbumped Caddy still does.

Chasing that turned up a real bug one level up. Running
`tests/proxy.sh --only nginx --filter framing` printed:

```
no longer differing — delete these lines from tests/proxy-known.txt:
    curl	nginx	--http1.0 downgrades the exchange
    curl	nginx	HTTP/1.0 status line echoes the version
    …
```

Every one of those is a *valid* row. The curl section had not run — it was
filtered out — so nothing matched it, and the staleness check read "not found"
as "no longer differs". Following that advice would have deleted six recorded
differences the run never looked at.

The loop already guarded against one version of this: a proxy that failed to
come up is skipped, with a comment saying so. The author saw the proxy case and
not the section case, one level up. Now `section "$kind" || continue` sits
beside it: **a row is stale when it was looked for and not found, never when it
was never looked for.** Verified both ways — the filtered run proposes nothing,
and the full run still reports 32/32 with no stale rows.

Also worth recording, since the peer reported it and it was wrong: `proxy.sh`
does **not** have six bare `exit 1` paths. Five print a diagnosis first, and the
sixth is inside `demo_still_up()`, which prints three lines and fifteen of the
demo's log. The symptom it was reported from — a log containing the demo banner
and nothing after — remains unexplained, and is not that.

### The driver that started the wrong binary

The peer chased its own unexplained `exit 1` to the end and the answer turned
out to be ours, in four scripts rather than one.

A tree built under Windows carries **both** apphosts side by side:
`Demo/bin/Debug/net10.0/HTTP1.Demo` is an ELF binary and `HTTP1.Demo.exe` a PE
one. Every driver selected between them like this:

```bash
DEMO_EXE="$ROOT/Demo/bin/Debug/net10.0/HTTP1.Demo"
[ -f "$DEMO_EXE.exe" ] && DEMO_EXE="$DEMO_EXE.exe"
```

— preferring the `.exe` *because it exists*, with no idea what it is running on.
Driven from inside WSL, that launches a **Windows** process through binfmt
interop. It starts perfectly, binds the Windows host's `0.0.0.0`, prints
`Ready.` into the log, and then the readiness poll on the VM's own `127.0.0.1`
times out against a demo that is running on a different machine. The log
therefore contains both a clear diagnostic *and* a healthy-looking startup
banner, which is how it read as a silent failure to someone looking at the tail.

Now `case "$(uname -s)"` guards it, in `run-tests.sh`, `proxy.sh`, `browser.sh`
and `smuggler.sh`. Verified on both sides: Git Bash resolves to `HTTP1.Demo.exe`
and WSL to `HTTP1.Demo`, and the Windows gate is unchanged at 9/9.

**Why CI never caught it:** a Linux build produces no `.exe` for the line to
prefer, so on `ubuntu-latest` the bug is unreachable. It needs a Windows-built
tree driven from Linux — which is not a configuration CI has, and is exactly
the one a developer on this machine reaches for. A green CI leg was never
evidence about this path.

Worth separating from the peer's hypothesis, which did not hold: it proposed
that the host-side readiness check polls the container-facing hostname
(`host.docker.internal`). It does not — the poll is on `127.0.0.1`, and
`DEMO_HOST` is only handed to `docker compose` and printed. Right symptom, right
instinct that the Linux branch was at fault, wrong mechanism. The evidence it
supplied was what made the real one findable.

### The shape all three had in common

Worth separating from the fixes, because the fixes are small and this is not.

Three defects surfaced in two days, found three different ways, and they are the
same defect:

| | The signal | Why it could not fail |
|---|---|---|
| `tests/proxy-known.txt` | "every difference already recorded" | pruning a row was indistinguishable from never looking for it — the file recorded a date, not a version, so it could not say whether we got stricter or the proxy did |
| `--filter framing` | "no longer differing — delete these lines" | a section that did not run matched nothing, and *not found* was read as *no longer differs* |
| the `.exe` preference | a green nightly proxies leg | a Linux build produces no `.exe`, so the faulty line was **unreachable on CI**. The leg was never evidence about that path at all |

Each was green for as long as it existed. None of them was a test that passed
when it should have failed — they were checks **structurally incapable** of
failing, read for weeks as passes.

That is a different failure mode from a bug, and it is not caught by running the
suite more often: running it again reproduces the same vacuum. It is caught by
asking of a green check *what would have to be true for this to go red*, and
noticing when the answer is "nothing reachable from here".

The third one is the sharpest, because CI is exactly where this instinct is
weakest. `ubuntu-latest` was green on the proxies job every night while the line
that broke a developer's run could not execute there. A passing CI leg is
evidence about the configuration CI runs, and about no other.

(The first two were found and fixed here, `788554e` and `8c7372d`. A peer
session reports a fourth of the same shape in a sibling repository — a `$Args`
parameter that silently never bound — which I have not verified and record as
theirs rather than as a finding of ours.)

Also checked, so it is not left ambiguous: **neither sibling needs the apphost
fix.** HTTP2ConformanceTests runs the built `.dll` through `dotnet`
(platform-neutral IL, no apphost to prefer) and HTTP3ConformanceTests names the
extensionless apphost and lets each platform resolve it. Zero hits for the
pattern in either repository's `tests/` or `tools/`.

### A fourth of the shape, in our own code, found by applying the rule

The peer opened the `$Args` case from source and it turned out to be a *third*
disease rather than either of the two above: the harness verdict was live and
honest about what it ran, but a function-level `[string[]] $Args` collided with
PowerShell's automatic variable, so the caller's arguments never landed and
eleven of twelve scenarios never executed. The label was built independently of
the arguments, so the report named twelve subjects and had run one — a positive
claim of coverage that was false, where ours were absences of signal.

Its sharper form of the rule: ask not only what would make a green check go red,
but **whether the thing it names is the thing it ran.**

Applied here, by falsification rather than by reading:

| | |
|---|---|
| `--port 18999` (dead) | throws instead of quietly passing against 8080 — the argument binds |
| `--tls --port 8080` | fails the handshake against the cleartext listener — `--tls` binds |
| `--tls --port 8443` | 32/32 — and the TLS leg really is TLS |
| `--base http://127.0.0.1:18999` | **5 of 78 still green** |

Those five are the find. All of them assert an *absence* — four `hasnt()` checks
and one `size_download == 0` — and against a server that is not running,
everything is absent. They could not fail for the one reason that should have
failed them hardest.

Harmless in a full run, since a dead server fails everything else in the same
breath. Not harmless as evidence: a `hasnt()` line quoted on its own carries
none. `hasnt()` now refuses to judge an empty exchange, and the HEAD check pairs
the status with the size, because a dead server also downloads zero bytes.
Against a dead port the matrix now scores **0/78** where it scored 5/78; against
the demo it is unchanged at 78/78, gate 9/9.

Four instances now, three distinct mechanisms: unreachable by configuration,
unreachable by filtering, unreachable by argument loss, and vacuously true
against nothing at all.

---

## 2026-10-03 — H-10 closed: the preflight, and the pin that carries it

The one open finding that was holding a gate red, and the only one no driver
here but a real browser could reach. Fixed upstream in three commits
([Hermod#81](https://github.com/Vanaheimr/Hermod/pull/81)), pin advanced,
**browser 27/27** where it read 24/27 since A8 landed.

### Why it could not be a handler

The objection that shaped the design was the right one: in this stack everything
is manual — the resource decides what its own semantics mean. So what business
does a preflight have being automatic?

The answer is that the rule presupposes the request *reaches* the resource, and
a preflight never does. It is an `OPTIONS` for a method the route has no handler
for, so routing answers it — `405` — before any handler is consulted. The
handler is not declining to deal with it; it is never offered the chance.

Which makes the real question not "should Hermod automate this" but **"routing
already automates a rejection; where does the application get to intervene?"**
And the seam for that already existed: `AHTTPPipeline` runs before routing and
short-circuits on a non-null response, which is exactly the shape. So the fix is
a component the application *installs*, like `HTTPAuthPipeline` — not behaviour
baked into the server. A server that adds none behaves as before.

### Three commits, and the first one does not fix it

Separating them mattered, because the first is independently correct and the
temptation is to call it the fix:

1. **Resource-level `OPTIONS`** (RFC 9110 §9.3.7). An unregistered `OPTIONS` was
   answered `405` *while the rejection carried `Methods.Keys`* — routing
   refusing a question it had the answer to. **This alone does not fix a
   preflight**: the browser gets `204` instead of `405` and is refused all the
   same, for want of `Access-Control-Allow-*`.
2. **`HTTPCORSPipeline` + `CORSPolicy`**, the policy stated by the application.
3. **The pipeline asking the router** for the methods the route actually has, so
   `Access-Control-Allow-Methods` cannot promise what no handler answers.

Commit 1 also produced an inconsistency of my own making, caught before it
shipped: appending `OPTIONS` to the `204`'s `Allow` alone left one resource
giving two accounts of itself — `OPTIONS / → 204` beside
`DELETE / → 405, Allow: GET, HEAD`. The `405`'s `Allow` is the field a client
consults *because* it was refused, so it is the one that must not lie.

### Two of my own errors, both caught by measuring

**The browser stayed red after commit 2.** curl got a correct preflight answer;
all three engines still failed. Cause: I had *guessed* the allowed header name
(`X-Demo`) instead of reading the test, which sends `X-Demo-Preflight`. The
refusal worked exactly as designed — I had configured the wrong list. The
lesson is small and keeps recurring: the driver's source is the specification of
what the driver does, and guessing at it produces a failure that looks like the
server's.

**The test for commit 3 was falsified rather than trusted.** A test for a
*narrowing* that passes under both the narrowed and the unnarrowed version
measures nothing — the exact failure mode this repository hit four times in the
preceding days. So the intersection was disabled and the test re-run: it goes
red, because the policy allows `DELETE`, the route does not have it, and only
the router knows the difference. Then restored.

### The pin carries more than the fix

The bump is to master's tip `285dadd4`, not to my merge `a5737489`, and that is
worth naming rather than glossing: between this repository's previous pin and
the new one sit **two SSH commits, one shared-HTTP change by someone else
(`22768a4a`, which is where the +1 test comes from), and HTTP/2 + HTTP/3
WebSocket work (#82)** — none of it mine.

What was measured against the new pin is HTTP/1: **907** under `Tests.HTTP.`,
the gate at 9/9 and 303/303, browser 27/27. #82's HTTP/2 and HTTP/3 code is not
exercised by this repository at all, which is a true statement and not a claim
that it is fine.

Measuring against the *merged* pin rather than against the branch is the whole
point of doing it after the merge: until now everything had been verified
against code that was not yet what this repository tests.

## 2026-10-03 — H-27 closed: a resolver nobody asked for, and the control that stopped paying

A9 had measured it on 2026-09-26 and the number sat in `PLAN.md` for a week: a
fresh `HTTPClient` per request cost 39.4 ms p50, of which **38.3 ms was the
constructor** and 1.06 ms the request it then made. `ATCPClient` built the
default `DNSClient` there, and that default searches the machine's network
configuration for resolvers — two sweeps of every network interface,
`GetIPProperties()` on each.

The work was not merely early. A client dialling a literal IP address resolves
nothing: `Connect` fills `ResolvedIPAddresses` from `RemoteIPAddress` and never
reaches the branch that queries, so the resolver just paid for is never touched.
A long-lived client paid once and nobody noticed; one client per request paid
every time.

The DNS client is now a `Lazy<IDNSClient>` — one handed in wrapped as a value,
one of the client's own making built when the property is first read. The detail
that matters as much as the Lazy is the disposal: `if (ownsDNSClient &&
DNSClient is not null)` would have **built the very client it then throws
away**, moving the whole cost from the way in to the way out. It asks
`dnsClient.IsValueCreated` instead.

### What the number is, and what the control is

Measured on one machine the same day, before and after, 500 iterations:

| p50 | without the fix | with the fix |
|---|---|---|
| constructing the client | **57.654 ms** | **0.009 ms** |
| a fresh client per request, whole | 59.324 ms | 2.347 ms |
| the same with one shared `DNSClient` | 1.645 ms | 2.406 ms |

The third row is the evidence; the first is only the speed-up. `h1bench`'s
`connect` scenario runs that row precisely so a figure cannot be read as "the
connection is expensive" — and handing in a shared `DNSClient`, which had been a
36-fold win, is now worth nothing at all. There is nothing left to share. What
remains, about 2 ms, is the handshake and the request, which no resolver can
account for.

57.7 ms where September had said 38.3 ms, on the same stack: more interfaces are
up on this machine now than then — WSL, Docker — and the sweep scales with them.
The finding got worse while sitting in the plan, which is an argument for the
`h1bench` row existing at all.

### The test suite defended itself against me

`DisposeStopsTimersTests` went red, and that is the best thing that happened all
day:

```
timers running while 20 HTTP clients were alive
```

It counts the timers twenty clients run while alive against those still running
after disposal — and a DNS client that was never made has no cache timer to
leave behind. With the constructor no longer making one, the test's assertion
about *disposal* would have passed over nothing at all. `TimerCount.AssertNoneLeft`
checks the first count before the second for exactly this reason, in a comment
written long before this change: *"Without a timer of their own while alive,
there would be nothing to leave behind, and the second check would pass without
having looked at anything."*

So this is the fifth instance of the theme this log keeps returning to — and the
first from the other direction. The four earlier ones were checks of mine that
could not fail. This was someone else's guard catching **my** change in the act
of hollowing out their test. The two tests now ask for the DNS client, which
both gives them something to count and is independent evidence that the laziness
is real: `Timer.ActiveCount` rather than a clock, twenty clients starting zero
cache timers between them.

### The new tests, falsified before being believed

`HTTPClientLazyDNSClientTests` observes construction through the **logger
factory**, not through a stopwatch: the default DNS client is made with a logger
of its own, and `ATCPClient` asks for an `IDNSClient` logger at exactly one
place. A counting `ILoggerFactory` therefore reports construction exactly, with
no timing bound to be flaky about.

Run against the unfixed library, three of its five tests fail — the constructor,
the disposal, and a whole request to a literal address — and the two that hold
in both worlds stay green. The request test asserts `200` and `"pong"` *before*
asserting that no resolver was made, because a request that never happened
resolves nothing either, and would make the real check true for a reason that
has nothing to do with DNS.

### H-31, found by reading the thing being fixed

`DNSClient`'s two constructors declare **opposite** defaults for the search:
`false` where manual servers are given, `true` where none are — without manual
servers a client that does not search has no servers at all. Both forwarded to
one body reading `?? true`.

I first described this as "the declared `false` is a lie", and that was too
strong; checking it corrected it. C# takes the *callee's* declared default for
an omitted argument, so the overloads that chain without passing the flags got
`false` and behaved correctly, and an existing test depends on it: three IPv6
addresses in, exactly one server out, "so that what is counted is only what this
test put in". What actually broke was an **explicitly passed `null`** — which is
what forwarding an optional setting does, and the only way a `Boolean?` reaches
there from configuration.

Narrow, and worth fixing anyway, because the consequence is not merely cost:
multi-server queries race and the fastest valid response wins, so a resolver
that joins the set unasked can answer before the one the caller named. The body
now reads `?? false` and the constructor without manual servers coalesces its
own `true` before forwarding, so each default is resolved where it was declared
and read.

Its seven tests each first ask what the search finds on the machine they run on
and call `Assert.Ignore` when that is nothing: on a container naming no
resolvers, "searched" and "did not search" look identical, and a green check
there would be one that could not have gone red. On this machine they run for
real — seven passed, none skipped — and against the unfixed library exactly the
`null`-with-manual-servers case fails, the other six passing because the old
body agreed with the declared default wherever the argument was omitted.

### The pin carries more than the fix, again — including a test framework

[Hermod#87](https://github.com/Vanaheimr/Hermod/pull/87) merged as `77c79106`,
and master had already moved three merges past it by the time of the bump. The
pin is master's tip `8c3ac23e`, and what sits between the old pin `285dadd4` and
it is: #86 (WebSocket over HTTP/2 and HTTP/3 reading a frame in one copy), my
#87, #88 (NUnit 5 warning follow-up) and #89 (HTTP/3 requests ending with their
connection). Of library code, only `Hermod/HTTP3/` — nothing under `HTTP1/`,
`HTTP/`, `TCP/` or `DNS/` outside my own two commits.

One of those deserves singling out, because it changed the ground under this
repository's measurements rather than its code: **`f96ede7d` moved HermodTests
from NUnit 4.6.1 to NUnit 5.0.0**, and it is an *ancestor of my own merge
commit* — master had taken it before GitHub merged #87. Everything I measured
locally while writing the fix ran under NUnit 4. My two new test files had never
been compiled, let alone run, under the framework the merged tree actually uses.
That is precisely the shape of gap this log has a convention about: a green run
is evidence about the configuration it ran in and about no other.

So it was measured against the pin, and the test project builds with **0
warnings and 0 errors** under NUnit 5 — the standard #88 had just established
for every other file — with all tests passing.

### Measured against `8c3ac23e`

| | |
|---|---|
| `Tests.HTTP.` | **912** (907 + the five new) |
| the filter CI gates on | **913** |
| the protocol regression selection | **377** (374 + H-10's three) |
| WebSockets | 149, unmoved |
| `Tests.DNS` | **502** (including H-31's seven) |
| `Tests.TCP` / `Timers` / `Warden` / `HTTPS` | 113 |
| WebSocket, Modbus, SMTP, Rendezvous, HTTP/2, HTTP/3 | **1287** (1183 against the old pin — #86 and #89 brought the rest) |
| this repository's gate | 9/9 harnesses, **303/303** |

H-27's five tests went into a new file and so are in `Tests.HTTP.` but not in
the regression selection, while H-10's three went into a file that selection
does name. That is the same eleven-file gap seen from both sides in one week,
and still a one-line change upstream that nobody has made.

Track B is now **16 of 31** — H-31 is the finding this fix produced, numbered
rather than buried in the one above it. Of the fifteen still open, none holds a
gate red.

## 2026-10-03 — H-23 closed, and the sentence it was filed on

H-23 said: `HEAD` is not derived from `GET`, an unregistered `HEAD` is answered
`405`, and the `Allow` field omits `HEAD` as well. It had sat open since A2 with
a citation — RFC 9110 §9.3.2 — and a quotation:

> A server SHOULD support HEAD for any resource it supports GET for

**That sentence is not in RFC 9110.** Not in §9.3.2, not anywhere else. I had
been asked whether HEAD really must be offered wherever GET is, went to check
rather than answer from memory, and found the quotation was mine rather than the
RFC's. The same misquote sat in a comment in `Demo/Program.cs`, where it had
been justifying the hand-registered `HEAD` handlers for two years of commits.

What the specification actually says, extracted from the section text:

| | |
|---|---|
| §9.1 | "All general-purpose servers MUST support the methods GET and HEAD." |
| §9.1 | a method recognized and implemented but not allowed for the target resource → SHOULD 405 |
| §9.3.2 | HEAD is GET except the server MUST NOT send content |
| §9.3.2 | the server SHOULD send the same header fields GET would have, and MAY omit those determined only while generating the content |

So the answer to the question was **no**: there is no per-resource rule, and a
resource may allow `GET` and not `HEAD` — §9.1 names the `405` as how it says
so. The requirement is one level up and it is a **MUST**: a general-purpose
server must support `HEAD`. A server answering `405` to `HEAD` on every route
whose handler had not registered one by hand was per-resource conformant and
server-level in breach, which is a more precise finding than the one that had
been written down, and a weaker one per resource.

The row in `PLAN.md` now carries the correction rather than a quietly swapped
citation. A wrong reference that supports the right conclusion is the kind of
error that survives review, because nobody re-reads a section they agree with.

### §9.3.2 also had an opinion about the implementation

The obvious implementation is to run the `GET` handler and drop the body, and
§9.3.2 names exactly that:

> These minor inconsistencies are considered preferable to generating and
> discarding the content for a HEAD request

That is rationale rather than a requirement, and it is the only place the
specification expresses a preference about cost. It did not change the decision
— automatic correctness is worth more here than automatic frugality — but it
changed what had to be written down: the derivation costs what the `GET` costs,
a 64 MiB download answered as `HEAD` allocates 64 MiB to send nothing, and a
handler that cares can read `Request.HTTPMethod` and return the header fields
alone. That is in `HTTP1/README.md` now, where a handler author will meet it.

### What the fix turned out not to need

Nothing for the body. `AHTTPServer.HasNoResponseBody` has always suppressed it
for a `HEAD` request, and three things follow from that one predicate: no static
content is copied, no chunk worker starts, and no event-stream worker starts. So
the header fields that go out are the ones `GET` would have sent — §9.3.2's
SHOULD, for free — and a chunked `GET` answered as `HEAD` carries
`Transfer-Encoding: chunked`, no body, and a reusable connection. RFC 9112 §6.3
item 1 ends any response to `HEAD` at the blank line "regardless of the header
fields present", so that is unambiguous rather than lucky.

The routing change is one condition at each of the two sites where H-10's
automatic `OPTIONS` answer already sat, and `PathNode.AdvertisedMethods` is now
the single definition of what a resource offers. That last part came out of the
H-10 work rather than this one: appending `OPTIONS` inside `ProcessRequest` was
right for the two answers beside it and reached nothing else, so anything asking
routing what a resource offers — `GetRegisteredMethods`, and through it the CORS
preflight — saw the registered set and never the added ones. One definition,
three readers.

### The demo was hiding the finding from the harness

This is the part worth keeping. `h1semantics` has had three `HEAD` checks since
A2 and the curl matrix two more, and all five were green all along — against
`/` and `/files/resource.txt`, the only two routes where the demo registered
`HEAD` by hand. Seventeen `GET` routes, two of them covered.

So the harness that found H-23 could not see it. The finding lived in a sentence
in `PLAN.md`, and the gate it belonged to said nothing, because the consumer had
worked around the gap before the gate could notice. That is a third variety of
the vacuum this log keeps cataloguing: not a check that cannot fail, but a check
whose subject had been quietly repaired underneath it.

Both registrations are gone. Against the library without the fix `h1semantics`
now reads **67/73**, and the six red checks include the plain `HEAD /` that had
been green since A2.

Two of the three original checks stay green even against a `405`, incidentally:
"HEAD keeps representation metadata → contains Content-Type:" and "HEAD has no
body → does not contain the demo's greeting" are both true of the JSON error
body. Only the status check distinguishes them, which is why it sits first.

### One harness bug of my own

The upstream test for connection reuse after a chunked `HEAD` sends two requests
on one connection. The first version stopped as soon as a second status line had
arrived — and a header section and a body need not arrive in one read, so it
returned a complete header section and no body. Which looks exactly like a
server that answers and then fails to send the content: I was two minutes from
filing a finding against the thing I had just fixed.

"Two status lines have arrived" is not "the second response is complete". It
reads to EOF now, the second request being the one that asks for the close.

### Measured against `416fcd06`

| | |
|---|---|
| `Tests.HTTP.` | **918** (912 + six) |
| the filter CI gates on | **919** |
| the protocol regression selection | 377, unmoved — the new tests are in a new file |
| this repository's gate | 9/9 harnesses, **311/311** (303 + eight probes) |

The pin is my own merge commit this time, with #91 and #92 (HTTP/3) behind it
and nothing of theirs under `HTTP1/`, `HTTP/`, `TCP/` or `DNS/`.

**And the ground moved again.** Between the branch point and the merge,
`NUnit.Analyzers 4.15.0` was added to `HermodTests` — so the merged tree runs
analyzers my files had never been compiled against, exactly as NUnit 5 had been
added under H-27 a few hours earlier. Measured: **no NUnit analyzer warnings at
all**, and none of any kind in the three files these two findings added.

That measurement needed a second attempt. The first build after the checkout
reported "0 warnings" — from an incremental build that had compiled nothing,
because the previous command had already built the same tree. A no-op build
reports a clean one. `-t:Rebuild` is what actually runs the analyzers, and the
honest figure for the solution is 358 warnings, all of them in the submodule's
own projects.

A third attempt, in fact: the first `-t:Rebuild` of the whole solution failed
with four `MSB3021`/`MSB3027` errors, because a background `dotnet test` still
held `testhost` open on the output directory. Four errors that were mine and not
the code's — worth the line, because "the build is broken" was the first thing
I thought.

### Three numbers this repository had stopped checking

The counts above are the ones H-23 moved. Re-measuring them turned up three
that nothing had moved and nobody had re-read:

| Claim | Said | Says now |
|---|---|---|
| `dotnet build HTTP1.slnx` | 0 warnings, 0 errors | 0 errors, **286 warnings** — all in the submodules' own projects, none in this repository's |
| `tests/run-tests.sh --tls` | 279/279, "`h1desync` drives the cleartext listener only" | **311/311, 9/9** — `h1desync` runs in either leg, and its 24 are the same 24 twice |
| the filter CI gates on | 832 | **919** — the row had stood through three pins |

The first is the interesting one, because it is how the claim survived: an
incremental build that compiles nothing reports no warnings, so every casual
check confirmed it. `-t:Rebuild` is the only form of that command which answers
the question the row was asking.

The `--wsl` leg is **485/485** over 12/12, measured rather than derived: 311
plus the Debian curl's 78, the five foreign peers' 58 and the smuggling
differential's 38.

And 1610 passing across HTTP/2, HTTP/3, WebSocket, TCP, Timers, Warden and DNS
against the same pin — which is #91's and #92's work, not this repository's, and
is reported here as "nothing broke" rather than as coverage.

## 2026-10-03 — Three small ones, and two notes that were wrong about themselves

H-28, H-11 and H-24, the XS group, in one branch and three commits
([Hermod#97](https://github.com/Vanaheimr/Hermod/pull/97)). What they have in
common is not their size: **two of the three had a note in `PLAN.md` explaining
why they were not being fixed, and in both cases the note was wrong.** That is
now the third such correction in two days, after H-23's citation, and it is
worth naming as a pattern rather than as three accidents.

### H-28 · the finding that verifies itself

Ten of eleven throw sites in `ChunkedTransferEncodingStream` raise
`HTTPInvalidChunkException`, a `FormatException`, so a caller can catch
"malformed input" without catching defects as well. `ReadCRLF` raised a bare
`System.Exception` for the same condition its asynchronous sibling thirty lines
above handled correctly.

The interesting part is the verification, and the harness had specified it in
advance. `tests/h1fuzz/known-findings.txt` says in its own header: *"Deleting a
line is how a fix gets verified: the finding comes back as NEW and the run goes
red if it was not actually fixed."* So the line came out, and the run that had
been reporting

```
chunked   known: Exception: Expected CRLF  (×165,015)
```

now reports `h1fuzz: no findings`. 165,015 inputs per budget reach that code
path, so this is a fix that was *measured* rather than inspected — and the file
is empty again, for the first time since A10 was built.

Its two tests went into `HTTP11AuditRegressionTests`, which the eleven-file
regression selection does name, so that count moved 377 → 379. H-27's five,
H-23's six and H-11's fourteen all went into new files and did not. Three times
in one week the same one-line gap in that filter, still unmade.

### H-11 · the right parser, in the wrong place, again

RFC 9110 §5.6.7 obliges a recipient to accept all three HTTP-date formats. The
date fields used `DateTimeOffset.TryParse`, which takes neither obsolete one —
and names no culture either, so what a `Date` field accepted depended on the
machine.

And a correct three-format parser already existed: in
`WebSocketClientReconnectPolicy.RetryAfter`, written for §5.6.7, reachable from
exactly one caller. **That is H-3's shape precisely** — RFC 7616 Digest was
unreachable for sitting in `HTTP2/` — and the reason `HTTPDate` went into
`Hermod/HTTP/` rather than `HTTP1/`: HTTP/2 and HTTP/3 carry the same fields
with the same semantics.

Two things a format string cannot do, and both came out of reading the section
rather than the code:

**The two-digit year.** §5.6.7: a timestamp "more than 50 years in the future"
means the most recent past year with the same last two digits — a window that
moves with the clock. `InvariantCulture`'s `TwoDigitYearMax` is 2049, so "55"
is 1955 whatever year it is read in; in 2060 that reads a timestamp five years
past as one a hundred and five years past. The century is chosen in `HTTPDate`
now, and `Now` is a parameter so the rule can be asserted instead of waited
for.

**The day name of the RFC 850 format is dropped rather than matched**, and that
one I found by writing a test that failed. `Saturday, 06-Nov-55` was refused,
and the reason was not the year logic: .NET validates a `dddd` against the
date, and the weekday it checks against belongs to whichever century the pivot
guessed. 6 November 1955 and 6 November 2055 are not the same day of the week,
so a correct timestamp is refused for the wrong century's calendar. The two
formats carrying a four-digit year stay strict, where the check means what it
says. The asymmetry is deliberate and has a test that states it.

A third thing turned up on the way out: `Last-Modified`'s serializer said
`ToISO8601()`, which is not an HTTP-date at all. Before changing it I measured
what the demo actually sends — `Last-Modified: Thu, 01 Jan 2026 00:00:00 GMT` —
and then found why: `AHTTPPDUBuilder.cs:157` serializes *every*
`DateTimeOffset`-valued header field with the `Date` field's serializer,
whichever field it is. So the wrong serializer was dead for the socket and
waiting for the first caller to serialize the field itself. A trap rather than a
bug, and worth the two minutes it took to tell the difference instead of
filing it as one.

### H-24 · a field name is not a reason phrase

Six status lines carried phrases predating RFC 9110. The row had been closed as
*a decision rather than a fix* for this reason:

> Renaming the fields is breaking for every downstream Vanaheimr project

`HTTPStatusCode` keeps the identifier and the phrase in two different places.
Downstream code compiles against `HTTPStatusCode.RequestEntityTooLarge`; the
status line carries `Name`, a string nothing links against. Five corrections
landed without one identifier changing:

| | was | is |
|---|---|---|
| 413 | Request Entity Too Large | **Content Too Large** (§15.5.14) |
| 414 | Request-URI Too Long | **URI Too Long** (§15.5.15) |
| 416 | Requested Range Not Satisfiable | **Range Not Satisfiable** (§15.5.17) |
| 422 | Unprocessable Entity | **Unprocessable Content** (§15.5.21) |
| 306 | Switch Proxy | **(Unused)** (§15.4.7) |

418 keeps `I'm a teapot`. RFC 9110 does not mention 418; the phrase is RFC
2324's, every stack implementing the code implements that phrase, and the
registry's `(Unused)` would discard the only thing anybody uses 418 for. The
line between the two reserved codes is whether the RFC has an opinion: for 306
§15.4.7 does, and it is followed. `HTTPStatusCodeTests` still pins the exact
divergence set, now `{418}`.

Before changing a single phrase I grepped the library, its tests, the harnesses
and the scripts for all six strings. A reason phrase is exactly the kind of
thing something matches on, and the answer — only the definitions and the test
that pins them — is what made this a five-minute change rather than a risk.

### Measured against `0044ecf7`

| | |
|---|---|
| `Tests.HTTP.` | **937** (918 + sixteen of these three, plus two from #94) |
| the filter CI gates on | **938** |
| the protocol regression selection | **379** (377 + H-28's two) |
| HTTP/2, HTTP/3, WebSocket, TCP, Timers, Warden, DNS, SMTP | **1726** |
| this repository's gate | 9/9, **311/311**, and `h1fuzz: no findings` |
| `dotnet build HTTP1.slnx -t:Rebuild` | 0 errors, 286 warnings, none in this repository's own projects, no NUnit analyzer warnings |

The 1726 is the row that had to be run rather than assumed: `HTTPStatusCode` and
`HTTPDate` are shared with HTTP/2 and HTTP/3, and a reason phrase is a string
something may well match on.

The pin also carries foreign HTTP/1 work for the first time in a while: **#94**,
"the upgrade request knows its connection", touched
`HTTP1/Request/HTTPRequest.cs` and `WebSocket/Server/AWebSocketServer.cs` —
which is the path the gate's four `/ws` upgrade checks run through, and they are
green. The rest between the pins is SMTP (#95, #96, #98–#100), which this
repository does not execute, and saying so is a statement about coverage rather
than about that code.

Track B is **20 of 31**, and the eleven still open are what is left of the state
analysis: H-5, H-7, H-9, H-12, H-13, H-14, H-15, H-17, H-18, H-19, H-20. None
holds a gate red; none is XS any more.

## 2026-10-04 — A red nightly that was about a race, and a stub that lied about Python

The scheduled nightly of 2026-10-03 failed its `autobahn-client` leg, and had
been red since. It was the first thing to look at when asked what was left to
do, and it is worth the entry because **nothing was wrong with the client**:

```
09:35:11.312  Asking the fuzzingserver to write its reports...
09:35:11.324  Ran 517 cases. Stalled: 0, threw: 0.
09:35:11.466  No index.json in .../reports-client -- the suite never wrote a report
```

`/updateReports` returned in **11 ms** for a 517-case report; the driver looked
for the file **142 ms** later and declared it missing. All 517 cases had run,
none stalled, none threw. The leg was red about a race, in a step that cannot
tell "the verdict is bad" from "there is no verdict" — the same conflation this
log keeps finding in its own checks, this time in the reporting of one.

The fix is in `tests/autobahn-client.sh`: wait for `index.json`, up to 120 s at
two polls a second, with readiness defined as *it parses* rather than *it
exists* — a half-written report exists too, and would come apart in the parser
below with a message about JSON instead of about timing. A report that never
arrives now exits **3**, not 1, and says that this is not a verdict about the
client.

The server-side driver keeps its single existence check, and that asymmetry is
now written down beside it so nobody levels it: there `docker run` is in the
foreground, so the container has exited before the check; here it stays up and
serving, and the report is written after `/updateReports` returns. Nine other
single-shot `[ -f ... ]` checks across `tests/*.sh` were looked at: seven are
build outputs, where nothing writes concurrently, and the only two about a file
a container writes are these.

### The race would not reproduce, so the branch was tested instead

Locally the suite came back 445/517 with 72 declined and no hard failures — the
documented figures — and the new "the report took *N*s" line **did not print at
all**. The file was there on the first look. The GitHub runner is slower and
more contended; this machine is not.

A guard whose trigger will not reproduce is a guard that has not been
exercised, which is the thing this repository has a convention about. So the
loop was driven directly against the four states it has to separate:

| | |
|---|---|
| a report already there | ready at once |
| none at all | times out, exit 3 |
| a half-written one | times out, exit 3 |
| one that appears after 3 s | **ready after 3.0 s** |

The third row is why readiness is a parse, and the fourth is the case the
nightly needed.

### And the test found what the suite run could not

On the first attempt **all four states failed**, including the one that should
pass immediately. The probe was fine; `python3` was not. On Windows,
`command -v python3` finds the Microsoft Store stub in `WindowsApps/`, which
prints an advertisement and exits 49 rather than running Python — so the
readiness check read it as "not ready yet", would have waited the full two
minutes, and would then have reported that the suite wrote no report.

A wrong answer rather than a missing one, and it only showed because the branch
was tested in the environment that has the stub. The script now asks python3 to
*run* rather than to exist, before anything else happens, which covers all
three of its uses at once: the container's port probe, this wait, and the
report's parse.

Verified end to end afterwards in WSL, where `python3` is real: a three-case
slice goes green through the new prerequisite check, the wait and the parse, and
the container log shows "Updating reports, requested by peer ... / Report
generation complete." for a report that needs no waiting at all.

The next scheduled nightly is at 03:37 UTC, about three hours after this
landed, which is where the fix meets the machine that produced the race.

### The nightly that followed, and the clock that is six hours off

2026-10-04, 10:04 UTC: **9/9 jobs green**, and the leg that had been red reports
445/517 passing, 72 declined, 0 hard failures — `INFORMATIONAL 3, NON-STRICT 12,
OK 430, UNIMPLEMENTED 72`, at the floor exactly.

**The new guard did not fire.** No "the report took *N*s" line in the log, so
the leg is green for the same reason it was green before the fix, not because of
it. One green run shows the fix broke nothing; it does not show that it works.
What shows that is the four-state test of the loop — the evidence has to come
from where the condition can be made to happen.

The timestamps are the interesting part, and they refine the diagnosis:

| | 2026-10-03 (red) | 2026-10-04 (green) |
|---|---|---|
| `/updateReports` → returned | **11 ms** | **596 ms** |
| report present when checked | no | yes |

Fifty-four times longer for the same call. That looks less like "the suite needs
a moment to write 517 cases" and more like the failing run's request having
returned without being processed — the 11 ms being the symptom rather than the
cause. The guard covers either reading, because it waits on the artefact instead
of trusting the call, which is why it was written that way and not as a fixed
sleep.

One failure in seven nights is the base rate, so the condition may not recur for
weeks.

### And while looking at the run times

`nightly.yml` asks for 03:37 UTC. It has never once run then. Measured across
the seven preceding scheduled runs of each of the four repositories:

| | cron | observed, 2026-10-04 |
|---|---|---|
| Hermod | 02:17 | 08:27 |
| HTTP/3 | 02:43 | 08:45 |
| HTTP/2 | 03:11 | 09:37 |
| HTTP/1 | 03:37 | 10:04 |

About **six hours** late, every night, all four. The comment on the `cron` line
had claimed GitHub's scheduler "delays runs by tens of minutes", which is what
being off the hour was supposed to mitigate; the measurement says otherwise and
the comment now says what was measured.

The useful half of that: **the stagger survives.** The four slip together and in
order, still 18–52 minutes apart, so the thing the stagger exists for — four
repositories not contending for runners — still holds, and these times are not
worth changing for that reason. What does change is a reader's expectation: a
"nightly" result is waiting at mid-morning UTC, not before breakfast. Also
worth knowing before anyone shifts one of the four by itself, which would walk
it into a neighbour.

Three documents said 03:37 as if it were when the run happens. They now say what
it asks for and what it gets.

## 2026-10-04 — Both submodules forward, and a TLS handshake that can now time out

Hermod `0044ecf7` → `d2d608d2`, **27 PRs** (#101–#127), and Styx `ba317094` →
`c530de16`, six commits. Almost all of the Hermod range is SMTP, which this
repository does not execute. Four files in it are ours, from three commits of
one idea:

| | |
|---|---|
| `HTTP1/Client/AHTTPClient.cs` | `TLSHandshakeTimeout => ConnectTimeout` |
| `HTTP1/WebSocket/Client/WebSocketClient.cs` | the same for WSS |
| `TCP/TCPClient/ATLSClient.cs` | where the timeout is applied |
| `DNS/Client/DNSTLSClient.cs` | and for DNS-over-TLS |

The reasoning upstream is worth repeating here, because it is the shape of
defect this repository has met twice: *"nothing else ended a handshake that the
server never answered: SendRequest waited for as long as its caller's token
allowed, and the background renewal, which passes none, for good."* An unbounded
wait on a peer that stops talking — the same mechanism as the harness hang of
2026-09-21, from the other end.

So the **TLS leg** was the measurement that mattered, not the cleartext one, and
a handshake timeout that is too tight would show up there as flakiness rather
than as a failure. It did not:

| | |
|---|---|
| `Tests.HTTP.` | **940** |
| the filter CI gates on | **941** |
| the protocol regression selection | 379, unmoved |
| gate, cleartext | 9/9, **311/311** |
| gate, `--tls` | 9/9, **311/311** |
| gate, `--wsl` | 12/12, **485/485** |
| HTTP/2, HTTP/3, WebSocket, TCP, Timers, Warden, DNS, SMTP | **1927** |
| `dotnet build HTTP1.slnx -t:Rebuild` | 0 errors, **273** warnings — 286 at the previous pin, and still none in this repository's own projects |

The three tests the handshake commits brought with them are in `Tests.HTTP.`
(`HTTPSClientHandshakeTimeoutTests`, `WebSocketClientHandshakeTimeoutTests` and
a `SilentTLSServer` helper to go with them), which is where 938 became 941.

Styx carries NUnit 5 and `NUnit.Analyzers` into `StyxTests` — the same move
HermodTests made on 2026-10-03 — and *"Every ParseOptional of a JObject keeps
one contract"*, which is shared Illias code this stack parses JSON with. Nothing
here reads it directly; it is in the 1927 by way of Hermod.

## 2026-10-04 — The XS group, and a fix that arrived from the other direction

H-17, H-9, H-12 and H-20 — the four XS findings left of the state analysis — in
one branch and one commit each ([Hermod#132](https://github.com/Vanaheimr/Hermod/pull/132)),
plus H-32, which this work produced and somebody else fixed while I was writing
it.

**Two of the four were not what their row said**, which is now the fourth and
fifth time in three days:

| | the row said | it was |
|---|---|---|
| H-12 | "No HSTS" | emission has existed for years through `SecurityHeaderOptions`, with a two-year default. The **value** was missing: nothing could read the field and nothing checked what was written into it |
| H-20 | "IPv6 zone identifiers in URIs" | not unsupported — **read wrongly**. `[fe80::a%25en1]` yielded zone `25en1`, a success with the wrong answer rather than a refusal |

H-20's is the worse kind. A refusal is a message; a link-local address dialled
through an interface that does not exist is a connection that fails somewhere
else, later, for a reason nobody can see from here.

### H-9 is now a decision, which is what the row asked for

It said: "deliberately not implemented is a valid answer, but then document
it". So TRACE stays unimplemented, and the answer changed from 405 to **501**:
§9.1 keeps 405 for a method "recognized and implemented, but not allowed for
the target resource", and this refusal is the server's for every resource at
once — a 405 whose `Allow` named GET, HEAD and OPTIONS invited a client to go
looking for a resource that allows TRACE.

The reason not to implement it is §9.3.8's own, and it is the sort of sentence
worth quoting at the code: a TRACE response carries the request's fields back,
so the recipient "SHOULD exclude any request fields that are likely to contain
sensitive data". That is a judgement about `Authorization`, `Cookie` and
whatever an application invented, which a library would make once, for
everybody, and wrong. A registered TRACE handler still decides — the default is
a default and not a prohibition.

### H-32: reported, and fixed by someone else within the hour

While giving the server an ALPN answer, `AllowedTLSProtocols` turned up next to
it: accepted by `HTTPServer`, kept, passed down to `ATCPServer` — and read from
a property of `TCPConnection` that **nothing anywhere assigned**. Null on every
connection ever made, so a server configured for TLS 1.3 alone went on
accepting 1.2. The doc comments admitted it: *"kept in AllowedTLSProtocols, but
not read yet"*, which is the honest version of a setting that does nothing, and
still a setting that does nothing.

It was not one of the four, so it went into #132's description rather than into
#132. That turned out to be the fast path: `7dc1d593` landed with the same
design — down to the default interface member "as `TLSApplicationProtocols`
is", which is #132's own ALPN property — before I had finished testing mine.

So my implementation went in the bin. **Its tests did not**, because that
commit says of itself *"built, not tested here"* and names a downstream OCPI
test as its evidence. A conformance finding closed by a test in another
repository is closed on somebody else's schedule, and nothing in `HermodTests`
touched `AllowedTLSProtocols` before or after. Three tests, in
[Hermod#133](https://github.com/Vanaheimr/Hermod/pull/133).

That is the second time this week that reporting a thing beside the thing was
worth more than fixing it: the first was #82's shared-HTTP change showing up as
a test count I could not explain until I looked.

### Two of my own errors

**A test that was green alone and red in the suite.** Not flakiness: I had
built `Hermod.csproj` and then run the tests with `--no-build`, so the run used
the older copy of the library sitting in `HermodTests/bin`. The same stale-binary
trap as this morning's "0 warnings" from an incremental build, in a different
costume. Build the project you are about to run.

**RFC 6874 §3 is not normative.** I had taken "remove the ZoneID before
including that URI in an HTTP request" for a MUST and was about to write that
into a comment; the section opens by saying it makes no normative statements.
It is "highly desirable", the code says so, and the difference matters because
the next reader would have believed me.

### Measured against `23d0dfd2`

| | |
|---|---|
| `Tests.HTTP.` / the filter CI gates on | **975** / **976** |
| the protocol regression selection | 379, unmoved — all five findings' tests are in new files |
| gate, cleartext | 9/9, **313/313** |
| gate, `--tls` | 9/9, **314/314** |
| gate, `--wsl` | 12/12, **487/487** |
| HTTP/2, HTTP/3, WebSocket, TCP, Timers, Warden, DNS, SMTP | **2000** — 1927 at the previous pin |
| `dotnet build HTTP1.slnx -t:Rebuild` | 0 errors, 278 warnings, none in this repository's projects, no NUnit analyzer warnings |

Two probes landed here as well: `TRACE → 501` in `h1semantics`, with a second
check asserting that the refusal reflects **no** header back out of the
request — a 501 that echoed one would be the §9.3.8 hazard without the
feature; and
the ALPN answer in the curl matrix, on the TLS leg alone. That last one is why
the two legs now differ by one — ALPN exists only inside a TLS handshake, so
there is no cleartext equivalent to run, and a matrix that pretended otherwise
would be the kind of symmetry this repository keeps warning itself about.

Track B is **25 of 32**. The seven open are H-5, H-7, H-13, H-14, H-15, H-18
and H-19 — one L and six S, and none of them XS any more, which this time is
measured rather than asserted.

## 2026-10-04 — H-18: the modernisation had happened to the copy

H-18's row said *"~4 000 lines of probable dead code"*, P3, clarify before the
harnesses depend on either. That is the sixth row in four days to be wrong
about itself, and the first where the error pointed the dangerous way: what it
called dead was partly the opposite.

`HTTP1/Server/URLMapping_old/` held 3 727 lines in seven files:

| | lines | live |
|---|---|---|
| `ContentTypeNode`, `HTTPMethodNode`, `HTTPStandardHandlers`, `HostnameNode`, `URL_Node` | 2 228 | one each — the `namespace` line |
| `HTTPStandardHandlersX.cs` | 1 453 | **848** |
| `URLReplacement.cs` | 46 | **19** |

`62edf0a3` (2026-04-20, *"Deprecated old HTTP implementation in favour of new
one!"*) had created the directory by moving `URLMapping/` into it, every file
already commented out; the two commits since only carried it along in tree-wide
moves. One file came back to life as a copy called `HTTPStandardHandlersX`, and
the modernisation then happened **to the copy** — extension methods on today's
`HTTPAPI`, `HTTPExtAPI` and `HTTPServer` — while the deprecated twin kept the
plain name. That was the suspicion when the two member lists were laid side by
side — there should only ever have been one `HTTPStandardHandlers.cs` — and the
lists bore it out: the copy has every member of the twin but two `IHTTPServer`
overloads it replaced with `HTTPAPI` ones, plus `Logger`, a third redirect
handler and a fourth folder overload.

### A search that could not have found anything

My first check for callers searched for the class names, found none outside
the directory, and I reported "six of seven are unreachable". For five that was
true. For `HTTPStandardHandlersX` the search was **structurally incapable** of
finding a caller: extension methods are called as `httpAPI.MapSomething(…)` and
never by the name of their class. Searching for the member names instead found
eight files in five other repositories — Norn's three among them, committed
yesterday — and four tests in Hermod itself, one of them inside the 975 CI
gates on. The code in `_old` was being executed by the gate.

The anti-vacuity check that made the *other* zeros trustworthy was cheap: the
same globs on `URLReplacement`, a type known to be live, returned seven
downstream files. A filter that sees nothing would have returned none.

### Two PRs, because the first was merged while I was correcting it

[Hermod#138](https://github.com/Vanaheimr/Hermod/pull/138) moved the two live
files into `URLMapping/`, renamed the class back to `HTTPStandardHandlers`, and
deleted the five comment-only files — and with them the seventh RFC 2616
citation that H-8 had left because it sat in commented-out code. The rename is
invisible to every caller: extension methods resolve by namespace and
signature, and the namespace is the same on both sides.

The tests are surface tests, because a move cannot change behaviour but can
lose an overload, and that breaks a caller in another repository at compile
time where this suite never looks. They reach the class **by name** rather
than through `typeof`, so the fixture compiles against the old assembly and
"red before" is a measurement rather than "would not have built": renaming the
class back turned 4/4 red.

It was merged as `6dae1d9d` while I was amending it, because recounting the
coverage gap for this entry had shown my own figure wrong: I had written that
four of the eleven overloads have no caller. My count had included the new
fixture's list of expected names — the test pinning the methods was the only
thing "calling" them. It is four *methods* and **seven** *overloads*.

[Hermod#143](https://github.com/Vanaheimr/Hermod/pull/143) carried that
correction, and Achim's two follow-ups:

- **the last `X`.** `HTTPRequestHandlersX` → `HTTPRequestHandlers`, fourteen
  occurrences in five files and no name anywhere outside Hermod. The one thing
  that looked like a conflict was not: `MethodNode` already has a *property*
  of exactly that name, which C# allows, since one is read in type position and
  the other in expression position.
- **Apache 2.0 throughout.** Three files were GPL v3. A fourth,
  `HTTPEventSourceTests.cs`, was **AGPL** — found only on a second pass,
  because the first searched for the GPL's wording and the Affero header is
  worded differently. Seven had no header at all, all written in Hermod by
  git's own record. Six files of `Hermod/IP/` stay headerless on purpose: added
  in 2011 as *"Some RAW IP stuff..."*, they read like the Windows SDK's
  raw-socket samples, and whether they are GraphDefined's to license is not
  something a script can settle.

### H-33, the gap the fixture names

Seven of `HTTPStandardHandlers`' eleven overloads — the file and
embedded-resource serving, and all three on `HTTPExtAPI` — have no caller in
Hermod's tests, while Norn and HTTPSSETests call them. That would be a P3
coverage note but for two of them mapping URLs onto the file system, where the
classic defect is a security defect. On reading, `MapFileSystemFolder`'s
traversal guard is `GetFullPath(Combine(root, path)).StartsWith(root)`, which
without a trailing separator also lets a sibling directory sharing the prefix
through; `RegisterFileSystemFile` opens whatever path a caller-supplied builder
makes of the raw URL parameters. Both are hypotheses, not results: whether
`..` reaches them at all depends on what the request-target parser has
normalised first, and that is the first thing a test has to establish. Filed
P2.

### My own mess

Every Python patch in this session read files as `utf-8-sig` and wrote them
back the same way, which **adds a byte-order mark** to any file that had none.
Found when the first three bytes of `PLAN.md` stopped matching `HEAD`: here,
`CLAUDE.md`, `PLAN.md` and this log, all stripped again in this commit; in
Hermod, its `HTTP1/README.md` and six `.cs` files, already merged with #138 and
#143. Hermod's `.cs` files are split 643 to 1033 on BOMs, so those six are
harmless; the README is the odd one out of sixteen. They go back in the next
Hermod PR rather than in a pin bump of their own.

They went back in [Hermod#145](https://github.com/Vanaheimr/Hermod/pull/145),
checked against the first three bytes each file had before #138, together with
the Apache header for the six raw-IP files under `Hermod/IP/` that were the last
sources without one. Those six have a BOM of their own, and they keep it.

### Measured against `04dca36d`

The pin is not `a0abb530`, the merge of #143, but two merges further on. #145
was merged while the measurement ran, and another session's
[Hermod#144](https://github.com/Vanaheimr/Hermod/pull/144) (SMTP, DANE)
landed between the two. Nothing was pinned unmeasured, so everything below was
run again against `04dca36d`.

| | |
|---|---|
| `Tests.HTTP.` / the filter CI gates on | **980** / **981** — H-18's five |
| the protocol regression selection | 379, unmoved |
| `Tests.HTTP.WebSockets` | **154** — the README said 149 since `bd34db7c`. Two WebSocket commits of 2026-10-03 added to it, and three pin bumps since then never re-ran that row |
| gate, cleartext | 9/9, **313/313** |
| gate, `--tls` | 9/9, **314/314** |
| gate, `--wsl` | 12/12, **487/487** |
| HTTP/2, HTTP/3, WebSocket, TCP, Timers, Warden, DNS, SMTP | **2083 of 2084** |

The one failure in that last row is `DANE_EE_authenticates_the_next_hop`. It
fails five times out of five on its own, always after 17 s, with `TempFail`,
"All MX hosts unreachable", and the connection aborted by the host. It fails
the same way at `a0abb530`, so neither #144 nor #145 caused it. It did not
exist yet at `23d0dfd2`. It came with `aa5daed3`, merged as
[Hermod#136](https://github.com/Vanaheimr/Hermod/pull/136) in the other
session's work, so this is the first time it has run on this machine, and it
has never passed here. That is SMTP, and outside what this repository measures
for. It is recorded here, and in the row in
`CLAUDE.md`, rather than fixed here.

A first run of the CI filter here read 980 of 981. The one was
`SendTwoTextFrames_Slow_Test`, failing after 114 ms while I was building and
testing in a second worktree beside it. Alone it passed twenty times out of
twenty. Its asserts read server-side event flags right after the client's
`Connect()` returns, which is a race that only load can lose. The run above is
the clean one; nothing else ran beside it.

Two measurement scripts of mine also failed. The first printed only the last
lines of each gate leg and lost the check counts. The second piped through
`tee /dev/stderr` into a file that stdout already wrote to, so the second
handle wrote from offset zero over everything before it. Only the WSL leg
survived. Everything above comes from the third script, which ran each step on
its own.

### H-33, measured instead of read

Both hypotheses in the H-33 row were wrong, and the defect was somewhere
else. `..` never reaches the handler: the request parser answers `400` to
every spelling tried. A colon does reach it, and on Windows
`GET /files/C:secret.txt` served a file from the sibling directory, by way of
`Path.Combine` and the separator-less `StartsWith` together. Any missing file
was a `500` carrying the absolute path on the server. The fix is in review
as [Hermod#152](https://github.com/Vanaheimr/Hermod/pull/152). Its full story
belongs to the pin bump that includes it.
