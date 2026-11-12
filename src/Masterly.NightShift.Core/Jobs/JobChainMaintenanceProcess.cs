using Masterly.NightShift.Client;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Keeps the chains tidy, on one server at a time: every few minutes it removes from the list of chains the ones
/// whose record expired, and finishes what an interrupted <c>RetryChainFrom</c> or <c>ReplaceChain</c> left to do
/// (replaced jobs to delete, a held step to enqueue). Each run does a bounded amount of work. It needs a storage
/// with expiring hashes and ranges of sets (<see cref="JobStorageFeatures.ExtendedApi"/>) and does nothing without.
/// </summary>
internal sealed class JobChainMaintenanceProcess(IBackgroundJobFactory factory, IBackgroundJobStateChanger stateChanger) : IBackgroundProcess
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobChainMaintenanceProcess));

    // Where the last run stopped in the list of chains. One thread runs the process, so nothing guards it.
    private double _listPosition;

    /// <summary>The time between two runs. Replaceable for tests.</summary>
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    public void Execute(BackgroundProcessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            JobChains.Maintain(context.Storage, new BackgroundJobClient(context.Storage, factory, stateChanger), ref _listPosition);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // The next run starts where this one did
            Logger.WarnException("The chains couldn't be maintained; the next run tries again.", ex);
        }

        context.Wait(Interval);
    }

    public override string ToString() => "Job chain maintenance";
}
