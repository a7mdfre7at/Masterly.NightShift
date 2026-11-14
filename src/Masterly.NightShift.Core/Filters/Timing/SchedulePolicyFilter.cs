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
/// <para>The policy is checked when the job is created, for the moment it is to run, and again when the delayed job
/// scheduler enqueues it, because the policy may have changed meanwhile; a job with a queue of its own
/// (<see cref="ScheduleOptions.Queue"/>), which the scheduler puts in its queue without a state change, is checked
/// when a worker takes it. That check judges by the storage's clock where the storage has one, the clock the
/// scheduler finds jobs due by. A blocked job is moved forward to the next
/// instant the policy allows, deleted as skipped, or, at creation, not created, as
/// <see cref="ScheduleOptions.WhenBlocked"/> says; a job already created whose due time is blocked is skipped
/// instead of rejected. A job enqueued by hand (the dashboard's Enqueue now, <c>Requeue</c>) isn't checked, nor a
/// job waiting in its queue. A policy deleted after the job was created no longer applies to it. At creation, a
/// policy is read from the storage at most once every few seconds (<see cref="CalendarCacheDuration"/>), so a changed
/// policy applies to new jobs within that time; when a job comes due, its policy is always read afresh.</para>
/// <para>A policy that can't be evaluated (it can't be read, its time zone or a date provider is unknown on this
/// server, a date provider throws) neither blocks the job nor lets it run, whatever
/// <see cref="ScheduleOptions.WhenBlocked"/> says: the job is never skipped or rejected for it. A job that would
/// run now waits in the Scheduled state, with a reason that names the policy and the error, and is checked again
/// after 1, 5 and 15 minutes, then every hour, until the policy can be evaluated; the number of checks that failed
/// in a row is kept in the <see cref="CheckAttemptsParameter"/> job parameter. At creation, a delayed job keeps
/// its time and is checked when it is due. Waiting isn't a retry: the attempts of
/// <see cref="AutomaticRetryAttribute"/> aren't used up, and the moment the job was meant to run
/// (<see cref="PerformContext.ScheduledFor"/>) stays, so <see cref="ScheduleOptions.SkipAfter"/> still counts from
/// it. A job that is skipped as late after such a wait says so in its reason, see
/// <see cref="SkipIfLateAttribute"/>.</para>
/// </remarks>
public sealed class SchedulePolicyFilter : IClientFilter, IElectStateFilter, IApplyStateFilter
{
    /// <summary>
    /// The <c>PolicyCheckAttempts</c> job parameter: how many times in a row the job's policy couldn't be evaluated
    /// when the job was to run. Once the policy can be evaluated again it is set to zero when the policy lets the
    /// job run, and cleared when the policy moves or skips the job.
    /// </summary>
    /// <remarks>
    /// The zero records that the job waited for its policy: the policy filter runs when the job is enqueued and the
    /// late-start check (<see cref="SkipIfLateAttribute"/>, <see cref="ScheduleOptions.SkipAfter"/>) when a worker
    /// starts it, so this parameter is what tells that check why the job is late. A job moved to a new time is
    /// late from that time on, so nothing is kept for it.
    /// </remarks>
    public const string CheckAttemptsParameter = "PolicyCheckAttempts";

    private const string SkippedPrefix = "Skipped";
    private const string MovedPrefix = "Moved by policy";

    // How long a job whose policy can't be evaluated waits for its next check, by the checks that failed before;
    // the last delay repeats
    private static readonly TimeSpan[] CheckDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60),
    ];

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(SchedulePolicyFilter));

    // The policies read recently, per storage, so creating many jobs that follow one policy doesn't read it each time
    private static readonly ConditionalWeakTable<JobStorage, ConcurrentDictionary<string, CachedCalendar>> Calendars = [];

    // The job parameter that goes with a Scheduled state this filter elected for a due job, written once that state
    // is the one applied: the new time of a job moved forward, the failed checks of a job that waits for its policy
    private static readonly ConditionalWeakTable<ScheduledState, ParameterWrite> ElectedStates = [];

    /// <summary>How long a policy read from the storage is used before it is read again. Replaceable for tests.</summary>
    internal static TimeSpan CalendarCacheDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public void OnCreating(CreatingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.InitialState is not (EnqueuedState or ScheduledState))
        {
            return;
        }

        if (!context.Parameters.TryGetValue(ScheduleOptions.PolicyParameter, out object? value) || value is not string name)
        {
            return;
        }

        name = SchedulePolicy.NormalizeName(name, nameof(context));
        SchedulePolicyCalendar calendar = LoadCalendar(context.Storage, context.Connection, name, allowCached: true)
            ?? throw new InvalidOperationException($"There is no schedule policy named '{name}'. Save it with SaveSchedulePolicy before creating jobs that follow it.");

        DateTime now = JobClock.UtcNow;
        DateTime runAt = context.InitialState is ScheduledState scheduled ? scheduled.EnqueueAt : now;
        SchedulePolicyDecision decision = calendar.Evaluate(runAt, out SchedulePolicyFailure? failure);
        if (decision.Allowed)
        {
            return;
        }

        if (failure != null)
        {
            CreateWaiting(context, name, failure, now);
            return;
        }

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
                // When the search meets an instant it can't evaluate before an allowed one, the job is moved there:
                // every instant before it is blocked, and the policy is asked again when the job is due
                SchedulePolicySearchResult search = calendar.Search(runAt);
                DateTime next = search.Allowed
                    ?? search.Failure?.Instant
                    ?? throw new InvalidOperationException($"Schedule policy '{name}' allows no time in the ten years after {runAt:yyyy-MM-dd HH:mm} UTC.");

                KeepQueue(context);
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

        // Only a due job; a job enqueued by hand runs
        if (!IsDue(context))
        {
            return;
        }

        string? name = ReadParameter(context, ScheduleOptions.PolicyParameter);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        // Read afresh: this check exists because the policy may have changed since the job was created
        SchedulePolicyCalendar? calendar = LoadCalendar(context.Storage, context.Connection, name, allowCached: false);
        if (calendar == null)
        {
            Logger.Debug($"Background job '{context.BackgroundJob.Id}' follows schedule policy '{name}', which no longer exists, so it runs.");
            return;
        }

        // The clock that found the job due: a time chosen by another clock may be due already, and the scheduler
        // would take the job again at once
        DateTime now = StorageTime.UtcNow(context.Storage, context.Connection);
        SchedulePolicyDecision decision = calendar.Evaluate(now, out SchedulePolicyFailure? failure);
        int? failedChecks = ReadFailedChecks(context);

        if (failure != null)
        {
            // Neither allowed nor blocked: the job isn't run, and it isn't skipped either, whatever WhenBlocked says
            Wait(context, name, failure, now, failedChecks ?? 0);
            return;
        }

        if (failedChecks.HasValue)
        {
            EndFailedChecks(context, failedChecks.Value, runs: decision.Allowed);
        }

        if (decision.Allowed)
        {
            return;
        }

        // The job exists already, so a policy that rejects it now skips it
        if (ReadWhenBlocked(ReadParameter(context, ScheduleOptions.WhenBlockedParameter)) != WhenBlocked.MoveForward)
        {
            Elect(context, new DeletedState { Reason = TerminateJobFilter.FormatReason(SkippedPrefix, decision.Reason!) }); // Not allowed, so explained
            return;
        }

        SchedulePolicySearchResult search = calendar.Search(now);
        if (!search.Allowed.HasValue && search.Failure == null)
        {
            // A whole search found nothing allowed: checked again tomorrow, as the policy may change
            Elect(context, new ScheduledState(now.AddDays(1))
            {
                Reason = TerminateJobFilter.FormatReason(MovedPrefix, $"'{name}' allows no time in the next ten years"),
            });
            return;
        }

        // An instant the search can't evaluate, met before an allowed one, is where the job is checked again: every
        // instant before it is blocked
        DateTime next = search.Allowed ?? search.Failure!.Instant; // One of the two, as checked above
        ScheduledState moved = new(next) { Reason = TerminateJobFilter.FormatReason(MovedPrefix, decision.Reason!) }; // Not allowed, so explained
        Elect(context, moved);

        // The run's intended time is the new one, so its lateness counts from there. Written when this state is the
        // one applied: a later filter may still elect another
        ElectedStates.AddOrUpdate(moved, new ParameterWrite(
            PerformContext.ScheduledForParameter,
            JobHelper.ToMillisecondTimestamp(next).ToString(CultureInfo.InvariantCulture)));
    }

    /// <inheritdoc />
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.NewState is ScheduledState scheduled && ElectedStates.TryGetValue(scheduled, out ParameterWrite? write))
        {
            SetJobParameter(context, transaction, write);
        }
    }

    /// <inheritdoc />
    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }

    /// <summary>
    /// How long a job waits for the next check of a policy that can't be evaluated, after
    /// <paramref name="failedChecks"/> checks that failed in a row before this one: 1, 5 and 15 minutes, then an hour.
    /// </summary>
    internal static TimeSpan GetCheckDelay(int failedChecks)
    {
        return CheckDelays[Math.Clamp(failedChecks, 0, CheckDelays.Length - 1)];
    }

    // The delayed job scheduler enqueues a due job with a state change, except a job with a queue of its own
    // (ScheduleOptions.Queue, Job.Queue): that one goes from the schedule straight into its queue without one, and
    // is still Scheduled when a worker takes it. Its check comes then; moved or skipped there, it never runs, and
    // the worker drops its queue entry
    private static bool IsDue(ElectStateContext context)
    {
        return context.CandidateState switch
        {
            EnqueuedState { IsDue: true } => true,
            ProcessingState => ScheduledState.StateName.Equals(context.CurrentState, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    // A job checked as a worker takes it never was Processing, so its history doesn't say it was
    private static void Elect(ElectStateContext context, IState state)
    {
        if (context.CandidateState is ProcessingState)
        {
            context.ReplaceCandidateState(state);
        }
        else
        {
            context.CandidateState = state;
        }
    }

    // A job created while its policy can't be evaluated is created all the same, to be checked when it is due: a
    // delayed job keeps its time, and a job that would run now waits for the first check
    private static void CreateWaiting(CreatingContext context, string name, SchedulePolicyFailure failure, DateTime now)
    {
        ReportFailure(name, failure, now);

        if (context.InitialState is not EnqueuedState)
        {
            Logger.Debug($"A delayed job of {context.Job.Type.Name}.{context.Job.Method.Name} is created although its schedule policy can't be evaluated, and is checked when it is due: {failure.Reason}");
            return;
        }

        TimeSpan delay = GetCheckDelay(0);
        Logger.Debug($"A job of {context.Job.Type.Name}.{context.Job.Method.Name} waits {delay} for its schedule policy, which can't be evaluated: {failure.Reason}");

        KeepQueue(context);
        context.SetJobParameter(CheckAttemptsParameter, 1);

        // The job was meant to run now, not when its policy is checked next
        context.SetJobParameter(PerformContext.ScheduledForParameter, JobHelper.ToMillisecondTimestamp(now));
        context.ReplaceInitialState(new ScheduledState(now + delay) { Reason = FormatWaitingReason(delay, failure) });
    }

    // A due job whose policy can't be evaluated stays scheduled and is checked again later, with a growing delay.
    // Not a retry: the job didn't run, and AutomaticRetry's attempts aren't touched
    private static void Wait(ElectStateContext context, string name, SchedulePolicyFailure failure, DateTime now, int failedChecks)
    {
        ReportFailure(name, failure, now);

        TimeSpan delay = GetCheckDelay(failedChecks);
        Logger.Debug($"Background job '{context.BackgroundJob.Id}' waits {delay} for its schedule policy, which can't be evaluated: {failure.Reason}");

        ScheduledState waiting = new(now + delay) { Reason = FormatWaitingReason(delay, failure) };
        Elect(context, waiting);

        // Counted when this state is the one applied: a later filter may still elect another
        ElectedStates.AddOrUpdate(waiting, new ParameterWrite(
            CheckAttemptsParameter,
            SerializationHelper.Serialize(failedChecks + 1, SerializationOption.User)));
    }

    // The reason of a state holds 100 characters, so it starts with what happens next and ends with the error
    private static string FormatWaitingReason(TimeSpan delay, SchedulePolicyFailure failure)
    {
        return TerminateJobFilter.FormatReason(
            string.Create(CultureInfo.InvariantCulture, $"Waiting for policy, next check in {(int)delay.TotalMinutes} min"),
            failure.Reason);
    }

    // Once a minute for each policy, whatever the number of jobs that follow it
    private static void ReportFailure(string name, SchedulePolicyFailure failure, DateTime now)
    {
        if (SchedulePolicyCalendar.TryStartProviderWarning($"policy:{name}", now))
        {
            Logger.Warn($"Schedule policy '{name}' can't be evaluated, so the jobs that follow it wait and are checked again later: {failure.Reason}.");
        }
    }

    // A queue given only by the Enqueued state comes back when the job is enqueued at its new time
    private static void KeepQueue(CreatingContext context)
    {
        if (context.InitialState is EnqueuedState enqueued && context.Job.Queue == null &&
            !EnqueuedState.DefaultQueue.Equals(enqueued.Queue, StringComparison.OrdinalIgnoreCase))
        {
            context.SetJobParameter(DrainModeFilter.HeldQueueParameter, enqueued.Queue);
        }
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

    // The checks that failed in a row, or null for a job that never waited for its policy. A count that can't be
    // read starts again: it only chooses the delay
    private static int? ReadFailedChecks(ElectStateContext context)
    {
        try
        {
            return context.GetJobParameter<int?>(CheckAttemptsParameter, allowStale: true) is { } failedChecks
                ? Math.Max(failedChecks, 0)
                : null;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"Job parameter '{CheckAttemptsParameter}' of background job '{context.BackgroundJob.Id}' can't be read, so its policy checks are counted from the start.", ex);
            return null;
        }
    }

    // The policy was evaluated, whatever state the job ends in, so the checks that failed in a row end at once. A
    // job that runs now keeps the parameter, at zero: this filter runs when the job is enqueued and the late-start
    // check when a worker starts it, so the zero is what tells that check that the job waited for its policy, when
    // it skips the job as late. A job that is moved is late from its new time on, and one that is skipped is gone,
    // so nothing is kept for them. A count that stays only makes a later wait longer, so failing to write doesn't
    // fail the state change
    private static void EndFailedChecks(ElectStateContext context, int failedChecks, bool runs)
    {
        if (runs && failedChecks == 0)
        {
            return;
        }

        try
        {
            context.Connection.SetJobParameter(
                context.BackgroundJob.Id,
                CheckAttemptsParameter,
                runs ? SerializationHelper.Serialize(0, SerializationOption.User) : null);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"Job parameter '{CheckAttemptsParameter}' of background job '{context.BackgroundJob.Id}' couldn't be {(runs ? "set to zero" : "cleared")}.", ex);
        }
    }

    private static WhenBlocked ReadWhenBlocked(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out WhenBlocked whenBlocked) && Enum.IsDefined(whenBlocked)
            ? whenBlocked
            : WhenBlocked.MoveForward;
    }

    private static void SetJobParameter(ApplyStateContext context, IWriteOnlyTransaction transaction, ParameterWrite write)
    {
        string jobId = context.BackgroundJob.Id;

        if (transaction is JobStorageTransaction storageTransaction &&
            context.Storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter))
        {
            storageTransaction.SetJobParameter(jobId, write.Name, write.Value);
            return;
        }

        // Without it in the transaction, once the state is committed: a state change that fails leaves the old value
        IStorageConnection connection = context.Connection;
        context.RunAfterCommit(() => connection.SetJobParameter(jobId, write.Name, write.Value));
    }

    private sealed record CachedCalendar(SchedulePolicyCalendar Calendar, ITimeZoneResolver TimeZoneResolver, long LoadedAt);

    private sealed record ParameterWrite(string Name, string Value);
}
