using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

/// <summary>The shape of the webhook body.</summary>
public enum WebhookFormat
{
    /// <summary>The <see cref="Alert"/> as camelCase JSON.</summary>
    Json,

    /// <summary>
    /// A Slack or Mattermost <c>{"text": "..."}</c> message. <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> are
    /// escaped, so text from an exception can't ping a channel or hide a link.
    /// </summary>
    Slack,

    /// <summary>
    /// A Discord <c>{"content": "..."}</c> message, cut to Discord's 2000 characters, with mentions turned off
    /// so text from an exception can't ping anybody.
    /// </summary>
    Discord,

    /// <summary>
    /// A Microsoft Teams message with an Adaptive Card (<c>{"type": "message", "attachments": [...]}</c>), as
    /// the webhooks of Teams workflows ("Post to a channel when a webhook request is received") take it: the
    /// title, the message, and the job, method, resource and server as facts. Markdown characters are escaped,
    /// so text from an exception can't add links or formatting.
    /// </summary>
    Teams,
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

    /// <summary>
    /// How long one request may take, enforced per request; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// turns it off. Default 10 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Extra request headers, for example an authorization token.</summary>
    public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many requests may be in flight at once; further alerts are dropped with a warning. Default 16.</summary>
    public int MaxConcurrentRequests { get; set; } = 16;

    /// <summary>
    /// Which alerts are sent, beyond <see cref="Events"/>: the jobs they are about and a cooldown. <c>null</c>, the
    /// default, sends every alert of <see cref="Events"/>.
    /// </summary>
    public AlertRule? Rule { get; set; }

    /// <summary>
    /// The message, with placeholders: <c>{{id}}</c>, <c>{{kind}}</c>, <c>{{title}}</c>, <c>{{message}}</c>,
    /// <c>{{occurredAt}}</c> (ISO 8601, UTC), <c>{{jobId}}</c>, <c>{{method}}</c>, <c>{{resource}}</c>, <c>{{serverId}}</c>
    /// and <c>{{monitor}}</c>. For <see cref="WebhookFormat.Json"/> it is the whole body, and values are inserted
    /// JSON-escaped, so put them in quotes; for the other formats it replaces the text, and values are escaped for
    /// the format. <c>null</c>, the default, uses the built-in message.
    /// </summary>
    public string? Template { get; set; }

    /// <summary>
    /// How long to wait before each new attempt of an alert whose delivery failed. One minute, then two, by default;
    /// empty turns retries off. Each attempt carries the alert's <see cref="Alert.Id"/>.
    /// </summary>
    public IList<TimeSpan> RetryDelays { get; } = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)];
}

/// <summary>One attempt to deliver an alert, as <see cref="WebhookAlertSink.Deliveries"/> lists it.</summary>
/// <param name="AlertId">The alert's <see cref="Alert.Id"/>.</param>
/// <param name="Title">The alert's title.</param>
/// <param name="At">When the attempt ended, UTC.</param>
/// <param name="Attempt">1 for the first attempt.</param>
/// <param name="Succeeded">Whether the endpoint accepted it.</param>
/// <param name="StatusCode">The HTTP status code, or <c>null</c> when no answer came.</param>
/// <param name="Error">Why it failed, or <c>null</c>.</param>
public sealed record WebhookDelivery(string AlertId, string Title, DateTime At, int Attempt, bool Succeeded, int? StatusCode, string? Error);

/// <summary>
/// Posts alerts to an HTTP endpoint without blocking the job that raised them. Register it with
/// <c>GlobalConfiguration.Configuration.UseWebhookAlerts(url)</c>.
/// </summary>
/// <remarks>
/// <para>A delivery that fails with a network error, a timeout, a 5xx status, 408 or 429 is retried after
/// <see cref="WebhookAlertOptions.RetryDelays"/>, or after the <c>Retry-After</c> the endpoint sent when that is
/// longer (at most <see cref="MaxRetryAfter"/>). Another 4xx status, or a 3xx, isn't retried: the request itself is
/// wrong. Alerts waiting for a retry don't hold a request slot, and at most
/// <see cref="WebhookAlertOptions.MaxConcurrentRequests"/> of them wait at once; a failed alert beyond that isn't
/// retried and is counted in <see cref="Dropped"/>.</para>
/// <para>The sink's own <see cref="HttpClient"/> doesn't follow redirects, so the headers of
/// <see cref="WebhookAlertOptions.Headers"/>, an authorization token for example, never reach another host.</para>
/// </remarks>
public sealed class WebhookAlertSink : IAlertSink, IDisposable, IAsyncDisposable
{
    private static readonly ILog Logger = LogProvider.For<WebhookAlertSink>();

    private static readonly JsonSerializerOptions JsonSettings = Dashboard.DashboardJson.Options;

    /// <summary>The longest <see cref="DisposeAsync"/> waits for requests in flight.</summary>
    public static readonly TimeSpan MaxDisposeWait = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait before a retry an endpoint can ask for with <c>Retry-After</c>: 10 minutes.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(10);

    // How long Dispose waits, after canceling them, for the requests in flight to record their outcome
    private static readonly TimeSpan CanceledRequestsWait = TimeSpan.FromSeconds(1);

    private const int DiscordMaxContentLength = 2000;

    // Teams refuses messages over about 28 KB; an exception message rarely needs more than this
    private const int TeamsMaxTextLength = 4000;

    // What makes links, emphasis, code and HTML in the Markdown Teams reads; each is escaped with a backslash
    private const string TeamsMarkdownCharacters = "\\`*_[]<>~";

    // What makes masked links, emphasis, spoilers, code, quotes and headings in the Markdown Discord renders in
    // webhook content; each is escaped with a backslash
    private const string DiscordMarkdownCharacters = "\\*_~`|>[]()#";
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _inFlight;

    // Canceled when the sink is disposed, so no request outlives it, whatever the timeout and whoever owns the
    // client. Never disposed: SendAsync may still link to it after Dispose, and a source without a timer or a wait
    // handle holds nothing that needs disposing.
    private readonly CancellationTokenSource _disposeCts = new();

    // Canceled as soon as disposing starts: an alert waiting for a retry isn't worth waiting for. Never disposed,
    // like _disposeCts.
    private readonly CancellationTokenSource _retryCts = new();

    // What requests are made of, read once: changing the options later changes nothing while alerts are sent
    private readonly Uri _url;
    private readonly string _loggableUrl;
    private readonly KeyValuePair<string, string>[] _headers;
    private readonly TimeSpan[] _retryDelays;
    private readonly TimeSpan _timeout;

    private readonly int _maxConcurrentRequests;
    private long _sent;
    private long _dropped;
    private long _failed;
    private int _pendingRetries;
    private int _disposed;
    private readonly LinkedList<WebhookDelivery> _deliveries = new();

    /// <summary>Creates the sink with its own <see cref="HttpClient"/>.</summary>
    public WebhookAlertSink([NotNull] WebhookAlertOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// Creates the sink with the given client, for example one with a stub handler in tests. The client's own
    /// settings, <see cref="HttpClient.Timeout"/> and whether it follows redirects included, are left as they are;
    /// <see cref="WebhookAlertOptions.Timeout"/> is applied to each request. The caller keeps ownership:
    /// <see cref="Dispose"/> doesn't dispose the client, and it is up to the caller to keep it from following
    /// redirects, so the headers don't reach another host.
    /// </summary>
    public WebhookAlertSink([NotNull] WebhookAlertOptions options, [CanBeNull] HttpClient? httpClient)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Url)) throw new ArgumentException("WebhookAlertOptions.Url is required.", nameof(options));

        // The URL itself stays out of the message: Slack, Mattermost and Discord put the webhook's secret in it
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out Uri? url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("WebhookAlertOptions.Url must be an absolute http or https URL.", nameof(options));
        }

        if (options.MaxConcurrentRequests <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentRequests must be positive.");
        if (!IsValidTimeout(options.Timeout)) throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive or Timeout.InfiniteTimeSpan.");
        if (options.RetryDelays.Any(static x => x < TimeSpan.Zero || x.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RetryDelays must be zero or positive, and at most 24 days.");
        }

        _url = url;
        _loggableUrl = GetLoggableUrl(options.Url);
        _headers = [.. options.Headers];
        _retryDelays = [.. options.RetryDelays];
        _timeout = options.Timeout;

        // A caller's client may already have sent requests, and then its Timeout can't be changed. The sink's
        // timeout is enforced per request instead, so its own client doesn't need one either.
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient(CreateHandler()) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _maxConcurrentRequests = options.MaxConcurrentRequests;
        _inFlight = new SemaphoreSlim(_maxConcurrentRequests, _maxConcurrentRequests);
    }

    /// <summary>
    /// The options. <see cref="WebhookAlertOptions.Url"/>, <see cref="WebhookAlertOptions.Headers"/>,
    /// <see cref="WebhookAlertOptions.Timeout"/>, <see cref="WebhookAlertOptions.RetryDelays"/> and
    /// <see cref="WebhookAlertOptions.MaxConcurrentRequests"/> are read when the sink is created, so changing them
    /// later changes nothing; the others are read for each alert.
    /// </summary>
    public WebhookAlertOptions Options { get; }

    /// <summary>Alerts delivered with a successful status code.</summary>
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>Alerts waiting for a retry, for tests.</summary>
    internal int PendingRetries => Volatile.Read(ref _pendingRetries);

    /// <summary>
    /// Alerts dropped because too many requests were in flight, or failed alerts not retried because too many
    /// already waited for a retry.
    /// </summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Alerts whose request failed or returned a non-success status code.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>The last attempts to deliver alerts, newest first: at most <see cref="MaxDeliveries"/>, kept in memory.</summary>
    public IReadOnlyList<WebhookDelivery> Deliveries
    {
        get
        {
            lock (_deliveries)
            {
                return [.. _deliveries];
            }
        }
    }

    /// <summary>How many attempts <see cref="Deliveries"/> keeps: 50.</summary>
    public const int MaxDeliveries = 50;

    /// <inheritdoc />
    public void Publish(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if ((Options.Events & alert.Kind) == 0) return;
        if (Options.Rule != null && !Options.Rule.Allows(alert)) return;

        if (Volatile.Read(ref _disposed) != 0)
        {
            Logger.Debug($"Webhook alert '{alert.Title}' was not sent: the sink is disposed.");
            return;
        }

        if (!_inFlight.Wait(0))
        {
            Interlocked.Increment(ref _dropped);
            Logger.Warn($"Webhook alert '{alert.Title}' was dropped: {_maxConcurrentRequests} requests are already in flight.");
            return;
        }

        _ = Task.Run(() => SendInBackgroundAsync(alert));
    }

    /// <summary>Builds the request body for an alert.</summary>
    public string CreateBody([NotNull] Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        if (Options.Template != null)
        {
            return CreateTemplateBody(alert, Options.Template);
        }

        if (Options.Format == WebhookFormat.Slack)
        {
            // Slack reads <...> as links and mentions (<!channel>, <https://evil|click here>) and &...; as entities
            StringBuilder text = new();
            text.Append('*').Append(EscapeSlack(alert.Title)).Append('*');
            text.Append('\n').Append(EscapeSlack(alert.Message));
            if (alert.JobId != null) text.Append("\nJob: ").Append(EscapeSlack(alert.JobId));
            if (alert.Resource != null) text.Append("\nResource: ").Append(EscapeSlack(alert.Resource));
            if (alert.ServerId != null) text.Append("\nServer: ").Append(EscapeSlack(alert.ServerId));

            return JsonSerializer.Serialize(new { text = text.ToString() }, JsonSettings);
        }

        if (Options.Format == WebhookFormat.Teams)
        {
            return CreateTeamsBody(alert);
        }

        if (Options.Format == WebhookFormat.Discord)
        {
            // Discord renders Markdown in the content: a job's text could otherwise add masked links
            // ([text](https://evil)), headings or code blocks
            StringBuilder text = new();
            text.Append("**").Append(EscapeDiscord(alert.Title)).Append("**");
            text.Append('\n').Append(EscapeDiscord(alert.Message));
            if (alert.JobId != null) text.Append("\nJob: ").Append(EscapeDiscord(alert.JobId));
            if (alert.Resource != null) text.Append("\nResource: ").Append(EscapeDiscord(alert.Resource));
            if (alert.ServerId != null) text.Append("\nServer: ").Append(EscapeDiscord(alert.ServerId));

            string content = text.Length <= DiscordMaxContentLength ? text.ToString() : text.ToString(0, DiscordMaxContentLength - 1) + "…";

            // An empty "parse" list turns off @everyone, @here, user and role mentions in the content
            return JsonSerializer.Serialize(new { content, allowed_mentions = new { parse = Array.Empty<string>() } }, JsonSettings);
        }

        return JsonSerializer.Serialize(new
        {
            source = "nightshift",
            id = alert.Id,
            alert.Monitor,
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

    /// <summary>
    /// Sends one alert and waits for the answer. It doesn't count against
    /// <see cref="WebhookAlertOptions.MaxConcurrentRequests"/>, which limits only <see cref="Publish"/>.
    /// </summary>
    public async Task<bool> SendAsync([NotNull] Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        SendResult result = await SendCoreAsync(alert, attempt: 1).ConfigureAwait(false);
        return result.Sent;
    }

    /// <summary>The handler of the sink's own client: it doesn't follow redirects.</summary>
    internal static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false };

    /// <summary>
    /// The URL as it may appear in logs: scheme, host and port, plus a short hash of the whole URL to tell
    /// webhooks on the same host apart. The path and query are left out, because Slack, Mattermost and Discord
    /// put the webhook's secret token there.
    /// </summary>
    internal static string GetLoggableUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return "(invalid URL)";

        string server = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        bool hasSecretParts = uri.UserInfo.Length > 0 || uri.PathAndQuery != "/" || uri.Fragment.Length > 0;

        return hasSecretParts ? $"{server}/... (hash {GetShortHash(uri.OriginalString)})" : server;
    }

    private static string GetShortHash(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] hash = SHA256.HashData(bytes);
        StringBuilder text = new(6);
        for (int i = 0; i < 3; i++)
        {
            text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    private string CreateTemplateBody(Alert alert, string template)
    {
        switch (Options.Format)
        {
            case WebhookFormat.Slack:
                return JsonSerializer.Serialize(new { text = Render(template, alert, EscapeSlack) }, JsonSettings);
            case WebhookFormat.Discord:
                string content = Render(template, alert, EscapeDiscord);
                if (content.Length > DiscordMaxContentLength) content = string.Concat(content.AsSpan(0, DiscordMaxContentLength - 1), "…");
                return JsonSerializer.Serialize(new { content, allowed_mentions = new { parse = Array.Empty<string>() } }, JsonSettings);
            case WebhookFormat.Teams:
                return CreateTeamsBody(alert, Render(template, alert, EscapeTeams));
            default:
                return Render(template, alert, static value => JsonEncodedText.Encode(value, JsonSettings.Encoder).ToString());
        }
    }

    // Replaces the placeholders with the alert's values, each escaped for where it goes
    internal static string Render(string template, Alert alert, Func<string, string> escape)
    {
        StringBuilder text = new(template);
        Replace("id", alert.Id);
        Replace("kind", alert.Kind.ToString());
        Replace("title", alert.Title);
        Replace("message", alert.Message);
        Replace("occurredAt", alert.OccurredAt.ToString("o", CultureInfo.InvariantCulture));
        Replace("jobId", alert.JobId);
        Replace("method", alert.Method);
        Replace("resource", alert.Resource);
        Replace("serverId", alert.ServerId);
        Replace("monitor", alert.Monitor);
        return text.ToString();

        void Replace(string name, string? value) => text.Replace("{{" + name + "}}", escape(value ?? string.Empty));
    }

    private static string CreateTeamsBody(Alert alert) => CreateTeamsBody(alert, null);

    private static string CreateTeamsBody(Alert alert, string? text)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = JsonSettings.Encoder }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "message");
            writer.WriteStartArray("attachments");
            writer.WriteStartObject();
            writer.WriteString("contentType", "application/vnd.microsoft.card.adaptive");
            writer.WriteNull("contentUrl");
            writer.WriteStartObject("content");
            writer.WriteString("$schema", "http://adaptivecards.io/schemas/adaptive-card.json");
            writer.WriteString("type", "AdaptiveCard");
            writer.WriteString("version", "1.4");
            writer.WriteStartArray("body");

            writer.WriteStartObject();
            writer.WriteString("type", "TextBlock");
            writer.WriteString("text", EscapeTeams(alert.Title));
            writer.WriteString("weight", "Bolder");
            writer.WriteString("size", "Medium");
            writer.WriteBoolean("wrap", true);
            writer.WriteEndObject();

            writer.WriteStartObject();
            writer.WriteString("type", "TextBlock");
            writer.WriteString("text", text ?? EscapeTeams(alert.Message));
            writer.WriteBoolean("wrap", true);
            writer.WriteEndObject();

            writer.WriteStartObject();
            writer.WriteString("type", "FactSet");
            writer.WriteStartArray("facts");
            WriteFact(writer, "Kind", alert.Kind.ToString());
            WriteFact(writer, "Job", alert.JobId);
            WriteFact(writer, "Method", alert.Method);
            WriteFact(writer, "Resource", alert.Resource);
            WriteFact(writer, "Server", alert.ServerId);
            WriteFact(writer, "When", alert.OccurredAt.ToString("u", CultureInfo.InvariantCulture));
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteFact(Utf8JsonWriter writer, string title, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        writer.WriteStartObject();
        writer.WriteString("title", title);
        writer.WriteString("value", EscapeTeams(value));
        writer.WriteEndObject();
    }

    private static string EscapeTeams(string value)
    {
        if (value.Length > TeamsMaxTextLength) value = string.Concat(value.AsSpan(0, TeamsMaxTextLength - 1), "…");

        StringBuilder text = new(value.Length + 16);
        foreach (char character in value)
        {
            if (TeamsMarkdownCharacters.Contains(character)) text.Append('\\');
            text.Append(character);
        }

        return text.ToString();
    }

    private static string EscapeDiscord(string value)
    {
        StringBuilder text = new(value.Length + 16);
        foreach (char character in value)
        {
            if (DiscordMarkdownCharacters.Contains(character)) text.Append('\\');
            text.Append(character);
        }

        return text.ToString();
    }

    private static string EscapeSlack(string value)
    {
        return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static bool IsValidTimeout(TimeSpan timeout)
    {
        // The range HttpClient.Timeout and CancellationTokenSource.CancelAfter both accept
        return timeout == System.Threading.Timeout.InfiniteTimeSpan
            || (timeout > TimeSpan.Zero && timeout.TotalMilliseconds <= int.MaxValue);
    }

    private async Task SendInBackgroundAsync(Alert alert)
    {
        // Nothing waits for this task, so nothing may escape it
        try
        {
            SendResult result;
            try
            {
                result = await SendCoreAsync(alert, attempt: 1).ConfigureAwait(false);
            }
            finally
            {
                // Publish took this slot, so it's released exactly once
                _inFlight.Release();
            }

            if (result.Retryable) await RetryAsync(alert, result).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.ErrorException($"Webhook alert '{alert.Title}' could not be delivered to {_loggableUrl}.", ex);
        }
    }

    private async Task RetryAsync(Alert alert, SendResult result)
    {
        if (_retryDelays.Length == 0 || Volatile.Read(ref _disposed) != 0) return;

        // Retries don't hold a slot while they wait, so their number is limited on its own
        if (Interlocked.Increment(ref _pendingRetries) > _maxConcurrentRequests)
        {
            Interlocked.Decrement(ref _pendingRetries);
            Interlocked.Increment(ref _dropped);
            Logger.Warn($"Webhook alert '{alert.Title}' won't be retried: {_maxConcurrentRequests} alerts already wait for a retry.");
            return;
        }

        try
        {
            for (int retry = 0; retry < _retryDelays.Length && Volatile.Read(ref _disposed) == 0; retry++)
            {
                try
                {
                    await Task.Delay(GetRetryDelay(_retryDelays[retry], result.RetryAfter), _retryCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.Debug($"Webhook alert '{alert.Title}' won't be retried: the sink was disposed.");
                    return;
                }

                result = await SendCoreAsync(alert, attempt: retry + 2).ConfigureAwait(false);
                if (!result.Retryable) return;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingRetries);
        }
    }

    // A Retry-After longer than the configured delay wins, up to MaxRetryAfter
    internal static TimeSpan GetRetryDelay(TimeSpan configured, TimeSpan? retryAfter)
    {
        return retryAfter is { } asked && asked > configured
            ? (asked > MaxRetryAfter ? MaxRetryAfter : asked)
            : configured;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter) return null;
        if (retryAfter.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retryAfter.Date is { } date) return date.UtcDateTime > JobClock.UtcNow ? date.UtcDateTime - JobClock.UtcNow : TimeSpan.Zero;
        return null;
    }

    // Worth another attempt: the endpoint is down or overloaded, or asked for one. Another 4xx (or a redirect,
    // which the sink doesn't follow) fails the same way each time.
    private static bool IsTransient(HttpStatusCode statusCode)
    {
        return (int)statusCode >= 500 || statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
    }

    private void Record(Alert alert, int attempt, bool succeeded, int? statusCode, string? error)
    {
        lock (_deliveries)
        {
            _deliveries.AddFirst(new WebhookDelivery(alert.Id, alert.Title, JobClock.UtcNow, attempt, succeeded, statusCode, error));
            if (_deliveries.Count > MaxDeliveries) _deliveries.RemoveLast();
        }
    }

    private async Task<SendResult> SendCoreAsync(Alert alert, int attempt)
    {
        TimeSpan timeout = _timeout;

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, _url);
            request.Content = new StringContent(CreateBody(alert), Encoding.UTF8, "application/json");

            foreach (KeyValuePair<string, string> header in _headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            timeoutCts.CancelAfter(timeout);

            using HttpResponseMessage response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            Record(alert, attempt, response.IsSuccessStatusCode, (int)response.StatusCode, response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");

            if (response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _sent);
                return new SendResult(Sent: true, Retryable: false, RetryAfter: null);
            }

            Interlocked.Increment(ref _failed);
            Logger.Warn($"Webhook alert '{alert.Title}' was rejected by {_loggableUrl}: HTTP {(int)response.StatusCode}.");
            return new SendResult(Sent: false, Retryable: IsTransient(response.StatusCode), GetRetryAfter(response));
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            Record(alert, attempt, false, null, "The sink was disposed");
            Interlocked.Increment(ref _failed);
            Logger.Warn($"Webhook alert '{alert.Title}' was not sent to {_loggableUrl}: the sink was disposed before an answer came.");
            return new SendResult(Sent: false, Retryable: false, RetryAfter: null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            Record(alert, attempt, false, null, $"No answer within {timeout}");
            Interlocked.Increment(ref _failed);
            Logger.Warn($"Webhook alert '{alert.Title}' could not be sent to {_loggableUrl}: no answer within {timeout}.");
            return new SendResult(Sent: false, Retryable: true, RetryAfter: null);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // HttpClient's exceptions name the host at most, never the path
            Record(alert, attempt, false, null, ex.Message);
            Interlocked.Increment(ref _failed);
            Logger.WarnException($"Webhook alert '{alert.Title}' could not be sent to {_loggableUrl}.", ex);
            return new SendResult(Sent: false, Retryable: true, RetryAfter: null);
        }
    }

    /// <summary>
    /// Stops sending alerts at once: later <see cref="Publish"/> calls are ignored, alerts waiting for a retry are
    /// given up, and the requests in flight (those of <see cref="SendAsync"/> too) are canceled and counted in
    /// <see cref="Failed"/>. It waits a moment for them to end, then disposes the <see cref="HttpClient"/> the sink
    /// created; a client passed in is left to its owner. To let the requests in flight finish first, use
    /// <see cref="DisposeAsync"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _retryCts.Cancel();
        _disposeCts.Cancel();

        if (!WaitForRequests(CanceledRequestsWait))
        {
            Logger.Warn($"Disposed the webhook alert sink for {_loggableUrl} while canceled requests were still ending.");
        }

        DisposeClient();
    }

    /// <summary>
    /// Stops sending alerts: later <see cref="Publish"/> calls are ignored and alerts waiting for a retry are given up.
    /// Waits, without blocking a thread, for the requests <see cref="Publish"/> started to finish, then cancels the
    /// requests still running (those of <see cref="SendAsync"/> too) and disposes the <see cref="HttpClient"/> the
    /// sink created; a client passed in is left to its owner. The wait lasts at most
    /// <see cref="WebhookAlertOptions.Timeout"/>, and never more than <see cref="MaxDisposeWait"/>, also when the
    /// timeout is <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _retryCts.Cancel();

        TimeSpan wait = _timeout == System.Threading.Timeout.InfiniteTimeSpan || _timeout > MaxDisposeWait ? MaxDisposeWait : _timeout;
        if (!await WaitForRequestsAsync(wait).ConfigureAwait(false))
        {
            Logger.Warn($"Disposing the webhook alert sink for {_loggableUrl} while requests are still in flight; they are canceled.");
        }

        _disposeCts.Cancel();
        DisposeClient();
    }

    // Holding every slot means every request Publish started has released its own. The semaphore isn't disposed:
    // a SemaphoreSlim that never handed out its wait handle holds nothing that needs disposing.
    private bool WaitForRequests(TimeSpan wait)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < _maxConcurrentRequests; i++)
        {
            TimeSpan remaining = wait - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero || !_inFlight.Wait(remaining)) return false;
        }

        return true;
    }

    private async Task<bool> WaitForRequestsAsync(TimeSpan wait)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < _maxConcurrentRequests; i++)
        {
            TimeSpan remaining = wait - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero || !await _inFlight.WaitAsync(remaining).ConfigureAwait(false)) return false;
        }

        return true;
    }

    private void DisposeClient()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>How an attempt ended.</summary>
    private readonly record struct SendResult(bool Sent, bool Retryable, TimeSpan? RetryAfter);
}
