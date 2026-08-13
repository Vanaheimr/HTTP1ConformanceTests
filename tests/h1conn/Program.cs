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
// h1conn — RFC 9112 §9 (connection management) + RFC 1945 (HTTP/1.0 keep-alive)
//
// Persistence and pipelining are where framing bugs become *visible*: a message
// whose end the server got wrong does not corrupt that response, it corrupts
// the next one on the same connection.
// ---------------------------------------------------------------------------

var target = Target.FromArguments(args);
var checks = new Checks("h1conn");

target.Banner("h1conn — persistence & pipelining");

var host = target.Authority;


// --- §9.3: HTTP/1.1 is persistent by default -------------------------------

{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync($"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n");
    var first  = await connection.ReadAsync(TimeSpan.FromSeconds(1));

    await connection.SendAsync($"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n");
    var second = await connection.ReadAsync(TimeSpan.FromSeconds(1));

    checks.Status("HTTP/1.1 first request on a reused connection",  first,  200);
    checks.Status("HTTP/1.1 second request on the same connection", second, 200);

}


// --- §9.6: Connection: close ------------------------------------------------

{
    var response = await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");

    checks.Status  ("Connection: close request", response, 200);
    checks.Contains("close echoed in response",  response, "Connection: close");
}

// A contradictory pair must resolve to close — the safe direction. Resolving it
// the other way is how a connection gets reused that the peer has already
// abandoned.
{
    var response = await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close, keep-alive\r\n\r\n");

    checks.Status  ("Connection: close + keep-alive", response, 200);
    checks.Contains("close wins over keep-alive",     response, "Connection: close");
}


// --- RFC 1945 / §9.3: HTTP/1.0 defaults to non-persistent -------------------

{
    var response = await target.RoundTripAsync("GET / HTTP/1.0\r\n\r\n");

    checks.Contains("HTTP/1.0 closes by default", response, "Connection: close");
}

{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync($"GET / HTTP/1.0\r\nHost: {host}\r\nConnection: keep-alive\r\n\r\n");
    var first  = await connection.ReadAsync(TimeSpan.FromSeconds(1));

    checks.Status  ("HTTP/1.0 + keep-alive",              first, 200);
    checks.Contains("HTTP/1.0 keep-alive is negotiated",  first, "keep-alive");

    await connection.SendAsync($"GET / HTTP/1.0\r\nHost: {host}\r\nConnection: keep-alive\r\n\r\n");
    var second = await connection.ReadAsync(TimeSpan.FromSeconds(1));

    checks.Status("HTTP/1.0 second request on the same connection", second, 200);

}


// --- §9.3.2: pipelining -----------------------------------------------------

// Three requests written in one go, before any response is read. The server
// must answer all three, in order, on the one connection.
{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync(
              $"GET /status/200 HTTP/1.1\r\nHost: {host}\r\n\r\n" +
              $"GET /status/404 HTTP/1.1\r\nHost: {host}\r\n\r\n" +
              $"GET /status/418 HTTP/1.1\r\nHost: {host}\r\n\r\n"
          );

    var response = await connection.ReadAsync(TimeSpan.FromSeconds(2));

    checks.That(
        "3 pipelined requests → 3 responses",
        Checks.ResponseCount(response) == 3,
        $"counted {Checks.ResponseCount(response)}"
    );

    var order200 = response.IndexOf("200", StringComparison.Ordinal);
    var order404 = response.IndexOf("404", StringComparison.Ordinal);
    var order418 = response.IndexOf("418", StringComparison.Ordinal);

    checks.That(
        "pipelined responses arrive in request order",
        order200 >= 0 && order404 > order200 && order418 > order404,
        $"offsets 200@{order200} 404@{order404} 418@{order418}"
    );

}

// A chunked request followed by another request in the same write. The server
// must consume exactly the chunked body and then parse the *next* request —
// the precise point at which a smuggled request would slip through.
{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync(
              $"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n" +
              $"GET /status/418 HTTP/1.1\r\nHost: {host}\r\n\r\n"
          );

    var response = await connection.ReadAsync(TimeSpan.FromSeconds(2));

    checks.That(
        "chunked request delimited before the next pipelined request",
        Checks.ResponseCount(response) == 2,
        $"counted {Checks.ResponseCount(response)}"
    );

    checks.Contains("the request after the chunked body was processed", response, "418");

}

// An invalid leading request must close the connection *before* the trailing
// bytes can be reinterpreted as a second request.
{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync(
              $"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Length: 5\r\nContent-Length: 6\r\n\r\nhello" +
              $"GET /status/418 HTTP/1.1\r\nHost: {host}\r\n\r\n"
          );

    var response = await connection.ReadAsync(TimeSpan.FromSeconds(2));

    checks.Status        ("invalid leading pipelined request", response, 400);
    checks.DoesNotContain("trailing bytes were not executed",  response, "418");
    checks.That(
        "only one response after an invalid leading request",
        Checks.ResponseCount(response) == 1,
        $"counted {Checks.ResponseCount(response)}"
    );

}


// --- Reuse after bodyless responses ----------------------------------------

// HEAD and 304 are the two cases where a client can most easily mis-count the
// body and desynchronise. Both must leave the connection usable.
foreach (var (label, request) in new (String, String)[] {
             ("HEAD",  $"HEAD /files/resource.txt HTTP/1.1\r\nHost: {host}\r\n\r\n"),
             ("304",   $"GET /files/resource.txt HTTP/1.1\r\nHost: {host}\r\nIf-None-Match: \"hermod-h1-demo-resource-v1\"\r\n\r\n")
         })
{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync(request);
    await connection.ReadAsync(TimeSpan.FromSeconds(1));

    await connection.SendAsync($"GET /status/418 HTTP/1.1\r\nHost: {host}\r\n\r\n");
    var next = await connection.ReadAsync(TimeSpan.FromSeconds(1));

    checks.Status($"connection reusable after a {label} response", next, 418);

}


// --- Half-close --------------------------------------------------------------

// A client that sends its request and then shuts down its send side still
// expects the response. Treating the FIN as an abort loses it.
{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync($"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n");
    connection.ShutdownSend();

    checks.Status("response delivered after client half-close", await connection.ReadAsync(TimeSpan.FromSeconds(2)), 200);

}


return checks.Summary();
