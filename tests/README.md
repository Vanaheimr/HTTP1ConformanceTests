# Tests

Raw-wire conformance harnesses for the hand-rolled HTTP/1.x stack. Every check
here sends bytes that no HTTP client will produce — duplicate `Content-Length`
fields, obsolete line folding, chunk sizes that lie, request targets with null
bytes — which is why they are raw sockets rather than `HTTPClient`.

## Running

```bash
tests/run-tests.sh                  # build, start the demo, run everything
tests/run-tests.sh --no-build       # skip the build
tests/run-tests.sh --filter attack  # only harnesses matching *attack*
tests/run-tests.sh --tls            # drive the TLS listener instead
tests/run-tests.sh --keep-demo      # leave the demo running afterwards
```

Bash rather than PowerShell: CI runs on Linux, and Git Bash makes the same
script work on Windows. One script, no drift between two copies.

The runner builds the solution, starts the demo host, runs each harness as its
own process, and prints a summary. Each harness self-reports its check count and
exits non-zero on any failure, so the runner relies on exit codes and never
scrapes output for a marker character.

## Status

**311/311 checks pass, over both transports** — 209 raw-wire + 78 curl + 24
desync — across 9/9 harnesses, two of which need no demo response at all: the
fuzzer's fixed-seed pass and `h1desync`'s assertions. With `--wsl`, a second
curl build, the foreign peers and the smuggling differential join in:
**485/485** over 12/12.

The curl figure is 78 **here** and 79 on the CI Debian leg, and that is not a
discrepancy to reconcile. Two of the matrix's checks are conditional, and a
third is always present but expects a different answer per build:

| Check | Runs when |
|---|---|
| `--http1.1 honoured by an HTTP/2-capable curl` | the curl under test was built with nghttp2 — a build that *cannot* upgrade proves nothing by not upgrading |
| `curl stored the ETag` | the target is local, so `--etag-save` writes somewhere this script can read |
| `--digest SHA-256` | *always runs*, but expects 200 from a build that implements RFC 7616 with SHA-256 and 401 from one that does not. curl 8.14 (OpenSSL) does; curl 8.21 (Schannel) sends no `Authorization` at all. Predicted from the TLS backend in the version banner and then asserted, so a build that breaks the pattern fails rather than being quietly accommodated |

So 77 checks always run, and the two counting flags give three real combinations:

| Context | HTTP/2 | local target | curl checks | suite |
|---|---|---|---:|---:|
| Windows / Git Bash, and the CI Windows leg | no | yes | 78 | **279** |
| the Debian curl reached through WSL (`--wsl`) | yes | no | 78 | — |
| the CI `debian:13` container | yes | yes | 79 | **280** |

The first two agree at 60 for opposite reasons, which is worth knowing before
someone reconciles them into one number.

| Harness | Checks | Covers |
|---|---:|---|
| `curl-matrix.sh` | 78–79 | **third-party**: version handling, methods, framing, `Expect`, connection reuse, conditionals via curl's own ETag store, ranges, negotiation, auth incl. `--anyauth`, `--compressed`, redirects, curl's exit codes |
| `h1syntax` | 32 | RFC 9112 §2–3, RFC 9110 §5 — request line, request-target forms, version syntax, field syntax, `obs-fold`, `Host`, limits, fragmented delivery |
| `h1framing` | 51 | RFC 9112 §6–7 — the body-length algorithm, `Content-Length` validity, CL+TE, transfer codings, chunk syntax, chunk extensions, trailers, response framing |
| `h1conn` | 20 | RFC 9112 §9 + RFC 1945 — persistence per version, `Connection` tokens, pipelining and ordering, reuse after bodyless replies, half-close |
| `h1semantics` | 65 | RFC 9110 + RFC 10008 — methods, `Allow`, conditionals, ranges, negotiation, auth, QUERY, `Expect: 100-continue`, redirects |
| `h1sse` | 16 | WHATWG SSE — stream head, framing, `Last-Event-ID` replay, mid-stream abort, concurrent subscribers |
| `h1attack` | 17 | RFC 9112 §11.2 + hardening — CL.TE / TE.CL desync, response splitting, slowloris, oversized bodies, chunk-metadata and trailer floods |
| `h1raw` | — | diagnostic: send an arbitrary request, dump the reply with control characters made visible. Not in the gate |

Wall-clock: **~103 s cleartext**, **~270 s over TLS** (≈250 TLS handshakes plus
encrypted bulk transfer). The cleartext run is the CI gate; the TLS run is worth
doing before a release.

## The curl leg

`curl-matrix.sh` is the first thing in this gate that is not our own code.
Everything else establishes something about an implementation written here; curl
establishes that an independent one agrees. It runs standalone too:

```bash
tests/curl-matrix.sh                                        # cleartext
tests/curl-matrix.sh --base https://127.0.0.1:8443 --insecure
tests/curl-matrix.sh --curl /usr/bin/curl --label "debian"  # a different build
```

Three checks are worth calling out because they only work with a real client:

- **`--anyauth`** makes curl probe, parse `WWW-Authenticate`, and pick a scheme.
  Passing means our challenge is not merely *present* but *parseable* by an
  implementation that did not write it.
- **`--etag-save` / `--etag-compare`** round-trips the validator through curl's
  own store rather than through a string we constructed.
- **`-T -`** makes curl chunk the *request*: with no length known up front it
  must use `Transfer-Encoding`, so this is a real client emitting real chunks —
  a different claim from our harness emitting hand-written ones.

**One check is a pinned expected failure.** `--digest` returns 401, because
`HTTPDigestAuthentication` is not RFC 7616 (`PLAN.md`, **H-3**). It is asserted
as 401 on purpose: the day H-3 is fixed, that check turns red and says so.

### The second curl — `--wsl`

Debian carries curl 8.14 **with** nghttp2/nghttp3. That build is the more
interesting witness: a client that *could* upgrade and does not proves ALPN
negotiation in a way the Windows build (no HTTP/2 at all) structurally cannot.

```bash
tests/run-tests.sh --wsl        # 357/357 — both curl builds
```

`--wsl` starts the demo with `--bind-any` (0.0.0.0 instead of loopback) so the
WSL VM can route to it. **Opt-in**, because a plain test run must never widen a
listener as a side effect; without it the runner prints
`SKIP … pass --wsl`, since a silent skip is indistinguishable from a pass.

No firewall rule turned out to be necessary — WSL's vSwitch reaches the host
once the listener is not loopback-bound. **This also unblocks A5 and A6**, whose
containers need exactly the same reachability.

#### Two layers of argument mangling, neither of them curl's

Running a Linux binary through `wsl -d Debian -- curl` from Git Bash sends every
argument across two boundaries, and each mangles a different thing. Both produce
failures that look exactly like conformance findings:

| | What it does | Fix |
|---|---|---|
| Git Bash (MSYS) | rewrites POSIX-looking arguments into Windows paths — right for a Windows curl, fatal for a Linux one, which then saves its ETag to a path that does not exist inside WSL and silently stores nothing | `MSYS2_ARG_CONV_EXCL='*'` |
| `wsl.exe` | hands the arguments to a shell on the Linux side, which **globs** them. A bare `*` request target expands to the caller's directory listing — `wsl -d Debian -- echo '*'` prints the repo contents — and curl then treats the filenames as URLs and dials out to whatever resolves | escape it: `\*` |

Both are needed together: the first alone leaves the glob, the second alone
leaves the path. Stdin redirection is unaffected by either — a pipe is not a
path, which is why the `-T -` chunked-upload check works unchanged.

## What the harnesses actually test

Two things worth stating plainly, because both are easy to misread later.

**`h1semantics` verifies the demo as much as the library.** Hermod exposes
`If-None-Match`, `Range`, `Accept` and friends as typed fields and applies no
policy of its own — it is an origin server, and the resource decides what its
own preconditions mean. The 304s, 206s and negotiated variants are produced by
`Demo/Program.cs`. A harness reporting "no automatic 206" would be reporting a
design decision; what these checks can honestly establish is that an origin
server built on this library *can* implement RFC 9110 correctly.

**A single origin server cannot exhibit a TE.TE desync.** Request smuggling is a
disagreement between two parsers, and there is only one here. The first version
of those checks asserted that a trailing `GET` must not be answered, which was
simply wrong: after a well-formed terminal chunk those bytes are a legitimate
pipelined request, and answering them is correct HTTP/1.1. What one server can
be held to is that the message boundary is *deterministic* — either the message
is refused, or it is read as chunked and the remainder is exactly one pipelined
request. The real differential needs two implementations disagreeing, which is
[http-garden](https://github.com/narf-industries/http-garden) — `PLAN.md`, A6.

The CL.TE and TE.CL checks are meaningful against one server, because RFC 9112
§6.1 forbids that combination outright: a smuggled request being answered there
would prove the server picked one field and ignored the other.

## Timeouts

The runner starts the demo with `--fast-timeouts`, which shortens its header and
body read deadlines from 30 s to 3 s. That changes how long the harness waits,
not what it asserts: the claim under test is "an incomplete message eventually
yields 408", never a particular number of seconds. Without it, each of the five
timeout checks would cost half a minute.

## Writing a new check

`H1Core` holds the shared pieces:

- **`RawConnection`** — connect (TCP or TLS), send bytes verbatim, read a
  response. `ReadResponseAsync` returns as soon as the response is complete
  *by its own framing*; `ReadAsync` reads until close or window expiry (use it
  when you need several responses, e.g. pipelining); `ReadHeadersAsync` returns
  on the header terminator (use it when you are measuring server latency).
- **`Checks`** — `Status`, `Contains`, `DoesNotContain`, `Closed`, `That`, plus
  `ResponseCount` for pipelining. `Summary()` prints the verdict and returns the
  exit code.
- **`Target`** — `--host`, `--port`, `--tls` parsing, so any harness can be
  aimed at the TLS listener or at a proxy in front of the demo without a rebuild.

Two traps that already cost debugging time here, both now handled by `H1Core`
but easy to reintroduce:

- **Reading past the response.** HTTP/1.1 is persistent, so the server does not
  close after answering. A read that waits for a close waits out its whole
  window on *every* check — across ~200 checks that is minutes of pure harness
  delay. Use `ReadResponseAsync` unless you specifically need more.
- **Writing without a deadline.** These harnesses deliberately send payloads the
  server should reject, and a server that rejects a large body stops reading it.
  An unbounded write then blocks once the send buffer fills — invisibly over
  cleartext, where the socket errors out quickly, and as a hang over TLS.
  `SendAsync` is bounded and treats a refused write as data (`WriteWasRefused`),
  not as an error. The same rule applies to the shell leg: every curl in
  `curl-matrix.sh` carries `--max-time` / `--connect-timeout`, set once in
  `$TIMEOUTS` rather than per check.
- **Reading *too little* in the smuggling checks.** The opposite trap, and the
  nastier one, because it fails silently *green*. `ReadResponseAsync` stops at
  the end of the first response — but the whole question in `h1attack` is "did a
  *second* response appear", and a reader that stops after the first can never
  see one. Those checks use `ReadEverythingAsync` for that reason. Switching
  them to the fast reader does not make them fail; it makes them tautologies
  that pass whatever the server does.

Pass several acceptable status codes to `Checks.Status` where the RFC says a
recipient MUST reject something without saying how — pinning one code there
asserts Hermod's taste rather than the standard.

## Autobahn (A4)

`tests/autobahn.sh` drives the canonical RFC 6455 suite — 517 cases — against the
demo host's WebSocket echo server on `:8081`. Docker is the only prerequisite.

Currently **481/517**. Everything except compression parameter negotiation passes;
the 36 that do not are sections 13.3 and 13.5, where the client offers
`server_max_window_bits=9` — asking the server to cap its own compression window —
and the server declines, because .NET's `DeflateStream` cannot set `windowBits`.
RFC 7692 §7.1.2.1 requires declining an offer that cannot be satisfied, so this is
correct behaviour and Autobahn says `UNIMPLEMENTED` rather than `FAILED`.

It is scheduled nightly for 03:37 UTC — and starts around 10:00 UTC, the whole
family of four running about six hours late; see the `cron` comment in
`.github/workflows/nightly.yml` — and gates on a **floor** of 481 rather than on
perfection: passing must not drop below it, and a single hard failure (`FAILED`,
`WRONG CODE`, `UNCLEAN`) fails the run whatever the count says. Excluding the two
sections to buy a green badge would have been the alternative, and it is the worse
one — an exclusion hides the cases, a floor keeps counting them. See
[`TestingAgainst_Autobahn.md`](TestingAgainst_Autobahn.md) for the full account,
including what the first run found and why this suite belongs in this repository
rather than only in the HTTP/2 sibling.

## The peers (A7)

`tests/interop.sh` drives two directions that curl cannot cover on its own.

**Foreign clients against our server.** `tests/peers/client.go`, `Client.java`,
`client.mjs` and `client.py` each run the *same* ten checks — baseline, chunked
body, trailers, gzip round-trip, HEAD-matches-GET, `Range` → 206,
`Accept-Ranges`, 404, redirect, reuse — so the matrix is comparable across four
independent HTTP stacks. `wget` adds three more. Each program prints
`PASS`/`FAIL`/`SKIP` lines and the driver counts them; a peer that cannot make
a check says SKIP with the reason, because a silent pass and a silent skip look
the same in a log a week later.

Three of those skips are real and worth knowing: `java.net.http` and Python's
`http.client` do not expose the trailer section at all, and `node:http` follows
no redirects.

**Our client against foreign servers.** `tests/peers/server.go` and
`server.mjs` serve the same five routes framed by Go's and Node's stacks;
`tests/h1peer` is our `HTTPClient` reading them. This is the direction that had
no witness before — the server had been judged by curl, by Autobahn and now by
five clients, while the client had only ever talked to a server from the same
source tree.

The peers are stdlib-only on purpose: `go run`, `java Client.java`, `node`,
`python3`. Nothing is fetched and nothing is installed, so a clean checkout
needs the runtimes and nothing else.

On Linux everything runs natively. On Windows the toolchains live in the Debian
WSL distribution — the same one the second curl comes from — so the demo must
be bound to `0.0.0.0`, which is what `--wsl` does. The second direction is
loopback *inside* the peer environment and crosses no VM boundary, which is
also why it needs no special handling in CI.

```bash
tests/interop.sh --base http://127.0.0.1:8080     # both directions
tests/interop.sh --only go                        # one peer
tests/run-tests.sh --wsl --filter peers           # with the demo lifecycle
```

CI runs it nightly on `ubuntu-latest`, which ships all five runtimes. It is
deliberately not a push gate: the Debian container ships none of them, and a
gate whose check count moves with the runner image is worse than no gate.

## The benchmark (A9)

`tests/h1bench` is not a gate and is not in CI. It is the baseline an
optimisation has to beat, and the thing to re-run before believing one worked.

```bash
dotnet run -c Release --project tests/h1bench             # everything
dotnet run -c Release --project tests/h1bench -- connect  # one scenario
dotnet run -c Release --project tests/h1bench -- --mib 256
```

Client and server share one process over loopback, so every figure covers both
roles. The one comparison worth quoting is the control: .NET's `HttpClient`
against our server and against Kestrel, same loopback, same process, one after
the other — **0.240 ms** p50 against **0.252 ms**. An absolute number with
nothing beside it is how "slower than I expected" becomes "slow".

See A9 in [`PLAN.md`](../PLAN.md) for the full table and the three things the
numbers said that the code did not — one of which became finding **H-27**, now
fixed upstream: the `connect` scenario's third row, a fresh client per request
with one shared `DNSClient`, used to be 36× faster than the second and is now
worth nothing at all. That row is there to tell "the connection is expensive"
from "the constructor is", and it earned its place.

## The fuzzer (A10)

`tests/h1fuzz` throws malformed input at three parsers — `HTTPRequest.TryParse`,
`HTTPResponse.TryParse` and `ChunkedTransferEncodingStream` — at roughly one to
two million inputs per target per minute.

```bash
dotnet run -c Release --project tests/h1fuzz                   # 30 s per target
dotnet run -c Release --project tests/h1fuzz -- --seconds 600
dotnet run -c Release --project tests/h1fuzz -- chunked --seed 1234
dotnet run -c Release --project tests/h1fuzz -- --replay artifacts/h1fuzz/<file>
tests/run-tests.sh --filter fuzz                               # as the gate runs it
```

A finding is **not** "the parser rejected it": that is the correct answer to
almost all of this input, and counting rejections would produce noise rather
than signal. A finding is an exception the target does not promise — a
`TryParse` promises a Boolean, so anything it throws counts; the chunked
decoder promises to refuse malformed framing, so `IOException` and
`FormatException` are it keeping its word and `NullReferenceException` is not —
or an input over the deadline, or output out of all proportion to input.

Findings are deduplicated by signature, because one defect reached by 386,214
inputs is one defect. `known-findings.txt` lists the ones already filed: they
are still printed, with their count, but do not fail the run. Deleting a line
there is how a fix gets verified — the finding comes back as NEW if it was not
actually fixed.

In the gate the seed is fixed and the budget is five seconds per target, which
makes it a regression test: the same inputs every run, so red means this change
broke something rather than that today's dice were unkind. The nightly moves
the seed with the date and runs ten minutes per target.

It is deliberately not SharpFuzz driven by AFL++, which is what `PLAN.md`
originally asked for; A10 there says why, and what would have to be installed
to get the stronger instrument.

## The smuggling differential (A6)

A single origin server cannot smuggle a request past itself: the attack is a
*disagreement* between two parsers, so no instrument pointed at one
implementation can see it. A6 is four instruments, written up in full in
[`../docs/TestingAgainst_Smuggling.md`](../docs/TestingAgainst_Smuggling.md).

```bash
tests/run-tests.sh --filter desync             # the 24 assertions, gate
dotnet run --project tests/h1desync -- --list  # the catalogue, with citations
dotnet run --project tests/h1desync -- --only cl-te
tests/smuggle.sh                               # hermod vs go vs node
tests/smuggle.sh --update                      # rewrite the known file
tests/smuggler.sh                              # smuggler + h2csmuggler
tests/http-garden.sh --contract                # is our Garden target still valid?
```

**`tests/h1desync`** sends 38 ambiguously framed messages and splits them in
two, which is the design rather than a detail of presentation. 24 carry a
normative sentence from RFC 9112 — quoted in `ProbeCatalogue.cs` next to the
payload it governs — and those are asserted. The other 14 do not: §6.1 says a
server **MAY** reject a request carrying both Content-Length and
Transfer-Encoding "or process such a request in accordance with the
Transfer-Encoding alone", and an assertion there would be this repository's
taste dressed as conformance. Those are observed, and they are exactly the
probes a smuggling chain is built from, so they go to the differential.

Three of the assertions run the other way — `te-ows-spaces`, `te-ows-htab`,
`te-mixed-case` are well-formed chunked requests in unusual but legal
clothing and **must** be decoded. A harness that only ever demands rejection
gives its best score to a server that rejects everything.

**`tests/smuggle.sh`** runs `h1desync --observe` against Hermod, Go's
`net/http` and Node's `node:http` and joins the answers on the probe id. The
vocabulary is deliberately coarse — `REJECT`, `ONE`, `TWO`, `NONE`, plus `!`
for a closed connection — because two servers never produce identical bytes
and a richer comparison would report a difference on every row and mean
nothing. 28 rows agree; the 10 that do not are recorded in
`smuggle-known.txt`, and a row that is not in that file, or whose
disagreement changed shape, fails the run.

**`tests/smuggler.sh`** brings payload sets nobody here designed:
[smuggler](https://github.com/defparam/smuggler) sweeps 134 obfuscations of
the Transfer-Encoding line as CL.TE and TE.CL, detecting by timing;
[h2csmuggler](https://github.com/BishopFox/h2csmuggler) must keep finding no
h2c upgrade to tunnel through. Both are cloned on demand into
`thirdparty/` and pinned.

**`tests/http-garden/`** is Hermod as a target in
[the HTTP Garden](https://github.com/narfindustries/http-garden), which
compares parse trees field by field across 45 implementations. Neither a gate
nor a nightly: the Garden builds every target from source with clang and
ASan, which is compiler-hours. `--contract` checks that our target still
answers the Garden's format without needing a single peer.

## The proxies (A5)

Five reverse proxies in front of the demo host — nginx, HAProxy, Caddy, Apache
httpd, Envoy — because they are the strictest HTTP/1.1 consumers there are,
and because a smuggling gadget is only real in a chain.

```bash
tests/proxy.sh                     # brings them up, runs, tears them down
tests/proxy.sh --keep --only nginx
tests/proxy.sh --filter framing    # curl | framing | poison | via
tests/proxy.sh --update            # rewrite tests/proxy-known.txt
```

Every image is pulled rather than built, so this costs a minute of docker pull
rather than the compiler-hours the HTTP Garden does. It still takes about ten
minutes to run, which is why it is a nightly.

Four measurements. **curl** runs the whole 78-check matrix through each chain
against 78/78 direct; **framing** runs `h1desync --observe` through each chain
against the same run made directly; **poison** sends an ambiguous message and
then asks innocent questions on fresh connections; **via** checks the
intermediary-facing things visible from outside — a chunked body arriving
intact, a trailer surviving a re-framing hop, `Connection: close` honoured.

63 differences are recorded by name in `proxy-known.txt` and a new one fails
the run; a row that stops differing is reported so the line can be deleted.

The poison column needs reading with its calibration, which is in
[`../docs/TestingAgainst_Proxies.md`](../docs/TestingAgainst_Proxies.md): all
five proxies discard an upstream connection carrying an unexpected extra
response, so the detector has never been seen to fire through any of them.
"Clean" therefore says more about the proxies than about the origin.

## The browsers (A8)

Three engines — Chromium, Firefox and WebKit — through Playwright, because a
browser is the one consumer in this repository that was not written to be a
test.

```bash
tests/browser.sh                  # starts its own demo, all three
tests/browser.sh --browser webkit
tests/browser.sh --headed         # watch it
tests/browser.sh --no-install     # skip rather than fetch half a gigabyte
```

The battery runs inside the demo's own `/` document rather than a page served
for the occasion, so every fetch, `EventSource` and `WebSocket` is same-origin
from a real page. **24 of 27 pass.**

Four checks are things nothing else here can establish: `nextHopProtocol` is
the browser's own verdict that it spoke HTTP/1.1; `EventSource` is the client
half of SSE rather than a read of the body; `connectStart === connectEnd` is
the browser's account of keep-alive; and CORS cannot be tested with curl at
all, because curl sends the request and is answered.

The three failures are one finding — **H-10**, no automatic CORS preflight —
recorded in `browser-known.txt` with its number. A failure not in that file
fails the run.

## Not here yet

`PLAN.md` has no third-party suite left open. CI (A11)
landed on 2026-09-22; the reference peers (A7), the benchmark (A9) and the
fuzzer (A10) on 2026-09-26; the smuggling differential (A6) on 2026-09-27.
