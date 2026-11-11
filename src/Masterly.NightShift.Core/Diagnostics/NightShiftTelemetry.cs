using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Masterly.NightShift;

/// <summary>
/// Names of the <see cref="ActivitySource"/> and <see cref="Meter"/> that Masterly.NightShift uses for
/// traces and metrics after <see cref="GlobalConfigurationExtensions.UseTelemetry"/> is called.
/// With OpenTelemetry, subscribe with <c>AddSource(NightShiftTelemetry.ActivitySourceName)</c>
/// and <c>AddMeter(NightShiftTelemetry.MeterName)</c>.
/// </summary>
public static class NightShiftTelemetry
{
    /// <summary>
    /// The name of the <see cref="ActivitySource"/> that creates "create" (producer) spans
    /// when a background job is created, and "process" (consumer) spans when it is performed.
    /// </summary>
    public const string ActivitySourceName = "Masterly.NightShift";

    /// <summary>
    /// The name of the <see cref="Meter"/> that reports the <c>nightshift.job.created</c>,
    /// <c>nightshift.job.duration</c> and <c>nightshift.job.active</c> instruments.
    /// </summary>
    public const string MeterName = "Masterly.NightShift";

    internal static readonly ActivitySource ActivitySource = new(
        ActivitySourceName,
        typeof(NightShiftTelemetry).Assembly.GetName().Version?.ToString());

    internal static readonly Meter Meter = new(
        MeterName,
        typeof(NightShiftTelemetry).Assembly.GetName().Version?.ToString());

    internal static readonly Counter<long> JobsCreated = Meter.CreateCounter<long>(
        "nightshift.job.created",
        unit: "{job}",
        description: "Number of background jobs created.");

    internal static readonly Histogram<double> JobDuration = CreateJobDurationHistogram();

    internal static readonly Counter<long> JobsCompleted = Meter.CreateCounter<long>(
        "nightshift.job.completed",
        unit: "{job}",
        description: "Number of background job executions that ended, by outcome: succeeded, failed, skipped, canceled or retry_later.");

    internal static readonly Counter<long> JobsRetried = Meter.CreateCounter<long>(
        "nightshift.job.retried",
        unit: "{job}",
        description: "Number of failed background job attempts scheduled for a retry.");

    internal static readonly Counter<long> JobsStaleRestarted = Meter.CreateCounter<long>(
        "nightshift.job.stale_restarts",
        unit: "{job}",
        description: "Number of background jobs picked up again after their server stopped while they ran.");

    internal static readonly Histogram<double> JobLateness = Meter.CreateHistogram<double>(
        "nightshift.job.lateness",
        unit: "s",
        description: "How late delayed and recurring background jobs started after the moment they were scheduled for.");

    internal static readonly UpDownCounter<long> ActiveJobs = Meter.CreateUpDownCounter<long>(
        "nightshift.job.active",
        unit: "{job}",
        description: "Number of background jobs being performed right now.");

    private static Histogram<double> CreateJobDurationHistogram()
    {
        const string name = "nightshift.job.duration";
        const string unit = "s";
        const string description = "Duration of background job executions, including server filters.";

#if NET9_0_OR_GREATER
        return Meter.CreateHistogram(name, unit, description, tags: null, advice: new InstrumentAdvice<double>
        {
            // Background jobs run from milliseconds to hours, the default boundaries are too short
            HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 900, 3600]
        });
#else
        return Meter.CreateHistogram<double>(name, unit, description);
#endif
    }
}
