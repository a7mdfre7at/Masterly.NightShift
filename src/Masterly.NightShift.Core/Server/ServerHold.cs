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
/// <para>The parameter lists the holds, oldest first, separated by <c>;</c>: each is the Unix time in milliseconds at
/// which it began, a <c>-</c>, and the time it ended, which is missing while the hold lasts:
/// <c>1763000000000-1763000031250;1763000090000-</c>. A hold ends when the job leaves the Scheduled state it was
/// held in, whatever state follows: enqueued again, moved by its schedule policy, deleted. Only the part of a hold
/// that falls between the moment the job was meant to run and the moment it starts is left out of its lateness, so
/// a job moved to a later time after the hold isn't counted less late for it. A hold whose end wasn't written counts
/// up to the moment the job starts. The last <see cref="MaxHolds"/> holds are kept: an older one counts as lateness.</para>
/// <para>It is bookkeeping: an error is logged and never fails the state change. It reads only the parameters the
/// storage loaded with the job (<see cref="BackgroundJob.ParametersSnapshot"/>), so it costs no round trip; on a
/// storage that doesn't load them, a hold counts as lateness, as it did before.</para>
/// </remarks>
internal static class ServerHold
{
    /// <summary>How many holds the parameter keeps.</summary>
    internal const int MaxHolds = 16;

    private const char HoldSeparator = ';';
    private const char EndSeparator = '-';

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(ServerHold));

    /// <summary>
    /// Notes that a paused server holds the job from now on, during the election of the Scheduled state that holds it.
    /// </summary>
    public static void Begin(ElectStateContext context)
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

            // A hold whose end wasn't written ends where this one begins
            if (holds.Count > 0 && holds[^1].End == null)
            {
                holds[^1] = holds[^1] with { End = Math.Max(holds[^1].Start, now) };
            }

            holds.Add(new Hold(now, null));
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

            long now = JobHelper.ToMillisecondTimestamp(JobClock.UtcNow);
            holds[^1] = holds[^1] with { End = Math.Max(holds[^1].Start, now) };
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
                long overlap = Math.Min(hold.End ?? end, end) - Math.Max(hold.Start, start);
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
            int separator = part.IndexOf(EndSeparator, StringComparison.Ordinal);
            if (separator <= 0 ||
                !long.TryParse(part.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out long start))
            {
                continue;
            }

            string endText = part[(separator + 1)..];
            if (endText.Length == 0)
            {
                holds.Add(new Hold(start, null));
            }
            else if (long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out long end) && end >= start)
            {
                holds.Add(new Hold(start, end));
            }
        }

        return holds;
    }

    private static void Write(ElectStateContext context, List<Hold> holds)
    {
        string value = string.Join(HoldSeparator, holds.Select(static hold => string.Create(
            CultureInfo.InvariantCulture,
            $"{hold.Start}{EndSeparator}{hold.End}")));
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

    // Unix times in milliseconds; End is null while the hold lasts
    private readonly record struct Hold(long Start, long? End);
}
