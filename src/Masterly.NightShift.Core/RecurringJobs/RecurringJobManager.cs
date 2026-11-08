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

#nullable enable

using System;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Profiling;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// Represents a recurring job manager that allows to create, update
    /// or delete recurring jobs.
    /// </summary>
    public class RecurringJobManager : IRecurringJobManager, IRecurringJobManagerV2, IRecurringJobManagerV3
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

        private readonly ILog _logger = LogProvider.GetLogger(typeof(RecurringJobManager));

        private readonly JobStorage _storage;
        private readonly IBackgroundJobFactory _factory;
        private readonly Func<DateTime> _nowFactory;
        private readonly ITimeZoneResolver _timeZoneResolver;

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
            : this(storage, filterProvider, timeZoneResolver, static () => DateTime.UtcNow)
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
            : this(storage, factory, timeZoneResolver, static () => DateTime.UtcNow)
        {
        }

        internal RecurringJobManager(
            [NotNull] JobStorage storage, 
            [NotNull] IBackgroundJobFactory factory,
            [NotNull] ITimeZoneResolver timeZoneResolver,
            [NotNull] Func<DateTime> nowFactory)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _timeZoneResolver = timeZoneResolver ?? throw new ArgumentNullException(nameof(timeZoneResolver));
            _nowFactory = nowFactory ?? throw new ArgumentNullException(nameof(nowFactory));
        }

        public JobStorage Storage => _storage;

        internal ITimeZoneResolver TimeZoneResolver => _timeZoneResolver;

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options)
        {
            if (recurringJobId == null) throw new ArgumentNullException(nameof(recurringJobId));
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (cronExpression == null) throw new ArgumentNullException(nameof(cronExpression));
            if (options == null) throw new ArgumentNullException(nameof(options));

            ValidateCronExpression(cronExpression);
            ValidateMisfireHandling(options.MisfireHandling, nameof(options));
            ValidateWindow(options.StartAt, options.EndAt, nameof(options));

            if (job.Queue != null && !Storage.HasFeature(JobStorageFeatures.JobQueueProperty))
            {
                throw new NotSupportedException("Current storage doesn't support specifying queues directly for a specific job. Please use the QueueAttribute instead.");
            }

            using (var connection = _storage.GetConnection())
            using (connection.AcquireDistributedRecurringJobLock(recurringJobId, DefaultTimeout))
            {
                var now = _nowFactory();
                var recurringJob = connection.GetOrCreateRecurringJob(recurringJobId);
                var scheduleChanged = false;

                recurringJob.Job = InvocationData.SerializeJob(job).SerializePayload();

                var meaningChanged = false;

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

                if (meaningChanged)
                {
                    recurringJob.RecordScheduleChange(now);
                }

#pragma warning disable 618
                recurringJob.Queue = options.QueueName;
#pragma warning restore 618
                recurringJob.MisfireHandling = options.MisfireHandling;
                recurringJob.RetryAttempt = 0;

                // A paused job stays paused when the application re-registers it on start-up
                if ((scheduleChanged || recurringJob.Error != null) && !recurringJob.Paused)
                {
                    recurringJob.ScheduleNext(_timeZoneResolver, now.AddSeconds(-1));
                }

                if (recurringJob.IsChanged(now, out var changedFields))
                {
                    using (var transaction = connection.CreateWriteTransaction())
                    {
                        transaction.UpdateRecurringJob(recurringJob, changedFields, _logger);
                        transaction.Commit();
                    }
                }
            }
        }
 
        // A rewrite that keeps the meaning ("0 0 * * *" and "@daily") isn't a schedule change, so Strict keeps
        // catching up on the runs missed before it
        private static bool IsSameCron([CanBeNull] string? stored, [NotNull] string cronExpression)
        {
            if (stored == null) return false;

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
            if (storedId == null) return false;

#if NET8_0_OR_GREATER
            if (String.Equals(ToIanaId(storedId), ToIanaId(timeZone.Id), StringComparison.OrdinalIgnoreCase)) return true;
#endif

            try
            {
                var stored = _timeZoneResolver.GetTimeZoneById(storedId);
                return stored != null && stored.HasSameRules(timeZone);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return false;
            }
        }

#if NET8_0_OR_GREATER
        private static string ToIanaId(string id)
        {
            return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId) ? ianaId : id;
        }
#endif

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
            if (!Enum.IsDefined(typeof(MisfireHandlingMode), misfireHandling))
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    misfireHandling,
                    $"Misfire handling mode {(int)misfireHandling} is not defined. Use Relaxed, Strict or Ignorable.");
            }
        }

        private static readonly DateTimeOffset UnixEpoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

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

        [Obsolete("Use the `TriggerJob` method instead. Kept for code ported from Hangfire.")]
        public string? TriggerExecution(string recurringJobId) => TriggerJob(recurringJobId);

        public string? TriggerJob(string recurringJobId)
        {
            if (recurringJobId == null) throw new ArgumentNullException(nameof(recurringJobId));

            using (var connection = _storage.GetConnection())
            using (connection.AcquireDistributedRecurringJobLock(recurringJobId, DefaultTimeout))
            {
                var now = _nowFactory();

                var recurringJob = connection.GetRecurringJob(recurringJobId);
                if (recurringJob == null) return null;

                BackgroundJob? backgroundJob;

                try
                {
                    backgroundJob = _factory.TriggerRecurringJob(_storage, connection, EmptyProfiler.Instance, recurringJob, now);
                    recurringJob.ScheduleNext(_timeZoneResolver, now);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    // TODO: Preserving backward compatibility, should be removed in 2.0.0.
                    throw new AggregateException(ex);
                }

                if (recurringJob.IsChanged(now, out var changedFields))
                {
                    // Work that waits for the commit, such as the activity feed entry, runs only once the run is stored
                    var afterCommit = new PostCommitActions();

                    try
                    {
                        using (var transaction = connection.CreateWriteTransaction())
                        {
                            if (backgroundJob != null)
                            {
                                _factory.StateMachine.EnqueueBackgroundJob(
                                    _storage,
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
            if (recurringJobId == null) throw new ArgumentNullException(nameof(recurringJobId));

            return UpdateExisting(recurringJobId, static (recurringJob, _) => recurringJob.Pause());
        }

        /// <inheritdoc />
        public bool Resume(string recurringJobId)
        {
            if (recurringJobId == null) throw new ArgumentNullException(nameof(recurringJobId));

            return UpdateExisting(recurringJobId, (recurringJob, now) =>
            {
                if (recurringJob.Paused)
                {
                    recurringJob.Resume(_timeZoneResolver, now);
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
            if (recurringJobId == null) throw new ArgumentNullException(nameof(recurringJobId));

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

            var jobQueues = queue != null && _storage.HasFeature(JobStorageFeatures.JobQueueProperty);

            return UpdateExisting(recurringJobId, (recurringJob, now) =>
            {
                var scheduleChanged = false;

                var meaningChanged = false;

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
                    recurringJob.MisfireHandling = misfireHandling.Value;
                }

                if (meaningChanged)
                {
                    recurringJob.RecordScheduleChange(now);
                }

                if (scheduleChanged || recurringJob.Error != null)
                {
                    recurringJob.RetryAttempt = 0;
                    recurringJob.ScheduleNext(_timeZoneResolver, now.AddSeconds(-1));
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
            if (recurringJob.Job == null) return;

            try
            {
                var invocationData = InvocationData.DeserializePayload(recurringJob.Job);
                var current = GetExplicitQueue(invocationData.Queue);
                var target = GetExplicitQueue(queue);

                if (String.Equals(current, target, StringComparison.Ordinal)) return;
                if (target == null && !String.Equals(current, GetExplicitQueue(recurringJob.Queue), StringComparison.Ordinal)) return;

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
            return String.IsNullOrEmpty(queue) || EnqueuedState.DefaultQueue.Equals(queue, StringComparison.OrdinalIgnoreCase)
                ? null
                : queue;
        }

        private bool UpdateExisting(string recurringJobId, Action<RecurringJobEntity, DateTime> update)
        {
            using (var connection = _storage.GetConnection())
            using (connection.AcquireDistributedRecurringJobLock(recurringJobId, DefaultTimeout))
            {
                var now = _nowFactory();
                var recurringJob = connection.GetRecurringJob(recurringJobId);
                if (recurringJob == null) return false;

                update(recurringJob, now);

                if (recurringJob.IsChanged(now, out var changedFields))
                {
                    using (var transaction = connection.CreateWriteTransaction())
                    {
                        transaction.UpdateRecurringJob(recurringJob, changedFields, _logger);
                        transaction.Commit();
                    }
                }

                return true;
            }
        }

        public void RemoveIfExists(string recurringJobId)
        {
            if (recurringJobId == null) throw new ArgumentNullException(nameof(recurringJobId));

            using (var connection = _storage.GetConnection())
            using (connection.AcquireDistributedRecurringJobLock(recurringJobId, DefaultTimeout))
            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.RemoveHash($"recurring-job:{recurringJobId}");
                transaction.RemoveFromSet("recurring-jobs", recurringJobId);

                transaction.Commit();
            }
        }
    }
}
