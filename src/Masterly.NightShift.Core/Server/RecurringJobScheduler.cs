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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Profiling;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Server
{
    /// <summary>
    /// Represents a background process responsible for <i>enqueueing recurring 
    /// jobs</i>.
    /// </summary>
    /// 
    /// <remarks>
    /// <para>This background process polls the <i>recurring job schedule</i>
    /// for recurring jobs ready to be enqueued, every polling delay
    /// (<see cref="BackgroundJobServerOptions.SchedulePollingInterval"/>), or on
    /// every minute boundary when the delay is zero. One pass creates at most
    /// 100 runs of a recurring job; when more are due, the following passes,
    /// which start right away, create the rest.</para>
    /// 
    /// <note type="tip">
    /// Use custom background processes if you need to schedule recurring jobs
    /// with frequency less than one minute. Please see the 
    /// <see cref="IBackgroundProcess"/> interface for details.
    /// </note>
    /// 
    /// <para>Recurring job schedule is based on Set and Hash data structures
    /// of a job storage, so you can use this background process as an example 
    /// of a custom extension.</para>
    /// 
    /// <para>Multiple instances of this background process can be used in
    /// separate threads/processes without additional configuration (distributed
    /// locks are used). However, this only adds support for fail-over, and does 
    /// not increase the performance.</para>
    /// 
    /// <note type="important">
    /// If you are using <b>custom filter providers</b>, you need to pass a 
    /// custom <see cref="IBackgroundJobFactory"/> instance to make this 
    /// process respect your filters when enqueueing background jobs.
    /// </note>
    /// </remarks>
    /// 
    /// <threadsafety static="true" instance="true"/>
    /// 
    /// <seealso cref="RecurringJobManager"/>
    public class RecurringJobScheduler : IBackgroundProcess
    {
        private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(1);
        private static readonly int BatchSize = 1000;
        private static readonly int MaxRetryAttemptCount = 5;
        private static readonly int MaxSupportedVersion = 2;

        private readonly ILog _logger = LogProvider.For<RecurringJobScheduler>();
        private readonly ConcurrentDictionary<Type, bool> _isBatchingAvailableCache = new ConcurrentDictionary<Type, bool>();

        private readonly IBackgroundJobFactory _factory;
        private readonly Func<DateTime> _nowFactory;
        private readonly ITimeZoneResolver _timeZoneResolver;
        private readonly TimeSpan _pollingDelay;
        private readonly IProfiler _profiler;
        private bool _parallelismIssueLogged;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecurringJobScheduler"/>
        /// class with default background job factory.
        /// </summary>
        public RecurringJobScheduler()
            : this(new BackgroundJobFactory(JobFilterProviders.Providers))
        {
        }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="RecurringJobScheduler"/>
        /// class with custom background job factory and a state machine.
        /// </summary>
        /// <param name="factory">Factory that will be used to create background jobs.</param>
        /// 
        /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
        public RecurringJobScheduler(
            [NotNull] IBackgroundJobFactory factory)
            : this(factory, TimeSpan.Zero)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RecurringJobScheduler"/> class
        /// with custom background job factory, state machine and clocks.
        /// </summary>
        /// <param name="factory">Factory that will be used to create background jobs.</param>
        /// <param name="pollingDelay">Delay before another polling attempt, when no jobs scheduled yet.</param>
        /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
        public RecurringJobScheduler(
            [NotNull] IBackgroundJobFactory factory,
            TimeSpan pollingDelay)
            : this(factory, pollingDelay, GlobalTimeZoneResolver.Current)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RecurringJobScheduler"/> class
        /// with custom background job factory, state machine and clocks.
        /// </summary>
        /// <param name="factory">Factory that will be used to create background jobs.</param>
        /// <param name="pollingDelay">Delay before another polling attempt, when no jobs scheduled yet.</param>
        /// <param name="timeZoneResolver">Function that returns a time zone object by its identifier.</param>
        /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="timeZoneResolver"/> is null.</exception>
        public RecurringJobScheduler(
            [NotNull] IBackgroundJobFactory factory,
            TimeSpan pollingDelay,
            [NotNull] ITimeZoneResolver timeZoneResolver)
            : this(factory, pollingDelay, timeZoneResolver, static () => DateTime.UtcNow)
        {
        }

        public RecurringJobScheduler(
            [NotNull] IBackgroundJobFactory factory,
            TimeSpan pollingDelay,
            [NotNull] ITimeZoneResolver timeZoneResolver,
            [NotNull] Func<DateTime> nowFactory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (nowFactory == null) throw new ArgumentNullException(nameof(nowFactory));
            if (timeZoneResolver == null) throw new ArgumentNullException(nameof(timeZoneResolver));

            _factory = factory;
            _nowFactory = nowFactory;
            _timeZoneResolver = timeZoneResolver;
            _pollingDelay = pollingDelay;
            _profiler = new SlowLogProfiler(_logger);
        }

        /// <summary>
        /// Gets or sets the maximum degree of parallelism for a scheduler instance.
        /// When greater than <c>1</c> and batching enabling, recurring jobs will
        /// be scheduled in parallel under separate connections, increasing the
        /// throughput.
        /// </summary>
        public int MaxDegreeOfParallelism { get; set; }

        /// <summary>
        /// Gets or sets a task scheduler that will be used when parallel scheduling
        /// is enabled via the <see cref="MaxDegreeOfParallelism"/> option.
        /// </summary>
        public TaskScheduler TaskScheduler { get; set; }

        // With a zero polling delay the scheduler wakes up on every minute boundary (see Execute)
        private TimeSpan PollingInterval => _pollingDelay > TimeSpan.Zero ? _pollingDelay : TimeSpan.FromMinutes(1);

        /// <inheritdoc />
        public void Execute(BackgroundProcessContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            int jobsProcessed;

            do
            {
                jobsProcessed = EnqueueNextRecurringJobs(context);

                if (jobsProcessed != 0)
                {
                    _logger.Debug($"{jobsProcessed} recurring job(s) processed by scheduler.");
                }
            } while (jobsProcessed > 0 && !context.IsStopping);

            if (_pollingDelay > TimeSpan.Zero)
            {
                context.Wait(_pollingDelay);
            }
            else
            {
                var now = _nowFactory();
                context.Wait(now.AddMilliseconds(-now.Millisecond).AddSeconds(-now.Second).AddMinutes(1) - now);
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return GetType().Name;
        }

        private int EnqueueNextRecurringJobs(BackgroundProcessContext context)
        {
            return UseConnectionDistributedLock(context.Storage, connection =>
            {
                var jobsProcessed = 0;

                var now = DateTime.SpecifyKind(
                    !context.Storage.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime)
                        ? _nowFactory()
                        : ((JobStorageConnection)connection).GetUtcDateTime(),
                    DateTimeKind.Utc);

                if (IsBatchingAvailable(context.Storage, connection))
                {
                    var timestamp = JobHelper.ToTimestamp(now);
                    var recurringJobIds = ((JobStorageConnection)connection).GetFirstByLowestScoreFromSet("recurring-jobs", 0, timestamp, BatchSize);

                    if (recurringJobIds != null)
                    {
                        if (MaxDegreeOfParallelism > 1)
                        {
                            Parallel.ForEach(
                                recurringJobIds,
                                new ParallelOptions
                                {
                                    MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                                    CancellationToken = context.StoppingToken,
                                    TaskScheduler = TaskScheduler
                                },
                                (recurringJobId, state) =>
                                {
                                    using (var dedicated = context.Storage.GetConnection())
                                    {
                                        TryEnqueueBackgroundJob(context, dedicated, recurringJobId, now);
                                    }

                                    Interlocked.Increment(ref jobsProcessed);
                                });
                        }
                        else
                        {
                            foreach (var recurringJobId in recurringJobIds)
                            {
                                if (context.IsStopping) break;

                                TryEnqueueBackgroundJob(context, connection, recurringJobId, now);
                                jobsProcessed++;
                            }
                        }
                    }
                }
                else
                {
                    if (MaxDegreeOfParallelism > 1 && !_parallelismIssueLogged)
                    {
                        _logger.Warn("Parallel execution is configured but can't be used, because current storage implementation doesn't support batching.");
                        _parallelismIssueLogged = true;
                    }

                    for (var i = 0; i < BatchSize; i++)
                    {
                        if (context.IsStopping) break;

                        var timestamp = JobHelper.ToTimestamp(now);

                        var recurringJobId = connection.GetFirstByLowestScoreFromSet("recurring-jobs", 0, timestamp);
                        if (recurringJobId == null) break;

                        TryEnqueueBackgroundJob(context, connection, recurringJobId, now);
                        jobsProcessed++;
                    }
                }

                return jobsProcessed;
            });
        }

        private void TryEnqueueBackgroundJob(
            BackgroundProcessContext context,
            IStorageConnection connection,
            string recurringJobId,
            DateTime now)
        {
            using (connection.AcquireDistributedRecurringJobLock(recurringJobId, LockTimeout))
            {
                var hash = connection.GetAllEntriesFromHash($"recurring-job:{recurringJobId}");

                if (hash == null || hash.Count == 0)
                {
                    RemoveRecurringJob(connection, recurringJobId);
                    return;
                }

                RecurringJobEntity recurringJob;

                try
                {
                    recurringJob = new RecurringJobEntity(recurringJobId, hash);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    // One recurring job that can't be read (an unknown misfire mode, a damaged field) must not
                    // stop the others, which are due after it
                    RetryUnreadableRecurringJob(connection, recurringJobId, hash, now, ex);
                    return;
                }

                ScheduleRecurringJob(context, connection, recurringJobId, recurringJob, now);
            }
        }

        private void RetryUnreadableRecurringJob(
            IStorageConnection connection,
            string recurringJobId,
            IDictionary<string, string> hash,
            DateTime now,
            Exception error)
        {
            // The same retries as any other scheduling error, written field by field, as the entity can't be built
            var retryAttempt = hash.TryGetValue("RetryAttempt", out var attemptString) &&
                               int.TryParse(attemptString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempt)
                ? attempt
                : 0;

            var fields = new Dictionary<string, string>
            {
                { "Error", error.ToStringWithOriginalStackTrace(States.FailedState.MaxLinesInExceptionDetails, includeFileInfo: false) },
            };

            double score;

            if (retryAttempt < MaxRetryAttemptCount)
            {
                var nextAttempt = now.Add(PollingInterval);

                _logger.WarnException(
                    $"Recurring job '{recurringJobId}' can't be read and will be retried in {PollingInterval}.",
                    error);

                fields.Add("RetryAttempt", (retryAttempt + 1).ToString(CultureInfo.InvariantCulture));
                fields.Add("NextExecution", JobHelper.SerializeDateTime(nextAttempt));
                score = JobHelper.ToTimestamp(nextAttempt);
            }
            else
            {
                _logger.ErrorException(
                    $"Recurring job '{recurringJobId}' can't be read and will be disabled. Remove it and register it again.",
                    error);

                fields.Add("NextExecution", String.Empty);
                score = -1.0D;
            }

            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.SetRangeInHash($"recurring-job:{recurringJobId}", fields);
                transaction.AddToSet("recurring-jobs", recurringJobId, score);
                transaction.Commit();
            }
        }

        private void ScheduleRecurringJob(BackgroundProcessContext context, IStorageConnection connection,
            string recurringJobId, RecurringJobEntity recurringJob, DateTime now)
        {
            // We always start a transaction, regardless our recurring job was updated or not,
            // to prevent from infinite loop, when there's an old processing server (pre-1.7.0)
            // in our environment that doesn't know it should modify the score for entries in
            // the recurring jobs set.
            var afterCommit = new PostCommitActions();

            try
            {
                using (var transaction = connection.CreateWriteTransaction())
                {
                    Exception exception = null;

                    // The last run that is created and enqueued in this transaction (or canceled by a filter). When a
                    // run fails, the job's last execution goes back to it, so the next pass creates the failed run and
                    // the ones after it again instead of skipping them
                    var lastHandledExecution = recurringJob.LastExecution;
                    var lastHandledJobId = recurringJob.LastJobId;

                    try
                    {
                        // We can't handle recurring job with unsupported versions - there may be additional
                        // features. we don't know about. We also shouldn't stop the whole scheduler as
                        // there may be jobs with lower versions. Instead, we'll re-schedule such a job and
                        // emit a warning message to the log.
                        if (recurringJob.Version.HasValue && recurringJob.Version > MaxSupportedVersion)
                        {
                            throw new NotSupportedException($"Server '{context.ServerId}' can't process recurring job '{recurringJobId}' of version '{recurringJob.Version ?? 1}'. Max supported version of this server is '{MaxSupportedVersion}'.");
                        }
                    
                        var runs = new List<KeyValuePair<DateTime, BackgroundJob>>();

                        // How late an Ignorable run may be noticed and still be created
                        var precision = PollingInterval + PollingInterval;

                        var executions = recurringJob.ScheduleNext(
                            _timeZoneResolver,
                            recurringJob.ScheduleFrom ?? recurringJob.CreatedAt?.AddSeconds(-1) ?? now.AddSeconds(-1),
                            now,
                            precision);

                        foreach (var execution in executions)
                        {
                            var backgroundJob = _factory.TriggerRecurringJob(
                                context.Storage,
                                connection,
                                _profiler,
                                recurringJob,
                                execution);

                            if (String.IsNullOrEmpty(backgroundJob?.Id))
                            {
                                _logger.Debug($"Recurring job '{recurringJobId}' execution at '{execution}' has been canceled.");
                                backgroundJob = null;
                            }

                            runs.Add(new KeyValuePair<DateTime, BackgroundJob>(execution, backgroundJob));
                        }

                        foreach (var run in runs)
                        {
                            if (run.Value != null)
                            {
                                _factory.StateMachine.EnqueueBackgroundJob(
                                    context.Storage,
                                    connection,
                                    transaction,
                                    recurringJob,
                                    run.Value,
                                    "Triggered by recurring job scheduler",
                                    _profiler,
                                    afterCommit);
                            }

                            lastHandledExecution = run.Key;
                            lastHandledJobId = run.Value?.Id;
                        }

                        recurringJob.RetryAttempt = 0;
                    }
                    catch (Exception ex) when (ex.IsCatchableExceptionType())
                    {
                        exception = ex;
                    }

                    if (exception != null)
                    {
                        // A run created but not enqueued has no state, and its data expires like any job created without one
                        recurringJob.LastExecution = lastHandledExecution;
                        recurringJob.LastJobId = lastHandledJobId;
                        RetryRecurringJob(recurringJobId, recurringJob, now, exception);
                    }

                    recurringJob.IsChanged(now, out var changedFields);
                    transaction.UpdateRecurringJob(recurringJob, changedFields, _logger);

                    // We should commit transaction outside of the internal try/catch block, because these
                    // exceptions are always due to network issues, and in case of a timeout exception we
                    // can't determine whether it was actually succeeded or not, and can't perform an
                    // idempotent retry in this case.
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
        }

        private void RetryRecurringJob(string recurringJobId, RecurringJobEntity recurringJob, DateTime now, Exception error)
        {
            var errorString = error.ToStringWithOriginalStackTrace(States.FailedState.MaxLinesInExceptionDetails, includeFileInfo: false);

            if (recurringJob.RetryAttempt < MaxRetryAttemptCount)
            {
                var delay = PollingInterval;

                _logger.WarnException(
                    $"Recurring job '{recurringJobId}' can't be scheduled due to an error and will be retried in {delay}.",
                    error);
                recurringJob.ScheduleRetry(now.Add(delay), errorString);
            }
            else
            {
                _logger.ErrorException(
                    $"Recurring job '{recurringJobId}' can't be scheduled due to an error and will be disabled.", error);
                recurringJob.Disable(errorString);
            }
        }

        private void RemoveRecurringJob(IStorageConnection connection, string recurringJobId)
        {
            _logger.Debug($"Recurring job '{recurringJobId}' doesn't exist and will be removed from schedule.");

            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.RemoveFromSet("recurring-jobs", recurringJobId);
                transaction.Commit();
            }
        }

        private T UseConnectionDistributedLock<T>(JobStorage storage, Func<IStorageConnection, T> action)
        {
            var resource = "recurring-jobs:lock";
            try
            {
                using (var connection = storage.GetConnection())
                using (connection.AcquireDistributedLock(resource, LockTimeout))
                {
                    return action(connection);
                }
            }
            catch (DistributedLockTimeoutException e) when (e.Resource.EndsWith(resource, StringComparison.Ordinal))
            {
                // DistributedLockTimeoutException here doesn't mean that recurring jobs weren't scheduled.
                // It just means another Masterly.NightShift server did this work.
                _logger.Log(
                    LogLevel.Debug,
                    () => $@"An exception was thrown during acquiring distributed lock the {resource} resource within {LockTimeout.TotalSeconds} seconds. The recurring jobs have not been handled this time.",
                    e);
            }

            return default;
        }

        private bool IsBatchingAvailable(JobStorage storage, IStorageConnection connection)
        {
            if (storage.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest) ||
                storage.HasFeature("BatchedGetFirstByLowestScoreFromSet")) // FROM RCs
            {
                return true;
            }

            return _isBatchingAvailableCache.GetOrAdd(
                connection.GetType(),
                type =>
                {
                    if (connection is JobStorageConnection storageConnection)
                    {
                        try
                        {
                            storageConnection.GetFirstByLowestScoreFromSet(null, 0, 0, 1);
                        }
                        catch (ArgumentNullException ex) when (ex.ParamName == "key")
                        {
                            return true;
                        }
                        catch (Exception ex) when (ex.IsCatchableExceptionType())
                        {
                            //
                        }
                    }

                    return false;
                });
        }
    }
}