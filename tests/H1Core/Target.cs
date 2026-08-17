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
    /// Where a harness points. Defaults to the demo host's cleartext listener;
    /// override with "--host", "--port" and "--tls" so the same harness can be
    /// aimed at the TLS listener or at a third-party server (nginx in front of
    /// the demo, say) without a rebuild.
    /// </summary>
    public sealed record Target(String Host, UInt16 Port, Boolean TLS)
    {

        /// <summary>
        /// The Host field value to put in requests.
        /// </summary>
        public String Authority
            => $"{Host}:{Port}";

        #region (static) FromArguments(Arguments)

        public static Target FromArguments(String[] Arguments)
        {

            // 127.0.0.1 rather than "localhost": on Windows the latter usually
            // resolves to ::1 first, and a harness that silently tests a
            // different address family than it reports is worse than one that
            // fails to connect.
            var host  = "127.0.0.1";
            var port  = (UInt16) 8080;
            var tls   = false;

            for (var i = 0; i < Arguments.Length; i++)
            {
                switch (Arguments[i])
                {

                    case "--host" when i + 1 < Arguments.Length:
                        host = Arguments[++i];
                        break;

                    case "--port" when i + 1 < Arguments.Length:
                        port = UInt16.Parse(Arguments[++i]);
                        break;

                    case "--tls":
                        tls  = true;
                        if (port == 8080)
                            port = 8443;
                        break;

                }
            }

            return new Target(host, port, tls);

        }

        #endregion

        #region RoundTripAsync(Request, Window = null, Bodyless = false)

        /// <summary>
        /// Send one request, read one complete response.
        ///
        /// Pass Bodyless for a HEAD request: the reply carries a Content-Length
        /// describing content it must not send, so a reader that trusts the field
        /// would wait for bytes that never arrive.
        /// </summary>
        public Task<String> RoundTripAsync(String     Request,
                                           TimeSpan?  Window     = null,
                                           Boolean    Bodyless   = false)
            => RawConnection.RoundTripAsync(Host, Port, Request, TLS, Window, Bodyless);

        public Task<RawConnection> ConnectAsync()
            => RawConnection.ConnectAsync(Host, Port, TLS);

        #endregion

        #region Banner(Harness)

        public void Banner(String Harness)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {Harness} → {(TLS ? "https" : "http")}://{Authority} ===");
            Console.WriteLine();
        }

        #endregion

    }

}
