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
    /// What one implementation did with one probe: how many messages it read
    /// out of the bytes, what it answered, and whether it hung up.
    ///
    /// This is deliberately a small, coarse vocabulary rather than a diff of
    /// the response text. Two implementations will never produce identical
    /// bytes — different Date fields, different Server fields, different
    /// error prose — so comparing responses would report a difference for
    /// every probe and mean nothing. What a smuggling gadget actually needs
    /// is a disagreement about *where the message ended*, and that is visible
    /// in the message count alone.
    /// </summary>
    public sealed record Observation(Int32     Count,
                                     UInt16[]  Codes,
                                     Boolean   Closed)
    {

        /// <summary>
        /// The first status code, if the peer answered at all.
        /// </summary>
        public UInt16? First
            => Codes.Length > 0 ? Codes[0] : null;

        /// <summary>
        /// Whether the first response is a refusal to accept the message.
        ///
        /// 404 is deliberately absent: a 404 means the request parsed and the
        /// path was unknown, which is the opposite of a framing refusal. It
        /// matters because the foreign peers answer 404 for routes our demo
        /// serves, and a classifier that read that as "rejected" would invent
        /// a disagreement on every probe.
        /// </summary>
        public Boolean Rejected
            => First is 400 or 413 or 414 or 431 or 501 or 505;

        /// <summary>
        /// The comparable verdict — the only field the differential compares.
        ///
        ///   NONE    nothing came back
        ///   REJECT  one response, and it refuses the message
        ///   ONE     one response: the whole payload was read as one message
        ///   TWO     two responses: the peer found a second request in there
        ///   MANY    three or more
        /// </summary>
        public String Class
            => Count == 0             ? "NONE"
             : Rejected && Count == 1 ? "REJECT"
             : Count == 1             ? "ONE"
             : Count == 2             ? "TWO"
             :                          "MANY";

        public override String ToString()
            => $"{Class}[{(Codes.Length > 0 ? String.Join(",", Codes) : "-")}]{(Closed ? " closed" : " open")}";

    }

}
