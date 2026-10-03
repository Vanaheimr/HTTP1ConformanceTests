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

**Current state (2026-10-03):** **A0 ✅**, **A1 ✅** (demo host, 3 listeners,
19 routes), **A2 ✅** (6 harnesses), **A3 ✅** (curl) — **311/311 checks green
over cleartext *and* TLS**. **A4 ✅** — both directions driven and gated nightly: server 481/517, client
445/517, zero hard failures either way. **A7 ✅** — five foreign clients and two
foreign servers, 58/58, nightly. **A9 ✅** — `tests/h1bench`, with Kestrel as the
control. **A10 ✅** — `tests/h1fuzz`, deterministic in the gate and exploring
nightly; it found **H-28** on its first run. **A6 ✅** — the smuggling
differential: `tests/h1desync` in the gate, three implementations compared
nightly, two third-party probe suites, and Hermod added to the HTTP Garden; it
found **H-29** and **H-30**, and a MUST violation in Go's `net/http` that is
not ours to fix. **A11 ✅** — CI per push
on two legs, nightly for both Autobahn directions. **A8 ✅** — three browser engines, **27/27** since H-10 closed; it read 24/27 before, the three failures being H-10 shown from the only vantage point that can see it. Track B: **31 findings, 17 fixed upstream**
and pinned here, all of them whole — H-4, H-6, H-8, H-21 and H-22 landed
together with [Hermod#43](https://github.com/Vanaheimr/Hermod/pull/43) on 2026-09-26, one commit each and the
citation sweep last so that it covered what the other four added.

| Gate | State |
|---|---|
| `dotnet build HTTP1.slnx` | ✅ 0 errors. 286 warnings, every one of them in the submodules' own projects (`Hermod`, `HermodTests`, `Styx`, `StyxTests`) and none in this repository's — this row claimed 0 warnings until 2026-10-03, which an incremental build will tell you, because one that compiles nothing reports nothing. Measured with `-t:Rebuild` |
| `tests/run-tests.sh` | ✅ 311/311 + the fuzzer's fixed-seed pass, 9/9 harnesses, ~135 s |
| `tests/run-tests.sh --tls` | ✅ 311/311, 9/9, ~270 s — `h1desync` drives the cleartext listener in either leg, so its 24 are the same 24 twice. This row read 279 and said the TLS leg left them out |
| Hermod, the filter CI gates on (`Tests.HTTP.` + `Tests.HTTPS.`) | ✅ **919**, both legs — this row said 832 for three pins, which is what a count nobody re-measures does. 537 before H-1, 657 at the pin before H-10, 896 before H-27's five and H-23's six. **The filter reaches neither `Tests.TCP` nor `Tests.Warden`**, so H-26's ten tests run in Hermod's CI and not in ours, and H-27's fix lives in `ATCPClient` — which is why its regression test was written through `HTTPClient` |
| ↳ `Tests.HTTP.` alone | ✅ **918** — the missing one is all of `Tests.HTTPS.` |
| `tests/run-tests.sh --wsl` | ✅ **485/485** over 12/12 harnesses — adds the Debian curl (78), the foreign peers (A7, 58), the smuggling differential (A6, 38) and the fuzzer (A10) |
| third-party: curl | ✅ 78/78 per build, two builds, both transports — 79 in the CI Debian container, see [`tests/README.md`](tests/README.md) for the conditional checks |
| third-party: Autobahn (server) | ✅ 481/517 + 36 declined, nightly, gated — the intermittent mid-case drop was **H-25**, fixed 2026-09-24 |
| third-party: Autobahn (client) | ✅ 445/517 + 72 declined, nightly, gated |
| third-party: reference peers (Go, Java, Node, Python, wget) | ✅ 58/58 + 3 skips, both directions — nightly on `ubuntu-latest`, and locally with `--wsl` |
| benchmarks | ✅ `tests/h1bench`, not a gate — see A9 for the figures and the three findings |
| parser fuzzing | ✅ `tests/h1fuzz` — fixed seed in the gate, ten minutes per target nightly; 1 known finding (**H-28**) |
| smuggling: our differential | ✅ `tests/h1desync` 24/24 in the gate; `tests/smuggle.sh` 38/38 over 3 implementations, 10 known disagreements, nightly |
| smuggling: third-party | ✅ `tests/smuggler.sh` — smuggler 134/134 mutations, nothing found; h2csmuggler finds no h2c surface, pinned as a regression |
| smuggling: http-garden | ✅ target built and contract-verified; the full 45-server differential is compiler-hours, run by hand — see [`docs/TestingAgainst_Smuggling.md`](docs/TestingAgainst_Smuggling.md) |
| third-party: proxies (A5) | ✅ five reverse proxies, 63 recorded differences, no chain poisons — and the detector is calibrated-unfired, see the write-up |
| third-party: browsers (A8) | ✅ Chromium, Firefox and WebKit — **27/27** since H-10 closed on 2026-10-03 |
| CI per push (`windows-latest` + `debian:13`) | ✅ build + Hermod tests + 9 harnesses |
| Nightly (Autobahn, both directions) | ✅ gated on floors 481 / 445 |
| demo reachable from WSL containers | ✅ `--bind-any`, no firewall rule needed — unblocked A6, and A5 next |

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

**209/209 checks pass over both transports** — `tests/run-tests.sh`, ~97 s
cleartext, ~300 s over TLS. It read 199 when A2 closed; the ten since are the
two `308` redirect checks that H-1 unblocked and the eight `HEAD` probes that
came with H-23 — which this harness had been unable to see, the demo having
registered `HEAD` by hand on both routes it asked about. See [`tests/README.md`](tests/README.md) for the
per-harness breakdown and what the checks do and do not establish. One new
upstream finding: **H-23**, fixed on 2026-10-03 — and the harness that found
it could not see it, because the demo registered `HEAD` by hand on the two
routes it was asked about.

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
the gate stood at **279/279** (201 raw-wire + 78 curl) when this track closed,
at **303/303** since A6 added `h1desync`, and at **311/311** since H-23 took
the raw-wire half to 209. It read 257 when A3
closed; the twenty-two since are `308` joining `/redirect/{code}` once H-1
landed, the five Digest checks that H-3 made possible, four on the `/ws`
upgrade, and nine on content codings once H-2 was whole. See
[`tests/README.md`](tests/README.md#the-curl-leg).

✅ **The Debian curl leg runs too**, via `tests/run-tests.sh --wsl` → **485/485**.
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

## ✅ A5 · Intermediary interop · P2 · done 2026-09-27

**Image bump 2026-10-02:** nginx 1.27→1.31, HAProxy 3.0→3.4, Envoy v1.31→v1.39
(Caddy and httpd float on their major tags and were already current). **32/32
unchanged**, and three recorded differences *disappeared* — the newer proxies
got stricter and now agree with us:

| | probe | was |
|---|---|---|
| nginx | `chunk-lf-only` | `direct=REJECT chain=TWO` — the chain split one ambiguous message into **two requests** |
| envoy | `chunk-lf-only` | `direct=REJECT chain=TWO`, likewise |
| haproxy | `cl-list-diff` | `direct=REJECT chain=NONE` |

Both `chain=TWO` rows are request-splitting gadgets that these versions no
longer produce. The lines are deleted from `tests/proxy-known.txt`, so if a
future version reintroduces one, the run fails rather than shrugging. Caddy
still differs on `chunk-lf-only` (`chain=ONE`) — its image was not bumped.

Reverse proxies are the strictest HTTP/1.1 consumers in existence, and they
are the only shape in which A6's findings are real: that work found seven
probes on which Hermod, Go and Node place the end of a message differently,
and a disagreement is only an attack when two of them are chained.

`tests/proxies/docker-compose.yml` brings up five — nginx 1.27, HAProxy 3.0,
Caddy 2, Apache httpd 2.4, Envoy 1.31 — each reverse-proxying the demo host.
All pulled, none built, which is the deliberate contrast with the HTTP Garden.
`tests/proxy.sh` drives them; the write-up is
[`docs/TestingAgainst_Proxies.md`](docs/TestingAgainst_Proxies.md).

Four measurements, and they are not equally strong:

| | what | |
|---|---|---|
| `curl` | all 78 curl checks through each chain, against 78/78 direct | every difference recorded by name, a new one fails |
| `framing` | `h1desync --observe` through each chain, against direct | says what each proxy does with an ambiguous message |
| `poison` | the attack: send it, then ask innocent questions on fresh connections | **see the calibration below** |
| `via` | chunked bodies intact, trailers through a re-framing hop, `Connection: close` | |

**What it found.** No chain poisons a connection. Three readings are worth
keeping: `chunk-bws` — a chunk-size followed by a bare space — is forwarded by
**all five** in a shape that gets the hidden request executed, where Hermod
alone answers 400; Caddy splits `cl-te` and `te-cl` into two requests, which
is Go's `net/http` underneath it and the same behaviour A6 measured directly;
and Apache turns Hermod's 400-and-close for the chunked-twice spellings into a
500 of its own, which is only visible because **H-29** was fixed that morning.

**The calibration, which matters more than the result.** A back end was built
that answers one request with two responses — the state a successful desync
leaves behind, verified on the wire — and put behind each proxy in turn. All
five discarded the upstream connection rather than pass the extra response on.
So the poison detector has never been seen to fire through any of them, and
"clean" means *"no chain here produced an attack, and these five would have
absorbed one anyway"*. A real statement about the chain, a weak one about the
origin, and it is written down as such rather than quoted as a pass.

What is **not** done from the original sketch: the reverse direction, Hermod's
`HTTPClient` through a proxy to a foreign origin. `tests/interop.sh` already
points that client at Go and Node directly, and putting a proxy between them
measures the proxy. Left out deliberately rather than forgotten.

## ✅ A6 · Request smuggling / differential fuzzing · P2 · done 2026-09-27

RFC 9112 §11.2 is the section where HTTP/1.1 implementations actually fail, and
it is also the one a single-server harness cannot test. Smuggling *is* a
disagreement — one parser reads one message where the next reads two — so
`h1attack` answering "our server cannot be desynchronised" was always a smaller
claim than it looked.

Four instruments, written up in
[`docs/TestingAgainst_Smuggling.md`](docs/TestingAgainst_Smuggling.md):

| | | |
|---|---|---|
| `tests/h1desync` | 38 ambiguously framed messages; asserts the 24 that RFC 9112 states a rule for, observes the 14 it leaves open | push gate, **24/24** |
| `tests/smuggle.sh` | the same probes against Hermod, Go `net/http` and Node `node:http`, joined on the probe id | nightly, **38/38**, 10 known disagreements |
| `tests/smuggler.sh` | [smuggler](https://github.com/defparam/smuggler) (134 Transfer-Encoding obfuscations, CL.TE and TE.CL each) and [h2csmuggler](https://github.com/BishopFox/h2csmuggler) (must find nothing) | nightly, **3/3** |
| `tests/http-garden/` | Hermod as a target in [the HTTP Garden](https://github.com/narfindustries/http-garden), which compares parse trees field by field across 45 implementations | by hand — see below |

The split between asserting and observing is the design. §6.3 item 4 states a
MUST *and* a status code for a Transfer-Encoding whose final coding is not
chunked; §6.1 says a server **MAY** reject a request carrying both
Content-Length and Transfer-Encoding "or process such a request in accordance
with the Transfer-Encoding alone" and only requires the close. Asserting a
preference on the second kind would be taste dressed as conformance, so those
probes are observed and go to the differential — which is where they belong,
because they are what a chain is built from.

**What it found.** Ten of 38 rows disagree. Three are ours and are one finding:
Hermod accepts `Transfer-Encoding: chunked, chunked`, which §6.1 forbids a
sender to produce, because `AHTTPPDU.cs:422` decides "is this chunked" from the
last coding alone — filed as **H-29**, fixed and pinned the same day
([Hermod#54](https://github.com/Vanaheimr/Hermod/pull/54)), along with **H-30**
which the fix's own verification turned up. Six are Go's, and two of those are a
MUST violation rather than a divergence: `net/http` answers the CL.TE and TE.CL
shapes with two responses and **does not close the connection**, where §6.1 says
"Regardless, the server MUST close the connection after responding to such a
request to avoid the potential attacks." Verified by hand against go1.24.4.

**The Garden is built but not fully run, and that is a cost decision rather
than an omission.** `tests/http-garden/` holds a Dockerfile on the Garden's own
pattern and `HermodGarden`, which runs Hermod on 0.0.0.0:443 and answers with
the JSON parse tree the Garden compares; `tests/http-garden.sh --contract`
verifies that contract on every run. What is not automated is the other
forty-four targets: the Garden builds every one from source with clang and
ASan, which is compiler-hours and gigabytes. `--build --with <target>` takes
them one at a time. Two limitations of our target are written down in the doc
rather than left to be discovered — Hermod's parsed headers are a dictionary,
so field order and duplicate field lines are not reportable, and an exotic
method is answered 405 rather than described.

The pin in the line above is worth one sentence: the URL this section carried
until today was `narf-industries/http-garden`, which does not exist. The
organisation is `narfindustries`, no hyphen, and the wrong URL made `git clone`
hang on a credential prompt rather than fail — twenty minutes spent on a
network diagnosis for a typo.

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

## ✅ A8 · Browser interop · P2 · done 2026-09-27

Every other consumer here was written to be a test. A browser was not, and it
is the least forgiving HTTP/1.1 consumer in daily use.

`tools/browser/interop.mjs` drives **Chromium, Firefox and WebKit** through
Playwright; `tests/browser.sh` is the driver. The battery runs *in the demo's
own `/` document*, so every fetch, `EventSource` and `WebSocket` is a
same-origin request from a real page — the only arrangement in which CORS,
connection reuse and Resource Timing mean anything. Write-up:
[`docs/TestingAgainst_Browsers.md`](docs/TestingAgainst_Browsers.md).

**27 checks, 24 pass**, all three engines agreeing on every one.

Four of the nine are things no other driver here can establish: the browser
naming the protocol itself (`performance.nextHopProtocol` = `http/1.1`, its
verdict rather than ours), a real `EventSource` rather than a read of the SSE
body, `connectStart === connectEnd` as the browser's own account of keep-alive,
and CORS — which curl cannot test at all, because curl simply sends the
request and is answered.

**The three failures were one finding, and it was H-10 — closed 2026-10-03,
so this now reads 27/27 and `tests/browser-known.txt` is empty.**

What it was: the demo's `/cors` route sets `Access-Control-Allow-Origin`, so the
simple cross-origin GET worked. A POST with a custom header is not simple — the
browser sends an `OPTIONS` preflight, and nothing answered it (`405`,
`Allow: GET, POST`), while the identical POST from curl was answered 200.

`OPTIONS` is *still* deliberately not registered on that route, and that is now
the point rather than the gap: the preflight is answered by `HTTPCORSPipeline`
ahead of routing, which is the only place it can be answered. A hand-written
`OPTIONS` handler there would prove nothing about the mechanism — the same
reasoning that kept it unregistered while the gap was open.

This is the track's whole justification in one line: three engines agreed on a
defect that curl, the raw-wire harnesses, the foreign peers and five reverse
proxies all structurally could not see, because none of them sends a preflight.

**Deviation from the sketch above, stated rather than quiet:** bash plus a
Node module, not `tools/browser-interop.ps1`. This repository removed its
PowerShell runners on purpose — two implementations of one runner produce two
numbers that look like agreement, and the sibling projects paid for that
twice. What was taken from the HTTP/2 script is its good idea: the page runs
the battery and reports a verdict, rather than the driver scraping a DOM and
guessing when the run finished.

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
instead of guessing at it. Filed as **H-27**, and ✅ fixed upstream on
2026-10-03: construction is now **0.009 ms**, and the shared-`DNSClient` control
no longer helps at all — 2.347 ms against 2.406 ms in the same run, where it had
been a 36-fold win. That the control stopped paying is the part that confirms the
diagnosis; the speed-up alone would not have.

**Flat throughput on one client is the protocol, not a defect.** HTTP/1.1 has no
multiplexing, so 64 callers on one connection queue: req/s stays put and latency
rises linearly, exactly as it should. Give each caller its own client and the
server does four times the work. Worth stating, because the HTTP/2 sibling has a
superficially identical curve that *is* a defect (`requestStartLock`), and the
two must not be read as the same finding.

Still open here: the external load generators (`h2load --h1`, `bombardier`,
`oha`), which would also stress keep-alive reuse and connection-table cleanup
from outside this process.

## ✅ A10 · Parser fuzzing · P3 · done 2026-09-26

`tests/h1fuzz` — a deterministic mutation fuzzer against three parsers, needing
nothing installed:

| Target | Entry point | What it promises |
|---|---|---|
| `request` | `HTTPRequest.TryParse` | a Boolean, so *any* exception is a finding |
| `response` | `HTTPResponse.TryParse` | the same, on the client's side of the wire |
| `chunked` | `ChunkedTransferEncodingStream` | to refuse malformed framing *as* malformed framing |

Roughly 1–2 million inputs per target per minute, seeded from the request,
response and chunk shapes the harnesses already use, mutated by operators
chosen for HTTP rather than for generality: bare CR and LF injection, digit
runs turned into enormous ones (`Content-Length`, chunk-size), truncation,
line duplication, and splicing two corpus entries together — the shape a
smuggling bug lives in.

**Not SharpFuzz + AFL++, which is what this section asked for.** Written down
so it is a decision and not a drift: AFL++ is a system install and Linux-only,
so a clean checkout on Windows could not run it and CI would need a package
step for a job measured in hours; and coverage-guided fuzzing has no budget at
which it is a *gate*. What is here is weaker and cheap enough to gate on —
every run is `--seed N`, every finding prints the seed and iteration that
produced it and saves the exact bytes, and `--replay <file>` reproduces it with
a stack trace. If AFL++ is ever installed, these three functions are the entry
points to instrument and the saved corpus is the seed set to hand it.

**In the gate** it runs with a fixed seed and five seconds per target, which
makes it a regression test rather than a fuzzer: the same inputs every run, so
red means this change broke something and not that today's dice were unkind.
**Nightly** it runs ten minutes per target with a seed that moves with the
date, which is where the exploring happens.

A finding is not "the parser rejected it" — that is the right answer to almost
all of this input. It is an exception the target does not promise, an input
over the deadline, or output out of all proportion to input. Findings are
deduplicated by signature: one defect reached by 386,214 inputs is one defect,
and `tests/h1fuzz/known-findings.txt` holds the ones already filed, reported
loudly but not failing the run — the same bargain `tests/autobahn.sh` strikes
with its floor. Deleting a line there is how a fix gets verified.

**First run, first finding: H-28**, in thirty seconds.

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
| ✅ | **H-10** | No automatic CORS preflight | WHATWG Fetch | P2 | M | `Access-Control-*` were settable, but the `OPTIONS` preflight was answered `405` — and it is the one request a handler cannot own, because routing refuses it before any handler is consulted. **Fixed 2026-10-03, [Hermod#81](https://github.com/Vanaheimr/Hermod/pull/81), three commits.** First the resource-level `OPTIONS` (RFC 9110 §9.3.7), which routing was refusing while the rejection already carried `Methods.Keys` — that alone does not fix a preflight. Then an opt-in `HTTPCORSPipeline` + `CORSPolicy` installed *ahead of* routing, which is the only place the application can take the decision back. Then the pipeline asking the router for the methods the route actually has, so `Access-Control-Allow-Methods` cannot promise what no handler answers. **Demonstrated closed by A8: 27/27, up from 24/27**, all three engines, and `tests/browser-known.txt` is now empty — deleting a line there is how the fix was verified, not an assertion here |
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
| ⬜ | **H-28** | `ChunkedTransferEncodingStream` reports one malformed-framing case out of eleven as a bare `System.Exception`, which a caller cannot filter on | — | P3 | XS | Found by **A10** on its first run, 2026-09-26. Ten of the eleven throw sites use `HTTPInvalidChunkException`, which is a `FormatException` and therefore catchable as "this input was malformed"; `ChunkedTransferEncodingStream.cs:665` throws `new Exception("Expected CRLF")` thirty lines below a sibling that throws `HTTPInvalidChunkException` for the same condition. A caller wanting to distinguish bad input from a bug in the decoder has to catch `Exception`, which swallows both — and H-2's `ContentDecodingStream` exists precisely because the stack decided elsewhere that callers should have one exception type to catch. Listed in `tests/h1fuzz/known-findings.txt`; deleting that line is the regression test |
| ✅ | **H-29** | `Transfer-Encoding: chunked, chunked` was accepted and the body decoded once, although RFC 9112 §6.1 forbids a sender to produce it | RFC 9112 §6.1 | P3 | XS | Found by **A6** on 2026-09-27, as three rows of the differential that turned out to be one finding: `te-dup`, `te-obf-sp` and `te-chunked-chunked` all reduce, via RFC 9110 §5.3, to the same value. `AHTTPPDU.cs:422` asked only whether the **last** coding is chunked — right for `chunked, gzip`, where §6.3 item 4 then requires the 400 we already gave it, and silently dropping the duplicate here. Not a violation: §6.1 binds senders, and item 4 does not fire because chunked *is* final. It was accepting a framing no conforming client may send, on the one field smuggling is made of, while both peers refused it (Go: 501, Node: 400). **Fixed 2026-09-27, merged and pinned, [Hermod#54](https://github.com/Vanaheimr/Hermod/pull/54).** The predicate requires chunked to be final *and* to appear exactly once; the server answers 400 as a check of its own, because being stricter than the RFC is a choice and lumping it in with the MUST above it would have hidden that. Getting the two-line spellings to reach the predicate turned up **H-30**. 17 tests, and the differential said so itself: the three rows went to `REJECT[400]` and `tests/smuggle.sh` reported them as "no longer disagreeing" |
| ✅ | **H-30** | Repeated `Transfer-Encoding` field lines made the field **vanish** outside the server's own parse path, and a response refused for a framing reason kept its connection | RFC 9110 §5.3, RFC 9112 §6.3 | P2 | S | Two defects, found while verifying H-29's client half and fixed with it in [Hermod#54](https://github.com/Vanaheimr/Hermod/pull/54). *The field vanishing*: only `HTTPRequest.TryParse`'s server overload combined repeated lines; the public `TryParse(text, out request)` and **every response** kept them as a `String[]`, which `GetHeaderField<String>` cannot cast and so returned null — a message carrying `Transfer-Encoding: chunked` twice was read as declaring no transfer coding at all. Three parse paths, three answers to the same octets, inside one library. *The connection*: `TryValidateResponseFraming` has always refused a response whose coding it cannot frame, but kept the connection — and the refusal's reason is that the body's end is unknown, so it was never consumed. Measured: a second request on that connection came back "Invalid HTTP response status line", having read `5\r\nhello`. It shows only when the body arrives in a later TCP segment than the head, which is why the first version of its test was green before the fix |
| ✅ | **H-27** | Every `HTTPClient` built its own `DNSClient` **in its constructor**, whose default searches the machine's network configuration for resolvers — tens of milliseconds per construction, even when the URL is a literal IP address that will never be resolved | — | P2 | S | Found by **A9** on 2026-09-26, and measured rather than inferred: a fresh client per request was 39.4 ms p50, of which 38.3 ms was the constructor and 1.06 ms the request, and passing one shared `DNSClient` took the whole thing to 0.449 ms. **Fixed 2026-10-03, merged and pinned, [Hermod#87](https://github.com/Vanaheimr/Hermod/pull/87).** The DNS client is a `Lazy<IDNSClient>`: one handed in is wrapped as a value, one of the client's own making is built when the property is first read — and disposal asks the `Lazy` rather than the property, because reading it would build the very client that line then throws away and move the cost from the way in to the way out. Re-measured the same day on one machine, before and after: constructing the client **57.654 ms → 0.009 ms**, a fresh client per request 59.324 ms → 2.347 ms. The control is the better evidence: a shared `DNSClient` had been a 36-fold win and is now worth nothing at all (2.347 against 2.406 ms in one run), so there is no longer anything to share — the remaining ~2 ms is the handshake and the request, which no resolver can account for. Two existing tests had to change to keep testing what they test: `DisposeStopsTimersTests` counts the timers twenty clients run while alive against those left behind, and with no DNS client made there is no cache timer to leave — `TimerCount`'s own guard against vacuity fired with *"timers running while 20 HTTP clients were alive"*. They now ask for the DNS client, which is both something to count and independent evidence, from `Timer.ActiveCount` rather than from a clock. `HermodTests/HTTP/HTTPClientLazyDNSClientTests.cs` is the regression test, observing construction through the logger factory because the default DNS client is made with a logger of its own and `ATCPClient` asks for an `IDNSClient` logger at exactly one place. Without the fix three of its five tests fail — the constructor, the disposal and a whole request to a literal address — and the two that hold either way stay green. Still eager, deliberately: `ATCPServer`, which hands its DNS client to the Warden inside its own constructor, and `ICMPClient`, `Warden`, `ANotificationSender` and `ModbusTCPClient`, which call `new DNSClient()` directly — same mechanism, other components, and a server pays once per start rather than once per request |
| ✅ | **H-31** | `DNSClient`'s two constructors declare opposite defaults for the resolver search — `false` with manual servers, `true` without — and both forwarded to one body reading `?? true`, so an **explicitly passed `null`** searched although the signature it was read from says it does not | — | P3 | XS | Found 2026-10-03 while fixing **H-27**, and fixed with it in [Hermod#87](https://github.com/Vanaheimr/Hermod/pull/87). The declared `false` held only as long as the argument was left out — C# takes the callee's default for an omitted one — so the chaining overloads were right and a caller forwarding an optional setting, which is where a `Boolean?` comes from, was not. Not only a cost: multi-server queries race and the fastest valid response wins, so a resolver joining the set unasked can answer before the one the caller named. The body now reads `?? false` and the constructor without manual servers coalesces its own `true` before forwarding, so each default is resolved where it was declared. Nothing upstream passes `null` here, so no caller changed behaviour; what changed is that the signature can be relied on. `HermodTests/DNS/Clients/DNSServerSearchDefaultTests.cs` covers all seven cases and each first asks what the search finds on the machine it runs on, calling `Assert.Ignore` when that is nothing — on a container naming no resolvers, "searched" and "did not search" look alike, and a green check there could not have gone red |
| ✅ | **H-23** | `HEAD` was not derived from `GET` — an unregistered `HEAD` was answered `405`, and the `Allow` field it returned omitted `HEAD` as well | RFC 9110 §9.1, §9.3.2 | P2 | S | Found while building **A2**. **Fixed 2026-10-03, merged and pinned, [Hermod#93](https://github.com/Vanaheimr/Hermod/pull/93).** **The citation in this row was wrong until the fix.** It read §9.3.2 and quoted *"A server SHOULD support HEAD for any resource it supports GET for"* — a sentence that is **not in RFC 9110**, in that section or any other; the same misquote sat in a comment in `Demo/Program.cs`. What the RFC says is §9.1, *"All general-purpose servers MUST support the methods GET and HEAD"*, which binds the **server** and not the resource: §9.1 names the `405` as how a resource refuses a method it does not allow, so answering `405` to `HEAD` was per-resource conformant and a server-level MUST it could not back up. §9.3.2 supplies the semantics — `HEAD` is `GET` with the content suppressed, same header fields. Routing now falls back to the `GET` handler at the two sites where the automatic `OPTIONS` answer sits, as one condition rather than a branch; a registered `HEAD` handler still wins. No body logic was needed: `AHTTPServer.HasNoResponseBody` already suppressed it, which is why a chunked `GET` answered as `HEAD` carries `Transfer-Encoding: chunked`, no body, and a reusable connection — RFC 9112 §6.3 item 1 ends any `HEAD` response at the blank line whatever the framing fields say. It costs what the `GET` costs, because the handler generates content the writer drops; §9.3.2 prefers minor header inconsistencies to exactly that, so `HTTP1/README.md` says so and points an expensive handler at `Request.HTTPMethod`. `PathNode.AdvertisedMethods` is now the single definition of what a resource offers — registered plus the two the server adds — read by the `405`, the automatic `OPTIONS` and `GetRegisteredMethods`, through which the CORS pipeline builds `Access-Control-Allow-Methods`: the `+ OPTIONS` of H-10 was right for the two answers beside it and reached nothing else. Six tests upstream, four of them red without the fix; eight probes here, and the demo's two hand-registered `HEAD` handlers are gone, which is what makes the gate's existing `HEAD` checks measure the derivation. Against the unfixed library `h1semantics` then reads 67/73 — including the plain `HEAD /` that had been green since A2 because the demo covered the gap |

---

# Suggested sequence

```
✅A0 ──▶ ✅A1 ──┬──▶ ✅A2 ──▶ ✅A3 ──▶ ✅A11 (CI: build + harnesses + curl)
                │
                ├──▶ ✅A4  (Autobahn — both directions, both gated nightly)
                │
                └──▶ ✅A5, ✅A6, ✅A7, ✅A8  (external suites)

Track B in parallel: seventeen of thirty-one are in. ✅H-1 and ✅H-2 first
(small, high leverage), then ✅H-3 and ✅H-16, the two Warden findings
✅H-25 and ✅H-26, and ✅H-4 ✅H-6 ✅H-8 ✅H-21 ✅H-22 together on
2026-09-26. ✅H-29 and ✅H-30 came out of A6, ✅H-10 out of A8 and ✅H-27
out of A9 — the four that no amount of reading the code had produced. A3 and
A4 turned out to need none of them, so nothing was ever waiting on this track.
```

**First milestone:** ✅ A0 ✅ + A1 ✅ + A2 ✅ + A3 ✅ + H-1 ✅ + H-2 ✅ — a
runnable demo host, the raw-wire gate, the curl matrix, and the two Hermod fixes
that are cheap and obviously right. Six of six, finally: H-2 turned out to be
four fixes rather than one, and the last of them closed on 2026-09-24. The
number is now **311/311**, and 78 of those come from a client nobody here wrote
— the first part of it that is not self-assessment.

**Second milestone:** ✅ A4 + ✅ A11 + ✅ A5 — Autobahn reproducible from a
clean checkout in both directions, CI green on two legs, proxy interop. Three of
three, and with A5 in there is no track left open: **A0–A11 are all done.** What
remains is Track B and keeping the suites honest as their peers move.

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
