using System.Globalization;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Skips a run that starts too late: when the job starts more than <see cref="Window"/> after the moment it was
/// meant to run, it is deleted with the reason <c>Skipped: started ... late</c> instead of running. A report due
/// at 09:00 that only gets a worker at 11:30, after an outage, is usually better skipped than sent.
/// </summary>
/// <remarks>
/// <para>"Meant to run" is <see cref="PerformContext.ScheduledFor"/>: the time a delayed job was scheduled for, or
/// the occurrence of a recurring job. A continuation is measured from the moment its parents let it start, and any
/// other job, such as a fire-and-forget job, from its creation, so the attribute also skips a job that waited too
/// long in its queue. See <see cref="PerformContext.Lateness"/>.</para>
/// <para>The check is made once per job, just before the job method would start, and recorded in the
/// <c>LateStartChecked</c> job parameter. A job that passed it isn't checked again, so a retry runs however late it
/// is, and so does a job requeued by hand, from the dashboard or with <c>Requeue</c>, even after it was skipped. A job
/// that also has a <c>SkipAfter</c> window (<see cref="LateStartFilter"/>) is checked against the stricter of the two,
/// and the check is recorded once.</para>
/// <para>A skipped job isn't retried and doesn't count as a failure. Continuations waiting for the <c>Deleted</c>
/// state start, and telemetry reports the run as <c>skipped</c>.</para>
/// </remarks>
/// <example>
/// <code>
/// [SkipIfLate("00:30:00")]
/// public Task SendMorningReport() { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class SkipIfLateAttribute : JobFilterAttribute, IServerFilter
{
    /// <summary>The job parameter that records that the lateness of the job was checked.</summary>
    public const string CheckedParameter = "LateStartChecked";

    // PerformContext.Items entry with the window this run was already checked against
    private const string CheckedItem = "X_NS_LateStartChecked";

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(SkipIfLateAttribute));

    /// <summary>Skips runs that start more than <paramref name="window"/> late.</summary>
    /// <param name="window">
    /// The longest lateness allowed, as a time span such as <c>"00:30:00"</c> or <c>"1.00:00:00"</c>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="window"/> is <c>null</c>, empty, or not a time span.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> isn't positive.</exception>
    public SkipIfLateAttribute([NotNull] string window)
        : this(ParseWindow(window))
    {
    }

    /// <summary>Skips runs that start more than <paramref name="windowInSeconds"/> seconds late.</summary>
    /// <param name="windowInSeconds">The longest lateness allowed, in seconds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="windowInSeconds"/> isn't positive.</exception>
    public SkipIfLateAttribute(int windowInSeconds)
        : this(TimeSpan.FromSeconds(windowInSeconds))
    {
    }

    private SkipIfLateAttribute(TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "The window must be positive.");
        }

        Window = window;
    }

    /// <summary>Gets the longest lateness allowed.</summary>
    public TimeSpan Window { get; }

    /// <summary>
    /// The job parameter with the longest lateness allowed for a job, in seconds, as
    /// <see cref="RecurringJobOptions.SkipAfter"/> sets it on each run. <see cref="LateStartFilter"/> applies it.
    /// </summary>
    public const string WindowParameter = "SkipAfter";

    /// <inheritdoc />
    public void OnPerforming(PerformingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetJobParameter<bool>(CheckedParameter, allowStale: true))
        {
            return;
        }

        Check(context, Window);
    }

    /// <summary>
    /// Skips the run when it started more than <paramref name="window"/> late, and records the check. A run already
    /// checked against a window as strict or stricter isn't checked again; a stricter window is, without recording.
    /// </summary>
    internal static void Check(PerformContext context, TimeSpan window)
    {
        if (context.Items.TryGetValue(CheckedItem, out object? checkedWindow) && checkedWindow is TimeSpan previous)
        {
            if (window >= previous)
            {
                return;
            }
        }
        else
        {
            // Recorded before the job is skipped too, so a skipped job requeued by hand runs
            RecordCheck(context);
        }

        context.Items[CheckedItem] = window;

        TimeSpan lateness = context.Lateness;
        if (lateness > window)
        {
            throw new TerminateJobException(
                $"started {Format(lateness)} late, more than the {Format(window)} allowed",
                TerminateJobOutcome.Skipped);
        }
    }

    // Only a record: without it a retry is checked again, which a storage error shouldn't turn into a failure
    private static void RecordCheck(PerformContext context)
    {
        try
        {
            context.SetJobParameter(CheckedParameter, true);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException(
                $"Could not record that the lateness of background job '{context.BackgroundJob.Id}' was checked.",
                ex);
        }
    }

    /// <inheritdoc />
    public void OnPerformed(PerformedContext context)
    {
    }

    internal static string Format(TimeSpan value)
    {
        if (value.TotalSeconds < 60)
        {
            return $"{value.TotalSeconds:0} s";
        }

        if (value.TotalMinutes < 60)
        {
            return $"{value.TotalMinutes:0.#} min";
        }

        if (value.TotalHours < 48)
        {
            return $"{value.TotalHours:0.#} h";
        }

        return $"{value.TotalDays:0.#} days";
    }

    private static TimeSpan ParseWindow(string window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(window);

        return TimeSpan.TryParse(window, CultureInfo.InvariantCulture, out TimeSpan parsed)
            ? parsed
            : throw new ArgumentException($"'{window}' isn't a time span such as \"00:30:00\".", nameof(window));
    }
}

/// <summary>
/// Skips a run that starts later than its <c>SkipAfter</c> job parameter allows, as
/// <see cref="SkipIfLateAttribute"/> does: recurring jobs with <see cref="RecurringJobOptions.SkipAfter"/> set it on
/// every run. Added to
/// <see cref="GlobalJobFilters.Filters"/> by default.
/// </summary>
/// <remarks>
/// It reads the parameter from the parameters the worker loaded with the job, so a job without it costs a
/// dictionary lookup. A storage that doesn't load them with the job (<see cref="BackgroundJob.ParametersSnapshot"/>
/// is <c>null</c>) gets no check, rather than a storage read for every job.
/// </remarks>
public sealed class LateStartFilter : IServerFilter
{
    /// <inheritdoc />
    public void OnPerforming(PerformingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyDictionary<string, string>? parameters = context.BackgroundJob.ParametersSnapshot;
        if (parameters == null ||
            !parameters.TryGetValue(SkipIfLateAttribute.WindowParameter, out string? value) ||
            parameters.ContainsKey(SkipIfLateAttribute.CheckedParameter))
        {
            return;
        }

        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) || seconds <= 0)
        {
            return;
        }

        SkipIfLateAttribute.Check(context, TimeSpan.FromSeconds(seconds));
    }

    /// <inheritdoc />
    public void OnPerformed(PerformedContext context)
    {
    }
}
