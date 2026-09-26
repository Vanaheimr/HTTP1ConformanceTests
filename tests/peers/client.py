#!/usr/bin/env python3
"""A7 — Python's stdlib HTTP as a foreign client of the demo host.

    python3 client.py http://127.0.0.1:8080

http.client is deliberately low level: one connection, no redirect following,
no transparent decoding, and no access to the trailer section. That makes it a
good witness for framing and a poor one for anything the standard leaves to a
higher layer, so the redirect check borrows urllib.request - still the same
standard library, one layer up - and the trailer fields are a SKIP with the
reason rather than a silent pass.
"""

import gzip
import http.client
import sys
import urllib.request
from urllib.parse import urlparse

failed = 0


def pass_(label):
    print(f"PASS\t{label}")


def fail(label, detail):
    global failed
    failed += 1
    print(f"FAIL\t{label}\t{detail}")


def skip(label, reason):
    print(f"SKIP\t{label}\t{reason}")


def check(label, ok, detail):
    pass_(label) if ok else fail(label, detail)


def quote(text):
    if isinstance(text, bytes):
        text = text.decode("utf-8", "replace")
    text = str(text)
    return '"' + (text[:80] + "…" if len(text) > 80 else text).replace("\n", "\\n") + '"'


def main():

    if len(sys.argv) < 2:
        print("usage: client.py <base-url>", file=sys.stderr)
        return 2

    base = sys.argv[1].rstrip("/")
    url  = urlparse(base)

    # One connection for everything below, which is also the reuse check: every
    # request after the first is travelling on a connection the server agreed
    # to keep open.
    conn = http.client.HTTPConnection(url.hostname, url.port, timeout=20)

    def request(method, path, headers=None):
        conn.request(method, path, headers=headers or {})
        response = conn.getresponse()
        body = response.read()
        return response, body

    # --------------------------------------------------------- baseline

    response, body = request("GET", "/")
    check("baseline", response.status == 200 and len(body) > 0,
          f"status {response.status}, {len(body)} bytes")

    # ---------------------------------------------------------- chunked

    response, body = request("GET", "/chunked")
    check("chunked",
          response.status == 200 and body == b"chunk-one\nchunk-two\nchunk-three\n",
          f"status {response.status}, body {quote(body)}")

    # --------------------------------------------------------- trailers

    response, body = request("GET", "/trailers")
    check("trailers-body",
          response.status == 200 and body == b"body with trailers\n",
          f"status {response.status}, body {quote(body)}")
    skip("trailers", "http.client does not expose the trailer section")

    # ------------------------------------------------------------- gzip

    _, identity = request("GET", "/prose")
    response, encoded = request("GET", "/prose", {"Accept-Encoding": "gzip"})

    content_encoding = response.getheader("Content-Encoding") or ""
    vary             = response.getheader("Vary") or ""

    if content_encoding != "gzip":
        fail("gzip", f"Content-Encoding {quote(content_encoding)}")
    elif "accept-encoding" not in vary.lower():
        fail("gzip", f"Vary {quote(vary)}")
    else:
        decoded = gzip.decompress(encoded)
        check("gzip", decoded == identity,
              f"{len(decoded)} decoded vs {len(identity)} identity bytes")

    # ------------------------------------------------- HEAD matches GET

    get_response,  get_body  = request("GET",  "/files/resource.txt")
    head_response, head_body = request("HEAD", "/files/resource.txt")

    head_length = head_response.getheader("Content-Length")

    check("head-matches-get",
          head_response.status == 200
          and len(head_body) == 0
          and head_length is not None and int(head_length) == len(get_body)
          and head_response.getheader("ETag") == get_response.getheader("ETag"),
          f"status {head_response.status}, {len(head_body)} body bytes, "
          f"Content-Length {head_length} vs GET {len(get_body)}")

    # ------------------------------------------------------------ range

    response, body = request("GET", "/files/resource.txt", {"Range": "bytes=0-9"})
    content_range = response.getheader("Content-Range") or ""

    check("range",
          response.status == 206 and len(body) == 10 and content_range.startswith("bytes 0-9/"),
          f"status {response.status}, {len(body)} bytes, Content-Range {quote(content_range)}")

    # ---------------------------------------------------- accept-ranges

    check("accept-ranges", get_response.getheader("Accept-Ranges") == "bytes",
          f"Accept-Ranges {quote(get_response.getheader('Accept-Ranges'))}")

    # ------------------------------------------------------- status 404

    response, _ = request("GET", "/status/404")
    check("status-404", response.status == 404, f"status {response.status}")

    # --------------------------------------------------------- redirect
    #
    # One layer up, because http.client does not follow anything.

    try:
        with urllib.request.urlopen(base + "/redirect/302", timeout=20) as followed:
            check("redirect", followed.status == 200, f"final status {followed.status}")
    except Exception as error:                                # noqa: BLE001
        fail("redirect", str(error))

    # ------------------------------------------------------------ reuse
    #
    # Everything above went through one HTTPConnection. If the server had
    # closed it early, the request after that point would have raised instead
    # of answering - so reaching here with a working connection is the check.

    response, _ = request("GET", "/")
    check("reuse", response.status == 200, f"status {response.status} on the reused connection")

    conn.close()

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
