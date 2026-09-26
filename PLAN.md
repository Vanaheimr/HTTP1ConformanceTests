# Work plan — HTTP/1.1 Conformance Tests

Derived from the state analysis in [`README.md`](README.md). Two independent
tracks:

- **Track A — this repository**: demo host, raw-wire harnesses, third-party
  suite drivers, CI.
- **Track B — the Hermod submodule**: the gaps the analysis surfaced in the
  stack itself. Each is a change under `libs/Hermod/` that goes upstream — see
  the workflow below.

**Status legend:** ✅ done · 🔶 partial · ⬜ open · ❌ broken — markers are kept
current as work proceeds.

**Current state (2026-09-26):** **A0 ✅**, **A1 ✅** (demo host, 3 listeners,
18 routes), **A2 ✅** (6 harnesses), **A3 ✅** (curl) — **279/279 checks green
over cleartext *and* TLS**. **A4 ✅** — both directions driven and gated nightly: server 481/517, client
445/517, zero hard failures either way. **A7 ✅** — five foreign clients and two
foreign servers, 58/58, nightly. **A9 ✅** — `tests/h1bench`, with Kestrel as the
control. **A11 ✅** — CI per push on two legs,
nightly for both Autobahn directions. Track B: **27 findings, 11 fixed upstream**
and pinned here, all of them whole — H-4, H-6, H-8, H-21 and H-22 landed
together with [Hermod#43](https://github.com/Vanaheimr/Hermod/pull/43) on 2026-09-26, one commit each and the
citation sweep last so that it covered what the other four added.

| Gate | State |
|---|---|
| `dotnet build HTTP1.slnx` | ✅ 0 warnings, 0 errors |
| `tests/run-tests.sh` | ✅ 279/279, ~103 s |
| `tests/run-tests.sh --tls` | ✅ 279/279, ~270 s |
| Hermod, the filter CI gates on (`Tests.HTTP.` + `Tests.HTTPS.`) | ✅ 738, both legs — 537 before H-1, 562 before H-2's second half, 657 at the previous pin. Of the 81 added since, 59 are this repo's five findings and 22 are Hermod master's own WebSocket work; nothing is unaccounted for. **The filter reaches neither `Tests.TCP` nor `Tests.Warden`**, so the ten tests H-26 added run in Hermod's CI and not in ours — worth knowing, given that `AHTTPServer` derives from `ATCPServer` |
| ↳ `Tests.HTTP.` alone | ✅ 737 — the missing one is all of `Tests.HTTPS.` |
| `tests/run-tests.sh --wsl` | ✅ 415/415 — adds the Debian curl *and* the foreign peers (A7) |
| third-party: curl | ✅ 78/78 per build, two builds, both transports — 79 in the CI Debian container, see [`tests/README.md`](tests/README.md) for the conditional checks |
| third-party: Autobahn (server) | ✅ 481/517 + 36 declined, nightly, gated — the intermittent mid-case drop was **H-25**, fixed 2026-09-24 |
| third-party: Autobahn (client) | ✅ 445/517 + 72 declined, nightly, gated |
| third-party: reference peers (Go, Java, Node, Python, wget) | ✅ 58/58 + 3 skips, both directions — nightly on `ubuntu-latest`, and locally with `--wsl` |
| benchmarks | ✅ `tests/h1bench`, not a gate — see A9 for the figures and the three findings |
| third-party: proxies, http-garden, browsers | ⬜ A5, A6, A8 |
| CI per push (`windows-latest` + `debian:13`) | ✅ build + Hermod tests + 7 harnesses |
| Nightly (Autobahn, both directions) | ✅ gated on floors 481 / 445 |
| demo reachable from WSL containers | ✅ `--bind-any`, no firewall rule needed — unblocks A5 and A6 |

## Upstream workflow (Track B)

Findings get **fixed in Hermod**, not just reported. Same flow as the other
Vanaheimr projects:

```bash
cd libs/Hermod
git checkout -b http1/<topic> master        # e.g. http1/missing-status-codes
# … fix + tests in HermodTests/HTTP/ …
git commit && git push -u origin http1/<topic>
gh pr create --repo Vanaheimr/Hermod
# after the merge:
cd ../.. && git submodule update --remote libs/Hermod
git commit -am "Bump Hermod to <sha>"       # pointer bump in this repo
```

Verified mechanics: the submodule sits on `master` tracking
`origin/master` at `6cbb0216`, identical to the standalone
`D:\Coding\Vanaheimr\Hermod` checkout; `gh` is authenticated as `ahzf` with SSH
for git operations. `.gitmodules` intentionally points at the **HTTPS** URL so
anonymous clones work — if a push from inside the submodule is refused, switch
only the *local* remote to SSH (`git remote set-url origin
git@github.com:Vanaheimr/Hermod.git`) and leave `.gitmodules` alone.

**Rule:** every Track B fix ships with a focused regression test in
`HermodTests/HTTP/`, and updates the support statement + verification date in
`Hermod/HTTP1/README.md` — that is the maintenance rule the stack's own README
already sets out.

Priorities: **P1** = needed for a credible HTTP/1.1 conformance claim ·
**P2** = substantial added confidence · **P3** = nice to have.

---

# Track A — this repository

## ✅ A0 · Scaffolding

| | |
|---|---|
| ✅ | `LICENSE`, `.gitattributes`, `.gitignore`, `HTTP1.slnx` |
| ✅ | `README.md` (the RFC matrix), `PLAN.md`, `CLAUDE.md`, `docs/BUILD_LOG.md` |
| ✅ | remotes `origin` / `git1` / `git2`, initial commit pushed to all three |

## ✅ A1 · Demo host

Everything downstream drives against it, so it came first. Built, running,
and every route verified with curl + a raw RFC 6455 handshake — see
[`Demo/README.md`](Demo/README.md) for the route table and the verification
transcript. Two new upstream findings fell out of building it, **H-21** and
**H-22**, both fixed and pinned on 2026-09-26 — and the workaround `/chunked`
and `/trailers` carried for the second one is gone with it.

`Demo/HTTP1.Demo.csproj` on top of `HTTPTestServer` / `HTTPServer`:

| Listener | Port | State |
|---|---|---|
| HTTP | `:8080` | ✅ cleartext — the main conformance target |
| HTTPS | `:8443` | ✅ TLS, self-signed cert generated at startup |
| WebSocket | `:8081` | ✅ `WebSocketServer` echo — the Autobahn target |

Routes, deliberately parallel to the HTTP/2 demo so the two suites stay
comparable. All ✅ and exercised by the A2 harnesses:

| Route | Exercises |
|---|---|
| `/` | baseline `GET`/`HEAD`, `Content-Length` framing, HTTP/1.0 keep-alive negotiation |
| `/echo` | request body round-trip, `POST`/`PUT`/`PATCH` |
| `/large` (128 KiB) | large fixed-length bodies |
| `/slow` (2 s) | timeouts, client patience, connection reuse after a slow response |
| `/chunked` | `Transfer-Encoding: chunked` responses incl. chunk extensions |
| `/trailers` | trailer fields after the terminal chunk |
| `/files/resource.txt` | `HEAD`, `OPTIONS`, conditional requests, `Range` → `206`/`416` |
| `/files/greeting` | content negotiation (en/de text + en JSON) + `Vary` |
| `/secret` | `401` + `WWW-Authenticate`, Basic and Bearer |
| `/search` | `QUERY` (RFC 10008), fixed-length and chunked |
| `/events` | SSE — history, `Last-Event-ID`, retry, heartbeat |
| `/expect` | `Expect: 100-continue` and `417` |
| `/redirect/{code}` | `301` `302` `303` `307` `308` — the last since H-1 landed on 2026-09-23 |
| `/status/{code}` | arbitrary status codes, for the third-party drivers |

Also: `--fast-timeouts` shortens the read deadlines from 30 s to 3 s so the
timeout checks resolve quickly; the runner passes it.

✅ **`/ws` on the main port**, since 2026-09-24. `WebSocketUpgrade.For(...)` hands
the connection to the WebSocket server's own RFC 6455 §4.2.1 implementation —
there is deliberately no second copy of the handshake. Both HTTP listeners carry
it, so the upgrade works over cleartext and TLS; `:8081` stays as the Autobahn
fuzzingclient's target.

This was "blocked on H-16" for longer than it was blocked: Hermod grew the seam
on 2026-09-16 and the pin here only reached it on 2026-09-23, after which nobody
re-read the finding.

## ✅ A2 · Raw-wire harnesses

**201/201 checks pass over both transports** — `tests/run-tests.sh`, ~97 s
cleartext, ~300 s over TLS. It read 199 when A2 closed; the two since are the
`308` redirect checks that H-1 unblocked. See [`tests/README.md`](tests/README.md) for the
per-harness breakdown and what the checks do and do not establish. One new
upstream finding: **H-23**.

The original scope below is kept for reference; everything in it shipped except
the deferred items noted in `tests/README.md`.

<details><summary>original scope</summary>

The core of the repository: what no high-level client will emit. Model:
`h2attack`/`h2semantics`/`h2connect` — console apps, per-check `✓`/`✗`, one
process per scenario, driven by `tests/run-tests.ps1`.

| Harness | Covers | Spec |
|---|---|---|
| `h1raw` | diagnostic raw-socket client + wire logger (not in the pass/fail gate) | — |
| `h1syntax` | request line, field syntax, `obs-fold`, whitespace before `:`, control chars, non-canonical versions, request-target forms (origin/absolute/authority/asterisk), fragments, percent-escapes, dot segments | RFC 9112 §2–3, RFC 9110 §5 |
| `h1framing` | the §6.3 body-length algorithm as a full matrix: `Content-Length` duplicate/conflicting/overflow/signed/non-decimal, `CL`+`TE`, `TE` with `chunked` not final, unknown codings, truncated bodies, malformed chunk-size lines, chunk extensions (token/valueless/quoted/escaped/malformed), trailers incl. the forbidden list, metadata limits | RFC 9112 §6–7 |
| `h1conn` | persistence defaults per version, `Connection: close` vs. `keep-alive`, pipelining depth and ordering, chunked delimiting before the next pipelined request, invalid-leading-request close, half-close, reuse after `HEAD`/`204`/`304`, HTTP/1.0 keep-alive negotiation | RFC 9112 §9, RFC 1945 |
| `h1semantics` | `GET`/`HEAD`/`POST`/`PUT`/`DELETE`/`OPTIONS`/`QUERY`, `OPTIONS *`, `405` + `Allow`, `Expect: 100-continue` + `417`, conditional requests, `Range`/`206`/`416`/`multipart/byteranges`, negotiation + `Vary`, Basic/Bearer | RFC 9110 |
| `h1attack` | desync/smuggling vectors, slowloris (header + body), header floods, oversized request-target/field-line/field-count, chunk-metadata floods, early-abort cleanup, connection-table leaks | RFC 9112 §11.2 |
| `h1sse` | `text/event-stream` field parsing, multi-line `data`, comments, `retry`, `Last-Event-ID` replay, mid-stream abort, heartbeat | WHATWG HTML |
| `tests/run-tests.ps1` | build → start demo → run every scenario → summary, with `-NoBuild` / `-Filter` | — |

Note the split from Track B: `h1semantics` will find that `206`/`304` are not
generated automatically. That is **by design** (🔵 in the matrix) — the demo's
handlers must implement them, and the harness then verifies *the demo*, not the
library. Worth stating explicitly in `tests/README.md` so it is not read as a
library failure.

</details>

## ✅ A3 · curl matrix

**78/78 checks pass over both transports**, wired into `tests/run-tests.sh` —
the gate stands at **279/279** (201 raw-wire + 78 curl). It read 257 when A3
closed; the twenty-two since are `308` joining `/redirect/{code}` once H-1
landed, the five Digest checks that H-3 made possible, four on the `/ws`
upgrade, and nine on content codings once H-2 was whole. See
[`tests/README.md`](tests/README.md#the-curl-leg).

✅ **The Debian curl leg runs too**, via `tests/run-tests.sh --wsl` → **357/357**.
That build has nghttp2 and is the more interesting witness: a client that *could*
speak HTTP/2 and does not proves ALPN negotiation in a way the Windows build
cannot. It needs the demo on `--bind-any`, which the flag does; **no firewall
rule was necessary**. Opt-in, so a plain run never widens a listener.

**This unblocks A5 and A6** — their containers need the same reachability, and
it is now one flag rather than an open question.

curl is the closest thing HTTP/1.1 has to a reference client, and the installed
build (8.21, no HTTP/2) is ideal: it cannot silently upgrade.

- `tests/curl.ps1` + `tests/curl.sh` — a matrix over `--http1.0` / `--http1.1`,
  `-I` (HEAD), `-X OPTIONS`, `--data` / `-T` (chunked upload), `-H 'Expect:'`,
  ranges, `--cookie`/`--cookie-jar`, `-u` (Basic), `--anyauth`, `--compressed`
  (which has something to negotiate against since **H-2**), `--raw`,
  `--keepalive-time`, several
  requests on one connection, `-k` against `:8443`
- assertions on both the status/body **and** the `-v` wire trace
- `docs/TestingAgainst_curl.md`

## ✅ A4 · Autobahn TestSuite · P1 · done 2026-09-23

Hermod's WebSocket README already claims 296 + 242 + 126 + 126 cases with 0
failures. Those numbers were unreproducible from a clean checkout — this
repository is where they become a command.

**Server side: done.** `tests/autobahn.sh` drives `fuzzingclient` against the
demo host's echo server on `:8081` and the nightly gates on a floor of 481 of
517, with the 36 declines being the RFC 7692 §7.1.2.1 refusals of
`server_max_window_bits=9`. The first run also found the real defect it was
built to find: the demo never offered `permessage-deflate`, which took the
score from 301 to 481 with one line.

**Client side: done 2026-09-23.** `tests/autobahn-client.sh` starts the suite in
`fuzzingserver` mode and drives `tests/autobahn-client/` — our `WebSocketClient`
— through all 517 cases: **445 passing, 72 declined, 0 hard failures**, gated on
a floor of 445 in the nightly. The 72 are sections 13.3–13.6, declined by the
suite because our client's offer is a fixed constant that never carries
`server_max_window_bits`; see min_pass in that script for the wire evidence.

- ✅ echo server for `fuzzingclient` — the demo host's `:8081`, not a separate project
- ✅ `tests/autobahn-client/` — client driver on `WebSocketClient` against `fuzzingserver`
- `tests/autobahn/fuzzingclient.json` + `fuzzingserver.json`
- `tests/autobahn.sh` (runs in WSL/Debian) + `tests/autobahn.ps1` (thin
  `wsl -d Debian` wrapper, so Windows and Linux drive the same script)
- both with and without `permessage-deflate` (sections 12/13)
- `docs/TestingAgainst_Autobahn.md`

Runs the official `crossbario/autobahn-testsuite` image under WSL/Debian's
Docker. The scripts must start the daemon themselves (`service docker start`) —
WSL has no systemd, so it is not running after a reboot.

## ⬜ A5 · Intermediary interop · P2 · ~2–3 d

Reverse proxies are the strictest HTTP/1.1 consumers in existence; framing bugs
surface against nginx long before they surface against a browser.

- `tests/proxies/docker-compose.yml` — nginx, HAProxy, Envoy, Apache httpd,
  Caddy, each reverse-proxying the demo host (WSL/Debian Docker; the demo host
  runs on Windows and is reachable from WSL via the host IP, **not**
  `localhost` — pin that in the compose file)
- run the `h1semantics` + curl matrices *through* each proxy and diff against direct
- reverse direction: Hermod's `HTTPClient` → proxy → a known-good origin
- explicitly check the intermediary-facing rules: hop-by-hop field stripping,
  `Via`, trailer forwarding, chunked re-framing, `Connection` token handling
- `docs/TestingAgainst_Proxies.md`

## ⬜ A6 · Request smuggling / differential fuzzing · P2 · ~2 d

RFC 9112 §11.2 is the section where HTTP/1.1 implementations actually fail.
Hermod's strict framing rejection is well tested internally, but never against
an adversarial external tool.

- [`http-garden`](https://github.com/narf-industries/http-garden) — add Hermod
  as a target and run the differential fuzzer against the ~20 servers/proxies it
  already knows
- [`smuggler.py`](https://github.com/defparam/smuggler) — CL.TE / TE.CL / TE.TE probes
- [`h2csmuggler`](https://github.com/BishopFox/h2csmuggler) — must find nothing
  (h2c upgrade is absent); pin that as a regression
- `docs/TestingAgainst_Smuggling.md`

## ✅ A7 · Non-.NET reference peers · P2 · done 2026-09-26

Every interop test before this was .NET against .NET, or curl. Independent
implementations catch shared assumptions that two .NET stacks cannot — and the
*client* half had no independent witness at all, which is the asymmetry this
closes.

`tests/interop.sh` drives both directions; the peers live in `tests/peers/` and
are **stdlib-only on purpose**, so a clean checkout needs the runtimes and
nothing else — no package fetch, no lockfile, no build step.

| Peer | As client | As server |
|---|---|---|
| Go `net/http` | ✅ 10 checks | ✅ `server.go`, 7 checks via `tests/h1peer` |
| Node `node:http` | ✅ 11 checks | ✅ `server.mjs`, 7 checks via `tests/h1peer` |
| Java `java.net.http` | ✅ 10 checks (+1 skip) | — the JDK ships no HTTP server worth pointing at |
| Python `http.client` | ✅ 10 checks (+1 skip) | — the stdlib cannot frame chunked itself, so a Python server would be testing our framing rather than Python's |
| `wget` | ✅ 3 checks | — |
| Rust `hyper` | ⬜ needs crates.io, which breaks "clean checkout, nothing fetched" | ⬜ same |

**58/58 checks, 3 skips**, each with its reason on the line. The skips are the
honest part: `java.net.http` and `http.client` do not expose the trailer
section, and `node:http` follows no redirects, so those say SKIP instead of
passing on something else.

Each client runs the *same* ten checks, so the matrix is comparable across
stacks: baseline, chunked body, trailers, gzip round-trip, HEAD-matches-GET,
`Range` → 206, `Accept-Ranges`, 404, redirect, connection reuse. The gzip check
decodes by hand in every language rather than letting the transport do it — Go
switches off its own transparent decompression when the header is set by hand,
so leaving it to the transport would have the four peers measuring four
different things.

The other direction is `tests/h1peer`: our `HTTPClient` against `server.go` and
`server.mjs` — their Content-Length framing, their chunked framing, their
trailer section collected by us, their gzip undone by ours.

**Where it runs.** Linux natively. On Windows the toolchains live in the Debian
WSL distribution, the same one the second curl build comes from, so it needs
`--wsl` for the same reason: the demo has to be bound to `0.0.0.0` for the WSL
VM to have a route to it. Direction 2 is loopback *inside* the peer
environment, crosses no VM boundary, and therefore works unchanged in CI.

**CI:** a nightly job on `ubuntu-latest`, which ships all five runtimes. Not a
push gate — the Debian container ships none of them, and a gate whose check
count moves with the runner image is worse than no gate.

## ⬜ A8 · Browser interop · P2 · ~1–2 d

- `tools/browser-interop.ps1` (model: the HTTP/3 repo's script) driving
  Playwright over Chromium, Firefox and WebKit
- `EventSource` against `/events`, `WebSocket` against `/ws`, CORS preflight,
  chunked rendering, connection reuse, `fetch()` with ranges
- the practical acceptance test — a browser is the least forgiving consumer of
  SSE and WebSocket in daily use

## ✅ A9 · Benchmarks · P3 · done 2026-09-26

`tests/h1bench`, model: `h2bench`. Not a gate and not in CI: it is the baseline
an optimisation has to beat, and the thing to re-run before believing one
worked. Client and server share one process over loopback, so every figure
covers both roles.

Measured 2026-09-26, 16-core Windows box, .NET 10.0.12, Release:

| | |
|---|---|
| request header parsing | **48,795 parses/s**, **18,696 bytes allocated** per parse of a 376-byte header |
| chunked coding | **2,225 MiB/s** encode, **1,921 MiB/s** decode |
| small GET, one client | **~5,000 req/s** at 1, 8 and 64 concurrent — flat, because one client is one connection |
| small GET, a client each | **21,657 req/s** at 8, **20,245** at 64 · p50 0.29 ms / 2.07 ms |
| 64 MiB download | **987 MiB/s**, 3.00× the payload allocated |
| 64 MiB upload | **958 MiB/s**, 3.01× the payload allocated |
| request on a kept-open connection | p50 **0.197 ms** |
| **control**: .NET `HttpClient` → Hermod | p50 **0.240 ms** |
| **control**: .NET `HttpClient` → Kestrel | p50 **0.252 ms** |

Three things the numbers say that the code did not:

**Per-request latency is not a problem.** Against Kestrel on the same loopback,
in the same process, driven by the same `HttpClient`, our server is the
marginally faster of the two. That is what a control is for: an absolute number
with nothing beside it is how "slower than I expected" becomes "slow".

**A fresh client costs 39 ms, and none of it is the connection.** Split: 38.3 ms
constructing the `HTTPClient`, 1.06 ms for its first request. One shared
`DNSClient` takes the whole thing to **0.449 ms** — 86× — which names the cause
instead of guessing at it. Filed as **H-27**.

**Flat throughput on one client is the protocol, not a defect.** HTTP/1.1 has no
multiplexing, so 64 callers on one connection queue: req/s stays put and latency
rises linearly, exactly as it should. Give each caller its own client and the
server does four times the work. Worth stating, because the HTTP/2 sibling has a
superficially identical curve that *is* a defect (`requestStartLock`), and the
two must not be read as the same finding.

Still open here: the external load generators (`h2load --h1`, `bombardier`,
`oha`), which would also stress keep-alive reuse and connection-table cleanup
from outside this process.

## ⬜ A10 · Parser fuzzing · P3 · ~2 d

SharpFuzz + AFL++ against the request-parsing entry point, seeded from the
`h1syntax`/`h1framing` corpora. Target: no unhandled exception, no hang, no
connection-state leak on any input.

## ✅ A11 · CI · P2 · done 2026-09-23

`.github/workflows/ci.yml` runs per push on the same two legs as the sibling
repositories — `windows-latest` and a `debian:13` container — with
`fail-fast: false`, because "red on exactly one platform" is the signal a
two-leg matrix exists to produce, and in the HTTP/2 sibling that signal was a
real server bug rather than a platform quirk. Three steps: build, the pinned
Hermod's `Tests.HTTP.*`, and `tests/run-tests.sh` (7 harnesses — the curl
matrix among them, driven through the container's own curl on the Debian leg).
The `.trx` files upload on `always()` rather than `success()`: a red run is when
they matter most, and a step killed by `timeout-minutes` counts as a
*cancellation*, so `!cancelled()` would drop the evidence in exactly the case
that needs it.

`.github/workflows/nightly.yml` runs both Autobahn directions, in two jobs
rather than two steps of one, each gated on a floor: `autobahn` at 481,
`autobahn-client` at 445.

What the nightly does **not** have is the proxy, smuggling and browser jobs this
section originally listed. That is not unfinished CI work — A5, A6 and A8 do not
exist to be run, and they bring their own jobs when they land.

This section also used to ask for `run-tests.ps1`. There has never been one in
this repository, and after the HTTP/2 and HTTP/3 repos each paid for keeping a
second runner — one of them with a `$Args` parameter that silently never bound,
so twelve harness labels ran one scenario twelve times and reported 48/48 —
there will not be one.

---

# Track B — gaps in Hermod itself

Each of these is an upstream change under `libs/Hermod/`, shipped via the
workflow above: branch → fix + regression test → PR → merge → submodule pointer
bump here.

Two of them may end as "documented as deliberately out of scope" rather than
implemented — **H-9** (`TRACE` has a real XST security history) and the
never-standardized parts of **H-5**. Everything else is a fix.

A finding is 🔶 once the fix and its regression test are merged into Hermod, and
✅ only once the submodule pointer here moves onto it: until then this repository
still builds against the pin, so nothing is verified from a clean checkout.

| | # | Gap | Spec | P | Effort | Note |
|---|---|---|---|---|---|---|
| ✅ | **H-1** | Missing status codes: `103` `308` `421` `451` `511` `208` `508`; **`425` is defined but named `NoCode`** | RFC 8297, 9110 §15.4.9/§15.5.20, 7725, 6585, 8470, 5842 | P1 | XS | **Fixed 2026-09-23, merged and pinned, [Hermod#29](https://github.com/Vanaheimr/Hermod/pull/29).** `NoCode` → `TooEarly`; 102 and 226 added alongside the seven, so the whole IANA registry is now defined rather than all-but-two. `IsNotSuccessful` was `Code < 200 && Code >= 300` — constant false for every code — and is fixed with it. 7 tests |
| ✅ | **H-2** | No content coding for HTTP/1 bodies — *decoding* was missing in both roles; the "neither compresses" half of this finding was wrong, see the note | RFC 9110 §8.4, 1952, 7932, 8878 | P1 | S→M | **Whole, 2026-09-24, merged and pinned, [Hermod#29](https://github.com/Vanaheimr/Hermod/pull/29) + [Hermod#32](https://github.com/Vanaheimr/Hermod/pull/32).** Estimated S, turned out to be four fixes. *Decoding a buffered body* (#29): `HTTPContentCoding` lifted to `HTTP/General/`, wired into `AHTTPPDU` as `DecodeBody`/`TryDecodeBody` — reverse order, unknown codings refused, 64 MiB ceiling. *Decoding a streamed one* (#32): `TryDecodeBodyStream` puts the reversal inside `HTTPBodyStream`, which is the only thing that works for chunked, close-delimited and SSE bodies; `Content-Length` has to go with `Content-Encoding` there, because the buffering loop stops reading at it. *The client* (#32): `AutomaticDecompression`, off by default, offers `br, gzip, deflate` and undoes what comes back. *The server* (#32): `AutomaticContentCompression`, off by default, on every handler at once — `SinglePageAppHandler` keeps compressing static files the better way. 43 tests; +9 curl checks here |
| ✅ | **H-3** | `HTTPDigestAuthentication` is *not* RFC 7616 — it is `Digest base64(user):base64(secret)`, no realm/nonce/qop/nc/cnonce/response | RFC 7616 | P1 | M | **Fixed 2026-09-24, merged and pinned, [Hermod#30](https://github.com/Vanaheimr/Hermod/pull/30)** — the row stayed 🔶 for a few hours after the pin had already moved past it, which is the same lapse H-16 is a monument to. It was dead code, referenced by nothing, not even the `Authorization` dispatcher. The RFC 9110 §11 framework moved from `HTTP2/` to `HTTP/Authentication/` (it was version-independent all along and said so in its own summary), which is what had made the existing correct `DigestAuthenticationScheme` unreachable from HTTP/1.x. 10 tests, and curl 8.14 authenticates with **SHA-256**. See the note on multiple challenges in `HTTP1/README.md` |
| ✅ | **H-4** | `Forwarded` not implemented (only `X-Forwarded-For`) | RFC 7239 | P2 | S | Already marked `//ToDo` at `HTTP1/Request/HTTPRequest.cs:1125`. **Fixed 2026-09-26, merged and pinned, [Hermod#43](https://github.com/Vanaheimr/Hermod/pull/43).** `ForwardedElement` and `ForwardedNode` in `HTTP/General/`, parsed and serialisable, preferred over the `X-Forwarded-For` family where both arrive (§7.4) and recorded *beside* the peer socket rather than instead of it, because all of it is hearsay (§8.1). Quote-aware out of necessity rather than taste: a quoted value may contain both the comma that separates elements and the semicolon that separates pairs — the first version of that claim in the comments blamed IPv6 and was wrong, which measuring it is what showed. A second defect found on the way and fixed with it: the existing `X-Forwarded-For` path did `.Skip(1)`, discarding the client — the one address the field exists to carry, with the socket it kept being the *immediate* peer rather than a stand-in. 17 tests |
| ⬜ | **H-5** | No RFC 9111 cache (client- or server-side) | RFC 9111, 5861, 8246 | P2 | L | `HTTP2/Core/HTTPCache.cs` + `HTTPCacheControl`/`HTTPCacheDecision`/`HTTPStoredResponse` exist. Same lift as H-2, much larger. Verifiable against `cache-tests.fyi` |
| ✅ | **H-6** | No Structured Field Values parser/serializer | RFC 9651 | P2 | M | Prerequisite for most modern fields (9530, 9211, 9213, 9218, Client Hints). **Fixed 2026-09-26, merged and pinned, [Hermod#43](https://github.com/Vanaheimr/Hermod/pull/43).** Lists, Dictionaries and Items over all eight bare types, including the Date and Display String that RFC 8941 did not have; Section 4.2 parsing, Section 4.1 serialization, byte-for-byte round trips for every canonical form — which is the property RFC 9421 signatures would need. Strict on purpose: a trailing comma, a sixteenth digit, an upper-case key, an upper-case percent escape and a display string that is not valid UTF-8 all fail rather than get repaired, because two implementations that each repair a different malformed field are two implementations that disagree. Nothing uses it yet; it is the prerequisite H-13 and the rest are defined in terms of. 33 tests |
| ⬜ | **H-7** | No `Alt-Svc` | RFC 7838 | P2 | S | The natural bridge from this stack to the h2/h3 stacks — and directly testable with curl's `--alt-svc` |
| ✅ | **H-8** | ~70 source comments still cite RFC 2616 / RFC 7230-series | — | P2 | S | Mechanical; the HTTP1 README already flags it. **Fixed 2026-09-26, merged and pinned, [Hermod#43](https://github.com/Vanaheimr/Hermod/pull/43).** 62 of 69 rewritten to each field's current defining document *and section*, taken from the IANA HTTP Field Name registry rather than from memory — 45 lookups, where being confident about 44 is not the same as being right about all of them. Seven remain deliberately: six are RFC 4918 quoting RFC 2616 in text this codebase quotes in turn, where rewriting them would misquote RFC 4918, so each of the three blocks now carries a remark naming the current reference; the seventh is inside commented-out code under `URLMapping_old/`, which is H-18's question and not this one's |
| ⬜ | **H-9** | No server-side `TRACE` | RFC 9110 §9.3.8 | P3 | XS | Token + client exist; the server never handles it. Note the XST security history — "deliberately not implemented" is a valid answer, but then document it |
| ⬜ | **H-10** | No automatic CORS preflight | WHATWG Fetch | P2 | M | `Access-Control-*` are settable, but `OPTIONS` preflight is not answered automatically. Browser-visible (**A8**) |
| ⬜ | **H-11** | Obsolete HTTP-date formats (RFC 850, asctime) not parsed | RFC 9110 §5.6.7 | P3 | XS | Recipients **MUST** accept all three |
| ⬜ | **H-12** | No HSTS (`Strict-Transport-Security`) | RFC 6797 | P3 | XS | Header emission only; policy is the application's |
| ⬜ | **H-13** | `Content-MD5` typed (obsolete), RFC 9530 digest fields missing | RFC 9530 | P3 | S | Depended on **H-6**, which landed 2026-09-26 — the structured-fields parser a `Content-Digest` dictionary needs is there now, so this is unblocked |
| ⬜ | **H-14** | No `Link` header | RFC 8288 | P3 | S | |
| ⬜ | **H-15** | No Problem Details | RFC 9457 | P3 | S | Relevant for the `HTTPAPI` layer |
| ✅ | **H-16** | General HTTP server has no `Upgrade` dispatch — WebSocket is a separate listener | RFC 9110 §7.8, 9112 §9.6 | P2 | M | **Was already fixed upstream on 2026-09-16** (Hermod `3bc56fdb`, "A WebSocket can live on an HTTP path"), with its own `WebSocketOnAnHTTPPathTests`. This row described the state of a pin that had not moved since 2026-08-13; the bump of 2026-09-23 brought the fix in and nobody re-read the finding. What was genuinely left — the demo's own `/ws` route — landed 2026-09-24, with 4 curl checks and Autobahn's sections 1, 2 and 7 (64/64) driven through the upgrade |
| ⬜ | **H-17** | Server does not negotiate ALPN `http/1.1` | RFC 7301 | P3 | XS | Client side is configurable; the server never offers it |
| ⬜ | **H-18** | `HTTP1/Server/URLMapping_old/` alongside `URLMapping/` — two routing generations in the tree | — | P3 | S | ~4 000 lines of probable dead code. Clarify before the harnesses depend on either |
| ⬜ | **H-19** | No RFC 8187 parameter encoding / RFC 6266 `filename*` | RFC 8187, 6266 | P3 | S | |
| ⬜ | **H-20** | IPv6 zone identifiers in URIs | RFC 6874 | P3 | XS | |
| ✅ | **H-21** | `Accept-Ranges` is modeled as a **request** field, but RFC 9110 §14.3 defines it as a *response* field — `HTTPResponse.Builder` has no property for it | RFC 9110 §14.3 | P2 | XS | Found while building A1: the demo has to fall back to the generic `SetHeaderField("Accept-Ranges", …)`. Wrong side of the request/response split. **Fixed 2026-09-26, merged and pinned, [Hermod#43](https://github.com/Vanaheimr/Hermod/pull/43).** The response side has the field now, and the three request-side members are `[Obsolete]` rather than removed — deleting or renaming them would break every downstream Vanaheimr project, and a warning says the same thing without doing that. Found next door while reading the field beside it and fixed with it: the builder's `AcceptPatch` setter wrote `Allow`, so a handler advertising patch formats silently replaced the set of methods it claims to support with media types. `SetHeaderField` takes an `Object`, so neither the compiler nor the wire objected. 3 tests |
| ✅ | **H-22** | A chunked response silently produces an **empty body** unless `ContentStream` is a `ChunkedTransferEncodingStream` — setting `TransferEncoding = "chunked"` + `ChunkWorker` alone emits correct headers and nothing else, with no error | — | P2 | S | Found while building A1. The server dispatches the worker on the stream type, not the header field. Either wire the two together or fail loudly when they disagree; a silent empty body is the worst of the three options. **Fixed 2026-09-26, merged and pinned, [Hermod#43](https://github.com/Vanaheimr/Hermod/pull/43).** The server builds the `ChunkedTransferEncodingStream` itself when a response announces the coding without bringing one, so a handler no longer needs to know about `request.NetworkStream`, and it writes the terminal chunk whether or not the worker did. The client had the mirror image in its request path, found while fixing this one. What it deliberately does *not* do is frame everything that says "chunked": that broke 6 tests in the wider suite and they were right — a byte array under a chunked header is taken to be framed already, and `AutomaticallyChunkContent` is how a handler says otherwise. The demo's workaround, and the comment describing the defect, are gone with it. 6 tests |
| ✅ | **H-25** | **The TCP Warden reaps live connections.** `TCPConnection.IsConnectionClosed()` is `Poll(SelectRead) && Available == 0` — a race against the connection's own reader — and `ATCPServer.cs:570` closes the socket on it. Surfaced as Autobahn 12.4.18 and 12.5.15 dropping mid-case without a close handshake | RFC 6455 §7 | P1 | S | **Diagnosed 2026-09-24**, by the instrument added the day before, on its first red night: the log names an `ObjectDisposedException` on the NetworkStream inside the read loop and, twelve milliseconds later, `ATCPServer: Cleaned up stale client` on the same socket. `Poll` is true when data is readable *or* the peer closed; `Available == 0` separates them; the reader drains the socket in between. **Wider than WebSocket** — `AHTTPServer : ATCPServer`, so every long-lived HTTP/1.1 connection is exposed. Reproduction had failed 14 times before this, which is what the instrument was for. **Fixed 2026-09-24, merged and pinned, [Hermod#31](https://github.com/Vanaheimr/Hermod/pull/31).** The Warden reaps on the handler task it already holds, not on a socket it guesses about. Verified by `ConnectionLivenessTests`, which catches the predicate lying in 160–250 ms, 5 runs of 5; the end-to-end A/B could not carry it (1 failure in 4 forced runs of the old code, which is the base rate) and one attempt at it was invalid outright. See [`tests/TestingAgainst_Autobahn.md`](tests/TestingAgainst_Autobahn.md) |
| ✅ | **H-26** | Warden scheduling does not do what it says: `ATCPServer` registers its connection check as `EveryMinutes(1, …)` and ignores the `WardenCheckEvery` property it documents, and `Warden.EverySeconds(N, …)` tests `timestamp.Minute % N` rather than `Second` | — | P2 | XS | **Fixed 2026-09-25, merged and pinned, [Hermod#42](https://github.com/Vanaheimr/Hermod/pull/42).** Two findings, three defects. *`EverySeconds`*: six of eight overloads measured minutes; the two that did not are what shows it was a slip. *The interval*: the defaults resolved twice, against different numbers — the properties took 30 s while the Warden took the literals 3 min and 1 min from the constructor call — and `EveryMinutes(1, …)` is not "once a minute" but a predicate that is always true plus a one-minute `SleepTime` no constructor argument can reach, which is why reproducing H-25 needed a source edit. *And the one that hid them*: `AllWardenChecks` returned `AllWardenChecks`, so nothing could enumerate the checks to ask when they run — a `StackOverflow` is uncatchable, so reverting it takes the test host down rather than turning a test red. 10 tests, two of them about things that were never broken: `SleepTime` is what turns a sixty-second-wide slot into one run, and the predicate is *sampled*, so a slot narrower than `CheckEvery` can be missed. The reaper now runs every 30 s and first runs after 30 s rather than 3 min |
| ⬜ | **H-24** | Six status-code reason phrases predate RFC 9110: 413 `Request Entity Too Large`, 414 `Request-URI Too Long`, 416 `Requested Range Not Satisfiable`, 422 `Unprocessable Entity`, plus 306/418 carrying draft names for codes the RFC reserves | RFC 9110 §15 | P3 | XS | Found while doing H-1. Not a defect — §15 says a client SHOULD ignore the reason phrase — but it is what goes out on the wire, since the status line is `{Code} {Name}`. Renaming the fields is breaking for every downstream Vanaheimr project, so it is a decision rather than a fix; `HTTPStatusCodeTests` pins the exact divergence set meanwhile, so it cannot drift further unnoticed |
| ⬜ | **H-27** | Every `HTTPClient` builds its own `DNSClient`, whose default searches the machine's network configuration for resolvers — ~38 ms per construction, even when the URL is a literal IP address that will never be resolved | — | P2 | S | Found by **A9** on 2026-09-26, and measured rather than inferred: a fresh client per request is 39.4 ms p50, of which 38.3 ms is the constructor and 1.06 ms the request, and passing one shared `DNSClient` takes the whole thing to 0.449 ms. The line is `ATCPClient.cs:319` — `DNSClient ?? new DNSClient(...)` — whose default is `SearchForIPv4DNSServers: true` and `SearchForIPv6DNSServers: true`. Harmless for a long-lived client, ruinous for anything building one per request, and avoidable three ways: resolve lazily, share one default instance, or skip the search when the target is already an address. `tests/h1bench -- connect` is the regression test |
| ⬜ | **H-23** | `HEAD` is not derived from `GET` — an unregistered `HEAD` is answered `405`, and the `Allow` field it returns omits `HEAD` as well | RFC 9110 §9.3.2 | P2 | S | Found while building A2. "A server SHOULD support HEAD for any resource it supports GET for" — and the `405` naming only `GET` misleads the very client that consulted `Allow` to find out. Every GET route currently has to register `HEAD` by hand |

---

# Suggested sequence

```
✅A0 ──▶ ✅A1 ──┬──▶ ✅A2 ──▶ ✅A3 ──▶ ✅A11 (CI: build + harnesses + curl)
                │
                ├──▶ ✅A4  (Autobahn — both directions, both gated nightly)
                │
                └──▶ ⬜A5, ⬜A6, ⬜A7, ⬜A8  (external suites)

Track B in parallel: eleven of twenty-six are in. ✅H-1 and ✅H-2 first
(small, high leverage), then ✅H-3 and ✅H-16, the two Warden findings
✅H-25 and ✅H-26, and ✅H-4 ✅H-6 ✅H-8 ✅H-21 ✅H-22 together on
2026-09-26. A3 and A4 turned out to need none of them, so nothing was ever
waiting on this track.
```

**First milestone:** ✅ A0 ✅ + A1 ✅ + A2 ✅ + A3 ✅ + H-1 ✅ + H-2 ✅ — a
runnable demo host, the raw-wire gate, the curl matrix, and the two Hermod fixes
that are cheap and obviously right. Six of six, finally: H-2 turned out to be
four fixes rather than one, and the last of them closed on 2026-09-24. The
number is now **279/279**, and 78 of those come from a client nobody here wrote
— the first part of it that is not self-assessment.

**Second milestone:** ✅ A4 + ✅ A11 + ⬜ A5 — Autobahn reproducible from a
clean checkout in both directions, CI green on two legs, proxy interop. Two of
three.

---

# Settled

- **Track B goes upstream.** Findings are fixed in Hermod via branch → PR →
  merge → submodule bump, as in the other Vanaheimr projects. See the workflow
  section above.
- **Containers run in WSL/Debian.** Docker 26.1.5 is already installed there
  (the daemon needs `sudo service docker start` — WSL runs without systemd).
  The runner scripts therefore get a `.sh` variant invoked through
  `wsl -d Debian`, not a Docker Desktop dependency. Debian also carries a
  *second* curl (8.14.1) built **with** nghttp2/nghttp3 — the useful complement
  to the Windows curl 8.21, which has no HTTP/2 at all: the Windows one cannot
  accidentally upgrade, the Debian one proves `--http1.1` and ALPN are honoured.
- **Test placement follows the HTTP/2 repo.** In-process unit and integration
  tests live with the stack in `HermodTests/` (filter
  `FullyQualifiedName~Hermod.Tests.HTTP.`, 561 today); this repository holds
  only the demo-driven raw-wire harnesses, the third-party suite drivers and the
  tooling. A2 produces harnesses, not NUnit
  fixtures — a Track B fix's regression test goes upstream with the fix.
- **Remotes.** `origin` → GitHub, plus `git1`/`git2` on graphdefined.com, as in
  the other Vanaheimr repositories. Default branch `master`.

# Open questions

1. **Lift or duplicate?** H-2 and H-5 exist in usable form in `Hermod/HTTP2/Core/`.
   Move them to a version-neutral namespace shared by HTTP/1, /2 and /3, or
   reimplement per version? The shared route is better but touches the HTTP/2
   stack, which is currently at 146/146 h2spec and 481/517 Autobahn (+36 declined).
