# Testing against intermediaries (A5)

Reverse proxies are the strictest HTTP/1.1 consumers there are. They are also
the only shape in which the findings of [A6](TestingAgainst_Smuggling.md) are
real: that work found seven probes on which Hermod, Go's `net/http` and Node's
`node:http` place the end of a message differently, and a disagreement only
becomes an attack when two implementations are chained. Here they are chained.

Five of them, all pulled rather than built — a deliberate contrast with the
HTTP Garden, which compiles every target from source with clang and ASan and
is measured in compiler-hours:

| | version | port |
|---|---|---|
| nginx | 1.27-alpine | 18101 |
| HAProxy | 3.0-alpine | 18102 |
| Caddy | 2-alpine | 18103 |
| Apache httpd | 2.4-alpine | 18104 |
| Envoy | v1.31 | 18105 |

```bash
tests/proxy.sh                     # up, run, down
tests/proxy.sh --keep              # leave them running
tests/proxy.sh --only nginx
tests/proxy.sh --filter framing    # curl | framing | poison | via
tests/proxy.sh --update            # rewrite tests/proxy-known.txt
```

## Where the demo is, which is the one awkward part

Not in the compose network. `DEMO_HOST` is supplied by `tests/proxy.sh` and
differs by platform:

- **Linux** — the demo runs on the host and the containers reach it through
  `host.docker.internal`, aliased to `host-gateway` in the compose file.
- **Windows** — the demo runs on *Windows* and Docker runs inside the WSL VM.
  `host.docker.internal` would resolve to the VM, which is the wrong machine.
  `DEMO_HOST` is then the VM's default route, which *is* the Windows host, and
  the demo has to have been started with `--bind-any` or nothing outside
  loopback can reach it.

That one address is the whole of the difference, and the compose file takes it
as an environment variable so that nothing else has to know.

---

## What is measured, and how much each part is worth

### curl, through each proxy

The whole of [`tests/curl-matrix.sh`](../tests/curl-matrix.sh) — 78 checks —
run directly and then through each chain. Differences are expected: a proxy is
an HTTP endpoint on both sides and rewrites a great deal. Each one is recorded
by name in [`tests/proxy-known.txt`](../tests/proxy-known.txt), and a *new*
one fails the run.

Direct is 78/78, which is what makes the rest of the column meaningful: a
difference below is the proxy's doing and not a pre-existing failure.

The differences cluster into four recognisable behaviours, and none of them is
a Hermod defect:

| what | nginx | HAProxy | Caddy | httpd | Envoy |
|---|---|---|---|---|---|
| answers an HTTP/1.0 request as 1.1 | ✔ | | | ✔ | ✔ |
| refuses or rewrites `OPTIONS *` | ✔ | | ✔ | ✔ | ✔ |
| does not forward an `Upgrade` without being configured for it | ✔ | | | ✔ | ✔ |
| keeps the client connection for an HTTP/1.0 request | | ✔ | ✔ | | |
| refuses an unregistered method (`QUERY`, RFC 10008) | | | | | ✔ |

### framing, direct against each chain

`tests/h1desync --observe` through each proxy, against the same run made
directly, in the vocabulary A6 established — `REJECT`, `ONE`, `TWO`, `NONE`.
This says what each proxy *does* with an ambiguously framed message: forwards
it, normalises it, refuses it, or splits it into two requests.

It is the most informative column and the least judgemental: a row here is a
fact about the proxy, recorded rather than graded.

Three readings worth keeping:

- **`chunk-bws` goes through all five.** A chunk-size followed by a bare space
  — `5 \r\n`, where the grammar is `1*HEXDIG` and an extension must start with
  `;`. Hermod alone answers 400; every one of the five forwards it in a shape
  that gets the hidden request executed. A6 had already found Go accepting it;
  it turns out to be near-universal.
- **Caddy splits `cl-te` and `te-cl` into two requests.** Caddy is built on
  Go's `net/http`, and A6 measured exactly this behaviour in Go directly. The
  same finding arriving through a different door.
- **Apache answers 500 where Hermod answers 400.** For the three
  chunked-twice spellings, Hermod refuses with `400` and closes — the
  behaviour [H-29](../PLAN.md) added this morning — and Apache turns that
  upstream close into a `500 Internal Server Error` of its own. Apache's
  translation, not ours; visible only *because* H-29 was fixed, since before
  it Hermod would have answered `200` and then served the hidden request.

### poison — the attack itself

**This is the part to read carefully.**

The response count on the attacking connection answers the wrong question
through a proxy. Two responses mean the *front end* saw two requests and
forwarded both — that is pipelining, and a security policy on that proxy saw
both. The attack is the front end seeing one where the back end sees two, and
on the attacking connection that is invisible.

Where it shows is the *next* connection: the extra response sits in the
proxy's pooled upstream connection and is handed to whoever asks next. So
[`tests/proxy-poison.py`](../tests/proxy-poison.py) sends the payload, then
asks innocent questions on fresh connections and checks whether an answer
comes back that belongs to somebody else.

No chain poisons. **And that result is weaker than it looks**, which is worth
more than the result:

> Calibration, 2026-09-27. A back end was built that answers one request with
> two responses — the exact state a successful desync leaves behind, verified
> on the wire: one request in, `200 OK` and `418 I'm a teapot` out. Put behind
> each of the five proxies in turn, **all five discarded the upstream
> connection rather than hand the extra response to the next client.**

So the detector has never been seen to fire through any of these five. "Clean"
therefore means *"no chain here produced an attack, and these five would have
absorbed one in any case"* — a real statement about the chain, a weak one
about the origin. The calibration is
[`tests/proxy-calibrate.py`](../tests/proxy-calibrate.py), kept alongside the
suite but not run by it: its job was to answer this question once, and it is
here so the answer can be rechecked rather than believed.

It stays in the suite regardless. It is the right instrument, it is cheap, and
the day a proxy is added that does *not* absorb, it starts meaning something.

### via — the intermediary-facing rules that are visible from outside

A chunked body arriving intact, the trailer section surviving a re-framing
hop, `Connection: close` being honoured downstream. Dropping a trailer is
allowed, so that is recorded rather than demanded — Apache does.

---

## What A5 establishes, and what it does not

**It establishes** that the demo host works behind five widely deployed
reverse proxies, that every difference from the direct run is attributable to
the proxy, and that none of the seven A6 disagreements turns into a poisoned
connection through any of them.

**It does not establish** that Hermod cannot be smuggled through *some* proxy.
Five is not all of them, the payload set is the seven shapes A6 found, and —
the important one — the detector is unexercised, because these five proxies
protect against the final step whatever the origin does.

The honest summary is that the chain is safe for two independent reasons,
neither of which was arranged: Hermod refuses the ambiguous framings and
closes, and the proxies would have absorbed the consequences if it had not.

---

## One run in five that nobody can explain

Of five runs made while building this, four were 32/32 and one was 14/32 —
everything from `poison/haproxy` onwards, including every `via` check.

It has not been reproduced. The run that failed was the one whose predecessor
had been killed mid-flight, and the four since — two against an externally
started demo, two with the driver starting its own — have been clean. Three
explanations were tested and all three are wrong:

- **Not Hermod under proxy load.** The same workload against the same binary
  passes repeatedly, and the demo was still answering afterwards.
- **Not a stale upstream pool.** Killing the demo that all five proxies were
  pooled against and starting a new one: all five answered 200 immediately.
- **Not the driver owning the demo's lifetime.** The scenario was re-run
  exactly, driver-started demo and all, and came back 32/32.

So it stands as unexplained, most plausibly debris from a force-stopped
predecessor, and it is written down rather than rounded off — four green runs
do not make the fifth not have happened.

What *was* done about it is not a fix. The eighteen failures said nothing
about their common cause, so `tests/proxy.sh` now checks that the demo still
answers between sections and stops with

    the demo host stopped answering on :8080 after the framing section
    everything below this point would fail for that one reason

keeping the demo's log instead of deleting it. That turns the next occurrence
into a diagnosis rather than eighteen things to check by hand. It makes the
suite no more correct and considerably easier to believe.

## Two traps, recorded because both cost time

**`MSYS_NO_PATHCONV=1` must not be exported.** It is needed so that Git Bash
does not rewrite `/mnt/d/...` before `wsl.exe` sees it — without it the path
arrived as `C:/Program Files/Git/mnt/d/...`. Exported globally it also stops
Git Bash translating `/dev/null` for native Windows binaries, so every
`curl -s -o /dev/null` in the driver began to fail. The first of those is the
check for the demo host, which then reported the demo as down while it was
answering `200` in the next shell along. It is per-command now. The same
`/dev/null` trap this repository's `.gitignore` already carries a note about.

**A counter that is wrong in two opposite ways.** The first version of
`proxy-poison.py` reported the status codes on the attacking connection by
anchoring `HTTP/1.` to the start of a line — and so missed the second response
whenever the first one's body did not end in CRLF. That is the exact mistake
already made and fixed in `Checks.ResponseCount`, where the *other* cheap
trick — counting substrings — over-counts instead, because the demo's error
responses say `Server: Hermod HTTP/1.1 Demo`. Doing it correctly means walking
the framing. The verdict here needs only the first status of a single-response
follow-up, so the wrong thing was removed rather than the walker duplicated;
`tests/h1desync` remains the place that counts properly.
