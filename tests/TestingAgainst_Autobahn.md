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

## When *we* are the one that fails: 12.4.18, and a log that said nothing

On 2026-09-23 the nightly's server job went red for the first time, on case **12.4.18** — "send
1000 compressed messages each of payload size 131072", the heaviest case in the suite at 128 MiB
of payload each way. Three other runs the same day passed it.

What the case report says, and it is worth reading precisely:

| | |
|---|---|
| `behavior` | `FAILED` |
| `duration` | 1504 ms — locally the same case passes in ~3700 ms |
| `txFrameStats` | `{"1": 717}` — the suite sent 717 text frames |
| `rxFrameStats` | `{"1": 716}` — it got 716 back, and **no** opcode 8 |
| `droppedByMe` | `false` |
| `wasNotCleanReason` | "peer dropped the TCP connection without previous WebSocket closing handshake" |

So: we echoed 716 of 717 messages correctly and then the connection was simply gone, with no
close frame, about 40 % of the way through a case that usually finishes. Not a hang — a drop.

**And the log said nothing.** `demo-host.log` was in the artifact, exactly as the section above
promises, and it contained the startup banner and not one further line. That is not because
nothing went wrong; it is because Hermod's servers take an `ILoggerFactory` and default it to
`NullLoggerFactory.Instance`. Only two code paths can end that read loop without a close frame,
and *both* log — one at Debug ("Read error on WebSocket connection"), one at Error ("Exception in
HTTP WebSocket server connection loop"). Both records were formatted into a null sink.

A file that exists, is collected, is named in the documentation, and is empty of everything that
matters is worse than no file: it answers "did we keep evidence?" with yes.

### The demo host now has a voice

`Demo/Program.cs` takes `--log[=<level>]` and passes a small console `ILoggerFactory`
(`Demo/ConsoleLogger.cs`, forty lines, no package) to all three servers. `tests/autobahn.sh`
starts it with `--log=debug`, and the demo also prints one line saying so — "was the instrument
even switched on" is a question a silent log cannot answer.

Debug rather than Warning, because the read-error path is a Debug record, and it costs nothing:
the WebSocket server has two Debug statements in total and logs nothing per frame.

Verified by running the full suite with it on: **128 warning records** where there had been none,
every one of them a correct refusal of a protocol violation the suite deliberately commits —

```
  18:18:06.566  WARN  WebSocketServer: WebSocket protocol violation from 127.0.0.1:56648:
                      Control frame payload length must not exceed 125 bytes!
  18:18:06.687  WARN  WebSocketServer: WebSocket protocol violation from 127.0.0.1:56726:
                      A frame has RSV2 or RSV3 set, but no such extension was negotiated!
```

The first attempt at this proof was inconclusive and worth recording: a two-case slice produced
no records at all, which looked like broken wiring. It was not. The Debug line that seemed
guaranteed — `RemoveConnection`, "Removing HTTP WebSocket connection with …" — is on a method
**nothing calls**. A passing case logs nothing because a passing case has nothing to say.

### Reproduction: failed, 14 attempts — and then the instrument answered

Stated plainly, because a mechanism that fits is not a mechanism that was demonstrated:

- 10 × section 12.4 (18 cases) with the demo host pinned to two CPUs, harsher than the runner's
  four: 18/18 every time, and not one warning in the log.
- 2 full 517-case runs, likewise squeezed: 481/517, 0 hard failures, 128 records each. A third was
  spoiled by a port collision with a slice run concurrently, and is not counted.
- 1 full run unconstrained: the same.
- The CI nightly of 2026-09-23 16:37, the first with the instrument switched on: green — and its
  artifact carries 151 log lines where the failing run of 13:35 carried 22. Its 128 records match
  the local runs exactly, which is a consistency check worth having: the same 517 cases provoke the
  same number of refusals on both machines.

So 12.4.18 is **not diagnosed**. What changed is that the next occurrence names itself: the
record is written, the level captures it, and the file is in the artifact. That is the same move
the HTTP/2 sibling made with `PYTHONUNBUFFERED` after two wrong diagnoses drawn from a flush
boundary, and for the same reason — an intermittent failure is worth one instrument, not three
hypotheses.

`tests/autobahn.sh --cases '12.4.*'` exists for the next attempt: it runs a slice in under a
minute instead of eight. A slice does not carry the floor — a floor is a statement about the
whole suite — but a hard failure in one is still fatal, which is the half that matters when
chasing one case.

### The next occurrence, 2026-09-24 02:37 — and it named itself

Case **12.5.15** this time, not 12.4.18, and the same signature to the letter: `FAILED`,
`droppedByMe: false`, no opcode 8 in either direction, "peer dropped the TCP connection without
previous WebSocket closing handshake", 3342 ms into a case whose own deadline is 480 s.

The log the previous entry added is what turned that into an answer. Three lines, one socket:

```
02:34:56.847   case 12.5.15 starts
02:35:00.198   dbug  Read error on WebSocket connection 127.0.0.1:41346.
               System.ObjectDisposedException: Cannot access a disposed object.
               Object name: 'System.Net.Sockets.NetworkStream'.
                 at WebSocketServerConnection.ReadAsync  …/WebSocketServerConnection.cs:706
                 at AWebSocketServer.RunConnectionAsync  …/AWebSocketServer.cs:1203
02:35:00.210   dbug  ATCPServer: Cleaned up stale client 127.0.0.1:41346.
```

`AWebSocketServer.cs:1203` is one of the exactly two paths that can end that read loop without a
close frame — the one the previous entry found logs at Debug. It is reached because something
disposed the socket underneath an in-flight read, and twelve milliseconds later the TCP server
says what that something was: its Warden reaped the connection as *stale* while the case was
running.

### The cause: a liveness check racing the reader it is checking on

`ATCPServer`'s Warden walks its active connections and closes the ones it believes are gone
(`ATCPServer.cs:570`). What it believes is `TCPConnection.IsConnectionClosed()`:

```csharp
return socket.Poll(0, SelectMode.SelectRead) &&
      (socket.Available == 0);
```

This is the textbook idiom for "has the peer vanished", and it is a **race against the
connection's own reader**. `Poll(SelectRead)` is true when the socket is readable — which means
either data has arrived *or* the peer closed. `Available == 0` is what separates the two. Between
those two statements the WebSocket read loop drains the socket, so the Warden sees "readable, and
nothing available", concludes the peer is gone, and calls `TCPClient.Close()` on a connection that
is very much alive.

Every property of the failure follows from that and none of them had to be guessed:

| observed | because |
|---|---|
| only under load | the window needs a reader actively consuming — section 12.x sends a thousand large compressed messages |
| intermittent, ~1 run in 4 | it is the gap between two statements |
| mid-case, never at a boundary | the Warden runs on its own timer, unrelated to case boundaries |
| no close frame | the socket is already disposed when the loop notices |
| `droppedByMe: false` | correct — the suite did not drop it; we did |
| silent until 2026-09-23 | the record is a Debug one, and the demo host logged into a `NullLoggerFactory` |

**The exposure is wider than WebSocket.** `AHTTPServer : ATCPServer` too, so the same Warden with
the same predicate watches every plain HTTP/1.1 connection this stack serves: a large download, a
keep-alive connection mid-body, an SSE stream. Autobahn found it because section 12 keeps a reader
busier than anything else this repository runs, not because WebSocket is special.

Recorded as **H-25**.

### The fix, and the verification that had to be thrown away first

The Warden no longer asks the socket. It asks the **handler task** — which it already held, and
already awaits two lines further down. Completed means the connection is finished and its entry can
go; running means the connection is owned, and the owner is what notices a peer that went away
(`AWebSocketServer` pings and tears down a silent peer; `AHTTPServer` has its idle and Slowloris
deadlines). The trade is in the safe direction: a half-open connection is now held until its handler
times out, rather than a live one being killed while it works. [Hermod#31](https://github.com/Vanaheimr/Hermod/pull/31).

**The end-to-end evidence did not carry it, and saying so is the point.** Forcing the Warden to a
one-second period — roughly five hundred chances per run instead of eight — and running sections
12.4 and 12.5:

| | |
|---|---|
| old criterion | 1 hard failure (12.4.8) in **4** runs |
| new criterion | 0 in **4** runs |

One in four is the base rate the nightly already had. Four clean runs of anything proves nothing
against it. And the first attempt at that table was worse than inconclusive: the two variants were
copied from Git Bash's `/tmp` while the script ran in WSL, so every `cp` failed silently, six runs
of one build were labelled three-and-three, and all six came back clean. It was caught only because
`cp` printed its errors. The rerun prints the md5 of the source file it installed on every line —
a label that carries its evidence instead of asserting it.

So the verification is a test that asks the predicate directly rather than hoping a 517-case suite
trips it: `HermodTests/TCP/ConnectionLivenessTests.cs` stands up a real `TCPEchoTestServer`, lets a
peer flood it so the handler is genuinely reading, and samples `IsConnectionClosed()` until it lies.
**It lies in 160–250 ms, five runs out of five.** It also asserts that the peer was still connected,
because a "closed" reading on a connection that had really closed would prove nothing at all.

That test asserts a defect deliberately, the way the curl matrix pins an expected failure: if it
ever fails, the predicate stopped lying and the Warden could go back to asking it.

Two further defects turned up in the same corner and are **not** fixed with it, one per commit:
`WardenCheckEvery` is a documented property that the check registration ignores (`EveryMinutes(1,
…)`), which is why the reproduction above needed a source edit rather than a constructor argument;
and `Warden.EverySeconds(N, …)` tests `timestamp.Minute % N` rather than `Second`. **H-26.**

## Reading the report

`tests/autobahn/reports/index.html` (server) and `tests/autobahn/reports-client/index.html`
(client) are the human-readable reports: per case, the frames exchanged and the verdict.
`index.json` beside each is what the scripts parse, and the per-case JSON files are where the
actual handshake is recorded — `httpRequest` and `httpResponse` per case, which is how the
`server_max_window_bits` story above was read off the wire rather than inferred from the case
descriptions, whose wording ("requestMaxWindowBits") names the parameter differently.

The demo host's own log is kept beside the server report as `demo-host.log`, because it is the
only view of a failure from our side of the wire — see the section above for what it took to make
that sentence true. For the client run the equivalent is the
fuzzingserver container's log, whose tail the script prints.
