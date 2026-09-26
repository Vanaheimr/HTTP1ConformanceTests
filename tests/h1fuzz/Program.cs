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

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

// ---------------------------------------------------------------------------
// h1fuzz — A10. Malformed input against the parsers, on purpose.
//
//   dotnet run -c Release --project tests/h1fuzz                  # 30 s budget
//   dotnet run -c Release --project tests/h1fuzz -- --seconds 300
//   dotnet run -c Release --project tests/h1fuzz -- chunked       # one target
//   dotnet run -c Release --project tests/h1fuzz -- --seed 1234
//   dotnet run -c Release --project tests/h1fuzz -- --replay artifacts/h1fuzz/<file>
//
// WHY THIS AND NOT SharpFuzz + AFL++
//
// The plan called for SharpFuzz driven by AFL++, and that is still the deeper
// instrument: coverage-guided mutation finds inputs that a blind mutator will
// not. It is not what is here, for reasons worth writing down rather than
// discovering again later.
//
//   - AFL++ is a system install (apt) and Linux-only, so a clean checkout on
//     Windows could not run it and CI would need a package step for a job that
//     takes hours.
//   - Coverage-guided fuzzing has no budget at which it is a *gate*. It is a
//     background activity whose findings arrive days later.
//
// What this is instead: a deterministic mutation fuzzer that needs nothing
// installed, runs in seconds to minutes, and is reproducible — every run is
// `--seed N`, and every finding is written out as the exact bytes plus the
// seed and iteration that produced it. It is cheap enough to gate on and
// specific enough to debug from.
//
// That is a weaker instrument honestly described, not a replacement. If AFL++
// is ever installed, the target functions below are the entry points to
// instrument, and the saved corpus is the seed set to hand it.
//
// WHAT COUNTS AS A FINDING
//
// Not "the parser rejected it" — that is the correct answer to almost all of
// this input, and a fuzzer that counted rejections would report noise. A
// finding is one of:
//
//   - an exception the target does not promise. A TryParse promises a Boolean,
//     so *any* exception out of it is a finding. The chunked decoder promises
//     to refuse malformed framing, so IOException and friends are correct and
//     NullReference, IndexOutOfRange and the Argument family are not.
//   - an input that takes longer than the deadline: a hang is a denial of
//     service whether or not it ever returns.
//   - output wildly out of proportion to input, which is how an allocation
//     bomb shows up before it becomes an OutOfMemoryException.
// ---------------------------------------------------------------------------

// Counts and timings from this harness are read out of CI logs and quoted
// in docs, so they must not depend on the machine's locale.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

#region Arguments

var targets     = new List<String>();
var seed        = 20260926;
var seconds     = 30;
var iterations  = 0;                 // 0 = run until the clock says stop
var deadline    = TimeSpan.FromSeconds(2);
String? replay  = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed"       when i + 1 < args.Length:  seed       = Int32.Parse(args[++i]); break;
        case "--seconds"    when i + 1 < args.Length:  seconds    = Int32.Parse(args[++i]); break;
        case "--iterations" when i + 1 < args.Length:  iterations = Int32.Parse(args[++i]); break;
        case "--replay"     when i + 1 < args.Length:  replay     = args[++i];              break;
        default:
            if (!args[i].StartsWith("--"))
                targets.Add(args[i].ToLowerInvariant());
            break;
    }
}

Boolean Wanted(String Name)
    => targets.Count == 0 || targets.Contains(Name);

#endregion

var findingsDirectory = Path.Combine("artifacts", "h1fuzz");

// Signatures this repository already knows about, one per line, "#" for a
// comment. A known finding is still reported - loudly, with its count - but
// does not fail the run, which is the same bargain tests/autobahn.sh strikes
// with its floor: a suite that is red for a reason everybody has read stops
// being read at all.
//
// Deleting a line here is how a fix gets verified.
var knownFile  = Path.Combine(AppContext.BaseDirectory, "known-findings.txt");
var known      = File.Exists(knownFile)
                     ? File.ReadAllLines(knownFile).
                            Select(line => line.Trim()).
                            Where (line => line.Length > 0 && !line.StartsWith('#')).
                            ToHashSet()
                     : [];

Console.WriteLine();
Console.WriteLine("  h1fuzz — malformed input against the HTTP/1.x parsers");

#if DEBUG
Console.WriteLine("  ⚠  built in Debug — the budget goes into the debugger. Use -c Release.");
#endif


// ===========================================================================
#region The targets
// ===========================================================================

// A TryParse promises a Boolean. Nothing it throws is expected.
static Boolean NothingIsExpected(Exception _)
    => false;

// The chunked decoder promises to refuse malformed framing, and refusing is
// what these mean. Everything else - a null dereference, an index off the end,
// a bad argument - is the decoder losing its footing rather than saying no.
static Boolean MalformedFramingIsExpected(Exception e)
    => e is IOException          // InvalidDataException is one of these
         or FormatException
         or OverflowException
         or NotSupportedException;

var requestSeeds = new[] {

    "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n",
    "GET /path?a=1&b=2 HTTP/1.1\r\nHost: localhost:8080\r\nAccept: */*\r\nUser-Agent: fuzz\r\n\r\n",
    "POST /echo HTTP/1.1\r\nHost: h\r\nContent-Length: 5\r\nContent-Type: text/plain\r\n\r\nhello",
    "POST /echo HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n",
    "HEAD /files/resource.txt HTTP/1.1\r\nHost: h\r\nRange: bytes=0-9\r\nIf-None-Match: \"v1\"\r\n\r\n",
    "OPTIONS * HTTP/1.1\r\nHost: h\r\n\r\n",
    "GET / HTTP/1.0\r\n\r\n",
    "QUERY /search HTTP/1.1\r\nHost: h\r\nContent-Length: 2\r\n\r\nap",
    "GET / HTTP/1.1\r\nHost: h\r\nForwarded: for=192.0.2.43;proto=https\r\nCookie: a=1; b=2\r\n\r\n",
    "GET / HTTP/1.1\r\nHost: h\r\nExpect: 100-continue\r\nContent-Length: 0\r\n\r\n",

}.Select(Encoding.Latin1.GetBytes).ToArray();

var responseSeeds = new[] {

    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Type: text/plain\r\n\r\nhi",
    "HTTP/1.1 204 No Content\r\n\r\n",
    "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes 0-9/42\r\nContent-Length: 10\r\n\r\n0123456789",
    "HTTP/1.1 304 Not Modified\r\nETag: \"v1\"\r\n\r\n",
    "HTTP/1.1 301 Moved Permanently\r\nLocation: /elsewhere\r\nContent-Length: 0\r\n\r\n",
    "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"x\"\r\nContent-Length: 0\r\n\r\n",
    "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nTrailer: X-Sum\r\n\r\n4\r\nbody\r\n0\r\nX-Sum: 1\r\n\r\n",
    "HTTP/1.0 200 OK\r\nConnection: close\r\n\r\nclose delimited",

}.Select(Encoding.Latin1.GetBytes).ToArray();

var chunkedSeeds = new[] {

    "5\r\nhello\r\n0\r\n\r\n",
    "4\r\nWiki\r\n5\r\npedia\r\n0\r\n\r\n",
    "a\r\n0123456789\r\n0\r\n\r\n",
    "4;ext=1\r\nWiki\r\n0\r\n\r\n",
    "4;flag\r\nWiki\r\n0\r\nX-Trailer: value\r\n\r\n",
    "0\r\n\r\n",
    "FF\r\n" + new String('x', 255) + "\r\n0\r\n\r\n",

}.Select(Encoding.Latin1.GetBytes).ToArray();

var allTargets = new List<Target> {

    new ("request",
         requestSeeds,
         bytes => HTTPRequest.TryParse(Encoding.Latin1.GetString(bytes), out _),
         NothingIsExpected),

    new ("response",
         responseSeeds,
         bytes => HTTPResponse.TryParse(
                      Encoding.Latin1.GetString(bytes).Split("\r\n"),
                      out _
                  ),
         NothingIsExpected),

    new ("chunked",
         chunkedSeeds,
         bytes => {

             using var source  = new MemoryStream(bytes);
             using var chunked = new ChunkedTransferEncodingStream(source, LeaveInnerStreamOpen: true);

             // Bounded on purpose: a decoder that produces far more than it was
             // given is the finding, and reading it to the end would turn that
             // finding into an OutOfMemoryException with no input attached.
             var ceiling = Math.Max(64 * 1024, bytes.Length * 1024);
             var buffer  = new Byte[16 * 1024];
             var total   = 0L;

             while (true)
             {

                 var read = chunked.Read(buffer, 0, buffer.Length);

                 if (read <= 0)
                     break;

                 total += read;

                 if (total > ceiling)
                     throw new FuzzFinding($"decoded {total} bytes from {bytes.Length} — more than {ceiling}");

             }

         },
         MalformedFramingIsExpected),

};

#endregion


// ===========================================================================
#region Replay
// ===========================================================================

if (replay is not null)
{

    var bytes  = File.ReadAllBytes(replay);
    var name   = Path.GetFileName(replay).Split('-')[0];
    var target = allTargets.FirstOrDefault(t => t.Name == name)
                     ?? throw new Exception($"cannot tell which target '{replay}' belongs to — expected the file name to start with one of: {String.Join(", ", allTargets.Select(t => t.Name))}");

    Console.WriteLine($"  replaying {bytes.Length} bytes against '{target.Name}'");
    Console.WriteLine();

    target.Run(bytes);

    Console.WriteLine("  returned without throwing.");
    return 0;

}

#endregion


// ===========================================================================
#region The loop
// ===========================================================================

var overall  = 0;
var budget   = TimeSpan.FromSeconds(seconds);

Console.WriteLine($"  seed {seed}, {(iterations > 0 ? $"{iterations:N0} iterations" : $"{seconds} s")} per target, {deadline.TotalSeconds:N0} s deadline per input");
Console.WriteLine();

foreach (var target in allTargets.Where(t => Wanted(t.Name)))
{

    // Seeded per target, so "--seed 1234 chunked" reproduces exactly what
    // "--seed 1234" did for chunked, whether or not the other targets ran.
    var random   = new Random(seed ^ target.Name.GetHashCode(StringComparison.Ordinal));
    var corpus   = new List<Byte[]>(target.Seeds);
    var started  = Stopwatch.GetTimestamp();
    var executed = 0;
    var slowest  = TimeSpan.Zero;

    // Deduplicated by (kind, message). One defect reached by a million inputs
    // is one defect, and a list of a million identical lines is a report
    // nobody reads - the first input that produced each signature is the one
    // saved, because it is usually the smallest.
    var findings = new Dictionary<String, (String Detail, Int32 Count, String File)>();

    void Found(String Kind, String Detail, Byte[] Input)
    {

        var signature = $"{Kind}: {Detail}";

        if (findings.TryGetValue(signature, out var seen))
            findings[signature] = (seen.Detail, seen.Count + 1, seen.File);

        else
            findings[signature] = (Detail, 1, Record(target.Name, Kind, Input, seed, executed, findingsDirectory));

    }

    while (iterations > 0
               ? executed < iterations
               : Stopwatch.GetElapsedTime(started) < budget)
    {

        var input = Mutate(corpus[random.Next(corpus.Count)], corpus, random);

        executed++;

        var inputStarted = Stopwatch.GetTimestamp();

        try
        {

            // The deadline is measured, not enforced: .NET cannot abort a
            // runaway loop from outside, and pretending otherwise with a
            // thread abort would make the harness less trustworthy than the
            // thing it is testing. A true infinite loop therefore hangs this
            // process - which is itself a visible failure, and the reason the
            // budget is small.
            target.Run(input);

        }
        catch (FuzzFinding finding)
        {
            Found("amplification", finding.Message, input);
        }
        catch (Exception e) when (target.Expected(e))
        {
            // The target keeping its promise: this input was malformed and it
            // said so.
        }
        catch (Exception e)
        {
            Found(e.GetType().Name, e.Message, input);
        }

        var took = Stopwatch.GetElapsedTime(inputStarted);

        if (took > slowest)
            slowest = took;

        if (took > deadline)
            Found("deadline", $"over {deadline.TotalSeconds:N0} s for {input.Length} bytes", input);

        // Anything that survived without a finding and is not enormous is
        // worth keeping as a base for later mutations. This is not coverage
        // guidance - it is the cheap half of it.
        if (findings.Count == 0 && input.Length < 4096 && random.Next(64) == 0)
            corpus.Add(input);

    }

    var elapsed = Stopwatch.GetElapsedTime(started);

    var unknown = findings.Where(f => !known.Contains(f.Key)).ToArray();

    foreach (var (signature, finding) in findings.Where(f => known.Contains(f.Key)).OrderByDescending(f => f.Value.Count))
        Console.WriteLine($"  ~ {target.Name,-9} known: {signature}  (×{finding.Count:N0})");

    if (unknown.Length == 0)
        Console.WriteLine($"  ✓ {target.Name,-9} {executed,9:N0} inputs in {elapsed.TotalSeconds,5:N1} s " +
                          $"({executed / elapsed.TotalSeconds,9:N0}/s), corpus {corpus.Count,4}, slowest input {slowest.TotalMilliseconds:N1} ms");

    else
    {

        overall = 1;

        Console.WriteLine($"  ✗ {target.Name,-9} {executed,9:N0} inputs in {elapsed.TotalSeconds,5:N1} s — {unknown.Length} NEW finding(s) from {unknown.Sum(f => f.Value.Count):N0} input(s)");

        foreach (var (signature, finding) in unknown.OrderByDescending(f => f.Value.Count))
            Console.WriteLine($"      {signature}  (×{finding.Count:N0})  →  {finding.File}");

    }

}

Console.WriteLine();

if (overall == 0)
    Console.WriteLine($"  h1fuzz: no findings (seed {seed})");
else
    Console.WriteLine($"  h1fuzz: findings written to {findingsDirectory}/ — replay one with --replay <file>");

Console.WriteLine();

return overall;

#endregion


// ===========================================================================
#region Mutation
// ===========================================================================

// Deterministic given the Random handed in. Nothing here is clever: the point
// of a blind mutator is volume and the seed corpus, and the operators that
// matter for HTTP are the ones that attack framing - CRLFs, digits, and
// length.
static Byte[] Mutate(Byte[] Input, List<Byte[]> Corpus, Random Random)
{

    var bytes = Input.ToArray();

    // Occasionally splice two corpus entries: a request header followed by
    // another request's body is exactly the shape a smuggling bug lives in.
    if (bytes.Length > 0 && Random.Next(8) == 0)
    {
        var other = Corpus[Random.Next(Corpus.Count)];
        var cut   = Random.Next(bytes.Length);
        bytes = [.. bytes.Take(cut), .. other];
    }

    var operations = 1 + Random.Next(4);

    for (var i = 0; i < operations && bytes.Length > 0; i++)
    {

        switch (Random.Next(9))
        {

            // a single bit
            case 0:
                bytes[Random.Next(bytes.Length)] ^= (Byte) (1 << Random.Next(8));
                break;

            // an "interesting" byte: the delimiters and the boundaries
            case 1:
                Byte[] interesting = [ 0x00, 0x09, 0x0A, 0x0D, 0x20, 0x3A, 0x3B, 0x2C, 0x7F, 0xFF ];
                bytes[Random.Next(bytes.Length)] = interesting[Random.Next(interesting.Length)];
                break;

            // truncate: every parser's favourite way to walk off the end
            case 2:
                bytes = bytes.Take(Random.Next(bytes.Length)).ToArray();
                break;

            // duplicate a run, which is how a header line becomes two
            case 3:
            {
                var from   = Random.Next(bytes.Length);
                var length = Math.Min(Random.Next(1, 64), bytes.Length - from);
                bytes = [.. bytes.Take(from + length), .. bytes.Skip(from).Take(length), .. bytes.Skip(from + length)];
                break;
            }

            // insert a run of one byte - long header names, long chunk sizes
            case 4:
            {
                var at    = Random.Next(bytes.Length);
                var count = Random.Next(1, 512);
                var fill  = (Byte) (Random.Next(2) == 0 ? Random.Next(0x21, 0x7F) : 0x30 + Random.Next(10));
                bytes = [.. bytes.Take(at), .. Enumerable.Repeat(fill, count), .. bytes.Skip(at)];
                break;
            }

            // corrupt a decimal or hexadecimal run into something enormous:
            // Content-Length and chunk-size are where this pays
            case 5:
            {
                var start = Random.Next(bytes.Length);
                var digits = "9999999999999999999999"u8.ToArray();
                var length = Math.Min(Random.Next(1, digits.Length), bytes.Length - start);
                Array.Copy(digits, 0, bytes, start, length);
                break;
            }

            // drop a byte
            case 6:
            {
                var at = Random.Next(bytes.Length);
                bytes = [.. bytes.Take(at), .. bytes.Skip(at + 1)];
                break;
            }

            // inject a bare CR or LF: the line-ending edge cases that
            // HTTPLineEndingTests exists for, produced at random
            case 7:
            {
                var at = Random.Next(bytes.Length);
                bytes = [.. bytes.Take(at), (Byte) (Random.Next(2) == 0 ? 0x0D : 0x0A), .. bytes.Skip(at)];
                break;
            }

            // swap two bytes
            default:
            {
                var a = Random.Next(bytes.Length);
                var b = Random.Next(bytes.Length);
                (bytes[a], bytes[b]) = (bytes[b], bytes[a]);
                break;
            }

        }

    }

    // A ceiling, so one unlucky run of insertions does not turn the budget
    // into a memory test.
    return bytes.Length > 256 * 1024
               ? bytes.Take(256 * 1024).ToArray()
               : bytes;

}

#endregion

#region Recording

static String Record(String   Target,
                     String   Kind,
                     Byte[]   Input,
                     Int32    Seed,
                     Int32    Iteration,
                     String   Directory)
{

    System.IO.Directory.CreateDirectory(Directory);

    var file = Path.Combine(Directory, $"{Target}-{Seed}-{Iteration}-{Kind}.bin");

    File.WriteAllBytes(file, Input);

    return $"{file} ({Input.Length} bytes)";

}

#endregion

/// <summary>
/// Not an error from the target — the harness's own way of saying "this input
/// produced output out of all proportion to itself".
/// </summary>
file sealed class FuzzFinding(String Message) : Exception(Message);

// A target is a name, a seed corpus, and a function that either returns
// normally or throws. `Expected` says which exceptions are the target keeping
// its promise rather than breaking it.
sealed record Target(
    String                   Name,
    IReadOnlyList<Byte[]>    Seeds,
    Action<Byte[]>           Run,
    Func<Exception, Boolean> Expected
);

