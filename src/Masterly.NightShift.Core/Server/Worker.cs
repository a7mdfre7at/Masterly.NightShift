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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Profiling;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Server
{
    /// <summary>
    /// Represents a background process responsible for <i>processing 
    /// fire-and-forget jobs</i>.
    /// </summary>
    /// 
    /// <remarks>
    /// <para>This is the heart of background processing in Masterly.NightShift</para>
    /// </remarks>
    /// 
    /// <threadsafety static="true" instance="true"/>
    /// 
    /// <seealso cref="EnqueuedState"/>
    public class Worker : IBackgroundProcess
    {
        [Obsolete("Use JobStorageFeatures.StorageTransactionalAcknowledge instead. Kept for code ported from Hangfire.")]
        public static readonly string TransactionalAcknowledgePrefix = JobStorageFeatures.TransactionalAcknowledgePrefix;

        private static readonly ConcurrentDictionary<Guid, string> WorkerGuidCache = new();
        private static readonly string[] EligibleWorkerStates = new[] { EnqueuedState.StateName, ScheduledState.StateName, ProcessingState.StateName };
        private static readonly string[] ProcessingStateArray = new[] { ProcessingState.StateName };

        /// <summary>
        /// <see cref="PerformContext.Items"/> entry holding an <c>ICollection&lt;IDisposable&gt;</c> the worker
        /// disposes after the state change that follows the job (or when processing the job ends otherwise), so a
        /// server filter can keep something open until then: the job logging scope of Masterly.NightShift.NetCore
        /// adds itself, so what state filters log about the job (retries, deadlines) is in the job's scope. It is
        /// left out of the state change's custom data. A filter that finds no entry (a performer called without a
        /// worker) disposes it itself.
        /// </summary>
        internal const string DisposeAfterStateChangeItem = "X_NS_DisposeAfterStateChange";

        private readonly TimeSpan _jobInitializationWaitTimeout;
        private readonly int _maxStateChangeAttempts;

        private readonly ILog _logger = LogProvider.For<Worker>();

        private readonly IEnumerable<string> _queues;

        private readonly IBackgroundJobPerformer _performer;
        private readonly IBackgroundJobStateChanger _stateChanger;
        private readonly IProfiler _profiler;
        
        public Worker() : this(EnqueuedState.DefaultQueue)
        {
        }

        public Worker([NotNull] params string[] queues)
            : this(queues, new BackgroundJobPerformer(), new BackgroundJobStateChanger())
        {
        }

        public Worker(
            [NotNull] IEnumerable<string> queues,
            [NotNull] IBackgroundJobPerformer performer,
            [NotNull] IBackgroundJobStateChanger stateChanger)
            : this(queues, performer, stateChanger, jobInitializationTimeout: TimeSpan.FromMinutes(1), maxStateChangeAttempts: 10)
        {
        }

        internal Worker(
            [NotNull] IEnumerable<string> queues,
            [NotNull] IBackgroundJobPerformer performer, 
            [NotNull] IBackgroundJobStateChanger stateChanger,
            TimeSpan jobInitializationTimeout,
            int maxStateChangeAttempts)
        {
            if (queues == null) throw new ArgumentNullException(nameof(queues));
            if (performer == null) throw new ArgumentNullException(nameof(performer));
            if (stateChanger == null) throw new ArgumentNullException(nameof(stateChanger));
            
            _queues = queues;
            _performer = performer;
            _stateChanger = stateChanger;

            _jobInitializationWaitTimeout = jobInitializationTimeout;
            _maxStateChangeAttempts = maxStateChangeAttempts;

            _profiler = new SlowLogProfiler(_logger);
        }

        /// <inheritdoc />
        public void Execute(BackgroundProcessContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            using (var connection = context.Storage.GetConnection())
            using (var fetchedJob = connection.FetchNextJob(_queues.ToArray(), context.StoppingToken))
            {
                var requeueOnException = true;
                var disposeAfterStateChange = new List<IDisposable>();

                try
                {
                    BackgroundJob backgroundJob = null;

                    using (var timeoutCts = new CancellationTokenSource(_jobInitializationWaitTimeout))
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        context.StoppingToken,
                        timeoutCts.Token))
                    {
                        var processingState = new ProcessingState(context.ServerId, WorkerGuidCache.GetOrAdd(context.ExecutionId, static guid => guid.ToString()));

                        var appliedState = TryChangeState(
                            context, 
                            connection, 
                            fetchedJob.JobId, 
                            processingState, 
                            null,
                            EligibleWorkerStates,
                            null,
                            out backgroundJob,
                            linkedCts.Token,
                            context.StoppingToken);

                        // Cancel job processing if the job could not be loaded, was not in the initial state expected
                        // or if a job filter changed the state to something other than processing state
                        if (appliedState == null || !appliedState.Name.Equals(ProcessingState.StateName, StringComparison.OrdinalIgnoreCase))
                        {
                            // We should forget a job in a wrong state, or when timeout exceeded.
                            requeueOnException = false;
                            fetchedJob.RemoveFromQueue();
                            return;
                        }
                    }

                    // Checkpoint #3. Job is in the Processing state. However, there are
                    // no guarantees that it was performed. We need to re-queue it even
                    // it was performed to guarantee that it was performed AT LEAST once.
                    // It will be re-queued after the JobTimeout was expired.

                    var state = PerformJob(context, connection, fetchedJob.JobId, backgroundJob, disposeAfterStateChange, out var customData);
                    var transactionalAck = context.Storage.HasFeature(JobStorageFeatures.Transaction.RemoveFromQueue(fetchedJob.GetType()));

                    if (state != null)
                    {
                        // Ignore return value, because we should not do anything when current state is not Processing.
                        TryChangeState(
                            context,
                            connection,
                            fetchedJob.JobId,
                            state,
                            customData,
                            ProcessingStateArray,
                            transactionalAck ? fetchedJob : null,
                            out _,
                            CancellationToken.None,
                            context.ShutdownToken);
                    }

                    // Checkpoint #4. The job was performed, and it is in the one
                    // of the explicit states (Succeeded, Scheduled and so on).
                    // It should not be re-queued, but we still need to remove its
                    // processing information.

                    requeueOnException = false;
                    fetchedJob.RemoveFromQueue();

                    // Success point. No things must be done after previous command
                    // was succeeded.
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    if (context.IsStopping)
                    {
                        var action = requeueOnException ? "It will be re-queued" : "It will be removed from queue later";
                        _logger.Warn($"Worker stop requested while processing background job '{fetchedJob.JobId}'. {action}.");
                    }

                    if (requeueOnException)
                    {
                        Requeue(fetchedJob);
                    }

                    throw;
                }
                finally
                {
                    DisposeAfterStateChange(disposeAfterStateChange);
                }
            }
        }

        private void DisposeAfterStateChange(List<IDisposable> disposables)
        {
            // In reverse order, like nested using blocks
            for (var i = disposables.Count - 1; i >= 0; i--)
            {
                try
                {
                    disposables[i].Dispose();
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    _logger.WarnException("Could not dispose what a server filter kept open until the job's state change.", ex);
                }
            }

            disposables.Clear();
        }

        private IState TryChangeState(
            BackgroundProcessContext context, 
            IStorageConnection connection, 
            string jobId,
            IState state,
            IReadOnlyDictionary<string, object> customData,
            string[] expectedStates,
            IFetchedJob completeJob,
            out BackgroundJob backgroundJob,
            CancellationToken initializeToken,
            CancellationToken abortToken)
        {
            Exception exception = null;

            abortToken.ThrowIfCancellationRequested();

            // At least one retry attempt should always be performed.
            var maxRetryAttempts = _maxStateChangeAttempts > 0 ? _maxStateChangeAttempts : 1;

            for (var retryAttempt = 0; retryAttempt < maxRetryAttempts; retryAttempt++)
            {
                try
                {
                    var stateChangeContext = new StateChangeContext(
                        context.Storage,
                        connection,
                        null,
                        jobId,
                        state,
                        expectedStates,
                        disableFilters: false,
                        completeJob,
                        initializeToken,
                        _profiler,
                        context.ServerId,
                        customData);

                    var resultingState = _stateChanger.ChangeState(stateChangeContext);

                    backgroundJob = stateChangeContext.ProcessedJob;
                    return resultingState;
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    _logger.DebugException(
                        $"State change attempt {retryAttempt + 1} of {_maxStateChangeAttempts} failed due to an error, see inner exception for details", 
                        ex);

                    exception = ex;
                }

                abortToken.WaitOrThrow(TimeSpan.FromSeconds(retryAttempt));
            }

            _logger.ErrorException(
                $"{_maxStateChangeAttempts} state change attempt(s) failed due to an exception, moving job to the FailedState",
                exception);

            var failedStateContext = new StateChangeContext(
                context.Storage,
                connection,
                null,
                jobId,
                new FailedState(exception, context.ServerId) { Reason = $"Failed to change state to a '{state.Name}' one due to an exception after {_maxStateChangeAttempts} retry attempts" },
                expectedStates,
                disableFilters: true,
                completeJob,
                initializeToken,
                _profiler,
                context.ServerId);

            var failedResult = _stateChanger.ChangeState(failedStateContext);
            backgroundJob = failedStateContext.ProcessedJob;
            return failedResult;
        }

        private void Requeue(IFetchedJob fetchedJob)
        {
            try
            {
                fetchedJob.Requeue();
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                _logger.WarnException($"Failed to immediately re-queue the background job '{fetchedJob.JobId}'. Next invocation may be delayed, if invisibility timeout is used", ex);
            }
        }

        private IState PerformJob(
            BackgroundProcessContext context,
            IStorageConnection connection,
            string jobId,
            BackgroundJob backgroundJob,
            List<IDisposable> disposeAfterStateChange,
            out IReadOnlyDictionary<string, object> customData)
        {
            customData = null;

            try
            {
                if (backgroundJob == null)
                {
                    var jobData = connection.GetJobData(jobId);
                    if (jobData == null)
                    {
                        // Job expired just after moving to a processing state. This is an
                        // unreal scenario, but shit happens. Returning null instead of throwing
                        // an exception and rescuing from en-queueing a poisoned jobId back
                        // to a queue.
                        return null;
                    }

                    jobData.EnsureLoaded();
                    backgroundJob = new BackgroundJob(jobId, jobData.Job, jobData.CreatedAt, jobData.ParametersSnapshot);
                }

                using (var jobToken = new ServerJobCancellationToken(connection, backgroundJob.Id, context.ServerId, WorkerGuidCache.GetOrAdd(context.ExecutionId, static guid => guid.ToString()), context.StoppedToken))
                {
                    // Filters that change the job's state themselves (Throttle, RateLimit, CircuitBreaker) use this
                    // worker's state changer, and with it the server's filter provider
                    var items = new Dictionary<string, object>
                    {
                        { ConcurrencyControl.StateChangerItem, _stateChanger },
                        { DisposeAfterStateChangeItem, disposeAfterStateChange },
                    };
                    var performContext = new PerformContext(context.Storage, connection, backgroundJob, jobToken, _profiler, context.ServerId, items);

                    var latency = (DateTime.UtcNow - backgroundJob.CreatedAt).TotalMilliseconds;
                    var duration = Stopwatch.StartNew();

                    var result = _performer.Perform(performContext);
                    duration.Stop();

                    // A thread the job started may still use the items (PerformContext.Console adds to them under
                    // this lock), so they are copied under it, and only the copy is read
                    Dictionary<string, object> data;
                    lock (performContext.Items)
                    {
                        data = new Dictionary<string, object>(performContext.Items);
                    }

                    // A filter already moved the job out of the Processing state (back to Scheduled). Changing the
                    // state again could delete the job if another worker has picked it up in the meantime.
                    if (data.ContainsKey(ConcurrencyControl.RescheduledItem))
                    {
                        return null;
                    }

                    data.Remove(ConcurrencyControl.StateChangerItem);
                    data.Remove(DisposeAfterStateChangeItem);
                    customData = data;

                    return !data.TryGetValue(BackgroundJobPerformer.ContextCanceledKey, out var filter)
                        ? (IState)new SucceededState(result, (long) latency, duration.ElapsedMilliseconds)
                        : new DeletedState { Reason = $"Canceled by filter '{filter}'" };
                }
            }
            catch (JobAbortedException)
            {
                // Background job performance was aborted due to a
                // state change, so its identifier should be removed
                // from a queue.
                return null;
            }
            catch (JobPerformanceException ex)
            {
                return new FailedState(ex.InnerException, context.ServerId)
                {
                    Reason = ex.Message
                };
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                if (ex is OperationCanceledException && context.IsStopped)
                {
                    throw;
                }

                return new FailedState(ex, context.ServerId)
                {
                    Reason = "An exception occurred during processing of a background job."
                };
            }
        }
    }
}