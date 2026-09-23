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

using Microsoft.Extensions.Logging;

#endregion

namespace org.GraphDefined.Vanaheimr.Hermod.HTTP1.Demo
{

    /// <summary>
    /// An ILoggerFactory that writes to the console.
    ///
    /// Hermod's servers take an ILoggerFactory and default it to
    /// NullLoggerFactory.Instance, which is the right default for a library and
    /// the wrong one for a conformance target: every diagnosis the stack writes
    /// about a connection it is tearing down goes nowhere.
    ///
    /// That is not hypothetical. On 2026-09-23 the Autobahn nightly failed case
    /// 12.4.18 — 1000 compressed messages of 128 KiB each — with our server
    /// dropping the TCP connection after 716 of them and no close handshake. The
    /// two code paths that can end that loop without a close frame both log, one
    /// at Debug ("Read error on WebSocket connection") and one at Error
    /// ("Exception in HTTP WebSocket server connection loop"). Both records were
    /// written to a NullLogger, so the artifact held a run of 517 cases and not
    /// one word about why the one that failed did.
    ///
    /// Hence a logger rather than a bigger hypothesis. Deliberately hand-rolled:
    /// Microsoft.Extensions.Logging.Console would do this too, and this repository
    /// adds no package it can write in forty lines.
    /// </summary>
    public sealed class ConsoleLoggerFactory : ILoggerFactory
    {

        private readonly LogLevel minimumLevel;

        /// <summary>
        /// Create a console logger factory.
        /// </summary>
        /// <param name="MinimumLevel">Records below this level are dropped.</param>
        public ConsoleLoggerFactory(LogLevel MinimumLevel)
        {
            this.minimumLevel = MinimumLevel;
        }

        /// <summary>
        /// Parse a level name — "debug", "warning", … — case-insensitively.
        /// Anything unrecognised is Information, because a typo in a diagnostic
        /// flag must not silence the diagnostics.
        /// </summary>
        public static LogLevel ParseLevel(String? Text)

            => Text?.ToLowerInvariant() switch {
                   "trace"                 => LogLevel.Trace,
                   "debug"                 => LogLevel.Debug,
                   "info" or "information" => LogLevel.Information,
                   "warn" or "warning"     => LogLevel.Warning,
                   "error"                 => LogLevel.Error,
                   "critical"              => LogLevel.Critical,
                   "none" or "off"         => LogLevel.None,
                   _                       => LogLevel.Information
               };

        public ILogger CreateLogger(String CategoryName)
            => new ConsoleLogger(CategoryName, minimumLevel);

        public void AddProvider(ILoggerProvider Provider)
        { }

        public void Dispose()
        { }

    }


    /// <summary>
    /// One console logger, for one category.
    /// </summary>
    internal sealed class ConsoleLogger : ILogger
    {

        // The server logs from every connection task at once. An interleaved
        // stack trace is worth less than no stack trace at all, so a record and
        // its exception are written as one unit.
        private static readonly Lock      consoleLock = new ();

        private readonly        String    category;
        private readonly        LogLevel  minimumLevel;

        public ConsoleLogger(String    Category,
                             LogLevel  MinimumLevel)
        {

            // "org.GraphDefined.Vanaheimr.Hermod.WebSocket.WebSocketServer"
            //   -> "WebSocketServer"
            var lastDot        = Category.LastIndexOf('.');

            this.category      = lastDot >= 0 && lastDot < Category.Length - 1
                                     ? Category[(lastDot + 1)..]
                                     : Category;

            this.minimumLevel  = MinimumLevel;

        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => null;

        public Boolean IsEnabled(LogLevel Level)
            => Level != LogLevel.None && Level >= minimumLevel;

        public void Log<TState>(LogLevel                          Level,
                                EventId                           EventId,
                                TState                            State,
                                Exception?                        ExceptionOccurred,
                                Func<TState, Exception?, String>  Formatter)
        {

            if (!IsEnabled(Level))
                return;

            var line = $"  {DateTimeOffset.Now:HH:mm:ss.fff}  {Abbreviate(Level)}  {category}: {Formatter(State, ExceptionOccurred)}";

            lock (consoleLock)
            {

                Console.WriteLine(line);

                if (ExceptionOccurred is not null)
                    Console.WriteLine(ExceptionOccurred.ToString());

            }

        }

        private static String Abbreviate(LogLevel Level)

            => Level switch {
                   LogLevel.Trace        => "trce",
                   LogLevel.Debug        => "dbug",
                   LogLevel.Information  => "info",
                   LogLevel.Warning      => "WARN",
                   LogLevel.Error        => "FAIL",
                   LogLevel.Critical     => "CRIT",
                   _                     => "????"
               };

    }

}
