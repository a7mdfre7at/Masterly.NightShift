using System.Globalization;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Server;

/// <summary>
/// Keeps how long a job was held because the server that fetched it was paused on its own or fenced
/// (<see cref="DrainModeFilter"/>), in the <see cref="DrainModeFilter.ServerHoldParameter"/> job parameter, so that
/// this time doesn't count as lateness (<see cref="PerformContext.Lateness"/>). The job wasn't late: a server that
/// shouldn't have had it sat on it, while the servers that may work could have run it at once. The time a job is
/// held by the cluster-wide switch or by a paused queue is not kept: nothing could have run it then, so it is late.
/// </summary>
/// <remarks>
/// <para>The parameter holds the milliseconds the job was held so far, and, while a hold lasts, a <c>|</c> and the
/// Unix time in milliseconds at which it began: <c>31250</c>, or <c>31250|1763000000000</c>. The hold ends when the
/// job is enqueued again, which adds its length to the total. A hold whose end wasn't written counts up to the
/// moment the job starts.</para>
/// <para>It is bookkeeping: an error is logged and never fails the state change. It reads only the parameters the
/// storage loaded with the job (<see cref="BackgroundJob.ParametersSnapshot"/>), so it costs no round trip; on a
/// storage that doesn't load them, a hold counts as lateness, as it did before.</para>
/// </remarks>
internal static class ServerHold
{
    private const char Separator = '|';

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(ServerHold));

    /// <summary>
    /// Notes that a paused server holds the job from now on, during the election of the Scheduled state that holds it.
    /// </summary>
    public static void Begin(ElectStateContext context)
    {
        // Without the parameters loaded with the job the total can't be read, and nothing would read it later
        if (context.BackgroundJob.ParametersSnapshot == null)
        {
            return;
        }

        try
        {
            long now = JobHelper.ToMillisecondTimestamp(JobClock.UtcNow);
            (long total, long? since) = Read(context.BackgroundJob);

            // A hold whose end wasn't written ends where this one begins
            Write(context, total + Length(since, now), now);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"Could not note that a paused server holds background job '{context.BackgroundJob.Id}', so the hold counts as lateness.", ex);
        }
    }

    /// <summary>
    /// Ends the hold of a job that is enqueued again, during the election of the Enqueued state that follows the
    /// Scheduled one. Does nothing for a job that isn't held by a paused server.
    /// </summary>
    public static void End(ElectStateContext context)
    {
        if (!ScheduledState.StateName.Equals(context.CurrentState, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            (long total, long? since) = Read(context.BackgroundJob);
            if (since.HasValue)
            {
                Write(context, total + Length(since, JobHelper.ToMillisecondTimestamp(JobClock.UtcNow)), null);
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"Could not note that the hold of background job '{context.BackgroundJob.Id}' by a paused server is over, so it counts until the job starts.", ex);
        }
    }

    /// <summary>
    /// How long paused servers held the job before <paramref name="startedAt"/>, by the parameters loaded with the job.
    /// </summary>
    public static TimeSpan GetHeldTime(BackgroundJob job, DateTime startedAt)
    {
        try
        {
            (long total, long? since) = Read(job);
            return TimeSpan.FromMilliseconds(total + Length(since, JobHelper.ToMillisecondTimestamp(startedAt)));
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // A value that can't be read counts as no hold
            return TimeSpan.Zero;
        }
    }

    private static long Length(long? since, long until)
    {
        return since.HasValue && until > since.Value ? until - since.Value : 0;
    }

    private static (long Total, long? Since) Read(BackgroundJob job)
    {
        if (job.ParametersSnapshot == null ||
            !job.ParametersSnapshot.TryGetValue(DrainModeFilter.ServerHoldParameter, out string? serialized))
        {
            return (0, null);
        }

        string? value = SerializationHelper.Deserialize<string>(serialized, SerializationOption.User);
        if (string.IsNullOrEmpty(value))
        {
            return (0, null);
        }

        int separator = value.IndexOf(Separator, StringComparison.Ordinal);
        string totalText = separator < 0 ? value : value[..separator];

        long total = long.TryParse(totalText, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedTotal) ? parsedTotal : 0;
        long? since = separator >= 0 && long.TryParse(value[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long parsedSince)
            ? parsedSince
            : null;

        return (total, since);
    }

    private static void Write(ElectStateContext context, long total, long? since)
    {
        string value = since.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{total}{Separator}{since.Value}")
            : total.ToString(CultureInfo.InvariantCulture);
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
}
