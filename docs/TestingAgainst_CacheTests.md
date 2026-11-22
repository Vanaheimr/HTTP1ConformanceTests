# Testing against cache-tests

[cache-tests](https://github.com/http-tests/cache-tests) is the suite behind
[cache-tests.fyi](https://cache-tests.fyi/), Mark Nottingham's survey of how
HTTP caches behave: browsers, reverse proxies and CDNs, about 480 tests over
RFC 9111 and its neighbours. Since H-5 (2026-11-21, Hermod#286) Hermod's
HTTP/1.x client has a cache, `AHTTPClient.Cache`, and this is that cache
measured by somebody else's suite.

```bash
tests/cache-tests.sh                     # shared cache, gated on the record
tests/cache-tests.sh --mode private      # a private cache, reported, not gated
tests/cache-tests.sh --mode none         # no cache: calibrates the run
tests/cache-tests.sh --id freshness-none # one test, with its requests dumped
tests/cache-tests.sh --update            # rewrite the record, keeping its notes
```

About 70 seconds. Node 22.19 or newer and npm, and `dotnet build HTTP1.slnx`
for the proxy. The suite is cloned into `tests/thirdparty/cache-tests/`
(gitignored), pinned to `3942fbfe` (2026-11-17).

## The shape of the run

The suite tests a cache the way a CDN is tested. Its client sends every
request to the cache as if it were the origin. The cache forwards what it
cannot answer to the suite's own origin, a Node server that answers each test's
requests as the test configured them and counts what reaches it. A response
from the cache is one whose `Server-Request-Count` says the origin never saw
that request.

Hermod is not a proxy, so `tests/cache-proxy/` makes one, as thin as it can
be: an `HTTPServer` whose every route hands the request to an `HTTPClient`,
and every client shares one `HTTPClientCache`.

```
cache-tests client ─▶ HermodCacheProxy :8001 ─▶ HTTPClient + HTTPClientCache ─▶ origin :8000
```

What is measured is `HTTPClientCache`, unchanged. The plumbing has properties
of its own, and they are written down in `Program.cs` rather than discovered:

- **One cache, many clients.** `AHTTPClient` sends one request at a time, and
  the suite runs 25 tests at once, some with origin answers paused for
  seconds. Each request rents an idle client or makes one.
- **No retries.** The suite reports a retry as a result of its own (↻).
  `MaxNumberOfRetries: 1`, which is one attempt: the parameter counts attempts
  (H-48).
- **Field lines in, field lines out**, from the raw header block in both
  directions. Repeated lines are combined with `", "`, because Hermod's builders
  hold one value per name.
- **Hop-by-hop fields stay on their hop** (RFC 9110 §7.6.1).
- **No interim response is forwarded.** The client consumes a `1xx` and hands
  back the final response. The four `interim-*` results are the proxy's.
- A request that fails outright is a `502`, unless the cache serves stale in
  its place.
- **`Hermod-Cache`** on each response says what the cache did: `Hit`, `Stale`,
  `Revalidated`, `Miss` or `Unavailable`, and nothing when it was bypassed. The
  suite ignores the field. Its traces in `artifacts/cache-tests/shared.json`
  show it beside every answer.

## Reading the result

The suite's own CLI writes one raw outcome per test, `true` or the assertion
that failed. Counting the `true`s would mix four different things together, so
`tests/cache-proxy/classify.mjs` classifies each test with the suite's own
`determineTestResult`, the function cache-tests.fyi renders with:

| | |
|---|---|
| `pass` / `fail` | a **required** test: `fail` is a conformance question |
| `pass` / `optional_fail` | an **optimal** test: a missed optimisation, which the RFCs allow |
| `yes` / `no` | a behaviour **check**: neither right nor wrong |
| `setup_fail` | the test could not set up what it measures |
| `dependency_fail` | a test it depends on did not pass, so it says nothing of its own |
| `untested` | not run against a shared cache: browser-only tests |

## Results, 2026-11-22, Hermod `810fdfbd`

| | Hermod | nginx | Squid | ATS | httpd | Varnish | Caddy | HAProxy | Fastly |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| pass | **272** | 198 | 219 | 259 | 251 | 225 | 198 | 180 | 205 |
| yes | 89 | 40 | 103 | 81 | 69 | 41 | 50 | 60 | 46 |
| no | 33 | 57 | 30 | 42 | 55 | 63 | 50 | 37 | 59 |
| **fail** (required) | **5** | 31 | 27 | 18 | 16 | 17 | 13 | 19 | 37 |
| optional_fail | 26 | 33 | 52 | 28 | 33 | 45 | 66 | 55 | 42 |
| setup_fail | 6 | 12 | 7 | 4 | 8 | 12 | 8 | 7 | 3 |
| dependency_fail | 44 | 99 | 37 | 42 | 36 | 72 | 90 | 117 | 78 |
| other | 5 | 10 | 5 | 6 | 12 | 5 | 5 | 5 | 10 |

480 tests. The other columns are the results the suite publishes, from the
same checkout: recorded on 2026-11-17 against the versions `results/index.mjs`
names, classified by the same script. "Other" is `untested`, `harness_fail` and
`retry`. They are someone else's runs on someone else's machine, so the
comparison is a rough one. What it does say is that a client library's cache,
behind a proxy written for the occasion, fails fewer required tests than any of
them.

### Calibration

A green check needs to show what would turn it red, so the same run was made
twice more with the cache changed and nothing else:

| | pass | yes | fail | dependency_fail |
|---|---:|---:|---:|---:|
| `--mode shared` | 272 | 89 | 5 | 44 |
| `--mode private` | 252 | 89 | 8 | 61 |
| `--mode none` | 22 | 13 | 6 | 384 |

Without a cache, 22 tests pass: the ones that ask a cache not to store or not
to serve. So the other 250 are the cache's. `private` mode turns exactly the
shared cache's tests red: `cc-resp-private-shared`, `freshness-s-maxage-shared`,
`other-authorization` and 17 that depend on them. That is the suite telling the
two modes apart, which is what H-5 claims they are. Three runs in shared mode
gave the same 480 results. All three modes were measured again with H-44's
fix: one more pass and one fewer fail in each cache mode, and no change
without a cache. With H-46's, again: one more pass and one fewer setup
failure in each cache mode, and no change without a cache.

The record was checked the same way. Deleting the `age-parse-suffix` row,
recording a fail for `freshness-none` and inventing a row for a test that does
not exist gave three reports and exit 1:

```
    ✗ age-parse-suffix (recorded pass, now fail)
    ↑ freshness-none (recorded fail, now yes - delete the row)
    ✗ no-such-test (recorded fail, and the suite has no such test)
```

## The record

Every test that is not a `pass` or a `yes` is a row in
[`tests/cache-tests-known.txt`](../tests/cache-tests-known.txt), 119 of them,
each with its reason. The run fails on any test whose result is not what the
record says, in either direction. A test that starts passing is a row to
delete: a record that cannot tell "stopped failing" from "never ran" is one of
the checks CLAUDE.md warns about.

The 119 rows, by cause:

| Cause | Rows | |
|---|---:|---|
| **H-45** conditional requests | 24 | a request with `If-None-Match` or `If-Modified-Since` of its own goes past the cache |
| **CDN** | 23 | `CDN-Cache-Control` (RFC 9213) targets CDN caches, and this is not one |
| checks | 37 | behaviour checks answered no, each with the RFC text Hermod's answer rests on |
| by design | 14 | `Range` and `206`, qualified `no-cache`, unknown transfer codings, and the `304` for another entity tag |
| optional | 8 | `Vary` normalisation, `must-understand`, a POST reused for a GET, a lower-case `gmt` |
| **H-47** HEAD | 4 | a `HEAD` neither freshens nor updates the stored `GET` |
| proxy | 4 | no interim response reaches the suite |
| untested | 5 | browser-only tests |

## What it found

Four findings in the cache and two in the client around it, tracked in
[`PLAN.md`](../PLAN.md):

- ~~**H-44**~~, fixed 2026-11-22, [Hermod#291](https://github.com/Vanaheimr/Hermod/pull/291): `Age: 7200, 0` on one line was
  read as invalid, so as 0, and a response 7200 seconds old was served as fresh. RFC 9111 §5.1: a cache SHOULD
  use the first member of a list. The same value on two lines passes, because
  the stored head keeps them apart.
- **H-45** — a request with preconditions of its own goes past the cache, so it
  never answers one with a `304` from a fresh stored response. RFC 9111 §4.3.2
  says it SHOULD. H-5 made that decision for a client cache, whose caller rarely
  sends preconditions. Behind a proxy, where the caller is another cache, it is
  the wrong decision. 24 rows.
- ~~**H-46**~~, fixed 2026-11-22, [Hermod#292](https://github.com/Vanaheimr/Hermod/pull/292): every delta-seconds value was
  capped at 2^31, `max-age=4294967295` included. With `Age: 2147483646` and
  three seconds' pause, the response was stale. RFC 9111 §1.2.2 allows 2^31 or
  the greatest value the cache can conveniently represent. The cap is 2^38 now,
  some 8 700 years, which keeps the age arithmetic inside a `TimeSpan`.
- **H-47** — a `HEAD` goes past the cache, so its `200` neither freshens nor
  updates the stored `GET`. RFC 9111 §4.3.5: SHOULD. The four tests are checks.
- **H-48** — `MaxNumberOfRetries` counts attempts. `0` sends nothing and
  answers "Giving up … after 0 attempts". Found by writing the proxy.
- **H-49** — the client refuses a status code outside 100–599 as an invalid
  status line. RFC 9110 §15: a client SHOULD treat it as a `5xx`. The suite's
  origin uses `999` to say "this should have been conditional", so
  `304-etag-update-response-ETag` is a setup failure here.

## Bumping the pin

The suite moves: tests are added and reworded every few weeks. A bump is
`SUITE_PIN` in `tests/cache-tests.sh`, then `tests/cache-tests.sh --update`,
which rewrites the record from the new run and keeps the note of every row
whose result did not change. A row without a note is a new result, and needs
one before the commit.
