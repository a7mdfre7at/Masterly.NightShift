using System.Collections.Concurrent;
using System.Diagnostics;
using Masterly.NightShift;

namespace Playground;

/// <summary>What the Playground page shows under "Event log": alerts, webhooks, exception handlers, telemetry.</summary>
public sealed class PlaygroundLog
{
    private const int MaxEntries = 400;
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private long _sequence;

    public static PlaygroundLog Instance { get; } = new();

    public void Add(string source, string text)
    {
        _entries.Enqueue(new LogEntry(Interlocked.Increment(ref _sequence), DateTime.UtcNow, source, text));
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<LogEntry> Since(long after) => [.. _entries.Where(x => x.Id > after)];

    public void Clear() => _entries.Clear();
}

public sealed record LogEntry(long Id, DateTime At, string Source, string Text);

/// <summary>Receives every alert NightShift raises: failed jobs, missed deadlines, open circuits, monitor incidents.</summary>
public sealed class EventLogAlertSink : IAlertSink
{
    public void Publish(Alert alert) => PlaygroundLog.Instance.Add("alert", $"{alert.Kind}: {alert.Title}. {alert.Message}");
}

/// <summary>Called on every failed attempt and every canceled run, on the server that ran the job.</summary>
public sealed class EventLogExceptionHandler : IJobExceptionHandler
{
    public void OnJobException(JobExceptionContext context) =>
        PlaygroundLog.Instance.Add("exception handler", $"Job #{context.BackgroundJob.Id} threw {context.Exception.GetType().Name}: {context.Exception.Message}");

    public void OnJobCanceled(JobExceptionContext context) =>
        PlaygroundLog.Instance.Add("exception handler", $"Job #{context.BackgroundJob.Id} was canceled ({context.Exception.GetType().Name})");
}

/// <summary>
/// Listens to the "Masterly.NightShift" ActivitySource, as an OpenTelemetry exporter would, while the
/// "Watch telemetry" experiment has turned it on.
/// </summary>
public sealed class TelemetryWatcher : IHostedService
{
    private ActivityListener? _listener;

    public static DateTime WatchUntil { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == NightShiftTelemetry.ActivitySourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = static activity =>
            {
                if (DateTime.UtcNow > WatchUntil) return;

                string outcome = activity.GetTagItem("nightshift.job.outcome") as string ?? "-";
                PlaygroundLog.Instance.Add("telemetry",
                    $"span '{activity.DisplayName}' ({activity.Kind}) {activity.Duration.TotalMilliseconds:0} ms, outcome {outcome}, trace {activity.TraceId.ToString()[..8]}");
            },
        };
        ActivitySource.AddActivityListener(_listener);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>What an experiment can use.</summary>
public sealed class PlaygroundContext(IServiceProvider services)
{
    public IServiceProvider Services { get; } = services;
    public IBackgroundJobClient Jobs { get; } = services.GetRequiredService<IBackgroundJobClient>();
    public IRecurringJobManager Recurring { get; } = services.GetRequiredService<IRecurringJobManager>();
    public JobStorage Storage { get; } = services.GetRequiredService<JobStorage>();
}

/// <summary>Values experiments share: the long job to cancel, toggles.</summary>
public static class PlaygroundState
{
    public static string? LongJobId { get; set; }
    public static string? SlowJobId { get; set; }
}

/// <summary>The daily-close monitor expects its job by this time of day (UTC): three minutes after the app starts.</summary>
public static class DailyCloseDeadline
{
    public static TimeSpan Value { get; } = TimeSpan.FromMinutes(Math.Floor((DateTime.UtcNow.TimeOfDay + TimeSpan.FromMinutes(3)).TotalMinutes) % (24 * 60));
}
