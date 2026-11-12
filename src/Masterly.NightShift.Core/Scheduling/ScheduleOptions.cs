namespace Masterly.NightShift;

/// <summary>
/// What happens to a job whose start time its <see cref="SchedulePolicy"/> blocks
/// (<see cref="ScheduleOptions.WhenBlocked"/>).
/// </summary>
/// <remarks>
/// Only a time the policy blocks counts. A policy that can't be evaluated (it can't be read, its time zone or a date
/// provider is unknown on this server, a date provider throws) blocks nothing: the job is created whatever the
/// value, and waits until the policy can be evaluated (see <see cref="SchedulePolicyFilter"/>).
/// </remarks>
public enum WhenBlocked
{
    /// <summary>The job is moved to the next instant the policy allows.</summary>
    MoveForward = 0,

    /// <summary>The job is deleted with the reason <c>Skipped: ...</c>, without running.</summary>
    Skip = 1,

    /// <summary>
    /// The job isn't created: <c>Enqueue</c> and <c>Schedule</c> return <c>null</c>. When the policy can't be
    /// evaluated, the job is created and they return its id: the policy decides when the job is due, and skips it
    /// then if it blocks that time.
    /// </summary>
    Reject = 2,
}

/// <summary>
/// Options of a fire-and-forget or delayed job: the business calendar it follows, what happens when the calendar
/// blocks its start time, how late it may start and its queue.
/// <code>
/// BackgroundJob.Schedule&lt;Mailer&gt;(x => x.SendReminder(orderId), TimeSpan.FromHours(4), new ScheduleOptions
/// {
///     Policy = "business-hours",
///     WhenBlocked = WhenBlocked.MoveForward,
///     SkipAfter = TimeSpan.FromDays(1),
/// });
/// </code>
/// </summary>
public sealed class ScheduleOptions
{
    /// <summary>The <c>Policy</c> job parameter: the name of the policy the job follows.</summary>
    public const string PolicyParameter = "Policy";

    /// <summary>The <c>WhenBlocked</c> job parameter: what happens when the policy blocks the job.</summary>
    public const string WhenBlockedParameter = "WhenBlocked";

    /// <summary>
    /// Gets or sets the name of the stored <see cref="SchedulePolicy"/> the job follows, or <c>null</c>. The policy
    /// is checked when the job is created, for the moment it is to run, and again when a delayed job is due, as the
    /// policy may have changed. Enqueuing a job by hand from the dashboard bypasses it.
    /// </summary>
    [CanBeNull]
    public string? Policy
    {
        get;
        set => field = value == null ? null : SchedulePolicy.NormalizeName(value, nameof(value));
    }

    /// <summary>
    /// Gets or sets what happens when <see cref="Policy"/> blocks the job's start time. Moves it forward by default.
    /// Setting it without <see cref="Policy"/> is a mistake: creating the job then throws an
    /// <see cref="ArgumentException"/>.
    /// </summary>
    /// <remarks>
    /// It applies to a time the policy blocks, not to a policy that can't be evaluated, for example because a date
    /// provider is down: such a job is never skipped or rejected. It is created, so with
    /// <see cref="NightShift.WhenBlocked.Reject"/> too <c>Enqueue</c> and <c>Schedule</c> return an id; a job that
    /// would run now waits in the Scheduled state and is checked again after 1, 5 and 15 minutes, then every hour,
    /// and a delayed job keeps its time and is checked when it is due.
    /// </remarks>
    public WhenBlocked WhenBlocked
    {
        get;
        set
        {
            field = Enum.IsDefined(value)
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a WhenBlocked value.");
            _whenBlockedSet = true;
        }
    }

    // Whether WhenBlocked was set, so it isn't silently ignored without a Policy
    private bool _whenBlockedSet;

    /// <summary>
    /// Gets or sets how late the job may start, at least one second, or <c>null</c>: a job that starts later than
    /// this after the moment it was scheduled for is skipped (deleted with the reason <c>Skipped: ...</c>), as
    /// <see cref="SkipIfLateAttribute"/> does. Needs a storage that loads the job parameters with the job, as SQL
    /// Server does.
    /// </summary>
    public TimeSpan? SkipAfter
    {
        get;
        set => field = value is { } skipAfter && skipAfter < TimeSpan.FromSeconds(1)
            ? throw new ArgumentOutOfRangeException(nameof(value), value, "SkipAfter must be at least one second.")
            : value;
    }

    /// <summary>
    /// Gets or sets the queue of the job, or <c>null</c> for the queue its method's attributes give. Kept with the
    /// job, so it needs a storage with <see cref="Storage.JobStorageFeatures.JobQueueProperty"/>.
    /// </summary>
    [CanBeNull]
    public string? Queue
    {
        get;
        set
        {
            if (value != null) EnqueuedState.ValidateQueueName(nameof(value), value);
            field = value;
        }
    }

    /// <summary>The job parameters the options stand for.</summary>
    /// <exception cref="ArgumentException"><see cref="WhenBlocked"/> is set without <see cref="Policy"/>.</exception>
    internal Dictionary<string, object> ToParameters()
    {
        if (_whenBlockedSet && Policy == null)
        {
            throw new ArgumentException(
                "ScheduleOptions.WhenBlocked has no effect without a Policy: set Policy too, or leave WhenBlocked unset.");
        }

        Dictionary<string, object> parameters = new(StringComparer.Ordinal);

        if (Policy != null)
        {
            parameters[PolicyParameter] = Policy;
            parameters[WhenBlockedParameter] = WhenBlocked.ToString();
        }

        if (SkipAfter.HasValue)
        {
            parameters[SkipIfLateAttribute.WindowParameter] = (long)SkipAfter.Value.TotalSeconds;
        }

        return parameters;
    }
}
