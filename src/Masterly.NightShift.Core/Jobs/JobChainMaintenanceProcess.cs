using Masterly.NightShift.Client;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Keeps the chains tidy, on one server at a time: every few minutes it removes from the list of chains the ones
/// whose record expired (on a storage with <see cref="JobStorageFeatures.ExtendedApi"/>: nothing expires without),
/// finishes what an interrupted <c>RetryChainFrom</c> or <c>ReplaceChain</c> left to do (replaced jobs to delete, a
/// held step to enqueue), and starts the steps that wait for several steps and were never told to start. Each run
/// does a bounded amount of work, and the next comes sooner when work is left.
/// </summary>
internal sealed class JobChainMaintenanceProcess(IBackgroundJobFactory factory, IBackgroundJobStateChanger stateChanger) : IBackgroundProcess
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobChainMaintenanceProcess));

    // Where the last run stopped in the list of chains. One thread runs the process, so nothing guards it.
    private double _listPosition;

    /// <summary>The time between two runs. Replaceable for tests.</summary>
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The time until the next run when one stopped at a limit with work left. Replaceable for tests.</summary>
    internal static TimeSpan BusyInterval { get; set; } = TimeSpan.FromSeconds(10);

    public void Execute(BackgroundProcessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        bool moreToDo = false;

        try
        {
            moreToDo = JobChains.Maintain(context.Storage, new BackgroundJobClient(context.Storage, factory, stateChanger), ref _listPosition).MoreToDo;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // The next run starts where this one did
            Logger.WarnException("The chains couldn't be maintained; the next run tries again.", ex);
        }

        // Each run does a bounded amount of work; with more left, the next one comes sooner
        context.Wait(moreToDo ? BusyInterval : Interval);
    }

    public override string ToString() => "Job chain maintenance";
}
