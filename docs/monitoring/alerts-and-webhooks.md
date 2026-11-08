# Alerts and webhooks

Masterly.NightShift raises an alert when a job finally fails, when a job finishes after its deadline, and when a circuit breaker opens. Alerts go to the sinks you register: the built-in `WebhookAlertSink` posts them to an HTTP endpoint (your own API, Slack, Mattermost, Discord), and you can write your own sink for anything else.

## Quick example

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift"))
    .UseWebhookAlerts("https://alerts.example.com/nightshift"));

builder.Services.AddNightShiftServer();
```

Every alert is now sent as a JSON `POST` to that URL. Without DI, call the same method on `GlobalConfiguration.Configuration`.

## How it works

`Alerts` is a process-wide bus. Built-in filters call `Alerts.Raise(alert)`, and the bus hands the alert to every registered `IAlertSink`. When no sink is registered, raising costs nothing: the filters check `Alerts.HasSinks` first.

Three places raise alerts:

| Kind | Raised by | When |
|---|---|---|
| `JobFailed` | `AlertsFilter` (a global apply-state filter) | A job has entered the Failed state and the change is committed. With the default `AutomaticRetry` (10 retries) that is after the last retry failed, not on every failed attempt. |
| `DeadlineMissed` | `[Deadline]` | A job with a deadline is about to enter the Succeeded state after its deadline has passed. |
| `CircuitOpened` | `[CircuitBreaker]` | A circuit opens: the failure threshold is reached within the window, or the half-open probe job failed. |

`UseWebhookAlerts(...)` and `UseAlerts(...)` register the sink and add `AlertsFilter` to the global filters (only once, however many sinks you add). `[Deadline]` and `[CircuitBreaker]` raise their alerts directly, so they work with any registered sink.

Alerts are raised synchronously, on the thread that changes the job's state (usually a worker), after the job's lock is released. `JobFailed` and `DeadlineMissed`, and `CircuitOpened` for a failed probe, wait until the state change is committed to storage, so a state change that fails and is retried sends one alert, and no alert goes out for a change that never happened. `CircuitOpened` for a reached threshold describes the circuit's own state, which is stored in a transaction of its own, so it is sent after the job's state change is attempted, even when that attempt fails. `Alerts.Raise` catches and logs an exception thrown by a sink (as a warning, in the `Masterly.NightShift.Alert` log category) and moves on to the next sink.

`WebhookAlertSink` does not block that thread: `Publish` checks the event filter, takes a slot from its in-flight limit, and sends the request on the thread pool. When all slots are busy, the alert is dropped with a warning. Each request gets `Timeout` to finish; after that it is canceled and counted as failed.

## Options / API

### Alert kinds

`AlertKind` is a `[Flags]` enum, so you can combine values when choosing which alerts a webhook receives.

| Value | Number | Meaning |
|---|---|---|
| `None` | 0 | No alert. |
| `JobFailed` | 1 | A job reached the Failed state. |
| `DeadlineMissed` | 2 | A job with a `[Deadline]` finished after its deadline. |
| `CircuitOpened` | 4 | A `[CircuitBreaker]` circuit opened. |
| `All` | 7 | Every kind. |

### `Alert`

| Property | Type | Meaning |
|---|---|---|
| `Kind` | `AlertKind` | What the alert is about. |
| `Title` | `string` | A one-line title, for example `Job 42 failed: EmailService.SendWelcome`. |
| `Message` | `string` | The details, for example `SmtpException: Mailbox unavailable`. |
| `OccurredAt` | `DateTime` | When it happened, UTC. Set by the constructor; settable. |
| `JobId` | `string?` | The job it is about. Set by all three built-in kinds. |
| `Method` | `string?` | `Type.Method` of the job. Set by `JobFailed` and `DeadlineMissed`. |
| `Resource` | `string?` | The circuit breaker resource. Set by `CircuitOpened`. |
| `ServerId` | `string?` | The server that reported it. Set by `JobFailed`. |

The constructor is `new Alert(AlertKind kind, string title, string message)`.

The built-in alerts look like this:

| Kind | Title | Message |
|---|---|---|
| `JobFailed` | `Job {id} failed: {Type}.{Method}` | `{ExceptionType}: {exception message}` |
| `DeadlineMissed` | `Job {id} missed its deadline: {Type}.{Method}` | `Finished {n} seconds after the deadline of {timeout}.` |
| `CircuitOpened` | `Circuit '{resource}' is open` | `{n} failure(s) within {window} seconds; jobs are held for {open} seconds.` |

### Configuration methods

| Method | Meaning |
|---|---|
| `UseWebhookAlerts(string url, Action<WebhookAlertOptions>? configure = null)` | Creates a `WebhookAlertSink` with `Url = url`, applies `configure`, and registers it with `UseAlerts`. |
| `UseAlerts(IAlertSink sink)` | Registers the sink with `Alerts.AddSink` and adds `AlertsFilter` to `GlobalJobFilters` if it is not there yet. |

### `WebhookAlertOptions`

| Property | Type | Default | Meaning |
|---|---|---|---|
| `Url` | `string?` | set by `UseWebhookAlerts` | The URL that receives one `POST` per alert. Required: the sink's constructor throws if it is empty. |
| `Events` | `AlertKind` | `AlertKind.All` | Which alerts are sent. Others are ignored by this sink. |
| `Format` | `WebhookFormat` | `WebhookFormat.Json` | The body format. |
| `Timeout` | `TimeSpan` | 10 seconds | How long one request may take. It is applied to each request with a cancellation token, so an `HttpClient` you pass to the sink keeps its own settings. `Timeout.InfiniteTimeSpan` turns it off. Must be positive: the sink's constructor throws otherwise. |
| `Headers` | `Dictionary<string, string>` | empty | Extra request headers, for example an authorization token. Read-only property; add entries to it. Keys are case-insensitive. |
| `MaxConcurrentRequests` | `int` | 16 | How many requests `Publish` may have in flight at once. Further alerts are dropped with a warning. `SendAsync` is not limited. Must be positive. |

### `WebhookFormat`

| Value | Body |
|---|---|
| `Json` | The alert as camelCase JSON (below). |
| `Slack` | A `{"text": "..."}` message, which Slack and Mattermost incoming webhooks accept. `&`, `<` and `>` are escaped. |
| `Discord` | A `{"content": "..."}` message for a Discord webhook, with mentions turned off. |
| `Teams` | A Microsoft Teams message with an Adaptive Card, for a Teams workflow webhook. Markdown characters are escaped. |

The request always has `Content-Type: application/json; charset=utf-8`.

**JSON format.** Property names are camelCase, `kind` is a camelCase string, `occurredAt` is UTC with a trailing `Z`, and properties that are `null` are left out. A failed job:

```json
{
  "source": "nightshift",
  "kind": "jobFailed",
  "title": "Job 42 failed: EmailService.SendWelcome",
  "message": "SmtpException: Mailbox unavailable",
  "occurredAt": "2026-11-06T09:41:27.5812345Z",
  "jobId": "42",
  "method": "EmailService.SendWelcome",
  "serverId": "web-01:4120:3f2b8c1e-6a2d-4c5e-9f0a-1b2c3d4e5f60"
}
```

An opened circuit (no `method` or `serverId`, but a `resource`):

```json
{
  "source": "nightshift",
  "kind": "circuitOpened",
  "title": "Circuit 'payment-gateway' is open",
  "message": "5 failure(s) within 60 seconds; jobs are held for 300 seconds.",
  "occurredAt": "2026-11-06T09:45:02.1043210Z",
  "jobId": "57",
  "resource": "payment-gateway"
}
```

The real body is written on one line; it is indented here for reading.

**Slack format.** The title in bold, the message, then `Job:`, `Resource:` and `Server:` lines for the values that are set:

```json
{"text":"*Job 42 failed: EmailService.SendWelcome*\nSmtpException: Mailbox unavailable\nJob: 42\nServer: web-01:4120:3f2b8c1e-6a2d-4c5e-9f0a-1b2c3d4e5f60"}
```

Slack reads `<...>` as a link or a mention and `&...;` as an entity. The title, message and values come from your jobs, often from exception messages, so `&`, `<` and `>` are written as `&amp;`, `&lt;` and `&gt;`, as Slack asks. An exception message that contains `<!channel>` or `<https://example.com|click here>` shows as plain text: it can't ping the channel or hide a link.

**Discord format.** Discord's own webhooks read `content`, not `text`. The title in bold (`**...**`), then the same lines as the Slack format, cut to Discord's limit of 2000 characters. `allowed_mentions` with an empty `parse` list stops `@everyone`, `@here` and user or role mentions in the text from pinging anybody:

```json
{"content":"**Job 42 failed: EmailService.SendWelcome**\nSmtpException: Mailbox unavailable\nJob: 42","allowed_mentions":{"parse":[]}}
```

Use it with the webhook URL Discord gives you (`https://discord.com/api/webhooks/{id}/{token}`). The Slack format also works with Discord when you add `/slack` to the end of that URL, but then mentions are not turned off.

Discord renders Markdown in webhook messages: masked links (`[text](https://example.com)`), emphasis, spoilers, code blocks, quotes and headings. The title, message and values come from your jobs, often from exception messages, so `\`, `*`, `_`, `~`, `` ` ``, `|`, `>`, `[`, `]`, `(`, `)` and `#` in them are escaped with a backslash. An exception message that contains `[click here](https://example.com)` or starts with `# ` shows as plain text: it can't hide a link or add formatting.

**Teams format.** A message with one Adaptive Card (version 1.4): the title in bold, the message, and a fact list with the kind, the job, method, resource and server when they are set, and the time in UTC. This is the body that the webhook of a Teams workflow ("Post to a channel when a webhook request is received") takes:

```json
{"type":"message","attachments":[{"contentType":"application/vnd.microsoft.card.adaptive","contentUrl":null,"content":{"$schema":"http://adaptivecards.io/schemas/adaptive-card.json","type":"AdaptiveCard","version":"1.4","body":[{"type":"TextBlock","text":"Job 42 failed: EmailService.SendWelcome","weight":"Bolder","size":"Medium","wrap":true},{"type":"TextBlock","text":"SmtpException: Mailbox unavailable","wrap":true},{"type":"FactSet","facts":[{"title":"Kind","value":"JobFailed"},{"title":"Job","value":"42"},{"title":"Method","value":"EmailService.SendWelcome"},{"title":"When","value":"2026-11-06 09:41:27Z"}]}]}}]}
```

Teams reads Markdown in card text, so `\`, `` ` ``, `*`, `_`, `[`, `]`, `<`, `>` and `~` in the title, message and values are escaped with a backslash: text from an exception can't add a link or formatting. Each text is cut to 4000 characters.

```csharp
config.UseWebhookAlerts(teamsWorkflowUrl, options => options.Format = WebhookFormat.Teams);
```

### `WebhookAlertSink`

| Member | Meaning |
|---|---|
| `WebhookAlertSink(WebhookAlertOptions options)` | Creates the sink with its own `HttpClient`. |
| `WebhookAlertSink(WebhookAlertOptions options, HttpClient? httpClient)` | Uses the given client as it is: its settings, `Timeout` included, are not changed, and it may already have sent requests. Useful in tests with a stub handler, or to share a configured client. |
| `Options` | The options. |
| `Publish(Alert alert)` | Filters by `Events`, takes one of the `MaxConcurrentRequests` slots, then sends in the background. Never blocks on the request. |
| `SendAsync(Alert alert)` | Sends one alert and waits for the answer. Returns `true` for a success status code. Does not filter by `Events` and does not use a `MaxConcurrentRequests` slot. Never throws for HTTP or network errors or a timeout; they are logged as warnings. |
| `CreateBody(Alert alert)` | Returns the request body for an alert, in the configured format. |
| `Sent` | Alerts delivered with a success status code. |
| `Failed` | Alerts whose request threw, timed out or returned a non-success status code. |
| `Dropped` | Alerts dropped because `MaxConcurrentRequests` requests were in flight. |
| `Dispose()` | Stops sending: later `Publish` calls are ignored. Waits for the requests `Publish` started to finish, at most `Timeout` and never more than 30 seconds (`WebhookAlertSink.MaxDisposeWait`), also with `Timeout.InfiniteTimeSpan`. Then it cancels every request still running, those of `SendAsync` too, whoever owns the client; each canceled request is counted in `Failed` and logged as a warning. Last, it disposes the `HttpClient` the sink created; a client you passed in is yours to dispose. |

There is no retry: a request that fails is counted in `Failed`, logged, and forgotten.

### `Alerts`

| Member | Meaning |
|---|---|
| `Alerts.AddSink(IAlertSink sink)` | Registers a sink. Does not add `AlertsFilter`; prefer `UseAlerts`. |
| `Alerts.Raise(Alert alert)` | Delivers an alert to every sink. A sink that throws is logged and skipped. |
| `Alerts.Clear()` | Removes every sink. |
| `Alerts.HasSinks` | Whether at least one sink is registered. |
| `Alerts.Sinks` | The registered sinks. |

### `IAlertSink`

```csharp
public interface IAlertSink
{
    void Publish(Alert alert);
}
```

Implementations must not throw and should return quickly. They run on the thread that changes the job's state, usually a worker, after the state change and once the job's lock is released, so a sink can change the same job; but the worker waits for every sink before it takes its next job.

## More examples

### Slack, only for failures and circuits

```csharp
config.UseWebhookAlerts(slackWebhookUrl, options =>
{
    options.Format = WebhookFormat.Slack;
    options.Events = AlertKind.JobFailed | AlertKind.CircuitOpened;
});
```

### An authenticated endpoint

```csharp
config.UseWebhookAlerts("https://ops.example.com/api/alerts", options =>
{
    options.Headers["Authorization"] = $"Bearer {token}";
    options.Timeout = TimeSpan.FromSeconds(5);
    options.MaxConcurrentRequests = 4;
});
```

### Two destinations

Each call adds another sink. Here everything goes to the operations API and only failures go to Slack:

```csharp
config
    .UseWebhookAlerts("https://ops.example.com/api/alerts")
    .UseWebhookAlerts(slackWebhookUrl, options =>
    {
        options.Format = WebhookFormat.Slack;
        options.Events = AlertKind.JobFailed;
    });
```

### Deadline and circuit breaker alerts

These alerts need the attributes on the job. See [Timing](../filters/timing.md) and [Retries](../filters/retries.md) for what the attributes do.

```csharp
public class PaymentService
{
    [Deadline("00:15:00")]
    public void CapturePayment(int orderId) { }

    [CircuitBreaker("payment-gateway", failureThreshold: 5, windowSeconds: 60, openSeconds: 300)]
    public void RefundPayment(int orderId) { }
}
```

### A custom sink that writes to the application log

Register the sink in DI and pass it to `UseAlerts` from the `AddNightShift` overload that receives the service provider:

```csharp
using Masterly.NightShift;
using Microsoft.Extensions.Logging;

public sealed class LoggingAlertSink : IAlertSink
{
    private readonly ILogger<LoggingAlertSink> _logger;

    public LoggingAlertSink(ILogger<LoggingAlertSink> logger)
    {
        _logger = logger;
    }

    public void Publish(Alert alert)
    {
        _logger.LogError(
            "NightShift alert {AlertKind}: {Title}. {Message} (job {JobId}, server {ServerId})",
            alert.Kind, alert.Title, alert.Message, alert.JobId, alert.ServerId);
    }
}
```

```csharp
builder.Services.AddSingleton<LoggingAlertSink>();

builder.Services.AddNightShift((provider, config) => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift"))
    .UseAlerts(provider.GetRequiredService<LoggingAlertSink>()));
```

### A custom sink that delivers in the background

When delivery is slow (e-mail, a chat API with its own message format, a paging service), let the sink only queue the alert and have a hosted service send it. This keeps the job's state change fast. This example posts to a Microsoft Teams webhook with its own message; `WebhookFormat.Teams` already sends an Adaptive Card without blocking the worker, so you need this only to send something else:

```csharp
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Masterly.NightShift;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class QueuedAlertSink : IAlertSink
{
    private readonly Channel<Alert> _channel = Channel.CreateBounded<Alert>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite });

    public ChannelReader<Alert> Reader => _channel.Reader;

    public void Publish(Alert alert)
    {
        _channel.Writer.TryWrite(alert);
    }
}

public sealed class TeamsAlertSender : BackgroundService
{
    private readonly QueuedAlertSink _sink;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TeamsAlertSender> _logger;

    public TeamsAlertSender(QueuedAlertSink sink, IHttpClientFactory httpClientFactory, ILogger<TeamsAlertSender> logger)
    {
        _sink = sink;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var alert in _sink.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var body = JsonSerializer.Serialize(new { title = alert.Title, text = alert.Message });
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _httpClientFactory.CreateClient("teams")
                    .PostAsync("https://example.webhook.office.com/webhookb2/...", content, stoppingToken);

                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not deliver alert {Title}", alert.Title);
            }
        }
    }
}
```

```csharp
builder.Services.AddHttpClient("teams");
builder.Services.AddSingleton<QueuedAlertSink>();
builder.Services.AddHostedService<TeamsAlertSender>();

builder.Services.AddNightShift((provider, config) => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift"))
    .UseAlerts(provider.GetRequiredService<QueuedAlertSink>()));

builder.Services.AddNightShiftServer();
```

### Raising an alert from your own code

`Alerts.Raise` is public, so a job can report a problem that is not an exception. There is no "custom" kind: pick the closest existing one, because sinks filter on it (`WebhookAlertSink` ignores an alert whose kind is not in its `Events`, so `AlertKind.None` is never sent).

```csharp
public class ImportService
{
    public void ImportCatalog(string feedUrl)
    {
        var rejected = 0;
        // ... import ...

        if (rejected > 100 && Alerts.HasSinks)
        {
            Alerts.Raise(new Alert(
                AlertKind.JobFailed,
                "Catalog import rejected too many rows",
                $"{rejected} rows from {feedUrl} were rejected."));
        }
    }
}
```

### Testing the webhook during deployment

`SendAsync` sends one alert and tells you whether it was accepted, and `CreateBody` shows what would be sent:

```csharp
using var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "https://alerts.example.com/nightshift" });

Console.WriteLine(sink.CreateBody(new Alert(AlertKind.JobFailed, "Test alert", "Sent from the deployment checklist.")));
bool delivered = await sink.SendAsync(new Alert(AlertKind.JobFailed, "Test alert", "Sent from the deployment checklist."));
```

## Things to know

- **Configure alerts in the processes that run jobs.** Alerts are raised where the state change happens, which for `JobFailed`, `DeadlineMissed` and `CircuitOpened` is the server that performs the job. A web app that only enqueues jobs raises nothing. Sinks are static per process, so in a cluster each server sends the alerts for the jobs it processed, and each alert is sent once.
- **`JobFailed` means the Failed state.** A job whose retries are exhausted with `[AutomaticRetry(OnAttemptsExceeded = AttemptsExceededAction.Delete)]` is deleted, not failed, and raises no alert. Failed attempts that will be retried raise nothing either.
- **`DeadlineMissed` is raised only when a late job succeeds.** A job that fails after its deadline raises `JobFailed` (if it reaches Failed); a job that is still waiting or running past its deadline raises nothing until it finishes. The dashboard highlights those jobs instead.
- **Alerts are sent after the commit.** A worker retries a state change whose commit failed, and each alert is sent once, after the commit that worked. This holds for state changes made by workers, `ChangeState`, the dashboard, job creation (a job created directly in the Failed state) and the recurring job scheduler. Only a state change applied with a transaction you pass in yourself (a `StateChangeContext` created with a transaction) raises its alerts right away, before you commit. A `CircuitOpened` alert is the exception the other way: the open state is stored in a transaction of its own, so the alert is sent once the job's state change is over even when its commit failed (a retry finds the circuit open and doesn't alert again).
- **A job failed by the worker's last-resort fallback raises no alert.** When every attempt to change a job's state throws, the worker moves the job to Failed with all filters skipped, `AlertsFilter` included. The job still shows on the dashboard's Failed page, and the worker logs the error.
- **No grouping or rate limiting.** A hundred failing jobs send a hundred alerts. `MaxConcurrentRequests` caps the number of requests in flight; the rest are dropped and counted in `Dropped`. Write a custom sink if you need batching or de-duplication.
- **Logs show only the start of the webhook URL.** Warnings about rejected, failed or timed-out requests write the URL as `https://hooks.slack.com/... (hash 3fa2c1)`: scheme, host and port, with the path, query and any user name and password left out, because Slack, Mattermost and Discord put the webhook's secret token in the path. The hash is the first six hex digits of the SHA-256 of the whole URL (UTF-8), so you can tell two webhooks on the same host apart. `HttpClient` instrumentation that you add yourself (for example OpenTelemetry's) can still record the full URL. Where the receiver supports it, an `Authorization` header is still the better place for a secret.
- **Calling `UseWebhookAlerts` twice adds two sinks.** Registration is not idempotent, for example if the configuration code runs twice in tests. `Alerts.Clear()` removes all sinks.
- Alerts work on every target framework, including .NET Framework.

## See also

- [Monitoring overview](README.md)
- [Retries](../filters/retries.md): `AutomaticRetry`, `CircuitBreaker`
- [Timing](../filters/timing.md): `Deadline`
- [Custom filters](../filters/custom-filters.md): raise alerts from your own filter
- [Logging](logging.md)
- [Dependency injection](../server/dependency-injection.md)
