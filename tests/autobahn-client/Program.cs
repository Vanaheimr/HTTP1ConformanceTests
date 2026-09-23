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

using System.Threading.Channels;

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

        /// <summary>
        /// How long one case may take before we give up on it and hang up ourselves.
        /// <para>
        /// 120 s, and the number is bounded from below by the suite rather than chosen. Every case
        /// in sections 12 and 13 says "Timeout case after 60 secs" in its own description, so a
        /// driver deadline under 60 s is tighter than the thing it is driving and turns a slow case
        /// into a reported stall.
        /// </para>
        /// <para>
        /// It was 30 s for exactly one CI run, which is how this got measured. Locally all 517 cases
        /// finished with zero stalls; on the hosted runner case 500 — that is 13.7.1, a thousand
        /// compressed messages with permessage-deflate negotiated — did not, and the forced
        /// disconnect left the fuzzingserver unable to serve the remaining seventeen cases, so
        /// /updateReports produced nothing and the whole run reported no result at all. One case
        /// over its deadline cost 517 cases their report.
        /// </para>
        /// </summary>
        private static readonly TimeSpan  caseTimeout      = TimeSpan.FromSeconds(120);

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

                        // A case we hang up on mid-flight is the one thing that can take the WHOLE
                        // run down with it: the 2026-09-23 nightly lost all 517 results because a
                        // forced disconnect left the fuzzingserver unable to serve the seventeen
                        // cases after it, so /updateReports wrote nothing. Cheap insurance, and it
                        // costs nothing on a run with no stalls.
                        await Task.Delay(TimeSpan.FromSeconds(2));

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

            // The echo goes through an UNBOUNDED queue, drained by a task of its own, and that is
            // the whole design rather than a detail.
            //
            // The obvious version — await the send from inside the receive handler — deadlocks, and
            // an earlier revision of this file did exactly that while asserting in a comment that it
            // was the right way to keep ordering. The read loop awaits the handler, so a send that
            // blocks parks the reader. With a thousand messages of up to 128 KiB in flight the
            // peer's receive buffer fills while it is still sending; our write blocks on a socket
            // nobody is draining, and we cannot drain theirs because we are parked inside the
            // handler. Neither side moves again.
            //
            // It only bit under CPU contention. Locally the whole of section 12.4 ran in 37 s with
            // the slowest case at 3.8 s; on a two-core hosted runner, 12.4.12 — 1.1 s here — sat
            // past a 120 s deadline, along with 12.4.14 and 12.4.17. A hundredfold gap is not a
            // slow machine, it is a hang that a fast machine happens to race past.
            //
            // A Channel keeps the order the handler saw (FIFO), and unbounded is deliberate:
            // bounding it would push the blocking back into the handler and rebuild the deadlock
            // one layer up. The cost is memory — a case can queue about 128 MB before the pump
            // catches up, which this driver can afford and a library could not.
            var received = 0;
            var echoed   = 0;

            var echoQueue = Channel.CreateUnbounded<(Boolean IsText, String? Text, Byte[]? Bytes)>(
                                new UnboundedChannelOptions { SingleReader = true }
                            );

            client.OnTextMessageReceived += (timestamp, cl, connection, frame, eventTrackingId, text, ct) => {
                Interlocked.Increment(ref received);
                echoQueue.Writer.TryWrite((true, text, null));
                return Task.CompletedTask;
            };

            client.OnBinaryMessageReceived += (timestamp, cl, connection, frame, eventTrackingId, bytes, ct) => {
                Interlocked.Increment(ref received);
                echoQueue.Writer.TryWrite((false, null, bytes));
                return Task.CompletedTask;
            };

            await client.Connect();

            // Sends are swallowed, because a case ending mid-echo is normal here rather than
            // exceptional: several cases close the moment they have seen enough, and a write into
            // that close is not a finding.
            var pump = Task.Run(async () => {
                await foreach (var item in echoQueue.Reader.ReadAllAsync())
                {
                    try
                    {
                        if (item.IsText)
                            await client.SendTextMessage(item.Text!);
                        else
                            await client.SendBinaryMessage(item.Bytes!);
                        Interlocked.Increment(ref echoed);
                    }
                    catch { }
                }
            });

            var deadline = DateTime.UtcNow + caseTimeout;

            while (client.Connected && DateTime.UtcNow < deadline)
                await Task.Delay(pollInterval);

            var finished = !client.Connected;

            if (!finished)
            {
                // The two counters are the point of this branch. The hangs seen on CI (12.4.12,
                // 12.4.14, 12.4.17 in one run, 13.7.1 in another) have never reproduced locally,
                // not even with the driver pinned to a single core, so the mechanism is a
                // hypothesis rather than a finding. These numbers settle it on the next occurrence
                // instead of leaving it open: received far ahead of echoed means the send side is
                // wedged, the two close together means we are simply waiting on a peer that has
                // stopped talking. Guessing between those two from a timestamp is what this is
                // meant to replace.
                Console.WriteLine($"  case {Number}: received {Volatile.Read(ref received)}, echoed {Volatile.Read(ref echoed)}, queued {Volatile.Read(ref received) - Volatile.Read(ref echoed)}");
                await CloseQuietly(client);
            }

            echoQueue.Writer.TryComplete();
            await pump.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });

            return finished;

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
