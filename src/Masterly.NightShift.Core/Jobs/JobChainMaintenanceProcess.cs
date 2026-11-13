using Masterly.NightShift.Client;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Keeps the chains tidy, on one server at a time: every few minutes it finishes what an interrupted
/// <c>RetryChainFrom</c> or <c>ReplaceChain</c> left to do (replaced jobs to delete, a held step to enqueue) and looks
/// at the chains that are due for a look. It starts their waiting steps that were never told to start, keeps their
/// record for as long as a step is left to run or failed for good, and removes from the list of chains the ones
/// whose record expired (on a storage with <see cref="JobStorageFeatures.ExtendedApi"/>: nothing expires without).
/// A run reads only the chains that are due, a bounded number of them, and the next comes sooner when work is left.
/// </summary>
internal sealed class JobChainMaintenanceProcess(IBackgroundJobFactory factory, IBackgroundJobStateChanger stateChanger) : IBackgroundProcess
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobChainMaintenanceProcess));

    // Whether every listed chain is known to have its place among the chains to look at: checked in the storage
    // until it is so. One thread runs the process, so nothing guards it.
    private bool _indexed;

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
            moreToDo = JobChains.Maintain(context.Storage, new BackgroundJobClient(context.Storage, factory, stateChanger), ref _indexed, context.StoppingToken).MoreToDo;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // The next run finds the same chains due
            Logger.WarnException("The chains couldn't be maintained; the next run tries again.", ex);
        }

        // Each run does a bounded amount of work; with more left, the next one comes sooner
        context.Wait(moreToDo ? BusyInterval : Interval);
    }

    public override string ToString() => "Job chain maintenance";
}
