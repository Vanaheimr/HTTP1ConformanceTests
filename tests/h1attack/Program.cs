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
// h1attack — RFC 9112 §11.2 (smuggling / splitting) and resource hardening
//
// The pass condition throughout is "the server did not do the interesting
// thing": no smuggled request executed, no unbounded buffer, no connection
// left hanging. A check that passes here is one where nothing happened, which
// is exactly why each one states what the *failure* would have looked like.
// ---------------------------------------------------------------------------

var target = Target.FromArguments(args);
var checks = new Checks("h1attack");

target.Banner("h1attack — smuggling & hardening");

var host = target.Authority;


// --- §11.2 Request smuggling: the CL/TE family ------------------------------

// CL.TE — a front end that trusts Content-Length sees one request; a back end
// that trusts Transfer-Encoding sees the terminal chunk and treats the rest as
// a *second* request. If the smuggled GET is answered, the two ends disagreed.
{

    var response = await target.RoundTripAsync(
                       $"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Length: 6\r\nTransfer-Encoding: chunked\r\n\r\n" +
                       $"0\r\n\r\nGET /status/418 HTTP/1.1\r\nHost: {host}\r\n\r\n"
                   );

    checks.Status        ("CL.TE desync attempt rejected",      response, 400);
    checks.DoesNotContain("no smuggled request was executed",   response, "418");

}

// TE.CL — the mirror image.
{

    var response = await target.RoundTripAsync(
                       $"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\nContent-Length: 4\r\n\r\n" +
                       $"5c\r\nGET /status/418 HTTP/1.1\r\nHost: {host}\r\n\r\n\r\n0\r\n\r\n"
                   );

    checks.Status        ("TE.CL desync attempt rejected",    response, 400);
    checks.DoesNotContain("no smuggled request was executed", response, "418");

}

// TE.TE — two Transfer-Encoding fields, one obfuscated so that one
// implementation honours it and the other does not.
//
// A single origin server cannot exhibit a TE.TE desync: the attack *is* a
// disagreement between two parsers, and here there is only one. The first
// version of these checks asserted "the trailing GET must not be answered",
// which was simply wrong — with a well-formed terminal chunk those bytes are a
// legitimate pipelined request, and answering them is correct HTTP/1.1.
//
// What can be verified against one server is that the boundary is placed
// *deterministically*: either the message is refused outright, or it is read as
// chunked and the remainder is exactly one pipelined request. Any other count
// means the server placed the end of the body somewhere the sender did not.
//
// The real TE.TE differential needs two implementations disagreeing — that is
// http-garden, PLAN.md A6.
foreach (var (label, second) in new (String, String)[] {
             ("duplicate Transfer-Encoding",       "Transfer-Encoding: chunked"),
             ("TE with obfuscated duplicate",      "Transfer-Encoding: xchunked"),
             ("TE with space-prefixed duplicate",  "Transfer-Encoding:  chunked")
         })
{

    var response = await target.RoundTripAsync(
                       $"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n{second}\r\n\r\n" +
                       $"0\r\n\r\nGET /status/418 HTTP/1.1\r\nHost: {host}\r\n\r\n"
                   );

    var count  = Checks.ResponseCount(response);
    var status = Checks.StatusOf(response);

    checks.That(
        $"{label} → message boundary is deterministic",
        (status >= 400 && count == 1) ||     // refused outright
        (status == 200 && count == 2),       // read as chunked + one pipelined request
        $"{count} response(s), first status {status}"
    );

}

// Response splitting: CRLF injected through a request field must never reach
// the response header section as a field separator.
{

    var response = await target.RoundTripAsync(
                       $"GET / HTTP/1.1\r\nHost: {host}\r\nX-Inject: a\r\nX-Smuggled: yes\r\n\r\n"
                   );

    checks.DoesNotContain("injected field is not echoed into the response", response, "X-Smuggled");

}


// --- Slowloris: header section that never completes -------------------------

// The connection must be torn down on the header deadline rather than held
// open indefinitely. The pass condition is "something happened within 45 s".
{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync($"GET / HTTP/1.1\r\nHost: {host}\r\nX-Slow: ");

    var response = await connection.ReadAsync(TimeSpan.FromSeconds(45));

    checks.That(
        "incomplete header section is terminated on the deadline",
        response.Length == 0 || Checks.StatusOf(response) is 400 or 408 or 431,
        $"got: {Checks.FirstLine(response)}"
    );

}

// A declared body that never arrives: 408 is the documented answer.
{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Length: 100\r\n\r\npartial");

    var response = await connection.ReadAsync(TimeSpan.FromSeconds(45));

    checks.That(
        "incomplete request body times out",
        response.Length == 0 || Checks.StatusOf(response) is 408 or 400,
        $"got: {Checks.FirstLine(response)}"
    );

}

// Chunk metadata that never terminates — the chunked equivalent of the above,
// and the one a body-size limit alone does not catch.
{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhel");

    var response = await connection.ReadAsync(TimeSpan.FromSeconds(45));

    checks.That(
        "incomplete chunked body times out",
        response.Length == 0 || Checks.StatusOf(response) is 408 or 400,
        $"got: {Checks.FirstLine(response)}"
    );

}


// --- Resource limits: rejection must happen before allocation ---------------

// A huge declared Content-Length must be refused on the *header*, not after
// reading 10 GiB into memory. The give-away is the response time.
{

    await using var connection = await target.ConnectAsync();

    // Headers only — not one byte of the 10 GiB is ever sent. If the server
    // answers at all, it answered on the declared length alone.
    await connection.SendAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Length: 10737418240\r\n\r\n");

    var started  = DateTimeOffset.UtcNow;
    var response = await connection.ReadHeadersAsync(TimeSpan.FromSeconds(10));
    var elapsed  = DateTimeOffset.UtcNow - started;

    checks.Status("oversized Content-Length rejected", response, 413, 400);

    // ReadHeadersAsync returns on the header terminator, so this really is the
    // server's latency rather than the harness's own window.
    checks.That(
        "oversized Content-Length rejected on the header, not the body",
        elapsed < TimeSpan.FromSeconds(5),
        $"took {elapsed.TotalSeconds:F1}s"
    );

}

// Chunked bodies bypass the declared length entirely, so the limit has to be
// enforced while streaming as well.
{

    // 144 × 64 KiB ≈ 9.4 MiB — just past the server's 8 MiB default body limit.
    // Sizing it to *cross* the limit rather than to dwarf it keeps the check
    // honest and keeps the suite fast, which matters most over TLS.
    var payload  = new String('x', 64 * 1024);
    var body     = String.Concat(Enumerable.Repeat($"{payload.Length:X}\r\n{payload}\r\n", 144)) + "0\r\n\r\n";

    var response = await target.RoundTripAsync(
                       $"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n{body}",
                       TimeSpan.FromSeconds(15)
                   );

    checks.That(
        "oversized chunked body is rejected while streaming",
        response.Length == 0 || Checks.StatusOf(response) is 413 or 400,
        $"got: {Checks.FirstLine(response)}"
    );

}

// Chunk metadata flood: many tiny chunks each carrying a large extension. The
// payload stays under any body limit — only a *metadata* limit catches it.
{

    var extension = new String('e', 4096);
    var body      = String.Concat(Enumerable.Repeat($"1;x={extension}\r\na\r\n", 100)) + "0\r\n\r\n";

    var response  = await target.RoundTripAsync(
                        $"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n{body}",
                        TimeSpan.FromSeconds(15)
                    );

    checks.That(
        "chunk metadata flood is bounded",
        response.Length == 0 || Checks.StatusOf(response) is 400 or 413 or 431,
        $"got: {Checks.FirstLine(response)}"
    );

}

// Trailer flood — the same idea after the terminal chunk.
{

    var trailers = String.Concat(Enumerable.Range(0, 500).Select(i => $"X-Trailer-{i}: value\r\n"));

    var response = await target.RoundTripAsync(
                       $"POST /echo HTTP/1.1\r\nHost: {host}\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n{trailers}\r\n",
                       TimeSpan.FromSeconds(15)
                   );

    checks.That(
        "trailer flood is bounded",
        response.Length == 0 || Checks.StatusOf(response) is 400 or 431 or 413,
        $"got: {Checks.FirstLine(response)}"
    );

}


// --- The server survives all of it -------------------------------------------

// The check that gives the rest their meaning: after every abuse above, an
// ordinary request must still be served. Rejecting hostile input is only half
// the requirement — staying up is the other half.
checks.Status(
    "server still serving after the abuse suite",
    await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n"),
    200
);


return checks.Summary();
