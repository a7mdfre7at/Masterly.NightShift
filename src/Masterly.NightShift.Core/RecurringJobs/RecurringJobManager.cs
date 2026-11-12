// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using Masterly.NightShift.Client;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Profiling;

namespace Masterly.NightShift;

/// <summary>
/// Represents a recurring job manager that allows to create, update
/// or delete recurring jobs.
/// </summary>
public class RecurringJobManager : IRecurringJobManager, IRecurringJobManagerV2, IRecurringJobManagerV3
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly ILog _logger = LogProvider.GetLogger(typeof(RecurringJobManager));
    private readonly IBackgroundJobFactory _factory;
    private readonly Func<DateTime> _nowFactory;

    public RecurringJobManager()
        : this(JobStorage.Current)
    {
    }

    public RecurringJobManager([NotNull] JobStorage storage)
        : this(storage, JobFilterProviders.Providers)
    {
    }

    public RecurringJobManager([NotNull] JobStorage storage, [NotNull] IJobFilterProvider filterProvider)
        : this(storage, filterProvider, GlobalTimeZoneResolver.Current)
    {
    }

    public RecurringJobManager(
        [NotNull] JobStorage storage,
        [NotNull] IJobFilterProvider filterProvider,
        [NotNull] ITimeZoneResolver timeZoneResolver)
        : this(storage, filterProvider, timeZoneResolver, static () => JobClock.UtcNow)
    {
    }

    public RecurringJobManager(
        [NotNull] JobStorage storage,
        [NotNull] IJobFilterProvider filterProvider,
        [NotNull] ITimeZoneResolver timeZoneResolver,
        [NotNull] Func<DateTime> nowFactory)
        : this(storage, new BackgroundJobFactory(filterProvider), timeZoneResolver, nowFactory)
    {
    }

    public RecurringJobManager([NotNull] JobStorage storage, [NotNull] IBackgroundJobFactory factory)
        : this(storage, factory, GlobalTimeZoneResolver.Current)
    {
    }

    public RecurringJobManager([NotNull] JobStorage storage, [NotNull] IBackgroundJobFactory factory, [NotNull] ITimeZoneResolver timeZoneResolver)
        : this(storage, factory, timeZoneResolver, static () => JobClock.UtcNow)
    {
    }

    internal RecurringJobManager(
        [NotNull] JobStorage storage,
        [NotNull] IBackgroundJobFactory factory,
        [NotNull] ITimeZoneResolver timeZoneResolver,
        [NotNull] Func<DateTime> nowFactory)
    {
        Storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        TimeZoneResolver = timeZoneResolver ?? throw new ArgumentNullException(nameof(timeZoneResolver));
        _nowFactory = nowFactory ?? throw new ArgumentNullException(nameof(nowFactory));
    }

    public JobStorage Storage { get; }

    internal ITimeZoneResolver TimeZoneResolver { get; }

    public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(recurringJobId);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(cronExpression);
        ArgumentNullException.ThrowIfNull(options);

        ValidateCronExpression(cronExpression);
        ValidateMisfireHandling(options.MisfireHandling, nameof(options));
        ValidateWindow(options.StartAt, options.EndAt, nameof(options));
        ValidateSkipAfter(options.SkipAfter, options.MisfireHandling, nameof(options));

        if (job.Queue != null && !Storage.HasFeature(JobStorageFeatures.JobQueueProperty))
        {
            throw new NotSupportedException("Current storage doesn't support specifying queues directly for a specific job. Please use the QueueAttribute instead.");
        }

        using (IStorageConnection connection = Storage.GetConnection())
        using (connection.AcquireDistributedRecurringJobLock(recurringJobId, DefaultTimeout))
        {
            DateTime now = _nowFactory();
            RecurringJobEntity recurringJob = connection.GetOrCreateRecurringJob(recurringJobId);
            bool scheduleChanged = false;

            if (options.Policy != null && !SchedulePolicies.Exists(connection, options.Policy))
            {
                throw new ArgumentException($"There is no schedule policy named '{options.Policy}'. Save it with SaveSchedulePolicy before registering the job.", nameof(options));
            }

            recurringJob.Job = InvocationData.SerializeJob(job).SerializePayload();

            bool meaningChanged = false;

            if (!cronExpression.Equals(recurringJob.Cron, StringComparison.OrdinalIgnoreCase))
            {
                meaningChanged |= !IsSameCron(recurringJob.Cron, cronExpression);
                recurringJob.Cron = cronExpression;
                scheduleChanged = true;
            }

            if (!options.TimeZone.Id.Equals(recurringJob.TimeZoneId, StringComparison.OrdinalIgnoreCase))
            {
                meaningChanged |= !IsSameTimeZone(recurringJob.TimeZoneId, options.TimeZone);
                recurringJob.TimeZoneId = options.TimeZone.Id;
                scheduleChanged = true;
            }

            // A new window is a new schedule: Strict doesn't create the runs it would have had before it was set
            if (recurringJob.SetWindow(options.StartAt?.UtcDateTime, options.EndAt?.UtcDateTime))
            {
                meaningChanged = true;
                scheduleChanged = true;
            }

            // Another policy is another schedule: the next run is computed again, without catching up
            if (!string.Equals(options.Policy, recurringJob.Policy, StringComparison.Ordinal))
            {
                recurringJob.Policy = options.Policy;
                meaningChanged = true;
                scheduleChanged = true;
            }

            if (meaningChanged)
            {
                recurringJob.RecordScheduleChange(now);
            }

            recurringJob.Queue = options.QueueName;
            recurringJob.MisfireHandling = options.MisfireHandling;
            recurringJob.SkipAfter = options.SkipAfter;
            recurringJob.RetryAttempt = 0;

            // A paused job stays paused when the application re-registers it on start-up. A job that waits for a
            // policy that couldn't be evaluated keeps its pending occurrence when its schedule is the same
            if ((scheduleChanged || HasErrorToRecoverFrom(recurringJob)) && !recurringJob.Paused)
            {
                recurringJob.ScheduleNext(TimeZoneResolver, now.AddSeconds(-1));
            }

            if (recurringJob.IsChanged(now, out IReadOnlyDictionary<string, string>? changedFields))
            {
                using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
                transaction.UpdateRecurringJob(recurringJob, changedFields, _logger);
                transaction.Commit();
            }
        }
    }

    // An error is recovered from by computing the next occurrence from now. Not the error of a job that waits
    // for its policy: it is on the schedule already, to be looked at again soon, and its next occurrence computed
    // from now would put off the occurrences it missed until the next one still to come, a month away for a
    // monthly job
    private static bool HasErrorToRecoverFrom(RecurringJobEntity recurringJob)
    {
        return recurringJob.Error != null && !recurringJob.WaitsForPolicy;
    }

    // A rewrite that keeps the meaning ("0 0 * * *" and "@daily") isn't a schedule change, so Strict keeps
    // catching up on the runs missed before it
    private static bool IsSameCron([CanBeNull] string? stored, [NotNull] string cronExpression)
    {
        if (stored == null)
        {
            return false;
        }

        try
        {
            return RecurringJobEntity.ParseCronExpression(stored).Equals(RecurringJobEntity.ParseCronExpression(cronExpression));
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return false;
        }
    }

    // The same zone under another id, for example servers on Windows and Linux registering TimeZoneInfo.Local
    // as "W. Europe Standard Time" and "Europe/Berlin", isn't a schedule change either
    private bool IsSameTimeZone([CanBeNull] string? storedId, [NotNull] TimeZoneInfo timeZone)
    {
        if (storedId == null)
        {
            return false;
        }

        if (string.Equals(ToIanaId(storedId), ToIanaId(timeZone.Id), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            TimeZoneInfo stored = TimeZoneResolver.GetTimeZoneById(storedId);
            return stored != null && stored.HasSameRules(timeZone);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return false;
        }
    }

    private static string ToIanaId(string id)
    {
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out string? ianaId) ? ianaId : id;
    }

    private static void ValidateCronExpression(string cronExpression)
    {
        try
        {
            RecurringJobEntity.ParseCronExpression(cronExpression);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            throw new ArgumentException(
                "CRON expression is invalid. Please see the inner exception for details.",
                nameof(cronExpression),
                ex);
        }
    }

    // A value outside the enum would be stored, and no server could read the recurring job afterwards
    internal static void ValidateMisfireHandling(MisfireHandlingMode misfireHandling, [InvokerParameterName] string parameterName)
    {
        if (!Enum.IsDefined(misfireHandling))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                misfireHandling,
                $"Misfire handling mode {(int)misfireHandling} is not defined. Use Relaxed, Strict, Ignorable or RunLatest.");
        }
    }

    // SkipAfter is stored in whole seconds; RunLatest needs it, or it would catch up without a limit
    internal static void ValidateSkipAfter(TimeSpan? skipAfter, MisfireHandlingMode misfireHandling, [InvokerParameterName] string parameterName)
    {
        if (skipAfter.HasValue && skipAfter.Value < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(parameterName, skipAfter.Value, "SkipAfter must be at least one second.");
        }

        if (misfireHandling == MisfireHandlingMode.RunLatest && !skipAfter.HasValue)
        {
            throw new ArgumentException("MisfireHandlingMode.RunLatest needs SkipAfter, the age after which a missed occurrence no longer runs.", parameterName);
        }
    }

    private static readonly DateTimeOffset UnixEpoch = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Schedules and run times are kept as Unix timestamps, so a window can't start or end before 1970
    internal static void ValidateWindow(DateTimeOffset? startAt, DateTimeOffset? endAt, [InvokerParameterName] string parameterName)
    {
        if (startAt.HasValue && startAt.Value < UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                startAt.Value,
                "StartAt can't be earlier than 1970-01-01T00:00:00Z.");
        }

        if (endAt.HasValue && endAt.Value < UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                endAt.Value,
                "EndAt can't be earlier than 1970-01-01T00:00:00Z.");
        }

        if (startAt.HasValue && endAt.HasValue && endAt.Value <= startAt.Value)
        {
            throw new ArgumentException(
                $"EndAt ({endAt.Value:o}) must be later than StartAt ({startAt.Value:o}).",
                parameterName);
        }
    }

    public void Trigger(string recurringJobId) => TriggerJob(recurringJobId);

    public string? TriggerJob(string recurringJobId)
    {
        ArgumentNullException.ThrowIfNull(recurringJobId);

        using (IStorageConnection connection = Storage.GetConnection())
        using (connection.AcquireDistributedRecurringJobLock(recurringJobId, DefaultTimeout))
        {
            DateTime now = _nowFactory();

            RecurringJobEntity? recurringJob = connection.GetRecurringJob(recurringJobId);
            if (recurringJob == null)
            {
                return null;
            }

            BackgroundJob? backgroundJob;

            try
            {
                backgroundJob = _factory.TriggerRecurringJob(Storage, connection, EmptyProfiler.Instance, recurringJob, now);
                recurringJob.ScheduleNext(TimeZoneResolver, now);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // TODO: Preserving backward compatibility, should be removed in 2.0.0.
                throw new AggregateException(ex);
            }

            if (recurringJob.IsChanged(now, out IReadOnlyDictionary<string, string>? changedFields))
            {
                // Work that waits for the commit, such as the activity feed entry, runs only once the run is stored
                PostCommitActions afterCommit = new();

                try
                {
                    using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
                    if (backgroundJob != null)
                    {
                        _factory.StateMachine.EnqueueBackgroundJob(
                            Storage,
                            connection,
                            transaction,
                            recurringJob,
                            backgroundJob,
                            "Triggered using recurring job manager",
                            EmptyProfiler.Instance,
                            afterCommit);
                    }

                    transaction.UpdateRecurringJob(recurringJob, changedFields, _logger);
                    transaction.Commit();
                }
                catch
                {
                    // Work about something already stored on its own still runs, a retry wouldn't do it again
                    afterCommit.RunAfterFailure();
                    throw;
                }

                afterCommit.Run();
                return backgroundJob?.Id;
            }

            return null;
        }
    }

    /// <inheritdoc />
    public bool Pause(string recurringJobId)
    {
        ArgumentNullException.ThrowIfNull(recurringJobId);

        return UpdateExisting(recurringJobId, static (recurringJob, _) => recurringJob.Pause());
    }

    /// <inheritdoc />
    public bool Resume(string recurringJobId)
    {
        ArgumentNullException.ThrowIfNull(recurringJobId);

        return UpdateExisting(recurringJobId, (recurringJob, now) =>
        {
            if (recurringJob.Paused)
            {
                recurringJob.Resume(TimeZoneResolver, now);
            }
        });
    }

    /// <inheritdoc />
    public bool UpdateSchedule(
        string recurringJobId,
        string? cronExpression,
        TimeZoneInfo? timeZone = null,
        string? queue = null,
        MisfireHandlingMode? misfireHandling = null)
    {
        ArgumentNullException.ThrowIfNull(recurringJobId);

        if (cronExpression != null)
        {
            ValidateCronExpression(cronExpression);
        }

        if (queue != null)
        {
            EnqueuedState.ValidateQueueName(nameof(queue), queue);
        }

        if (misfireHandling.HasValue)
        {
            ValidateMisfireHandling(misfireHandling.Value, nameof(misfireHandling));
        }

        bool jobQueues = queue != null && Storage.HasFeature(JobStorageFeatures.JobQueueProperty);

        return UpdateExisting(recurringJobId, (recurringJob, now) =>
        {
            bool scheduleChanged = false;

            bool meaningChanged = false;

            if (cronExpression != null && !cronExpression.Equals(recurringJob.Cron, StringComparison.OrdinalIgnoreCase))
            {
                meaningChanged |= !IsSameCron(recurringJob.Cron, cronExpression);
                recurringJob.Cron = cronExpression;
                scheduleChanged = true;
            }

            if (timeZone != null && !timeZone.Id.Equals(recurringJob.TimeZoneId, StringComparison.OrdinalIgnoreCase))
            {
                meaningChanged |= !IsSameTimeZone(recurringJob.TimeZoneId, timeZone);
                recurringJob.TimeZoneId = timeZone.Id;
                scheduleChanged = true;
            }

            if (queue != null)
            {
                if (jobQueues)
                {
                    UpdateJobQueue(recurringJob, queue);
                }

                recurringJob.Queue = queue;
            }

            if (misfireHandling.HasValue)
            {
                ValidateSkipAfter(recurringJob.SkipAfter, misfireHandling.Value, nameof(misfireHandling));
                recurringJob.MisfireHandling = misfireHandling.Value;
            }

            if (meaningChanged)
            {
                recurringJob.RecordScheduleChange(now);
            }

            if (scheduleChanged || HasErrorToRecoverFrom(recurringJob))
            {
                recurringJob.RetryAttempt = 0;
                recurringJob.ScheduleNext(TimeZoneResolver, now.AddSeconds(-1));
            }
        });
    }

    // A run's later enqueues, such as AutomaticRetry's retries, use the job's own queue and not the recurring
    // job's, so on storages that keep it the job carries the new queue too, as AddOrUpdate(id, queue, ...) and
    // the manifest store it. Moving back to the default queue clears the job's queue only when it follows the
    // recurring job's queue: a queue the job was registered with stays, so the dashboard's Edit dialog, which
    // sends the recurring job's queue ("default") with every change, doesn't move such a job.
    private void UpdateJobQueue(RecurringJobEntity recurringJob, string queue)
    {
        if (recurringJob.Job == null)
        {
            return;
        }

        try
        {
            InvocationData invocationData = InvocationData.DeserializePayload(recurringJob.Job);
            string? current = GetExplicitQueue(invocationData.Queue);
            string? target = GetExplicitQueue(queue);

            if (string.Equals(current, target, StringComparison.Ordinal))
            {
                return;
            }

            if (target == null && !string.Equals(current, GetExplicitQueue(recurringJob.Queue), StringComparison.Ordinal))
            {
                return;
            }

            recurringJob.Job = new InvocationData(
                invocationData.Type,
                invocationData.Method,
                invocationData.ParameterTypes,
                invocationData.Arguments,
                target).SerializePayload();
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            _logger.WarnException(
                $"The job of recurring job '{recurringJob.RecurringJobId}' can't be read, so its queue is changed for new runs only: their retries keep the job's previous queue.",
                ex);
        }
    }

    /// <summary>The queue name, or <c>null</c> when it is empty or the default queue.</summary>
    private static string? GetExplicitQueue(string? queue)
    {
        return string.IsNullOrEmpty(queue) || EnqueuedState.DefaultQueue.Equals(queue, StringComparison.OrdinalIgnoreCase)
            ? null
            : queue;
    }

    private bool UpdateExisting(string recurringJobId, Action<RecurringJobEntity, DateTime> update)
    {
        using (IStorageConnection connection = Storage.GetConnection())
        using (connection.AcquireDistributedRecurringJobLock(recurringJobId, DefaultTimeout))
        {
            DateTime now = _nowFactory();
            RecurringJobEntity? recurringJob = connection.GetRecurringJob(recurringJobId);
            if (recurringJob == null)
            {
                return false;
            }

            update(recurringJob, now);

            if (recurringJob.IsChanged(now, out IReadOnlyDictionary<string, string>? changedFields))
            {
                using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
                transaction.UpdateRecurringJob(recurringJob, changedFields, _logger);
                transaction.Commit();
            }

            return true;
        }
    }

    public void RemoveIfExists(string recurringJobId)
    {
        ArgumentNullException.ThrowIfNull(recurringJobId);

        using (IStorageConnection connection = Storage.GetConnection())
        using (connection.AcquireDistributedRecurringJobLock(recurringJobId, DefaultTimeout))
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.RemoveHash($"recurring-job:{recurringJobId}");
            transaction.RemoveFromSet("recurring-jobs", recurringJobId);
            RecurringJobHistory.Remove(transaction, recurringJobId);

            transaction.Commit();
        }
    }
}
