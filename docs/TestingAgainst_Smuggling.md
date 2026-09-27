# Testing against request smuggling (A6)

RFC 9112 §11.2 is two paragraphs long and says almost nothing operational:

> Request smuggling is a technique that exploits differences in protocol
> parsing among various recipients to hide additional requests … within an
> apparently harmless request.

The operative word is **differences**. A single origin server cannot smuggle a
request past itself. The attack is a disagreement — one parser reads one
message where the next reads two, and nobody authorised the second one — so an
instrument pointed at one implementation can, in principle, never see it.

That is the whole difficulty of testing for this, and it is why A6 took four
instruments rather than one.

| | What it asks | Where it runs |
|---|---|---|
| [`tests/h1attack`](../tests/h1attack) | can *our* server be desynchronised by the classic shapes? | push gate |
| [`tests/h1desync`](../tests/h1desync) | given an ambiguous message, does our server do what RFC 9112 **requires** — where it requires anything? | push gate |
| [`tests/smuggle.sh`](../tests/smuggle.sh) | do Hermod, Go and Node place the end of a message in the same place? | nightly, `--peers` locally |
| [`tests/smuggler.sh`](../tests/smuggler.sh) | the same question from payload sets nobody here designed | nightly |
| [`tests/http-garden.sh`](../tests/http-garden.sh) | the same question against forty-five parsers, compared field by field | by hand |

---

## The distinction the whole thing turns on

RFC 9112 states a **MUST** for some ambiguities and deliberately leaves others
open. `tests/h1desync` keeps the two apart, and the separation is not
presentation — it decides what may be asserted.

**Where there is a rule, it is asserted.** 24 of the 38 probes carry a
normative sentence, quoted in [`ProbeCatalogue.cs`](../tests/h1desync/ProbeCatalogue.cs)
next to the payload it governs:

| Rule | Probes |
|---|---|
| §6.3 item 4 — a Transfer-Encoding whose final coding is not chunked: **MUST 400** and close | `te-identity` `te-chunked-gzip` `te-gzip` `te-unknown` |
| §6.3 item 5 — an invalid Content-Length: **MUST** treat as an unrecoverable error | `cl-dup-diff` `cl-list-diff` `cl-plus` `cl-hex` `cl-negative` `cl-overflow` |
| §5.1 — whitespace between field name and colon: **MUST** reject with 400 | `ws-colon-cl` `ws-colon-te` `ws-colon-htab` |
| §7.1 — chunk-size is `1*HEXDIG`, and large numerals must not overflow | `chunk-plus` `chunk-hex-0x` `chunk-bws` `chunk-overflow` |
| §6.1 — CL+TE: either answer is allowed, but the server **MUST close** afterwards | `cl-te` `te-cl` |
| §6.1 — an HTTP/1.0 message with Transfer-Encoding: **MUST** treat the framing as faulty and close | `http10-te` |
| §5.2 — an obs-fold in a request: **MUST** reject, or replace with SP (which here makes the coding not-chunked, so §6.3 item 4 takes over) | `obs-fold-te` |
| §5.1, RFC 9110 §16.10 — OWS and case are legal, so these **are** chunked and **must** be decoded | `te-ows-spaces` `te-ows-htab` `te-mixed-case` |

Hermod: **24/24**.

The last row is the counterweight and it is not decorative. A harness that
only ever demands rejection gives its best score to a server that rejects
everything, which is not conformance either.

**Where there is no rule, nothing is asserted.** The other 14 probes are
observed and reported. §6.1 says a server

> MAY reject a request that contains both Content-Length and Transfer-Encoding
> or process such a request in accordance with the Transfer-Encoding alone

— so an assertion there would be this repository's taste wearing conformance's
clothes. Those probes are not dropped, because they are precisely the ones a
smuggling chain is built from. They go to the differential instead.

---

## The differential

`tests/smuggle.sh` runs `h1desync --observe` against three implementations and
joins the answers on the probe id. The vocabulary is small on purpose — two
servers will never produce identical bytes, so comparing responses would
report a difference on every probe and mean nothing:

| | |
|---|---|
| `REJECT` | one response, and it refuses the message |
| `ONE` | one response: the whole payload was read as one message |
| `TWO` | two responses: the peer found a second request inside it |
| `NONE` | nothing came back |

A trailing `!` means the peer closed the connection.

**28 of 38 rows agree. The other ten, as measured on 2026-09-27** against
go1.24.4 and Node's `node:http`:

| Probe | hermod | go | node | |
|---|---|---|---|---|
| `cl-te` | `REJECT[400]!` | `TWO[200,404]` | `REJECT[400]!` | Go serves the hidden request |
| `te-cl` | `REJECT[400]!` | `TWO[200,404]` | `REJECT[400]!` | " |
| `te-dup` | `TWO[200,418]` | `REJECT[501]!` | `REJECT[400]!` | **H-29** |
| `te-obf-sp` | `TWO[200,418]` | `REJECT[501]!` | `REJECT[400]!` | **H-29** |
| `te-chunked-chunked` | `TWO[200,418]` | `REJECT[501]!` | `REJECT[400]!` | **H-29** |
| `cl-dup-same` | `REJECT[400]!` | `TWO[200,404]` | `REJECT[400]!` | we are stricter than required |
| `http10-te` | `REJECT[400]!` | `ONE[200]!` | `REJECT[400]!` | |
| `chunk-bws` | `REJECT[400]!` | `TWO[200,404]` | `REJECT[400]!` | Go accepts `5 ` as a chunk-size |
| `chunk-trailer-cl` | `REJECT[400]!` | `TWO[200,404]` | `REJECT[400]!` | |
| `lf-only-headers` | `REJECT[400]!` | `TWO[200,404]` | `REJECT[400]!` | permitted by §2.2 |

These are recorded in [`tests/smuggle-known.txt`](../tests/smuggle-known.txt).
A row that is not in that file fails the run — including a row whose
disagreement changed shape — and a row that stops disagreeing is reported
loudly so the line can be deleted. It is the bargain
[`tests/autobahn.sh`](../tests/autobahn.sh) strikes with its floor and
`tests/h1fuzz` with its known findings, and it is what keeps an instrument
whose normal output is "ten disagreements" from being one nobody reads.

### What Go's rows say

`cl-te` and `te-cl` are the classic CL.TE and TE.CL shapes, and Go answers
both with **two responses on an open connection**. Verified by hand, outside
the harness, against go1.24.4:

```
POST /echo HTTP/1.1 / Host / Content-Length: 6 / Transfer-Encoding: chunked
0\r\n\r\nGET /status/418 HTTP/1.1\r\nHost\r\n\r\n

HTTP/1.1 200 OK … Content-Length: 0
HTTP/1.1 404 Not Found … 404 page not found        ← the hidden request, served
(connection still open)
```

Processing it per Transfer-Encoding alone is explicitly permitted. Leaving the
connection open is not:

> A server MAY reject a request that contains both Content-Length and
> Transfer-Encoding or process such a request in accordance with the
> Transfer-Encoding alone. **Regardless, the server MUST close the connection
> after responding to such a request to avoid the potential attacks.**
> — RFC 9112 §6.1

`chunk-bws` is the same shape from a different door: Go accepts `5 ` — a
chunk-size followed by a bare space — where `chunk-size = 1*HEXDIG` and an
extension has to begin with `;`. It reads the body and serves what follows as
a pipelined request.

`lf-only-headers` is **not** a defect: §2.2 says a recipient MAY recognise a
single LF as a line terminator. It is a divergence, and a divergence is
exactly as useful as a defect for building a chain.

None of this is Hermod's bug, and none of it would have been visible from
anything pointed at Hermod alone. That is the argument for the instrument.

### What our rows say — H-29

`te-dup`, `te-obf-sp` and `te-chunked-chunked` are one finding wearing three
hats. All three reduce, via RFC 9110 §5.3's rule that repeated field lines
combine, to

    Transfer-Encoding: chunked, chunked

which §6.1 forbids a sender to produce:

> A sender MUST NOT apply the chunked transfer coding more than once to a
> message body (i.e., chunking an already chunked message is not allowed).

There is no matching recipient requirement — §6.1 is about senders, and §6.3
item 4 does not fire because chunked *is* the final coding — so Hermod is not
violating anything. What it is doing is accepting a framing that no conforming
client may send, on the one field request smuggling is made of, while its two
peers refuse it (Go: 501, Node: 400).

The line is [`AHTTPPDU.cs:422`](../libs/Hermod/Hermod/HTTP1/AHTTPPDU.cs):

```csharp
public Boolean IsChunkedTransferEncoding
    => TransferEncoding is not null &&
        TransferEncoding.Split(",", …)
                       .LastOrDefault()
                       ?.Equals("chunked", StringComparison.OrdinalIgnoreCase) == true;
```

Only the **last** coding is looked at. That is right for `chunked, gzip` —
which is why `te-chunked-gzip` correctly gets its 400 — and it silently drops
the duplicate for `chunked, chunked`. Filed as **H-29**, P3: strictness is
free here, and there is no legitimate client to break.

---

## The payload sets nobody here designed

38 probes we wrote are 38 probes we thought of. `tests/smuggler.sh` runs two
tools that were written by people whose job was finding desyncs in servers
they did not write.

**[smuggler](https://github.com/defparam/smuggler)** sweeps 134 obfuscations
of the Transfer-Encoding line, each tried as CL.TE and as TE.CL: the byte
space around the field name and the colon — `0x01`, `0x0b`, `0x0c`, `0x7f`,
`0xa0`, `0xff` before it, after it, inside it, folded around an unrelated
`X: X`. Detection is by **timing**, not status code: a desynced server waits
for a body that never arrives. **134/134 probed, nothing found.**

One result there is worth keeping. The `xprespace` family hides the field
behind `X: X<byte>Transfer-Encoding: chunked`. Hermod answers **400** for
`0x7f` and **200** for `0xa0` and `0xff`, and that is the RFC read correctly
on a detail that is easy to get backwards: RFC 9110 §5.5 admits `obs-text`
(`%x80-FF`) into a field value and does not admit DEL. So the two high bytes
make one long legal value with no Transfer-Encoding in it, and `0x7f` makes
the field invalid.

**[h2csmuggler](https://github.com/BishopFox/h2csmuggler)** asks whether an
HTTP/1.1 upgrade to h2c is honoured and can be tunnelled through. It **must**
find nothing — this server implements no h2c upgrade — and that is the point
of running it: the negative is now pinned. If h2c support is added later
without the guards, this is the line that notices.

Both are checked out on demand into `tests/thirdparty/` (gitignored) and
pinned to a commit.

### One trap, worth recording

A tool that did not run reports no findings, and so does a tool that ran and
found nothing. The first version of `tests/smuggler.sh` printed a green
"no CL.TE or TE.CL issue reported" for a run that had died on its first line
with `Cannot find config file`.

So the no-findings check is now gated on coverage, and coverage is **counted**
rather than assumed: the expected number comes from the config file itself,
which is plain Python filling a `mutations` dict and can be counted by running
it with a stub for the one class it imports. `134/134` for the default set,
`966/966` for `--config doubles`, and nothing hard-coded to go stale. Pointing
it at a dead port now produces two red checks saying "not established", where
the first version said green.

---

## The Garden

[The HTTP Garden](https://github.com/narfindustries/http-garden) is forty-five
HTTP implementations in Docker, and it compares something much sharper than a
response count: every target answers with a JSON description of the request
**as it parsed it** — method, version, URI, header fields, body — so a
disagreement is visible field by field.

`tests/http-garden/` is Hermod as a Garden target: a Dockerfile on the
Garden's own pattern (`FROM http-garden-soil`, everything parametrized by
`APP_REPO`/`APP_VERSION`, plus a `STYX_VERSION` because Hermod references
Styx) and `HermodGarden`, which runs Hermod's HTTP/1.1 server on
`0.0.0.0:80` and answers in that format. `tests/http-garden.sh` installs it
into a checkout, registers the service, builds, and drives the Garden's repl.

**It is neither a gate nor a nightly, and the reason is cost.** The Garden
builds every target from source with clang and ASan: nginx, Apache, HAProxy,
Envoy, Tomcat and the rest are compiler-hours and gigabytes, once. Run it
deliberately and read the result by hand.

```bash
tests/http-garden.sh --build              # the soil image, then ours
tests/http-garden.sh --contract           # does our target still answer?
tests/http-garden.sh --build --with nginx
tests/http-garden.sh --targets "hermod nginx"
tests/http-garden.sh --stop
```

### What it looks like when it runs

Hermod against `tornado`, 2026-09-27, five payloads. The Garden prints each
target's parse tree and then clusters the targets that agree:

```
garden> payload 'POST /echo HTTP/1.1
Host: a
Transfer-Encoding: chunked
Transfer-Encoding: chunked

0

' | fanout | cluster
hermod: [
    HTTPRequest(
        method=b'POST', uri=b'/echo', version=b'1.1',
        headers=[
            (b'host', b'a'),
            (b'transfer-encoding', b'chunked, chunked'),
        ],
        body=b'',
    ),
]
tornado: [
    HTTPResponse(version=b'1.1', method=b'400', reason=b'Bad Request'),
]
    0. hermod
    1. tornado
```

Two clusters is a disagreement. This is **H-29** again, from a third
implementation and with the mechanism visible rather than inferred: the parse
tree shows Hermod combining the two field lines into `chunked, chunked` — RFC
9110 §5.3, correctly — and then accepting the result.

Of the five payloads, `cl-te` and `chunk-bws` put both in one cluster (both
400), `te-dup` and `te-chunked-chunked` split on H-29, and `cl-dup-same`
splits the other way: Hermod 400s the two agreeing Content-Lengths that
Tornado accepts. Every one of those matches what `tests/smuggle.sh` said, from
an instrument that shares no code with it.

`--contract` exists because everything else depends on Hermod producing the
JSON the Garden compares. If it stopped — a change to the routing, to the
response builder, to anything — every differential would read as "hermod
disagrees with everyone", which is what a real and interesting finding looks
like too.

### Two limitations of this target, stated rather than discovered

**Header order and duplicate field lines are not reported.** Hermod's parsed
header representation is a case-insensitive dictionary: it has no order, and
repeated field lines are combined at parse time — which is what RFC 9110 §5.3
says to do, and which means the information is gone by the time a handler sees
it. `HermodGarden` emits the fields sorted by name so the output is at least
deterministic. The cost: an ordering difference between this target and
another is an artefact, and a payload whose interest is the *order* of two
fields, or the difference between `A: 1, 2` and two `A:` lines, is invisible
here. Payloads whose interest is the value, the name, or whether a field was
accepted at all — most of them — are not affected.

**An exotic method is answered 405 rather than described.** Hermod routes on a
parsed `HTTPMethod`, so the target registers a catch-all path for eight known
methods. A 405 still means the request parsed, but it carries no JSON, so
payloads whose interest is the method itself are not covered.

**Two things about the checkout that are load-bearing and documented
nowhere.** `tools/targets.py` hardcodes `_NETWORK_NAME = "http-garden_default"`,
and Compose derives the project — and so the network — from the directory
name. A checkout in `/tmp/hg` gives `hg_default`, at which point the repl
finds no containers, prints one warning naming all fifty services, and then
answers every payload with nothing: an empty result that looks like a result.
`tests/http-garden.sh` refuses a `--garden-dir` whose basename is not
`http-garden`. And the port a target is reached on is
`x_props.get("port", 443 if requires_tls else 80)` — most targets are
plaintext on 80, so ours is too, and `GARDEN_TLS=1` moves it to 443 for a run
that wants TLS (the service then needs `requires-tls: true`).

**There is no ASan.** Every other target in the Garden is compiled with
clang's sanitizers; there is no equivalent for managed code. What this image
has instead is the .NET runtime's own bounds checking. It is a different
instrument and a weaker one for memory-safety findings — which, for a stack
with no unsafe blocks, is the trade one would make anyway.

---

## Running it

```bash
tests/run-tests.sh --filter desync         # the 24 assertions, no peers needed
tests/run-tests.sh --peers --filter smuggle
tests/smuggle.sh --base http://127.0.0.1:8080
tests/smuggle.sh --update                  # rewrite the known-disagreements file
tests/smuggler.sh                          # starts its own demo host
tests/smuggler.sh --config exhaustive
tests/http-garden.sh --contract
```

`tests/h1desync` is in the push gate; it needs nothing but the demo. The
differential needs Go and Node, so it is opt-in locally (`--peers`, which
`--wsl` implies) and a nightly on `ubuntu-latest`. The third-party tools clone
from GitHub on first run, which is why they are a nightly rather than a gate:
a push gate that reaches the network for two repositories fails for reasons
that have nothing to do with the push.
