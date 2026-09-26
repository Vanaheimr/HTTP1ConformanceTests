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

using System.Diagnostics;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

// ---------------------------------------------------------------------------
// h1bench — A9. What this stack costs, with something to compare it to.
//
//   dotnet run -c Release --project tests/h1bench              # everything
//   dotnet run -c Release --project tests/h1bench -- get       # one scenario
//   dotnet run -c Release --project tests/h1bench -- --mib 256
//
// WHAT THESE NUMBERS ARE, AND ARE NOT
//
// Client and server run in the *same process* over loopback. That makes the
// harness self-contained and the figures reproducible, and it means every
// number covers the whole round trip - our client, our server, and the
// loopback stack, sharing one machine's cores. They are for tracking this
// stack against itself over time.
//
// They are not a comparison with nginx. The one comparison that is here is
// Kestrel, on the same loopback, in the same process, driven by the same
// HttpClient: an absolute number with no control is how "slower than I
// expected" gets mistaken for "slow", and the HTTP/2 sibling learned that the
// expensive way - its per-request latency turned out to be *better* than
// Kestrel's while its throughput ceiling was a real defect.
//
// Allocation is GC.GetTotalAllocatedBytes, which is process-wide, so "bytes
// per request" covers both roles. That is the honest figure for what one
// request costs this stack.
//
// This is not a pass/fail gate and is not in CI. It is the baseline any
// optimisation has to beat, and the thing to re-run before believing one
// worked.
// ---------------------------------------------------------------------------

// Figures from this harness get quoted in READMEs and read out of CI logs, so
// they must not depend on the machine's locale: "1.403 MiB/s" and "1,403 MiB/s"
// are the same measurement and a factor of a thousand apart to a reader.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

#region Arguments

var scenarios = new List<String>();
var payloadMiB = 64;
var requests   = 2000;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--mib"      when i + 1 < args.Length:  payloadMiB = Int32.Parse(args[++i]); break;
        case "--requests" when i + 1 < args.Length:  requests   = Int32.Parse(args[++i]); break;
        default:
            if (!args[i].StartsWith("--"))
                scenarios.Add(args[i].ToLowerInvariant());
            break;
    }
}

Boolean Wanted(String Name)
    => scenarios.Count == 0 || scenarios.Contains(Name);

#endregion

Console.WriteLine();
Console.WriteLine("  h1bench — Hermod HTTP/1.1");
Console.WriteLine($"  {Environment.OSVersion}, {Environment.ProcessorCount} logical cores, .NET {Environment.Version}");
Console.WriteLine($"  server GC: {System.Runtime.GCSettings.IsServerGC}");

#if DEBUG
Console.WriteLine();
Console.WriteLine("  ⚠  built in Debug — these numbers measure the debugger. Use -c Release.");
#endif

Console.WriteLine();


// ===========================================================================
#region In-memory: the parser, with no socket in the way
// ===========================================================================

if (Wanted("parse"))
{

    Section("Request parsing (no sockets)");

    var requestText = String.Concat(
                          "GET /some/resource/path?q=benchmark&n=7 HTTP/1.1\r\n",
                          "Host: 127.0.0.1:8080\r\n",
                          "User-Agent: h1bench/1.0\r\n",
                          "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8\r\n",
                          "Accept-Language: en-GB,en;q=0.9,de;q=0.8\r\n",
                          "Accept-Encoding: gzip, deflate, br\r\n",
                          "Connection: keep-alive\r\n",
                          "Cache-Control: max-age=0\r\n",
                          "Upgrade-Insecure-Requests: 1\r\n",
                          "Cookie: session=abcdef0123456789; theme=dark\r\n",
                          "\r\n"
                      );

    // Warm up the JIT and whatever static tables the parser touches on first
    // use, so the measured loop is steady state rather than first-run cost.
    for (var i = 0; i < 1_000; i++)
        HTTPRequest.TryParse(requestText, out _);

    var count = 200_000;
    var (elapsed, allocated) = Measure(() => {
        for (var i = 0; i < count; i++)
            HTTPRequest.TryParse(requestText, out _);
    });

    Console.WriteLine($"  a 10-field request header, {requestText.Length} bytes");
    Console.WriteLine($"  {count / elapsed.TotalSeconds,12:N0} parses/s");
    Console.WriteLine($"  {(Double) allocated / count,12:N0} bytes allocated per parse");
    Console.WriteLine();

}

#endregion

// ===========================================================================
#region In-memory: chunked framing
// ===========================================================================

if (Wanted("chunked"))
{

    Section("Chunked transfer coding (no sockets)");

    var chunk  = new Byte[8 * 1024];
    Random.Shared.NextBytes(chunk);

    var chunks = 4_000;

    var (encodeTime, encodeAlloc) = Measure(() => {
        using var sink    = new MemoryStream();
        using var chunked = new ChunkedTransferEncodingStream(sink, LeaveInnerStreamOpen: true);
        for (var i = 0; i < chunks; i++)
            chunked.Write(chunk, 0, chunk.Length);
        chunked.Finish().AsTask().GetAwaiter().GetResult();
    });

    var framed = new MemoryStream();
    {
        using var chunked = new ChunkedTransferEncodingStream(framed, LeaveInnerStreamOpen: true);
        for (var i = 0; i < chunks; i++)
            chunked.Write(chunk, 0, chunk.Length);
        chunked.Finish().AsTask().GetAwaiter().GetResult();
    }

    var payload = (Double) chunks * chunk.Length / (1024 * 1024);

    var (decodeTime, decodeAlloc) = Measure(() => {
        framed.Position = 0;
        using var chunked = new ChunkedTransferEncodingStream(framed, LeaveInnerStreamOpen: true);
        using var sink    = new MemoryStream();
        chunked.CopyTo(sink);
    });

    Console.WriteLine($"  {chunks:N0} chunks of {chunk.Length / 1024} KiB — {payload:N0} MiB, framed as {framed.Length / (1024.0 * 1024):N1} MiB");
    Console.WriteLine($"  {payload / encodeTime.TotalSeconds,12:N0} MiB/s encode   ({encodeAlloc / (1024.0 * 1024),6:N1} MiB allocated)");
    Console.WriteLine($"  {payload / decodeTime.TotalSeconds,12:N0} MiB/s decode   ({decodeAlloc / (1024.0 * 1024),6:N1} MiB allocated)");
    Console.WriteLine();

}

#endregion

// ===========================================================================
#region Networked scenarios
// ===========================================================================

if (Wanted("get") || Wanted("download") || Wanted("upload") || Wanted("connect") || Wanted("control"))
{

    var smallBody = "pong\n".ToUTF8Bytes();
    var largeBody = new Byte[payloadMiB * 1024 * 1024];
    Random.Shared.NextBytes(largeBody);

    // The default request-body ceiling is 8 MiB (AHTTPPDU.DefaultMaxHTTPBodySize),
    // which the upload scenario is deliberately larger than. Raising it here is
    // a benchmark decision, not a recommendation: a server that accepts 64 MiB
    // request bodies from anyone has made a different decision than this default.
    var server = new HTTPServer(
                     TCPPort:          IPPort.Zero,
                     MaxHTTPBodySize:  (UInt64) (payloadMiB + 8) * 1024 * 1024,
                     AutoStart:        true
                 );

    var api = new HTTPAPI(server);

    api.AddHandler(HTTPPath.Root + "ping",
                   HTTPMethod:    HTTPMethod.GET,
                   HTTPDelegate:  request => Task.FromResult(
                                      new HTTPResponse.Builder(request) {
                                          HTTPStatusCode  = HTTPStatusCode.OK,
                                          ContentType     = HTTPContentType.Text.PLAIN,
                                          Content         = smallBody
                                      }.AsImmutable));

    api.AddHandler(HTTPPath.Root + "large",
                   HTTPMethod:    HTTPMethod.GET,
                   HTTPDelegate:  request => Task.FromResult(
                                      new HTTPResponse.Builder(request) {
                                          HTTPStatusCode  = HTTPStatusCode.OK,
                                          ContentType     = HTTPContentType.Application.OCTETSTREAM,
                                          Content         = largeBody
                                      }.AsImmutable));

    api.AddHandler(HTTPPath.Root + "sink",
                   HTTPMethod:    HTTPMethod.POST,
                   HTTPDelegate:  request => Task.FromResult(
                                      new HTTPResponse.Builder(request) {
                                          HTTPStatusCode  = HTTPStatusCode.OK,
                                          ContentType     = HTTPContentType.Text.PLAIN,
                                          Content         = $"{request.HTTPBody?.Length ?? 0}\n".ToUTF8Bytes()
                                      }.AsImmutable));

    var ourBase = URL.Parse($"http://127.0.0.1:{server.TCPPort}");

    // Shared on purpose wherever more than one client is built: see the
    // "persistent vs. fresh connection" scenario for what the default costs.
    var sharedDNS = new org.GraphDefined.Vanaheimr.Hermod.DNS.DNSClient();

    Console.WriteLine($"  our server on {ourBase}");
    Console.WriteLine();

    // ---------------------------------------------------------- small GET

    if (Wanted("get"))
    {

        Section("Small GET — our client, our server");

        // Two shapes, and the difference between them is the point.
        //
        // One HTTPClient is one connection, and HTTP/1.1 has no multiplexing:
        // callers on it queue, by the protocol's design rather than by any
        // defect. So "64 concurrent on one client" measures the depth of that
        // queue, and the flat req/s that comes out of it is the correct answer
        // to the wrong question - it is what HTTPClientPool exists for.
        //
        // The second shape gives each concurrent caller its own client, which
        // is where the server's own parallelism becomes visible.
        Console.WriteLine("  one client — callers queue on a single connection (HTTP/1.1 has no multiplexing)");
        Console.WriteLine();

        foreach (var concurrency in new[] { 1, 8, 64 })
        {

            using var client = new HTTPClient(ourBase);

            // Warm up: the first request on a client pays for connection
            // setup and for every lazily-built table in both roles.
            for (var i = 0; i < 50; i++)
                await client.RunRequest(HTTPMethod.GET, HTTPPath.Root + "ping", RequestTimeout: TimeSpan.FromSeconds(30));

            var samples   = new List<Double>(requests);
            var lockObj   = new Object();
            var remaining = requests;

            var (elapsed, allocated) = await MeasureAsync(async () => {

                var workers = Enumerable.Range(0, concurrency).Select(async _ => {

                    while (true)
                    {

                        lock (lockObj)
                        {
                            if (remaining <= 0) return;
                            remaining--;
                        }

                        var started = Stopwatch.GetTimestamp();
                        await client.RunRequest(HTTPMethod.GET, HTTPPath.Root + "ping", RequestTimeout: TimeSpan.FromSeconds(30));
                        var took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                        lock (lockObj)
                            samples.Add(took);

                    }

                });

                await Task.WhenAll(workers);

            });

            remaining = requests;

            Console.WriteLine($"  {concurrency,3} concurrent — {requests / elapsed.TotalSeconds,9:N0} req/s, " +
                              $"{(Double) allocated / requests,8:N0} bytes/request");
            Percentiles($"      latency", samples);

        }

        Console.WriteLine();
        Console.WriteLine("  one client per caller — a connection each, so the server's own parallelism shows");
        Console.WriteLine();

        foreach (var concurrency in new[] { 8, 64 })
        {

            var clients = Enumerable.Range(0, concurrency).Select(_ => new HTTPClient(ourBase, DNSClient: sharedDNS)).ToArray();

            foreach (var client in clients)
                await client.RunRequest(HTTPMethod.GET, HTTPPath.Root + "ping", RequestTimeout: TimeSpan.FromSeconds(30));

            var samples   = new List<Double>(requests);
            var lockObj   = new Object();
            var remaining = requests;

            var (elapsed, allocated) = await MeasureAsync(async () => {

                await Task.WhenAll(clients.Select(async client => {

                    while (true)
                    {

                        lock (lockObj)
                        {
                            if (remaining <= 0) return;
                            remaining--;
                        }

                        var started = Stopwatch.GetTimestamp();
                        await client.RunRequest(HTTPMethod.GET, HTTPPath.Root + "ping", RequestTimeout: TimeSpan.FromSeconds(30));
                        var took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                        lock (lockObj)
                            samples.Add(took);

                    }

                }));

            });

            Console.WriteLine($"  {concurrency,3} clients    — {requests / elapsed.TotalSeconds,9:N0} req/s, " +
                              $"{(Double) allocated / requests,8:N0} bytes/request");
            Percentiles($"      latency", samples);

            foreach (var client in clients)
                client.Dispose();

        }

        Console.WriteLine();

    }

    // ----------------------------------------------------------- download

    if (Wanted("download"))
    {

        Section($"Download — {payloadMiB} MiB body");

        using var client = new HTTPClient(ourBase);

        await client.RunRequest(HTTPMethod.GET, HTTPPath.Root + "large", RequestTimeout: TimeSpan.FromMinutes(5));

        var (elapsed, allocated) = await MeasureAsync(async () => {
            var response = await client.RunRequest(HTTPMethod.GET, HTTPPath.Root + "large", RequestTimeout: TimeSpan.FromMinutes(5));
            if (response.HTTPBody?.Length != largeBody.Length)
                throw new Exception($"got {response.HTTPBody?.Length ?? 0} bytes, expected {largeBody.Length}");
        });

        Console.WriteLine($"  {payloadMiB / elapsed.TotalSeconds,12:N0} MiB/s");
        Console.WriteLine($"  {(Double) allocated / largeBody.Length,12:N2}× the payload allocated");
        Console.WriteLine();

    }

    // ------------------------------------------------------------- upload

    if (Wanted("upload"))
    {

        Section($"Upload — {payloadMiB} MiB body");

        using var client = new HTTPClient(ourBase);

        var (elapsed, allocated) = await MeasureAsync(async () => {
            var response = await client.RunRequest(
                                     HTTPMethod.POST,
                                     HTTPPath.Root + "sink",
                                     RequestBuilder: builder => {
                                         builder.ContentType = HTTPContentType.Application.OCTETSTREAM;
                                         builder.Content     = largeBody;
                                     },
                                     RequestTimeout: TimeSpan.FromMinutes(5)
                                 );
            if (response.HTTPStatusCode.Code != 200)
                throw new Exception($"status {response.HTTPStatusCode.Code}");
        });

        Console.WriteLine($"  {payloadMiB / elapsed.TotalSeconds,12:N0} MiB/s");
        Console.WriteLine($"  {(Double) allocated / largeBody.Length,12:N2}× the payload allocated");
        Console.WriteLine();

    }

    // --------------------------------------------- what a connection costs

    if (Wanted("connect"))
    {

        Section("Persistent vs. fresh connection");

        // The HTTP/1.1 question that has no HTTP/2 equivalent: a request on a
        // connection that is already open, against the same request paying for
        // a TCP handshake and a fresh client.
        var count = Math.Min(requests, 500);

        using (var reused = new HTTPClient(ourBase))
        {

            await reused.RunRequest(HTTPMethod.GET, HTTPPath.Root + "ping", RequestTimeout: TimeSpan.FromSeconds(30));

            var samples = new List<Double>(count);

            for (var i = 0; i < count; i++)
            {
                var started = Stopwatch.GetTimestamp();
                await reused.RunRequest(HTTPMethod.GET, HTTPPath.Root + "ping", RequestTimeout: TimeSpan.FromSeconds(30));
                samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            Percentiles("  kept open  ", samples);

        }

        // Split into construction and first request. "A fresh connection costs
        // N ms" is not a finding until it says which half of the work N is in,
        // and on loopback a TCP handshake is microseconds - so anything larger
        // is being spent somewhere else.
        {

            var whole        = new List<Double>(count);
            var construction = new List<Double>(count);
            var firstRequest = new List<Double>(count);

            for (var i = 0; i < count; i++)
            {

                var started = Stopwatch.GetTimestamp();

                using var fresh = new HTTPClient(ourBase);

                var constructed = Stopwatch.GetTimestamp();

                await fresh.RunRequest(HTTPMethod.GET, HTTPPath.Root + "ping", RequestTimeout: TimeSpan.FromSeconds(30));

                var done = Stopwatch.GetTimestamp();

                construction.Add(Stopwatch.GetElapsedTime(started,     constructed).TotalMilliseconds);
                firstRequest.Add(Stopwatch.GetElapsedTime(constructed, done).       TotalMilliseconds);
                whole.       Add(Stopwatch.GetElapsedTime(started,     done).       TotalMilliseconds);

            }

            Percentiles("  new each time", whole);
            Percentiles("    ↳ constructing the client", construction);
            Percentiles("    ↳ its first request      ", firstRequest);

        }

        // The same loop with one DNS client shared between them. If this
        // collapses, the cost above is not the connection and not the request:
        // it is ATCPClient's `DNSClient ?? new DNSClient(...)`, whose default
        // searches the machine's network configuration for resolvers - once
        // per client, and even when the URL is a literal address that will
        // never be resolved.
        {

            var samples = new List<Double>(count);

            for (var i = 0; i < count; i++)
            {
                var started = Stopwatch.GetTimestamp();
                using var fresh = new HTTPClient(ourBase, DNSClient: sharedDNS);
                await fresh.RunRequest(HTTPMethod.GET, HTTPPath.Root + "ping", RequestTimeout: TimeSpan.FromSeconds(30));
                samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            Percentiles("  new each time, shared DNS client", samples);

        }

        Console.WriteLine();

    }

    // ------------------------------------------------- the Kestrel control

    if (Wanted("control"))
    {

        Section("Control — .NET HttpClient against both servers");

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var kestrel = builder.Build();
        kestrel.MapGet("/ping", () => Results.Text("pong\n", "text/plain"));

        await kestrel.StartAsync();

        var kestrelBase = kestrel.Services
                                 .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                                 .Features
                                 .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                                 .Addresses
                                 .First();

        using var http = new System.Net.Http.HttpClient {
                             DefaultRequestVersion       = new Version(1, 1),
                             DefaultVersionPolicy        = System.Net.Http.HttpVersionPolicy.RequestVersionExact,
                             Timeout                     = TimeSpan.FromSeconds(30)
                         };

        async Task<List<Double>> Drive(String Base, Int32 Count)
        {

            for (var i = 0; i < 50; i++)
                (await http.GetAsync($"{Base}/ping")).EnsureSuccessStatusCode();

            var samples = new List<Double>(Count);

            for (var i = 0; i < Count; i++)
            {
                var started = Stopwatch.GetTimestamp();
                var response = await http.GetAsync($"{Base}/ping");
                await response.Content.ReadAsByteArrayAsync();
                samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            return samples;

        }

        var count = Math.Min(requests, 1000);

        Percentiles("  HttpClient → Hermod ", await Drive(ourBase.ToString().TrimEnd('/'), count));
        Percentiles("  HttpClient → Kestrel", await Drive(kestrelBase.TrimEnd('/'),        count));

        await kestrel.StopAsync();

        Console.WriteLine();
        Console.WriteLine("  Same process, same loopback, same client, one after the other. The");
        Console.WriteLine("  control is what makes the left column mean anything.");
        Console.WriteLine();

    }

    await server.Stop();

}

#endregion


// ===========================================================================
#region Measurement
// ===========================================================================

static void Section(String Title)
{
    Console.WriteLine($"  ── {Title} " + new String('─', Math.Max(0, 62 - Title.Length)));
    Console.WriteLine();
}

static (TimeSpan Elapsed, Int64 Allocated) Measure(Action Work)
{

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before  = GC.GetTotalAllocatedBytes(precise: true);
    var started = Stopwatch.GetTimestamp();

    Work();

    var elapsed = Stopwatch.GetElapsedTime(started);
    var after   = GC.GetTotalAllocatedBytes(precise: true);

    return (elapsed, after - before);

}

static async Task<(TimeSpan Elapsed, Int64 Allocated)> MeasureAsync(Func<Task> Work)
{

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before  = GC.GetTotalAllocatedBytes(precise: true);
    var started = Stopwatch.GetTimestamp();

    await Work();

    var elapsed = Stopwatch.GetElapsedTime(started);
    var after   = GC.GetTotalAllocatedBytes(precise: true);

    return (elapsed, after - before);

}

// p50 and p99 rather than a mean: a mean hides the tail, and the tail is where
// a lock, a timer or a buffer growth shows up.
static void Percentiles(String Label, List<Double> Samples)
{

    if (Samples.Count == 0)
    {
        Console.WriteLine($"  {Label}: no samples");
        return;
    }

    var sorted = Samples.OrderBy(sample => sample).ToArray();

    Double At(Double Fraction)
        => sorted[Math.Clamp((Int32) (Fraction * sorted.Length), 0, sorted.Length - 1)];

    Console.WriteLine($"  {Label}  p50 {At(0.50),7:N3} ms   p90 {At(0.90),7:N3} ms   " +
                      $"p99 {At(0.99),7:N3} ms   max {sorted[^1],7:N3} ms   n={sorted.Length}");

}

#endregion
