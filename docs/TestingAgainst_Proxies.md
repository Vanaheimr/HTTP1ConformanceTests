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
| nginx | 1.31-alpine | 18101 |
| HAProxy | 3.4-alpine | 18102 |
| Caddy | 2-alpine | 18103 |
| Apache httpd | 2.4-alpine | 18104 |
| Envoy | v1.39-latest | 18105 |

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

The whole of [`tests/curl-matrix.sh`](../tests/curl-matrix.sh) — 99 checks —
run directly and then through each chain. Differences are expected: a proxy is
an HTTP endpoint on both sides and rewrites a great deal. Each one is recorded
by name in [`tests/proxy-known.txt`](../tests/proxy-known.txt), and a *new*
one fails the run.

Direct is 99/99, which is what makes the rest of the column meaningful: a
difference below is the proxy's doing and not a pre-existing failure.

The differences cluster into recognisable behaviours, and none of them is
a Hermod defect:

| what | nginx | HAProxy | Caddy | httpd | Envoy |
|---|---|---|---|---|---|
| answers an HTTP/1.0 request as 1.1 | ✔ | | | ✔ | ✔ |
| refuses or rewrites `OPTIONS *` | ✔ | | ✔ | ✔ | ✔ |
| does not forward an `Upgrade` without being configured for it | ✔ | | | ✔ | ✔ |
| keeps the client connection for an HTTP/1.0 request | | ✔ | ✔ | | |
| refuses an unregistered method (`QUERY`, RFC 10008) | | | | | ✔ |
| removes the upstream's `Alt-Svc` | | | ✔ | | |
| asks the upstream for gzip on its own, decodes it, and keeps the gzip variant's `ETag` and digests | | | ✔ | | |
| drops a `103` the upstream sent | ✔ | | | | ✔ |
| sends HTTP/1.1 upstream for an HTTP/1.0 client, so the origin sends a `103` | ✔ | | ✔ | ✔ | |
| hands that `103` to the HTTP/1.0 client | | | ✔ | | |

**The last row, since 2026-10-28**, is the one where the odd proxy out is the
one that is right. The demo's `/alt-svc` announces `http%2F1.1=":8443"`, and an
alternative with no host names the host of the origin *the client sees*
(RFC 7838 §2.1). Through a proxy that is the proxy's host, and nothing listens
on its `:8443`: nginx, HAProxy, httpd and Envoy hand the client a claim that
stopped being true at the hop. Caddy lists `Alt-Svc` in its `hopHeaders` and
strips it in both directions (`modules/caddyhttp/reverseproxy/reverseproxy.go`).
RFC 9110 §7.6.1 does not make it hop-by-hop, so this is Caddy's judgement rather
than a requirement — but it is the judgement that keeps the client's view
consistent, and the four that pass it through are relaying an origin's
statement about itself as though it were their own.

**The row below it, since 2026-10-29**, is Caddy again, and this time it is
the odd one out for the wrong reason. A client that sends no `Accept-Encoding`
gets identity directly and through four of the proxies. Caddy 2.11.4 asks the
demo for gzip itself, decodes the answer and sends the identity octets on,
chunked. It removes `Content-Encoding` and `Content-Length`, and leaves
everything else the gzip variant came with:

| through Caddy, no `Accept-Encoding` | what it names |
|---|---|
| `ETag: "prose-1-gzip"` on `/prose` | the gzip representation; the identity one is `"prose-1"` |
| `Content-Digest: sha-256=:fsHkW+Qj…:` on `/digest` | the 579 gzip octets, while 10 290 identity ones arrive |
| `Repr-Digest`, the same | the same |

Hermod computes all three over the gzip octets, as RFC 9530 asks of it. They
become false at the hop. RFC 9110 §7.7 lets a proxy transform content, and
RFC 9530 says nothing about what an intermediary owes the digest fields
when it does. It names "transforming proxies" among what the fields exist
to detect, and this one is detected. That has a concrete consequence for
Hermod's own client. With `VerifyDigests` on and `AutomaticDecompression`
left off, which is the default, it sends no `Accept-Encoding`. Behind Caddy,
every digested response the origin would have compressed then reaches it
as a `ClientError`. That follows from the bytes above and the mismatch path
`DigestFieldsTests` pins, and it has not been run. And a strong `ETag` that belongs to one representation
is attached to another. Recorded as Caddy's in `tests/proxy-known.txt`: the identity
digest check and the bare-key one, both of which request identity. The ETag
row, `the identity one keeps the plain ETag`, has been there since
2026-09-27 without its reason. This is the reason.

**The three rows about `103`, since 2026-10-30**, are what each proxy does
with an interim response. The demo's `/early-hints` sends a `103` before its
`200` unless the request is HTTP/1.0, and says in the body whether it did.
RFC 9110 §15.2 asks two things of a proxy here: it MUST forward a `1xx` it
did not itself ask for, and, being a server to its client, it MUST NOT send
one to an HTTP/1.0 client.

| | HTTP/1.1 client gets | HTTP/1.0 client gets | the origin, for that 1.0 client |
|---|---|---|---|
| HAProxy 3.4.6 | `103`, `200` | `200` | sent no `103`: the request arrived as HTTP/1.0 |
| httpd 2.4.68 | `103`, `200` | `200` | sent one, and httpd withheld it |
| nginx 1.31.6 | `200` | `200` | sent one, and nginx dropped it |
| Envoy 1.39.2 | `200` | `426` | not reached: this chain refuses HTTP/1.0 |
| Caddy 2.11.4 | `103`, `200` | **`103`, `200`** | sent one, and Caddy passed it on |

HAProxy and httpd meet both requirements. nginx and Envoy drop the `103` for
every client, and both do it by configuration. nginx has passed a `103` on
only under its `early_hints` directive, which arrived in 1.29.0 and which
this chain does not set; the example in nginx's own documentation enables it
for HTTP/2 and HTTP/3 alone, which is RFC 8297 §3's caution about HTTP/1.1
clients taken as policy. Envoy forwards `100`, `102`, `103` and `104` only
with `proxy_100_continue`, which this chain does not set either
(`HeaderUtility::isSpecial1xx`, and the assertion in
`FilterManager::encode1xxHeaders`). As configured here, neither meets the
MUST to forward, and each is one configuration line away from it.

**Caddy is the only one that breaks a rule as shipped.** It speaks HTTP/1.1
upstream, as RFC 9112 §2.3 has an intermediary do, so the origin sends the
`103`, and Caddy writes it to the HTTP/1.0 client as `HTTP/1.0 103 Early
Hints`. The fault is in Go rather than in Caddy. Caddy's reverse proxy relays
a `1xx` through `ResponseWriter.WriteHeader`, and Go's `net/http` server
writes any `1xx` other than `101` without checking the request's version. The
version only decides whether the status line says `HTTP/1.0` or `HTTP/1.1`:
`writeStatusLine(w.conn.bufw, w.req.ProtoAtLeast(1, 1), code, …)` in
`server.go`. Measured against Go directly: on go1.24.4, a handler that calls
`WriteHeader(103)` sends `HTTP/1.0 103 Early Hints` to `curl --http1.0`. On
go1.27.3, the latest release on 2026-10-31, it does the same, and so does
`httputil.ReverseProxy` in front of it. The same line is on Go's `master`, and
the Caddy image is built with go1.26.3. It is the second MUST that Go's
`net/http` misses in this repository, after A6's CL.TE connection that is not
closed. Neither is ours to fix. This one is filed as
[golang/go#82743](https://github.com/golang/go/issues/82743) (2026-10-31); the
CL.TE one is not.

The third column is a difference and not a defect. nginx, Caddy and httpd
send their own version upstream, so behind them the origin sees HTTP/1.1 and
sends a `103`, and keeping it from the HTTP/1.0 client becomes the proxy's
job. HAProxy forwards the
request as the HTTP/1.0 it was, which is the only one of the five where the
origin's own rule does the protecting.

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
