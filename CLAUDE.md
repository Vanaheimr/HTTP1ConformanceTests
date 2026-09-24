# HTTP/1.1 Conformance Tests & Demo — C# / .NET 10

This repository is the **runnable demo host** plus the **conformance / interop
test drivers** for the from-scratch HTTP/1.0 + HTTP/1.1 stack (built directly on
`TcpClient`/`SslStream` — no `Kestrel`, no `HttpListener`, no
`System.Net.Http`). The stack itself lives in the Vanaheimr **Hermod** library,
pulled in here as a git submodule under `libs/Hermod/Hermod/HTTP1/`. This repo
adds the `Demo/` host, the `tests/` raw-wire harnesses, and the third-party
suite drivers; the **561 NUnit tests** live with the stack in Hermod
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

Target framework is `net10.0`. TLS uses a self-signed cert generated at startup.

**Tests:** most coverage is the **561 NUnit tests** in `libs/Hermod/HermodTests/`
under `FullyQualifiedName~Hermod.Tests.HTTP.` — of which **329** are the HTTP/1.x
protocol regression selection and **49** cover RFC 6455/7692 WebSockets. CI gates
on 561 + the single test in `Tests.HTTPS.` = **562**. These are run counts, not
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
is not running after a reboot and the runner scripts start it themselves
(`sudo service docker start`).

**Reaching the demo from WSL:** start it with `--bind-any` (0.0.0.0 instead of
loopback) and address it by the **host IP** from `ip route show default`, not
`localhost`. No firewall rule is needed. `tests/run-tests.sh --wsl` does both.
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
**A1 done** — the demo host on `:8080` / `:8443` / `:8081`. See [`Demo/README.md`](Demo/README.md).
**A2 done** — the raw-wire harnesses (201 checks).
**A3 done** — the curl matrix (69 checks, 70 where curl has HTTP/2 *and* the
target is local — see [`tests/README.md`](tests/README.md)). The gate is **270/270 over both
transports** (`tests/run-tests.sh`, ~103 s cleartext / ~270 s TLS). See
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
| Hermod's own NUnit suites | 561 `Tests.HTTP.` / 329 regression selection / 49 WebSockets, all run counts — 562 under the filter CI gates on, which adds the one test in `Tests.HTTPS.`; see [`README.md`](README.md) for each filter |
| this repo's gate | **270/270**, cleartext and TLS (201 raw-wire + 69 curl) |
| with `--wsl` (second curl build) | **339/339** |
| Autobahn (server) | **481/517** + 36 declined, 0 hard failures — nightly, gated on the floor. The intermittent mid-case drop was **H-25**, fixed 2026-09-24 |
| Autobahn (client) | **445/517** + 72 declined, 0 hard failures — nightly, gated on the floor |

Building A1 and A2 produced three upstream findings between them (**H-21**,
**H-22**, **H-23**), which is the pattern to expect: this repository is the
first consumer of these APIs that is not also a test written by their author.

### What the state analysis found

Documented per-RFC in [`README.md`](README.md), tracked as H-1…H-20 in
[`PLAN.md`](PLAN.md). The four that matter most:

| | |
|---|---|
| **H-2** | **no content coding at all** for HTTP/1 bodies — `Content-Encoding`/`Accept-Encoding` are header models with no codec behind them. `HTTP2/Core/HTTPContentCoding.cs` already implements `br`/`gzip`/`deflate` |
| **H-3** | `HTTPDigestAuthentication` is **not** RFC 7616 — it is `Digest base64(user):base64(secret)`, no realm/nonce/qop/response. curl's `--digest` will not interoperate |
| **H-1** | `308` missing entirely; `425` exists under the stale name `NoCode` (RFC 8470 calls it *Too Early*). Also absent: `103` `421` `451` `511` |
| ~~**H-16**~~ | *the general HTTP server has no `Upgrade` dispatch* — fixed upstream 2026-09-16, and the demo's `/ws` route landed 2026-09-24 |

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
