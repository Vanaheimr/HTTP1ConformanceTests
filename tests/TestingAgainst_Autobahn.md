# Testing against the Autobahn TestSuite

[Autobahn|TestSuite](https://github.com/crossbario/autobahn-testsuite) is the canonical RFC 6455
WebSocket conformance suite. Its `fuzzingclient` drives 517 cases against a WebSocket **echo**
server — framing, fragmentation, UTF-8 handling, the close handshake, and `permessage-deflate`
(RFC 7692) across a dozen parameter sets.

```bash
tests/autobahn.sh              # build + run everything
tests/autobahn.sh --no-build
```

The only prerequisite is Docker; the suite ships usably only as an image, because the native
`wstest` is legacy Python 2.

## Why this suite belongs in *this* repository

Autobahn speaks WebSocket over an **HTTP/1.1 Upgrade handshake** — which is exactly what this stack
does. The whole path is under test here: handshake, framing, compression, close.

That is not true of the HTTP/2 sibling, which also runs Autobahn. Autobahn does not speak RFC 8441
(WebSocket over HTTP/2), so there the suite is pointed at a plain-TCP tunnel behind an HTTP/1.1
handshake and what it certifies is the framing layer alone.

The two are also not the same code. Hermod carries **three separate WebSocket implementations**:

| | Size | Covered by Autobahn |
|---|---|---|
| `HTTP1/WebSocket/` | 24 files, ~12 000 lines — own `WebSocketFrame`, own `WebSocketPerMessageDeflate`, server + client + applications | **this driver, since 2026-09-22** |
| `HTTP2/WebSocket/` | 6 files, ~860 lines | the HTTP/2 sibling's nightly |
| `HTTP3/WebSocket/` | 4 files — a byte-identical copy of the HTTP/2 one | nothing |

Until this driver existed, the widely-quoted "Autobahn 517/517" certified the *smallest* of the
three, and the largest — the one with the production server, client and applications on top — was
covered by nothing.

## The target

`Demo/Program.cs` has served the target all along: a WebSocket echo server on `:8081`, with the
comment *"the Autobahn fuzzingclient target"*. It simply was never driven.

`tests/autobahn.sh` starts the demo host itself and stops it again, and deliberately **omits
`--fast-timeouts`**, which `tests/run-tests.sh` does pass. That flag shortens the demo's read
deadlines so the timeout harness resolves in seconds; several Autobahn cases pause mid-message on
purpose, and a shortened deadline would close those connections and report our own test
configuration as a conformance failure.

## Current result: 481 / 517

Measured 2026-09-22, the first time this stack was ever pointed at the suite.

| Verdict | Count |
|---|---|
| `OK` | 476 |
| `INFORMATIONAL` | 3 |
| `NON-STRICT` | 2 |
| `UNIMPLEMENTED` | **36** |

**Everything except compression parameter negotiation passes.** All 301 non-compression cases were
clean on the very first run, before anything was changed — which is a real result for 12 000 lines
that no foreign suite had ever touched.

### What the first run found

Sections 12 and 13 — all 216 cases — came back `UNIMPLEMENTED`, because the demo never offered
`permessage-deflate`. `AWebSocketServer.EnablePerMessageDeflate` exists and is documented as
*"Disabled by default"*, which is the right default for a library and the wrong one for a
conformance target: the extension is implemented (`WebSocketPerMessageDeflate.cs`, 351 lines) and
was simply not switched on. One line in the demo took the score from 301 to 481.

### What remains: `client_max_window_bits = 9`

The 36 that are still `UNIMPLEMENTED` are sections **13.3** and **13.5**, and the pattern is exact:

| Case | Client offer | Result |
|---|---|---|
| 13.1, 13.2 | window bits not requested | OK |
| 13.4, 13.6 | window bits = 15 | OK |
| **13.3, 13.5** | **window bits = 9** | **UNIMPLEMENTED** |
| 13.7 | a list including 9, but also "not requested" | OK — the server picks another offer |

So the server accepts the extension when the window is left at the default or explicitly 15, and
declines the offer when the client asks for a smaller window. RFC 7692 §7.1.2 permits declining an
offer that cannot be satisfied — the fallback is simply no compression — so this is a limitation
rather than a protocol violation, and Autobahn says `UNIMPLEMENTED` rather than `FAILED`.

It is worth a look nonetheless, because the HTTP/2 sibling's implementation reports 517/517 against
the same suite and therefore does accept `9`. Two implementations in the same library, one
answering an offer the other declines.

## Why this is not in CI yet

`tests/autobahn.sh` exits non-zero on any non-passing case, so wiring it into a gate today would
make it red on day one. Excluding sections 13.3 and 13.5 to get a green badge is the wrong trade:
an allowance like that outlives the reason for it, and this repository has a fresh example next
door of a number that looked like corroboration and was not.

The sequence that gets this gated is: settle `client_max_window_bits`, re-measure, then add a
nightly job (the HTTP/2 sibling's `nightly.yml` is the template — Autobahn needs Docker, which
belongs in a nightly rather than a push gate).

## Reading the report

`tests/autobahn/reports/index.html` is the human-readable report: per case, the frames exchanged
and the verdict. `index.json` is what the script parses. The demo host's own log is kept beside
them as `demo-host.log`, because it is the only view of a failure from our side of the wire.
