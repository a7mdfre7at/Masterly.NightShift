// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
//
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as
// published by the Free Software Foundation, either version 3
// of the License, or any later version.
//
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

#nullable enable

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using Hangfire.Annotations;
using Hangfire.Common;
using Hangfire.Logging;

namespace Hangfire.Server
{
    /// <summary>
    /// Settings for <see cref="KeepAliveProcess"/>.
    /// </summary>
    public sealed class KeepAliveOptions
    {
        /// <summary>
        /// Gets or sets the URL the process requests, normally the application's own public address, for example
        /// the dashboard's <c>/hangfire/ping</c> endpoint. The request must go through the host that applies the idle
        /// timeout (IIS, not a Kestrel port behind it), or it won't count as activity.
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// Gets or sets a function that resolves the URL on first use, for hosts that only know their address
        /// after start-up. Used when <see cref="Url"/> is <c>null</c>.
        /// </summary>
        public Func<string?>? UrlFactory { get; set; }

        /// <summary>Gets or sets how often the request is sent. Default is 5 minutes; IIS idles after 20 by default.</summary>
        public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Gets or sets the request timeout. Default is 15 seconds.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>Gets or sets whether failed requests and non-success status codes are logged as warnings. Default is <c>true</c>.</summary>
        public bool LogFailures { get; set; } = true;
    }

    /// <summary>
    /// The result of the last keep-alive request, shown on the dashboard's Servers page.
    /// </summary>
    public sealed class KeepAliveStatus
    {
        internal KeepAliveStatus(string? url, DateTime? lastAttemptAt, bool? lastSucceeded, string? lastError, TimeSpan interval)
        {
            Url = url;
            LastAttemptAt = lastAttemptAt;
            LastSucceeded = lastSucceeded;
            LastError = lastError;
            Interval = interval;
        }

        /// <summary>Gets the URL being requested, once resolved.</summary>
        public string? Url { get; }

        /// <summary>Gets when the last request was sent (UTC), or <c>null</c> before the first one.</summary>
        public DateTime? LastAttemptAt { get; }

        /// <summary>
        /// Gets whether the last request reached the host, whatever the status code: an error page still counts
        /// as activity for the idle timeout. <c>false</c> means no response at all (DNS, connection or timeout).
        /// </summary>
        public bool? LastSucceeded { get; }

        /// <summary>Gets the last failure, or a note about a non-success status code.</summary>
        public string? LastError { get; }

        /// <summary>Gets the configured interval.</summary>
        public TimeSpan Interval { get; }
    }

    /// <summary>
    /// Keeps a web host from idling by requesting one of the application's own URLs at a fixed interval.
    /// IIS counts the request as activity and doesn't shut the worker process down for inactivity.
    /// </summary>
    /// <remarks>
    /// <para>This covers the idle timeout only. A scheduled application pool recycle, a deployment or a server
    /// restart still stops the process, and nothing inside the process can start it again: for that, set the
    /// application pool to <c>AlwaysRunning</c> with preloading, use "Always On" on Azure App Service, or host
    /// the Hangfire server in a Windows Service or worker. The Servers page shows this process's hosting and
    /// keep-alive status.</para>
    /// <para>Register it as an additional background process: <c>new BackgroundJobServer(options, storage,
    /// new[] { new KeepAliveProcess(...) })</c>, or with <c>services.AddHangfireKeepAlive(...)</c> in ASP.NET Core.</para>
    /// </remarks>
    public sealed class KeepAliveProcess : IBackgroundProcess, IDisposable
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(KeepAliveProcess));
        private static KeepAliveStatus? _status;

        private readonly KeepAliveOptions _options;
        private readonly HttpClient _httpClient;
        private string? _url;
        private bool _urlResolved;

        /// <summary>
        /// Initializes the process to request the given URL every 5 minutes.
        /// </summary>
        /// <param name="url">The application's own URL, see <see cref="KeepAliveOptions.Url"/>.</param>
        public KeepAliveProcess([NotNull] string url)
            : this(new KeepAliveOptions { Url = url ?? throw new ArgumentNullException(nameof(url)) })
        {
        }

        /// <summary>
        /// Initializes the process.
        /// </summary>
        /// <param name="options">The settings. Either <see cref="KeepAliveOptions.Url"/> or
        /// <see cref="KeepAliveOptions.UrlFactory"/> must be set.</param>
        public KeepAliveProcess([NotNull] KeepAliveOptions options)
            : this(options, null)
        {
        }

        internal KeepAliveProcess([NotNull] KeepAliveOptions options, [CanBeNull] HttpClient? httpClient)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            if (options.Url == null && options.UrlFactory == null)
            {
                throw new ArgumentException("Either Url or UrlFactory must be set.", nameof(options));
            }

            if (options.Interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Interval should be greater than zero.");
            if (options.Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Timeout should be greater than zero.");

            _httpClient = httpClient ?? new HttpClient { Timeout = options.Timeout };
            _status = new KeepAliveStatus(options.Url, null, null, null, options.Interval);
        }

        /// <summary>
        /// Gets the status of the keep-alive process running in this process, or <c>null</c> when none is configured.
        /// </summary>
        [CanBeNull]
        public static KeepAliveStatus? Status => Volatile.Read(ref _status);

        /// <summary>
        /// Gets whether this process is hosted by IIS or IIS Express, where the idle timeout applies.
        /// </summary>
        public static bool IsHostedByIis
        {
            get
            {
                try
                {
                    var name = Process.GetCurrentProcess().ProcessName;
                    return name.Equals("w3wp", StringComparison.OrdinalIgnoreCase) || name.Equals("iisexpress", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    return false;
                }
            }
        }

        /// <inheritdoc />
        public void Execute(BackgroundProcessContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            context.ShutdownToken.WaitOrThrow(_options.Interval);
            Ping(context.ShutdownToken);
        }

        /// <summary>Sends one keep-alive request now. Returns whether the host answered, with any status code.</summary>
        public bool Ping(CancellationToken cancellationToken)
        {
            var url = ResolveUrl();
            var now = DateTime.UtcNow;

            if (url == null)
            {
                Volatile.Write(ref _status, new KeepAliveStatus(null, now, false, "The keep-alive URL could not be resolved.", _options.Interval));
                return false;
            }

            try
            {
                using (var response = _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).GetAwaiter().GetResult())
                {
                    // Any answer proves the host handled a request; a non-success code is only noted
                    var note = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                    Volatile.Write(ref _status, new KeepAliveStatus(url, now, true, note, _options.Interval));

                    if (note != null && _options.LogFailures)
                    {
                        Logger.Warn($"Keep-alive request to '{url}' returned {note}. The host is alive, but the URL may be wrong or blocked by the dashboard authorization.");
                    }

                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Volatile.Write(ref _status, new KeepAliveStatus(url, now, false, ex.Message, _options.Interval));

                if (_options.LogFailures)
                {
                    Logger.WarnException($"Keep-alive request to '{url}' failed.", ex);
                }

                return false;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _httpClient.Dispose();
        }

        /// <inheritdoc />
        public override string ToString() => GetType().Name;

        private string? ResolveUrl()
        {
            if (_urlResolved) return _url;

            _url = _options.Url;

            if (_url == null && _options.UrlFactory != null)
            {
                try
                {
                    _url = _options.UrlFactory();
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    Logger.WarnException("The keep-alive URL factory threw an exception; it will be retried on the next attempt.", ex);
                    return null;
                }
            }

            _urlResolved = _url != null;
            return _url;
        }
    }
}
