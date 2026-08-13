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
// h1framing — RFC 9112 §6 (message body length) and §7 (transfer codings)
//
// This is the section where HTTP/1.1 implementations actually fail. The
// body-length algorithm in §6.3 has seven steps and every ambiguity between
// them is a request-smuggling primitive, so each check below is really the
// question "can a front end and a back end disagree about where this message
// ends".
// ---------------------------------------------------------------------------

var target = Target.FromArguments(args);
var checks = new Checks("h1framing");

target.Banner("h1framing — message framing & transfer codings");

var host = target.Authority;


// --- Content-Length validity (§6.3 step 5, §8.6) ---------------------------

checks.Status(
    "valid Content-Length body",
    await target.RoundTripAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Type: text/plain\r\nContent-Length: 5\r\n\r\nhello"),
    200
);

foreach (var (label, value) in new (String, String)[] {
             ("negative Content-Length",          "-1"),
             ("signed Content-Length",            "+5"),
             ("hexadecimal Content-Length",       "0x5"),
             ("non-numeric Content-Length",       "five"),
             ("empty Content-Length",             ""),
             ("Content-Length with space",        "5 5"),
             ("overflowing Content-Length",       "99999999999999999999999")
         })
{
    checks.Status(
        label,
        await target.RoundTripAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Length: {value}\r\n\r\nhello"),
        400
    );
}

// §6.3 step 4: duplicates that disagree are unrecoverable; duplicates that
// agree are permitted to be treated as one, but Hermod rejects both — the
// stricter reading, and the one that cannot be desynchronised.
checks.Status(
    "duplicate Content-Length (same value)",
    await target.RoundTripAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Length: 5\r\nContent-Length: 5\r\n\r\nhello"),
    400
);

checks.Status(
    "conflicting Content-Length values",
    await target.RoundTripAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Length: 5\r\nContent-Length: 6\r\n\r\nhello"),
    400
);

checks.Status(
    "comma-combined Content-Length",
    await target.RoundTripAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Length: 5, 6\r\n\r\nhello"),
    400
);


// --- Transfer-Encoding vs Content-Length (§6.1) ----------------------------

// The classic CL.TE / TE.CL primitive. §6.1: if both are present the message is
// malformed; a recipient MUST close the connection after responding.
checks.Status(
    "Content-Length + Transfer-Encoding",
    await target.RoundTripAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n"),
    400
);

checks.Status(
    "Transfer-Encoding + Content-Length (reversed order)",
    await target.RoundTripAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\n0\r\n\r\n"),
    400
);


// --- Transfer codings (§7) -------------------------------------------------

checks.Status(
    "valid chunked request",
    await target.RoundTripAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n"),
    200
);

// §6.1: "chunked" must be the final coding, otherwise the message length is
// undeterminable.
checks.Status(
    "chunked not the final coding",
    await target.RoundTripAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked, gzip\r\n\r\n0\r\n\r\n"),
    400
);

checks.Status(
    "unknown transfer coding",
    await target.RoundTripAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: bogus\r\n\r\n0\r\n\r\n"),
    400, 501
);

// The TE.TE obfuscation family: a front end and a back end disagreeing about
// whether these count as "chunked" is the whole attack.
foreach (var (label, value) in new (String, String)[] {
             ("obfuscated TE (leading space)",     " chunked"),
             ("obfuscated TE (tab prefix)",        "\tchunked"),
             ("obfuscated TE (quoted)",            "\"chunked\""),
             ("obfuscated TE (mixed case)",        "ChUnKeD"),
             ("obfuscated TE (chunked, chunked)",  "chunked, chunked")
         })
{

    var response = await target.RoundTripAsync(
                       $"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: {value}\r\n\r\n5\r\nhello\r\n0\r\n\r\n"
                   );

    // Either interpretation is safe as long as it is *decided*: treat it as
    // chunked (200) or refuse it (4xx). What must not happen is a 200 whose
    // body length came from somewhere else entirely.
    checks.Status(label, response, 200, 400, 501);

}

// §6.1: HTTP/1.0 predates transfer codings.
checks.Status(
    "chunked request from an HTTP/1.0 client",
    await target.RoundTripAsync($"POST /echo HTTP/1.0\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n"),
    400
);


// --- Chunk syntax (§7.1) ---------------------------------------------------

// Malformed chunk *syntax* is detectable immediately and answered 400.
foreach (var (label, body) in new (String, String)[] {
             ("non-hex chunk size",            "zz\r\nhello\r\n0\r\n\r\n"),
             ("negative chunk size",           "-5\r\nhello\r\n0\r\n\r\n"),
             ("chunk size overflow",           "FFFFFFFFFFFFFFFFFF\r\nhello\r\n0\r\n\r\n"),
             ("missing chunk terminator",      "5\r\nhello0\r\n\r\n")
         })
{
    checks.Status(
        label,
        await target.RoundTripAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n{body}"),
        400, 408
    );
}

// A *truncated* body is a different case: nothing is malformed yet, the sender
// has simply stopped, so the server can only wait for its read deadline and
// then answer 408. That takes as long as the deadline — hence the wider window,
// and hence the runner starting the demo with --fast-timeouts.
foreach (var (label, body) in new (String, String)[] {
             ("chunk shorter than declared",   "9\r\nhello\r\n0\r\n\r\n"),
             ("missing terminal chunk",        "5\r\nhello\r\n")
         })
{
    checks.Status(
        label,
        await target.RoundTripAsync(
            $"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n{body}",
            TimeSpan.FromSeconds(40)
        ),
        400, 408
    );
}

// §7.1.1 — chunk extensions: token values, valueless flags, quoted strings.
checks.Status(
    "valid chunk extensions",
    await target.RoundTripAsync(
        $"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\n\r\n" +
        "5;name=value;flag;quoted=\"a b\"\r\nhello\r\n0\r\n\r\n"
    ),
    200
);

checks.Status(
    "chunk extension with control character",
    await target.RoundTripAsync(
        $"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n" +
        "5;na\x01me=value\r\nhello\r\n0\r\n\r\n"
    ),
    400
);

checks.Status(
    "chunk extension with unterminated quote",
    await target.RoundTripAsync(
        $"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n" +
        "5;q=\"unterminated\r\nhello\r\n0\r\n\r\n"
    ),
    400
);


// --- Trailers (§7.1.2) -----------------------------------------------------

checks.Status(
    "valid trailer fields",
    await target.RoundTripAsync(
        $"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\nTrailer: X-Check\r\n\r\n" +
        "5\r\nhello\r\n0\r\nX-Check: ok\r\n\r\n"
    ),
    200
);

// §7.1.2 forbids trailers that would change how the message is framed or
// processed — allowing Content-Length here would reopen the framing question
// *after* the body has been read.
foreach (var forbidden in new[] { "Content-Length: 5", "Transfer-Encoding: chunked", "Host: evil.example", "Content-Type: text/html" })
{
    checks.Status(
        $"forbidden trailer \"{forbidden.Split(':')[0]}\"",
        await target.RoundTripAsync(
            $"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\nTrailer: X\r\n\r\n" +
            $"5\r\nhello\r\n0\r\n{forbidden}\r\n\r\n"
        ),
        400
    );
}

checks.Status(
    "whitespace-only trailer line",
    await target.RoundTripAsync(
        $"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n" +
        "5\r\nhello\r\n0\r\n   \r\n\r\n"
    ),
    400
);


// --- Response framing ------------------------------------------------------

{
    var response = await target.RoundTripAsync($"GET /chunked HTTP/1.1\r\nHost: {host}\r\n\r\n");

    checks.Contains      ("chunked response advertises the coding", response, "Transfer-Encoding: chunked");
    checks.DoesNotContain("chunked response has no Content-Length", response, "Content-Length:");
    checks.Contains      ("chunk extensions reach the wire",        response, ";kind=demo;flag");
    checks.Contains      ("terminal chunk present",                 response, "\r\n0\r\n");
}

{
    var response = await target.RoundTripAsync($"GET /trailers HTTP/1.1\r\nHost: {host}\r\n\r\n");

    checks.Contains("response announces its trailers", response, "Trailer:");
    checks.Contains("trailer follows the terminal chunk", response, "X-Demo-Checksum: deadbeef");
}

// §6.3 step 1: a HEAD response is bodyless regardless of what its
// Content-Length says. Getting this wrong desynchronises every keep-alive
// connection that ever sees a HEAD.
{
    var response = await target.RoundTripAsync($"HEAD /files/resource.txt HTTP/1.1\r\nHost: {host}\r\n\r\n", Bodyless: true);

    checks.Status  ("HEAD response",                 response, 200);
    checks.Contains("HEAD keeps Content-Length",     response, "Content-Length:");
    checks.DoesNotContain("HEAD carries no body",    response, "Hello from the Hermod");
}

// Likewise for status codes that forbid content.
{
    var response = await target.RoundTripAsync($"GET /status/204 HTTP/1.1\r\nHost: {host}\r\n\r\n");

    checks.Status        ("204 response",            response, 204);
    checks.DoesNotContain("204 carries no body",     response, "status 204");
}


// --- HTTP/1.0 close-delimited responses ------------------------------------

{
    var response = await target.RoundTripAsync("GET / HTTP/1.0\r\n\r\n");

    checks.Status  ("HTTP/1.0 request",                  response, 200);
    checks.Contains("HTTP/1.0 response closes",          response, "Connection: close");
    checks.Contains("HTTP/1.0 status line echoes 1.0",   response, "HTTP/1.0 200");
}


return checks.Summary();
