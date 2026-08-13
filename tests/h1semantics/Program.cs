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
// h1semantics — RFC 9110 (methods, conditionals, ranges, negotiation, auth)
//               and RFC 10008 (QUERY)
//
// IMPORTANT — what is under test here.
//
// Hermod exposes If-None-Match, Range, Accept and friends as typed fields and
// applies no policy of its own: it is an origin server, and the resource
// decides what its own preconditions mean. So the 304s, 206s and negotiated
// variants below are produced by the *demo's handlers* (Demo/Program.cs,
// ServeResource), and this harness verifies that code at least as much as the
// library underneath it.
//
// That is not a gap. A harness reporting "no automatic 206" would be reporting
// a design decision. What it can honestly report is whether an origin server
// built on this library can implement RFC 9110 correctly — which is the
// question that actually matters.
// ---------------------------------------------------------------------------

var target = Target.FromArguments(args);
var checks = new Checks("h1semantics");

target.Banner("h1semantics — RFC 9110 semantics");

var host = target.Authority;


// --- §9 Methods -------------------------------------------------------------

checks.Status("GET /",   await target.RoundTripAsync($"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n"), 200);

{
    var response = await target.RoundTripAsync($"HEAD / HTTP/1.1\r\nHost: {host}\r\n\r\n", Bodyless: true);

    checks.Status        ("HEAD /",                      response, 200);
    checks.Contains      ("HEAD keeps representation metadata", response, "Content-Type:");
    checks.DoesNotContain("HEAD has no body",            response, "Hermod HTTP/1.1 demo host");
}

checks.Status(
    "POST /echo",
    await target.RoundTripAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Type: text/plain\r\nContent-Length: 4\r\n\r\nping"),
    200
);

checks.Contains(
    "POST /echo returns the request body",
    await target.RoundTripAsync($"POST /echo HTTP/1.1\r\nHost: {host}\r\nContent-Type: text/plain\r\nContent-Length: 4\r\n\r\nping"),
    "ping"
);

// §9.3.7 / §15.5.6: an unsupported method on a known resource is 405, and the
// response must carry Allow — otherwise the client cannot discover what to do.
{
    var response = await target.RoundTripAsync($"DELETE /files/resource.txt HTTP/1.1\r\nHost: {host}\r\n\r\n");

    checks.Status  ("DELETE on a GET-only resource", response, 405);
    checks.Contains("405 carries Allow",             response, "Allow:");
}

// §9.3.7: server-wide OPTIONS.
checks.Status("OPTIONS *", await target.RoundTripAsync($"OPTIONS * HTTP/1.1\r\nHost: {host}\r\n\r\n"), 200, 204);

{
    var response = await target.RoundTripAsync($"OPTIONS /files/resource.txt HTTP/1.1\r\nHost: {host}\r\n\r\n");

    checks.Status  ("OPTIONS on a resource", response, 204, 200);
    checks.Contains("resource OPTIONS lists methods", response, "Allow:");
}


// --- §8.8 Validators + §13 Conditional requests -----------------------------

{
    var response = await target.RoundTripAsync($"GET /files/resource.txt HTTP/1.1\r\nHost: {host}\r\n\r\n");

    checks.Status  ("GET /files/resource.txt", response, 200);
    checks.Contains("response carries an ETag",         response, "ETag:");
    checks.Contains("response carries Last-Modified",   response, "Last-Modified:");
    checks.Contains("response advertises Accept-Ranges", response, "Accept-Ranges: bytes");
}

{
    var response = await target.RoundTripAsync(
                       $"GET /files/resource.txt HTTP/1.1\r\nHost: {host}\r\nIf-None-Match: \"hermod-h1-demo-resource-v1\"\r\n\r\n"
                   );

    checks.Status        ("If-None-Match matching → 304", response, 304);
    checks.Contains      ("304 repeats the validator",    response, "ETag:");
    checks.DoesNotContain("304 has no body",              response, "Hello from the Hermod");
}

checks.Status(
    "If-None-Match: * → 304",
    await target.RoundTripAsync($"GET /files/resource.txt HTTP/1.1\r\nHost: {host}\r\nIf-None-Match: *\r\n\r\n"),
    304
);

checks.Status(
    "If-None-Match not matching → 200",
    await target.RoundTripAsync($"GET /files/resource.txt HTTP/1.1\r\nHost: {host}\r\nIf-None-Match: \"other\"\r\n\r\n"),
    200
);

checks.Status(
    "If-Modified-Since after Last-Modified → 304",
    await target.RoundTripAsync($"GET /files/resource.txt HTTP/1.1\r\nHost: {host}\r\nIf-Modified-Since: Wed, 01 Jul 2026 00:00:00 GMT\r\n\r\n"),
    304
);

checks.Status(
    "If-Modified-Since before Last-Modified → 200",
    await target.RoundTripAsync($"GET /files/resource.txt HTTP/1.1\r\nHost: {host}\r\nIf-Modified-Since: Mon, 01 Jan 2024 00:00:00 GMT\r\n\r\n"),
    200
);

// §13.1.3: If-None-Match takes precedence over If-Modified-Since when both are
// present, even when they disagree.
checks.Status(
    "If-None-Match wins over If-Modified-Since",
    await target.RoundTripAsync(
        $"GET /files/resource.txt HTTP/1.1\r\nHost: {host}\r\n" +
        "If-None-Match: \"other\"\r\nIf-Modified-Since: Wed, 01 Jul 2026 00:00:00 GMT\r\n\r\n"
    ),
    200
);


// --- §14 Range requests -----------------------------------------------------

{
    var response = await target.RoundTripAsync($"GET /files/resource.txt HTTP/1.1\r\nHost: {host}\r\nRange: bytes=0-4\r\n\r\n");

    checks.Status  ("Range: bytes=0-4 → 206",  response, 206);
    checks.Contains("206 carries Content-Range", response, "Content-Range: bytes 0-4/42");
    checks.Contains("206 body is the slice",     response, "Hello");
}

{
    var response = await target.RoundTripAsync($"GET /files/resource.txt HTTP/1.1\r\nHost: {host}\r\nRange: bytes=-6\r\n\r\n");

    checks.Status  ("suffix range bytes=-6 → 206", response, 206);
    checks.Contains("suffix range resolves from the end", response, "Content-Range: bytes 36-41/42");
}

{
    var response = await target.RoundTripAsync($"GET /files/resource.txt HTTP/1.1\r\nHost: {host}\r\nRange: bytes=10-\r\n\r\n");

    checks.Status  ("open-ended range bytes=10- → 206", response, 206);
    checks.Contains("open-ended range runs to the end", response, "Content-Range: bytes 10-41/42");
}

{
    var response = await target.RoundTripAsync($"GET /files/resource.txt HTTP/1.1\r\nHost: {host}\r\nRange: bytes=9999-\r\n\r\n");

    checks.Status  ("unsatisfiable range → 416", response, 416);
    checks.Contains("416 states the full length", response, "Content-Range: bytes */42");
}

// §14.2: an unrecognised range unit is ignored, not an error.
checks.Status(
    "unknown range unit is ignored",
    await target.RoundTripAsync($"GET /files/resource.txt HTTP/1.1\r\nHost: {host}\r\nRange: items=0-4\r\n\r\n"),
    200, 416
);


// --- §12 Content negotiation ------------------------------------------------

{
    var response = await target.RoundTripAsync($"GET /files/greeting HTTP/1.1\r\nHost: {host}\r\n\r\n");

    checks.Status  ("GET /files/greeting",            response, 200);
    checks.Contains("negotiated response carries Vary", response, "Vary:");
    checks.Contains("default variant is English",     response, "Hello World");
}

checks.Contains(
    "Accept-Language: de selects the German variant",
    await target.RoundTripAsync($"GET /files/greeting HTTP/1.1\r\nHost: {host}\r\nAccept-Language: de\r\n\r\n"),
    "Hallo Welt"
);

{
    var response = await target.RoundTripAsync($"GET /files/greeting HTTP/1.1\r\nHost: {host}\r\nAccept: application/json\r\n\r\n");

    checks.Contains("Accept: application/json selects JSON", response, "{\"greeting\"");
    checks.Contains("JSON variant declares its media type",  response, "application/json");
}


// --- §11 Authentication -----------------------------------------------------

{
    var response = await target.RoundTripAsync($"GET /secret HTTP/1.1\r\nHost: {host}\r\n\r\n");

    checks.Status  ("unauthenticated /secret → 401", response, 401);
    checks.Contains("401 carries a challenge",       response, "WWW-Authenticate:");
    checks.Contains("challenge names a realm",       response, "realm=");
}

// "alice:secret" base64-encoded — written out rather than computed so the
// expected wire bytes are visible in the source.
checks.Status(
    "Basic with valid credentials",
    await target.RoundTripAsync($"GET /secret HTTP/1.1\r\nHost: {host}\r\nAuthorization: Basic YWxpY2U6c2VjcmV0\r\n\r\n"),
    200
);

checks.Status(
    "Basic with wrong credentials",
    await target.RoundTripAsync($"GET /secret HTTP/1.1\r\nHost: {host}\r\nAuthorization: Basic YWxpY2U6d3Jvbmc=\r\n\r\n"),
    401
);

checks.Status(
    "Bearer with a valid token",
    await target.RoundTripAsync($"GET /secret HTTP/1.1\r\nHost: {host}\r\nAuthorization: Bearer valid-token-123\r\n\r\n"),
    200
);

checks.Status(
    "Bearer with an invalid token",
    await target.RoundTripAsync($"GET /secret HTTP/1.1\r\nHost: {host}\r\nAuthorization: Bearer nope\r\n\r\n"),
    401
);


// --- RFC 10008 QUERY ---------------------------------------------------------

{
    var response = await target.RoundTripAsync(
                       $"QUERY /search HTTP/1.1\r\nHost: {host}\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nap"
                   );

    checks.Status  ("QUERY with a fixed-length body", response, 200);
    checks.Contains("QUERY filters the corpus",       response, "apple");
    checks.Contains("QUERY result identifies itself", response, "Content-Location:");
    checks.DoesNotContain("QUERY excludes non-matches", response, "banana");
}

// The same query with a chunked body: QUERY's whole point is a safe method that
// carries content, so it has to work under both framings.
checks.Contains(
    "QUERY with a chunked body",
    await target.RoundTripAsync(
        $"QUERY /search HTTP/1.1\r\nHost: {host}\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nap\r\n0\r\n\r\n"
    ),
    "apple"
);


// --- §10.1.1 Expect: 100-continue -------------------------------------------

{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync(
              $"POST /expect HTTP/1.1\r\nHost: {host}\r\nContent-Type: text/plain\r\nContent-Length: 7\r\nExpect: 100-continue\r\n\r\n"
          );

    var interim = await connection.ReadAsync(TimeSpan.FromSeconds(2));

    checks.Contains("Expect: 100-continue → interim 100", interim, "100 Continue");

    await connection.SendAsync("payload");

    var final = await connection.ReadAsync(TimeSpan.FromSeconds(2));

    checks.Status("body accepted after 100 Continue", final, 200);

}

// §10.1.1: an expectation the server cannot meet is 417, and the body must not
// be read.
checks.Status(
    "unsupported expectation → 417",
    await target.RoundTripAsync(
        $"POST /expect HTTP/1.1\r\nHost: {host}\r\nContent-Length: 7\r\nExpect: the-impossible\r\n\r\n"
    ),
    417
);

// HTTP/1.0 has no interim responses; the body follows immediately.
{
    var response = await target.RoundTripAsync(
                       $"POST /expect HTTP/1.0\r\nHost: {host}\r\nContent-Type: text/plain\r\nContent-Length: 7\r\nExpect: 100-continue\r\n\r\npayload"
                   );

    checks.DoesNotContain("HTTP/1.0 gets no interim 100", response, "100 Continue");
}


// --- §15.4 Redirects ---------------------------------------------------------

foreach (var (code, expected) in new (String, UInt16)[] { ("301", 301), ("302", 302), ("303", 303), ("307", 307) })
{

    var response = await target.RoundTripAsync($"GET /redirect/{code} HTTP/1.1\r\nHost: {host}\r\n\r\n");

    checks.Status  ($"redirect {code}",            response, expected);
    checks.Contains($"redirect {code} has Location", response, "Location:");

}


return checks.Summary();
