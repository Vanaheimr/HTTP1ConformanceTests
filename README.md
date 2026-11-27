# HTTP/1.1 Conformance & Interoperability Test Suite

The **conformance / interop test drivers** and the runnable **demo host** for the
HTTP/1.0 + HTTP/1.1 stack that lives in the Vanaheimr **Hermod** library — a
from-scratch implementation built directly on `TcpClient`/`SslStream` (no
Kestrel, no `System.Net.Http` stack, everything hand-rolled). Hermod is pulled in
here as a git submodule under `libs/`.

This is the HTTP/1.1 sibling of
[HTTP2ConformanceTests](https://github.com/Vanaheimr/HTTP2ConformanceTests) and
[HTTP3ConformanceTests](https://github.com/Vanaheimr/HTTP3ConformanceTests), and
it follows the same idea: the protocol-level unit and integration tests live
next to the stack in `HermodTests/`, while **this** repository adds the runnable
host, the raw-wire harnesses that no high-level client can produce, and the
drivers for **third-party conformance suites** (curl, Autobahn, request-smuggling
differentials, real reverse proxies, real browsers).

> ⚠️ **Reference implementation** — built for learning and for owning the wire
> protocol end-to-end, not as a drop-in production web server.

## Status

The **demo host** and the **raw-wire harnesses** are in place, CI gates every push
on Windows and Debian 13, and the first third-party conformance suite — Autobahn —
runs nightly and gates on its result (see [`PLAN.md`](PLAN.md) for the rest).

```bash
tests/run-tests.sh        # 337 checks, ~135 s
```

| | |
|---|---|
| `tests/` raw-wire harnesses | **214/214** — syntax, framing, connection management, RFC 9110 semantics, SSE, smuggling/hardening |
| `tests/curl-matrix.sh` | **99/99** (100 on the CI Debian leg, 105 over TLS — see the conditional checks) — the first checks here made by a client nobody in this repository wrote |
| both, over cleartext *and* TLS | **337** cleartext and **343** over TLS, the extra six being the ALPN check that only a TLS handshake can carry, curl's alt-svc cache, which curl fills only over TLS, and four misdirected requests, which only a certificate can make misdirected — and **539/539** with `--wsl`, which adds a second curl build, the foreign peers and the smuggling differential |
| `Demo/` host | `:8080` cleartext, `:8443` TLS, `:8081` WebSocket — 24 routes |
| `tests/autobahn.sh` (nightly, gated) | **481/517** Autobahn cases — the canonical RFC 6455 suite against `:8081`; the 36 open ones are `server_max_window_bits=9`, which RFC 7692 says to decline |
| `tests/interop.sh` | **65/65** + 3 skips — Go, Java, Node, Python and wget against our server, and our client against Go's and Node's servers. The client direction had no independent witness before this |
| `tests/h1bench` | not a gate — throughput, latency percentiles and allocation, with Kestrel as a control on the same loopback |
| `tests/h1fuzz` | ~1–2 M mutated inputs per target per minute against three parsers. Fixed seed in the gate, exploring nightly. **No known findings** — the one it produced on its first run, [H-28](PLAN.md), was fixed on 2026-10-03 and its line deleted, which is how this harness verifies a fix |
| `tests/h1desync` | **24/24** — 38 ambiguously framed messages; the 24 RFC 9112 states a rule for are asserted, the 14 it leaves open are observed |
| `tests/smuggle.sh` | **38/38** over Hermod, Go `net/http` and Node `node:http`. 28 rows agree; the 10 that do not are [written up](docs/TestingAgainst_Smuggling.md) and pinned in `tests/smuggle-known.txt` |
| `tests/smuggler.sh` | **134/134** Transfer-Encoding obfuscations from [smuggler](https://github.com/defparam/smuggler), nothing found; [h2csmuggler](https://github.com/BishopFox/h2csmuggler) finds no h2c surface, which is now a pinned regression |
| `tests/http-garden/` | Hermod as a target in [the HTTP Garden](https://github.com/narfindustries/http-garden), which compares parse trees across 45 implementations. Contract-verified on every run; the full sweep is compiler-hours and is run by hand |
| `tests/proxy.sh` | **32/32** — the demo behind nginx, HAProxy, Caddy, Apache and Envoy. 73 differences from the direct run, each recorded by name; no chain poisons a connection, though [the detector is calibrated-unfired](docs/TestingAgainst_Proxies.md) |
| `tests/browser.sh` | **27/27** — Chromium, Firefox and WebKit through Playwright. The three failures are one finding, [H-10](docs/TestingAgainst_Browsers.md), on each engine |
| `tests/cache-tests.sh` (nightly, gated) | **480 as recorded** — Hermod's client cache through [cache-tests](https://github.com/http-tests/cache-tests), the suite behind cache-tests.fyi: 285 pass and 94 yes, 5 required tests fail, against 13 to 37 for the proxies and the CDN the suite publishes. Every other result is recorded with its reason, and none of them is an open finding: the four it [found in the cache](docs/TestingAgainst_CacheTests.md), H-44 to H-47, are fixed, H-44 and H-46 on 2026-11-22, H-47 and H-45 on 2026-11-24 |

On top of that, the coverage inside Hermod itself:

| Where | Measured by | Count | What |
|---|---|---:|---|
| everything HTTP/1.x | `FullyQualifiedName~Hermod.Tests.HTTP.` | **1836** | the two rows below plus URL/query/hostname/method models, the `HTTPAPI` layer and the `HTTPTestServer` |
| ↳ the protocol regression selection | the fifty-name filter printed in [`HTTP1/README.md`](libs/Hermod/Hermod/HTTP1/README.md) | **1147** | every fixture whose tests drive the HTTP/1.x client or server over the wire, or test a field or coding the stack writes or reads: framing and connections end to end, methods and status codes, TLS and origin, content codings in both roles, the typed fields, Digest authentication. The list there is grouped by concern and says what it leaves out and why — the URL and query models, the `HTTPAPI` layer, the static-file handlers, the `HTTPTestServer`, SSE and WebSockets, and H-27's `HTTPClientLazyDNSClientTests`, which measures what a client costs rather than what it sends. It was eleven names and **381** until [Hermod#221](https://github.com/Vanaheimr/Hermod/pull/221) (2026-11-03). A finding whose tests went into a file the filter named moved it, as H-10's three and H-28's two did; one whose tests came in a fixture of their own — the four of 2026-09-26, H-23's, H-11's — landed in the row above and not in this one, because joining the selection took a name in that filter, and none was added after 2026-09-24. On one tree the eleven names select 381 and the thirty-eight 782, and the 401 between them are exactly the tests of the 27 fixtures added: no new name reaches into a class it was not meant for. The thirty-ninth, `NagleTests`, came with [Hermod#256](https://github.com/Vanaheimr/Hermod/pull/256) (2026-11-19) and adds its own four. The fortieth and forty-first, `HTTPClientRedirectTests` and `HTTPClientChallengeTests`, came with H-38's first half ([Hermod#265](https://github.com/Vanaheimr/Hermod/pull/265), 2026-11-20) and add 25, and H-41 ([Hermod#266](https://github.com/Vanaheimr/Hermod/pull/266)) adds 8 to a fixture already named. The forty-second, `HTTPClientDownloadTests`, came with H-38's second half ([Hermod#268](https://github.com/Vanaheimr/Hermod/pull/268), 2026-11-20) and adds 12, and the 2 more are in `ChunkedFramingTests`, already named. The forty-third, `HTTPResourceSemanticsTests`, came with H-37 ([Hermod#275](https://github.com/Vanaheimr/Hermod/pull/275), 2026-11-20) and adds 47. The forty-fourth, `AbsoluteFormTests`, came with H-42 ([Hermod#278](https://github.com/Vanaheimr/Hermod/pull/278), 2026-11-21) and adds 19, and the 5 more are in `MisdirectedRequestTests` and `HTTPServerSocketRegressionTests`, already named. The forty-fifth, `StaticFileValidatorsTests`, came with H-43 ([Hermod#283](https://github.com/Vanaheimr/Hermod/pull/283), 2026-11-21) and adds 16, and the forty-sixth and forty-seventh, `HTTPCacheTests` and `HTTPClientCacheTests`, came with H-5 ([Hermod#286](https://github.com/Vanaheimr/Hermod/pull/286), 2026-11-21) and add 43. H-44 ([Hermod#291](https://github.com/Vanaheimr/Hermod/pull/291), 2026-11-22) adds one to each of those two, and H-46 ([Hermod#292](https://github.com/Vanaheimr/Hermod/pull/292), 2026-11-22) two to the first and one to the second, and H-47 ([Hermod#296](https://github.com/Vanaheimr/Hermod/pull/296), 2026-11-24) three to each, and H-45 ([Hermod#300](https://github.com/Vanaheimr/Hermod/pull/300), 2026-11-24) three to each again, and H-48 ([Hermod#306](https://github.com/Vanaheimr/Hermod/pull/306), 2026-11-24) five to `HTTPClientProtocolRegressionTests`, and H-49 ([Hermod#310](https://github.com/Vanaheimr/Hermod/pull/310), 2026-11-25) five more to it and one each to `HTTPStatusCodeTests` and `HTTPCacheTests`, and H-50 ([Hermod#313](https://github.com/Vanaheimr/Hermod/pull/313), 2026-11-25) nine more to `HTTPClientProtocolRegressionTests`. The forty-eighth, `ClientCertificateFieldsTests`, came with RFC 9440 ([Hermod#341](https://github.com/Vanaheimr/Hermod/pull/341), 2026-11-27) and adds 35, the forty-ninth, `ContentTypeFieldTests`, with H-51 ([Hermod#342](https://github.com/Vanaheimr/Hermod/pull/342), 2026-11-27) and adds 38, and the fiftieth, `MultipartFormDataTests`, with RFC 7578 ([Hermod#345](https://github.com/Vanaheimr/Hermod/pull/345), 2026-11-27) and adds 48 |
| ↳ WebSockets | `FullyQualifiedName~Hermod.Tests.HTTP.WebSockets` | **249** | RFC 6455 framing, handshake hardening, subprotocols, backpressure, reconnect, `permessage-deflate`, the close handshake waiting for the peer's close, and the same client and server over HTTP/2 (RFC 8441) and HTTP/3 (RFC 9220), where since `81ab1c86` a replaced certificate is shown to the next client without a restart, and since `0800bcc7` a UDP port taken on one loopback address is a taken port, and since `852ec2d9` ([Hermod#319](https://github.com/Vanaheimr/Hermod/pull/319)) a slow network is simulated at the pace asked for on Windows too |
| what CI actually gates on | `…Tests.HTTP.` **+** `…Tests.HTTPS.` | **1837** | the row above plus `Tests.HTTPS.`, which is one single test — that one test is the whole difference between 1836 and 1837, and it is why this table and a CI log used to disagree by one for no visible reason. What the filter does *not* reach is `Tests.TCP` and `Tests.Warden`, the layer `AHTTPServer` is built on |
| Autobahn vs. the **server** (demo host `:8081`) | `tests/autobahn.sh` | **481** / 517 + 36 declined | RFC 6455 + RFC 7692, nightly and gated on a floor — see [`tests/TestingAgainst_Autobahn.md`](tests/TestingAgainst_Autobahn.md) |
| Autobahn vs. the **client** (`fuzzingserver`) | `tests/autobahn-client.sh` | **445** / 517 + 72 declined | our `WebSocketClient` driven through the suite, nightly and gated — 0 hard failures |

Every count above is a **run** count, re-measured on 2026-11-27 against Hermod
`e73d854c` and Styx `a6b9f9fb` by running the filter in its own row — which is now printed, because
every time a figure here drifted it was a figure whose filter nobody could check. The two Autobahn
rows were measured in a Linux-built clone inside WSL, the configuration the nightly runs them in.

`--list-tests` and `dotnet test` disagree on this checkout, and by a stable
amount: three, whatever the totals are — re-measured 2026-09-24 at 564 listed
against 561 run for `Tests.HTTP.`, and 332 against 329 for the regression
selection. Nothing is skipped in either run, so the listing counts
three entries that never become a test case — and the same three both times,
since they sit inside the regression files. `Tests.HTTP.WebSockets` agrees at 49.
A number that is not the number the gate produces is not the number to publish.

**Why the regression selection moved from 299 to 329**, since a reader is owed
more than "it went up": **five of those thirty are not new tests.** The filter
itself gained three files. On 2026-09-23, `HTTPStatusCodeTests` (7 tests, all
new) and `ContentEncodingHeaderTests` (13, of which 8 are new and 5 existed all
along but had never been in the selection); on 2026-09-24,
`HTTPDigestAuthenticationTests` (10, all new). So 25 tests were written and 5
were merely included. Run the five-file filter this figure used to name and it
still answers 299, exactly as before: the selection grew, the older tests did
not multiply.

That filter lives in [`libs/Hermod/Hermod/HTTP1/README.md`](libs/Hermod/Hermod/HTTP1/README.md),
which recorded **295** on 2026-07-18 and now records 329. Those two are not
comparable either: the 295 was a list count.

## Requirements

- .NET 11 SDK (`net11.0` target)
- `bash` for the runner scripts — on Windows, the Git Bash that ships with Git
  for Windows. There is no PowerShell runner in this repository and there never
  was; see A11 in [`PLAN.md`](PLAN.md) for why the two sibling repositories
  removed theirs
- `curl` — any recent build; see the note on the two builds below
- **WSL/Debian with Docker** for the container-based suites (Autobahn, the proxy
  matrix, http-garden). Not Docker Desktop
- optional: Python 3 for the smuggling drivers, Node for the browser drivers

## Get the sources

The HTTP/1.x stack lives in the Vanaheimr **Hermod** library, which — together
with **Styx** — is pulled in as a git submodule under `libs/`. Clone **with
submodules**, otherwise `libs/Hermod` and `libs/Styx` are empty and nothing
builds:

```bash
git clone --recurse-submodules https://github.com/Vanaheimr/HTTP1ConformanceTests.git
```

Already cloned without `--recurse-submodules`?

```bash
git submodule update --init --recursive
```

## Build & test

```bash
dotnet build HTTP1.slnx
```

The Hermod-side regression suites (these work today):

```powershell
dotnet test libs/Hermod/HermodTests/HermodTests.csproj --filter "FullyQualifiedName~HTTPClientTests|FullyQualifiedName~HTTPServerSocketRegressionTests|FullyQualifiedName~HTTPClientProtocolRegressionTests|FullyQualifiedName~HTTP11AuditRegressionTests|FullyQualifiedName~HTTP_TCPClientTests|FullyQualifiedName~ChunkedFramingTests|FullyQualifiedName~TransferCodingTests|FullyQualifiedName~BodilessResponseTests|FullyQualifiedName~HTTPLineEndingTests|FullyQualifiedName~HTTPClientPoolTests|FullyQualifiedName~NagleTests|FullyQualifiedName~HTTPStatusCodeTests|FullyQualifiedName~HTTPHeadDerivedFromGetTests|FullyQualifiedName~HTTPServerTraceTests|FullyQualifiedName~HTTPCORSPreflightTests|FullyQualifiedName~EarlyHintsTests|FullyQualifiedName~ProblemDetailsTests|FullyQualifiedName~HTTPServerListenerMatrixTests|FullyQualifiedName~HTTPServerALPNTests|FullyQualifiedName~HTTPServerAllowedTLSProtocolsTests|FullyQualifiedName~HTTPSClientTLSByDefaultTests|FullyQualifiedName~HTTPSClientHandshakeTimeoutTests|FullyQualifiedName~AcceptedConnectionTests|FullyQualifiedName~MisdirectedRequestTests|FullyQualifiedName~ContentEncodingHeaderTests|FullyQualifiedName~ContentCodingStreamTests|FullyQualifiedName~ClientContentDecodingTests|FullyQualifiedName~ServerContentCompressionTests|FullyQualifiedName~ResponseHeaderFieldTests|FullyQualifiedName~StructuredFieldsTests|FullyQualifiedName~HTTPDateTests|FullyQualifiedName~HTTPCookiesTests|FullyQualifiedName~ForwardedFieldTests|FullyQualifiedName~AlternativeServicesTests|FullyQualifiedName~DigestFieldsTests|FullyQualifiedName~LinkFieldTests|FullyQualifiedName~ContentDispositionTests|FullyQualifiedName~StrictTransportSecurityTests|FullyQualifiedName~HTTPDigestAuthenticationTests|FullyQualifiedName~HTTPClientRedirectTests|FullyQualifiedName~HTTPClientChallengeTests|FullyQualifiedName~HTTPClientDownloadTests|FullyQualifiedName~HTTPResourceSemanticsTests|FullyQualifiedName~AbsoluteFormTests|FullyQualifiedName~StaticFileValidatorsTests|FullyQualifiedName~HTTPCacheTests|FullyQualifiedName~HTTPClientCacheTests"
```

```powershell
# HTTP/1.1 WebSockets only — a bare "~WebSocket" filter also picks up the
# HTTP/2 (RFC 8441) and HTTP/3 (RFC 9220) WebSocket tests:
dotnet test libs/Hermod/HermodTests/HermodTests.csproj --filter "FullyQualifiedName~Tests.HTTP.WebSockets"
```

---

# What Hermod implements

The stack's own reference — API, limits, exclusions — lives next to the code:

- [`libs/Hermod/Hermod/HTTP1/README.md`](libs/Hermod/Hermod/HTTP1/README.md) — HTTP/1.0 + HTTP/1.1
- [`libs/Hermod/Hermod/HTTP1/WebSocket/README.md`](libs/Hermod/Hermod/HTTP1/WebSocket/README.md) — RFC 6455 + RFC 7692

Roughly 55 000 lines under `Hermod/HTTP1/` (100 files) plus the shared HTTP
model under `Hermod/HTTP/` (methods, status codes, URL/path/query, content
types, cookies, authentication, the HTTP API layer).

| Subsystem | Path | State |
|---|---|---|
| Message model (PDU, builders, header fields) | `HTTP1/AHTTPPDU.cs`, `HTTP1/{Request,Response}/` | complete, typed header-field model |
| Server | `HTTP1/Server/` | `AHTTPServer`, `HTTPServer`, `HTTPTestServer`, `HTTPConnection`, URL routing, pipelines |
| Client | `HTTP1/Client/` | `HTTPClient`, `HTTPSClient`, `HTTPClientPool`, logging, SOAP |
| Chunked transfer coding | `HTTP1/Server/ChunkedTransferEncoding/` | send + receive, extensions, trailers, metadata limits |
| Server-Sent Events | `HTTP1/ServerSentEvents/` | server + client, history/replay, `Last-Event-ID`, retry |
| WebSocket | `HTTP1/WebSocket/` | server + client, RFC 6455 + RFC 7692, Autobahn-verified |
| Auth values | `HTTP/Authentication/` | Basic, Bearer, Token, TOTP, `WWW-Authenticate` builder |

## The three tiers of "supported"

Hermod deliberately separates **wire protocol** from **application semantics**.
The matrix below uses these markers:

| | Meaning |
|---|---|
| ✅ | implemented **and** covered by automated tests in `HermodTests/` |
| 🟢 | implemented, tested only indirectly / manually |
| 🟡 | **model only** — the header/method/status is typed, parsed and serialized, but Hermod applies no automatic policy; the handler decides |
| 🔵 | **by design the handler's job** — Hermod is an origin server, not a cache/proxy/framework |
| ❌ | not implemented (and would be a real gap) |
| ⬜ | out of scope for an HTTP/1.x origin client/server |

The distinction matters: "Hermod has a `Range` header field" is *not* the same
claim as "Hermod serves `206 Partial Content`". The former is 🟡, the latter is
🔵 — the handler builds the partial representation, or since H-37 leaves it to
`AddResource`, which is opt-in for exactly that reason.

---

# RFC & specification matrix

## Core — the current HTTP standard (2022, STD 97/98/99)

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110) | HTTP Semantics (STD 97) | ✅ core: methods, status codes, `Host`, connection handling, `Expect: 100-continue`, bodyless responses, representation metadata, extension fields. Resource semantics 🔵 |
| [RFC 9111](https://www.rfc-editor.org/rfc/rfc9111) | HTTP Caching (STD 98) | ✅ the client's, opt-in since H-5 (2026-11-21, [Hermod#286](https://github.com/Vanaheimr/Hermod/pull/286)): `AHTTPClient.Cache`, private by default, with the logic HTTP/2 uses, both moved to `HTTP/Caching/`. 🔵 the server's: Hermod is an origin, and a cache is an intermediary's or a client's |
| [RFC 9112](https://www.rfc-editor.org/rfc/rfc9112) | HTTP/1.1 message syntax & routing (STD 99) | ✅ start lines, header parsing, framing, persistence, pipelining, chunked, trailers, malformed-message rejection |
| [RFC 9113](https://www.rfc-editor.org/rfc/rfc9113) | HTTP/2 | ⬜ separate stack — see HTTP2ConformanceTests. Note: `Upgrade: h2c` was **removed** by 9113 and is correctly absent |
| [RFC 9114](https://www.rfc-editor.org/rfc/rfc9114) | HTTP/3 | ⬜ separate stack — see HTTP3ConformanceTests |
| [RFC 9205](https://www.rfc-editor.org/rfc/rfc9205) | Building Protocols with HTTP (BCP 56bis) | 🔵 guidance for applications on top |

## Historic & obsoleted — still required for interop

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| [RFC 1945](https://www.rfc-editor.org/rfc/rfc1945) | HTTP/1.0 | ✅ requests/responses, `Content-Length`, close-delimited bodies, default close, negotiated keep-alive, chunked rejection, `HTTP/1.0` status line |
| [RFC 2616](https://www.rfc-editor.org/rfc/rfc2616) | HTTP/1.1 (obsolete) | 🟢 superseded by 9110/9112. Of the 69 source comments that still cited it, 62 now cite each field's current document and section (H-8, 2026-09-26); six remain on purpose, RFC 4918 quoting RFC 2616 in text Hermod quotes in turn, each block with a remark naming the current reference |
| [RFC 7230](https://www.rfc-editor.org/rfc/rfc7230)–[7235](https://www.rfc-editor.org/rfc/rfc7235) | HTTP/1.1 series (obsolete) | 🟢 superseded by 9110–9112 |
| [RFC 2145](https://www.rfc-editor.org/rfc/rfc2145) | Use and interpretation of HTTP version numbers | ✅ non-canonical versions (`HTTP/1`, `HTTP/01.1`, wrong case) are rejected |
| "HTTP/1.0 Keep-Alive" (never standardized) | `Connection: Keep-Alive` + `Keep-Alive` field | ✅ honoured only when negotiated in both directions; `close` wins |

## Message syntax, framing & connection management

| Spec / section | Topic | Hermod HTTP/1.x |
|---|---|---|
| RFC 9112 §2–3 | Message format, request line, request-target forms | ✅ origin-form ✅, asterisk-form (`OPTIONS *`) ✅, absolute-form ✅ since H-42 (2026-11-21, [Hermod#278](https://github.com/Vanaheimr/Hermod/pull/278)), as §3.2.2 requires: *"A server MUST accept the absolute-form"*. Its authority replaces `Host`, and an `https` target on cleartext is a `421` (RFC 9110 §7.4). It was refused with `400` and marked *by design* (not a proxy) until then; authority-form (`CONNECT`) → `501` |
| RFC 9110 §5 | Field syntax, obs-fold, whitespace before `:`, control chars | ✅ all rejected |
| RFC 9112 §6 | Message body length, the 7-step algorithm | ✅ incl. duplicate/conflicting `Content-Length`, `CL`+`TE` conflict, overflow/sign/non-decimal |
| RFC 9110 §8.6 | `Content-Length` on a request without content | ✅ since H-41 (2026-11-20): none on a `GET`, `HEAD`, `DELETE`, `CONNECT`, `OPTIONS` or `TRACE` without content, `0` on a `POST`, `PUT`, `PATCH` or `QUERY`. Every request without content said `0` before |
| RFC 9112 §7 | Transfer codings | ✅ `chunked` (both directions, both roles), chunk extensions, trailers, forbidden-trailer list. `gzip`/`deflate`/`compress` **transfer** codings ❌ |
| RFC 9112 §9 | Persistence, pipelining, `Connection` | ✅ persistent by default, pipelining in wire order, chunked request delimited before the next, invalid leading request closes the connection |
| RFC 9112 §11.2 | Message smuggling / response splitting | ✅ strict rejection of ambiguous framing, and verified differentially since 2026-09-27: `tests/h1desync` 24/24 against the requirements RFC 9112 states, three implementations compared in `tests/smuggle.sh`, two third-party probe suites, and Hermod added to the HTTP Garden. Two findings, **H-29** and **H-30**, both fixed the day they were found ([Hermod#54](https://github.com/Vanaheimr/Hermod/pull/54)) — see [`docs/TestingAgainst_Smuggling.md`](docs/TestingAgainst_Smuggling.md) |
| RFC 9110 §7.6.1 | Connection-specific (hop-by-hop) fields | 🟡 `Connection`, `Trailer`, `Upgrade`, `Via` typed |
| [RFC 7239](https://www.rfc-editor.org/rfc/rfc7239) | `Forwarded` HTTP extension | ✅ `ForwardedElement`/`ForwardedNode`, parsed and serialisable, preferred over the `X-Forwarded-For` family where both arrive (§7.4) and recorded beside the peer socket rather than instead of it (§8.1) — H-4, 2026-09-26 |
| [RFC 9440](https://www.rfc-editor.org/rfc/rfc9440) | `Client-Cert` / `Client-Cert-Chain` | ✅ since [Hermod#341](https://github.com/Vanaheimr/Hermod/pull/341) (2026-11-27), for both ends of a TLS-terminating reverse proxy. The proxy's `ForwardClientCertificate` removes whatever the client sent of either field and declares the certificate and chain the connection proved, or nothing; the server behind it reads `ForwardedClientCertificate` and `ForwardedClientCertificateChain`, a certificate only from exactly one DER-encoded certificate, and the chain over several field lines, which the parser refused until then. Both RFC 9440 examples to the octet. 🔵 Which proxy to believe is the application's (§4): the stack reads neither field itself. HTTP/2 and HTTP/3 handlers have the codec, `HTTPClientCertificateFields`, without the request accessors |
| [RFC 8586](https://www.rfc-editor.org/rfc/rfc8586) | `CDN-Loop` | ⬜ |
| [RFC 9209](https://www.rfc-editor.org/rfc/rfc9209) | `Proxy-Status` | ⬜ |

## URIs, dates, encodings & field-value syntax

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| [RFC 3986](https://www.rfc-editor.org/rfc/rfc3986) | URI Generic Syntax (STD 66) | ✅ `URL`, `HTTPPath`, `QueryString`, `URIScheme`, `URLHost`; fragments, bad percent-escapes, encoded separators, dot segments, repeated slashes and double-encoding all rejected |
| [RFC 6874](https://www.rfc-editor.org/rfc/rfc6874) | IPv6 zone identifiers in URIs | ✅ `IPv6Address.TryParseURIHost` reads `%25` as the zone delimiter, which the plain parser had read as part of the zone (`25en1`); `URL.HostHeader` drops the zone — H-20, 2026-10-04 |
| [RFC 5234](https://www.rfc-editor.org/rfc/rfc5234) / [RFC 7405](https://www.rfc-editor.org/rfc/rfc7405) | ABNF (STD 68) | ✅ used as the parsing reference |
| [RFC 3629](https://www.rfc-editor.org/rfc/rfc3629) | UTF-8 (STD 63) | ✅ incl. the incremental validator in the WebSocket path |
| RFC 9110 §5.6.7 + [RFC 1123](https://www.rfc-editor.org/rfc/rfc1123) | HTTP-date (IMF-fixdate) | ✅ `HTTPDate`: IMF-fixdate produced, all three formats parsed as recipients MUST, the RFC 850 two-digit year by §5.6.7's sliding window — H-11, 2026-10-03 |
| [RFC 9651](https://www.rfc-editor.org/rfc/rfc9651) | Structured Field Values (obsoletes 8941) | ✅ Lists, Dictionaries and Items over all eight bare types, §4.2 parsing and §4.1 serialization, strict rather than repairing — H-6, 2026-09-26. Its first users are RFC 9530's digest fields (H-13, 2026-10-29), which is how a Byte Sequence without its `=` padding was found to fail, where §4.2.7 says a parser SHOULD NOT; fixed with them |
| [RFC 8187](https://www.rfc-editor.org/rfc/rfc8187) | Charset/language in field parameters (`filename*`) | ✅ `HTTPExtendedValue`, since H-14: encodes in UTF-8, as a producer MUST, and decodes UTF-8 and ISO-8859-1, refusing any other charset rather than guessing at it, and a malformed escape or a character outside `attr-char`. `Link`'s `title*` uses it, and since H-19 `Content-Disposition`'s `filename*` does too |
| [RFC 6266](https://www.rfc-editor.org/rfc/rfc6266) | `Content-Disposition` in HTTP | ✅ `HTTPContentDisposition`, in `Hermod/HTTP/`, typed on both directions since H-19 (2026-10-30). Parsing follows Section 4.1, where a repeated parameter makes the value invalid, and `filename*` wins over `filename` (Section 4.3). A sender writes what Appendix D advises: `filename` alone where ASCII is enough, otherwise an ASCII fallback first and `filename*` in UTF-8 after it. `SafeFileName` is the name made fit to store under. The demo's `/download` sends `Grüße.txt`, and each half of Appendix D has a foreign witness: curl `-J`, which reads `filename=` alone, saves under `Gruesse.txt`, and wget and Go's `mime.ParseMediaType` read `filename*` and get `Grüße.txt` |
| [RFC 8288](https://www.rfc-editor.org/rfc/rfc8288) | Web Linking (`Link`) | ✅ typed on responses, `HTTPLinks` of `HTTPLink`, in `Hermod/HTTP/`. A link is built under Section 3's grammar and parsed by Appendix B, with one deviation written down: the comma between two link-values is consumed, as Section 3.5 needs and B.3 read literally does not do. Repeated field lines are one value. Which links to send is the handler's. H-14, [Hermod#182](https://github.com/Vanaheimr/Hermod/pull/182), verified 2026-10-29 by three checks in the curl matrix against the demo's `/pages/{n}`, which compare the field as curl received it, `HEAD` included, and pass unchanged through all five proxies |
| [RFC 6570](https://www.rfc-editor.org/rfc/rfc6570) | URI Templates | ⬜ routing uses its own path-parameter syntax |

## Methods

| Spec | Methods | Hermod HTTP/1.x |
|---|---|---|
| RFC 9110 §9 | `GET` `HEAD` `POST` `PUT` `DELETE` `OPTIONS` `TRACE` `CONNECT` | ✅ routed, incl. server-wide `OPTIONS *`, resource `OPTIONS`, `405` + `Allow`, and `HEAD` derived from `GET` with `Allow` naming it (H-23, 2026-10-03). `TRACE` → `501` by decision (H-9, 2026-10-04): what an echo may carry back is §9.3.8's judgement about sensitive fields, which only the application can make, and a registered `TRACE` handler still decides. `CONNECT` → `501` ⬜. The client sends a request that went out and got no answer again only if its method is idempotent or it carries an `Idempotency-Key`, and only once (§9.2.2): ✅ H-50, 2026-11-25. Before, it retried any method, a `POST` included, and retried a failed retry |
| [RFC 5789](https://www.rfc-editor.org/rfc/rfc5789) | `PATCH`, `Accept-Patch` | 🟡 method + `Accept-Patch` typed; patch-document semantics 🔵 |
| [RFC 10008](https://www.rfc-editor.org/rfc/rfc10008) | `QUERY` (safe, body-carrying read) | ✅ modeled safe+idempotent, end-to-end tested with fixed-length and chunked content, trailers, chunk extensions. `Accept-Query` 🟡, negotiation/caching 🔵 — except with `AddResource` and a query handler, where the result runs through the `GET` pipeline: preconditions, negotiation, `Content-Location`, and a `400` for content without a `Content-Type` (H-37) |
| [RFC 4918](https://www.rfc-editor.org/rfc/rfc4918) | WebDAV — `COPY` `LOCK` `MKCOL` `MOVE` `PROPFIND` `PROPPATCH` `UNLOCK` | 🟡 method tokens + `DAV`/`Depth`/`Destination`/`If`/`Lock-Token`/`Overwrite`/`Timeout` fields typed; no WebDAV resource implementation |
| [RFC 3253](https://www.rfc-editor.org/rfc/rfc3253) / [3744](https://www.rfc-editor.org/rfc/rfc3744) / [5323](https://www.rfc-editor.org/rfc/rfc5323) / [5842](https://www.rfc-editor.org/rfc/rfc5842) | WebDAV versioning/ACL/SEARCH/BIND | ⬜ |
| — | Hermod extension methods (`MIRROR` `SEARCH` `EXISTS` `COUNT` `FILTER` `STATUS` `AUTH` `SUBSCRIBE` …, ~30) | ✅ parsed, routed, with safe/idempotent metadata; `HTTPMethod.Register()` for application methods |

## Status codes

| Spec | Codes | Hermod |
|---|---|---|
| RFC 9110 §15 | 1xx/2xx/3xx/4xx/5xx core set | ✅ present and used |
| RFC 9110 §15.4 | Redirection, the client following it | ✅ since H-38 (2026-11-20), with `MaxRedirects`, off by default: `301`/`302`/`303` turn a `POST` into a `GET` without its content fields, `307`/`308` keep method and body, `300` and `304` are not followed. Same origin only, as on HTTP/2: a `Location` elsewhere comes back unfollowed. The targets followed are on `HTTPResponse.RedirectChain` |
| [RFC 8297](https://www.rfc-editor.org/rfc/rfc8297) | `103 Early Hints` | ✅ since H-40 (2026-10-30). A handler sends a `103` with `request.SendEarlyHints(links)`, at once and while it is still working, and any other interim response with `SendInformationalResponse`. None goes to HTTP/1.0 (RFC 9110 §15.2), and none after the handler has returned. The client keeps the interim responses it receives on `HTTPResponse.InformationalResponses`. Whether to send hints is the application's: RFC 8297's security considerations warn that an HTTP/1.1 client which takes a `1xx` for the final response misreads everything after it. Measured on the demo's `/early-hints`: curl, Go, Node, Java and wget read past the `103`, and Python's `http.client` is that client, python/cpython#72756, open since 2016. Behind the five proxies of A5, HAProxy and httpd pass the `103` on and keep it from an HTTP/1.0 client; nginx without its `early_hints` directive and Envoy without `proxy_100_continue` drop it; and Caddy hands it to an HTTP/1.0 client as well, which §15.2 forbids — that one is Go's `net/http`, which writes any `1xx` whatever the request's version, [golang/go#82743](https://github.com/golang/go/issues/82743) ([`docs/TestingAgainst_Proxies.md`](docs/TestingAgainst_Proxies.md)). Before H-40 the server could not send one and the client dropped what it received. Until H-40 was found this row read 🟡 "not something this matrix has verified", which was true and hid that there was nothing to verify |
| [RFC 7538](https://www.rfc-editor.org/rfc/rfc7538) → 9110 §15.4.9 | `308 Permanent Redirect` | ✅ defined since H-1 (2026-09-23) |
| RFC 9110 §15.5.20 | `421 Misdirected Request` | ✅ defined since H-1 (2026-09-23) |
| [RFC 8470](https://www.rfc-editor.org/rfc/rfc8470) | `425 Too Early` | ✅ `TooEarly` — named `NoCode`, a stale RFC 2518 draft name, until H-1 (2026-09-23) |
| [RFC 7725](https://www.rfc-editor.org/rfc/rfc7725) | `451 Unavailable For Legal Reasons` | ✅ defined since H-1 (2026-09-23) |
| [RFC 6585](https://www.rfc-editor.org/rfc/rfc6585) | `428` `429` `431` `511` | ✅ `428`/`429`/`431` (with a token-bucket rate limiter), and `511` since H-1 (2026-09-23) |
| RFC 4918 / [RFC 5842](https://www.rfc-editor.org/rfc/rfc5842) | `207` `422` `423` `424` `507` / `208` `508` | ✅ `207`/`422`/`423`/`424`/`507`, and `208`/`508` since H-1 (2026-09-23) |
| [RFC 3229](https://www.rfc-editor.org/rfc/rfc3229) | `226 IM Used` (delta encoding) | ⬜ delta encoding; the code itself is defined since H-1 |
| [RFC 2295](https://www.rfc-editor.org/rfc/rfc2295) | `506 Variant Also Negotiates` | 🟡 code present, TCN not implemented |
| [RFC 2324](https://www.rfc-editor.org/rfc/rfc2324) | `418 I'm a teapot` | ✅ obviously |

## Authentication

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| RFC 9110 §11 | Authentication framework, `WWW-Authenticate`, `Authorization`, `Proxy-*` | ✅ challenge/credential plumbing + `WWWAuthenticate` builder; policy 🔵. The client answers a `401` once with its `Credentials` since H-38 (2026-11-20): Digest, then Bearer, Token, Basic, nothing before it is asked, and never over an `Authorization` the caller set. Off by default |
| [RFC 7617](https://www.rfc-editor.org/rfc/rfc7617) | Basic | ✅ typed parse/serialize + end-to-end server tests (challenge, malformed, invalid, forbidden) |
| [RFC 6750](https://www.rfc-editor.org/rfc/rfc6750) | OAuth 2.0 Bearer | 🟡 typed parse/serialize; validation 🔵; the client answers a Bearer challenge with a token from its `Credentials` (H-38) |
| [RFC 7616](https://www.rfc-editor.org/rfc/rfc7616) | Digest (SHA-256) | ✅ `DigestAuthenticationScheme`, RFC 7616 with SHA-256, reachable from HTTP/1.x since the RFC 9110 §11 framework moved out of `HTTP2/` (H-3, 2026-09-24); `HTTPDigestAuthentication` parses and reserializes the credentials. curl 8.14 authenticates with SHA-256. The old custom `Digest base64(user):base64(secret)` scheme was dead code |
| [RFC 2617](https://www.rfc-editor.org/rfc/rfc2617) | Basic + Digest (obsolete) | ⬜ superseded |
| [RFC 9421](https://www.rfc-editor.org/rfc/rfc9421) | HTTP Message Signatures | ❌ (`Argus` has its own non-RFC signature verification) |
| [RFC 9530](https://www.rfc-editor.org/rfc/rfc9530) | Digest Fields (`Content-Digest`, `Repr-Digest`) | ✅ `Content-Digest`, `Repr-Digest` and both `Want-` fields typed in both directions, sha-256 and sha-512, with the grammar HTTP/2 uses — strict now, a field that does not parse being ignored whole as RFC 9651 §4.2 requires (H-13, 2026-10-29). Opt-in on both ends: the server digests the octets it sends (`AutomaticContentDigests`), the client asks for a digest and checks it before decoding (`VerifyDigests`). The compression filter computes a handler's digests again over what it compresses, which the curl matrix checks against coreutils' digest of the octets that arrived. `Repr-Digest` is the handler's to compute 🔵. The obsolete `Content-MD5` ([RFC 1864](https://www.rfc-editor.org/rfc/rfc1864)) stays typed, documented as removed by RFC 7231 |
| [RFC 8188](https://www.rfc-editor.org/rfc/rfc8188) | Encrypted Content-Encoding (`aes128gcm`) | ✅ since [Hermod#339](https://github.com/Vanaheimr/Hermod/pull/339) (2026-11-26), `HTTPEncryptedContentCoding`: encrypts and decrypts in records of a fixed size, the header's keyid naming the key, both examples of RFC 8188 §3 reproduced to the octet; every body §2 makes a decrypter fail on — cut off, tampered with, reordered, under another key, a delimiter where none belongs — is refused. 🔵 The key is the application's, as RFC 8188 leaves it, so the coding is not among those the stack undoes on its own, and a body in it is left as it came |
| — | Hermod extensions: `Token`, `TOTP`, `API-Key` | ✅ non-standard, documented as such |

## Cookies

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| [RFC 6265](https://www.rfc-editor.org/rfc/rfc6265) | HTTP State Management | 🟢 `HTTPCookie`/`HTTPCookies` with `Path`, `Secure`, `HttpOnly`, `SameSite`, `Expires`; `Cookie`/`Set-Cookie` typed. Attribute-parsing edge cases untested |
| 6265bis (draft) | `__Secure-`/`__Host-` prefixes, `SameSite` defaults, size limits | ❌ |

## Conditional requests, ranges & caching

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| RFC 9110 §8.8 + §13 | Validators (`ETag`, `Last-Modified`), preconditions (`If-Match`, `If-None-Match`, `If-Modified-Since`, `If-Unmodified-Since`, `If-Range`) | ✅ opt-in since H-37 (2026-11-20): a resource registered with `AddResource` has its preconditions evaluated by `HTTPResourceSemantics`, the layer HTTP/2 has always used — `412`, or `304` with the validators, in §13.2.2's order, and an `ETag` derived from the octets unless the representation states one. Static files since H-43 (2026-11-21, [Hermod#283](https://github.com/Vanaheimr/Hermod/pull/283)): `MapFileSystemFolder` and its kin send `Last-Modified` and an `ETag` and go through the same layer, as `SinglePageAppHandler` does. A handler registered with `AddHandler` sees the same fields typed and decides for itself, 🔵 as before. The demo's `/files/resource.txt` uses the layer, so `h1semantics` measures the library there |
| RFC 9110 §14 | Range requests, `206`, `Accept-Ranges`, `Content-Range`, `multipart/byteranges` | ✅ opt-in since H-37 (2026-11-20): `AddResource` answers one range with `206`, several with one `multipart/byteranges` body and none satisfiable with `416`, honours `If-Range`, and ignores `Range` on a `HEAD` (§14.2). Static files the same since H-43, up to `HTTPStandardHandlers.MaxBufferedFileSize` (16 MiB); a larger file keeps its validators and its `304` and `412` but has no ranges. `AddHandler` 🔵 as before. The client's side ✅ since H-38 (2026-11-20): `DownloadAsync` writes a body to its destination as it arrives and asks for the rest with `Range` and `If-Range`, guarded by a strong entity-tag or else `Last-Modified`. A `200`, or a `206` that starts anywhere else, starts the destination over, and a `416` for exactly what is already written finishes it |
| [RFC 8673](https://www.rfc-editor.org/rfc/rfc8673) | Range requests for unknown-length content | ⬜ |
| RFC 9111 | Caching | ✅ client, opt-in since H-5 (2026-11-21, [Hermod#286](https://github.com/Vanaheimr/Hermod/pull/286)): `AHTTPClient.Cache` takes an `HTTPClientCache`, which stores responses to `GET`, serves fresh ones with their `Age`, revalidates stale ones with `If-None-Match` / `If-Modified-Since`, keys variants by `Vary`, honours the request's directives and `Pragma: no-cache`, answers `only-if-cached` with `504` without reaching the origin, and invalidates a target after a successful unsafe request. `HTTPResponse.CacheStatus` says where an answer came from. A `200` to a `HEAD` updates the stored `GET` where its validators and `Content-Length` match, and leaves it stale until it is validated again where they do not (H-47, 2026-11-24, [Hermod#296](https://github.com/Vanaheimr/Hermod/pull/296)). A caller's own `If-None-Match` and `If-Modified-Since` are evaluated against the stored `200`, which is revalidated first with the cache's own validators where it has to be, and answered with a `304` where they match (H-45, 2026-11-24, [Hermod#300](https://github.com/Vanaheimr/Hermod/pull/300)). `Range`, `If-Range`, `If-Match` and `If-Unmodified-Since` go past it. The logic moved from `HTTP2/Core/` and lost its deviations on the way, so the HTTP/2 cache is fixed with it. 🔵 server: an origin keeps no cache. Measured against [cache-tests](docs/TestingAgainst_CacheTests.md) since 2026-11-22: 5 of its required tests fail, four asking for RFC 9213's `CDN-Cache-Control` and one for an interim response the proxy in front of the cache cannot pass on. The four findings it made in the cache are fixed: H-44, an `Age` list read as invalid, and H-46, every delta-seconds capped at 2^31, the same day ([Hermod#291](https://github.com/Vanaheimr/Hermod/pull/291), [Hermod#292](https://github.com/Vanaheimr/Hermod/pull/292)), H-47, a `HEAD` that updated nothing, and H-45, a caller's preconditions never evaluated, on 2026-11-24 ([Hermod#296](https://github.com/Vanaheimr/Hermod/pull/296), [Hermod#300](https://github.com/Vanaheimr/Hermod/pull/300)) |
| [RFC 5861](https://www.rfc-editor.org/rfc/rfc5861) | `stale-while-revalidate`, `stale-if-error` | ✅ client, since H-5: served stale and revalidated in the background, and served stale in place of a `500`, `502`, `503`, `504` or a failed request, each within its window and not where `must-revalidate` forbids it |
| [RFC 8246](https://www.rfc-editor.org/rfc/rfc8246) | `Cache-Control: immutable` | ✅ client, since H-5: a fresh `immutable` response is not revalidated by a request's `max-age`; `no-cache` still revalidates it |
| [RFC 9211](https://www.rfc-editor.org/rfc/rfc9211) / [9213](https://www.rfc-editor.org/rfc/rfc9213) | `Cache-Status` / `CDN-Cache-Control` | ⬜ |

## Content codings & negotiation

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| RFC 9110 §8.4 | Content codings | ✅ `HTTPContentCoding` in `HTTP/General/`: a buffered body decoded in reverse order and a streamed one inside `HTTPBodyStream`, unknown codings refused, a 64 MiB ceiling; the client's `AutomaticDecompression` and the server's `AutomaticContentCompression`, both off by default — H-2, 2026-09-24. The row said "neither compresses" until then, which was half wrong: `SinglePageAppHandler` had compressed static files all along |
| [RFC 1950](https://www.rfc-editor.org/rfc/rfc1950) / [1951](https://www.rfc-editor.org/rfc/rfc1951) / [1952](https://www.rfc-editor.org/rfc/rfc1952) | ZLIB / DEFLATE / GZIP | ✅ `gzip` and both of `deflate`'s formats, ZLIB-wrapped and raw, for HTTP bodies since H-2 — and DEFLATE in WebSocket `permessage-deflate` |
| [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932) | Brotli (`br`) | ✅ since H-2, via the BCL's `BrotliStream` |
| [RFC 8878](https://www.rfc-editor.org/rfc/rfc8878) | Zstandard (`zstd`) | ✅ since [Hermod#337](https://github.com/Vanaheimr/Hermod/pull/337) (2026-11-26), via the BCL's `ZstandardStream`, new in .NET 11 — the row read ❌ because the BCL had no codec until then. Both directions, on every path `br` and `gzip` take; `br` stays the server's first choice. Held to [RFC 9659](https://www.rfc-editor.org/rfc/rfc9659): no frame is written that needs a window above 8 MB, and none is taken — a larger one is refused as content that cannot be decoded, not as a connection failing |
| RFC 9110 §12 | Proactive negotiation — `Accept`, `Accept-Charset`, `Accept-Language`, `Accept-Encoding`, `Vary` | 🟢 `AcceptTypes.BestMatchingContentType()` for media types. ✅ opt-in since H-37: the representations of an `HTTPVariantHandler` registered with `AddResource` are negotiated on `Accept`, `Accept-Encoding` and `Accept-Language`, with `Vary` and a `406` only when every one is refused. Otherwise, and for `Accept-Charset` in any case, 🔵 |
| [RFC 2295](https://www.rfc-editor.org/rfc/rfc2295) / [2296](https://www.rfc-editor.org/rfc/rfc2296) | Transparent content negotiation | ⬜ |
| [RFC 6838](https://www.rfc-editor.org/rfc/rfc6838) / [6839](https://www.rfc-editor.org/rfc/rfc6839) | Media types, `+json`/`+xml` suffixes | 🟢 `HTTPContentType` |
| [RFC 7578](https://www.rfc-editor.org/rfc/rfc7578) | `multipart/form-data` | ✅ since [Hermod#345](https://github.com/Vanaheimr/Hermod/pull/345) (2026-11-27): `HTTPMultipartFormData`, in `Hermod/HTTP/` where HTTP/2 and HTTP/3 can use it too, reads a form and builds one. It is framed as RFC 2046 §5.1.1 has it: a delimiter counts only at the start of a line, the preamble and the epilogue are ignored, and a body that ends before its close delimiter is refused. Each part has one `Content-Disposition` of `form-data` with a `name`, and its charset is its own, the form's `_charset_` (§4.6) or UTF-8. `TryParseMultipartFormDataRequestBody` undoes the request's content coding first, and answers `415` and `400`. Verified by 48 tests, among them the bodies Chromium, Firefox, curl and Python sent, copied octet for octet. It replaces `Hermod.MIME`, whose parser kept the CRLF before every delimiter as content and threw on a preamble. The row read 🟡 until then. No harness here sends a form |
| [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) | Problem Details (`application/problem+json`) | ✅ `HTTPProblemDetails`, in `Hermod/HTTP/` where HTTP/2 and HTTP/3 can use it too. A handler sends one with `SetProblemDetails`, which sets the status code from the problem's, and a client reads one with `TryParseProblemDetails`, which ignores a member of the wrong type (§3.1). The server's own errors are problem details when `Accept` prefers `application/problem+json` (§4), and stay `application/json` otherwise, so a client that never asked sees no change. JSON only: the XML form of Appendix B is not offered. H-15, [Hermod#173](https://github.com/Vanaheimr/Hermod/pull/173), verified 2026-10-29 by four checks in the curl matrix, three of them red against `de9b5652` |

## Upgrade & protocol switching

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| RFC 9110 §7.8 + RFC 9112 §9.6 | `Upgrade`, `101 Switching Protocols` | ✅ a WebSocket can live on an HTTP path of the general server since Hermod `3bc56fdb` (2026-09-16, `WebSocketOnAnHTTPPathTests`), beside the standalone WebSocket listener; the demo's `/ws` route uses it — H-16 |
| [RFC 6455](https://www.rfc-editor.org/rfc/rfc6455) §4 | WebSocket handshake | ✅ see below |
| [RFC 2817](https://www.rfc-editor.org/rfc/rfc2817) | Upgrading to TLS within HTTP/1.1 | ⬜ |
| RFC 9113 (removes RFC 7540 §3.2) | `Upgrade: h2c` | ⬜ **correctly not implemented** — h2c upgrade was removed from the standard, and its absence is a smuggling defence, pinned as a regression by `h2csmuggler` in `tests/smuggler.sh` |
| [RFC 7838](https://www.rfc-editor.org/rfc/rfc7838) | `Alt-Svc` — advertising h2/h3 | 🟡 typed `Alt-Svc` on responses and `Alt-Used` on requests, with the grammar HTTP/2's ALTSVC frame uses (H-7, 2026-10-28). Whether to announce an alternative is the application's; the client records alternatives and does not act on them. The demo's `/alt-svc` sends `http%2F1.1=":8443"`, which **curl does not recognise** — it matches the unencoded `http/1.1` the RFC forbids, and records nothing for the encoding it requires. Pinned in the curl matrix |

## WebSocket

| Spec | Title | Hermod |
|---|---|---|
| [RFC 6455](https://www.rfc-editor.org/rfc/rfc6455) | The WebSocket Protocol | ✅ server + client. Framing, masking, fragmentation state machine, control-frame rules, strict UTF-8 (§8.1) with incremental validation, close handshake + status-code validation, handshake validation both directions (`400`/`426`), CSPRNG nonces and masking keys, subprotocol negotiation. **Autobahn: 296 + 242 cases, 0 failed** by Hermod's own count; driven here, **481/517 + 36 declined** against the server and **445/517 + 72 declined** against the client, 0 hard failures either way — see [`tests/TestingAgainst_Autobahn.md`](tests/TestingAgainst_Autobahn.md) |
| [RFC 7692](https://www.rfc-editor.org/rfc/rfc7692) | `permessage-deflate` | ✅ negotiated, sync-flush, RSV1 only on the first frame, decompression-bomb guard. **Autobahn 12/13: 126 + 126, 0 failed.** Limitation: window bits < 15 unsupported (`DeflateStream`), `no_context_takeover` always negotiated |
| [RFC 8307](https://www.rfc-editor.org/rfc/rfc8307) | Well-Known URIs for WebSocket | ⬜ |
| [RFC 8441](https://www.rfc-editor.org/rfc/rfc8441) / [RFC 9220](https://www.rfc-editor.org/rfc/rfc9220) | WebSocket over HTTP/2 / HTTP/3 | ⬜ here — implemented in the HTTP/2 stack (`HTTP2/WebSocket/`) |
| [RFC 6454](https://www.rfc-editor.org/rfc/rfc6454) | The Web Origin Concept | ✅ `AllowedOrigins` allow-list → `403` (CSWSH protection) |
| — | Hardening: handshake timeout, max handshake size, per-IP connection limits, message-size limits, backpressure, heartbeat/zombie detection, reconnect with backoff+jitter | ✅ |

## Streaming — Server-Sent Events

| Spec | Title | Hermod |
|---|---|---|
| [WHATWG HTML — Server-Sent Events](https://html.spec.whatwg.org/multipage/server-sent-events.html) | `text/event-stream` | ✅ server + client. `id`/`event`/`data`/`retry`, multi-line data, bounded history, `Last-Event-ID` replay, per-request filtering, comment heartbeats, cancellation and disconnect cleanup. Not an RFC |
| [RFC 6202](https://www.rfc-editor.org/rfc/rfc6202) | Long polling / streaming best practices | 🟢 informational; the SSE implementation follows it |

## TLS & transport

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| [RFC 8446](https://www.rfc-editor.org/rfc/rfc8446) | TLS 1.3 | ✅ via `SslStream` |
| [RFC 7301](https://www.rfc-editor.org/rfc/rfc7301) | ALPN | ✅ settable on the client; the server offers `http/1.1` alone and refuses a client insisting on `h2` with §3.2's `no_application_protocol` — H-17, 2026-10-04, and probed on the TLS leg of the gate |
| [RFC 9525](https://www.rfc-editor.org/rfc/rfc9525) | Service identity in TLS (obsoletes 6125) | 🟢 delegated to `SslStream` + configurable validators |
| [RFC 2818](https://www.rfc-editor.org/rfc/rfc2818) | HTTP over TLS | 🟢 superseded by RFC 9110 §4.2.2 |
| [RFC 6797](https://www.rfc-editor.org/rfc/rfc6797) | HSTS | 🟡 `StrictTransportSecurity` parsed strictly where §6.1 is strict, with a typed response field — H-12, 2026-10-04. `SecurityHeaderOptions` had carried the field all along; whether to send it is the application's, and the default is nothing |
| RFC 9110 §7.4 | Rejecting a request for an origin the TLS certificate is not valid for | ✅ `421` (§15.5.20) with `Connection: close`, before routing and before the body. The `Host` is judged against the certificate that connection's handshake presented, by `HTTPAuthority`'s RFC 6125 §6.4 matching, which moved from `HTTP2/Core` to `Hermod/HTTP/` for it. `AHTTPServer.IsAuthorityServed` takes its place behind a trusted gateway, the one exception §7.4 makes and a server cannot detect. A cleartext connection, which has no certificate, and an HTTP/1.0 request without `Host` are not checked. H-36, [Hermod#167](https://github.com/Vanaheimr/Hermod/pull/167), verified 2026-10-29 by four checks in the curl matrix's TLS leg, three of them red against `35b6b88e`. Until then `GET https://evil.example/` on the demo's `:8443` was a `200`, by `Host` alone and with SNI as well, and before that day this row read 🟢 "SNI/`Host` cross-check not enforced", a MUST described as a missing nicety |

## Browser-facing (non-IETF)

| Spec | Topic | Hermod |
|---|---|---|
| [WHATWG Fetch](https://fetch.spec.whatwg.org/) | CORS | ✅ `Access-Control-*` typed, and the `OPTIONS` preflight answered by an opt-in `HTTPCORSPipeline` + `CORSPolicy` ahead of routing, with `Access-Control-Allow-Methods` taken from the route — H-10, 2026-10-03, which the browser track found and verifies |
| [W3C CSP](https://www.w3.org/TR/CSP3/) | `Content-Security-Policy` | 🟢 sent as a string by `SinglePageAppHandler` for HTML documents and only for them, from `SecurityHeaderOptions`, whose default is same-origin throughout with `frame-ancestors 'none'` — asserted in `SinglePageAppTests`. No policy model and no parser, and no other handler sends it. The row said ❌ until 2026-10-28; the handler had been sending it all along, the same shape of error as H-12's |
| — | `X-Frame-Options` | 🟡 typed; `SinglePageAppHandler` sends `DENY` for HTML documents, beside the CSP's `frame-ancestors` |
| [WHATWG HTML](https://html.spec.whatwg.org/) | `EventSource`, `WebSocket` APIs | ✅ the browser side of SSE + WS — `tests/browser.sh`, Chromium, Firefox and WebKit, nightly (A8, 27/27) |

---

# Third-party conformance & interop tooling

HTTP/1.1 has no single canonical conformance suite the way HTTP/2 has
[h2spec](https://github.com/summerwind/h2spec). Coverage therefore has to be
assembled from several independent third-party tools — which is arguably
*better*, because each of them is a real-world consumer with its own strictness.

| Tool | Kind | What it proves | Availability here |
|---|---|---|---|
| [**curl**](https://curl.se/) | client | the reference HTTP client: `--http1.0`/`--http1.1`, chunked, `Expect: 100-continue`, keep-alive, ranges, cookies, Basic/Digest auth, `HEAD`, `--raw`, `-v` wire traces | ✅ **two** builds, deliberately: Windows 8.21 **without** HTTP/2 (cannot accidentally upgrade) and Debian 8.14 **with** nghttp2/nghttp3 (proves `--http1.1` and ALPN are honoured) |
| [**Autobahn TestSuite**](https://github.com/crossbario/autobahn-testsuite) | WebSocket | the canonical RFC 6455 + RFC 7692 suite, **both** `fuzzingclient` (tests our server) and `fuzzingserver` (tests our client) | ✅ via WSL/Debian Docker |
| [**http-garden**](https://github.com/narfindustries/http-garden) | differential fuzzer | request smuggling / parser differentials against 45 real servers and proxies — the state of the art for RFC 9112 §11.2 | ✅ Hermod added as a target, `tests/http-garden.sh` |
| [**smuggler.py**](https://github.com/defparam/smuggler) | scanner | CL.TE / TE.CL / TE.TE desync probes | ✅ `tests/smuggler.sh`, nightly — 134/134, nothing found |
| [**h2csmuggler**](https://github.com/BishopFox/h2csmuggler) | scanner | `Upgrade: h2c` smuggling — must find nothing, since h2c upgrade is absent | ✅ `tests/smuggler.sh`, and the negative is pinned |
| **nginx / HAProxy / Envoy / Apache httpd / Caddy / Traefik** | intermediaries | reverse-proxying Hermod. Proxies are the strictest HTTP/1.1 consumers in existence — framing bugs surface here first. Also as *upstreams* for Hermod's client | ✅ via WSL/Debian Docker |
| **Go `net/http`**, **Python `httpx`/`aiohttp`**, **Java `HttpClient`/OkHttp**, **Rust `hyper`** | reference peers | independent, strict, *non*-.NET implementations on both sides of the wire | 🟡 per-runtime setup, in WSL |
| **.NET `HttpClient` / Kestrel / Minimal API** | reference peer | already used inside `HermodTests/HTTP/dotNET/` | ✅ |
| [**h2load**](https://nghttp2.org/documentation/h2load-howto.html) (`--h1`), **wrk**, **bombardier**, **oha**, **k6** | load | keep-alive reuse and pipelining under concurrency; connection-lifecycle leaks | 🟡 apt/Docker in WSL |
| [**Playwright**](https://playwright.dev/) (Chromium/Firefox/WebKit) | browsers | `EventSource`, `WebSocket`, CORS preflight, chunked rendering, keep-alive — the acceptance test that matters most in practice | ✅ Node present |
| [**websocat**](https://github.com/vi/websocat), **`ws`**, Python **`websockets`** | WebSocket clients | independent WS peers beyond Autobahn | 🟡 |
| [**testssl.sh**](https://testssl.sh/) | TLS | the HTTPS listener's TLS posture (adjacent to, not part of, HTTP conformance) | 🟡 |
| **`openssl s_client`**, **`nc`**, `tshark` | raw wire | hand-crafted byte sequences; `tshark`'s dissector as an independent framing validator | ✅ |
| [**cache-tests.fyi**](https://cache-tests.fyi/) | caching | the client's cache since H-5, behind `tests/cache-proxy/`, a reverse proxy written for the purpose, because the suite tests a cache standing in front of its own origin | ✅ `tests/cache-tests.sh`, nightly — 480 tests as recorded, see [the write-up](docs/TestingAgainst_CacheTests.md) |

**Container host:** everything above that needs Docker runs in **WSL/Debian**
(Docker 26.1.5, already installed) rather than Docker Desktop. WSL has no
systemd, so the daemon needs `sudo service docker start` after a reboot — the
runner scripts do that themselves. The demo host runs on Windows, so from
inside a container it is reachable via the host IP, not `localhost`.

**Not applicable:** h2spec, nghttp2's `nghttp`/`nghttpd`, the QUIC interop
runner — all HTTP/2- or HTTP/3-specific.

---

# Project layout

Planned — mirrors the HTTP/2 repository, see [`PLAN.md`](PLAN.md) for the
sequencing:

```
HTTP1ConformanceTests/               solution HTTP1.slnx (at the repo root)
├── libs/
│   ├── Hermod/                      ← git submodule (Vanaheimr Hermod)
│   │   ├── Hermod/HTTP1/            the hand-rolled HTTP/1.x stack
│   │   ├── Hermod/HTTP/             shared HTTP model (methods, status, URL, …)
│   │   └── HermodTests/             the existing NUnit suites (Tests.HTTP.*)
│   └── Styx/                        ← git submodule (Vanaheimr Styx)
├── Demo/                            demo host — http :8080, https :8443, ws :8081
├── tests/
│   ├── run-tests.sh                 the gate: build → start demo → all harnesses
│   ├── H1Core/                      shared raw socket, checks, target parsing
│   ├── h1syntax/                    request line, targets, versions, field syntax
│   ├── h1framing/                   body length, transfer codings, chunks, trailers
│   ├── h1conn/                      persistence, pipelining, half-close
│   ├── h1semantics/                 methods, conditionals, ranges, negotiation, auth
│   ├── h1sse/                       Server-Sent Events
│   ├── h1attack/                    smuggling, slowloris, floods, limits
│   └── h1raw/                       diagnostic wire dumper (not in the gate)
└── docs/BUILD_LOG.md                chronological build history
```

# License

Apache License 2.0 — © 2010-2026 GraphDefined GmbH. The full text is in
[`LICENSE`](LICENSE). The Hermod and Styx submodules are likewise Apache-2.0.
