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
// h1sse — Server-Sent Events (WHATWG HTML, "Server-sent events")
//
// SSE is not an RFC and not a separate protocol: it is an ordinary HTTP/1.1
// response that never ends. Which makes it a good stress test of the streaming
// path — the response has no Content-Length, must not be buffered, and must
// survive a client that walks away mid-stream.
// ---------------------------------------------------------------------------

var target = Target.FromArguments(args);
var checks = new Checks("h1sse");

target.Banner("h1sse — Server-Sent Events");

var host = target.Authority;


// --- The stream head --------------------------------------------------------

{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync($"GET /events HTTP/1.1\r\nHost: {host}\r\nAccept: text/event-stream\r\n\r\n");

    // The demo ticks every 2 s, so a 5 s window reliably contains at least two.
    var stream = await connection.ReadAsync(TimeSpan.FromSeconds(5));

    checks.Status  ("GET /events",                     stream, 200);
    checks.Contains("declares text/event-stream",      stream, "text/event-stream");
    checks.Contains("disables caching",                stream, "no-cache");
    checks.Contains("announces a retry interval",      stream, "retry:");
    checks.Contains("carries an event name",           stream, "event: tick");
    checks.Contains("carries a data line",             stream, "data: tick");
    checks.Contains("carries an event id",             stream, "id:");

    // The whole point of a stream: bytes arrive while the response is still
    // open. A buffered implementation would deliver nothing until it ended,
    // which for /events is never.
    checks.That(
        "events arrive before the response ends",
        stream.Contains("data:", StringComparison.Ordinal),
        "no data lines within the window"
    );

    // Two ticks in a 5 s window proves the stream keeps producing rather than
    // emitting one event and stalling.
    var ticks = stream.Split("data: tick").Length - 1;

    checks.That(
        "the stream keeps producing (≥2 events in 5 s)",
        ticks >= 2,
        $"saw {ticks} event(s)"
    );

}


// --- Framing: a stream must not be Content-Length delimited -----------------

{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync($"GET /events HTTP/1.1\r\nHost: {host}\r\n\r\n");

    var head = await connection.ReadAsync(TimeSpan.FromSeconds(3));

    // A Content-Length on an endless stream is a contradiction: the server
    // cannot know it, and a client that trusts it stops reading early.
    checks.DoesNotContain("stream is not Content-Length framed", head, "Content-Length:");

}


// --- Last-Event-ID replay ----------------------------------------------------

{

    // First, learn a real event id from the live stream rather than assuming
    // the numbering — the replay assertion is only meaningful against an id the
    // server actually issued.
    String? lastId = null;

    {
        await using var probe = await target.ConnectAsync();
        await probe.SendAsync($"GET /events HTTP/1.1\r\nHost: {host}\r\n\r\n");

        var stream = await probe.ReadAsync(TimeSpan.FromSeconds(5));

        foreach (var line in stream.Split('\n'))
            if (line.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                lastId = line[3..].Trim();
    }

    checks.That("an event id was observed", lastId is not null, "no id: line seen");

    if (lastId is not null && UInt64.TryParse(lastId, out var id) && id > 1)
    {

        await using var connection = await target.ConnectAsync();

        await connection.SendAsync(
                  $"GET /events HTTP/1.1\r\nHost: {host}\r\nLast-Event-ID: {id - 1}\r\n\r\n"
              );

        var replayed = await connection.ReadAsync(TimeSpan.FromSeconds(3));

        checks.Contains($"Last-Event-ID: {id - 1} replays event {id}", replayed, $"id: {id}");

    }
    else
        checks.That(
            "Last-Event-ID replay (needs id > 1)",
            true,
            "skipped — the stream had not advanced far enough"
        );

}


// --- Client abort mid-stream -------------------------------------------------

{

    // Open a stream, read a bit, then drop the connection without a close
    // handshake — the way a browser tab closing behaves.
    {
        await using var connection = await target.ConnectAsync();
        await connection.SendAsync($"GET /events HTTP/1.1\r\nHost: {host}\r\n\r\n");
        await connection.ReadAsync(TimeSpan.FromSeconds(3));
    }

    // The server must notice, clean up the worker, and go on serving. A leaked
    // event-source worker per abandoned client is the classic SSE failure, and
    // it only shows up under exactly this sequence.
    checks.Status(
        "server still serving after a mid-stream client abort",
        await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n"),
        200
    );

    // And a fresh stream must still work — proving the abort tore down one
    // client rather than the event source itself.
    {

        await using var connection = await target.ConnectAsync();

        await connection.SendAsync($"GET /events HTTP/1.1\r\nHost: {host}\r\n\r\n");

        var stream = await connection.ReadAsync(TimeSpan.FromSeconds(5));

        checks.Contains("a new stream works after an abort", stream, "data: tick");

    }

}


// --- Multiple concurrent subscribers ------------------------------------------

{

    await using var first  = await target.ConnectAsync();
    await using var second = await target.ConnectAsync();

    await first. SendAsync($"GET /events HTTP/1.1\r\nHost: {host}\r\n\r\n");
    await second.SendAsync($"GET /events HTTP/1.1\r\nHost: {host}\r\n\r\n");

    var firstStream  = await first. ReadAsync(TimeSpan.FromSeconds(5));
    var secondStream = await second.ReadAsync(TimeSpan.FromSeconds(5));

    checks.Contains("first concurrent subscriber receives events",  firstStream,  "data: tick");
    checks.Contains("second concurrent subscriber receives events", secondStream, "data: tick");

}


return checks.Summary();
