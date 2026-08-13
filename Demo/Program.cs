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

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

#endregion

namespace org.GraphDefined.Vanaheimr.Hermod.HTTP1.Demo
{

    /// <summary>
    /// The demo host every conformance harness in this repository drives against.
    ///
    /// Three listeners: cleartext HTTP on :8080, TLS on :8443 (self-signed cert
    /// generated at startup), and the WebSocket echo server on :8081.
    ///
    /// The WebSocket server is a *separate* listener rather than a route on the
    /// main port because Hermod's HTTP/1.x server has no Upgrade dispatch today
    /// (PLAN.md, H-16). When that changes, /ws moves onto :8080 and this becomes
    /// one listener less.
    ///
    /// A note on what the handlers below do, because it is easy to misread:
    /// several of them implement semantics that a framework might do for you —
    /// conditional requests, Range, content negotiation. Hermod deliberately
    /// leaves those to the handler (it is an origin server, not a framework), so
    /// implementing them here is not a workaround. It is the demo doing its job,
    /// and the h1semantics harness then verifies *this file* as much as the
    /// library underneath it.
    /// </summary>
    public static class Program
    {

        #region Data

        private const           UInt16    httpPort      = 8080;
        private const           UInt16    httpsPort     = 8443;
        private const           UInt16    wsPort        = 8081;

        /// <summary>
        /// The representation served by /files/resource.txt — fixed content with a
        /// fixed validator, so conditional requests and Range are reproducible
        /// across restarts. A demo whose ETag changes per run cannot be asserted on.
        /// </summary>
        private static readonly Byte[]    resourceBytes = "Hello from the Hermod HTTP/1.1 demo host!\n".ToUTF8Bytes();
        private static readonly String    resourceETag  = "\"hermod-h1-demo-resource-v1\"";
        private static readonly DateTime  resourceDate  = new (2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>The /search corpus, for the RFC 10008 QUERY method.</summary>
        private static readonly String[]  corpus        = [
                                                              "apple", "apricot", "avocado",
                                                              "banana", "blueberry",
                                                              "cherry", "cranberry",
                                                              "damson", "date"
                                                          ];

        #endregion


        #region Main(Arguments)

        public static async Task<Int32> Main(String[] Arguments)
        {

            var certificate = CreateSelfSignedCertificate("localhost");

            Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║   Hermod HTTP/1.1 demo host — conformance target              ║");
            Console.WriteLine("╠═══════════════════════════════════════════════════════════════╣");
            Console.WriteLine($"║   cleartext :  http://localhost:{httpPort}                          ║");
            Console.WriteLine($"║   TLS       :  https://localhost:{httpsPort}                         ║");
            Console.WriteLine($"║   WebSocket :  ws://localhost:{wsPort}                            ║");
            Console.WriteLine("║                                                               ║");
            Console.WriteLine("║   Try:                                                        ║");
            Console.WriteLine($"║     curl --http1.1 -v http://localhost:{httpPort}/                  ║");
            Console.WriteLine($"║     curl --http1.0 -v http://localhost:{httpPort}/                  ║");
            Console.WriteLine($"║     curl -v http://localhost:{httpPort}/chunked                     ║");
            Console.WriteLine($"║     curl -k -v https://localhost:{httpsPort}/                       ║");
            Console.WriteLine("║                                                               ║");
            Console.WriteLine("║   Press Ctrl+C to stop.                                       ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            // ---------------------------------------------------------------
            // Cleartext HTTP on :8080
            // ---------------------------------------------------------------
            var httpServer   = await HTTPServer.StartNew(
                                         TCPPort:         IPPort.Parse(httpPort),
                                         HTTPServerName:  "Hermod HTTP/1.1 Demo"
                                     );

            ConfigureAPI(httpServer.AddHTTPAPI());

            Console.WriteLine($"  ✓ cleartext listener on :{httpServer.TCPPort}");

            // ---------------------------------------------------------------
            // TLS on :8443
            // ---------------------------------------------------------------
            var httpsServer  = await HTTPServer.StartNew(
                                         TCPPort:                    IPPort.Parse(httpsPort),
                                         HTTPServerName:             "Hermod HTTP/1.1 Demo (TLS)",
                                         ServerCertificateSelector:  (tcpServer, tcpClient) => certificate
                                     );

            ConfigureAPI(httpsServer.AddHTTPAPI());

            Console.WriteLine($"  ✓ TLS listener on :{httpsServer.TCPPort}");

            // ---------------------------------------------------------------
            // WebSocket echo on :8081 — the Autobahn fuzzingclient target
            // ---------------------------------------------------------------
            var webSocketServer = new WebSocketServer(
                                      HTTPPort:               IPPort.Parse(wsPort),
                                      HTTPServerName:         "Hermod HTTP/1.1 Demo (WebSocket)",
                                      RequireAuthentication:  false,
                                      SecWebSocketProtocols:  [ "echo", "demo" ],
                                      AutoStart:              true
                                  );

            webSocketServer.OnTextMessageReceived   += async (timestamp, server, connection, frame, eventTrackingId, text, ct) =>
                await webSocketServer.SendTextMessage  (connection, text,   eventTrackingId, ct);

            webSocketServer.OnBinaryMessageReceived += async (timestamp, server, connection, frame, eventTrackingId, data, ct) =>
                await webSocketServer.SendBinaryMessage(connection, data,   eventTrackingId, ct);

            Console.WriteLine($"  ✓ WebSocket listener on :{wsPort}");
            Console.WriteLine();
            Console.WriteLine("  Ready.");
            Console.WriteLine();

            // ---------------------------------------------------------------
            // Run until Ctrl+C
            // ---------------------------------------------------------------
            var shutdown = new TaskCompletionSource();

            Console.CancelKeyPress += (sender, e) => {
                e.Cancel = true;
                Console.WriteLine();
                Console.WriteLine("  Shutting down …");
                shutdown.TrySetResult();
            };

            await shutdown.Task;

            await httpServer.     Stop    (Message: "demo host shutting down");
            await httpsServer.    Stop    (Message: "demo host shutting down");
            await webSocketServer.Shutdown(Message: "demo host shutting down");

            return 0;

        }

        #endregion


        #region ConfigureAPI(HTTPAPI)

        /// <summary>
        /// Register every demo route. Called once per listener so the cleartext
        /// and the TLS host expose an identical surface — otherwise a harness
        /// result would depend on which port it happened to hit.
        /// </summary>
        private static void ConfigureAPI(HTTPAPI API)
        {

            #region GET /  — the baseline: Content-Length framed, nothing clever

            API.AddHandler(
                HTTPMethod.GET,
                HTTPPath.Root,
                HTTPDelegate: request => Task.FromResult(
                    new HTTPResponse.Builder(request) {
                        HTTPStatusCode  = HTTPStatusCode.OK,
                        ContentType     = HTTPContentType.Text.PLAIN,
                        Content         = "Hermod HTTP/1.1 demo host\n".ToUTF8Bytes()
                    }.AsImmutable
                )
            );

            #endregion

            #region * /echo  — request body round-trip

            foreach (var method in new[] { HTTPMethod.POST, HTTPMethod.PUT, HTTPMethod.PATCH })
                API.AddHandler(
                    method,
                    HTTPPath.Root + "echo",
                    HTTPDelegate: request => Task.FromResult(
                        new HTTPResponse.Builder(request) {
                            HTTPStatusCode  = HTTPStatusCode.OK,
                            ContentType     = request.ContentType ?? HTTPContentType.Application.OCTETSTREAM,
                            Content         = request.HTTPBody ?? []
                        }.AsImmutable
                    )
                );

            #endregion

            #region GET /large  — 128 KiB, fixed-length

            API.AddHandler(
                HTTPMethod.GET,
                HTTPPath.Root + "large",
                HTTPDelegate: request => {

                    var payload = new Byte[128 * 1024];

                    for (var i = 0; i < payload.Length; i++)
                        payload[i] = (Byte) ('A' + (i % 26));

                    return Task.FromResult(
                        new HTTPResponse.Builder(request) {
                            HTTPStatusCode  = HTTPStatusCode.OK,
                            ContentType     = HTTPContentType.Application.OCTETSTREAM,
                            Content         = payload
                        }.AsImmutable
                    );

                }
            );

            #endregion

            #region GET /slow  — 2 s handler: client patience + connection reuse afterwards

            API.AddHandler(
                HTTPMethod.GET,
                HTTPPath.Root + "slow",
                HTTPDelegate: async request => {

                    await Task.Delay(TimeSpan.FromSeconds(2));

                    return new HTTPResponse.Builder(request) {
                               HTTPStatusCode  = HTTPStatusCode.OK,
                               ContentType     = HTTPContentType.Text.PLAIN,
                               Content         = "slow response\n".ToUTF8Bytes()
                           }.AsImmutable;

                }
            );

            #endregion

            #region GET /chunked  — Transfer-Encoding: chunked, with chunk extensions

            API.AddHandler(
                HTTPMethod.GET,
                HTTPPath.Root + "chunked",
                HTTPDelegate: request => Task.FromResult(
                    new HTTPResponse.Builder(request) {
                        HTTPStatusCode     = HTTPStatusCode.OK,
                        ContentType        = HTTPContentType.Text.PLAIN,
                        TransferEncoding   = "chunked",
                        // The server dispatches the worker on the *stream type*,
                        // not on the Transfer-Encoding field — setting only the
                        // latter yields correct headers and an empty body.
                        ContentStream      = new ChunkedTransferEncodingStream(request.NetworkStream!, true),
                        ChunkWorker        = async (response, stream) => {

                            // Three chunks, the middle one carrying both a token
                            // extension and a valueless (flag) one — the shapes
                            // RFC 9112 §7.1.1 allows and that a parser is most
                            // likely to get wrong.
                            await stream.WriteAsync("chunk-one\n".ToUTF8Bytes(),   null);
                            await stream.WriteAsync("chunk-two\n".ToUTF8Bytes(),   [
                                                        new ("kind", "demo"),
                                                        new ("flag", null)
                                                    ]);
                            await stream.WriteAsync("chunk-three\n".ToUTF8Bytes(), null);

                            await stream.Finish();

                        }
                    }.AsImmutable
                )
            );

            #endregion

            #region GET /trailers  — chunked + trailer fields after the terminal chunk

            API.AddHandler(
                HTTPMethod.GET,
                HTTPPath.Root + "trailers",
                HTTPDelegate: request => Task.FromResult(
                    new HTTPResponse.Builder(request) {
                        HTTPStatusCode    = HTTPStatusCode.OK,
                        ContentType       = HTTPContentType.Text.PLAIN,
                        TransferEncoding  = "chunked",
                        Trailer           = "X-Demo-Checksum, X-Demo-Duration",
                        ContentStream     = new ChunkedTransferEncodingStream(request.NetworkStream!, true),
                        ChunkWorker       = async (response, stream) => {

                            await stream.WriteAsync("body with trailers\n".ToUTF8Bytes(), null);

                            await stream.Finish(
                                new Dictionary<String, String> {
                                    { "X-Demo-Checksum", "deadbeef" },
                                    { "X-Demo-Duration", "0ms"      }
                                }
                            );

                        }
                    }.AsImmutable
                )
            );

            #endregion

            #region GET|HEAD /files/resource.txt  — conditional requests + Range

            foreach (var method in new[] { HTTPMethod.GET, HTTPMethod.HEAD })
                API.AddHandler(
                    method,
                    HTTPPath.Root + "files" + "resource.txt",
                    HTTPDelegate: request => Task.FromResult(ServeResource(request))
                );

            API.AddHandler(
                HTTPMethod.OPTIONS,
                HTTPPath.Root + "files" + "resource.txt",
                HTTPDelegate: request => {

                    var builder = new HTTPResponse.Builder(request) {
                                      HTTPStatusCode  = HTTPStatusCode.NoContent,
                                      Allow           = [ HTTPMethod.GET, HTTPMethod.HEAD, HTTPMethod.OPTIONS ]
                                  };

                    // Accept-Ranges is only modeled as a *request* field in Hermod
                    // (it is a response field per RFC 9110 §14.3), so it goes on
                    // the wire via the generic setter until that is fixed upstream.
                    builder.SetHeaderField("Accept-Ranges", "bytes");

                    return Task.FromResult(builder.AsImmutable);

                }
            );

            #endregion

            #region GET /files/greeting  — proactive content negotiation + Vary

            API.AddHandler(
                HTTPMethod.GET,
                HTTPPath.Root + "files" + "greeting",
                HTTPDelegate: request => {

                    var wantsJSON  = request.Accept.Any(a => a.ContentType == HTTPContentType.Application.JSON_UTF8);
                    var german     = request.AcceptLanguage?.Contains("de", StringComparison.OrdinalIgnoreCase) == true;

                    var body       = (wantsJSON, german) switch {
                                         (true,  true)   => "{\"greeting\":\"Hallo Welt\"}",
                                         (true,  false)  => "{\"greeting\":\"Hello World\"}",
                                         (false, true)   => "Hallo Welt\n",
                                         (false, false)  => "Hello World\n"
                                     };

                    return Task.FromResult(
                        new HTTPResponse.Builder(request) {
                            HTTPStatusCode  = HTTPStatusCode.OK,
                            ContentType     = wantsJSON
                                                  ? HTTPContentType.Application.JSON_UTF8
                                                  : HTTPContentType.Text.PLAIN,
                            ContentLanguage = [ german ? "de" : "en" ],
                            // Vary is the whole point of this route: a cache that
                            // ignores it would serve the German JSON to everyone.
                            Vary            = "Accept, Accept-Language",
                            Content         = body.ToUTF8Bytes()
                        }.AsImmutable
                    );

                }
            );

            #endregion

            #region GET /secret  — RFC 9110 §11: 401 + WWW-Authenticate, Basic and Bearer

            API.AddHandler(
                HTTPMethod.GET,
                HTTPPath.Root + "secret",
                HTTPDelegate: request => {

                    var authorized = request.Authorization switch {
                                         HTTPBasicAuthentication  basic   => basic.Username == "alice" &&
                                                                            basic.Password == "secret",
                                         HTTPBearerAuthentication bearer  => bearer.Token  == "valid-token-123",
                                         _                                => false
                                     };

                    if (authorized)
                        return Task.FromResult(
                            new HTTPResponse.Builder(request) {
                                HTTPStatusCode  = HTTPStatusCode.OK,
                                ContentType     = HTTPContentType.Text.PLAIN,
                                Content         = "the secret\n".ToUTF8Bytes()
                            }.AsImmutable
                        );

                    return Task.FromResult(
                        new HTTPResponse.Builder(request) {
                            HTTPStatusCode   = HTTPStatusCode.Unauthorized,
                            WWWAuthenticate  = WWWAuthenticate.Basic("Hermod HTTP/1.1 demo"),
                            ContentType      = HTTPContentType.Text.PLAIN,
                            Content          = "unauthorized\n".ToUTF8Bytes()
                        }.AsImmutable
                    );

                }
            );

            #endregion

            #region GET|QUERY /search  — RFC 10008: a safe, body-carrying read

            API.AddHandler(
                HTTPMethod.GET,
                HTTPPath.Root + "search",
                HTTPDelegate: request => Task.FromResult(
                    new HTTPResponse.Builder(request) {
                        HTTPStatusCode  = HTTPStatusCode.OK,
                        ContentType     = HTTPContentType.Text.PLAIN,
                        Content         = String.Join("\n", corpus).ToUTF8Bytes()
                    }.AsImmutable
                )
            );

            API.AddHandler(
                HTTPMethod.QUERY,
                HTTPPath.Root + "search",
                HTTPDelegate: request => {

                    var needle  = (request.HTTPBodyAsUTF8String ?? "").Trim();
                    var hits    = corpus.Where(entry => entry.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToArray();

                    var builder = new HTTPResponse.Builder(request) {
                                      HTTPStatusCode  = HTTPStatusCode.OK,
                                      ContentType     = HTTPContentType.Text.PLAIN,
                                      Content         = String.Join("\n", hits).ToUTF8Bytes()
                                  };

                    // RFC 10008 §2.2: the response describes the result of applying
                    // the query, so give it its own identity via Content-Location.
                    builder.SetContentLocation($"/search?q={WebUtility.UrlEncode(needle)}");

                    return Task.FromResult(builder.AsImmutable);

                }
            );

            #endregion

            #region POST /expect  — Expect: 100-continue, and 417 for anything else

            API.AddHandler(
                HTTPMethod.POST,
                HTTPPath.Root + "expect",
                HTTPDelegate: request => Task.FromResult(
                    new HTTPResponse.Builder(request) {
                        HTTPStatusCode  = HTTPStatusCode.OK,
                        ContentType     = HTTPContentType.Text.PLAIN,
                        Content         = $"received {request.HTTPBody?.Length ?? 0} bytes\n".ToUTF8Bytes()
                    }.AsImmutable
                )
            );

            #endregion

            #region GET /redirect/{code}  — 301 302 303 307 (308 blocked on H-1)

            API.AddHandler(
                HTTPMethod.GET,
                HTTPPath.Root + "redirect" + "{code}",
                HTTPDelegate: request => {

                    var status = request.ParsedURLParametersX.TryGetValue("code", out var code) &&
                                 UInt16.TryParse(code, out var parsed)
                                     ? parsed
                                     : (UInt16) 302;

                    // 308 is deliberately absent from the list: Hermod has no
                    // HTTPStatusCode for it (PLAN.md, H-1). Once that lands, add
                    // it here and the harness picks it up.
                    var redirect = status switch {
                                       301 => HTTPStatusCode.MovedPermanently,
                                       303 => HTTPStatusCode.SeeOther,
                                       307 => HTTPStatusCode.TemporaryRedirect,
                                       _   => HTTPStatusCode.Found
                                   };

                    return Task.FromResult(
                        new HTTPResponse.Builder(request) {
                            HTTPStatusCode  = redirect,
                            Location        = Location.Parse("/"),
                            ContentType     = HTTPContentType.Text.PLAIN,
                            Content         = $"redirecting with {redirect.Code}\n".ToUTF8Bytes()
                        }.AsImmutable
                    );

                }
            );

            #endregion

            #region GET /status/{code}  — arbitrary status codes for the drivers

            API.AddHandler(
                HTTPMethod.GET,
                HTTPPath.Root + "status" + "{code}",
                HTTPDelegate: request => {

                    var status = request.ParsedURLParametersX.TryGetValue("code", out var code) &&
                                 UInt16.TryParse(code, out var parsed)
                                     ? HTTPStatusCode.ParseString(parsed.ToString())
                                     : HTTPStatusCode.OK;

                    return Task.FromResult(
                        new HTTPResponse.Builder(request) {
                            HTTPStatusCode  = status,
                            ContentType     = HTTPContentType.Text.PLAIN,
                            Content         = $"status {status.Code}\n".ToUTF8Bytes()
                        }.AsImmutable
                    );

                }
            );

            #endregion

            #region GET /events  — Server-Sent Events

            var eventSource = API.AddEventSource<String>(
                                  HTTPEventSource_Id.Parse("demoEvents"),
                                  MaxNumberOfCachedEvents:  100,
                                  RetryInterval:            TimeSpan.FromSeconds(5),
                                  EnableLogging:            false
                              );

            API.MapEventSource<String>(
                eventSource,
                HTTPPath.Root + "events",
                RequireAuthentication: false
            );

            // A ticker, so a connected client sees something without another
            // process having to poke the demo.
            _ = Task.Run(async () => {

                var counter = 0;

                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    await eventSource.SubmitEvent("tick", $"tick {++counter}");
                }

            });

            #endregion

        }

        #endregion

        #region ServeResource(Request)

        /// <summary>
        /// /files/resource.txt — conditional requests (RFC 9110 §13) and Range
        /// (RFC 9110 §14), both implemented *here* rather than by the library.
        ///
        /// Hermod exposes If-None-Match / If-Modified-Since / Range as typed
        /// fields and applies no policy of its own, which is the documented
        /// design: an origin server hands its handlers the information and lets
        /// them decide what the resource means. So this method is what the
        /// h1semantics harness actually exercises.
        /// </summary>
        private static HTTPResponse ServeResource(HTTPRequest Request)
        {

            // --- Conditional: If-None-Match wins over If-Modified-Since (§13.1.3)
            if (Request.IfNoneMatch is not null)
            {
                if (Request.IfNoneMatch == "*" ||
                    Request.IfNoneMatch.Split(',').Any(tag => tag.Trim().TrimStart('W', '/') == resourceETag))
                {
                    return NotModified(Request);
                }
            }
            else if (Request.IfModifiedSince is not null &&
                     DateTimeOffset.TryParse(Request.IfModifiedSince, out var since) &&
                     resourceDate <= since.UtcDateTime)
            {
                return NotModified(Request);
            }

            // --- Range: a single byte range only; anything else falls back to 200
            if (Request.Range is not null)
                return ServeRange(Request);

            var builder = new HTTPResponse.Builder(Request) {
                              HTTPStatusCode  = HTTPStatusCode.OK,
                              ContentType     = HTTPContentType.Text.PLAIN,
                              ETag            = resourceETag,
                              LastModified    = resourceDate,
                              Content         = resourceBytes
                          };

            builder.SetHeaderField("Accept-Ranges", "bytes");

            return builder.AsImmutable;

        }

        #endregion

        #region ServeRange(Request)

        private static HTTPResponse ServeRange(HTTPRequest Request)
        {

            var spec = (Request.Range ?? "").Trim();

            if (!spec.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                return RangeNotSatisfiable(Request);

            var parts = spec[6..].Split('-');

            if (parts.Length != 2)
                return RangeNotSatisfiable(Request);

            Int64 from, to;
            var   length = resourceBytes.Length;

            if (parts[0].Length == 0)
            {
                // suffix-byte-range-spec: "bytes=-N" — the last N bytes
                if (!Int64.TryParse(parts[1], out var suffix) || suffix <= 0)
                    return RangeNotSatisfiable(Request);

                from = Math.Max(0, length - suffix);
                to   = length - 1;
            }
            else
            {
                if (!Int64.TryParse(parts[0], out from) || from < 0)
                    return RangeNotSatisfiable(Request);

                to = parts[1].Length == 0
                         ? length - 1
                         : Int64.TryParse(parts[1], out var explicitTo)
                               ? Math.Min(explicitTo, length - 1)
                               : -1;

                if (to < 0)
                    return RangeNotSatisfiable(Request);
            }

            if (from >= length || from > to)
                return RangeNotSatisfiable(Request);

            var slice   = resourceBytes[(Int32) from .. (Int32) (to + 1)];

            var builder = new HTTPResponse.Builder(Request) {
                              HTTPStatusCode  = HTTPStatusCode.PartialContent,
                              ContentType     = HTTPContentType.Text.PLAIN,
                              ETag            = resourceETag,
                              LastModified    = resourceDate,
                              Content         = slice
                          };

            builder.SetHeaderField("Accept-Ranges", "bytes");
            builder.SetContentRange($"bytes {from}-{to}/{length}");

            return builder.AsImmutable;

        }

        #endregion

        #region NotModified / RangeNotSatisfiable

        /// <summary>
        /// RFC 9110 §15.4.5: a 304 carries the validators it would have sent with
        /// a 200, and no body.
        /// </summary>
        private static HTTPResponse NotModified(HTTPRequest Request)

            => new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = HTTPStatusCode.NotModified,
                   ETag            = resourceETag,
                   LastModified    = resourceDate
               }.AsImmutable;

        private static HTTPResponse RangeNotSatisfiable(HTTPRequest Request)
        {

            var builder = new HTTPResponse.Builder(Request) {
                              HTTPStatusCode = HTTPStatusCode.RequestedRangeNotSatisfiable
                          };

            builder.SetContentRange($"bytes */{resourceBytes.Length}");

            return builder.AsImmutable;

        }

        #endregion

        #region CreateSelfSignedCertificate(HostName)

        /// <summary>
        /// A self-signed certificate for the TLS listener, generated at startup so
        /// nothing has to be checked in and nothing expires in the repository.
        /// </summary>
        private static X509Certificate2 CreateSelfSignedCertificate(String HostName)
        {

            using var rsa = RSA.Create(2048);

            var request = new CertificateRequest(
                              $"CN={HostName}",
                              rsa,
                              HashAlgorithmName.SHA256,
                              RSASignaturePadding.Pkcs1
                          );

            // Fully qualified: Hermod has its own IPAddress in scope here.
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(HostName);
            sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
            sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(sanBuilder.Build());

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    [ new Oid("1.3.6.1.5.5.7.3.1") ],   // serverAuth
                    critical: false
                )
            );

            var certificate = request.CreateSelfSigned(
                                  DateTimeOffset.UtcNow.AddDays(-1),
                                  DateTimeOffset.UtcNow.AddYears(1)
                              );

            // On Windows an ephemeral key cannot be used by SslStream directly —
            // round-tripping through a PKCS#12 export attaches a usable key handle.
            return X509CertificateLoader.LoadPkcs12(
                       certificate.Export(X509ContentType.Pfx),
                       null,
                       X509KeyStorageFlags.Exportable
                   );

        }

        #endregion

    }

}
