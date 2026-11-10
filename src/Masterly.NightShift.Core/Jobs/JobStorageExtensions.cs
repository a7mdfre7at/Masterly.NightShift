using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift;

/// <summary>
/// Reads a job's state and result, and requeues failed jobs in bulk, from code.
/// </summary>
public static class JobStorageExtensions
{
    private const string RequeueReason = "Requeued with RequeueFailedJobs";
    private const string ResultKey = "Result";

    /// <param name="storage">The storage.</param>
    extension([NotNull] JobStorage storage)
    {
        /// <summary>
        /// Requeues the failed jobs of the storage, or those <paramref name="filter"/> selects, like "Requeue all" on
        /// the dashboard's Failed page. The jobs are changed with a new <see cref="BackgroundJobClient"/> for
        /// <paramref name="storage"/>; pass a client to <see cref="RequeueFailedJobs(JobStorage, IBackgroundJobClient, Func{FailedJobDto, bool}, TimeSpan?)"/>
        /// to use another one, such as the one registered in the service container.
        /// </summary>
        /// <param name="filter">Selects the jobs to requeue, or <c>null</c> to requeue every failed job.</param>
        /// <param name="timeBudget">
        /// How long the call may take, or <c>null</c> (the default) to go on until the end of the failed list. When the
        /// time is up, the result isn't <see cref="BulkJobResult.Complete"/>, and another call goes on with the jobs left.
        /// </param>
        /// <returns>How many jobs were requeued, and whether the whole list was read.</returns>
        public BulkJobResult RequeueFailedJobs(
            [CanBeNull, InstantHandle] Func<FailedJobDto, bool>? filter = null,
            TimeSpan? timeBudget = null)
        {
            ArgumentNullException.ThrowIfNull(storage);

            return RequeueFailedJobs(storage, new BackgroundJobClient(storage), filter, timeBudget);
        }

        /// <summary>
        /// Requeues the failed jobs of the storage, or those <paramref name="filter"/> selects, through
        /// <paramref name="client"/>, like "Requeue all" on the dashboard's Failed page. The failed list is read a page
        /// at a time through the monitoring API, and each job is requeued only if it is still failed when its turn
        /// comes. A job that fails again while the call runs isn't requeued a second time.
        /// </summary>
        /// <param name="client">The client that changes the states.</param>
        /// <param name="filter">Selects the jobs to requeue, or <c>null</c> to requeue every failed job.</param>
        /// <param name="timeBudget">
        /// How long the call may take, or <c>null</c> (the default) to go on until the end of the failed list. When the
        /// time is up, the result isn't <see cref="BulkJobResult.Complete"/>, and another call goes on with the jobs left.
        /// </param>
        /// <returns>How many jobs were requeued, and whether the whole list was read.</returns>
        public BulkJobResult RequeueFailedJobs(
            [NotNull] IBackgroundJobClient client,
            [CanBeNull, InstantHandle] Func<FailedJobDto, bool>? filter = null,
            TimeSpan? timeBudget = null)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentNullException.ThrowIfNull(client);
            if (timeBudget < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeBudget), "The time budget can't be negative.");

            IMonitoringApi monitor = storage.GetMonitoringApi();

            return BulkStateChange.RunOnPages(
                id => client.ChangeState(id, new EnqueuedState { Reason = RequeueReason }, FailedState.StateName),
                (from, count) => monitor.FailedJobs(from, count)?.Select(item => new KeyValuePair<string, bool>(
                    item.Key,
                    item.Value != null && item.Value.InFailedState && (filter == null || filter(item.Value)))),
                monitor.FailedCount,
                BulkStateChange.Unlimited,
                timeBudget);
        }

        /// <summary>
        /// Reads a job's current state: its name, reason and data, with the job's creation time.
        /// </summary>
        /// <param name="jobId">The job's id.</param>
        /// <returns>The state, or <c>null</c> when the job doesn't exist (or expired) or has no state yet.</returns>
        [CanBeNull]
        public JobStateInfo? GetJobState([NotNull] string jobId)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentNullException.ThrowIfNull(jobId);

            using IStorageConnection connection = storage.GetReadOnlyConnection();
            JobData? jobData = connection.GetJobData(jobId);
            if (jobData == null) return null;

            StateData? stateData = connection.GetStateData(jobId);
            if (stateData == null) return null;

            Dictionary<string, string> data = new(StringComparer.OrdinalIgnoreCase);
            if (stateData.Data != null)
            {
                foreach (KeyValuePair<string, string> entry in stateData.Data)
                {
                    data[entry.Key] = entry.Value;
                }
            }

            return new JobStateInfo(stateData.Name, stateData.Reason, data, jobData.CreatedAt);
        }

        /// <summary>
        /// Reads the value a job returned, when the job has succeeded. The value is read from the <c>Result</c> of its
        /// <c>Succeeded</c> state with the serializer options set by <c>UseSerializerSettings</c> (read leniently),
        /// as <see cref="FromResultAttribute"/> reads it.
        /// </summary>
        /// <typeparam name="T">The type to read the value as.</typeparam>
        /// <param name="jobId">The job's id.</param>
        /// <param name="result">The value, or the default of <typeparamref name="T"/> when the method returns <c>false</c>.</param>
        /// <returns>
        /// <c>true</c> when the job has succeeded and stored a value; <c>false</c> when the job doesn't exist, isn't
        /// in the <c>Succeeded</c> state, returned nothing (<c>void</c>, <c>Task</c> or <c>null</c>), or returned a
        /// value that couldn't be serialized.
        /// </returns>
        /// <exception cref="System.Text.Json.JsonException">The stored value can't be read as <typeparamref name="T"/>.</exception>
        public bool TryGetResult<T>([NotNull] string jobId, out T? result)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentNullException.ThrowIfNull(jobId);

            result = default;
            StateData? stateData;

            using (IStorageConnection connection = storage.GetReadOnlyConnection())
            {
                stateData = connection.GetStateData(jobId);
            }

            if (stateData?.Data == null || !SucceededState.StateName.Equals(stateData.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string serialized = stateData.Data.TryGetValue(ResultKey, out string? value)
                ? value
                : stateData.Data.FirstOrDefault(static entry => ResultKey.Equals(entry.Key, StringComparison.OrdinalIgnoreCase)).Value;

            if (string.IsNullOrWhiteSpace(serialized) || serialized == SucceededState.UnserializableResult)
            {
                return false;
            }

            result = SerializationHelper.Deserialize<T>(serialized, SerializationOption.User);
            return true;
        }
    }
}
