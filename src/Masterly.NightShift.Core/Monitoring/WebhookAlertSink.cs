using System.Diagnostics;
using System.Globalization;
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
}

/// <summary>
/// Posts alerts to an HTTP endpoint without blocking the job that raised them. Register it with
/// <c>GlobalConfiguration.Configuration.UseWebhookAlerts(url)</c>.
/// </summary>
public sealed class WebhookAlertSink : IAlertSink, IDisposable
{
    private static readonly ILog Logger = LogProvider.For<WebhookAlertSink>();

    private static readonly JsonSerializerOptions JsonSettings = Dashboard.DashboardJson.Options;

    /// <summary>The longest <see cref="Dispose"/> waits for requests in flight.</summary>
    public static readonly TimeSpan MaxDisposeWait = TimeSpan.FromSeconds(30);

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

    // Canceled at the end of Dispose's wait, so no request outlives the sink, whatever the timeout and whoever
    // owns the client. Never disposed: SendAsync may still link to it after Dispose, and a source without a
    // timer or a wait handle holds nothing that needs disposing.
    private readonly CancellationTokenSource _disposeCts = new();

    private readonly int _maxConcurrentRequests;
    private long _sent;
    private long _dropped;
    private long _failed;
    private int _disposed;

    /// <summary>Creates the sink with its own <see cref="HttpClient"/>.</summary>
    public WebhookAlertSink([NotNull] WebhookAlertOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// Creates the sink with the given client, for example one with a stub handler in tests. The client's own
    /// settings, <see cref="HttpClient.Timeout"/> included, are left as they are; <see cref="WebhookAlertOptions.Timeout"/>
    /// is applied to each request. The caller keeps ownership: <see cref="Dispose"/> doesn't dispose the client.
    /// </summary>
    public WebhookAlertSink([NotNull] WebhookAlertOptions options, [CanBeNull] HttpClient? httpClient)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Url)) throw new ArgumentException("WebhookAlertOptions.Url is required.", nameof(options));
        if (options.MaxConcurrentRequests <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentRequests must be positive.");
        if (!IsValidTimeout(options.Timeout)) throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive or Timeout.InfiniteTimeSpan.");

        // A caller's client may already have sent requests, and then its Timeout can't be changed. The sink's
        // timeout is enforced per request instead, so its own client doesn't need one either.
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _maxConcurrentRequests = options.MaxConcurrentRequests;
        _inFlight = new SemaphoreSlim(_maxConcurrentRequests, _maxConcurrentRequests);
    }

    /// <summary>The options.</summary>
    public WebhookAlertOptions Options { get; }

    /// <summary>Alerts delivered with a successful status code.</summary>
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>Alerts dropped because too many requests were in flight.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Alerts whose request failed or returned a non-success status code.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <inheritdoc />
    public void Publish(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if ((Options.Events & alert.Kind) == 0) return;

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

        Task.Run(() => SendAndReleaseAsync(alert));
    }

    /// <summary>Builds the request body for an alert.</summary>
    public string CreateBody([NotNull] Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

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

        return await SendCoreAsync(alert).ConfigureAwait(false);
    }

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

    private static string CreateTeamsBody(Alert alert)
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
            writer.WriteString("text", EscapeTeams(alert.Message));
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

    private async Task SendAndReleaseAsync(Alert alert)
    {
        try
        {
            await SendCoreAsync(alert).ConfigureAwait(false);
        }
        finally
        {
            // Publish took this slot, so it's released exactly once
            _inFlight.Release();
        }
    }

    private async Task<bool> SendCoreAsync(Alert alert)
    {
        TimeSpan timeout = Options.Timeout;

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, Options.Url);
            request.Content = new StringContent(CreateBody(alert), Encoding.UTF8, "application/json");

            foreach (KeyValuePair<string, string> header in Options.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            timeoutCts.CancelAfter(timeout);

            using HttpResponseMessage response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _sent);
                return true;
            }

            Interlocked.Increment(ref _failed);
            Logger.Warn($"Webhook alert '{alert.Title}' was rejected by {GetLoggableUrl(Options.Url)}: HTTP {(int)response.StatusCode}.");
            return false;
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failed);
            Logger.Warn($"Webhook alert '{alert.Title}' was not sent to {GetLoggableUrl(Options.Url)}: the sink was disposed before an answer came.");
            return false;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failed);
            Logger.Warn($"Webhook alert '{alert.Title}' could not be sent to {GetLoggableUrl(Options.Url)}: no answer within {timeout}.");
            return false;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // HttpClient's exceptions name the host at most, never the path
            Interlocked.Increment(ref _failed);
            Logger.WarnException($"Webhook alert '{alert.Title}' could not be sent to {GetLoggableUrl(Options.Url)}.", ex);
            return false;
        }
    }

    /// <summary>
    /// Stops sending alerts, waits for the requests <see cref="Publish"/> started to finish, cancels the requests
    /// still running after the wait (those of <see cref="SendAsync"/> too), then disposes the
    /// <see cref="HttpClient"/> the sink created; a client passed in is left to its owner. The wait lasts at most
    /// <see cref="WebhookAlertOptions.Timeout"/>, and never more than <see cref="MaxDisposeWait"/>, also when the
    /// timeout is <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Holding every slot means every request Publish started has released its own. The semaphore isn't
        // disposed: a SemaphoreSlim that never handed out its wait handle holds nothing that needs disposing.
        TimeSpan wait = Options.Timeout == System.Threading.Timeout.InfiniteTimeSpan || Options.Timeout > MaxDisposeWait ? MaxDisposeWait : Options.Timeout;
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool drained = true;

        for (int i = 0; i < _maxConcurrentRequests; i++)
        {
            TimeSpan remaining = wait - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero || !_inFlight.Wait(remaining))
            {
                drained = false;
                break;
            }
        }

        if (!drained)
        {
            Logger.Warn($"Disposing the webhook alert sink for {GetLoggableUrl(Options.Url)} while requests are still in flight; they are canceled.");
        }

        _disposeCts.Cancel();

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
