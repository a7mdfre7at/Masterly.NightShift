using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Masterly.NightShift.Client;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Applies the <see cref="SchedulePolicy"/> a fire-and-forget or delayed job was created with
/// (<see cref="ScheduleOptions.Policy"/>, the <c>Policy</c> job parameter). A default global filter: jobs without
/// the parameter cost a dictionary lookup.
/// </summary>
/// <remarks>
/// The policy is checked when the job is created, for the moment it is to run, and again when the delayed job
/// scheduler enqueues it, because the policy may have changed meanwhile. A blocked job is moved forward to the next
/// instant the policy allows, deleted as skipped, or, at creation, not created, as
/// <see cref="ScheduleOptions.WhenBlocked"/> says; a job already created whose due time is blocked is skipped
/// instead of rejected. A job enqueued by hand (the dashboard's Enqueue now, <c>Requeue</c>) isn't checked, nor a
/// job waiting in its queue. A policy deleted after the job was created no longer applies to it. At creation, a
/// policy is read from the storage at most once every few seconds (<see cref="CalendarCacheDuration"/>), so a changed
/// policy applies to new jobs within that time; when a job comes due, its policy is always read afresh.
/// </remarks>
public sealed class SchedulePolicyFilter : IClientFilter, IElectStateFilter, IApplyStateFilter
{
    private const string SkippedPrefix = "Skipped";
    private const string MovedPrefix = "Moved by policy";

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(SchedulePolicyFilter));

    // The policies read recently, per storage, so creating many jobs that follow one policy doesn't read it each time
    private static readonly ConditionalWeakTable<JobStorage, ConcurrentDictionary<string, CachedCalendar>> Calendars = [];

    // The Scheduled states this filter moved due jobs to: once one is the state applied, its time is the run's
    private static readonly ConditionalWeakTable<ScheduledState, object> MovedStates = [];
    private static readonly object Moved = new();

    /// <summary>How long a policy read from the storage is used before it is read again. Replaceable for tests.</summary>
    internal static TimeSpan CalendarCacheDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public void OnCreating(CreatingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.InitialState is not (EnqueuedState or ScheduledState)) return;
        if (!context.Parameters.TryGetValue(ScheduleOptions.PolicyParameter, out object? value) || value is not string name) return;

        name = SchedulePolicy.NormalizeName(name, nameof(context));
        SchedulePolicyCalendar calendar = LoadCalendar(context.Storage, context.Connection, name, allowCached: true)
            ?? throw new InvalidOperationException($"There is no schedule policy named '{name}'. Save it with SaveSchedulePolicy before creating jobs that follow it.");

        DateTime runAt = context.InitialState is ScheduledState scheduled ? scheduled.EnqueueAt : JobClock.UtcNow;
        SchedulePolicyDecision decision = calendar.Evaluate(runAt);
        if (decision.Allowed) return;

        switch (ReadWhenBlocked(context.Parameters.TryGetValue(ScheduleOptions.WhenBlockedParameter, out object? whenBlocked) ? whenBlocked as string : null))
        {
            case WhenBlocked.Reject:
                Logger.Debug($"A job of {context.Job.Type.Name}.{context.Job.Method.Name} wasn't created: {decision.Reason}");
                context.Canceled = true;
                break;

            case WhenBlocked.Skip:
                context.ReplaceInitialState(new DeletedState { Reason = TerminateJobFilter.FormatReason(SkippedPrefix, decision.Reason!) }); // Not allowed, so explained
                break;

            default:
                DateTime next = calendar.FindNextAllowed(runAt)
                    ?? throw new InvalidOperationException($"Schedule policy '{name}' allows no time in the ten years after {runAt:yyyy-MM-dd HH:mm} UTC.");

                // A queue given only by the Enqueued state comes back when the job is enqueued at the new time
                if (context.InitialState is EnqueuedState enqueued && context.Job.Queue == null &&
                    !EnqueuedState.DefaultQueue.Equals(enqueued.Queue, StringComparison.OrdinalIgnoreCase))
                {
                    context.SetJobParameter(DrainModeFilter.HeldQueueParameter, enqueued.Queue);
                }

                context.ReplaceInitialState(new ScheduledState(next) { Reason = TerminateJobFilter.FormatReason(MovedPrefix, decision.Reason!) }); // Not allowed, so explained
                break;
        }
    }

    /// <inheritdoc />
    public void OnCreated(CreatedContext context)
    {
    }

    /// <inheritdoc />
    public void OnStateElection(ElectStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Only the delayed job scheduler's enqueue of a due job; a job enqueued by hand runs
        if (context.CandidateState is not EnqueuedState { IsDue: true }) return;

        string? name = ReadParameter(context, ScheduleOptions.PolicyParameter);
        if (string.IsNullOrWhiteSpace(name)) return;

        // Read afresh: this check exists because the policy may have changed since the job was created
        SchedulePolicyCalendar? calendar = LoadCalendar(context.Storage, context.Connection, name, allowCached: false);
        if (calendar == null)
        {
            Logger.Debug($"Background job '{context.BackgroundJob.Id}' follows schedule policy '{name}', which no longer exists, so it runs.");
            return;
        }

        DateTime now = JobClock.UtcNow;
        SchedulePolicyDecision decision = calendar.Evaluate(now);
        if (decision.Allowed) return;

        // The job exists already, so a policy that rejects it now skips it
        if (ReadWhenBlocked(ReadParameter(context, ScheduleOptions.WhenBlockedParameter)) != WhenBlocked.MoveForward)
        {
            context.CandidateState = new DeletedState { Reason = TerminateJobFilter.FormatReason(SkippedPrefix, decision.Reason!) }; // Not allowed, so explained
            return;
        }

        DateTime? next = calendar.FindNextAllowed(now);
        if (!next.HasValue)
        {
            // Nothing allowed in sight, for example a date provider missing on this server: checked again tomorrow
            context.CandidateState = new ScheduledState(now.AddDays(1))
            {
                Reason = TerminateJobFilter.FormatReason(MovedPrefix, $"'{name}' allows no time in the next ten years"),
            };
            return;
        }

        ScheduledState moved = new(next.Value) { Reason = TerminateJobFilter.FormatReason(MovedPrefix, decision.Reason!) }; // Not allowed, so explained
        context.CandidateState = moved;

        // The run's intended time is the new one, so its lateness counts from there. Written when this state is the
        // one applied: a later filter may still elect another
        MovedStates.AddOrUpdate(moved, Moved);
    }

    /// <inheritdoc />
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.NewState is ScheduledState scheduled && MovedStates.TryGetValue(scheduled, out _))
        {
            SetScheduledFor(context, transaction, scheduled.EnqueueAt);
        }
    }

    /// <inheritdoc />
    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }

    private static SchedulePolicyCalendar? LoadCalendar(JobStorage storage, IStorageConnection connection, string name, bool allowCached)
    {
        ITimeZoneResolver timeZoneResolver = GlobalTimeZoneResolver.Current;
        ConcurrentDictionary<string, CachedCalendar> calendars = Calendars.GetValue(storage, static _ => new(StringComparer.Ordinal));
        long now = Stopwatch.GetTimestamp();

        if (allowCached &&
            calendars.TryGetValue(name, out CachedCalendar? cached) &&
            ReferenceEquals(cached.TimeZoneResolver, timeZoneResolver) &&
            Stopwatch.GetElapsedTime(cached.LoadedAt, now) < CalendarCacheDuration)
        {
            return cached.Calendar;
        }

        SchedulePolicyCalendar? calendar = SchedulePolicyCalendar.Load(connection, name, timeZoneResolver);

        // A missing policy isn't remembered: one saved right after is used at once
        if (calendar != null)
        {
            calendars[name] = new CachedCalendar(calendar, timeZoneResolver, now);
        }
        else
        {
            calendars.TryRemove(name, out _);
        }

        return calendar;
    }

    private static string? ReadParameter(ElectStateContext context, string name)
    {
        try
        {
            return context.GetJobParameter<string>(name, allowStale: true);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"Job parameter '{name}' of background job '{context.BackgroundJob.Id}' can't be read, so its schedule policy is ignored.", ex);
            return null;
        }
    }

    private static WhenBlocked ReadWhenBlocked(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out WhenBlocked whenBlocked) && Enum.IsDefined(whenBlocked)
            ? whenBlocked
            : WhenBlocked.MoveForward;
    }

    private static void SetScheduledFor(ApplyStateContext context, IWriteOnlyTransaction transaction, DateTime scheduledFor)
    {
        string jobId = context.BackgroundJob.Id;
        string value = JobHelper.ToMillisecondTimestamp(scheduledFor).ToString(CultureInfo.InvariantCulture);

        if (transaction is JobStorageTransaction storageTransaction &&
            context.Storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter))
        {
            storageTransaction.SetJobParameter(jobId, PerformContext.ScheduledForParameter, value);
            return;
        }

        // Without it in the transaction, once the state is committed: a state change that fails leaves the old time
        IStorageConnection connection = context.Connection;
        context.RunAfterCommit(() => connection.SetJobParameter(jobId, PerformContext.ScheduledForParameter, value));
    }

    private sealed record CachedCalendar(SchedulePolicyCalendar Calendar, ITimeZoneResolver TimeZoneResolver, long LoadedAt);
}
