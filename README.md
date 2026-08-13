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

The **demo host** and the **raw-wire harnesses** are in place; the third-party
suite drivers are next (see [`PLAN.md`](PLAN.md)).

```bash
tests/run-tests.sh        # 257/257 checks, ~103 s
```

| | |
|---|---|
| `tests/` raw-wire harnesses | **199/199** — syntax, framing, connection management, RFC 9110 semantics, SSE, smuggling/hardening |
| `tests/curl-matrix.sh` | **58/58** — the first checks here made by a client nobody in this repository wrote |
| both, over cleartext *and* TLS | **257/257** |
| `Demo/` host | `:8080` cleartext, `:8443` TLS, `:8081` WebSocket — 14 routes |
| remaining third-party suites | not yet — Autobahn (A4), proxies (A5), http-garden (A6), browsers (A8) |

On top of that, the coverage inside Hermod itself:

| Where | Count | What |
|---|---:|---|
| `HermodTests/` namespace `Tests.HTTP.*` | **440** NUnit tests | everything HTTP/1.x — see the two rows below plus URL/query/hostname/method models, the `HTTPAPI` layer and the `HTTPTestServer` |
| ↳ the HTTP/1.x protocol regression selection | **300** | client/server end-to-end, framing regressions, HTTP/1.0 behaviour, pipelining, chunked + trailers, limits/timeouts, .NET interop |
| ↳ `Tests.HTTP.WebSockets` | **42** | RFC 6455 framing, handshake hardening, subprotocols, backpressure, reconnect, `permessage-deflate` |
| Autobahn (run manually, not yet automated here) | 296 + 242 + 126 + 126 | server & client suites incl. sections 12/13 |

Counts verified by `dotnet test --list-tests` against this checkout. The
protocol-regression figure is the filter documented in
[`libs/Hermod/Hermod/HTTP1/README.md`](libs/Hermod/Hermod/HTTP1/README.md),
which recorded 295 on 2026-07-18 — it has since grown to 300.

## Requirements

- .NET 10 SDK (`net10.0` target)
- PowerShell 7+ (`pwsh`) for the runner scripts
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
dotnet test libs/Hermod/HermodTests/HermodTests.csproj --filter "FullyQualifiedName~HTTPClientTests|FullyQualifiedName~HTTPServerSocketRegressionTests|FullyQualifiedName~HTTPClientProtocolRegressionTests|FullyQualifiedName~HTTP11AuditRegressionTests|FullyQualifiedName~HTTPServerListenerMatrixTests"
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
🔵 — the handler builds the partial representation.

---

# RFC & specification matrix

## Core — the current HTTP standard (2022, STD 97/98/99)

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110) | HTTP Semantics (STD 97) | ✅ core: methods, status codes, `Host`, connection handling, `Expect: 100-continue`, bodyless responses, representation metadata, extension fields. Resource semantics 🔵 |
| [RFC 9111](https://www.rfc-editor.org/rfc/rfc9111) | HTTP Caching (STD 98) | ❌ no cache. `Cache-Control`/`Age`/`Expires`/`Vary` are 🟡. *(the HTTP/2 stack has `HTTPCache.cs` — HTTP/1 does not)* |
| [RFC 9112](https://www.rfc-editor.org/rfc/rfc9112) | HTTP/1.1 message syntax & routing (STD 99) | ✅ start lines, header parsing, framing, persistence, pipelining, chunked, trailers, malformed-message rejection |
| [RFC 9113](https://www.rfc-editor.org/rfc/rfc9113) | HTTP/2 | ⬜ separate stack — see HTTP2ConformanceTests. Note: `Upgrade: h2c` was **removed** by 9113 and is correctly absent |
| [RFC 9114](https://www.rfc-editor.org/rfc/rfc9114) | HTTP/3 | ⬜ separate stack — see HTTP3ConformanceTests |
| [RFC 9205](https://www.rfc-editor.org/rfc/rfc9205) | Building Protocols with HTTP (BCP 56bis) | 🔵 guidance for applications on top |

## Historic & obsoleted — still required for interop

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| [RFC 1945](https://www.rfc-editor.org/rfc/rfc1945) | HTTP/1.0 | ✅ requests/responses, `Content-Length`, close-delimited bodies, default close, negotiated keep-alive, chunked rejection, `HTTP/1.0` status line |
| [RFC 2616](https://www.rfc-editor.org/rfc/rfc2616) | HTTP/1.1 (obsolete) | 🟢 superseded by 9110/9112; still the citation in ~70 legacy source comments → cleanup ToDo |
| [RFC 7230](https://www.rfc-editor.org/rfc/rfc7230)–[7235](https://www.rfc-editor.org/rfc/rfc7235) | HTTP/1.1 series (obsolete) | 🟢 superseded by 9110–9112 |
| [RFC 2145](https://www.rfc-editor.org/rfc/rfc2145) | Use and interpretation of HTTP version numbers | ✅ non-canonical versions (`HTTP/1`, `HTTP/01.1`, wrong case) are rejected |
| "HTTP/1.0 Keep-Alive" (never standardized) | `Connection: Keep-Alive` + `Keep-Alive` field | ✅ honoured only when negotiated in both directions; `close` wins |

## Message syntax, framing & connection management

| Spec / section | Topic | Hermod HTTP/1.x |
|---|---|---|
| RFC 9112 §2–3 | Message format, request line, request-target forms | ✅ origin-form ✅, asterisk-form (`OPTIONS *`) ✅, absolute-form ❌ *by design* (not a proxy), authority-form (`CONNECT`) → `501` |
| RFC 9110 §5 | Field syntax, obs-fold, whitespace before `:`, control chars | ✅ all rejected |
| RFC 9112 §6 | Message body length, the 7-step algorithm | ✅ incl. duplicate/conflicting `Content-Length`, `CL`+`TE` conflict, overflow/sign/non-decimal |
| RFC 9112 §7 | Transfer codings | ✅ `chunked` (both directions, both roles), chunk extensions, trailers, forbidden-trailer list. `gzip`/`deflate`/`compress` **transfer** codings ❌ |
| RFC 9112 §9 | Persistence, pipelining, `Connection` | ✅ persistent by default, pipelining in wire order, chunked request delimited before the next, invalid leading request closes the connection |
| RFC 9112 §11.2 | Message smuggling / response splitting | ✅ strict rejection of ambiguous framing — but not yet verified against an external differential fuzzer → **plan item** |
| RFC 9110 §7.6.1 | Connection-specific (hop-by-hop) fields | 🟡 `Connection`, `Trailer`, `Upgrade`, `Via` typed |
| [RFC 7239](https://www.rfc-editor.org/rfc/rfc7239) | `Forwarded` HTTP extension | ❌ — only `X-Forwarded-For` is typed. Marked `//ToDo` in `HTTPRequest.cs:1125` |
| [RFC 9440](https://www.rfc-editor.org/rfc/rfc9440) | `Client-Cert` / `Client-Cert-Chain` | ❌ (mTLS itself is supported at the TLS layer) |
| [RFC 8586](https://www.rfc-editor.org/rfc/rfc8586) | `CDN-Loop` | ⬜ |
| [RFC 9209](https://www.rfc-editor.org/rfc/rfc9209) | `Proxy-Status` | ⬜ |

## URIs, dates, encodings & field-value syntax

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| [RFC 3986](https://www.rfc-editor.org/rfc/rfc3986) | URI Generic Syntax (STD 66) | ✅ `URL`, `HTTPPath`, `QueryString`, `URIScheme`, `URLHost`; fragments, bad percent-escapes, encoded separators, dot segments, repeated slashes and double-encoding all rejected |
| [RFC 6874](https://www.rfc-editor.org/rfc/rfc6874) | IPv6 zone identifiers in URIs | ❌ |
| [RFC 5234](https://www.rfc-editor.org/rfc/rfc5234) / [RFC 7405](https://www.rfc-editor.org/rfc/rfc7405) | ABNF (STD 68) | ✅ used as the parsing reference |
| [RFC 3629](https://www.rfc-editor.org/rfc/rfc3629) | UTF-8 (STD 63) | ✅ incl. the incremental validator in the WebSocket path |
| RFC 9110 §5.6.7 + [RFC 1123](https://www.rfc-editor.org/rfc/rfc1123) | HTTP-date (IMF-fixdate) | 🟢 IMF-fixdate produced/parsed; obsolete RFC 850 and asctime forms ❌ |
| [RFC 9651](https://www.rfc-editor.org/rfc/rfc9651) | Structured Field Values (obsoletes 8941) | ❌ no structured-field parser/serializer |
| [RFC 8187](https://www.rfc-editor.org/rfc/rfc8187) | Charset/language in field parameters (`filename*`) | ❌ |
| [RFC 6266](https://www.rfc-editor.org/rfc/rfc6266) | `Content-Disposition` in HTTP | 🟡 typed field, no parameter-encoding logic |
| [RFC 8288](https://www.rfc-editor.org/rfc/rfc8288) | Web Linking (`Link`) | ❌ |
| [RFC 6570](https://www.rfc-editor.org/rfc/rfc6570) | URI Templates | ⬜ routing uses its own path-parameter syntax |

## Methods

| Spec | Methods | Hermod HTTP/1.x |
|---|---|---|
| RFC 9110 §9 | `GET` `HEAD` `POST` `PUT` `DELETE` `OPTIONS` `TRACE` `CONNECT` | ✅ routed, incl. server-wide `OPTIONS *`, resource `OPTIONS`, `405` + `Allow`. `TRACE` 🟡 (token + client only, no server echo). `CONNECT` → `501` ⬜ |
| [RFC 5789](https://www.rfc-editor.org/rfc/rfc5789) | `PATCH`, `Accept-Patch` | 🟡 method + `Accept-Patch` typed; patch-document semantics 🔵 |
| [RFC 10008](https://www.rfc-editor.org/rfc/rfc10008) | `QUERY` (safe, body-carrying read) | ✅ modeled safe+idempotent, end-to-end tested with fixed-length and chunked content, trailers, chunk extensions. `Accept-Query` 🟡, negotiation/caching 🔵 |
| [RFC 4918](https://www.rfc-editor.org/rfc/rfc4918) | WebDAV — `COPY` `LOCK` `MKCOL` `MOVE` `PROPFIND` `PROPPATCH` `UNLOCK` | 🟡 method tokens + `DAV`/`Depth`/`Destination`/`If`/`Lock-Token`/`Overwrite`/`Timeout` fields typed; no WebDAV resource implementation |
| [RFC 3253](https://www.rfc-editor.org/rfc/rfc3253) / [3744](https://www.rfc-editor.org/rfc/rfc3744) / [5323](https://www.rfc-editor.org/rfc/rfc5323) / [5842](https://www.rfc-editor.org/rfc/rfc5842) | WebDAV versioning/ACL/SEARCH/BIND | ⬜ |
| — | Hermod extension methods (`MIRROR` `SEARCH` `EXISTS` `COUNT` `FILTER` `STATUS` `AUTH` `SUBSCRIBE` …, ~30) | ✅ parsed, routed, with safe/idempotent metadata; `HTTPMethod.Register()` for application methods |

## Status codes

| Spec | Codes | Hermod |
|---|---|---|
| RFC 9110 §15 | 1xx/2xx/3xx/4xx/5xx core set | ✅ present and used |
| [RFC 8297](https://www.rfc-editor.org/rfc/rfc8297) | `103 Early Hints` | ❌ not defined |
| [RFC 7538](https://www.rfc-editor.org/rfc/rfc7538) → 9110 §15.4.9 | `308 Permanent Redirect` | ❌ not defined |
| RFC 9110 §15.5.20 | `421 Misdirected Request` | ❌ not defined |
| [RFC 8470](https://www.rfc-editor.org/rfc/rfc8470) | `425 Too Early` | ❌ the slot `425` is present but named `NoCode` (a stale RFC 2518 draft name) — **bug** |
| [RFC 7725](https://www.rfc-editor.org/rfc/rfc7725) | `451 Unavailable For Legal Reasons` | ❌ not defined |
| [RFC 6585](https://www.rfc-editor.org/rfc/rfc6585) | `428` `429` `431` `511` | 🟢 `428`/`429`/`431` ✅ (with a token-bucket rate limiter), `511` ❌ |
| RFC 4918 / [RFC 5842](https://www.rfc-editor.org/rfc/rfc5842) | `207` `422` `423` `424` `507` / `208` `508` | 🟢 `207`/`422`/`423`/`424`/`507` ✅; `208`/`508` ❌ |
| [RFC 3229](https://www.rfc-editor.org/rfc/rfc3229) | `226 IM Used` (delta encoding) | ⬜ |
| [RFC 2295](https://www.rfc-editor.org/rfc/rfc2295) | `506 Variant Also Negotiates` | 🟡 code present, TCN not implemented |
| [RFC 2324](https://www.rfc-editor.org/rfc/rfc2324) | `418 I'm a teapot` | ✅ obviously |

## Authentication

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| RFC 9110 §11 | Authentication framework, `WWW-Authenticate`, `Authorization`, `Proxy-*` | ✅ challenge/credential plumbing + `WWWAuthenticate` builder; policy 🔵 |
| [RFC 7617](https://www.rfc-editor.org/rfc/rfc7617) | Basic | ✅ typed parse/serialize + end-to-end server tests (challenge, malformed, invalid, forbidden) |
| [RFC 6750](https://www.rfc-editor.org/rfc/rfc6750) | OAuth 2.0 Bearer | 🟡 typed parse/serialize; validation 🔵 |
| [RFC 7616](https://www.rfc-editor.org/rfc/rfc7616) | Digest (SHA-256) | ❌ **`HTTPDigestAuthentication` is not RFC 7616** — it is a custom `Digest base64(user):base64(secret)` scheme with no realm/nonce/qop/nc/cnonce/response. Misleading name → rename or implement |
| [RFC 2617](https://www.rfc-editor.org/rfc/rfc2617) | Basic + Digest (obsolete) | ⬜ superseded |
| [RFC 9421](https://www.rfc-editor.org/rfc/rfc9421) | HTTP Message Signatures | ❌ (`Argus` has its own non-RFC signature verification) |
| [RFC 9530](https://www.rfc-editor.org/rfc/rfc9530) | Digest Fields (`Content-Digest`, `Repr-Digest`) | ❌ — only the obsolete `Content-MD5` ([RFC 1864](https://www.rfc-editor.org/rfc/rfc1864)) is typed |
| [RFC 8188](https://www.rfc-editor.org/rfc/rfc8188) | Encrypted Content-Encoding (`aes128gcm`) | ❌ |
| — | Hermod extensions: `Token`, `TOTP`, `API-Key` | ✅ non-standard, documented as such |

## Cookies

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| [RFC 6265](https://www.rfc-editor.org/rfc/rfc6265) | HTTP State Management | 🟢 `HTTPCookie`/`HTTPCookies` with `Path`, `Secure`, `HttpOnly`, `SameSite`, `Expires`; `Cookie`/`Set-Cookie` typed. Attribute-parsing edge cases untested |
| 6265bis (draft) | `__Secure-`/`__Host-` prefixes, `SameSite` defaults, size limits | ❌ |

## Conditional requests, ranges & caching

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| RFC 9110 §8.8 + §13 | Validators (`ETag`, `Last-Modified`), preconditions (`If-Match`, `If-None-Match`, `If-Modified-Since`, `If-Unmodified-Since`, `If-Range`) | 🟡 all fields typed and exposed; **no automatic `304`/`412` evaluation** — 🔵 handler's job |
| RFC 9110 §14 | Range requests, `206`, `Accept-Ranges`, `Content-Range`, `multipart/byteranges` | 🟡 fields typed; **no automatic `206` generation** — 🔵 |
| [RFC 8673](https://www.rfc-editor.org/rfc/rfc8673) | Range requests for unknown-length content | ⬜ |
| RFC 9111 | Caching | ❌ no cache, client- or server-side |
| [RFC 5861](https://www.rfc-editor.org/rfc/rfc5861) | `stale-while-revalidate`, `stale-if-error` | ❌ |
| [RFC 8246](https://www.rfc-editor.org/rfc/rfc8246) | `Cache-Control: immutable` | ❌ |
| [RFC 9211](https://www.rfc-editor.org/rfc/rfc9211) / [9213](https://www.rfc-editor.org/rfc/rfc9213) | `Cache-Status` / `CDN-Cache-Control` | ⬜ |

## Content codings & negotiation

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| RFC 9110 §8.4 | Content codings | ❌ **no codec** — `Content-Encoding`/`Accept-Encoding` are 🟡 header models only. Neither the server nor the client compresses or decompresses. *(the HTTP/2 stack has `HTTP2/Core/HTTPContentCoding.cs` with `br`/`gzip`/`deflate` — HTTP/1 has nothing)* |
| [RFC 1950](https://www.rfc-editor.org/rfc/rfc1950) / [1951](https://www.rfc-editor.org/rfc/rfc1951) / [1952](https://www.rfc-editor.org/rfc/rfc1952) | ZLIB / DEFLATE / GZIP | ❌ for HTTP bodies (DEFLATE **is** used by WebSocket `permessage-deflate`) |
| [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932) | Brotli (`br`) | ❌ |
| [RFC 8878](https://www.rfc-editor.org/rfc/rfc8878) | Zstandard (`zstd`) | ❌ |
| RFC 9110 §12 | Proactive negotiation — `Accept`, `Accept-Charset`, `Accept-Language`, `Accept-Encoding`, `Vary` | 🟢 `AcceptTypes.BestMatchingContentType()` for media types; language/charset/encoding negotiation and `Vary` bookkeeping 🔵 |
| [RFC 2295](https://www.rfc-editor.org/rfc/rfc2295) / [2296](https://www.rfc-editor.org/rfc/rfc2296) | Transparent content negotiation | ⬜ |
| [RFC 6838](https://www.rfc-editor.org/rfc/rfc6838) / [6839](https://www.rfc-editor.org/rfc/rfc6839) | Media types, `+json`/`+xml` suffixes | 🟢 `HTTPContentType` |
| [RFC 7578](https://www.rfc-editor.org/rfc/rfc7578) | `multipart/form-data` | 🟡 content type modeled, no multipart parser |
| [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) | Problem Details (`application/problem+json`) | ❌ |

## Upgrade & protocol switching

| Spec | Title | Hermod HTTP/1.x |
|---|---|---|
| RFC 9110 §7.8 + RFC 9112 §9.6 | `Upgrade`, `101 Switching Protocols` | 🟢 the WebSocket server performs the upgrade; the **general** HTTP server has no `Upgrade` dispatch — the two are separate listeners |
| [RFC 6455](https://www.rfc-editor.org/rfc/rfc6455) §4 | WebSocket handshake | ✅ see below |
| [RFC 2817](https://www.rfc-editor.org/rfc/rfc2817) | Upgrading to TLS within HTTP/1.1 | ⬜ |
| RFC 9113 (removes RFC 7540 §3.2) | `Upgrade: h2c` | ⬜ **correctly not implemented** — h2c upgrade was removed from the standard, and its absence is a smuggling defence (`h2csmuggler` → plan item) |
| [RFC 7838](https://www.rfc-editor.org/rfc/rfc7838) | `Alt-Svc` — advertising h2/h3 | ❌ — would be the natural bridge to the h2/h3 stacks |

## WebSocket

| Spec | Title | Hermod |
|---|---|---|
| [RFC 6455](https://www.rfc-editor.org/rfc/rfc6455) | The WebSocket Protocol | ✅ server + client. Framing, masking, fragmentation state machine, control-frame rules, strict UTF-8 (§8.1) with incremental validation, close handshake + status-code validation, handshake validation both directions (`400`/`426`), CSPRNG nonces and masking keys, subprotocol negotiation. **Autobahn: 296 + 242 cases, 0 failed** |
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
| [RFC 7301](https://www.rfc-editor.org/rfc/rfc7301) | ALPN | 🟢 `ApplicationProtocols` is settable on the client; the server does not negotiate `http/1.1` explicitly → plan item |
| [RFC 9525](https://www.rfc-editor.org/rfc/rfc9525) | Service identity in TLS (obsoletes 6125) | 🟢 delegated to `SslStream` + configurable validators |
| [RFC 2818](https://www.rfc-editor.org/rfc/rfc2818) | HTTP over TLS | 🟢 superseded by RFC 9110 §4.2.2 |
| [RFC 6797](https://www.rfc-editor.org/rfc/rfc6797) | HSTS | ❌ no `Strict-Transport-Security` |
| RFC 9110 §7.2 | `Host` vs. TLS SNI consistency | 🟢 `Host` validated; SNI/`Host` cross-check not enforced |

## Browser-facing (non-IETF)

| Spec | Topic | Hermod |
|---|---|---|
| [WHATWG Fetch](https://fetch.spec.whatwg.org/) | CORS | 🟡 `Access-Control-*` response fields typed and settable; **no automatic preflight handling** |
| [W3C CSP](https://www.w3.org/TR/CSP3/) | `Content-Security-Policy` | ❌ |
| — | `X-Frame-Options` | 🟡 typed |
| [WHATWG HTML](https://html.spec.whatwg.org/) | `EventSource`, `WebSocket` APIs | 🟢 the browser side of SSE + WS — verified manually, not in CI → plan item |

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
| [**http-garden**](https://github.com/narf-industries/http-garden) | differential fuzzer | request smuggling / parser differentials against ~20 real servers and proxies — the state of the art for RFC 9112 §11.2 | ✅ via WSL/Debian Docker |
| [**smuggler.py**](https://github.com/defparam/smuggler) | scanner | CL.TE / TE.CL / TE.TE desync probes | ✅ Python 3 on both sides |
| [**h2csmuggler**](https://github.com/BishopFox/h2csmuggler) | scanner | `Upgrade: h2c` smuggling — must find nothing, since h2c upgrade is absent | ✅ |
| **nginx / HAProxy / Envoy / Apache httpd / Caddy / Traefik** | intermediaries | reverse-proxying Hermod. Proxies are the strictest HTTP/1.1 consumers in existence — framing bugs surface here first. Also as *upstreams* for Hermod's client | ✅ via WSL/Debian Docker |
| **Go `net/http`**, **Python `httpx`/`aiohttp`**, **Java `HttpClient`/OkHttp**, **Rust `hyper`** | reference peers | independent, strict, *non*-.NET implementations on both sides of the wire | 🟡 per-runtime setup, in WSL |
| **.NET `HttpClient` / Kestrel / Minimal API** | reference peer | already used inside `HermodTests/HTTP/dotNET/` | ✅ |
| [**h2load**](https://nghttp2.org/documentation/h2load-howto.html) (`--h1`), **wrk**, **bombardier**, **oha**, **k6** | load | keep-alive reuse and pipelining under concurrency; connection-lifecycle leaks | 🟡 apt/Docker in WSL |
| [**Playwright**](https://playwright.dev/) (Chromium/Firefox/WebKit) | browsers | `EventSource`, `WebSocket`, CORS preflight, chunked rendering, keep-alive — the acceptance test that matters most in practice | ✅ Node present |
| [**websocat**](https://github.com/vi/websocat), **`ws`**, Python **`websockets`** | WebSocket clients | independent WS peers beyond Autobahn | 🟡 |
| [**testssl.sh**](https://testssl.sh/) | TLS | the HTTPS listener's TLS posture (adjacent to, not part of, HTTP conformance) | 🟡 |
| **`openssl s_client`**, **`nc`**, `tshark` | raw wire | hand-crafted byte sequences; `tshark`'s dissector as an independent framing validator | ✅ |
| [**cache-tests.fyi**](https://cache-tests.fyi/) | caching | only relevant if an RFC 9111 cache is ever added (**H-5**) | ⬜ |

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
