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
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift
{
    /// <summary>The shape of the webhook body.</summary>
    public enum WebhookFormat
    {
        /// <summary>The <see cref="Alert"/> as camelCase JSON.</summary>
        Json,

        /// <summary>A Slack / Mattermost / Discord-compatible <c>{"text": "..."}</c> message.</summary>
        Slack,
    }

    /// <summary>Settings of <see cref="WebhookAlertSink"/>.</summary>
    public sealed class WebhookAlertOptions
    {
        /// <summary>The URL that receives a POST per alert. Required.</summary>
        public string? Url { get; set; }

        /// <summary>Which alerts are sent. Default <see cref="AlertKind.All"/>.</summary>
        public AlertKind Events { get; set; } = AlertKind.All;

        /// <summary>The body format. Default <see cref="WebhookFormat.Json"/>.</summary>
        public WebhookFormat Format { get; set; } = WebhookFormat.Json;

        /// <summary>Request timeout. Default 10 seconds.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Extra request headers, for example an authorization token.</summary>
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many requests may be in flight at once; further alerts are dropped with a warning. Default 16.</summary>
        public int MaxConcurrentRequests { get; set; } = 16;
    }

    /// <summary>
    /// Posts alerts to an HTTP endpoint without blocking the job that raised them. Register it with
    /// <c>GlobalConfiguration.Configuration.UseWebhookAlerts(url)</c>.
    /// </summary>
    public sealed class WebhookAlertSink : IAlertSink, IDisposable
    {
        private static readonly ILog Logger = LogProvider.For<WebhookAlertSink>();

        private static readonly JsonSerializerOptions JsonSettings = Dashboard.DashboardJson.Options;

        private readonly WebhookAlertOptions _options;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _inFlight;
        private long _sent;
        private long _dropped;
        private long _failed;

        /// <summary>Creates the sink with its own <see cref="HttpClient"/>.</summary>
        public WebhookAlertSink([NotNull] WebhookAlertOptions options)
            : this(options, null)
        {
        }

        /// <summary>Creates the sink with the given client; tests use it with a stub handler.</summary>
        public WebhookAlertSink([NotNull] WebhookAlertOptions options, [CanBeNull] HttpClient? httpClient)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (String.IsNullOrWhiteSpace(options.Url)) throw new ArgumentException("WebhookAlertOptions.Url is required.", nameof(options));
            if (options.MaxConcurrentRequests <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentRequests must be positive.");

            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = options.Timeout;
            _inFlight = new SemaphoreSlim(options.MaxConcurrentRequests, options.MaxConcurrentRequests);
        }

        /// <summary>The options.</summary>
        public WebhookAlertOptions Options => _options;

        /// <summary>Alerts delivered with a successful status code.</summary>
        public long Sent => Interlocked.Read(ref _sent);

        /// <summary>Alerts dropped because too many requests were in flight.</summary>
        public long Dropped => Interlocked.Read(ref _dropped);

        /// <summary>Alerts whose request failed or returned a non-success status code.</summary>
        public long Failed => Interlocked.Read(ref _failed);

        /// <inheritdoc />
        public void Publish(Alert alert)
        {
            if (alert == null) throw new ArgumentNullException(nameof(alert));
            if ((_options.Events & alert.Kind) == 0) return;

            if (!_inFlight.Wait(0))
            {
                Interlocked.Increment(ref _dropped);
                Logger.Warn($"Webhook alert '{alert.Title}' was dropped: {_options.MaxConcurrentRequests} requests are already in flight.");
                return;
            }

            Task.Run(() => SendAsync(alert));
        }

        /// <summary>Builds the request body for an alert.</summary>
        public string CreateBody([NotNull] Alert alert)
        {
            if (alert == null) throw new ArgumentNullException(nameof(alert));

            if (_options.Format == WebhookFormat.Slack)
            {
                var text = new StringBuilder();
                text.Append('*').Append(alert.Title).Append('*');
                text.Append('\n').Append(alert.Message);
                if (alert.JobId != null) text.Append("\nJob: ").Append(alert.JobId);
                if (alert.Resource != null) text.Append("\nResource: ").Append(alert.Resource);
                if (alert.ServerId != null) text.Append("\nServer: ").Append(alert.ServerId);

                return JsonSerializer.Serialize(new { text = text.ToString() }, JsonSettings);
            }

            return JsonSerializer.Serialize(new
            {
                source = "nightshift",
                alert.Kind,
                alert.Title,
                alert.Message,
                alert.OccurredAt,
                alert.JobId,
                alert.Method,
                alert.Resource,
                alert.ServerId,
            }, JsonSettings);
        }

        /// <summary>Sends one alert and waits for the answer; <see cref="Publish"/> calls it in the background.</summary>
        public async Task<bool> SendAsync([NotNull] Alert alert)
        {
            if (alert == null) throw new ArgumentNullException(nameof(alert));

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, _options.Url))
                {
                    request.Content = new StringContent(CreateBody(alert), Encoding.UTF8, "application/json");

                    foreach (var header in _options.Headers)
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    using (var response = await _httpClient.SendAsync(request).ConfigureAwait(false))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            Interlocked.Increment(ref _sent);
                            return true;
                        }

                        Interlocked.Increment(ref _failed);
                        Logger.Warn($"Webhook alert '{alert.Title}' was rejected by {_options.Url}: HTTP {(int)response.StatusCode}.");
                        return false;
                    }
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Interlocked.Increment(ref _failed);
                Logger.WarnException($"Webhook alert '{alert.Title}' could not be sent to {_options.Url}.", ex);
                return false;
            }
            finally
            {
                if (_inFlight.CurrentCount < _options.MaxConcurrentRequests)
                {
                    _inFlight.Release();
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _httpClient.Dispose();
            _inFlight.Dispose();
        }
    }
}
