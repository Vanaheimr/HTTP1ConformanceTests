# Testing against browsers (A8)

Every other consumer in this repository was written to be a test. Our
harnesses, curl, five foreign stdlib clients, five reverse proxies — each one
is code somebody chose to point at the demo. A browser is not, and it is the
least forgiving HTTP/1.1 consumer in daily use.

Three engines through Playwright — **Chromium, Firefox and WebKit**. WebKit is
the reason for the dependency: it is the only way to reach Safari's engine
from a script, and it is the engine nobody tests.

```bash
tests/browser.sh                     # starts its own demo, all three engines
tests/browser.sh --browser webkit
tests/browser.sh --headed            # watch it
tests/browser.sh --update            # rewrite tests/browser-known.txt
tests/browser.sh --no-install        # skip rather than fetch half a gigabyte
```

**27 checks, 24 pass.** The three that fail are one finding, on all three
engines, and finding it is what A8 was for.

## The battery runs in the page

The driver navigates to the demo's own `/` document and evaluates the battery
inside it. Every `fetch`, `EventSource` and `WebSocket` below is therefore a
same-origin request from a real document, which is the only arrangement in
which CORS, connection reuse and the Resource Timing entries mean anything.

No page is served for the occasion. A `/browser` route would have made the
test convenient and would have measured that route; A8 tests the demo as it
is. (The `/cors` route is the one exception, and it exists to *expose* a gap
rather than to smooth one — see below.)

## What only a browser can say

| | |
|---|---|
| **it names the protocol itself** | `performance.nextHopProtocol` is `http/1.1` — Chrome's verdict that it spoke HTTP/1.1, not ours. Everything else here is this repository asserting the protocol it believes it used |
| **it implements EventSource** | curl can read an SSE body. Only a browser has the client half of the protocol — the event-type dispatch, the `id`, the `retry` interval |
| **it enforces CORS** | curl sends the cross-origin request and is answered 200. A browser refuses to make it at all |
| **it accounts for its own connections** | Resource Timing reports `connectStart === connectEnd` for a request that opened no connection. That is the browser's account of keep-alive rather than ours |

The rest of the battery — a chunked body reassembled, a negotiated content
coding decoded, a `Range` answered 206 — is covered elsewhere too, and is here
because a browser doing it is a different claim from curl doing it.

## The finding: H-10, from the only vantage point that can see it

The demo's `/cors` route sets `Access-Control-Allow-Origin: *`, so the simple
cross-origin GET works: Hermod can set the field, and the browser reads the
body.

A POST carrying a custom request header is **not** a simple request. The
browser sends an `OPTIONS` preflight first, and nothing answers it:

```
> OPTIONS /cors            (the browser's preflight)
< HTTP/1.1 405 Method Not Allowed
< Allow: GET, POST

> POST /cors               (the same request, from curl)
< HTTP/1.1 200 OK
```

Every non-browser client in this repository is answered. The browser is not,
on all three engines — Chromium says `Failed to fetch`, Firefox says
`NetworkError when attempting to fetch resource.`, WebKit says `Load failed`,
and all three mean the preflight.

That is **H-10 — no automatic CORS preflight**, which has sat in the findings
table since A0 marked "Browser-visible (**A8**)". It is now demonstrated
rather than asserted.

`OPTIONS` is deliberately **not** registered on `/cors`. A hand-written
handler there would answer the preflight and hide the gap the route exists to
show — the same reasoning as the `HEAD` registrations elsewhere in the demo,
which H-23 is about.

The three failures are recorded in
[`tests/browser-known.txt`](../tests/browser-known.txt) with their finding
number. A failure that is not in that file fails the run; one that stops
failing is reported so the line can be deleted. Falsified: delete the `webkit`
line and the run exits 1 naming it.

## Three checks that were wrong before they were right

Worth recording, because each looked like a server defect for a few minutes
and each was mine:

- **The chunked body ends with a newline.** 33 octets, measured.
  `tests/interop.sh` compares through a `$(...)`, which strips trailing
  newlines on both sides and so never had to know; a string comparison in
  JavaScript does.
- **`/large` does not serve ranges.** It is deliberately a plain
  octet-stream with no conditional or range handling, so pointing a Range
  check at it measured that choice. `/files/resource.txt` is the route that
  answers 206.
- **The demo's events are named.** It sends `event: tick`, and `onmessage`
  fires only for *unnamed* events — so the first version waited twenty seconds
  for a message type the server never sends. Both are listened for now.

## Cost, and why this is a nightly

Playwright's three engines are about half a gigabyte, once. `tests/browser.sh`
fetches them on demand and skips with a reason if it cannot. There is no
Docker and no WSL: the browsers run natively wherever this runs, and the demo
host stays bound to loopback — the cross-origin twin is `http://localhost`,
which reaches a loopback listener perfectly well. A test run has no business
widening a listener it did not have to.

## A deviation from the plan, stated

`PLAN.md` asked for `tools/browser-interop.ps1`, modelled on the HTTP/3 repo's
script. This is bash plus a Node module instead, because this repository
removed its PowerShell runners on purpose: two implementations of one runner
produce two numbers that look like agreement, and the sibling projects paid
for that twice — once with a `$Args` parameter that silently never bound. The
one idea worth taking from the HTTP/2 script is here: the page runs the
battery and reports a verdict, rather than the driver scraping a DOM and
guessing when the run finished.
