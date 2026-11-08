#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Server
{
    /// <summary>
    /// Keeps the queue of a held job: a job moved to the Scheduled state instead of running (<see cref="DrainModeFilter"/>,
    /// <see cref="RunWindowAttribute"/>, <see cref="ThrottleAttribute"/>, <see cref="RateLimitAttribute"/>,
    /// <see cref="CircuitBreakerAttribute"/>). When the hold is over, the delayed job scheduler enqueues the job with a
    /// new Enqueued state for the default queue. A job created with a queue of its own goes back to it anyway, but a
    /// queue given only by the Enqueued state (<c>new EnqueuedState("emails")</c>) would be lost, so it is kept in the
    /// <see cref="DrainModeFilter.HeldQueueParameter"/> job parameter and applied to the next Enqueued state that comes
    /// from the Scheduled one, once. Every filter that holds jobs applies it, so it works without
    /// <see cref="DrainModeFilter"/> too.
    /// </summary>
    internal static class HeldQueue
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(HeldQueue));
        private static readonly object Marker = new object();

        // The queue each running job was fetched from, for jobs without a queue of their own: recorded when the job
        // moves to Processing (its previous state is still the current one then), for filters that may hold the job
        // while it runs. An empty string means the default queue.
        private static readonly ConditionalWeakTable<BackgroundJob, string> FetchedQueues = new ConditionalWeakTable<BackgroundJob, string>();

        // The elections in which Restore already ran: every holding filter calls it, the first one does the work
        private static readonly ConditionalWeakTable<ElectStateContext, object> RestoredElections = new ConditionalWeakTable<ElectStateContext, object>();

        /// <summary>
        /// Remembers in memory the queue a job is fetched from, during the election of the Processing state, so that
        /// <see cref="Remember(PerformContext)"/> can keep it. Reads the current state once per job run, so only the
        /// filters that may hold a running job call it, and only for jobs without a queue of their own.
        /// </summary>
        public static void RecordFetchedQueue(ElectStateContext context)
        {
            if (context.CandidateState is not ProcessingState) return;
            if (context.BackgroundJob.Job == null || context.BackgroundJob.Job.Queue != null) return;

            // A job requeued while it was running (a shutdown, a crash) is fetched again in the Processing state
            if (!EnqueuedState.StateName.Equals(context.CurrentState, StringComparison.OrdinalIgnoreCase)
                && !ProcessingState.StateName.Equals(context.CurrentState, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (FetchedQueues)
            {
                // Several filters on one job record it once
                if (FetchedQueues.TryGetValue(context.BackgroundJob, out _)) return;
            }

            var queue = GetQueue(context.Storage, context.Connection, context.BackgroundJob) ?? String.Empty;

            lock (FetchedQueues)
            {
                if (!FetchedQueues.TryGetValue(context.BackgroundJob, out _))
                {
                    FetchedQueues.Add(context.BackgroundJob, queue);
                }
            }
        }

        /// <summary>
        /// Remembers the queue of a job held during the election of the Processing state, while the Enqueued state is
        /// still the current one. <paramref name="queue"/> may be given when the caller has read it already.
        /// </summary>
        public static void Remember(ElectStateContext context, string? queue = null)
        {
            if (context.BackgroundJob.Job?.Queue != null) return;

            queue ??= GetQueue(context.Storage, context.Connection, context.BackgroundJob);
            Write(context.Connection, context.BackgroundJob, queue);
        }

        /// <summary>
        /// Remembers the queue of a running job that a filter holds, as recorded by <see cref="RecordFetchedQueue"/>.
        /// </summary>
        public static void Remember(PerformContext context)
        {
            if (context.BackgroundJob.Job?.Queue != null) return;

            string? queue;
            lock (FetchedQueues)
            {
                if (!FetchedQueues.TryGetValue(context.BackgroundJob, out queue)) return;
            }

            Write(context.Connection, context.BackgroundJob, queue);
        }

        /// <summary>
        /// During the election of an Enqueued state that comes from the Scheduled one: puts a held job back in the
        /// queue it was held from, when the new state names the default queue, and forgets that queue in the same
        /// transaction, so later returns to the default queue (a retry, an explicit change) stay there. Every filter
        /// that holds jobs calls it; only the first call of an election does anything.
        /// </summary>
        public static void Restore(ElectStateContext context, EnqueuedState enqueuedState)
        {
            // The parameter snapshot (when the storage provides one) spares a read
            if (context.BackgroundJob.Job?.Queue != null) return;
            if (!ScheduledState.StateName.Equals(context.CurrentState, StringComparison.OrdinalIgnoreCase)) return;

            lock (RestoredElections)
            {
                if (RestoredElections.TryGetValue(context, out _)) return;
                RestoredElections.Add(context, Marker);
            }

            var queue = Read(context);
            if (String.IsNullOrEmpty(queue)) return;

            if (EnqueuedState.DefaultQueue.Equals(enqueuedState.Queue, StringComparison.OrdinalIgnoreCase)
                && !String.IsNullOrWhiteSpace(queue)
                && EnqueuedState.TryValidateQueueName(queue!))
            {
                enqueuedState.Queue = queue!;
            }

            Forget(context);
        }

        private static void Write(IStorageConnection connection, BackgroundJob job, string? queue)
        {
            if (String.IsNullOrEmpty(queue) || EnqueuedState.DefaultQueue.Equals(queue, StringComparison.OrdinalIgnoreCase)) return;

            // A job held again and again writes the parameter once
            if (String.Equals(Read(connection, job), queue, StringComparison.Ordinal)) return;

            connection.SetJobParameter(job.Id, DrainModeFilter.HeldQueueParameter, SerializationHelper.Serialize(queue, SerializationOption.User));
        }

        private static void Forget(ElectStateContext context)
        {
            // In the state change's transaction when the storage can, so a state change that fails keeps the queue
            if (context.Transaction is JobStorageTransaction transaction
                && context.Storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter))
            {
                transaction.SetJobParameter(context.BackgroundJob.Id, DrainModeFilter.HeldQueueParameter, null);
            }
            else
            {
                context.Connection.SetJobParameter(context.BackgroundJob.Id, DrainModeFilter.HeldQueueParameter, null);
            }
        }

        private static string? Read(ElectStateContext context)
        {
            try
            {
                return context.GetJobParameter<string>(DrainModeFilter.HeldQueueParameter, allowStale: true);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // An unreadable parameter counts as none
                return null;
            }
        }

        private static string? Read(IStorageConnection connection, BackgroundJob job)
        {
            try
            {
                string? value;

                if (job.ParametersSnapshot == null || !job.ParametersSnapshot.TryGetValue(DrainModeFilter.HeldQueueParameter, out value))
                {
                    value = connection.GetJobParameter(job.Id, DrainModeFilter.HeldQueueParameter);
                }

                return SerializationHelper.Deserialize<string>(value, SerializationOption.User);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return null;
            }
        }

        /// <summary>
        /// The queue a job is enqueued to, resolved like the Enqueued state handler does: the current state's queue,
        /// unless it is the default one and the job has a queue of its own. A job requeued while it was running (a
        /// shutdown, a crash) is still in the Processing state, which names no queue: then the queue of the last
        /// Enqueued state in its history counts (one more read, only on that path). <c>null</c> when it can't be read.
        /// </summary>
        public static string? GetQueue(JobStorage? storage, IStorageConnection connection, BackgroundJob job)
        {
            var jobQueue = job.Job?.Queue;
            string? stateQueue = null;

            try
            {
                var state = connection.GetStateData(job.Id);
                stateQueue = GetStateQueue(state?.Data);

                if (stateQueue == null && state != null && ProcessingState.StateName.Equals(state.Name, StringComparison.OrdinalIgnoreCase))
                {
                    stateQueue = GetLastEnqueuedQueue(storage, job.Id);
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // A storage hiccup must not hold the job
                Logger.DebugException($"Could not read the state of background job '{job.Id}' to find its queue.", ex);
            }

            if (stateQueue == null) return jobQueue;
            if (jobQueue != null && EnqueuedState.DefaultQueue.Equals(stateQueue, StringComparison.OrdinalIgnoreCase)) return jobQueue;

            return stateQueue;
        }

        // The queue of the latest Enqueued state in the job's history, read through the monitoring API that every
        // storage implements
        private static string? GetLastEnqueuedQueue(JobStorage? storage, string jobId)
        {
            if (storage == null) return null;

            try
            {
                var history = storage.GetMonitoringApi().JobDetails(jobId)?.History;
                if (history == null) return null;

                // Storages list the newest state first; the time decides, and the first one listed wins a tie
                StateHistoryDto? latest = null;
                foreach (var entry in history)
                {
                    if (entry == null || !EnqueuedState.StateName.Equals(entry.StateName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (latest == null || entry.CreatedAt > latest.CreatedAt) latest = entry;
                }

                return GetStateQueue(latest?.Data);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.DebugException($"Could not read the history of background job '{jobId}' to find its queue.", ex);
                return null;
            }
        }

        private static string? GetStateQueue(IDictionary<string, string>? data)
        {
            return data != null && data.TryGetValue("Queue", out var queue) && !String.IsNullOrEmpty(queue) ? queue : null;
        }
    }
}
