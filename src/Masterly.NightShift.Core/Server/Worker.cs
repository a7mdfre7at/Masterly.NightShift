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

using System.Diagnostics;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Profiling;

namespace Masterly.NightShift.Server;

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
    private static readonly string[] EligibleWorkerStates = [EnqueuedState.StateName, ScheduledState.StateName, ProcessingState.StateName];
    private static readonly string[] ProcessingStateArray = [ProcessingState.StateName];

    /// <summary>
    /// <see cref="PerformContext.Items"/> entry holding an <c>ICollection&lt;IDisposable&gt;</c> the worker
    /// disposes after the state change that follows the job (or when processing the job ends otherwise), so a
    /// server filter can keep something open until then: the job logging scope of Masterly.NightShift.NetCore
    /// adds itself, so what state filters log about the job (retries, deadlines) is in the job's scope. It is
    /// left out of the state change's custom data. A filter that finds no entry (a performer called without a
    /// worker) disposes it itself.
    /// </summary>
    internal const string DisposeAfterStateChangeItem = "X_NS_DisposeAfterStateChange";

    // How often a job that this worker must give up is put back in its queue before that is left to the storage
    private const int RequeueAttempts = 3;
    private static readonly TimeSpan RequeueRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly TimeSpan _jobInitializationWaitTimeout;
    private readonly int _maxStateChangeAttempts;

    // Shared by the threads that run this worker, a pool: what their waiting with a job in hand may cost it
    private readonly WorkerWaits _waits;

    private readonly ILog _logger = LogProvider.For<Worker>();

    // Whether this worker warned that its storage reports a queue entry id it can't give (once is enough)
    private int _queueEntryUnsupportedLogged;

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
        : this(queues, performer, stateChanger, jobInitializationTimeout, maxStateChangeAttempts, new WorkerWaits())
    {
    }

    internal Worker(
        [NotNull] IEnumerable<string> queues,
        [NotNull] IBackgroundJobPerformer performer,
        [NotNull] IBackgroundJobStateChanger stateChanger,
        TimeSpan jobInitializationTimeout,
        int maxStateChangeAttempts,
        [NotNull] WorkerWaits waits)
    {
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(performer);
        ArgumentNullException.ThrowIfNull(stateChanger);

        _queues = queues;
        _performer = performer;

        LongRunningAttribute.NoteLocalWorker(queues);
        _stateChanger = stateChanger;

        _jobInitializationWaitTimeout = jobInitializationTimeout;
        _maxStateChangeAttempts = maxStateChangeAttempts;
        _waits = waits ?? throw new ArgumentNullException(nameof(waits));

        _profiler = new SlowLogProfiler(_logger);
    }

    /// <inheritdoc />
    public void Execute(BackgroundProcessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        using IStorageConnection connection = context.Storage.GetConnection();
        IFetchedJob fetchedJob = Fetch(connection, context.StoppingToken);
        using FetchedJobScope fetched = new(fetchedJob);
        bool requeueOnException = true;
        List<IDisposable> disposeAfterStateChange = [];

        // The same id names this worker in the Processing state, in the lease its final state change checks, and
        // in the job's cancellation token
        string workerId = GetWorkerId(context);

        try
        {
            BackgroundJob? backgroundJob = null;
            string? queueEntry = GetQueueEntry(context.Storage, connection, fetchedJob);

            using (CancellationTokenSource timeoutCts = new(_jobInitializationWaitTimeout))
            using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                context.StoppingToken,
                timeoutCts.Token))
            {
                IState? appliedState = StartProcessing(context, connection, fetchedJob.JobId, workerId, queueEntry, linkedCts.Token, out backgroundJob, out bool stopped);

                // The server stops while this worker waited to look at the job again: nothing was stored about
                // the job, so it goes back to its queue as it was, for the next server
                if (stopped)
                {
                    requeueOnException = false;
                    fetched.Disposed = PutBack(fetchedJob, context);
                    return;
                }

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

            IState? state = PerformJob(context, connection, fetchedJob.JobId, workerId, backgroundJob!, disposeAfterStateChange, out IReadOnlyDictionary<string, object>? customData); // Loaded by the change to Processing above
            bool transactionalAck = context.Storage.HasFeature(JobStorageFeatures.Transaction.RemoveFromQueue(fetchedJob.GetType()));

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
                    context.ShutdownToken,
                    workerId);
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
                string action = requeueOnException ? "It will be re-queued" : "It will be removed from queue later";
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

    private IFetchedJob Fetch(IStorageConnection connection, CancellationToken stoppingToken)
    {
        // While this thread looks for a job, the waits of the other threads of the pool cost the pool nothing
        _waits.EnterFetch();

        try
        {
            return connection.FetchNextJob([.. _queues], stoppingToken);
        }
        finally
        {
            _waits.LeaveFetch();
        }
    }

    /// <summary>
    /// Moves the fetched job to the Processing state. A filter may answer "not now, look again in a moment" (see
    /// <see cref="JobLookAgain"/>): nothing is stored then, the job's lock is released, and this thread keeps the
    /// job, waits and changes the state again. The job stays fetched meanwhile, invisible to the other workers
    /// and kept so by the storage as while it runs: it isn't put back in its queue and fetched again at each
    /// look. The worker offers that only while its pool can afford the wait and the job hasn't been waited for
    /// for too long (<see cref="WorkerWaits"/>); otherwise the filter decides another way. <c>stopped</c> says
    /// that the server stopped while this thread waited: the job is as it was.
    /// </summary>
    private IState? StartProcessing(
        BackgroundProcessContext context,
        IStorageConnection connection,
        string jobId,
        string workerId,
        string? queueEntry,
        CancellationToken initializeToken,
        out BackgroundJob? backgroundJob,
        out bool stopped)
    {
        JobLookAgain lookAgain = new();
        long began = 0;
        TimeSpan waited = TimeSpan.Zero;
        bool asked = false;
        bool keeping = false;
        stopped = false;

        try
        {
            while (true)
            {
                // While it keeps the job, the pool counts the time, the looks included: offered while the budget
                // lasts and the job hasn't been kept too long
                bool offered = waited < _waits.MaxPerJob && _waits.CanWait;
                IState? appliedState;

                try
                {
                    // A state of its own for each look: it says when the job started, which is when the look
                    // that applies it is made, not when the worker took the job
                    appliedState = TryChangeState(
                        context,
                        connection,
                        jobId,
                        new ProcessingState(context.ServerId, workerId) { QueueEntry = queueEntry },
                        null,
                        EligibleWorkerStates,
                        null,
                        out backgroundJob,
                        initializeToken,
                        context.StoppingToken,
                        lookAgain: offered ? lookAgain : null,
                        queueEntry: queueEntry);
                }
                catch (OperationCanceledException) when (asked && context.StoppingToken.IsCancellationRequested)
                {
                    // The server stops while a look at a job this worker keeps is tried again: as a stop during
                    // a pause, nothing was stored about the job, and it goes back to its queue as it was
                    backgroundJob = null;
                    stopped = true;
                    return null;
                }

                if (appliedState != null || !offered || !lookAgain.Requested)
                {
                    return appliedState;
                }

                asked = true;

                if (!keeping)
                {
                    // Refused when the budget ran out since: the next look comes without the offer
                    keeping = _waits.Enter();
                    began = _waits.Timestamp();
                }

                // Told how long by the pool: not at all when its budget was used up meanwhile
                TimeSpan pause = keeping ? _waits.Allow(Pause(lookAgain.Pause, waited, _waits.MaxPerJob)) : TimeSpan.Zero;
                if (pause > TimeSpan.Zero)
                {
                    // Ends at once when the server stops
                    _waits.Sleep(pause, context.StoppingToken);
                }

                if (context.StoppingToken.IsCancellationRequested)
                {
                    stopped = true;
                    return null;
                }

                // By the clock of the machine (the pool's): the application's clock may stand still
                waited = keeping ? _waits.ElapsedSince(began) : _waits.MaxPerJob;
            }
        }
        finally
        {
            if (keeping)
            {
                _waits.Leave();
            }
        }
    }

    // As long as the filter asked for, and no shorter than this job has been waited for so far: so the looks get
    // rarer whatever the filter goes by. Within bounds, and not past the time one job is waited for.
    private static TimeSpan Pause(TimeSpan asked, TimeSpan waited, TimeSpan maxPerJob)
    {
        TimeSpan pause = asked > waited ? asked : waited;
        pause = pause < WorkerWaits.MinPause ? WorkerWaits.MinPause : pause > WorkerWaits.MaxPause ? WorkerWaits.MaxPause : pause;

        TimeSpan left = maxPerJob - waited;
        return pause > left ? left : pause;
    }

    /// <summary>
    /// Puts back in its queue a job this worker waited for and whose state it didn't change, because the server
    /// stops. Tried a few times: a job that stays fetched is invisible until the storage's invisibility timeout
    /// has passed, half an hour by default on most storages. Never throws: an exception here would send this
    /// thread into the delay of a process that failed, for a job that didn't.
    /// </summary>
    /// <returns>Whether the fetched job was disposed here, as the last attempt: it isn't disposed again.</returns>
    private bool PutBack(IFetchedJob fetchedJob, BackgroundProcessContext context)
    {
        for (int attempt = 1; attempt <= RequeueAttempts; attempt++)
        {
            try
            {
                fetchedJob.Requeue();
                return false;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                _logger.WarnException($"Could not put background job '{fetchedJob.JobId}' back in its queue while the server stops (attempt {attempt} of {RequeueAttempts}).", ex);
            }

            // The server is stopping already: the wait goes by the token that says it must be gone
            if (attempt < RequeueAttempts)
            {
                context.ShutdownToken.Wait(RequeueRetryDelay * attempt);
            }
        }

        // Disposing the fetched job puts it back too, on the storages that can: done here, so that what it
        // throws doesn't leave Execute
        try
        {
            fetchedJob.Dispose();
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            _logger.WarnException($"Background job '{fetchedJob.JobId}' could not be put back in its queue: it is fetched again once the storage's invisibility timeout has passed.", ex);
        }

        return true;
    }

    /// <summary>Disposes the fetched job when the worker is done with it, unless that was done already.</summary>
    private sealed class FetchedJobScope(IFetchedJob fetchedJob) : IDisposable
    {
        public bool Disposed { get; set; }

        public void Dispose()
        {
            if (!Disposed)
            {
                Disposed = true;
                fetchedJob.Dispose();
            }
        }
    }

    /// <summary>
    /// The queue entry the job was fetched from, where the storage tells it: so the job runs once when it has
    /// several entries (see <see cref="JobStorageConnection.GetQueueEntryId"/>).
    /// </summary>
    private string? GetQueueEntry(JobStorage storage, IStorageConnection connection, IFetchedJob fetchedJob)
    {
        if (connection is not JobStorageConnection storageConnection || !storage.HasFeature(JobStorageFeatures.Connection.GetQueueEntryId))
        {
            return null;
        }

        try
        {
            return storageConnection.GetQueueEntryId(fetchedJob);
        }
        catch (NotSupportedException ex)
        {
            // A storage that reports the feature without the member (a wrapper that passes HasFeature on, say)
            // would otherwise fail every job it hands out: the job runs as without the feature
            if (Interlocked.Exchange(ref _queueEntryUnsupportedLogged, 1) == 0)
            {
                _logger.WarnException(
                    $"The storage {storage} reports {JobStorageFeatures.Connection.GetQueueEntryId} but its connection doesn't support it, so a job with more than one queue entry can run twice at once.",
                    ex);
            }

            return null;
        }
    }

    /// <summary>
    /// The id of the worker running in <paramref name="context"/>. Not cached: every run of the worker's loop has a
    /// new execution id, so a cache would grow with every restart.
    /// </summary>
    private static string GetWorkerId(BackgroundProcessContext context)
    {
        return context.ExecutionId.ToString();
    }

    private void DisposeAfterStateChange(List<IDisposable> disposables)
    {
        // In reverse order, like nested using blocks
        for (int i = disposables.Count - 1; i >= 0; i--)
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

    private IState? TryChangeState(
        BackgroundProcessContext context,
        IStorageConnection connection,
        string jobId,
        IState state,
        IReadOnlyDictionary<string, object>? customData,
        string[] expectedStates,
        IFetchedJob? completeJob,
        out BackgroundJob? backgroundJob,
        CancellationToken initializeToken,
        CancellationToken abortToken,
        string? leaseHolder = null,
        JobLookAgain? lookAgain = null,
        string? queueEntry = null)
    {
        Exception? exception = null;

        abortToken.ThrowIfCancellationRequested();

        // At least one retry attempt should always be performed.
        int maxRetryAttempts = _maxStateChangeAttempts > 0 ? _maxStateChangeAttempts : 1;

        for (int retryAttempt = 0; retryAttempt < maxRetryAttempts; retryAttempt++)
        {
            try
            {
                StateChangeContext stateChangeContext = new(
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
                    customData)
                {
                    LeaseHolder = leaseHolder,
                    LookAgain = lookAgain,
                    QueueEntry = queueEntry,
                };

                IState? resultingState = _stateChanger.ChangeState(stateChangeContext);

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

        // What the last attempt asked before it threw doesn't hold for the state change of last resort, which
        // offers nothing
        lookAgain?.Reset();

        StateChangeContext failedStateContext = new(
            context.Storage,
            connection,
            null,
            jobId,
            new FailedState(exception!, context.ServerId) { Reason = $"Failed to change state to a '{state.Name}' one due to an exception after {_maxStateChangeAttempts} retry attempts" }, // Set by the failed attempts above
            expectedStates,
            disableFilters: true,
            completeJob,
            initializeToken,
            _profiler,
            context.ServerId)
        {
            LeaseHolder = leaseHolder,
            QueueEntry = queueEntry,
        };

        IState? failedResult = _stateChanger.ChangeState(failedStateContext);
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

    private IState? PerformJob(
        BackgroundProcessContext context,
        IStorageConnection connection,
        string jobId,
        string workerId,
        BackgroundJob backgroundJob,
        List<IDisposable> disposeAfterStateChange,
        out IReadOnlyDictionary<string, object>? customData)
    {
        customData = null;

        try
        {
            if (backgroundJob == null)
            {
                JobData? jobData = connection.GetJobData(jobId);
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

            using ServerJobCancellationToken jobToken = new(connection, backgroundJob.Id, context.ServerId, workerId, context.StoppedToken);
            // Filters that change the job's state themselves (Throttle, RateLimit, CircuitBreaker) use this
            // worker's state changer, and with it the server's filter provider
            Dictionary<string, object> items = new()
            {
                    { ConcurrencyControl.StateChangerItem, _stateChanger },
                    { DisposeAfterStateChangeItem, disposeAfterStateChange },
                };
            PerformContext performContext = new(context.Storage, connection, backgroundJob, jobToken, _profiler, context.ServerId, items);

            double latency = (JobClock.UtcNow - backgroundJob.CreatedAt).TotalMilliseconds;
            Stopwatch duration = Stopwatch.StartNew();

            object? result = _performer.Perform(performContext);
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

            return !data.TryGetValue(BackgroundJobPerformer.ContextCanceledKey, out object? filter)
                ? (IState)new SucceededState(result, (long)latency, duration.ElapsedMilliseconds)
                : new DeletedState { Reason = $"Canceled by filter '{filter}'" };
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
            return new FailedState(ex.InnerException!, context.ServerId) // Wraps the exception the job threw
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