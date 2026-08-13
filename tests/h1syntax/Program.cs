/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Vanaheimr Hermod <https://www.github.com/Vanaheimr/Hermod>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using org.GraphDefined.Vanaheimr.Hermod.HTTP1.Tests;

// ---------------------------------------------------------------------------
// h1syntax — RFC 9112 §2-3 (message format, request line, request targets) and
//            RFC 9110 §5 (field syntax)
//
// Every check here sends bytes that a real HTTP client refuses to produce, so
// the only way to exercise this layer is a raw socket.
// ---------------------------------------------------------------------------

var target = Target.FromArguments(args);
var checks = new Checks("h1syntax");

target.Banner("h1syntax — request line & field syntax");

var host = target.Authority;


// --- The control: if this fails, every rejection below is meaningless -------

checks.Status(
    "canonical HTTP/1.1 request",
    await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n"),
    200
);


// --- RFC 9112 §3.2 — request target forms ----------------------------------

checks.Status(
    "origin-form target",
    await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n"),
    200
);

// §3.2.2: absolute-form is for requests to a proxy. Hermod is an origin server
// and rejects it rather than silently treating it as origin-form — the
// permissive reading is a documented smuggling vector past front-end routers.
checks.Status(
    "absolute-form target (proxy request) rejected",
    await target.RoundTripAsync($"GET http://{host}/ HTTP/1.1\r\nHost: {host}\r\n\r\n"),
    400
);

// §3.2.3: authority-form belongs to CONNECT, which an origin server does not
// implement — 501 is the conformant answer, not 400.
checks.Status(
    "authority-form CONNECT",
    await target.RoundTripAsync($"CONNECT {host} HTTP/1.1\r\nHost: {host}\r\n\r\n"),
    501, 405
);

// §3.2.4: asterisk-form applies to the server as a whole.
checks.Status(
    "asterisk-form OPTIONS *",
    await target.RoundTripAsync($"OPTIONS * HTTP/1.1\r\nHost: {host}\r\n\r\n"),
    200, 204
);


// --- Request-target validation ---------------------------------------------

foreach (var (label, path) in new (String, String)[] {
             ("fragment in target",            "/#fragment"),
             ("malformed percent escape",      "/%zz"),
             ("truncated percent escape",      "/%2"),
             ("encoded path separator",        "/a%2Fb"),
             ("double-encoded separator",      "/a%252Fb"),
             ("dot segment",                   "/../etc/passwd"),
             ("encoded dot segment",           "/%2e%2e/etc"),
             ("null byte in target",           "/a\0b")
         })
{
    checks.Status(
        label,
        await target.RoundTripAsync($"GET {path} HTTP/1.1\r\nHost: {host}\r\n\r\n"),
        400
    );
}


// --- RFC 9112 §2.6 — HTTP version syntax -----------------------------------

// "HTTP-version = HTTP-name "/" DIGIT "." DIGIT" — exactly one digit either
// side, name case-sensitive. Everything else is malformed, not a version to
// negotiate down from.
foreach (var (label, version) in new (String, String)[] {
             ("version HTTP/1 (no minor)",      "HTTP/1"),
             ("version HTTP/01.1 (leading 0)",  "HTTP/01.1"),
             ("version http/1.1 (lowercase)",   "http/1.1"),
             ("version HTTP/1.1x (trailing)",   "HTTP/1.1x"),
             ("version HTTP /1.1 (space)",      "HTTP /1.1")
         })
{
    checks.Status(
        label,
        await target.RoundTripAsync($"GET / {version}\r\nHost: {host}\r\n\r\n"),
        400, 505
    );
}


// --- RFC 9110 §5 — field syntax --------------------------------------------

// §5.1: no whitespace between field name and colon. This one is a MUST-reject
// precisely because a lenient parser downstream of a strict one is how request
// smuggling starts.
checks.Status(
    "whitespace before field colon",
    await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost : {host}\r\n\r\n"),
    400
);

checks.Status(
    "space inside field name",
    await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost: {host}\r\nBad Header: x\r\n\r\n"),
    400
);

// §5.2: obs-fold is deprecated; a recipient that is not a proxy must reject it.
checks.Status(
    "obsolete line folding (obs-fold)",
    await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost: {host}\r\nX-Folded: one\r\n  two\r\n\r\n"),
    400
);

checks.Status(
    "control character in field value",
    await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost: {host}\r\nX-Bad: va\x01lue\r\n\r\n"),
    400
);

checks.Status(
    "bare CR inside field value",
    await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost: {host}\r\nX-Bad: va\rlue\r\n\r\n"),
    400
);


// --- RFC 9112 §3.2 / RFC 9110 §7.2 — Host ----------------------------------

checks.Status(
    "HTTP/1.1 without Host",
    await target.RoundTripAsync("GET / HTTP/1.1\r\n\r\n"),
    400
);

checks.Status(
    "two Host fields",
    await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost: {host}\r\nHost: evil.example\r\n\r\n"),
    400
);

checks.Status(
    "Host with control character",
    await target.RoundTripAsync("GET / HTTP/1.1\r\nHost: exa\x07mple\r\n\r\n"),
    400
);

// RFC 1945: HTTP/1.0 has no Host requirement, so its absence must NOT be an error.
checks.Status(
    "HTTP/1.0 without Host is accepted",
    await target.RoundTripAsync("GET / HTTP/1.0\r\n\r\n"),
    200
);


// --- Field names are case-insensitive (RFC 9110 §5.1) ----------------------

checks.Status(
    "field name case-insensitivity (hOsT)",
    await target.RoundTripAsync($"GET / HTTP/1.1\r\nhOsT: {host}\r\n\r\n"),
    200
);


// --- Limits: RFC 9110 §15.5.15 (414) and RFC 6585 (431) --------------------

checks.Status(
    "oversized request target",
    await target.RoundTripAsync($"GET /{new String('a', 16 * 1024)} HTTP/1.1\r\nHost: {host}\r\n\r\n"),
    414, 400
);

checks.Status(
    "oversized single field line",
    await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost: {host}\r\nX-Big: {new String('a', 16 * 1024)}\r\n\r\n"),
    431, 400
);

checks.Status(
    "excessive field count",
    await target.RoundTripAsync(
        $"GET / HTTP/1.1\r\nHost: {host}\r\n" +
        String.Concat(Enumerable.Range(0, 300).Select(i => $"X-Pad-{i}: v\r\n")) +
        "\r\n"
    ),
    431, 400
);


// --- Fragmented delivery: a valid request split across TCP reads ------------

// Not a hostile case — the segmentation a real network produces anyway. It is
// here because reassembly bugs hide behind loopback's tendency to deliver a
// small request in one read.
{

    await using var connection = await target.ConnectAsync();

    await connection.SendSegmentedAsync(
              $"GET / HTTP/1.1\r\nHost: {host}\r\nX-Split: yes\r\n\r\n",
              ChunkSize:  7,
              Delay:      TimeSpan.FromMilliseconds(20)
          );

    checks.Status("request fragmented across TCP reads", await connection.ReadAsync(), 200);

}


return checks.Summary();
