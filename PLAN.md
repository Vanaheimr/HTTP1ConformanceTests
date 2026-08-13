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

**Current state (2026-08-13):** **A0 ✅**, **A1 ✅** (demo host, 3 listeners,
14 routes), **A2 ✅** (6 harnesses, **199/199 checks green over cleartext *and*
TLS**), **A3 ⬜** next. Track B: **23 findings open**, none fixed upstream yet —
H-1 and H-23 are the cheapest starting points.

| Gate | State |
|---|---|
| `dotnet build HTTP1.slnx` | ✅ 0 warnings, 0 errors |
| `tests/run-tests.sh` | ✅ 199/199, ~97 s |
| `tests/run-tests.sh --tls` | ✅ 199/199, ~300 s |
| Hermod `Tests.HTTP.*` | ✅ 440 tests |
| third-party suites | ⬜ none wired in yet |

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
transcript. Two new upstream findings fell out of building it: **H-21** and
**H-22**.

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
| `/redirect/{code}` | `301` `302` `303` `307` — ⬜ `308` blocked on **H-1** |
| `/status/{code}` | arbitrary status codes, for the third-party drivers |

Also: `--fast-timeouts` shortens the read deadlines from 30 s to 3 s so the
timeout checks resolve quickly; the runner passes it.

⬜ **`/ws` on the main port is blocked on H-16** — the general HTTP server and
the WebSocket server are separate listeners in Hermod today, so the demo exposes
WebSocket on `:8081`. Once H-16 lands, `/ws` moves onto `:8080`.

## ✅ A2 · Raw-wire harnesses

**199/199 checks pass over both transports** — `tests/run-tests.sh`, ~97 s
cleartext, ~300 s over TLS. See [`tests/README.md`](tests/README.md) for the
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

## ⬜ A3 · curl matrix · P1 · ~1 d

curl is the closest thing HTTP/1.1 has to a reference client, and the installed
build (8.21, no HTTP/2) is ideal: it cannot silently upgrade.

- `tests/curl.ps1` + `tests/curl.sh` — a matrix over `--http1.0` / `--http1.1`,
  `-I` (HEAD), `-X OPTIONS`, `--data` / `-T` (chunked upload), `-H 'Expect:'`,
  ranges, `--cookie`/`--cookie-jar`, `-u` (Basic), `--anyauth`, `--compressed`
  (must degrade cleanly given **H-2**), `--raw`, `--keepalive-time`, several
  requests on one connection, `-k` against `:8443`
- assertions on both the status/body **and** the `-v` wire trace
- `docs/TestingAgainst_curl.md`

## ⬜ A4 · Autobahn TestSuite · P1 · ~1–2 d

Hermod's WebSocket README already claims 296 + 242 + 126 + 126 cases with 0
failures. Those numbers are currently unreproducible from a clean checkout —
this repository is where they become a command.

- `tests/autobahn-server/` — echo server on `WebSocketServer` for `fuzzingclient`
- `tests/autobahn-client/` — client driver on `WebSocketClient` against `fuzzingserver`
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

## ⬜ A7 · Non-.NET reference peers · P2 · ~2 d

Every current interop test is .NET-vs-.NET. Independent implementations catch
shared assumptions that two .NET stacks cannot.

| Peer | As client | As server |
|---|---|---|
| Go `net/http` | ✓ | ✓ |
| Python `httpx` / `aiohttp` | ✓ | ✓ |
| Rust `hyper` | ✓ | ✓ |
| Java `HttpClient` / OkHttp | ✓ | — |
| `wget`, `httpie`, `aria2c` (ranges) | ✓ | — |

## ⬜ A8 · Browser interop · P2 · ~1–2 d

- `tools/browser-interop.ps1` (model: the HTTP/3 repo's script) driving
  Playwright over Chromium, Firefox and WebKit
- `EventSource` against `/events`, `WebSocket` against `/ws`, CORS preflight,
  chunked rendering, connection reuse, `fetch()` with ranges
- the practical acceptance test — a browser is the least forgiving consumer of
  SSE and WebSocket in daily use

## ⬜ A9 · Benchmarks · P3 · ~1 d

`tests/h1bench`, model: `h2bench`. Small `GET` at 1/8/64 concurrent with latency
percentiles, large download/upload, chunked throughput, parser microbenchmarks,
allocation per request, plus **Kestrel as a control** on the same loopback — an
absolute number without a control is how "slower than expected" gets mistaken
for "slow". Additionally `h2load --h1`, `bombardier`, `oha` as external load
generators (they also stress keep-alive reuse and connection-table cleanup).

## ⬜ A10 · Parser fuzzing · P3 · ~2 d

SharpFuzz + AFL++ against the request-parsing entry point, seeded from the
`h1syntax`/`h1framing` corpora. Target: no unhandled exception, no hang, no
connection-state leak on any input.

## ⬜ A11 · CI · P2 · ~0.5 d

`.github/workflows/ci.yml` (build + Hermod HTTP/WS suites + `run-tests.ps1` +
curl matrix) and `nightly.yml` (Autobahn + proxies + smuggling + browsers) —
model: the HTTP/3 repo, which already has both.

---

# Track B — gaps in Hermod itself

Each of these is an upstream change under `libs/Hermod/`, shipped via the
workflow above: branch → fix + regression test → PR → merge → submodule pointer
bump here.

Two of them may end as "documented as deliberately out of scope" rather than
implemented — **H-9** (`TRACE` has a real XST security history) and the
never-standardized parts of **H-5**. Everything else is a fix.

| | # | Gap | Spec | P | Effort | Note |
|---|---|---|---|---|---|---|
| ⬜ | **H-1** | Missing status codes: `103` `308` `421` `451` `511` `208` `508`; **`425` is defined but named `NoCode`** | RFC 8297, 9110 §15.4.9/§15.5.20, 7725, 6585, 8470, 5842 | P1 | XS | `308` and the `425` misnomer are outright bugs. Pure data addition |
| ⬜ | **H-2** | No content coding for HTTP/1 bodies — neither client nor server compresses or decompresses | RFC 9110 §8.4, 1952, 7932, 8878 | P1 | S | **`HTTP2/Core/HTTPContentCoding.cs` already implements `br`/`gzip`/`deflate` with a zlib/raw-deflate sniffer.** Lift it into a shared location and wire it into `AHTTPPDU`. Biggest impact per line of code in this list |
| ⬜ | **H-3** | `HTTPDigestAuthentication` is *not* RFC 7616 — it is `Digest base64(user):base64(secret)`, no realm/nonce/qop/nc/cnonce/response | RFC 7616 | P1 | M | Either implement RFC 7616 properly, or rename to something non-colliding. The current name will mislead every reader; curl's `--digest` will not interoperate |
| ⬜ | **H-4** | `Forwarded` not implemented (only `X-Forwarded-For`) | RFC 7239 | P2 | S | Already marked `//ToDo` at `HTTP1/Request/HTTPRequest.cs:1125` |
| ⬜ | **H-5** | No RFC 9111 cache (client- or server-side) | RFC 9111, 5861, 8246 | P2 | L | `HTTP2/Core/HTTPCache.cs` + `HTTPCacheControl`/`HTTPCacheDecision`/`HTTPStoredResponse` exist. Same lift as H-2, much larger. Verifiable against `cache-tests.fyi` |
| ⬜ | **H-6** | No Structured Field Values parser/serializer | RFC 9651 | P2 | M | Prerequisite for most modern fields (9530, 9211, 9213, 9218, Client Hints) |
| ⬜ | **H-7** | No `Alt-Svc` | RFC 7838 | P2 | S | The natural bridge from this stack to the h2/h3 stacks — and directly testable with curl's `--alt-svc` |
| ⬜ | **H-8** | ~70 source comments still cite RFC 2616 / RFC 7230-series | — | P2 | S | Mechanical; the HTTP1 README already flags it |
| ⬜ | **H-9** | No server-side `TRACE` | RFC 9110 §9.3.8 | P3 | XS | Token + client exist; the server never handles it. Note the XST security history — "deliberately not implemented" is a valid answer, but then document it |
| ⬜ | **H-10** | No automatic CORS preflight | WHATWG Fetch | P2 | M | `Access-Control-*` are settable, but `OPTIONS` preflight is not answered automatically. Browser-visible (**A8**) |
| ⬜ | **H-11** | Obsolete HTTP-date formats (RFC 850, asctime) not parsed | RFC 9110 §5.6.7 | P3 | XS | Recipients **MUST** accept all three |
| ⬜ | **H-12** | No HSTS (`Strict-Transport-Security`) | RFC 6797 | P3 | XS | Header emission only; policy is the application's |
| ⬜ | **H-13** | `Content-MD5` typed (obsolete), RFC 9530 digest fields missing | RFC 9530 | P3 | S | Depends on **H-6** |
| ⬜ | **H-14** | No `Link` header | RFC 8288 | P3 | S | |
| ⬜ | **H-15** | No Problem Details | RFC 9457 | P3 | S | Relevant for the `HTTPAPI` layer |
| ⬜ | **H-16** | General HTTP server has no `Upgrade` dispatch — WebSocket is a separate listener | RFC 9110 §7.8, 9112 §9.6 | P2 | M | Blocks a `/ws` route on the main demo port (**A1**), and it is how every real deployment does it |
| ⬜ | **H-17** | Server does not negotiate ALPN `http/1.1` | RFC 7301 | P3 | XS | Client side is configurable; the server never offers it |
| ⬜ | **H-18** | `HTTP1/Server/URLMapping_old/` alongside `URLMapping/` — two routing generations in the tree | — | P3 | S | ~4 000 lines of probable dead code. Clarify before the harnesses depend on either |
| ⬜ | **H-19** | No RFC 8187 parameter encoding / RFC 6266 `filename*` | RFC 8187, 6266 | P3 | S | |
| ⬜ | **H-20** | IPv6 zone identifiers in URIs | RFC 6874 | P3 | XS | |
| ⬜ | **H-21** | `Accept-Ranges` is modeled as a **request** field, but RFC 9110 §14.3 defines it as a *response* field — `HTTPResponse.Builder` has no property for it | RFC 9110 §14.3 | P2 | XS | Found while building A1: the demo has to fall back to the generic `SetHeaderField("Accept-Ranges", …)`. Wrong side of the request/response split |
| ⬜ | **H-22** | A chunked response silently produces an **empty body** unless `ContentStream` is a `ChunkedTransferEncodingStream` — setting `TransferEncoding = "chunked"` + `ChunkWorker` alone emits correct headers and nothing else, with no error | — | P2 | S | Found while building A1. The server dispatches the worker on the stream type, not the header field. Either wire the two together or fail loudly when they disagree; a silent empty body is the worst of the three options |
| ⬜ | **H-23** | `HEAD` is not derived from `GET` — an unregistered `HEAD` is answered `405`, and the `Allow` field it returns omits `HEAD` as well | RFC 9110 §9.3.2 | P2 | S | Found while building A2. "A server SHOULD support HEAD for any resource it supports GET for" — and the `405` naming only `GET` misleads the very client that consulted `Allow` to find out. Every GET route currently has to register `HEAD` by hand |

---

# Suggested sequence

```
✅A0 ──▶ ✅A1 ──┬──▶ ✅A2 ──▶ ⬜A3 ──▶ ⬜A11 (CI: build + harnesses + curl)
                │
                ├──▶ ⬜A4  (Autobahn — independent of A2, needs the WS decision)
                │
                └──▶ ⬜A5, ⬜A6, ⬜A7, ⬜A8  (external suites)

Track B in parallel: ⬜H-1 and ⬜H-2 first (small, high leverage),
⬜H-3 and ⬜H-16 as decisions before A3/A4 depend on them.
```

**First milestone:** 🔶 A0 ✅ + A1 ✅ + A2 ✅ + A3 ⬜ + H-1 ⬜ + H-2 ⬜ — a
runnable demo host, the raw-wire gate, the curl matrix, and the two Hermod fixes
that are cheap and obviously right. Three of six done; the repository already
states a number (**199/199**) the way the HTTP/2 repo does, but it is still a
number about code written here.

**Second milestone:** ⬜ A4 + A11 + A5 — Autobahn reproducible from a clean
checkout, CI green, proxy interop.

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
  tests live with the stack in `HermodTests/` (namespace `Tests.HTTP.*`, 440
  today); this repository holds only the demo-driven raw-wire harnesses, the
  third-party suite drivers and the tooling. A2 produces harnesses, not NUnit
  fixtures — a Track B fix's regression test goes upstream with the fix.
- **Remotes.** `origin` → GitHub, plus `git1`/`git2` on graphdefined.com, as in
  the other Vanaheimr repositories. Default branch `master`.

# Open questions

1. **Lift or duplicate?** H-2 and H-5 exist in usable form in `Hermod/HTTP2/Core/`.
   Move them to a version-neutral namespace shared by HTTP/1, /2 and /3, or
   reimplement per version? The shared route is better but touches the HTTP/2
   stack, which is currently at 146/146 h2spec and 517/517 Autobahn.
