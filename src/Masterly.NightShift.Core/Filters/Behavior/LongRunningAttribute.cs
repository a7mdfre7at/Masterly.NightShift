namespace Masterly.NightShift;

/// <summary>
/// Sends a job that runs for a long time to the <c>long-running</c> queue (<see cref="Queue"/>), which servers serve
/// with a pool of workers of its own (<see cref="BackgroundJobServerOptions.LongRunningWorkerCount"/>), so long jobs
/// never take every worker from the short ones: <c>[LongRunning]</c> on a nightly import, a video transcode, a full
/// reindex.
/// </summary>
/// <remarks>
/// It works like <c>[Queue("long-running")]</c>, which it replaces when both are given, and has its order, so it is
/// applied after the other filters. A server serves the queue when <see cref="BackgroundJobServerOptions.LongRunningWorkerCount"/>
/// is above 0, or when the queue is in its <see cref="BackgroundJobServerOptions.Queues"/>; without such a server the
/// jobs wait in the queue.
/// </remarks>
public sealed class LongRunningAttribute : JobFilterAttribute, IElectStateFilter
{
    /// <summary>The queue of long-running jobs.</summary>
    public const string Queue = "long-running";

    /// <summary>Creates the attribute.</summary>
    public LongRunningAttribute()
    {
        Order = int.MaxValue;
    }

    /// <inheritdoc />
    public void OnStateElection(ElectStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.CandidateState is EnqueuedState enqueuedState)
        {
            enqueuedState.Queue = Queue;
        }
    }
}
