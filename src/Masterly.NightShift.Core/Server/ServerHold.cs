using System.Globalization;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Server;

/// <summary>
/// Keeps when a job was held because the server that fetched it was paused on its own or fenced
/// (<see cref="DrainModeFilter"/>), in the <see cref="DrainModeFilter.ServerHoldParameter"/> job parameter, so that
/// this time doesn't count as lateness (<see cref="PerformContext.Lateness"/>). The job wasn't late: a server that
/// shouldn't have had it sat on it, while the servers that may work could have run it at once. The time a job is
/// held by the cluster-wide switch or by a paused queue is not kept: nothing could have run it then, so it is late.
/// </summary>
/// <remarks>
/// <para>The parameter lists the holds, oldest first, separated by <c>;</c>, as Unix times in milliseconds. A hold that
/// ended is its start, a <c>-</c> and its end; one that lasts is its start, a <c>~</c> and the latest time it can
/// count until: <c>1763000000000-1763000031250;1763000090000~1763000135000</c>. That latest time is when the held
/// state was due (<see cref="ScheduledState.EnqueueAt"/>) and <see cref="PollingAllowance"/> after it, the time
/// the delayed job scheduler may take to see a due job. A hold ends when the job leaves the Scheduled state it was
/// held in, whatever state follows, and never after that latest time. A job may leave the state without anything
/// noting it: the delayed job scheduler puts a job with a queue of its own back in that queue without a state
/// change, and a state change that runs no filter (the job failed to be enqueued) can't end the hold. Such a hold
/// counts until that latest time only, and the time after it is the job's own lateness.</para>
/// <para>Only the part of a hold that falls between the moment the job was meant to run and the moment it starts is
/// left out of its lateness, so a job moved to a later time after the hold isn't counted less late for it. The last
/// <see cref="MaxHolds"/> holds are kept: an older one counts as lateness.</para>
/// <para>It is bookkeeping: an error is logged and never fails the state change. It reads only the parameters the
/// storage loaded with the job (<see cref="BackgroundJob.ParametersSnapshot"/>), so it costs no round trip; on a
/// storage that doesn't load them, a hold counts as lateness, as it did before.</para>
/// </remarks>
internal static class ServerHold
{
    /// <summary>How many holds the parameter keeps.</summary>
    internal const int MaxHolds = 16;

    /// <summary>
    /// How long after the held state was due a hold may still count: the default polling interval of the delayed job
    /// scheduler (<see cref="BackgroundJobServerOptions.SchedulePollingInterval"/>), the time it may take to see the
    /// due job and give it back to the servers. Until then the job was kept away from them by the hold. Where
    /// the scheduler polls less often, the rest counts as lateness.
    /// </summary>
    internal static readonly TimeSpan PollingAllowance = TimeSpan.FromSeconds(15);

    private const char HoldSeparator = ';';
    private const char EndSeparator = '-';
    private const char LimitSeparator = '~';

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(ServerHold));

    /// <summary>
    /// Notes that a paused server holds the job from now on, during the election of the Scheduled state that holds it
    /// until <paramref name="dueAt"/>.
    /// </summary>
    public static void Begin(ElectStateContext context, DateTime dueAt)
    {
        // Without the parameters loaded with the job the holds can't be read, and nothing would read them later
        if (context.BackgroundJob.ParametersSnapshot == null)
        {
            return;
        }

        try
        {
            long now = JobHelper.ToMillisecondTimestamp(JobClock.UtcNow);
            List<Hold> holds = Read(context.BackgroundJob);

            // A hold whose end wasn't written ends where this one begins, or at the latest time it can count until
            if (holds.Count > 0 && holds[^1].End == null)
            {
                holds[^1] = holds[^1].EndAt(now);
            }

            holds.Add(new Hold(now, null, Math.Max(now, JobHelper.ToMillisecondTimestamp(dueAt + PollingAllowance))));
            if (holds.Count > MaxHolds)
            {
                holds.RemoveRange(0, holds.Count - MaxHolds);
            }

            Write(context, holds);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"Could not note that a paused server holds background job '{context.BackgroundJob.Id}', so the hold counts as lateness.", ex);
        }
    }

    /// <summary>
    /// Ends the hold of a job that leaves the Scheduled state a paused server held it in, during the election of the
    /// state that follows, whatever it is. Does nothing for a job that isn't held by a paused server.
    /// </summary>
    public static void End(ElectStateContext context)
    {
        if (!ScheduledState.StateName.Equals(context.CurrentState, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            List<Hold> holds = Read(context.BackgroundJob);
            if (holds.Count == 0 || holds[^1].End != null)
            {
                return;
            }

            holds[^1] = holds[^1].EndAt(JobHelper.ToMillisecondTimestamp(JobClock.UtcNow));
            Write(context, holds);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"Could not note that the hold of background job '{context.BackgroundJob.Id}' by a paused server is over, so it counts until the job starts.", ex);
        }
    }

    /// <summary>
    /// How long paused servers held the job between <paramref name="from"/> and <paramref name="until"/>, by the
    /// parameters loaded with the job: the part of the job's lateness that isn't its own.
    /// </summary>
    public static TimeSpan GetHeldTime(BackgroundJob job, DateTime from, DateTime until)
    {
        try
        {
            long start = JobHelper.ToMillisecondTimestamp(from);
            long end = JobHelper.ToMillisecondTimestamp(until);
            long held = 0;

            // Holds don't overlap, as each one ends before the next begins
            foreach (Hold hold in Read(job))
            {
                long overlap = Math.Min(hold.End ?? hold.Limit, end) - Math.Max(hold.Start, start);
                if (overlap > 0)
                {
                    held += overlap;
                }
            }

            return TimeSpan.FromMilliseconds(held);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // A value that can't be read counts as no hold
            return TimeSpan.Zero;
        }
    }

    // The holds that can be read; anything else in the value (written by an earlier build, damaged) is left out
    private static List<Hold> Read(BackgroundJob job)
    {
        List<Hold> holds = [];
        if (job.ParametersSnapshot == null ||
            !job.ParametersSnapshot.TryGetValue(DrainModeFilter.ServerHoldParameter, out string? serialized))
        {
            return holds;
        }

        string? value = SerializationHelper.Deserialize<string>(serialized, SerializationOption.User);
        if (string.IsNullOrEmpty(value))
        {
            return holds;
        }

        foreach (string part in value.Split(HoldSeparator))
        {
            if (TryReadHold(part, out Hold hold))
            {
                holds.Add(hold);
            }
        }

        return holds;
    }

    private static bool TryReadHold(string text, out Hold hold)
    {
        hold = default;
        int separator = text.IndexOfAny([EndSeparator, LimitSeparator]);
        if (separator <= 0 ||
            !long.TryParse(text.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out long start))
        {
            return false;
        }

        string rest = text[(separator + 1)..];

        // Written by an earlier build, which kept no latest time: the hold interval and the allowance count
        if (text[separator] == EndSeparator && rest.Length == 0)
        {
            hold = new Hold(start, null, start + (long)(DrainMode.HoldInterval + PollingAllowance).TotalMilliseconds);
            return true;
        }

        if (!long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out long time) || time < start)
        {
            return false;
        }

        hold = text[separator] == EndSeparator ? new Hold(start, time, time) : new Hold(start, null, time);
        return true;
    }

    private static void Write(ElectStateContext context, List<Hold> holds)
    {
        string value = string.Join(HoldSeparator, holds.Select(static hold => hold.End is { } end
            ? string.Create(CultureInfo.InvariantCulture, $"{hold.Start}{EndSeparator}{end}")
            : string.Create(CultureInfo.InvariantCulture, $"{hold.Start}{LimitSeparator}{hold.Limit}")));
        string serialized = SerializationHelper.Serialize(value, SerializationOption.User);

        // In the state change's transaction when the storage can, so a state change that fails writes nothing
        if (context.Transaction is JobStorageTransaction transaction &&
            context.Storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter))
        {
            transaction.SetJobParameter(context.BackgroundJob.Id, DrainModeFilter.ServerHoldParameter, serialized);
        }
        else
        {
            context.Connection.SetJobParameter(context.BackgroundJob.Id, DrainModeFilter.ServerHoldParameter, serialized);
        }
    }

    // Unix times in milliseconds. End is null while the hold lasts; Limit is the latest time it can count until
    private readonly record struct Hold(long Start, long? End, long Limit)
    {
        // The hold ended at the given time, or at the latest time it can count until when that is earlier
        public Hold EndAt(long time) => End.HasValue ? this : this with { End = Math.Clamp(time, Start, Math.Max(Start, Limit)) };
    }
}
