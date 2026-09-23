/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of HTTP1ConformanceTests <https://www.github.com/Vanaheimr/HTTP1ConformanceTests>
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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

#endregion

namespace org.GraphDefined.Vanaheimr.Hermod.HTTP1.Tests
{

    /// <summary>
    /// Drives Hermod's <see cref="WebSocketClient"/> through the Autobahn TestSuite's
    /// <c>fuzzingserver</c>, which is the mirror of what <c>tests/autobahn.sh</c> does to the
    /// server: there the suite is the client and our echo server is under test; here the suite
    /// is the server and our client is under test.
    ///
    /// <para>
    /// This half existed only as a claim until 2026-09-23. Hermod's WebSocket README reports
    /// "Client, sections 1–7, 10 — 242 OK / 0 FAILED" and "Client, compression 12/13 — 126 OK /
    /// 0 FAILED", and PLAN.md recorded those numbers as unreproducible from a clean checkout.
    /// This is where they become a command.
    /// </para>
    ///
    /// <para>
    /// The fuzzingserver protocol is three kinds of connection, all to the same host:
    /// <list type="bullet">
    ///   <item><c>/getCaseCount</c> — sends one text message with the number of cases, then closes.</item>
    ///   <item><c>/runCase?case=N&amp;agent=A</c> — runs case N against whatever connects; the client
    ///         must echo every message back with its own type preserved, and the server closes when
    ///         the case is done.</item>
    ///   <item><c>/updateReports?agent=A</c> — makes the server write index.json and the HTML report.</item>
    /// </list>
    /// A case is therefore one connection, and "the case ended" is "the server hung up".
    /// </para>
    /// </summary>
    public static class Program
    {

        #region Data

        private static readonly TimeSpan  caseTimeout      = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan  controlTimeout   = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan  pollInterval     = TimeSpan.FromMilliseconds(20);

        #endregion


        public static async Task<Int32> Main(String[] Arguments)
        {

            var host     = "127.0.0.1";
            var port     = 9001;
            var agent    = "Hermod.HTTP1.Client";
            var deflate  = false;
            var first    = 0;
            var last     = 0;

            for (var i = 0; i < Arguments.Length; i++)
            {
                switch (Arguments[i])
                {
                    case "--host":     host    = Arguments[++i];                 break;
                    case "--port":     port    = Int32.Parse(Arguments[++i]);    break;
                    case "--agent":    agent   = Arguments[++i];                 break;
                    case "--deflate":  deflate = true;                           break;
                    case "--first":    first   = Int32.Parse(Arguments[++i]);    break;
                    case "--last":     last    = Int32.Parse(Arguments[++i]);    break;
                    case "-h":
                    case "--help":
                        Console.WriteLine("autobahn-client [--host H] [--port N] [--agent NAME] [--deflate] [--first N] [--last N]");
                        return 0;
                    default:
                        Console.Error.WriteLine($"Unknown argument: {Arguments[i]}");
                        return 2;
                }
            }

            var baseURL = $"ws://{host}:{port}";

            Console.WriteLine($"Autobahn fuzzingserver at {baseURL}, agent '{agent}', permessage-deflate {(deflate ? "on" : "off")}");

            Int32 caseCount;
            try
            {
                caseCount = await GetCaseCount(baseURL);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Could not reach the fuzzingserver at {baseURL}: {e.Message}");
                return 2;
            }

            if (first <= 0)            first = 1;
            if (last  <= 0 || last > caseCount) last = caseCount;

            Console.WriteLine($"{caseCount} cases reported, running {first}..{last}");
            Console.WriteLine();

            var stalled = new List<Int32>();
            var threw   = new List<Int32>();

            for (var n = first; n <= last; n++)
            {

                // Progress on one line per ten cases: the suite's own report is the verdict, and a
                // line per case buries it. A stalled or throwing case is always named, though —
                // those are the ones somebody has to look at.
                if (n % 10 == 0 || n == first)
                    Console.WriteLine($"  case {n}/{last}");

                try
                {
                    if (!await RunCase(baseURL, n, agent, deflate))
                    {
                        stalled.Add(n);
                        Console.WriteLine($"  case {n}: still connected after {caseTimeout.TotalSeconds:0}s — disconnected by us");
                    }
                }
                catch (Exception e)
                {
                    threw.Add(n);
                    Console.WriteLine($"  case {n}: {e.GetType().Name}: {e.Message}");
                }

            }

            Console.WriteLine();
            Console.WriteLine("Asking the fuzzingserver to write its reports...");

            try
            {
                await UpdateReports(baseURL, agent);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"updateReports failed: {e.Message}");
                return 2;
            }

            Console.WriteLine();
            Console.WriteLine($"Ran {last - first + 1} cases. Stalled: {stalled.Count}, threw: {threw.Count}.");

            // Deliberately NOT a verdict. Neither a stall nor an exception here is by itself a
            // conformance failure — some cases exist precisely to make a client give up, and the
            // suite decides what that was worth. The verdict lives in index.json and is applied by
            // tests/autobahn-client.sh, exactly as the server-side driver does it.
            return 0;

        }


        #region GetCaseCount(BaseURL)

        /// <summary>
        /// Connects to <c>/getCaseCount</c> and returns the single number the server sends.
        /// </summary>
        private static async Task<Int32> GetCaseCount(String BaseURL)
        {

            var received  = new TaskCompletionSource<String>(TaskCreationOptions.RunContinuationsAsynchronously);
            var client    = new WebSocketClient(URL.Parse($"{BaseURL}/getCaseCount"));

            client.OnTextMessageReceived += (timestamp, cl, connection, frame, eventTrackingId, text, ct) => {
                received.TrySetResult(text);
                return Task.CompletedTask;
            };

            var (connection, httpResponse) = await client.Connect();

            // Printed because it is the one handshake whose failure is ambiguous from the outside:
            // a 408 here means the suite was not listening yet, not that the client is broken. That
            // distinction cost an hour once — docker-proxy accepts a TCP connect the moment the
            // container exists, seconds before wstest is actually up behind it.
            Console.WriteLine($"  getCaseCount handshake: HTTP {httpResponse.HTTPStatusCode}");

            try
            {
                var text = await received.Task.WaitAsync(controlTimeout);
                return Int32.Parse(text.Trim());
            }
            finally
            {
                await CloseQuietly(client);
            }

        }

        #endregion

        #region RunCase(BaseURL, Number, Agent, Deflate)

        /// <summary>
        /// Runs one case: connect, echo everything back with its type preserved, and wait for the
        /// server to hang up. Returns false if it never did within <see cref="caseTimeout"/>.
        /// </summary>
        private static async Task<Boolean> RunCase(String   BaseURL,
                                                   Int32    Number,
                                                   String   Agent,
                                                   Boolean  Deflate)
        {

            var client = new WebSocketClient(URL.Parse($"{BaseURL}/runCase?case={Number}&agent={Uri.EscapeDataString(Agent)}")) {
                             EnablePerMessageDeflate = Deflate
                         };

            // Echoing from inside the receive handler is what keeps the order right: the handler is
            // awaited by the read loop, so message N is back on the wire before N+1 is delivered.
            // A queue plus a pump task would need its own ordering guarantee to say the same thing.
            //
            // Both sends are wrapped, because a case ending mid-echo is normal here rather than
            // exceptional: several cases close the connection the moment they have seen enough, and
            // a write into that close is not a finding.
            client.OnTextMessageReceived += async (timestamp, cl, connection, frame, eventTrackingId, text, ct) => {
                try { await cl.SendTextMessage(text); } catch { }
            };

            client.OnBinaryMessageReceived += async (timestamp, cl, connection, frame, eventTrackingId, bytes, ct) => {
                try { await cl.SendBinaryMessage(bytes); } catch { }
            };

            await client.Connect();

            var deadline = DateTime.UtcNow + caseTimeout;

            while (client.Connected && DateTime.UtcNow < deadline)
                await Task.Delay(pollInterval);

            if (client.Connected)
            {
                await CloseQuietly(client);
                return false;
            }

            return true;

        }

        #endregion

        #region UpdateReports(BaseURL, Agent)

        /// <summary>
        /// Connects to <c>/updateReports</c>, which is what makes the server write index.json.
        /// Without this the whole run leaves nothing behind.
        /// </summary>
        private static async Task UpdateReports(String BaseURL, String Agent)
        {

            var client    = new WebSocketClient(URL.Parse($"{BaseURL}/updateReports?agent={Uri.EscapeDataString(Agent)}"));

            await client.Connect();

            var deadline  = DateTime.UtcNow + controlTimeout;

            while (client.Connected && DateTime.UtcNow < deadline)
                await Task.Delay(pollInterval);

            await CloseQuietly(client);

        }

        #endregion

        #region CloseQuietly(Client)

        /// <summary>
        /// A close that cannot itself fail the run: by the time we get here the server has usually
        /// closed already, and the point is only to free the socket.
        /// </summary>
        private static async Task CloseQuietly(WebSocketClient Client)
        {
            try
            {
                if (Client.Connected)
                    await Client.Close();
            }
            catch { }

            try { Client.Disconnect(); } catch { }
        }

        #endregion

    }

}
