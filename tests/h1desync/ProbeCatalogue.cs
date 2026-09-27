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
    /// The ambiguous messages, grouped by what makes them ambiguous.
    ///
    /// Every payload ends with the same trailing request — a GET for a path
    /// that answers 418 on our demo and 404 everywhere else. Its purpose is
    /// not its status code but its existence: if a second response comes
    /// back, the server placed the end of the first message somewhere before
    /// those bytes, and that placement is the thing a smuggling chain
    /// exploits. One response means it read the whole payload as one message;
    /// two mean it found a request hidden inside the body.
    /// </summary>
    public static class ProbeCatalogue
    {

        public static IEnumerable<Probe> All(String Host)
        {

            // The hidden request. 418 is distinctive enough to grep for and
            // says nothing about whether the payload carrying it was legal.
            var smuggled = $"GET /status/418 HTTP/1.1\r\nHost: {Host}\r\n\r\n";

            // The assertions, named, so that the catalogue below reads as a
            // list of claims rather than as lambda soup.
            Func<Observation, Boolean> MustRejectAndClose = o => o.Rejected && o.Count == 1 && o.Closed;
            Func<Observation, Boolean> MustReject         = o => o.Rejected && o.Count == 1;
            Func<Observation, Boolean> MustClose          = o => o.Closed;
            Func<Observation, Boolean> MustReadAsOne      = o => o.Count == 1 && o.First == 200;


            #region Both Content-Length and Transfer-Encoding (RFC 9112 Section 6.1)

            // The classic pair, and the one place where this harness must not
            // assert what it would prefer. Section 6.1 is explicit that either
            // answer is allowed. What it does require is the close — an open
            // connection after an ambiguous message is what lets the *next*
            // request on it be framed by the attacker.

            const String mayReject =
                "A server MAY reject a request that contains both Content-Length and " +
                "Transfer-Encoding or process such a request in accordance with the " +
                "Transfer-Encoding alone. Regardless, the server MUST close the " +
                "connection after responding to such a request.";

            yield return new Probe(
                "cl-te",
                "Content-Length: 6 before Transfer-Encoding: chunked, terminal chunk, then a request",
                "RFC 9112 Section 6.1",
                mayReject,
                MustClose,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nContent-Length: 6\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n{smuggled}"
            );

            yield return new Probe(
                "te-cl",
                "Transfer-Encoding: chunked before Content-Length: 4 — the mirror image",
                "RFC 9112 Section 6.1",
                mayReject,
                MustClose,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: chunked\r\nContent-Length: 4\r\n\r\n0\r\n\r\n{smuggled}"
            );

            #endregion

            #region Transfer-Encoding whose final coding is not chunked (Section 6.3 item 4)

            // Here the RFC does state a MUST, and it states the status code
            // with it, which makes these the strictest checks in the file.

            const String mustFourHundred =
                "If a Transfer-Encoding header field is present in a request and the " +
                "chunked transfer coding is not the final encoding, the message body " +
                "length cannot be determined reliably; the server MUST respond with " +
                "the 400 (Bad Request) status code and then close the connection.";

            yield return new Probe(
                "te-identity",
                "Transfer-Encoding: identity — a coding, and not chunked",
                "RFC 9112 Section 6.3 item 4",
                mustFourHundred,
                o => o.First == 400 && o.Closed,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: identity\r\n\r\nhello{smuggled}"
            );

            yield return new Probe(
                "te-chunked-gzip",
                "Transfer-Encoding: chunked, gzip — chunked is present, but not last",
                "RFC 9112 Section 6.3 item 4",
                mustFourHundred,
                o => o.First == 400 && o.Closed,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: chunked, gzip\r\n\r\n0\r\n\r\n{smuggled}"
            );

            yield return new Probe(
                "te-gzip",
                "Transfer-Encoding: gzip — a coding the server knows, used alone",
                "RFC 9112 Section 6.3 item 4",
                mustFourHundred,
                o => o.First == 400 && o.Closed,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: gzip\r\n\r\n0\r\n\r\n{smuggled}"
            );

            // Two rules meet on this one: Section 6.3 item 4 says 400 because
            // chunked is not the final coding, Section 6.1 says a coding the
            // server does not understand SHOULD get a 501. Either is a
            // refusal, and the harness accepts both rather than pretending
            // the RFC picks one.
            yield return new Probe(
                "te-unknown",
                "Transfer-Encoding: rot13 — a coding nobody implements",
                "RFC 9112 Section 6.3 item 4 / Section 6.1",
                mustFourHundred + " Section 6.1 additionally: a server that receives a request " +
                "message with a transfer coding it does not understand SHOULD respond with 501.",
                o => (o.First == 400 || o.First == 501) && o.Closed,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: rot13\r\n\r\n0\r\n\r\n{smuggled}"
            );

            #endregion

            #region Transfer-Encoding that must be honoured — legal OWS and case

            // The counterweight to the section above, and it is not decorative.
            // A harness that only ever demands rejection gives its best score
            // to a server that rejects everything, which is not conformance
            // either. These three are well-formed chunked requests wearing
            // unusual but legal clothing, and the body has to come back out
            // of /echo.

            const String isChunked =
                "field-line = field-name \":\" OWS field-value OWS, and transfer codings are " +
                "case-insensitive — so each of these IS chunked, and the body has to be " +
                "decoded as such.";

            yield return new Probe(
                "te-ows-spaces",
                "Transfer-Encoding:   chunked — three spaces of perfectly legal OWS",
                "RFC 9112 Section 5.1",
                isChunked,
                MustReadAsOne,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding:   chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n"
            );

            yield return new Probe(
                "te-ows-htab",
                "Transfer-Encoding:<HTAB>chunked — a horizontal tab, which is also OWS",
                "RFC 9112 Section 5.1",
                isChunked,
                MustReadAsOne,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding:\tchunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n"
            );

            yield return new Probe(
                "te-mixed-case",
                "Transfer-Encoding: ChUnKeD",
                "RFC 9110 Section 16.10",
                isChunked,
                MustReadAsOne,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: ChUnKeD\r\n\r\n5\r\nhello\r\n0\r\n\r\n"
            );

            #endregion

            #region Transfer-Encoding obfuscation — no rule, only a choice

            // Nothing in RFC 9112 says what to do with a second, deliberately
            // malformed Transfer-Encoding line. That silence is the gadget:
            // one implementation reads the pair as chunked, another decides
            // the header is unusable and falls back to Content-Length or to
            // no body at all, and a chain built from the two has a desync.
            // Observed, never asserted.

            foreach (var (id, second) in new (String, String)[] {
                         ("te-dup",       "Transfer-Encoding: chunked"),
                         ("te-obf-x",     "Transfer-Encoding: xchunked"),
                         ("te-obf-sp",    "Transfer-Encoding:  chunked"),
                         ("te-obf-comma", "Transfer-Encoding: chunked, "),
                         ("te-obf-quote", "Transfer-Encoding: \"chunked\"")
                     })
            {
                yield return new Probe(
                    id,
                    $"Transfer-Encoding: chunked, then [{second}]",
                    null,
                    null,
                    null,
                    $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: chunked\r\n{second}\r\n\r\n0\r\n\r\n{smuggled}"
                );
            }

            yield return new Probe(
                "te-chunked-chunked",
                "Transfer-Encoding: chunked, chunked — the coding applied twice",
                null,
                null,
                null,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: chunked, chunked\r\n\r\n0\r\n\r\n{smuggled}"
            );

            #endregion

            #region Content-Length that cannot be trusted (Section 6.3 item 5)

            const String mustBeUnrecoverable =
                "If a message is received without Transfer-Encoding and with an invalid " +
                "Content-Length header field, then the message framing is invalid and the " +
                "recipient MUST treat it as an unrecoverable error, unless the field value " +
                "can be successfully parsed as a comma-separated list, all values in the " +
                "list are valid, and all values in the list are the same.";

            foreach (var (id, value, summary) in new (String, String, String)[] {
                         ("cl-dup-diff",  "Content-Length: 6\r\nContent-Length: 5",  "two Content-Length fields that disagree"),
                         ("cl-list-diff", "Content-Length: 6, 5",                    "a Content-Length list whose values differ"),
                         ("cl-plus",      "Content-Length: +6",                      "Content-Length: +6 — a sign is not a DIGIT"),
                         ("cl-hex",       "Content-Length: 0x6",                     "Content-Length: 0x6 — hexadecimal is not a DIGIT string"),
                         ("cl-negative",  "Content-Length: -1",                      "a negative Content-Length"),
                         ("cl-overflow",  "Content-Length: 99999999999999999999999", "a Content-Length past every integer width")
                     })
            {
                yield return new Probe(
                    id,
                    summary,
                    "RFC 9112 Section 6.3 item 5",
                    mustBeUnrecoverable,
                    MustReject,
                    $"POST /echo HTTP/1.1\r\nHost: {Host}\r\n{value}\r\n\r\nhello!{smuggled}"
                );
            }

            // The same section's *exception*: a list that parses, is valid and
            // is all one value is explicitly not an error. It does not follow
            // that a server has to accept it — nothing says it must — so these
            // are observed rather than asserted, and the differential is where
            // they earn their place.
            foreach (var (id, value, summary) in new (String, String, String)[] {
                         ("cl-dup-same",     "Content-Length: 6\r\nContent-Length: 6", "two Content-Length fields that agree"),
                         ("cl-list-same",    "Content-Length: 6, 6",                   "a Content-Length list whose values agree"),
                         ("cl-leading-zero", "Content-Length: 006",                    "Content-Length: 006 — leading zeros are DIGITs"),
                         ("cl-trailing-ows", "Content-Length: 6 ",                     "Content-Length with trailing OWS")
                     })
            {
                yield return new Probe(
                    id,
                    summary,
                    null,
                    null,
                    null,
                    $"POST /echo HTTP/1.1\r\nHost: {Host}\r\n{value}\r\n\r\nhello!{smuggled}"
                );
            }

            #endregion

            #region Whitespace between field name and colon (Section 5.1)

            const String mustRejectWhitespace =
                "A server MUST reject, with a response status code of 400 (Bad Request), " +
                "any received request message that contains whitespace between a header " +
                "field name and colon.";

            foreach (var (id, line) in new (String, String)[] {
                         ("ws-colon-cl",   "Content-Length : 6"),
                         ("ws-colon-te",   "Transfer-Encoding : chunked"),
                         ("ws-colon-htab", "X-Length\t: 6")
                     })
            {
                yield return new Probe(
                    id,
                    $"[{line.Replace("\t", "<HTAB>")}] — whitespace before the colon",
                    "RFC 9112 Section 5.1",
                    mustRejectWhitespace,
                    o => o.First == 400,
                    $"POST /echo HTTP/1.1\r\nHost: {Host}\r\n{line}\r\n\r\nhello!{smuggled}"
                );
            }

            #endregion

            #region An HTTP/1.0 message carrying Transfer-Encoding (Section 6.1)

            yield return new Probe(
                "http10-te",
                "an HTTP/1.0 request with Transfer-Encoding: chunked",
                "RFC 9112 Section 6.1",
                "A server or client that receives an HTTP/1.0 message containing a " +
                "Transfer-Encoding header field MUST treat the message as if the framing " +
                "is faulty, even if a Content-Length is present, and close the connection " +
                "after processing the message.",
                MustClose,
                $"POST /echo HTTP/1.0\r\nHost: {Host}\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n{smuggled}"
            );

            #endregion

            #region A chunk-size that is not 1*HEXDIG (Section 7.1)

            const String chunkGrammar =
                "chunk-size = 1*HEXDIG. Recipients MUST anticipate potentially large " +
                "hexadecimal numerals and prevent parsing errors due to integer conversion " +
                "overflows or precision loss due to integer representation.";

            foreach (var (id, size, summary) in new (String, String, String)[] {
                         ("chunk-plus",     "+5",                   "a chunk-size carrying a sign"),
                         ("chunk-hex-0x",   "0x5",                  "a chunk-size with an 0x prefix"),
                         ("chunk-bws",      "5 ",                   "a chunk-size followed by bare whitespace"),
                         ("chunk-overflow", "FFFFFFFFFFFFFFFFFFFF", "a chunk-size of twenty hex digits")
                     })
            {
                yield return new Probe(
                    id,
                    summary,
                    "RFC 9112 Section 7.1",
                    chunkGrammar,
                    MustReject,
                    $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: chunked\r\n\r\n{size}\r\nhello\r\n0\r\n\r\n{smuggled}"
                );
            }

            // Chunk-line shapes with no rule attached — where parsers are free
            // to differ, and therefore where a chain can desync.
            yield return new Probe(
                "chunk-ext-quoted-crlf",
                "a CRLF inside a quoted chunk-extension value",
                null,
                null,
                null,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: chunked\r\n\r\n5;x=\"a\r\nb\"\r\nhello\r\n0\r\n\r\n{smuggled}"
            );

            yield return new Probe(
                "chunk-lf-only",
                "chunk lines terminated by bare LF instead of CRLF",
                null,
                null,
                null,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: chunked\r\n\r\n5\nhello\n0\n\n{smuggled}"
            );

            yield return new Probe(
                "chunk-trailer-cl",
                "a Content-Length in the trailer section",
                null,
                null,
                null,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\nContent-Length: 10\r\n\r\n{smuggled}"
            );

            #endregion

            #region Obsolete line folding (Section 5.2)

            // The fold is placed inside the Transfer-Encoding value on purpose,
            // because both permitted answers then converge on a refusal:
            // reject the fold outright, or replace it with SP — which yields
            // "chun ked", a final coding that is not chunked, at which point
            // Section 6.3 item 4 takes over and requires the 400 anyway.
            yield return new Probe(
                "obs-fold-te",
                "Transfer-Encoding split across an obs-fold continuation line",
                "RFC 9112 Section 5.2, with Section 6.3 item 4",
                "A server that receives an obs-fold in a request message MUST either " +
                "reject the message by sending a 400 (Bad Request) or replace each " +
                "received obs-fold with one or more SP octets prior to interpreting the " +
                "field value.",
                MustRejectAndClose,
                $"POST /echo HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: chun\r\n ked\r\n\r\n0\r\n\r\n{smuggled}"
            );

            yield return new Probe(
                "lf-only-headers",
                "the whole header section terminated by bare LF",
                null,
                null,
                null,
                $"POST /echo HTTP/1.1\nHost: {Host}\nContent-Length: 6\n\nhello!{smuggled}"
            );

            #endregion

        }

    }

}
