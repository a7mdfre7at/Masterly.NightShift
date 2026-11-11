using System.Globalization;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Skips a run that starts too late: when the job starts more than <see cref="Window"/> after the moment it was
/// meant to run, it is deleted with the reason <c>Skipped: started ... late</c> instead of running. A report due
/// at 09:00 that only gets a worker at 11:30, after an outage, is usually better skipped than sent.
/// </summary>
/// <remarks>
/// <para>"Meant to run" is <see cref="PerformContext.ScheduledFor"/>: the time a delayed job was scheduled for, or
/// the occurrence of a recurring job. A job without it, such as a fire-and-forget job, is measured from its
/// creation, so the attribute also skips a job that waited too long in its queue. See
/// <see cref="PerformContext.Lateness"/>.</para>
/// <para>The check is made once per job, just before the job method would start, and recorded in the
/// <c>LateStartChecked</c> job parameter. A job that passed it isn't checked again, so a retry runs however late it
/// is, and so does a job requeued by hand, from the dashboard or with <c>Requeue</c>, even after it was skipped.</para>
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

    /// <summary>Skips runs that start more than <paramref name="window"/> late.</summary>
    /// <param name="window">The longest lateness allowed, as a time span such as <c>"00:30:00"</c> or <c>"1.00:00:00"</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="window"/> is <c>null</c>, empty, or not a time span.</exception>
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

    /// <inheritdoc />
    public void OnPerforming(PerformingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetJobParameter<bool>(CheckedParameter, allowStale: true)) return;

        TimeSpan lateness = context.Lateness;

        // Recorded before the job is skipped too, so a skipped job requeued by hand runs
        context.SetJobParameter(CheckedParameter, true);

        if (lateness > Window)
        {
            throw new TerminateJobException(
                $"started {Format(lateness)} late, more than the {Format(Window)} allowed",
                TerminateJobOutcome.Skipped);
        }
    }

    /// <inheritdoc />
    public void OnPerformed(PerformedContext context)
    {
    }

    internal static string Format(TimeSpan value)
    {
        if (value.TotalSeconds < 60) return $"{value.TotalSeconds:0} s";
        if (value.TotalMinutes < 60) return $"{value.TotalMinutes:0.#} min";
        if (value.TotalHours < 48) return $"{value.TotalHours:0.#} h";
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
