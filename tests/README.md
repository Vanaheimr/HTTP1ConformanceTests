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

**257/257 checks pass, over both transports** — 199 raw-wire + 58 curl.
With `--wsl`, a second curl build joins in: **315/315**.

| Harness | Checks | Covers |
|---|---:|---|
| `curl-matrix.sh` | 58 | **third-party**: version handling, methods, framing, `Expect`, connection reuse, conditionals via curl's own ETag store, ranges, negotiation, auth incl. `--anyauth`, `--compressed`, redirects, curl's exit codes |
| `h1syntax` | 32 | RFC 9112 §2–3, RFC 9110 §5 — request line, request-target forms, version syntax, field syntax, `obs-fold`, `Host`, limits, fragmented delivery |
| `h1framing` | 51 | RFC 9112 §6–7 — the body-length algorithm, `Content-Length` validity, CL+TE, transfer codings, chunk syntax, chunk extensions, trailers, response framing |
| `h1conn` | 20 | RFC 9112 §9 + RFC 1945 — persistence per version, `Connection` tokens, pipelining and ordering, reuse after bodyless replies, half-close |
| `h1semantics` | 63 | RFC 9110 + RFC 10008 — methods, `Allow`, conditionals, ranges, negotiation, auth, QUERY, `Expect: 100-continue`, redirects |
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
tests/run-tests.sh --wsl        # 315/315 — both curl builds
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

## Not here yet

`PLAN.md` tracks the rest: curl (A3), Autobahn (A4), reverse proxies (A5),
http-garden and the smuggling scanners (A6), non-.NET reference peers (A7),
browsers (A8), benchmarks (A9), parser fuzzing (A10), CI (A11).
