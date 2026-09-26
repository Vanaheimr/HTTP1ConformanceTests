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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.HTTP1.Tests;

// ---------------------------------------------------------------------------
// h1peer — OUR client against a foreign HTTP/1.1 server.
//
// This is the direction that had no independent witness. The server here has
// been judged by curl, by Autobahn, and by four foreign clients; the client had
// only ever talked to a server from the same source tree, which means every
// wire-visible assumption the two share was invisible to both.
//
// The peers are tests/peers/server.go and tests/peers/server.mjs - Go's
// net/http and Node's node:http, neither of which shares a line of code or a
// standard library with us or with each other. They serve the same five routes,
// so a check that passes against one and fails against the other says something
// specific rather than "somebody is wrong".
//
//   dotnet run --project tests/h1peer -- --base http://127.0.0.1:18080 --peer go
//
// tests/interop.sh starts the peers and passes the port it was told to listen
// on; nothing here starts a process of its own.
// ---------------------------------------------------------------------------

var baseURL  = "http://127.0.0.1:18080";
var peerName = "peer";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--base" when i + 1 < args.Length:  baseURL  = args[++i]; break;
        case "--peer" when i + 1 < args.Length:  peerName = args[++i]; break;
    }
}

var checks = new Checks($"h1peer/{peerName}");

Console.WriteLine();
Console.WriteLine($"  h1peer — our HTTPClient against {peerName} at {baseURL}");
Console.WriteLine();

// One client for the whole run, so the connection pool is exercised rather
// than sidestepped, and so the last check has something to reuse.
using var client = new HTTPClient(URL.Parse(baseURL)) {
                       AutomaticDecompression = true
                   };

var timeout = TimeSpan.FromSeconds(20);

async Task<HTTPResponse?> Get(String Path)
{
    try
    {
        return await client.RunRequest(
                         HTTPMethod.GET,
                         HTTPPath.Parse(Path),
                         RequestTimeout: timeout
                     );
    }
    catch (Exception e)
    {
        Console.WriteLine($"      (request to {Path} threw: {e.Message})");
        return null;
    }
}


// --- Content-Length framing, decided by their stack ------------------------

{
    var response = await Get("/plain");

    checks.That(
        "Content-Length body",
        response?.HTTPStatusCode.Code == 200 &&
        response.HTTPBodyAsUTF8String == $"hello from {peerName}\n",
        $"status {response?.HTTPStatusCode.Code}, body \"{response?.HTTPBodyAsUTF8String?.Replace("\n", "\\n")}\""
    );
}


// --- Chunked, framed by them and decoded by us -----------------------------

{
    var response = await Get("/chunked");

    checks.That(
        "chunked body",
        response?.HTTPStatusCode.Code == 200 &&
        response.HTTPBodyAsUTF8String == "one\ntwo\nthree\n",
        $"status {response?.HTTPStatusCode.Code}, body \"{response?.HTTPBodyAsUTF8String?.Replace("\n", "\\n")}\""
    );
}


// --- Their trailer section, collected by us --------------------------------
//
// The field arrives after the terminal chunk, which is the one place in HTTP/1
// where a header is read after the body. A client that stops at the zero chunk
// never sees it.

{
    var response = await Get("/trailers");

    var trailer = response is not null && response.TrailingHeaders.TryGetValue("X-Peer-Checksum", out var value)
                      ? value
                      : null;

    checks.That(
        "trailing body",
        response?.HTTPStatusCode.Code == 200 &&
        response.HTTPBodyAsUTF8String == "trailing body\n",
        $"status {response?.HTTPStatusCode.Code}, body \"{response?.HTTPBodyAsUTF8String?.Replace("\n", "\\n")}\""
    );

    checks.That(
        "trailer field after the terminal chunk",
        trailer == "c0ffee",
        $"X-Peer-Checksum \"{trailer}\", trailers seen: {response?.TrailingHeaders.Count ?? 0}"
    );
}


// --- Their gzip, undone by ours --------------------------------------------

{
    var response = await Get("/gzip");
    var expected = String.Concat(Enumerable.Repeat("compressible line\n", 64));

    checks.That(
        "content coding undone",
        response?.HTTPStatusCode.Code == 200 &&
        response.HTTPBodyAsUTF8String == expected,
        $"status {response?.HTTPStatusCode.Code}, {response?.HTTPBody?.Length ?? 0} bytes, " +
        $"Content-Encoding [{response?.ContentEncoding.AggregateWith(", ")}]"
    );
}


// --- A status they chose ----------------------------------------------------

{
    var response = await Get("/status/404");

    checks.That(
        "404 from the peer",
        response?.HTTPStatusCode.Code == 404,
        $"status {response?.HTTPStatusCode.Code}"
    );
}


// --- The connection survived all of it --------------------------------------

{
    var response = await Get("/plain");

    checks.That(
        "the connection is still usable",
        response?.HTTPStatusCode.Code == 200,
        $"status {response?.HTTPStatusCode.Code} after five earlier exchanges"
    );
}


return checks.Summary();
