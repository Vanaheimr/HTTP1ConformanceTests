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

#region Usings

using System.Collections.Concurrent;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

// ---------------------------------------------------------------------------
// Hermod's HTTP/1.x client cache, standing in front of an origin.
//
// cache-tests (github.com/http-tests/cache-tests, the suite behind
// cache-tests.fyi) tests a cache by talking to it as if it were the origin:
// the client sends every request to the cache, the cache forwards what it
// cannot answer to the suite's own origin, and the origin counts what arrives.
// A cached response is one whose Server-Request-Count says the origin never
// saw that request. That is a reverse proxy, which is not something Hermod
// is. What Hermod has is AHTTPClient.Cache, so this program is the thinnest
// reverse proxy that puts that cache, unchanged, where the suite expects one:
//
//     cache-tests client ──▶ HTTPServer :8001 ──▶ HTTPClient + HTTPClientCache ──▶ origin :8000
//
// What is measured is HTTPClientCache. Everything else here is plumbing, and
// the plumbing has properties of its own that a result can trip over, so they
// are written down rather than discovered:
//
//   * One cache, many clients. AHTTPClient sends one request at a time
//     (a semaphore), and the suite runs 25 tests at once, some of whose
//     origin answers are paused for seconds. Every request rents an idle
//     client, or a new one, and every client shares the one cache. A cache
//     entry is keyed by the request's Host, which is always the origin's.
//
//   * No retries. The client retries a failed request by default, and the
//     suite reports a retry as a result of its own (↻). A proxy that retried
//     on the cache's behalf would make that result about this program.
//
//   * Field lines in, field lines out, from the raw header block in both
//     directions, so that a value reaches the other side as it arrived and
//     not as Hermod's typed model re-serialises it. Repeated field lines are
//     combined with ", " (RFC 9110, Section 5.3), because Hermod's builders
//     hold one value per name. For Set-Cookie that is wrong, and the suite's
//     client cannot read Set-Cookie anyway (Fetch forbids it).
//
//   * Hop-by-hop fields are not forwarded (RFC 9110, Section 7.6.1): the
//     fixed list, and whatever a Connection field names.
//
//   * Interim responses are not forwarded. The client consumes a 1xx and
//     hands back the final response only, so interim.mjs measures this proxy
//     and not the cache.
//
//   * A request that fails outright (the origin closed the connection, timed
//     out) is a 502, unless the cache had something to serve in its place,
//     which is the stale-if-error case and the cache's decision.
//
//   * Hermod-Cache on every response says what the cache did: Hit, Stale,
//     Revalidated, Miss or Unavailable, and nothing when it was bypassed. The
//     suite ignores it; its traces then show it beside each answer.
//
// Usage:
//     HermodCacheProxy [--port 8001] [--origin http://127.0.0.1:8000]
//                      [--mode shared|private|none] [--log]
//
// --mode none forwards without a cache. It exists to calibrate the run: what
// the suite reports then is the proxy alone, and every result that differs
// between it and --mode shared is the cache's.
// ---------------------------------------------------------------------------

var port    = (UInt16) 8001;
var origin  = "http://127.0.0.1:8000";
var mode    = (HTTPCacheMode?) HTTPCacheMode.Shared;
var log     = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {

        case "--port"   when i + 1 < args.Length:
            port   = UInt16.Parse(args[++i]);
            break;

        case "--origin" when i + 1 < args.Length:
            origin = args[++i];
            break;

        case "--mode"   when i + 1 < args.Length:
            mode   = args[++i] switch {
                         "shared"   => HTTPCacheMode.Shared,
                         "private"  => HTTPCacheMode.Private,
                         "none"     => null,
                         var other  => throw new ArgumentException($"unknown mode '{other}', expected shared, private or none")
                     };
            break;

        case "--log":
            log    = true;
            break;

        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 2;

    }
}

var originURL = URL.Parse(origin);
var cache     = mode is HTTPCacheMode cacheMode ? new HTTPClientCache(Mode: cacheMode) : null;
var idle      = new ConcurrentBag<HTTPClient>();

#region The server

var server = await HTTPServer.StartNew(
                       IPAddress:       IPv4Address.Localhost,
                       TCPPort:         IPPort.Parse(port),
                       HTTPServerName:  "Hermod cache proxy"
                   );

var api = server.AddHTTPAPI();

// "{rest..}" catches the remainder of a path, and needs at least one segment
// to catch, hence the root as well; see tests/http-garden/app/Program.cs. The
// methods are the ones the suite sends: GET and HEAD for most of it, PUT for
// its own configuration, and POST, PUT, DELETE and M-SEARCH as the unsafe
// methods invalidation.mjs tries.
foreach (var method in new[] {
             HTTPMethod.GET,     HTTPMethod.HEAD,    HTTPMethod.POST,
             HTTPMethod.PUT,     HTTPMethod.PATCH,   HTTPMethod.DELETE,
             HTTPMethod.OPTIONS, HTTPMethod.Parse("M-SEARCH")
         })
{
    foreach (var path in new[] { HTTPPath.Root, HTTPPath.Parse("/{rest..}") })
        api.AddHandler(method, path, HTTPDelegate: Forward);
}

Console.WriteLine($"LISTENING {port} -> {origin} ({mode?.ToString() ?? "no cache"})");
Console.Out.Flush();

var forever = new TaskCompletionSource();
Console.CancelKeyPress += (sender, e) => { e.Cancel = true; forever.TrySetResult(); };
await forever.Task;

await server.Stop(Message: "cache proxy shutting down");

return 0;

#endregion


#region Forward(Request)

async Task<HTTPResponse> Forward(HTTPRequest Request)
{

    var client = idle.TryTake(out var rented)
                     ? rented
                     : new HTTPClient(originURL) { Cache = cache };

    try
    {

        var requestFields = Combine(Fields(Request.RawHTTPHeader));
        var body          = Request.HTTPBody is { Length: > 0 } content ? content : null;

        var outgoing      = client.CreateRequest(
                                Request.HTTPMethod,
                                Request.Path,
                                Request.QueryString,
                                Content:         body,
                                RequestBuilder:  builder => {

                                                     // The client's own defaults, which the
                                                     // suite's client did not send.
                                                     builder.RemoveHeaderField("Accept");
                                                     builder.RemoveHeaderField("User-Agent");
                                                     builder.RemoveHeaderField("Content-Type");

                                                     foreach (var (name, value) in EndToEnd(requestFields))
                                                         builder.SetHeaderField(name, value);

                                                 }
                            );

        var response      = await client.SendRequest(
                                      outgoing.AsImmutable,
                                      ConsumeResponseChunkedTEImmediately:  true,
                                      RequestTimeout:                       TimeSpan.FromSeconds(30),
                                      // One attempt, no retry. Until H-48
                                      // the parameter counted attempts and
                                      // this read 1.
                                      MaxNumberOfRetries:                   0
                                  );

        if (log)
            Console.Error.WriteLine($"{Request.HTTPMethod} {Request.Path}{Request.QueryString} -> {response.HTTPStatusCode.Code} {response.CacheStatus?.ToString() ?? "bypassed"}");

        // The client's own failure, its reason in the body.
        if (response.HTTPStatusCode == HTTPStatusCode.ClientError)
        {

            var reason = response.HTTPBodyAsUTF8String ?? "no reason given";

            if (log)
                Console.Error.WriteLine($"    {reason}");

            return new HTTPResponse.Builder(Request) {
                       HTTPStatusCode  = HTTPStatusCode.BadGateway,
                       ContentType     = HTTPContentType.Text.PLAIN,
                       Content         = $"the origin did not answer: {reason}\n".ToUTF8Bytes()
                   }.AsImmutable;

        }

        var answer        = new HTTPResponse.Builder(Request) {
                                HTTPStatusCode = response.HTTPStatusCode
                            };

        foreach (var (name, value) in EndToEnd(Combine(Fields(response.RawHTTPHeader))))
            answer.SetHeaderField(name, value);

        if (response.CacheStatus is HTTPCacheStatus status)
            answer.SetHeaderField("Hermod-Cache", status.ToString());

        if (Request.HTTPMethod != HTTPMethod.HEAD && response.HTTPBody is { Length: > 0 } responseBody)
            answer.Content = responseBody;

        return answer.AsImmutable;

    }
    finally
    {
        idle.Add(client);
    }

}

#endregion

#region Fields(RawHeader)

// The field lines of a raw header block, after its start line, as they
// arrived: name, and the value without its surrounding whitespace.
static List<(String Name, String Value)> Fields(String RawHeader)
{

    var fields = new List<(String, String)>();

    foreach (var line in RawHeader.Split("\r\n").Skip(1))
    {

        var colon = line.IndexOf(':');

        if (colon > 0)
            fields.Add((line[..colon].Trim(), line[(colon + 1)..].Trim(' ', '\t')));

    }

    return fields;

}

#endregion

#region Combine(Fields)

// One value per name, in the order the names first appeared.
static List<(String Name, String Value)> Combine(List<(String Name, String Value)> Fields)
{

    var combined = new List<(String Name, String Value)>();
    var index    = new Dictionary<String, Int32>(StringComparer.OrdinalIgnoreCase);

    foreach (var (name, value) in Fields)
    {
        if (index.TryGetValue(name, out var at))
            combined[at] = (combined[at].Name, combined[at].Value + ", " + value);
        else
        {
            index[name] = combined.Count;
            combined.Add((name, value));
        }
    }

    return combined;

}

#endregion

#region EndToEnd(Fields)

// RFC 9110, Section 7.6.1: the hop-by-hop fields, and whatever a Connection
// field names, stay on their hop. Host and Content-Length belong to the
// message the builder writes, not to the one that arrived.
static IEnumerable<(String Name, String Value)> EndToEnd(List<(String Name, String Value)> Fields)
{

    var hopByHop = new HashSet<String>(StringComparer.OrdinalIgnoreCase) {
                       "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate",
                       "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
                       "Host", "Content-Length"
                   };

    foreach (var (name, value) in Fields)
        if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            foreach (var option in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                hopByHop.Add(option);

    return Fields.Where(field => !hopByHop.Contains(field.Name));

}

#endregion
