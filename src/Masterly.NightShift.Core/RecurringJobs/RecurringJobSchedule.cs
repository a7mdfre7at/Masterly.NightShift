namespace Masterly.NightShift;

/// <summary>Reads when a stored recurring job runs next, from code.</summary>
public static class RecurringJobSchedule
{
    /// <summary>The most occurrences one call returns: 1,000.</summary>
    public const int MaxOccurrences = 1000;

    /// <param name="storage">The storage.</param>
    extension([NotNull] JobStorage storage)
    {
        /// <summary>
        /// Lists the next runs of a stored recurring job, as its CRON expression, time zone, start and end dates and
        /// schedule policy give them, starting after the current time. Occurrences its policy blocks are left out. A
        /// paused job, a job past its end and a job that doesn't exist have none.
        /// </summary>
        /// <param name="recurringJobId">The recurring job's id.</param>
        /// <param name="count">How many runs to list, from 1 to <see cref="MaxOccurrences"/>.</param>
        /// <returns>The UTC instants of the next runs, earliest first.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 1 to <see cref="MaxOccurrences"/>.</exception>
        /// <exception cref="TimeZoneNotFoundException">The job's time zone isn't known on this machine.</exception>
        [NotNull]
        public IReadOnlyList<DateTime> GetNextOccurrences([NotNull] string recurringJobId, int count)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentNullException.ThrowIfNull(recurringJobId);
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxOccurrences);

            // The connection stays open while the policy is read
            using IStorageConnection connection = storage.GetReadOnlyConnection();
            RecurringJobEntity? recurringJob = connection.GetRecurringJob(recurringJobId);

            if (recurringJob?.Cron == null || recurringJob.Paused) return [];

            return GetNextOccurrences(recurringJob, GlobalTimeZoneResolver.Current, JobClock.UtcNow, count);
        }
    }

    internal static IReadOnlyList<DateTime> GetNextOccurrences(RecurringJobEntity recurringJob, ITimeZoneResolver timeZoneResolver, DateTime now, int count)
    {
        TimeZoneInfo timeZone = timeZoneResolver.GetTimeZoneById(recurringJob.TimeZoneId);

        // Before the window opens, the first run is the first occurrence at or after its start
        DateTime from = recurringJob.StartAt.HasValue && recurringJob.StartAt.Value > now
            ? recurringJob.StartAt.Value.AddTicks(-1)
            : now;

        if (recurringJob.Policy != null && recurringJob.PolicySource != null)
        {
            // From the instant after "from", as Cron.GetNextOccurrences counts
            return [.. recurringJob.GetNextOccurrences(timeZoneResolver, from.AddTicks(1)).Take(count)];
        }

        List<DateTime> result = [];

        foreach (DateTime occurrence in Cron.GetNextOccurrences(recurringJob.Cron!, timeZone, from, count)) // Checked by the caller
        {
            if (recurringJob.EndAt.HasValue && occurrence > recurringJob.EndAt.Value) break;
            result.Add(occurrence);
        }

        return result;
    }
}
