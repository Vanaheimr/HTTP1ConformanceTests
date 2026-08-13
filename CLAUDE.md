# HTTP/1.1 Conformance Tests & Demo — C# / .NET 10

This repository is the **runnable demo host** plus the **conformance / interop
test drivers** for the from-scratch HTTP/1.0 + HTTP/1.1 stack (built directly on
`TcpClient`/`SslStream` — no `Kestrel`, no `HttpListener`, no
`System.Net.Http`). The stack itself lives in the Vanaheimr **Hermod** library,
pulled in here as a git submodule under `libs/Hermod/Hermod/HTTP1/`. This repo
adds the `Demo/` host, the `tests/` raw-wire harnesses, and the third-party
suite drivers; the **440 NUnit tests** live with the stack in Hermod
(`HermodTests/`, namespace `Tests.HTTP.*`).

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

Target framework is `net10.0`. TLS uses a self-signed cert generated at startup.

**Tests:** most coverage is the **440 NUnit tests** in `libs/Hermod/HermodTests/`
under `Tests.HTTP.*` — of which **300** are the HTTP/1.x protocol regression
selection and **42** cover RFC 6455/7692 WebSockets:

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
is not running after a reboot and the runner scripts start it themselves
(`sudo service docker start`). The demo host runs on Windows, so from inside a
container it is reachable via the **host IP**, not `localhost`.

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
| `Server/URLMapping/` | the current routing tree (host → path → method → content type) |
| `Server/URLMapping_old/` | **the previous routing generation — probably dead. See H-18** |
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
**A1 done** — the demo host on `:8080` / `:8443` / `:8081`, every route verified
with curl and a raw RFC 6455 handshake. See [`Demo/README.md`](Demo/README.md).
**A2 next** — the raw-wire harnesses.

Nothing under `tests/` exists yet. The numbers this repository can currently
quote are Hermod's own (440 / 300 / 42, verified by `--list-tests`) plus the
Autobahn results recorded in the WebSocket README, which are **not yet
reproducible from a clean checkout** — making them so is A4.

Building A1 produced two upstream findings on its own (**H-21**, **H-22**),
which is the pattern to expect: the demo is the first consumer of these APIs
that is not also a test written by the same person who wrote them.

### What the state analysis found

Documented per-RFC in [`README.md`](README.md), tracked as H-1…H-20 in
[`PLAN.md`](PLAN.md). The four that matter most:

| | |
|---|---|
| **H-2** | **no content coding at all** for HTTP/1 bodies — `Content-Encoding`/`Accept-Encoding` are header models with no codec behind them. `HTTP2/Core/HTTPContentCoding.cs` already implements `br`/`gzip`/`deflate` |
| **H-3** | `HTTPDigestAuthentication` is **not** RFC 7616 — it is `Digest base64(user):base64(secret)`, no realm/nonce/qop/response. curl's `--digest` will not interoperate |
| **H-1** | `308` missing entirely; `425` exists under the stale name `NoCode` (RFC 8470 calls it *Too Early*). Also absent: `103` `421` `451` `511` |
| **H-16** | the general HTTP server has no `Upgrade` dispatch — WebSocket is a separate listener |

### The distinction the matrix exists to preserve

"Hermod has a `Range` header field" is not the claim "Hermod serves `206`". The
matrix uses six markers rather than a checkmark because the interesting question
in an HTTP stack is not implemented/missing but **who owns the semantics**:
typed-but-no-policy (🟡), deliberately-the-handler's (🔵), and genuinely absent
(❌) are three different things. Collapsing them is how a design decision reads
as a defect.

Concretely, for the harnesses: `h1semantics` will find no automatic `206`/`304`.
That is correct. The **demo's handlers** implement them, and the harness then
verifies the demo — not the library.

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
- Runner scripts come in both `.ps1` and `.sh` variants: the PowerShell ones use
  `Get-NetTCPConnection` (Windows-only) and cannot run under `pwsh` on Linux.

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
