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

namespace org.GraphDefined.Vanaheimr.Hermod.HTTP1.Tests
{

    /// <summary>
    /// Per-check pass/fail reporting for a harness.
    ///
    /// Every harness prints one ✓/✗ line per check and exits non-zero if any
    /// failed, so the runner can rely on the exit code alone and does not have
    /// to scrape output for a marker character.
    /// </summary>
    public sealed class Checks(String Harness)
    {

        #region Data

        private readonly List<String> failures = [];
        private          Int32        total;

        #endregion

        #region That (Condition, Label, Detail = null)

        public void That(String   Label,
                         Boolean  Condition,
                         String?  Detail   = null)
        {

            total++;

            if (Condition)
                Console.WriteLine($"  \u2713 {Label}");

            else
            {

                failures.Add(Label);

                Console.WriteLine($"  \u2717 {Label}");

                if (Detail is not null)
                    Console.WriteLine($"      {Summarize(Detail)}");

            }

        }

        #endregion

        #region Status (Label, Response, ExpectedStatus)

        /// <summary>
        /// The most common assertion: the response's status line carries one of
        /// the expected codes.
        ///
        /// Several codes are accepted per check on purpose. RFC 9112 frequently
        /// says a recipient MUST reject a construct without saying *how*, so
        /// pinning a single code would be asserting Hermod's taste rather than
        /// the standard — 400 and 501 are both conformant answers to an unknown
        /// transfer coding, for instance.
        /// </summary>
        public void Status(String          Label,
                           String          Response,
                           params UInt16[] ExpectedStatus)
        {

            var actual = StatusOf(Response);

            That(
                $"{Label} → {String.Join(" | ", ExpectedStatus)}",
                actual.HasValue && ExpectedStatus.Contains(actual.Value),
                actual.HasValue
                    ? $"got {actual.Value}: {FirstLine(Response)}"
                    : $"no status line in: {Summarize(Response)}"
            );

        }

        #endregion

        #region Closed (Label, Response)

        /// <summary>
        /// The server answered nothing at all — the expected outcome when a
        /// connection is torn down rather than answered.
        /// </summary>
        public void Closed(String Label, String Response)

            => That(
                   $"{Label} → connection closed without a response",
                   Response.Length == 0,
                   $"got: {Summarize(Response)}"
               );

        #endregion

        #region Contains / DoesNotContain

        public void Contains(String Label, String Response, String Needle)

            => That(
                   $"{Label} → contains \"{Needle}\"",
                   Response.Contains(Needle, StringComparison.OrdinalIgnoreCase),
                   $"got: {Summarize(Response)}"
               );

        public void DoesNotContain(String Label, String Response, String Needle)

            => That(
                   $"{Label} → does not contain \"{Needle}\"",
                   !Response.Contains(Needle, StringComparison.OrdinalIgnoreCase),
                   $"got: {Summarize(Response)}"
               );

        #endregion

        #region (static) StatusOf / FirstLine / ResponseCount

        /// <summary>
        /// Walk the buffer as a sequence of HTTP/1.x responses, yielding each
        /// one's offset and status code.
        ///
        /// This is a real walk rather than a substring count, and it got there
        /// the hard way. Counting occurrences of "HTTP/1." over-counts: the
        /// demo's error responses carry <c>Server: Hermod HTTP/1.1 Demo</c>, so
        /// one 400 read as two responses. Anchoring the match to the start of a
        /// line fixes that and under-counts instead: a pipelined response whose
        /// predecessor's body does not end in CRLF — <c>helloHTTP/1.1 404</c>
        /// straight off a foreign peer's wire — is then invisible.
        ///
        /// Both failures are silent, and the number they get wrong is the only
        /// observable the A6 differential has. So the framing is parsed: status
        /// line, header section, then the body the framing announces, and on to
        /// the next. Anything that does not parse ends the walk, because a
        /// harness must never invent a response it cannot account for.
        ///
        /// Limitation, stated rather than discovered later: a reply to HEAD
        /// carries a Content-Length describing content it does not send, and
        /// nothing in the response itself says so. 1xx, 204 and 304 are handled
        /// — they are bodyless by rule — but a HEAD reply would send the walk
        /// looking for a body that is not there. No caller counts HEAD replies;
        /// use RoundTripAsync(Bodyless: true) for those.
        /// </summary>
        private static IEnumerable<(Int32 Offset, UInt16 Code)> Responses(String Response)
        {

            var position = 0;

            while (position < Response.Length)
            {

                #region The status line

                var statusEnd = EndOfLine(Response, position);

                if (statusEnd < 0 || !TryStatusCode(Response, position, statusEnd, out var code))
                    yield break;

                yield return (position, code);

                #endregion

                #region The header section — only the two fields that frame a body

                var cursor   = StartOfNextLine(Response, statusEnd);
                var chunked  = false;
                Int64? declaredLength = null;

                while (true)
                {

                    var lineEnd = EndOfLine(Response, cursor);

                    if (lineEnd < 0)
                        yield break;                      // truncated mid-header

                    if (lineEnd == cursor)                // the blank line
                    {
                        cursor = StartOfNextLine(Response, lineEnd);
                        break;
                    }

                    var line  = Response[cursor..lineEnd];
                    var colon = line.IndexOf(':');

                    if (colon > 0)
                    {

                        var name  = line[..colon].Trim();
                        var value = line[(colon + 1)..].Trim();

                        if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                            Int64.TryParse(value, out var parsed))
                            declaredLength = parsed;

                        else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                                 value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                            chunked = true;

                    }

                    cursor = StartOfNextLine(Response, lineEnd);

                }

                #endregion

                #region The body

                // RFC 9110 Section 6.4.1: these three never carry content,
                // whatever their fields claim.
                if (code is (>= 100 and < 200) or 204 or 304)
                    position = cursor;

                else if (chunked)
                {

                    var afterChunks = EndOfChunkedBody(Response, cursor);

                    if (afterChunks < 0)
                        yield break;

                    position = afterChunks;

                }

                else if (declaredLength is Int64 length)
                {

                    if (length < 0 || cursor + length > Response.Length)
                        yield break;

                    position = cursor + (Int32) length;

                }

                // No Content-Length and no chunked coding: the body runs to
                // the end of the connection, so nothing can follow it.
                else
                    yield break;

                #endregion

            }

        }

        #region (static) EndOfLine / StartOfNextLine / TryStatusCode / EndOfChunkedBody

        /// <summary>
        /// The index of the line terminator at or after From, accepting a bare
        /// LF as well as CRLF — some of the peers this walks answer with one.
        /// </summary>
        private static Int32 EndOfLine(String Text, Int32 From)
        {

            var index = Text.IndexOf('\n', From);

            if (index < 0)
                return -1;

            return index > From && Text[index - 1] == '\r'
                       ? index - 1
                       : index;

        }

        private static Int32 StartOfNextLine(String Text, Int32 EndOfLine)
            => EndOfLine < Text.Length && Text[EndOfLine] == '\r'
                   ? EndOfLine + 2
                   : EndOfLine + 1;

        private static Boolean TryStatusCode(String   Text,
                                             Int32    Start,
                                             Int32    End,
                                             out UInt16  Code)
        {

            Code = 0;

            if (End - Start < 12 ||
                !Text.AsSpan(Start).StartsWith("HTTP/1.", StringComparison.Ordinal) ||
                Text[Start + 7] is not ('0' or '1') ||
                Text[Start + 8] != ' ')
                return false;

            return UInt16.TryParse(Text.AsSpan(Start + 9, 3), out Code);

        }

        /// <summary>
        /// Where a chunked body ends, trailer section included, or -1 if the
        /// buffer runs out first.
        /// </summary>
        private static Int32 EndOfChunkedBody(String Text, Int32 From)
        {

            var cursor = From;

            while (true)
            {

                var lineEnd = EndOfLine(Text, cursor);

                if (lineEnd < 0)
                    return -1;

                var sizeText  = Text[cursor..lineEnd];
                var semicolon = sizeText.IndexOf(';');

                if (semicolon >= 0)
                    sizeText = sizeText[..semicolon];

                if (!Int32.TryParse(sizeText.Trim(),
                                    System.Globalization.NumberStyles.HexNumber,
                                    null,
                                    out var size) || size < 0)
                    return -1;

                cursor = StartOfNextLine(Text, lineEnd);

                if (size == 0)
                {

                    // The trailer section, then the blank line that ends it.
                    while (true)
                    {

                        var trailerEnd = EndOfLine(Text, cursor);

                        if (trailerEnd < 0)
                            return -1;

                        var wasBlank = trailerEnd == cursor;
                        cursor = StartOfNextLine(Text, trailerEnd);

                        if (wasBlank)
                            return cursor;

                    }

                }

                if (cursor + size > Text.Length)
                    return -1;

                cursor = StartOfNextLine(Text, EndOfLine(Text, cursor + size) is var e && e < 0 ? cursor + size : e);

            }

        }

        #endregion

        /// <summary>
        /// The status code of the first response in the buffer, if any.
        /// </summary>
        public static UInt16? StatusOf(String Response)
        {

            foreach (var (_, code) in Responses(Response))
                return code;

            return null;

        }

        /// <summary>
        /// Every status code in the buffer, in order. One response carrying a
        /// 400 and two responses carrying a 200 and a 418 are two different
        /// readings of the same bytes, and which one an implementation
        /// produced is what a differential probe compares.
        /// </summary>
        public static UInt16[] StatusCodes(String Response)
            => [.. Responses(Response).Select(r => r.Code)];

        public static String FirstLine(String Response)
        {

            var line = Response.Split('\r', '\n').FirstOrDefault(l => l.Length > 0) ?? "";

            return line.Length > 100 ? line[..100] + " …" : line;

        }

        /// <summary>
        /// How many HTTP responses the buffer holds — the pipelining assertion.
        /// Counts status lines rather than parsing, which is the point: a
        /// harness must be able to count responses the framing layer would have
        /// refused to hand over.
        /// </summary>
        public static Int32 ResponseCount(String Response)
            => Responses(Response).Count();

        #endregion

        #region (static) Summarize (Text)

        /// <summary>
        /// Collapse a wire dump to one readable line for failure output.
        /// </summary>
        private static String Summarize(String Text)
        {

            if (Text.Length == 0)
                return "(nothing)";

            var flattened = Text.Replace("\r", "\\r").Replace("\n", "\\n");

            return flattened.Length > 200 ? flattened[..200] + " …" : flattened;

        }

        #endregion

        #region Summary()

        /// <summary>
        /// Print the verdict and return the process exit code.
        /// </summary>
        public Int32 Summary()
        {

            var passed = total - failures.Count;

            Console.WriteLine();
            Console.WriteLine($"  {Harness}: {passed}/{total} checks passed");

            if (failures.Count > 0)
            {
                Console.WriteLine("  failed:");
                foreach (var failure in failures)
                    Console.WriteLine($"    - {failure}");
            }

            return failures.Count == 0 ? 0 : 1;

        }

        #endregion

    }

}
