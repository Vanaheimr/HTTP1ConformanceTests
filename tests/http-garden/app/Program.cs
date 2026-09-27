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

using System.Text;
using System.Security.Cryptography.X509Certificates;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

// ---------------------------------------------------------------------------
// Hermod as an HTTP Garden target.
//
// The Garden (github.com/narfindustries/http-garden) sends one payload to
// many servers and compares what each of them thought it received. For that
// to work, a target is not an ordinary server: it is a server wearing its
// parse tree on the outside. Every target answers with the same JSON document
// describing the request AS THAT SERVER UNDERSTOOD IT, and the Garden diffs
// the documents rather than the responses.
//
//     {"headers":[["<b64 name>","<b64 value>"], …],
//      "body":"<b64>","method":"<b64>","version":"<b64>","uri":"<b64>"}
//
// Base64 throughout, because half the point is fields whose bytes are not
// text: a header value carrying a bare CR, a method with a NUL in it, a URI
// that is not UTF-8. Anything that escaped rather than encoded would lose the
// distinction the Garden exists to find.
//
// Everything else about this program follows from the contract:
//
//   * 0.0.0.0:80 in the clear. The Garden decides where to connect with
//     port = x_props.get("port", 443 if requires_tls else 80), and most of
//     its targets - nginx, tornado, the rest - are plaintext on 80; only a
//     handful declare requires-tls. Plaintext is also the better choice on
//     the merits: Hermod's two listeners share one parser, so TLS would add
//     a variable to an experiment about parsing and nothing else. Setting
//     GARDEN_TLS=1 moves it to 443 with the certificate pair the Garden's
//     base image generates, for a run where that is wanted - the service
//     then needs "requires-tls: true" in its x-props.
//
//   * One catch-all route. A 404 from an unknown path would be Hermod's
//     ROUTER talking, and the Garden is asking about its PARSER; a target
//     that answered 404 to half the corpus would report agreement with
//     everybody on every payload it happened not to route.
//
//   * The headers reported are the PARSED ones, not the raw header block.
//     Hermod keeps both (AHTTPPDU.RawHTTPHeader is the record of what
//     arrived), and the raw one would be the wrong answer here: two servers
//     that received identical bytes always agree about the bytes. What can
//     differ - and what a smuggling chain is built out of - is what they
//     made of them.
// ---------------------------------------------------------------------------

var useTLS      = Environment.GetEnvironmentVariable("GARDEN_TLS") == "1";
var port        = UInt16.TryParse(Environment.GetEnvironmentVariable("GARDEN_PORT"), out var p)
                      ? p
                      : (UInt16) (useTLS ? 443 : 80);
var certPath    = Environment.GetEnvironmentVariable("GARDEN_CERT")     ?? "/app/garden.crt";
var certKeyPath = Environment.GetEnvironmentVariable("GARDEN_CERT_KEY") ?? "/app/garden.crt.key";

#region The certificate

// CreateFromPemFile hands back a certificate whose private key is ephemeral,
// and on Linux SslStream cannot always use one of those directly. The PKCS#12
// round trip is the usual remedy and costs nothing at startup.
X509Certificate2? certificate = null;

if (useTLS)
{
    try
    {
        using var fromPem = X509Certificate2.CreateFromPemFile(certPath, certKeyPath);
        certificate = X509CertificateLoader.LoadPkcs12(fromPem.Export(X509ContentType.Pkcs12), null);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"could not load {certPath} / {certKeyPath}: {e.Message}");
        return 1;
    }
}

#endregion

#region The server

var server = await HTTPServer.StartNew(
                       IPAddress:                  IPv4Address.Any,
                       TCPPort:                    IPPort.Parse(port),
                       HTTPServerName:             "Hermod",
                       ServerCertificateSelector:  certificate is null
                                                       ? null
                                                       : (tcpServer, tcpClient) => certificate
                   );

var api = server.AddHTTPAPI();

// "{rest..}" is Hermod's catch-the-remainder segment (HTTPAPI.cs, where a
// segment ending in "..}" sets CatchRestOfPath). Registered for the root as
// well, since the catcher needs at least one segment to catch.
//
// The method list is explicit rather than wildcarded because Hermod routes on
// a parsed HTTPMethod. A method outside this list is answered 405 - which the
// Garden still reads as a successful parse, since the JSON is what it
// compares and a 405 carries none. That is a real limitation of this target
// and it is written down rather than discovered: payloads whose interest is
// an exotic METHOD are not covered here.
foreach (var method in new[] {
             HTTPMethod.GET,     HTTPMethod.HEAD,    HTTPMethod.POST,
             HTTPMethod.PUT,     HTTPMethod.PATCH,   HTTPMethod.DELETE,
             HTTPMethod.OPTIONS, HTTPMethod.TRACE
         })
{
    foreach (var path in new[] { HTTPPath.Root, HTTPPath.Parse("/{rest..}") })
        api.AddHandler(method, path, HTTPDelegate: Describe);
}

Console.WriteLine($"LISTENING {port}");
Console.Out.Flush();

var forever = new TaskCompletionSource();
Console.CancelKeyPress += (sender, e) => { e.Cancel = true; forever.TrySetResult(); };
await forever.Task;

await server.Stop(Message: "garden target shutting down");

return 0;

#endregion


#region Describe(Request)

// The parse tree, as the Garden's contract spells it.
Task<HTTPResponse> Describe(HTTPRequest Request)
{

    var json = new StringBuilder();

    json.Append("{\"headers\":[");

    var first = true;

    // Sorted by name, because Hermod's parsed header representation is a
    // case-insensitive DICTIONARY: it has no order and it cannot hold two
    // field lines of the same name separately - RFC 9110 Section 5.3 says
    // they combine, and Hermod combines them at parse time. Emitting the
    // dictionary in its own enumeration order would make this target report
    // a different sequence for identical input from one run to the next,
    // which is worse than reporting a canonical one.
    //
    // The cost is stated rather than hidden: an ordering difference between
    // this target and another is an artefact of that dictionary and not a
    // finding, and a payload whose interest is the ORDER of two fields, or
    // the difference between "A: 1, 2" and two "A:" lines, is not visible
    // here. Payloads whose interest is the VALUE, the NAME, or whether a
    // field was accepted at all - which is most of them - are.
    foreach (var field in Request.OrderBy(f => f.Key, StringComparer.Ordinal))
    {

        if (!first)
            json.Append(',');

        first = false;

        json.Append('[').
             Append(Quoted(field.Key)).
             Append(',').
             Append(Quoted(field.Value?.ToString() ?? "")).
             Append(']');

    }

    json.Append("],\"body\":").   Append(QuotedBytes(Request.HTTPBody ?? [])).
         Append(",\"method\":").  Append(Quoted(Request.HTTPMethod.ToString())).
         Append(",\"version\":"). Append(Quoted(Request.ProtocolName + "/" + Request.ProtocolVersion)).
         Append(",\"uri\":").     Append(Quoted(Request.Path.ToString() + Request.QueryString.ToString())).
         Append('}');

    return Task.FromResult(
               new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = HTTPStatusCode.OK,
                   ContentType     = HTTPContentType.Application.JSON_UTF8,
                   Content         = Encoding.UTF8.GetBytes(json.ToString())
               }.AsImmutable
           );

}

// Latin1 rather than UTF8: these strings came off the wire as bytes and
// Hermod widened each one to a char. Latin1 narrows them back unchanged,
// where UTF8 would re-encode anything above 0x7f into two bytes and quietly
// rewrite the very payloads this target exists to report.
static String Quoted     (String Text)  => QuotedBytes(Encoding.Latin1.GetBytes(Text));
static String QuotedBytes(Byte[] Bytes) => "\"" + Convert.ToBase64String(Bytes) + "\"";

#endregion
