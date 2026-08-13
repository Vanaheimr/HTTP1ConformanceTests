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

using System.Text;

using org.GraphDefined.Vanaheimr.Hermod.HTTP1.Tests;

// ---------------------------------------------------------------------------
// h1raw — the diagnostic. Not part of the pass/fail gate.
//
// Send an arbitrary request from stdin or a literal and dump the reply with
// control characters made visible, so you can see the exact bytes rather than
// what a terminal decided to render. This is the tool you reach for when a
// harness check fails and you want to know what actually came back.
//
//   h1raw                                   # a canonical GET, as a smoke test
//   h1raw --request 'GET /x HTTP/1.1\r\nHost: h\r\n\r\n'
//   printf 'GET / HTTP/1.1\r\nHost: h\r\n\r\n' | h1raw --stdin
//   h1raw --tls --port 8443
// ---------------------------------------------------------------------------

var target = Target.FromArguments(args);

String? request = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {

        case "--request" when i + 1 < args.Length:
            // Accept the escapes as literal text so a request can be pasted
            // from a shell without fighting quoting rules.
            request = args[++i].Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\0", "\0");
            break;

        case "--stdin":
            request = await Console.In.ReadToEndAsync();
            break;

    }
}

request ??= $"GET / HTTP/1.1\r\nHost: {target.Authority}\r\n\r\n";

Console.WriteLine($"=== h1raw → {(target.TLS ? "https" : "http")}://{target.Authority} ===");
Console.WriteLine();
Console.WriteLine("--- request ---");
Console.WriteLine(Visible(request));
Console.WriteLine();

var response = await target.RoundTripAsync(request, TimeSpan.FromSeconds(5));

Console.WriteLine("--- response ---");
Console.WriteLine(response.Length == 0
                      ? "(nothing — connection closed without a reply)"
                      : Visible(response));
Console.WriteLine();
Console.WriteLine($"--- {response.Length} bytes, status {Checks.StatusOf(response)?.ToString() ?? "none"} ---");

return 0;


// Render CR and LF as visible markers while keeping the line structure, so a
// stray bare LF or a missing CR is something you can actually see.
static String Visible(String Text)
{

    var builder = new StringBuilder();

    foreach (var c in Text)
        switch (c)
        {
            case '\r':  builder.Append("\\r");            break;
            case '\n':  builder.Append("\\n\n");          break;
            case '\t':  builder.Append("\\t");            break;
            case < ' ': builder.Append($"\\x{(Int32) c:X2}"); break;
            default:    builder.Append(c);                break;
        }

    return builder.ToString();

}
