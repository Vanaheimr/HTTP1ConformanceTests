# HTTP/1.1 Conformance Tests & Demo — C# / .NET 11

This repository is the **runnable demo host** plus the **conformance / interop
test drivers** for the from-scratch HTTP/1.0 + HTTP/1.1 stack (built directly on
`TcpClient`/`SslStream` — no `Kestrel`, no `HttpListener`, no
`System.Net.Http`). The stack itself lives in the Vanaheimr **Hermod** library,
pulled in here as a git submodule under `libs/Hermod/Hermod/HTTP1/`. This repo
adds the `Demo/` host, the `tests/` raw-wire harnesses, and the third-party
suite drivers; the **1630 NUnit tests** live with the stack in Hermod
(`HermodTests/`, filter `FullyQualifiedName~Hermod.Tests.HTTP.`).

Sibling projects, same shape: **HTTP2ConformanceTests** and
**HTTP3ConformanceTests** in the same parent directory.

This file (CLAUDE.md) holds the **working notes for this repo**, a
concern-level map of the stack under test, and the conventions. The
reader-facing reference — the specification matrix with Hermod's actual status
per RFC — is [`README.md`](README.md); the work plan is [`PLAN.md`](PLAN.md);
the chronological build history is [`docs/BUILD_LOG.md`](docs/BUILD_LOG.md).

## Build & Run

```bash
dotnet build HTTP1.slnx
dotnet run --project Demo/HTTP1.Demo.csproj
# then, from another shell:
curl --http1.1 http://localhost:8080/
curl --http1.1 http://localhost:8080/echo -d "Hello HTTP/1.1!"
curl --http1.1 -k https://localhost:8443/
curl --http1.0 http://localhost:8080/         # HTTP/1.0 path: close-delimited
```

Target framework is `net11.0`. TLS uses a self-signed cert generated at startup.

**Tests:** most coverage is the **1630 NUnit tests** in `libs/Hermod/HermodTests/`
under `FullyQualifiedName~Hermod.Tests.HTTP.` — of which **992** are the HTTP/1.x
protocol regression selection and **246** cover RFC 6455/7692 WebSockets. CI gates
on those 1630 plus the single test in `Tests.HTTPS.` = **1631** — a filter that
reaches neither `Tests.TCP` nor `Tests.Warden`, which is where H-27's fix lives
and why its own regression test was written through `HTTPClient` rather than
through `ATCPClient`. The regression selection is the forty-seven fixtures
`Hermod/HTTP1/README.md` names, by concern, since Hermod#221 (2026-11-03). It
was eleven names and 381 before: a fixture joins only when it is named there,
and from 2026-09-24 on none was, so every finding that brought a fixture of its
own bypassed it. A new fixture of that kind adds its name in the same PR. These are run counts, not
`--list-tests` counts; see the note under the coverage table in
[`README.md`](README.md) for why the two differ by three here:

```powershell
dotnet test HTTP1.slnx --filter "FullyQualifiedName~Tests.HTTP."
```

The harnesses under `tests/` and the third-party drivers are being built —
see [`PLAN.md`](PLAN.md) for what exists and what does not.

## Two curls, deliberately

| | HTTP/2? | proves |
|---|---|---|
| Windows `curl` 8.21 (Schannel) | **no** | a pure HTTP/1.1 witness — cannot accidentally upgrade |
| WSL/Debian `curl` 8.14 (nghttp2/nghttp3) | yes | that `--http1.1` and ALPN are honoured *when the client could do otherwise* |

The second is the more interesting test. A client that *could* upgrade but does
not says something about ALPN negotiation that a client which simply cannot
never will.

## Containers run in WSL/Debian

Autobahn, the proxy matrix and http-garden all want Docker. It is installed in
**WSL/Debian** (26.1.5) — not Docker Desktop. WSL has no systemd, so the daemon
is down after `wsl --shutdown` or a reboot, and **no script starts it** — they
assume it is up and fail late if it is not. Start it by hand:

```
wsl -d Debian -u root service docker start
```

`-u root` rather than `sudo`, which prompts for a password this account does not
supply non-interactively and then hangs with no tty.

**If containers stop answering from Windows** while they answer inside WSL, the
WSL2 localhost relay has gone stale — `tests/proxy.sh` probes the published
ports over `127.0.0.1` from the Windows side, so every proxy reports "never
answered". `wsl --shutdown`, then start the daemon again as above; that restores
the forwarding. Seen 2026-10-02, with all five proxies down including the two
whose image versions had not changed, which is what identified it as the
environment rather than the bump.

**Reaching the demo from WSL:** start it with `--bind-any` (0.0.0.0 instead of
loopback) and address it by the **host IP** from `ip route show default`, not
`localhost`. `tests/run-tests.sh --wsl` does both. Windows Defender Firewall
keys its rule to the executable's **path**, so a new build directory (as
`net10.0` → `net11.0` on 2026-11-19) needs its own inbound Allow rule. Answer
the dialog Windows shows on the first start with *Allow*. On 2026-11-19 the
new path had a **Block** rule instead, and `--wsl` reported the demo unreachable.
Opt-in on purpose — a plain test run must never widen a listener.

Anything invoking a Linux binary from Git Bash (`wsl -d Debian -- curl`) also
needs `MSYS2_ARG_CONV_EXCL='*'` against MSYS path rewriting, and must escape
glob characters against the shell `wsl.exe` spawns. See `tests/README.md`.

## Architecture — the stack under test

Everything below lives in the submodule, not here.

### `Hermod/HTTP1/` — the HTTP/1.x stack (~55 000 lines, 100 files)

| Path | Concern |
|---|---|
| `AHTTPPDU.cs`, `AHTTPPDUBuilder.cs` | the direction-neutral message: header section, body, chunk extensions, trailers |
| `HTTPHeaderField.cs` | the typed field model + the common (request *and* response) fields |
| `Request/` | `HTTPRequest`, its builder, and the request-only typed fields |
| `Response/` | `HTTPResponse`, its builder, and the response-only typed fields |
| `Server/` | `AHTTPServer` → `HTTPServer` / `HTTPTestServer`, `HTTPConnection`, pipelines, security |
| `Server/ChunkedTransferEncoding/` | `ChunkedTransferEncodingStream`, chunk extensions, trailer validation, metadata limits |
| `Server/URLMapping/` | the routing tree (host → path → method → content type), `HTTPRequestHandlers`, and `HTTPStandardHandlers` — the static-file, resource and redirect helpers. One generation since H-18; the second was 2 228 lines of comment and two live files filed as old |
| `Client/` | `HTTPClient` / `HTTPSClient`, `HTTPClientPool`, logging, SOAP |
| `ServerSentEvents/` | `HTTPEventSource`, `HTTPEvent` — SSE server + client |
| `WebSocket/` | RFC 6455 + RFC 7692, server + client, `IncrementalUtf8Validator`, per-message deflate |

### `Hermod/HTTP/` — the version-neutral HTTP model

Shared with the HTTP/2 and HTTP/3 stacks: `HTTPMethod` (~50 tokens),
`HTTPStatusCode`, `URL`/`HTTPPath`/`QueryString`/`URIScheme`/`HTTPHostname`,
`HTTPContentType`, `AcceptTypes`, cookies, the authentication values, and the
`HTTPAPI` application layer.

### The server API, in one worked example

```csharp
var httpServer = await HTTPServer.StartNew(TCPPort: IPPort.Parse(8080));
var httpAPI    = httpServer.AddHTTPAPI();

httpAPI.AddHandler(
    HTTPMethod.GET,
    HTTPPath.Root + "hello",
    HTTPDelegate: request => Task.FromResult(
        new HTTPResponse.Builder(request) {
            HTTPStatusCode  = HTTPStatusCode.OK,
            ContentType     = HTTPContentType.Text.PLAIN,
            Content         = "Hello World!".ToUTF8Bytes()
        }.AsImmutable
    )
);
```

Path parameters are `{name}` (one segment) and `{name..}` (the rest), read back
via `request.ParsedURLParametersX`. Chunked responses set `ChunkWorker` instead
of `Content`. SSE is `httpAPI.AddEventSource<T>(id)` + `MapEventSource(…)`.

## Current state

**A0 done** — repository scaffolding, the specification matrix, the work plan.
**A1 done** — the demo host on `:8080` / `:8443` / `:8081`. See [`Demo/README.md`](Demo/README.md).
**A2 done** — the raw-wire harnesses (201 checks).
**A3 done** — the curl matrix (99 checks, 100 where curl has HTTP/2 *and* the
target is local, 105 over TLS — see [`tests/README.md`](tests/README.md)). The gate is **337 cleartext and
343 over TLS** (`tests/run-tests.sh`, ~103 s cleartext / ~270 s TLS). See
[`tests/README.md`](tests/README.md).
**A4 done** — Autobahn, **both directions**, both gated nightly on a floor
rather than on perfection.

| | Driver | Result |
|---|---|---|
| server | `tests/autobahn.sh` → `fuzzingclient` vs. the demo host on `:8081` | **481 / 517**, 36 declined, 0 hard |
| client | `tests/autobahn-client.sh` → our `WebSocketClient` vs. `fuzzingserver` | **445 / 517**, 72 declined, 0 hard |

Zero hard failures on both sides: everything either half attempts, it gets
right. The declines are the interesting part, and they are not the same thing
twice.

The server's 36 are sections 13.3 and 13.5, where the peer offers
`server_max_window_bits=9` and this server must refuse what `DeflateStream`
cannot deliver — RFC 7692 §7.1.2.1 requires that refusal rather than permitting
it.

The client's 72 are sections 13.3–13.6, and there it is the SUITE that declines
us: those sections expect a client offer carrying `server_max_window_bits`, and
our offer is the fixed constant `WebSocketPerMessageDeflate.ClientOfferHeader`,
which never carries it. Nothing is wrong on the wire; the compression those
sections wanted to exercise is simply never negotiated. Both numbers trace back
to one fact — `DeflateStream` exposes no window-size control — from its two
opposite ends.

Measured 2026-09-23, the first time Hermod's WebSocket client had ever been
pointed at a foreign suite. See
[`tests/TestingAgainst_Autobahn.md`](tests/TestingAgainst_Autobahn.md).

| | |
|---|---|
| Hermod's own NUnit suites | 1630 `Tests.HTTP.` / 992 regression selection / 246 WebSockets, all run counts — 1631 under the filter CI gates on, which adds the one test in `Tests.HTTPS.`; see [`README.md`](README.md) for each filter. Beside them, measured against the same pin: **3538** across HTTP/2, HTTP/3, WebSocket, TCP, Timers, Warden, DNS and SMTP. That figure moves between pins faster than anything here does, because most of Hermod's own work happens there and in SSH and QUIC, which no filter here selects: 2321 on 2026-10-28, 2324 and then 2337 on 2026-10-29, 2392 on 2026-10-30, 2465 on 2026-11-03, 2735 on 2026-11-19, whose 270 more are mostly DNS tests, 2863 on 2026-11-20, 2867 at `62eae97e`, whose 4 more are `IPVersionPreferenceTests` for RFC 8305's connection attempts in `ATCPClient`, 3004 at `832a32cc`, whose 137 more are SMTP's, 3019 at `6011efc6`, whose 15 more are SMTP's too, 3117 at `cc4bd449`, whose 98 more are SMTP's, 3219 at `862434cc`, whose 102 more are SMTP's 99, from Hermod#282, #284 and #285, and the three new HTTP/2 `CachingClientTests`, 3310 at `d27b68b0`, whose 91 more are SMTP's, from Hermod#287 to #290, 3455 at `c9f237f7`, whose 145 more are SMTP's 144, from Hermod#293 to #295, #297 and #298, and H-47's one HTTP/2 `CachingClientTests`, 3458 at `81ab1c86`, whose 3 more are H-45's one HTTP/2 `CachingClientTests` and Hermod#299's two `TLSCipherSuiteTests`, 3527 at `0800bcc7`, whose 69 more are SMTP's, from Hermod#301 to #305 and #308, and 3538 since `51bdc7d5`, whose 11 more are SMTP's, from Hermod#309. All 3538 pass here, `DANE_EE_authenticates_the_next_hop` included, which failed deterministically on this machine from the day it arrived with Hermod#136 until `09da8f3f` stopped Windows from fetching certificates while building a DANE chain — SMTP, not ours, recorded in `docs/BUILD_LOG.md` (2026-10-04, 2026-10-28) |
| this repo's gate | **337** cleartext, **343** over TLS (214 raw-wire + 99 curl + 24 desync; the TLS leg's six more are the ALPN check, curl's alt-svc cache and H-36's four misdirected requests, none of which a cleartext connection carries) — 303 before H-23's eight `HEAD` probes, 311 before H-9's and H-17's, 313/314 before H-7's `Alt-Svc` on 2026-10-28, 314/316 before the six RFC 9530 digest checks on 2026-10-29, 320/322 before H-36's four the same day, 320/326 before H-15's four problem-details checks later still, 324/330 before H-14's three link checks after that, 327/333 before H-40's five early-hints checks and H-19's two on `Content-Disposition` on 2026-10-30, 334/340 before H-42's three on the absolute-form on 2026-11-21. The TLS leg has been in the nightly since H-14, on Windows and on `debian:13`, where curl's HTTP/2 makes it 344 |
| with `--wsl` (second curl build + foreign peers + the smuggling differential) | **539/539** |
| reference peers (A7) | **65/65** + 3 skips — Go, Java, Node, Python, wget as clients; Go and Node as servers. One of the 65 is a known CPython deviation pinned rather than passed: `http.client` takes a `103` for the final response ([python/cpython#72756](https://github.com/python/cpython/issues/72756)). Another is pinned only on the JDKs that need it: since the October 2026 updates (21.0.13, 25.0.5; JDK-8383439, not public) `java.net.http` fails any chunked response with a trailer section with "CR expected". The nightly's runner took 25.0.5 on 2026-10-29, and the Java client then died after two checks for four nights, uncounted — see BUILD_LOG 2026-11-01 |
| benchmarks (A9) | `tests/h1bench`, not a gate — 0.296 ms p50 against Kestrel's 0.244 ms on the same loopback, .NET 11, the median of seven runs. September's 0.240 against 0.252 did not reproduce, not even for the old pin on .NET 10 |
| parser fuzzing (A10) | `tests/h1fuzz` — request, response and chunked parsers; deterministic in the gate, ten minutes per target nightly. Found **H-28** on its first run; fixed 2026-10-03, and `known-findings.txt` is empty again |
| browsers (A8) | `tests/browser.sh` — Chromium, Firefox and WebKit through Playwright, the battery running inside the demo's own document. **27/27**, all three engines agreeing. It read 24/27 until 2026-10-03: the browser's `OPTIONS` preflight was answered 405 while the identical POST from curl got 200 — **H-10**, the finding no other driver here could reach, and the one this track existed to find. See [`docs/TestingAgainst_Browsers.md`](docs/TestingAgainst_Browsers.md) |
| caching (A12) | `tests/cache-tests.sh` — Hermod's client cache through [cache-tests](https://github.com/http-tests/cache-tests), the suite behind cache-tests.fyi, behind `tests/cache-proxy/`, a reverse proxy whose every request goes through one shared `HTTPClientCache`. **480 tests as recorded**, nightly: 285 pass, 94 checks answer yes, 5 required tests fail, against 13 to 37 for the seven proxies and the CDN the suite publishes. The other 101 results are in `tests/cache-tests-known.txt` with their reasons, and the run fails on any that changes, in either direction. Found **H-44** to **H-49** on 2026-11-22. H-44 and H-46 were fixed the same day, H-47, H-45 and H-48 on 2026-11-24, and H-49 on 2026-11-25. All six are fixed. Calibrated without a cache (22 pass) and as a private one (exactly the 20 shared-cache tests turn red). See [`docs/TestingAgainst_CacheTests.md`](docs/TestingAgainst_CacheTests.md) |
| intermediaries (A5) | `tests/proxy.sh` — the demo behind nginx, HAProxy, Caddy, Apache httpd and Envoy, all pulled rather than built. **32/32**: every curl and framing difference from the direct run recorded by name in `tests/proxy-known.txt`, and no chain leaves a response on a pooled connection. The poison detector is *calibrated-unfired* — all five proxies absorb a synthetic poison — which is written down rather than quoted as a pass. Since H-40 it also shows Caddy handing a `103` to an HTTP/1.0 client, which RFC 9110 §15.2 forbids: Go's `net/http` writes any `1xx` whatever the request's version, measured on go1.24.4 and go1.27.3 and unchanged on Go's `master`. Not ours to fix; filed as [golang/go#82743](https://github.com/golang/go/issues/82743). See [`docs/TestingAgainst_Proxies.md`](docs/TestingAgainst_Proxies.md) |
| smuggling (A6) | `tests/h1desync` **24/24** in the gate — the requirements RFC 9112 actually states, quoted next to each probe. `tests/smuggle.sh` compares Hermod, Go and Node: 31 of 38 rows agree, 7 are pinned in `tests/smuggle-known.txt`. `tests/smuggler.sh` adds two third-party suites, and `tests/http-garden/` puts Hermod in the HTTP Garden. Found **H-29** and **H-30**, both fixed and pinned the same day ([Hermod#54](https://github.com/Vanaheimr/Hermod/pull/54)), and a MUST violation in Go's `net/http` that is not ours to fix: [golang/go#80942](https://github.com/golang/go/issues/80942), fixed on Go's `master` for 1.28 and still in go1.27.3. See [`docs/TestingAgainst_Smuggling.md`](docs/TestingAgainst_Smuggling.md) |
| Autobahn (server) | **481/517** + 36 declined, 0 hard failures — nightly, gated on the floor. The intermittent mid-case drop was **H-25**, fixed 2026-09-24. On 2026-11-01 the kernel OOM-killed the suite itself in 13.4.13, once, with the demo alive. The suite peaks at 3 607 MiB over a local run (2026-11-19), against the demo's flat 120 MB, and most of that is outside PyPy's GC heap. In November three more nights died at 14 516–15 147 MiB, and why the runner reaches four times what this machine does is open. Both drivers print the kernel's own report of a kill and, since 2026-11-19, the suite's memory per section on every run |
| Autobahn (client) | **445/517** + 72 declined, 0 hard failures — nightly, gated on the floor |

Building A1 and A2 produced three upstream findings between them (**H-21** and
**H-22**, both fixed 2026-09-26, and **H-23**, fixed 2026-10-03), which is the pattern
to expect: this repository is the first consumer of these APIs that is not also
a test written by their author.

### What the state analysis found

Documented per-RFC in [`README.md`](README.md), tracked as H-1…H-49 in
[`PLAN.md`](PLAN.md). The four that mattered most are the four that are done —
kept here struck through rather than deleted, because what each one turned out to
be is more useful than what it was reported as:

| | |
|---|---|
| ~~**H-2**~~ | *no content coding at all for HTTP/1 bodies* — half wrong when written (`SinglePageAppHandler` had compressed static files all along) and four fixes rather than one: decoding a buffered body, decoding a streamed one, the client's `Accept-Encoding`, and a server-wide compression filter. Whole 2026-09-24 |
| ~~**H-3**~~ | *`HTTPDigestAuthentication` is not RFC 7616* — it was dead code nothing referenced. Fixed 2026-09-24 by moving the RFC 9110 §11 framework out of `HTTP2/`, where it had made the already-correct scheme unreachable. curl authenticates with SHA-256 |
| ~~**H-1**~~ | *`308` missing, `425` under the stale name `NoCode`* — fixed 2026-09-23, with the rest of the IANA registry and an `IsNotSuccessful` that was constant false |
| ~~**H-16**~~ | *the general HTTP server has no `Upgrade` dispatch* — fixed upstream 2026-09-16, and the demo's `/ws` route landed 2026-09-24 |

### The distinction the matrix exists to preserve

"Hermod has a `Range` header field" is not the claim "Hermod serves `206`". The
matrix uses six markers rather than a checkmark because the interesting question
in an HTTP stack is not implemented/missing but **who owns the semantics**:
typed-but-no-policy (🟡), deliberately-the-handler's (🔵), and genuinely absent
(❌) are three different things. Collapsing them is how a design decision reads
as a defect.

Concretely, for the harnesses: a handler registered with `AddHandler` gets no
automatic `206`/`304`, and that is correct. Since H-37 (2026-11-20) a resource
registered with `AddResource` gets them from the library, the layer HTTP/2 has
always used. The demo's `/files/resource.txt` is such a resource, so
`h1semantics` measures the library there. Before, it measured the demo's own
`ServeResource`. The negotiation on `/files/greeting` is still the demo's own.

## Conventions

- English for code, identifiers, comments, and commit messages.
- Style follows the surrounding Vanaheimr/Hermod code: aligned member
  declarations, `#region` blocks per concern, RFC section references in comments.
- Every public enum / interface / class / struct / record in its **own file**
  named after the type.
- Dependency-free (BCL only) for anything that could end up in the stack.
- **Findings get fixed upstream, not just reported.** A gap found here becomes a
  branch in the Hermod submodule → PR against `Vanaheimr/Hermod` → merge →
  submodule pointer bump here. Every fix ships with a focused regression test in
  `HermodTests/HTTP/` and updates the support statement + verification date in
  `Hermod/HTTP1/README.md`.
- **Test placement:** in-process unit and integration tests live with the stack
  in `HermodTests/`. This repository holds only demo-driven harnesses,
  third-party drivers and tooling. A Track B fix's regression test goes upstream
  *with the fix* — otherwise fix and test drift into separate repositories.
- **Interop testing is part of "verified", not optional.** Today every HTTP/1.x
  interop test is .NET against .NET. That is the single biggest weakness in the
  current coverage: two stacks sharing a runtime also share assumptions. The
  third-party tracks (A3–A8) exist to break that.
- **Ask of a green check what would make it red.** Six defects in this
  repository were not tests that passed wrongly but checks *structurally
  incapable* of failing: a known-difference file that could not tell "stopped
  differing" from "never looked", a `--filter` that read *not measured* as *no
  longer differs*, an `.exe`-preference bug unreachable on `ubuntu-latest`
  so that the green nightly leg was never evidence about that path, an
  Autobahn driver whose `Stalled: 0, threw: 0` counted a case that never
  connected as one that ran (2026-10-08), and a framing match in
  `tests/proxy.sh` that compared the probe and not the shape measured, so a
  chain that began to execute a hidden request would have passed as already
  recorded (2026-11-01), and a peer driver in `tests/interop.sh` that read a
  client program dying halfway as a shorter list, so the nightly said 56/56
  while Java ran two of its eleven checks (2026-11-01). Running the
  suite more often reproduces the same vacuum. **A passing CI leg is evidence
  about the configuration CI runs and about no other** — a Windows-built tree
  driven from WSL is not one of them. See `docs/BUILD_LOG.md`, 2026-10-02.
- **Runner scripts are bash, not PowerShell.** CI runs on Linux and Git Bash
  makes the same script work on Windows — one script, no drift between two
  copies. Keep them POSIX-ish and free of Windows-only cmdlets.

## References

- [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110) — HTTP Semantics (STD 97)
- [RFC 9111](https://www.rfc-editor.org/rfc/rfc9111) — HTTP Caching (STD 98)
- [RFC 9112](https://www.rfc-editor.org/rfc/rfc9112) — HTTP/1.1 (STD 99)
- [RFC 1945](https://www.rfc-editor.org/rfc/rfc1945) — HTTP/1.0
- [RFC 6455](https://www.rfc-editor.org/rfc/rfc6455) — WebSocket · [RFC 7692](https://www.rfc-editor.org/rfc/rfc7692) — permessage-deflate
- [WHATWG HTML — Server-Sent Events](https://html.spec.whatwg.org/multipage/server-sent-events.html)
- the full matrix (~55 specs, 16 sections) is in [`README.md`](README.md)
- the stack's own reference: `libs/Hermod/Hermod/HTTP1/README.md` and
  `libs/Hermod/Hermod/HTTP1/WebSocket/README.md`
