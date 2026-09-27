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
// h1desync — A6: where does a message end?
//
// A single origin server cannot smuggle a request past itself. The attack is
// a *disagreement*: the front end reads one message where the back end reads
// two, and the second one was never authorised by anybody. So this harness
// asks one question of every implementation it can reach — given these exact
// bytes, how many requests do you see? — and reports where the answers differ.
//
// It has two jobs, and they are deliberately separate:
//
//   Against our own server it ASSERTS, but only the requirements that RFC 9112
//   actually states. A Transfer-Encoding whose final coding is not chunked has
//   a MUST and a status code attached; a request carrying both Content-Length
//   and Transfer-Encoding does not, because Section 6.1 permits either answer
//   and only requires the close. Asserting our preference on the second kind
//   would dress taste up as conformance, so those probes carry no verdict.
//
//   Against everybody — ours and the foreign peers — it OBSERVES, in a
//   vocabulary small enough to compare: how many responses came back, what
//   they were, and whether the peer hung up. tests/smuggle.sh runs this mode
//   against three implementations and diffs the answers. A row where they
//   disagree is a gadget for any chain that puts one in front of the other.
//
// Usage:
//     h1desync                            # assert + report, against the demo
//     h1desync --observe                  # OBS lines only, for the differential
//     h1desync --list                     # the catalogue, with citations
//     h1desync --only cl-te               # one probe
//     h1desync --base http://host:port    # what the peer drivers pass
// ---------------------------------------------------------------------------

#region Arguments

var observe  = args.Contains("--observe");
var list     = args.Contains("--list");
var only     = ValueOf("--only");
var slice    = TimeSpan.FromMilliseconds(Int32.TryParse(ValueOf("--slice-ms"),  out var s) ? s : 350);
var budget   = TimeSpan.FromMilliseconds(Int32.TryParse(ValueOf("--budget-ms"), out var b) ? b : 2500);

// --base is what the shell drivers already speak (interop.sh, curl-matrix.sh),
// so h1desync accepts it too rather than making them special-case this one.
var rewritten = args.ToList();
var baseUrl   = ValueOf("--base");

if (baseUrl is not null && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
{
    rewritten.AddRange(["--host", uri.Host, "--port", uri.Port.ToString()]);
    if (uri.Scheme == "https")
        rewritten.Add("--tls");
}

var target = Target.FromArguments([.. rewritten]);
var host   = target.Authority;
var probes = ProbeCatalogue.All(host).
                 Where(probe => only is null || probe.Id == only).
                 ToList();

// Checks.Summary() prints "0/0 checks passed" and exits 0, so a harness that
// ran nothing is indistinguishable from one that passed everything. Most of
// the harnesses here have their checks written out by hand and cannot reach
// that state; this one builds them from a generated catalogue and takes
// --only, so it can, and says so instead.
if (probes.Count == 0)
{
    Console.Error.WriteLine(only is null
                                ? "  h1desync: the probe catalogue came back empty"
                                : $"  h1desync: no probe is named '{only}'");
    return 2;
}

if (only is null && !probes.Any(probe => probe.IsNormative))
{
    Console.Error.WriteLine("  h1desync: the catalogue has no probe left to assert on");
    return 2;
}

String? ValueOf(String Name)
{
    var index = Array.IndexOf(args, Name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

#endregion

#region --list

if (list)
{

    Console.WriteLine();
    Console.WriteLine("| Probe | What is ambiguous | Rule |");
    Console.WriteLine("|---|---|---|");

    foreach (var probe in probes)
        Console.WriteLine($"| `{probe.Id}` | {probe.Summary} | {probe.Citation ?? "*none — observed only*"} |");

    Console.WriteLine();
    Console.WriteLine($"{probes.Count} probes, {probes.Count(p => p.IsNormative)} of them with something to assert.");

    return 0;

}

#endregion

#region Observe one probe

// Read until the peer hangs up, or until it has been quiet for two slices,
// or until the budget runs out.
//
// The quiet-period exit is what keeps this affordable. A server that answers
// and keeps the connection open would otherwise cost the whole budget on
// every probe, and the question here — did a SECOND response arrive — is
// settled within milliseconds of the first: a pipelined request is already
// in the server's buffer when it finishes the one before it.
async Task<Observation> ObserveAsync(String Wire)
{

    await using var connection = await target.ConnectAsync();

    await connection.SendAsync(Wire);

    var text      = new StringBuilder();
    var deadline  = DateTimeOffset.UtcNow + budget;
    var quiet     = 0;

    while (quiet < 2 && DateTimeOffset.UtcNow < deadline)
    {

        var part = await connection.ReadAsync(slice);

        if (part.Length == 0)
            quiet++;
        else
        {
            quiet = 0;
            text.Append(part);
        }

        if (connection.PeerClosed)
            break;

    }

    var whole = text.ToString();

    return new Observation(
               Checks.ResponseCount(whole),
               Checks.StatusCodes  (whole),
               connection.PeerClosed
           );

}

#endregion

#region --observe: the machine-readable half

if (observe)
{

    // One line per probe, tab-separated, nothing else on stdout. The driver
    // joins these on the probe id across implementations, so the format is a
    // contract — tests/smuggle.sh parses it with cut.
    foreach (var probe in probes)
    {

        Observation observation;

        try
        {
            observation = await ObserveAsync(probe.Wire);
        }
        catch (Exception e)
        {
            Console.WriteLine($"OBS\t{probe.Id}\tERROR\t{e.GetType().Name}\t-");
            continue;
        }

        Console.WriteLine(
            $"OBS\t{probe.Id}\t{observation.Class}\t" +
            $"{(observation.Codes.Length > 0 ? String.Join(",", observation.Codes) : "-")}\t" +
            $"{(observation.Closed ? "closed" : "open")}"
        );

    }

    return 0;

}

#endregion

#region The default run: assert what the RFC requires, report the rest

var checks = new Checks("h1desync");

target.Banner("h1desync — A6: where does a message end?");

var observed = new List<(Probe, Observation)>();

foreach (var probe in probes)
{

    Observation observation;

    try
    {
        observation = await ObserveAsync(probe.Wire);
    }
    catch (Exception e)
    {
        checks.That($"{probe.Id} — {probe.Summary}", false, $"probe threw {e.GetType().Name}: {e.Message}");
        continue;
    }

    if (probe.Verdict is not null)
        checks.That(
            $"{probe.Id} — {probe.Summary}",
            probe.Verdict(observation),
            $"{observation}   [{probe.Citation}]"
        );

    else
        observed.Add((probe, observation));

}

if (observed.Count > 0)
{

    Console.WriteLine();
    Console.WriteLine("  observed, with no rule to assert against — the differential is in tests/smuggle.sh:");
    Console.WriteLine();

    foreach (var (probe, observation) in observed)
        Console.WriteLine($"    ~ {probe.Id,-22} {observation,-28} {probe.Summary}");

}

Console.WriteLine();

return checks.Summary();

#endregion
