using System.Globalization;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift;

/// <summary>
/// One run of a recurring job, as <c>storage.GetRecurringJobRuns</c> (<see cref="RecurringJobHistory"/>) reads it.
/// </summary>
/// <param name="JobId">The id of the job the run created.</param>
/// <param name="State">The job's current state, or <c>null</c> when the job expired and is gone.</param>
/// <param name="CreatedAt">When the job was created, in UTC, or <c>null</c> when it expired.</param>
/// <param name="ScheduledFor">The occurrence the run was created for, in UTC, or <c>null</c> when unknown.</param>
/// <param name="StartedAt">When the last attempt started, in UTC, or <c>null</c> when it never started.</param>
/// <param name="Duration">How long the job method ran when the job succeeded, or <c>null</c>.</param>
/// <param name="RetryCount">How many times the job was retried.</param>
/// <param name="ServerId">The server that ran the last attempt, or <c>null</c>.</param>
/// <param name="Reason">The reason of the current state, such as why a run was skipped, or <c>null</c>.</param>
public sealed record RecurringJobRun(
    [NotNull] string JobId,
    [CanBeNull] string? State,
    DateTime? CreatedAt,
    DateTime? ScheduledFor,
    DateTime? StartedAt,
    TimeSpan? Duration,
    int RetryCount,
    [CanBeNull] string? ServerId,
    [CanBeNull] string? Reason)
{
    /// <summary>Gets whether the job is gone from the storage: it finished and expired.</summary>
    public bool Expired => State == null;
}

/// <summary>
/// The runs of each recurring job: the jobs it created, newest first. The scheduler and <c>Trigger</c> record each
/// run in the storage list <see cref="GetKey"/>, which keeps the last <see cref="MaxRuns"/> and is removed with the
/// recurring job.
/// </summary>
public static class RecurringJobHistory
{
    /// <summary>How many runs are kept for each recurring job: 50.</summary>
    public const int MaxRuns = 50;

    // SQL Server keeps list keys in 100 characters
    private const int MaxKeyLength = 100;

    /// <summary>Gets the key of the storage list with the job ids of a recurring job's runs, newest first.</summary>
    /// <param name="recurringJobId">The recurring job's id.</param>
    /// <returns><c>recurring-job:{id}:runs</c>, shortened with a hash when longer than 100 characters.</returns>
    [NotNull]
    public static string GetKey([NotNull] string recurringJobId)
    {
        ArgumentNullException.ThrowIfNull(recurringJobId);

        return ConcurrencyControl.ShortenResource($"recurring-job:{recurringJobId}:runs", MaxKeyLength);
    }

    internal static void RecordRun(IWriteOnlyTransaction transaction, string recurringJobId, string jobId)
    {
        string key = GetKey(recurringJobId);

        transaction.InsertToList(key, jobId);
        transaction.TrimList(key, 0, MaxRuns - 1);
    }

    internal static void Remove(IWriteOnlyTransaction transaction, string recurringJobId)
    {
        // Keeping nothing removes the list, on every storage
        transaction.TrimList(GetKey(recurringJobId), 1, 0);
    }

    /// <param name="storage">The storage.</param>
    extension([NotNull] JobStorage storage)
    {
        /// <summary>
        /// Reads the runs of a recurring job, newest first: each job it created, with its state, timing and retries.
        /// A job that expired is listed with <see cref="RecurringJobRun.Expired"/>.
        /// </summary>
        /// <param name="recurringJobId">The recurring job's id.</param>
        /// <param name="count">How many runs to read, from 1 to <see cref="MaxRuns"/>.</param>
        /// <returns>The runs, empty when there are none or the storage can't read lists by range
        /// (<see cref="JobStorageFeatures.ExtendedApi"/>).</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 1 to <see cref="MaxRuns"/>.</exception>
        /// <remarks>Each run is read with the monitoring API's job details, one query per run.</remarks>
        [NotNull]
        public IReadOnlyList<RecurringJobRun> GetRecurringJobRuns([NotNull] string recurringJobId, int count = MaxRuns)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentNullException.ThrowIfNull(recurringJobId);
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxRuns);

            if (!storage.HasFeature(JobStorageFeatures.ExtendedApi)) return [];

            List<string> jobIds;
            using (IStorageConnection connection = storage.GetReadOnlyConnection())
            {
                if (connection is not JobStorageConnection extended) return [];
                jobIds = extended.GetRangeFromList(GetKey(recurringJobId), 0, count - 1);
            }

            IMonitoringApi monitor = storage.GetMonitoringApi();
            return [.. jobIds.Select(jobId => ReadRun(monitor, jobId))];
        }
    }

    private static RecurringJobRun ReadRun(IMonitoringApi monitor, string jobId)
    {
        JobDetailsDto? details = monitor.JobDetails(jobId);
        if (details == null) return new RecurringJobRun(jobId, null, null, null, null, null, 0, null, null);

        StateHistoryDto? current = details.History?.MaxBy(static state => state.CreatedAt);
        StateHistoryDto? processing = details.History?
            .Where(static state => ProcessingState.StateName.Equals(state.StateName, StringComparison.OrdinalIgnoreCase))
            .MaxBy(static state => state.CreatedAt);

        TimeSpan? duration = null;
        if (current != null &&
            SucceededState.StateName.Equals(current.StateName, StringComparison.OrdinalIgnoreCase) &&
            TryGetValue(current.Data, "PerformanceDuration", out string? milliseconds) &&
            long.TryParse(milliseconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out long durationMs))
        {
            duration = TimeSpan.FromMilliseconds(durationMs);
        }

        DateTime? scheduledFor = null;
        if (TryGetValue(details.Properties, PerformContext.RecurringTimeParameter, out string? time) &&
            long.TryParse(time, NumberStyles.Integer, CultureInfo.InvariantCulture, out long timestamp))
        {
            scheduledFor = JobHelper.FromTimestamp(timestamp);
        }

        int retryCount = TryGetValue(details.Properties, "RetryCount", out string? retries) &&
            int.TryParse(retries, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;

        string? serverId = processing != null && TryGetValue(processing.Data, "ServerId", out string? server) ? server : null;

        return new RecurringJobRun(
            jobId,
            current?.StateName,
            details.CreatedAt,
            scheduledFor,
            processing?.CreatedAt,
            duration,
            retryCount,
            serverId,
            current?.Reason);
    }

    private static bool TryGetValue(IDictionary<string, string>? values, string key, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (values == null) return false;

        if (values.TryGetValue(key, out value)) return true;

        foreach (KeyValuePair<string, string> entry in values)
        {
            if (key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        return false;
    }
}
