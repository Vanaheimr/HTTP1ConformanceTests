# Testing against the Autobahn TestSuite

[Autobahn|TestSuite](https://github.com/crossbario/autobahn-testsuite) is the canonical RFC 6455
WebSocket conformance suite. It runs 517 cases — framing, fragmentation, UTF-8 handling, the
close handshake, and `permessage-deflate` (RFC 7692) across a dozen parameter sets — and it runs
them in **both directions**, which is why this repository has two drivers.

```bash
tests/autobahn.sh              # fuzzingclient  vs. our SERVER   → 481/517
tests/autobahn-client.sh       # fuzzingserver  vs. our CLIENT   → 445/517
```

Both take `--no-build`, and both gate on a floor in the nightly. The only prerequisite is Docker;
the suite ships usably only as an image, because the native `wstest` is legacy Python 2.

`WebSocketServer` and `WebSocketClient` are separate implementations inside one subsystem, so
certifying one says nothing about the other. Until 2026-09-23 only the server had ever met a
foreign suite, while Hermod's WebSocket README quoted client numbers that no command in any
repository could reproduce.

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
| `HTTP3/WebSocket/` | 4 files — a byte-identical copy of the HTTP/2 one | nothing, but see below |

Until this driver existed, the widely-quoted "Autobahn 517/517" certified the *smallest* of the
three, and the largest — the one with the production server, client and applications on top — was
covered by nothing.

The HTTP/3 copy still has no suite pointed at it, but it is no longer uncovered on the point this
page is about: since 2026-09-23 `HermodTests/HTTP2/WebSocketDeflateNegotiationTests` asserts one
table of 21 extension offers against **both** the HTTP/2 and the HTTP/3 copy of `WebSocketDeflate`,
and compares the two against each other, so the negotiation cannot drift between them again
unnoticed.

## The target

`Demo/Program.cs` has served the target all along: a WebSocket echo server on `:8081`, with the
comment *"the Autobahn fuzzingclient target"*. It simply was never driven.

`tests/autobahn.sh` starts the demo host itself and stops it again, and deliberately **omits
`--fast-timeouts`**, which `tests/run-tests.sh` does pass. That flag shortens the demo's read
deadlines so the timeout harness resolves in seconds; several Autobahn cases pause mid-message on
purpose, and a shortened deadline would close those connections and report our own test
configuration as a conformance failure.

## The server side: fuzzingclient against our echo server — 481 / 517

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

### What remains: `server_max_window_bits = 9`, and it is correct behaviour

The 36 that are still `UNIMPLEMENTED` are sections **13.3** and **13.5**, and the pattern is exact:

| Case | What the client actually offers | Result |
|---|---|---|
| 13.1, 13.2 | no `server_max_window_bits` | OK |
| 13.4, 13.6 | `server_max_window_bits=15` | OK |
| **13.3, 13.5** | **`server_max_window_bits=9`** | **UNIMPLEMENTED** |
| 13.7 | a list including 9, but also an offer without it | OK — the server takes the other offer |

Read off the wire, from the `httpRequest` recorded in each case report — not inferred
from the case description, which names the parameter only as "requestMaxWindowBits".

`server_max_window_bits=N` is the client telling the **server** to cap its own compression window.
.NET's `DeflateStream` exposes no control over `windowBits`, so the server cannot comply with 9 —
and RFC 7692 §7.1.2.1 says a server that cannot satisfy an offer must **decline** it, falling back
to no compression. That is exactly what `TryNegotiateAsServer` does, and `UNIMPLEMENTED` is
Autobahn's accurate word for "the server declined the extension for this offer" — not `FAILED`.

**So there is nothing to fix here.** An earlier version of this file named the parameter
`client_max_window_bits` and suggested the HTTP/2 sibling handled it better because it reported
517/517. Both halves were wrong. The parameter is `server_max_window_bits`, and the sibling's
higher score came from the opposite of a better implementation: its `WebSocketDeflate.ShouldAccept`
accepted any offer whose value merely contained the string `permessage-deflate`, without parsing
the parameters at all. Faced with `server_max_window_bits=9` it answered "accepted" and then
compressed with a 15-bit window — which a client that had allocated a 9-bit inflate window could
not decode. Autobahn does not catch that, because Python's zlib inflates with a large window
regardless.

**That is now settled, and it settled in this file's favour.** Hermod `eb7bf410` taught the
sibling's `ShouldAccept` to parse the offer, `8405935e` mirrored the fix into the HTTP/3 copy and
pinned both with a test, and the HTTP/2 conformance repository bumped its pin on 2026-09-23. Its
Autobahn run now reports **481/517** with the same verdict breakdown as this one, to the digit:
476 `OK`, 3 `INFORMATIONAL`, 2 `NON-STRICT`, 36 `UNIMPLEMENTED`. Its floor was lowered from 517 to
481 deliberately, with the reason recorded in its `autobahn.sh`.

Two implementations written independently of each other — 24 files against 6, sharing no code —
converging on the same number against the same 517 cases is a stronger statement than either of
them scoring 517. `TryNegotiateAsServer` here was right the whole time; what changed is that the
sibling stopped disagreeing with it.

## The other direction: `fuzzingserver` against our client — 445 / 517

`tests/autobahn-client.sh` inverts the topology. There the suite connects to our echo server;
here the suite **listens** and `tests/autobahn-client/` — a driver over Hermod's
`WebSocketClient` — connects into it, case by case.

The fuzzingserver protocol is three kinds of connection: `/getCaseCount` returns the number of
cases, `/runCase?case=N&agent=A` runs one case against whatever connects (the client must echo
every message back with its type preserved, and the server hangs up when the case is done), and
`/updateReports?agent=A` makes the server write the report. A case is one connection, and "the
case ended" is "the server hung up".

| Verdict | Count |
|---|---|
| `OK` | 440 |
| `INFORMATIONAL` | 3 |
| `NON-STRICT` | 2 |
| `UNIMPLEMENTED` | **72** |
| **hard failures** | **0** |

Measured 2026-09-23, the first time this client was ever pointed at the suite. **Zero hard
failures**: everything the client attempts, it gets right — 445 of 517 on the first run, with no
change to the library.

### The 72 declines, and why they are not the server's 36

The server's 36 and the client's 72 look like the same finding and are not.

The 72 are sections **13.3, 13.4, 13.5 and 13.6**, eighteen cases each, and they are declined by
the **suite**, not by us. Read off the wire from each case report: those sections configure the
fuzzingserver to expect a specific client offer.

| Section | The offer the suite expects | Result |
|---|---|---|
| 13.1, 13.2 | `requestMaxWindowBits = 0` — no such parameter | OK |
| **13.3, 13.4** | `server_max_window_bits` = **9** / **15** | **UNIMPLEMENTED** |
| **13.5, 13.6** | the same, plus `client_no_context_takeover` | **UNIMPLEMENTED** |
| 13.7 | a list, one entry of which carries no parameter | OK — that entry matches |

Our client offers the fixed constant `WebSocketPerMessageDeflate.ClientOfferHeader`:

```
Sec-WebSocket-Extensions: permessage-deflate; client_no_context_takeover; server_no_context_takeover
```

It never carries `server_max_window_bits`, so in 13.3–13.6 the suite answers with **no**
`Sec-WebSocket-Extensions` header at all and the compression those sections wanted to exercise is
never negotiated. Nothing is wrong on the wire. What is missing is an offer we do not know how to
make.

So the two numbers are opposite ends of one fact. `DeflateStream` exposes no control over
`windowBits`, which means this stack can neither *honour* a constrained window (the server's
RFC-required refusal, 36 cases) nor meaningfully *request* one (the client's narrow offer, 72
cases). Making the client offer configurable would be the fix for the second half; the
`HTTPRequestBuilder` hook on `WebSocketClient` already lets a caller replace the header by hand,
so the gap is "not offered as a feature" rather than "impossible".

## In CI: the nightly, gated on a floor

[`.github/workflows/nightly.yml`](../.github/workflows/nightly.yml) runs this every night at
03:37 UTC on `ubuntu-latest`, and **fails on the result** — a nightly that reports a number
without failing on it is a report nobody reads. It is not in the push gate because of how the
suite is *acquired* rather than how it behaves: Autobahn ships usably only as a Docker Hub image,
and a registry rate limit turning a push red would teach people to ignore the gate.

The gate is a **floor, not a target**. `min_pass` in `tests/autobahn.sh` is 481, and the run
counts three buckets rather than two:

| Bucket | Verdicts | Effect |
|---|---|---|
| passing | `OK`, `NON-STRICT`, `INFORMATIONAL` | must stay at or above `min_pass` |
| declined | `UNIMPLEMENTED` | tolerated and counted — it is the RFC-required refusal, not a defect |
| hard | `FAILED`, `WRONG CODE`, `UNCLEAN` | **always fatal**, whatever the count says |

So the floor can only ever absorb a change in how many extension offers we decline. It cannot
launder a real failure into a pass, which is what made 481 gateable at all.

The alternative was excluding sections 13.3 and 13.5 to buy a green badge. That is the worse
trade: an exclusion hides the cases, a floor keeps counting them — and an allowance outlives the
reason it was granted. When the number goes *up*, the script says so out loud and asks for the
floor to be raised; a floor nobody raises is a ratchet that has rusted.

Both drivers are gated the same way, in two jobs rather than two steps of one — they bind
different ports and run for minutes each, and a failure in one should not cost the other its
result:

| Job | Driver | Floor |
|---|---|---|
| `autobahn` | `tests/autobahn.sh` | 481 |
| `autobahn-client` | `tests/autobahn-client.sh` | 445 |

## Reading the report

`tests/autobahn/reports/index.html` (server) and `tests/autobahn/reports-client/index.html`
(client) are the human-readable reports: per case, the frames exchanged and the verdict.
`index.json` beside each is what the scripts parse, and the per-case JSON files are where the
actual handshake is recorded — `httpRequest` and `httpResponse` per case, which is how the
`server_max_window_bits` story above was read off the wire rather than inferred from the case
descriptions, whose wording ("requestMaxWindowBits") names the parameter differently.

The demo host's own log is kept beside the server report as `demo-host.log`, because it is the
only view of a failure from our side of the wire. For the client run the equivalent is the
fuzzingserver container's log, whose tail the script prints.
