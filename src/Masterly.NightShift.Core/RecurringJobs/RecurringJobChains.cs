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

            if (previous != null && storage.GetChain(previous) is { IsSettled: false })
            {
                throw new TerminateJobException($"the previous chain '{previous}' is still running");
            }
        }

        JobChainResult? result = JobChains.FromDefinition(definition).Enqueue(new BackgroundJobClient(storage));
        if (result == null) return null;

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
