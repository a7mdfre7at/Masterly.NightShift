using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

/// <summary>
/// Stores, reads and evaluates <see cref="SchedulePolicy"/> business calendars. A policy is kept in the hash
/// <c>schedule-policy:{name}</c> and listed in the set <c>schedule-policies</c>, with a revision raised by every
/// change. Saving or changing a policy reschedules the recurring jobs that follow it.
/// </summary>
public static class SchedulePolicies
{
    /// <summary>The set that lists the names of the stored policies.</summary>
    public const string SetKey = "schedule-policies";

    /// <summary>The most occurrences <c>PreviewSchedulePolicy</c> lists.</summary>
    public const int MaxPreviewCount = 1000;

    private const string KeyPrefix = "schedule-policy:";
    private const string RecurringJobsSetKey = "recurring-jobs";

    // One lock for every save and delete: a save checks the policies it refers to, a delete the policies that refer
    // to the deleted one, so a lock on the policy alone would let a save and a delete of two related policies cross
    private const string LockResource = "lock:schedule-policies";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(SchedulePolicies));

    /// <summary>The hash that stores the policy of this name.</summary>
    public static string GetKey([NotNull] string name) => KeyPrefix + SchedulePolicy.NormalizeName(name, nameof(name));

    /// <param name="storage">The storage.</param>
    extension([NotNull] JobStorage storage)
    {
        /// <summary>Lists the stored policies, by name.</summary>
        /// <exception cref="FormatException">A stored policy can't be read.</exception>
        [NotNull]
        public IReadOnlyList<SchedulePolicy> GetSchedulePolicies()
        {
            ArgumentNullException.ThrowIfNull(storage);

            using IStorageConnection connection = storage.GetReadOnlyConnection();
            List<SchedulePolicy> result = [];

            foreach (string name in connection.GetAllItemsFromSet(SetKey).Order(StringComparer.Ordinal))
            {
                if (Read(connection, name) is { } policy)
                {
                    result.Add(policy);
                }
            }

            return result;
        }

        /// <summary>Reads a stored policy, with its <see cref="SchedulePolicy.Revision"/>; <c>null</c> when there is none of this name.</summary>
        /// <exception cref="FormatException">The stored policy can't be read.</exception>
        [CanBeNull]
        public SchedulePolicy? GetSchedulePolicy([NotNull] string name)
        {
            ArgumentNullException.ThrowIfNull(storage);
            string key = GetKey(name);

            using IStorageConnection connection = storage.GetReadOnlyConnection();
            return Read(connection, key[KeyPrefix.Length..]);
        }

        /// <summary>
        /// Stores a policy, creating it or replacing the stored one whatever its revision, as declarations in code
        /// do. Nothing is written when the stored policy has the same rules. Sets the policy's
        /// <see cref="SchedulePolicy.Revision"/> and returns it.
        /// </summary>
        /// <remarks>
        /// A policy that changes reschedules the recurring jobs that follow it, directly or through other policies.
        /// Finding them reads every recurring job once, so a change costs one read per recurring job, plus one
        /// write per job that follows the policy; the policies themselves are read once per call.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// A policy it requires or makes an exception for isn't stored, or the references form a cycle.
        /// </exception>
        public long SaveSchedulePolicy([NotNull] SchedulePolicy policy)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentNullException.ThrowIfNull(policy);

            return Write(storage, policy, expectedRevision: null, out _)!.Value; // Only a revision check returns null
        }

        /// <summary>
        /// Stores a policy only when the stored one still has <paramref name="expectedRevision"/>, so two people
        /// editing it don't overwrite each other's changes: pass the <see cref="SchedulePolicy.Revision"/> read with
        /// it, or 0 to create a policy that must not exist yet. Returns the new revision, or <c>null</c> when the
        /// stored revision is another one (the policy was changed, created or deleted meanwhile).
        /// </summary>
        /// <remarks>
        /// A policy that changes reschedules the recurring jobs that follow it, directly or through other policies.
        /// Finding them reads every recurring job once, so a change costs one read per recurring job, plus one
        /// write per job that follows the policy; the policies themselves are read once per call.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// A policy it requires or makes an exception for isn't stored, or the references form a cycle.
        /// </exception>
        public long? UpdateSchedulePolicy([NotNull] SchedulePolicy policy, long expectedRevision)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);

            return Write(storage, policy, expectedRevision, out _);
        }

        /// <summary>
        /// Deletes a policy when the stored one still has <paramref name="expectedRevision"/>. Returns <c>false</c>
        /// when there is no such policy or it has another revision. Jobs already scheduled with it run without it.
        /// </summary>
        /// <remarks>Checking that nothing uses it reads every stored policy and every recurring job.</remarks>
        /// <exception cref="InvalidOperationException">A recurring job or another policy uses it.</exception>
        public bool DeleteSchedulePolicy([NotNull] string name, long expectedRevision)
        {
            ArgumentNullException.ThrowIfNull(storage);
            string key = GetKey(name);
            string normalized = key[KeyPrefix.Length..];

            using IStorageConnection connection = storage.GetConnection();
            using (connection.AcquireDistributedLock(LockResource, LockTimeout))
            {
                if (ReadRevision(connection, normalized) != expectedRevision)
                {
                    return false;
                }

                List<string> users = FindUsers(connection, normalized);
                if (users.Count > 0)
                {
                    throw new InvalidOperationException($"Schedule policy '{normalized}' can't be deleted while {string.Join(", ", users)} use it.");
                }

                using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
                transaction.RemoveHash(key);
                transaction.RemoveFromSet(SetKey, normalized);
                transaction.Commit();
                return true;
            }
        }

        /// <summary>Tells whether a stored policy allows a job to start at an instant, and why not.</summary>
        /// <param name="name">The policy's name.</param>
        /// <param name="instant">The instant to check; a time that isn't UTC is converted.</param>
        /// <exception cref="ArgumentException">There is no policy of this name.</exception>
        [NotNull]
        public SchedulePolicyDecision EvaluateSchedulePolicy([NotNull] string name, DateTime instant)
        {
            ArgumentNullException.ThrowIfNull(storage);

            return LoadCalendar(storage, name).Evaluate(ToUtc(instant));
        }

        /// <summary>
        /// The first instant at or after <paramref name="from"/> that a stored policy allows, or <c>null</c> when it
        /// allows none in the next ten years.
        /// </summary>
        /// <exception cref="ArgumentException">There is no policy of this name.</exception>
        public DateTime? FindNextAllowed([NotNull] string name, DateTime from)
        {
            ArgumentNullException.ThrowIfNull(storage);

            return LoadCalendar(storage, name).FindNextAllowed(ToUtc(from));
        }

        /// <summary>
        /// Lists the next occurrences of a CRON expression after the current time, each with whether a stored policy
        /// allows it and why not: what a recurring job with this schedule and policy would do. Blocked occurrences
        /// don't run.
        /// </summary>
        /// <param name="name">The policy's name.</param>
        /// <param name="cronExpression">The schedule.</param>
        /// <param name="timeZone">The schedule's time zone, UTC when <c>null</c>.</param>
        /// <param name="count">How many occurrences, from 1 to <see cref="MaxPreviewCount"/>.</param>
        /// <exception cref="ArgumentException">There is no policy of this name, or the CRON expression is invalid.</exception>
        [NotNull]
        public IReadOnlyList<SchedulePolicyDecision> PreviewSchedulePolicy(
            [NotNull] string name,
            [NotNull] string cronExpression,
            [CanBeNull] TimeZoneInfo? timeZone = null,
            int count = 20)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentNullException.ThrowIfNull(cronExpression);
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxPreviewCount);

            IEnumerable<DateTime> occurrences;
            try
            {
                occurrences = Cron.GetNextOccurrences(cronExpression, timeZone ?? TimeZoneInfo.Utc, JobClock.UtcNow, count);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType() && ex is not ArgumentException)
            {
                throw new ArgumentException($"'{cronExpression}' isn't a valid CRON expression.", nameof(cronExpression), ex);
            }

            SchedulePolicyCalendar calendar = LoadCalendar(storage, name);
            return [.. occurrences.Select(calendar.Evaluate)];
        }
    }

    internal static SchedulePolicy? Read(IStorageConnection connection, string normalizedName)
    {
        Dictionary<string, string>? fields = connection.GetAllEntriesFromHash(KeyPrefix + normalizedName);
        return fields == null || fields.Count == 0 ? null : SchedulePolicy.FromFields(normalizedName, fields);
    }

    /// <summary>Whether a policy of this name is stored.</summary>
    internal static bool Exists(IStorageConnection connection, string normalizedName)
    {
        Dictionary<string, string>? fields = connection.GetAllEntriesFromHash(KeyPrefix + normalizedName);
        return fields is { Count: > 0 };
    }

    private static long? ReadRevision(IStorageConnection connection, string normalizedName)
    {
        Dictionary<string, string>? fields = connection.GetAllEntriesFromHash(KeyPrefix + normalizedName);
        if (fields == null || fields.Count == 0)
        {
            return null;
        }

        // A policy that can't be read can still be replaced: its revision is all a write needs
        return fields.TryGetValue(SchedulePolicy.RevisionField, out string? value) &&
               long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long revision)
            ? revision
            : 0;
    }

    private static SchedulePolicyCalendar LoadCalendar(JobStorage storage, string name)
    {
        string normalized = GetKey(name)[KeyPrefix.Length..];

        using IStorageConnection connection = storage.GetReadOnlyConnection();
        return SchedulePolicyCalendar.Load(connection, normalized, GlobalTimeZoneResolver.Current)
            ?? throw new ArgumentException($"There is no schedule policy named '{normalized}'.", nameof(name));
    }

    private static DateTime ToUtc(DateTime instant)
    {
        return instant.Kind switch
        {
            DateTimeKind.Local => instant.ToUniversalTime(),
            _ => DateTime.SpecifyKind(instant, DateTimeKind.Utc),
        };
    }

    /// <summary>Stores a policy; <paramref name="written"/> tells whether it changed anything.</summary>
    internal static long? Write(JobStorage storage, SchedulePolicy policy, long? expectedRevision, out bool written)
    {
        DateTime now = JobClock.UtcNow;
        long revision;
        written = false;

        using (IStorageConnection connection = storage.GetConnection())
        {
            using (connection.AcquireDistributedLock(LockResource, LockTimeout))
            {
                long? current = ReadRevision(connection, policy.Name);
                if (expectedRevision.HasValue && (current ?? 0) != expectedRevision.Value)
                {
                    return null;
                }

                SchedulePolicy? stored = ReadOrNull(connection, policy.Name);
                if (stored != null && stored.HasSameRules(policy))
                {
                    policy.Revision = stored.Revision;
                    policy.UpdatedAt = stored.UpdatedAt;
                    return stored.Revision;
                }

                CheckReferences(connection, policy);

                revision = (current ?? 0) + 1;
                Dictionary<string, string> fields = policy.ToFields();
                fields[SchedulePolicy.RevisionField] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                fields[SchedulePolicy.UpdatedAtField] = JobHelper.SerializeDateTime(now);

                using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
                transaction.SetRangeInHash(KeyPrefix + policy.Name, fields);
                transaction.AddToSet(SetKey, policy.Name);
                transaction.Commit();
                written = true;
            }

            policy.Revision = revision;
            policy.UpdatedAt = JobHelper.DeserializeDateTime(JobHelper.SerializeDateTime(now));

            RescheduleRecurringJobs(connection, policy.Name, now);
        }

        return revision;
    }

    private static SchedulePolicy? ReadOrNull(IStorageConnection connection, string name)
    {
        try
        {
            return Read(connection, name);
        }
        catch (FormatException)
        {
            // A policy that can't be read is replaced
            return null;
        }
    }

    // The policies it refers to must exist, and following the references must never lead back to it
    private static void CheckReferences(IStorageConnection connection, SchedulePolicy policy)
    {
        foreach (string reference in policy.Required.Concat(policy.Excepted))
        {
            if (!Exists(connection, reference))
            {
                throw new ArgumentException($"Schedule policy '{policy.Name}' refers to '{reference}', which isn't stored. Save '{reference}' first.", nameof(policy));
            }
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        Stack<(string Name, string Path)> pending = new(policy.Required.Concat(policy.Excepted).Select(x => (x, $"{policy.Name} -> {x}")));

        while (pending.Count > 0)
        {
            (string name, string path) = pending.Pop();
            if (name == policy.Name)
            {
                throw new ArgumentException($"Schedule policy '{policy.Name}' would form a cycle: {path}.", nameof(policy));
            }

            if (!seen.Add(name))
            {
                continue;
            }

            SchedulePolicy? referenced = ReadOrNull(connection, name);
            if (referenced == null)
            {
                continue;
            }

            foreach (string next in referenced.Required.Concat(referenced.Excepted))
            {
                pending.Push((next, $"{path} -> {next}"));
            }
        }
    }

    // The policies that refer to this one and the recurring jobs that follow it
    private static List<string> FindUsers(IStorageConnection connection, string name)
    {
        List<string> users = [];

        foreach (string other in connection.GetAllItemsFromSet(SetKey).Order(StringComparer.Ordinal))
        {
            if (other == name)
            {
                continue;
            }

            SchedulePolicy? policy = ReadOrNull(connection, other);
            if (policy != null && (policy.Required.Contains(name) || policy.Excepted.Contains(name)))
            {
                users.Add($"policy '{other}'");
            }
        }

        foreach (string recurringJobId in FindRecurringJobs(connection, [name]))
        {
            users.Add($"recurring job '{recurringJobId}'");
        }

        return users;
    }

    private static List<string> FindRecurringJobs(IStorageConnection connection, HashSet<string> policies)
    {
        List<string> result = [];

        foreach (string recurringJobId in connection.GetAllItemsFromSet(RecurringJobsSetKey).Order(StringComparer.Ordinal))
        {
            Dictionary<string, string>? hash = connection.GetAllEntriesFromHash($"recurring-job:{recurringJobId}");
            if (hash != null && hash.TryGetValue(RecurringJobEntity.PolicyField, out string? policy) && policies.Contains(policy))
            {
                result.Add(recurringJobId);
            }
        }

        return result;
    }

    // A changed policy changes the next run of the recurring jobs that follow it, directly or through the policies
    // that refer to it. Like a new CRON expression, it doesn't make them catch up on runs. Best effort: a job that
    // can't be rescheduled now is corrected when it is next due.
    private static void RescheduleRecurringJobs(IStorageConnection connection, string name, DateTime now)
    {
        try
        {
            HashSet<string> affected = new(StringComparer.Ordinal) { name };
            List<SchedulePolicy> policies = [.. connection.GetAllItemsFromSet(SetKey).Select(x => ReadOrNull(connection, x)).OfType<SchedulePolicy>()];

            bool added = true;
            while (added)
            {
                added = false;
                foreach (SchedulePolicy policy in policies)
                {
                    if (!affected.Contains(policy.Name) && policy.Required.Concat(policy.Excepted).Any(affected.Contains))
                    {
                        added |= affected.Add(policy.Name);
                    }
                }
            }

            // Each policy is read once, however many jobs follow it
            SchedulePolicyCalendars calendars = new(connection);
            foreach (string recurringJobId in FindRecurringJobs(connection, affected))
            {
                Reschedule(connection, recurringJobId, now, calendars);
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.WarnException($"The recurring jobs that follow schedule policy '{name}' couldn't all be rescheduled; each one is when it is next due.", ex);
        }
    }

    private static void Reschedule(IStorageConnection connection, string recurringJobId, DateTime now, SchedulePolicyCalendars calendars)
    {
        using (connection.AcquireDistributedRecurringJobLock(recurringJobId, LockTimeout))
        {
            RecurringJobEntity? recurringJob = connection.GetRecurringJob(recurringJobId);
            if (recurringJob?.Cron == null || recurringJob.Paused || (!recurringJob.NextExecution.HasValue && recurringJob.Error == null))
            {
                return;
            }

            recurringJob.PolicyCalendars = calendars;
            recurringJob.RecordScheduleChange(now);
            recurringJob.RetryAttempt = 0;
            recurringJob.ScheduleNext(GlobalTimeZoneResolver.Current, now.AddSeconds(-1));

            if (recurringJob.IsChanged(now, out IReadOnlyDictionary<string, string> changedFields))
            {
                using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
                transaction.UpdateRecurringJob(recurringJob, changedFields, null);
                transaction.Commit();
            }
        }
    }
}
