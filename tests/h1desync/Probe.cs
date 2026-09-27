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
    /// One ambiguous message, and what — if anything — the RFC requires of a
    /// server that receives it.
    ///
    /// The split between <see cref="Requirement"/> being present and being
    /// null is the whole point of this harness, and it is not a detail of
    /// presentation. RFC 9112 makes some of these framings illegal outright:
    /// a Transfer-Encoding whose final coding is not chunked gets a MUST-400,
    /// and there the harness can assert. Others it explicitly leaves open —
    /// Section 6.1 says a server MAY reject a request carrying both
    /// Content-Length and Transfer-Encoding "or process such a request in
    /// accordance with the Transfer-Encoding alone" — and there an assertion
    /// would be this repository's taste dressed up as conformance.
    ///
    /// Those open cases are exactly the ones a smuggling chain is built from,
    /// which is why they are not simply dropped: they are observed, and the
    /// differential reports who chose what.
    /// </summary>
    /// <param name="Id">Stable identifier — the join key across implementations.</param>
    /// <param name="Summary">What is ambiguous about the payload.</param>
    /// <param name="Citation">Where the rule lives, for the ones that have a rule.</param>
    /// <param name="Requirement">The normative sentence, quoted, or null if the RFC leaves it open.</param>
    /// <param name="Verdict">Given the observation, did the server obey? Null for observe-only probes.</param>
    /// <param name="Wire">The bytes, verbatim.</param>
    public sealed record Probe(String                       Id,
                               String                       Summary,
                               String?                      Citation,
                               String?                      Requirement,
                               Func<Observation, Boolean>?  Verdict,
                               String                       Wire)
    {

        /// <summary>
        /// Whether this probe carries something to assert, as opposed to
        /// something to record.
        /// </summary>
        public Boolean IsNormative
            => Verdict is not null;

    }

}
