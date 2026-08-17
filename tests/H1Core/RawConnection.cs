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

#region Usings

using System.Net.Security;
using System.Net.Sockets;
using System.Text;

#endregion

namespace org.GraphDefined.Vanaheimr.Hermod.HTTP1.Tests
{

    /// <summary>
    /// A raw TCP/TLS connection for wire-level HTTP/1.x conformance testing.
    ///
    /// It deliberately does *not* normalize anything. Whatever string you hand
    /// to SendAsync goes on the wire byte for byte, including the malformed
    /// framing, the obsolete line folding and the duplicate Content-Length
    /// fields that no HTTP client would ever produce — which is the entire
    /// reason this class exists rather than reusing Hermod's HTTPClient.
    ///
    /// Reads are text-based and timeout-bounded rather than framing-aware, for
    /// the same reason: a conformance harness must be able to assert on bytes
    /// that a framing-aware reader would reject before you ever saw them.
    /// </summary>
    public sealed class RawConnection : IAsyncDisposable
    {

        #region Data

        private readonly TcpClient  tcpClient;
        private readonly Stream     stream;

        /// <summary>
        /// The default window for "read whatever the server sends". Long enough
        /// that a loaded CI box does not produce phantom failures, short enough
        /// that a suite of ~100 checks still finishes in reasonable time.
        /// </summary>
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(3);

        #endregion

        #region Properties

        /// <summary>
        /// Whether the underlying socket still reports a connection.
        /// </summary>
        public Boolean IsConnected
            => tcpClient.Connected;

        #endregion

        #region Constructor(s)

        private RawConnection(TcpClient TCPClient, Stream Stream)
        {
            tcpClient  = TCPClient;
            stream     = Stream;
        }

        #endregion


        #region (static) ConnectAsync (Host, Port, TLS = false)

        public static async Task<RawConnection> ConnectAsync(String   Host,
                                                             UInt16   Port,
                                                             Boolean  TLS   = false)
        {

            var tcpClient = new TcpClient();

            await tcpClient.ConnectAsync(Host, Port);

            Stream stream = tcpClient.GetStream();

            if (TLS)
            {

                var sslStream = new SslStream(
                                    stream,
                                    leaveInnerStreamOpen:  false,
                                    // The demo host uses a self-signed certificate generated
                                    // at startup; certificate validation is not what these
                                    // harnesses are testing.
                                    userCertificateValidationCallback: (a, b, c, d) => true
                                );

                await sslStream.AuthenticateAsClientAsync(Host);

                stream = sslStream;

            }

            return new RawConnection(tcpClient, stream);

        }

        #endregion

        #region SendAsync (Text | Bytes)

        /// <summary>
        /// Write the given text as ASCII, byte for byte, unmodified.
        /// </summary>
        public Task SendAsync(String Text)
            => SendAsync(Encoding.ASCII.GetBytes(Text));

        /// <summary>
        /// Write the bytes, bounded by a timeout, tolerating a peer that stops
        /// reading.
        ///
        /// Both of those matter here. These harnesses deliberately send payloads
        /// the server is supposed to reject, and a server that rejects a large
        /// body mid-flight stops reading it — at which point an unbounded write
        /// blocks once the send buffer fills. Over cleartext the socket errors
        /// out quickly and hides the problem; over TLS the extra buffering makes
        /// it hang instead. A conformance harness that can hang is worse than one
        /// that fails, so the write is bounded and a refused write is treated as
        /// data, not as an error.
        /// </summary>
        public async Task SendAsync(Byte[] Data, TimeSpan? Timeout = null)
        {

            using var cts = new CancellationTokenSource(Timeout ?? DefaultWindow);

            try
            {
                await stream.WriteAsync(Data, cts.Token);
                await stream.FlushAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // The peer stopped reading — expected for a rejected payload.
                WriteWasRefused = true;
            }
            catch (IOException)
            {
                WriteWasRefused = true;
            }

        }

        /// <summary>
        /// Whether a write could not be completed because the peer stopped
        /// reading or closed. For most checks this is a *pass* condition.
        /// </summary>
        public Boolean WriteWasRefused { get; private set; }

        #endregion

        #region SendSegmentedAsync (Text, ChunkSize, Delay)

        /// <summary>
        /// Write the text in pieces with a pause between them, so the request
        /// arrives across several TCP reads. Used both for the legitimate case
        /// (a fragmented request must still parse) and the hostile one (a
        /// slowloris that never completes its header section).
        /// </summary>
        public async Task SendSegmentedAsync(String    Text,
                                             Int32     ChunkSize,
                                             TimeSpan  Delay)
        {

            var bytes = Encoding.ASCII.GetBytes(Text);

            for (var offset = 0; offset < bytes.Length; offset += ChunkSize)
            {

                var length = Math.Min(ChunkSize, bytes.Length - offset);

                await SendAsync(bytes[offset..(offset + length)]);

                if (WriteWasRefused)
                    return;

                if (offset + length < bytes.Length)
                    await Task.Delay(Delay);

            }

        }

        #endregion

        #region ShutdownSend()

        /// <summary>
        /// Half-close: signal EOF to the peer while still reading.
        /// </summary>
        public void ShutdownSend()
        {
            try
            {
                tcpClient.Client.Shutdown(SocketShutdown.Send);
            }
            catch (SocketException)
            { }
        }

        #endregion

        #region ReadAsync (Window = null)

        /// <summary>
        /// Read whatever arrives until the peer closes or the window expires.
        ///
        /// Returning on timeout rather than throwing is deliberate: "the server
        /// sent nothing and kept the connection open" is a legitimate — and
        /// frequently the *expected* — outcome in these harnesses, not an error.
        /// </summary>
        public async Task<String> ReadAsync(TimeSpan? Window = null)
        {

            var deadline  = DateTimeOffset.UtcNow + (Window ?? DefaultWindow);
            var received  = new MemoryStream();
            var buffer    = new Byte[8192];

            while (DateTimeOffset.UtcNow < deadline)
            {

                var remaining = deadline - DateTimeOffset.UtcNow;

                if (remaining <= TimeSpan.Zero)
                    break;

                using var cts = new CancellationTokenSource(remaining);

                try
                {

                    var read = await stream.ReadAsync(buffer, cts.Token);

                    if (read == 0)      // peer closed
                        break;

                    received.Write(buffer, 0, read);

                }
                catch (OperationCanceledException)
                {
                    break;              // window expired — return what we have
                }
                catch (IOException)
                {
                    break;              // connection reset — likewise
                }

            }

            return Encoding.ASCII.GetString(received.ToArray());

        }

        #endregion

        #region ReadHeadersAsync (Window = null)

        /// <summary>
        /// Read until the first response's header section is complete, then
        /// return immediately.
        ///
        /// ReadAsync waits out its whole window whenever the server keeps the
        /// connection open, which makes it useless for measuring *how fast* the
        /// server answered — the elapsed time is then the window, not the
        /// latency. Any check whose claim is "the server rejected this without
        /// reading the body" needs this one instead.
        /// </summary>
        public async Task<String> ReadHeadersAsync(TimeSpan? Window = null)
        {

            var deadline  = DateTimeOffset.UtcNow + (Window ?? DefaultWindow);
            var received  = new MemoryStream();
            var buffer    = new Byte[8192];

            while (DateTimeOffset.UtcNow < deadline)
            {

                var remaining = deadline - DateTimeOffset.UtcNow;

                if (remaining <= TimeSpan.Zero)
                    break;

                using var cts = new CancellationTokenSource(remaining);

                try
                {

                    var read = await stream.ReadAsync(buffer, cts.Token);

                    if (read == 0)
                        break;

                    received.Write(buffer, 0, read);

                    var text = Encoding.ASCII.GetString(received.ToArray());

                    if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                        return text;

                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

            }

            return Encoding.ASCII.GetString(received.ToArray());

        }

        #endregion

        #region ReadResponseAsync (Window = null, Bodyless = false)

        /// <summary>
        /// Read exactly one complete response and return as soon as it is
        /// complete, using the response's own framing to decide where it ends.
        ///
        /// Why this exists: HTTP/1.1 connections are persistent, so the server
        /// does not close after answering — which means a plain "read until the
        /// peer closes" waits out its entire window on *every* check. Across
        /// roughly 200 checks that turns a suite that should take seconds into
        /// one that takes minutes, and the delay is entirely the harness's own.
        ///
        /// A response is complete when:
        ///   - it cannot carry content (1xx, 204, 304, or a HEAD reply), or
        ///   - its chunked body reached the terminal chunk, or
        ///   - its Content-Length bytes have arrived,
        /// and otherwise when the connection closes or the window expires.
        /// </summary>
        public async Task<String> ReadResponseAsync(TimeSpan?  Window     = null,
                                                    Boolean    Bodyless   = false)
        {

            var deadline  = DateTimeOffset.UtcNow + (Window ?? DefaultWindow);
            var received  = new MemoryStream();
            var buffer    = new Byte[8192];

            while (DateTimeOffset.UtcNow < deadline)
            {

                var text = Encoding.ASCII.GetString(received.ToArray());

                if (IsComplete(text, Bodyless))
                    return text;

                var remaining = deadline - DateTimeOffset.UtcNow;

                if (remaining <= TimeSpan.Zero)
                    break;

                using var cts = new CancellationTokenSource(remaining);

                try
                {

                    var read = await stream.ReadAsync(buffer, cts.Token);

                    if (read == 0)      // peer closed — close-delimited, done
                        break;

                    received.Write(buffer, 0, read);

                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

            }

            return Encoding.ASCII.GetString(received.ToArray());

        }

        /// <summary>
        /// Whether the buffer already holds one whole response.
        /// </summary>
        private static Boolean IsComplete(String Text, Boolean Bodyless)
        {

            var headerEnd = Text.IndexOf("\r\n\r\n", StringComparison.Ordinal);

            if (headerEnd < 0)
                return false;

            var headers  = Text[..headerEnd];
            var bodyFrom = headerEnd + 4;

            // RFC 9110 §15.2 / §15.3.5 / §15.4.5 — these never carry content,
            // whatever their fields claim. A HEAD reply is bodyless for the same
            // reason and the caller has to say so, since the response alone does
            // not reveal which method produced it.
            var status = Checks.StatusOf(Text);

            if (Bodyless || status is >= 100 and < 200 or 204 or 304)
                return true;

            if (headers.Contains("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
                // The terminal chunk, plus whatever trailers follow, ends at the
                // blank line after "0".
                return Text.IndexOf("\r\n0\r\n", bodyFrom - 2, StringComparison.Ordinal) >= 0 &&
                       Text.EndsWith("\r\n\r\n", StringComparison.Ordinal);

            var marker = headers.IndexOf("Content-Length:", StringComparison.OrdinalIgnoreCase);

            if (marker >= 0)
            {

                var lineEnd = headers.IndexOf("\r\n", marker, StringComparison.Ordinal);
                var value   = (lineEnd < 0 ? headers[(marker + 15)..] : headers[(marker + 15)..lineEnd]).Trim();

                if (Int32.TryParse(value, out var length))
                    return Text.Length - bodyFrom >= length;

            }

            // No framing information: the body ends when the connection does, so
            // only a close or the window can end it.
            return false;

        }

        #endregion

        #region (static) RoundTripAsync (Host, Port, Request, TLS = false, Window = null, Bodyless = false)

        /// <summary>
        /// Connect, send one raw request, read one complete reply, close. The
        /// shape most checks need.
        /// </summary>
        public static async Task<String> RoundTripAsync(String     Host,
                                                        UInt16     Port,
                                                        String     Request,
                                                        Boolean    TLS        = false,
                                                        TimeSpan?  Window     = null,
                                                        Boolean    Bodyless   = false)
        {

            await using var connection = await ConnectAsync(Host, Port, TLS);

            // The caller's window bounds the send as well as the read: a check
            // that hands over a multi-megabyte payload needs both to be generous,
            // and one that expects a fast rejection wants both to be short.
            await connection.SendAsync(Encoding.ASCII.GetBytes(Request), Window);

            return await connection.ReadResponseAsync(Window, Bodyless);

        }

        #endregion

        #region IAsyncDisposable

        public async ValueTask DisposeAsync()
        {

            try
            {
                await stream.DisposeAsync();
            }
            catch
            { }

            tcpClient.Dispose();

        }

        #endregion

    }

}
