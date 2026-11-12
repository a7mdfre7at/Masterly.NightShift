using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

/// <summary>
/// Sends a job that runs for a long time to the <c>long-running</c> queue (<see cref="Queue"/>), which servers serve
/// with a pool of workers of its own (<see cref="BackgroundJobServerOptions.LongRunningWorkerCount"/>), so long jobs
/// never take every worker from the short ones: <c>[LongRunning]</c> on a nightly import, a video transcode, a full
/// reindex.
/// </summary>
/// <remarks>
/// <para>It works like <c>[Queue("long-running")]</c>, which it replaces when both are given, and has its order, so it
/// is applied after the other filters.</para>
/// <para>Nothing serves the queue by default: <see cref="BackgroundJobServerOptions.LongRunningWorkerCount"/> is 0. A
/// server serves it when its <see cref="BackgroundJobServerOptions.LongRunningWorkerCount"/> is above 0, or when the
/// queue is in its <see cref="BackgroundJobServerOptions.Queues"/>; until such a server runs, the jobs wait in the
/// queue, and nothing fails. When this process runs servers but none of them serves the queue, the first job sent
/// there logs a warning, once, because the jobs wait unless a server in another process serves it.</para>
/// </remarks>
public sealed class LongRunningAttribute : JobFilterAttribute, IElectStateFilter
{
    /// <summary>The queue of long-running jobs.</summary>
    public const string Queue = "long-running";

    private const string NoLocalWorkerWarning =
        $"A [LongRunning] job was sent to the '{Queue}' queue, which no server of this process serves: set " +
        "BackgroundJobServerOptions.LongRunningWorkerCount above 0, or add the queue to Queues. Unless a server in " +
        "another process serves it, the job waits in the queue. This warning is logged once.";

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(LongRunningAttribute));

    // Whether this process created workers, whether one of them serves the queue, and whether the warning was logged
    private static volatile bool _localWorkers;
    private static volatile bool _localLongRunningWorkers;
    private static int _warned;

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
            WarnWhenNoLocalWorkerServesTheQueue();
        }
    }

    /// <summary>Notes a worker created in this process, and whether it serves the queue.</summary>
    internal static void NoteLocalWorker([NotNull] IEnumerable<string> queues)
    {
        if (queues.Contains(Queue, StringComparer.OrdinalIgnoreCase))
        {
            _localLongRunningWorkers = true;
        }

        _localWorkers = true;
    }

    /// <summary>Forgets the workers noted and the warning, for tests.</summary>
    internal static void ResetLocalWorkers()
    {
        _localLongRunningWorkers = false;
        _localWorkers = false;
        Interlocked.Exchange(ref _warned, 0);
    }

    // A process without workers only creates jobs, and can't know about the servers elsewhere: it doesn't warn
    private static void WarnWhenNoLocalWorkerServesTheQueue()
    {
        if (!_localWorkers || _localLongRunningWorkers || Volatile.Read(ref _warned) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            Logger.Warn(NoLocalWorkerWarning);
        }
    }
}
