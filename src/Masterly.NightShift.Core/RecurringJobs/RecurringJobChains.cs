using System.ComponentModel;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Recurring chains: a <see cref="JobChain"/> created afresh at each occurrence of a schedule.
/// <code>
/// RecurringJob.AddOrUpdateChain(
///     "nightly-etl",
///     JobChain.StartWith&lt;Etl&gt;(x => x.Extract()).Then&lt;Etl&gt;(x => x.Transform()).Then&lt;Etl&gt;(x => x.Load()),
///     Cron.Daily(2));
/// </code>
/// </summary>
/// <remarks>
/// The recurring job runs <see cref="JobChainRunner.Run"/>, which creates the chain and returns its identifier. With
/// <c>skipWhileRunning</c>, an occurrence whose previous chain hasn't finished is skipped (deleted with the reason
/// <c>Skipped: ...</c>). The chain's definition is stored as the recurring job's argument, so the recurring job is
/// registered again when the chain changes.
/// </remarks>
public static class RecurringJobChains
{
    private const int MaxKeyLength = 100;

    /// <param name="manager">The manager.</param>
    extension([NotNull] IRecurringJobManager manager)
    {
        /// <summary>Registers or updates a recurring job that creates a chain at each occurrence.</summary>
        /// <param name="recurringJobId">The recurring job's id.</param>
        /// <param name="chain">The chain to create.</param>
        /// <param name="cronExpression">The schedule.</param>
        /// <param name="options">The recurring job's options, or <c>null</c> for the defaults.</param>
        /// <param name="skipWhileRunning">Whether an occurrence is skipped while the previous chain hasn't finished.</param>
        public void AddOrUpdateChain(
            [NotNull] string recurringJobId,
            [NotNull] JobChain chain,
            [NotNull] string cronExpression,
            [CanBeNull] RecurringJobOptions? options = null,
            bool skipWhileRunning = false)
        {
            ArgumentNullException.ThrowIfNull(manager);
            ArgumentNullException.ThrowIfNull(recurringJobId);
            ArgumentNullException.ThrowIfNull(chain);
            ArgumentNullException.ThrowIfNull(cronExpression);

            string definition = JobChains.ToDefinition(chain);
            manager.AddOrUpdate(
                recurringJobId,
                Job.FromExpression(() => JobChainRunner.Run(definition, skipWhileRunning, null!)), // The performer passes the context
                cronExpression,
                options ?? new RecurringJobOptions());
        }
    }

    /// <summary>The hash that keeps the last chain a recurring chain created.</summary>
    internal static string GetKey(string recurringJobId)
    {
        return ConcurrencyControl.ShortenResource($"recurring-chain:{recurringJobId}", MaxKeyLength);
    }
}

/// <summary>The job a recurring chain runs: it creates the chain. Not meant to be called directly.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class JobChainRunner
{
    private const string LastChainField = "LastChainId";

    /// <summary>Creates the chain of a recurring chain's occurrence.</summary>
    /// <param name="definition">The chain, as stored by <c>AddOrUpdateChain</c>.</param>
    /// <param name="skipWhileRunning">Whether to skip the occurrence while the previous chain hasn't finished.</param>
    /// <param name="context">The run, passed by the server.</param>
    /// <returns>The identifier of the chain created.</returns>
    /// <exception cref="TerminateJobException">The previous chain hasn't finished and <paramref name="skipWhileRunning"/> is set.</exception>
    [DisplayName("Recurring chain")]
    public static string? Run([NotNull] string definition, bool skipWhileRunning, [NotNull] PerformContext context)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);

        JobStorage storage = context.Storage ?? JobStorage.Current;
        string? recurringJobId = context.RecurringJobId;
        string? key = recurringJobId != null ? RecurringJobChains.GetKey(recurringJobId) : null;

        if (skipWhileRunning && key != null)
        {
            string? previous;
            using (IStorageConnection connection = storage.GetConnection())
            {
                previous = connection.GetAllEntriesFromHash(key)?.GetValueOrDefault(LastChainField);
            }

            // Not the chain of this very job, run again after it recorded its chain: that one isn't a previous one
            if (previous != null && previous != JobChains.IdForJob(context.BackgroundJob.Id))
            {
                // A step of the previous chain that was to start long ago and never was told is started now: the
                // chain then still runs, and this occurrence is skipped for it rather than run beside it
                JobChains.RedriveLostDecisions(storage, new BackgroundJobClient(storage), previous);

                // Read without loading the methods of its steps: a deploy that renamed or removed one doesn't
                // keep the next occurrences from being decided
                if (JobChains.IsRunning(storage, previous))
                {
                    throw new TerminateJobException($"the previous chain '{previous}' is still running");
                }
            }
        }

        // One chain for this occurrence, whose identifier comes from the job that runs it: a job run again after
        // its chain was recorded (its process stopped before it finished) finds that chain instead of creating
        // a second one
        JobChainResult? result = JobChains.CreateOnce(
            JobChains.FromDefinition(definition),
            new BackgroundJobClient(storage),
            JobChains.IdForJob(context.BackgroundJob.Id));

        if (result == null)
        {
            return null;
        }

        if (key != null)
        {
            using IStorageConnection connection = storage.GetConnection();
            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            transaction.SetRangeInHash(key, new Dictionary<string, string> { [LastChainField] = result.ChainId });
            if (transaction is JobStorageTransaction storageTransaction && storage.HasFeature(JobStorageFeatures.ExtendedApi))
            {
                storageTransaction.ExpireHash(key, JobChain.RecordRetention);
            }

            transaction.Commit();
        }

        return result.ChainId;
    }
}
