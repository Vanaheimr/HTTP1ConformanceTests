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
        /// The status code of the first response in the buffer, if any.
        /// </summary>
        public static UInt16? StatusOf(String Response)
        {

            var index = Response.IndexOf("HTTP/1.", StringComparison.Ordinal);

            if (index < 0 || Response.Length < index + 13)
                return null;

            return UInt16.TryParse(Response.Substring(index + 9, 3), out var code)
                       ? code
                       : null;

        }

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
        {

            var count = 0;
            var index = 0;

            while ((index = Response.IndexOf("HTTP/1.", index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += 7;
            }

            return count;

        }

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
