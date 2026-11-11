using System.Globalization;
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
/// job waiting in its queue. A policy deleted after the job was created no longer applies to it.
/// </remarks>
public sealed class SchedulePolicyFilter : IClientFilter, IElectStateFilter
{
    private const string SkippedPrefix = "Skipped";
    private const string MovedPrefix = "Moved by policy";

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(SchedulePolicyFilter));

    /// <inheritdoc />
    public void OnCreating(CreatingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.InitialState is not (EnqueuedState or ScheduledState)) return;
        if (!context.Parameters.TryGetValue(ScheduleOptions.PolicyParameter, out object? value) || value is not string name) return;

        name = SchedulePolicy.NormalizeName(name, nameof(context));
        SchedulePolicyCalendar calendar = SchedulePolicyCalendar.Load(context.Connection, name, GlobalTimeZoneResolver.Current)
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

        SchedulePolicyCalendar? calendar = SchedulePolicyCalendar.Load(context.Connection, name, GlobalTimeZoneResolver.Current);
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

        context.CandidateState = new ScheduledState(next.Value) { Reason = TerminateJobFilter.FormatReason(MovedPrefix, decision.Reason!) }; // Not allowed, so explained

        // The run's intended time is the new one, so its lateness counts from there
        SetScheduledFor(context, next.Value);
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

    private static void SetScheduledFor(ElectStateContext context, DateTime scheduledFor)
    {
        string value = JobHelper.ToMillisecondTimestamp(scheduledFor).ToString(CultureInfo.InvariantCulture);

        if (context.Transaction is JobStorageTransaction transaction &&
            context.Storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter))
        {
            transaction.SetJobParameter(context.BackgroundJob.Id, PerformContext.ScheduledForParameter, value);
        }
        else
        {
            context.Connection.SetJobParameter(context.BackgroundJob.Id, PerformContext.ScheduledForParameter, value);
        }
    }
}
