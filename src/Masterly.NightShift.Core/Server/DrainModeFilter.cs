#nullable enable

using System;
using System.Globalization;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;

namespace Masterly.NightShift.Server
{
    /// <summary>
    /// Enforces <see cref="DrainMode"/>: when a worker elects the Processing state for a job while the switch is
    /// on, the job goes to the Scheduled state for <see cref="DrainMode.HoldInterval"/> instead, and the worker
    /// moves on. When the hold is over, the job goes back to the queue it was in. Registered globally by default.
    /// </summary>
    public sealed class DrainModeFilter : JobFilterAttribute, IElectStateFilter
    {
        /// <summary>The reason put on held jobs.</summary>
        public const string HeldReason = "Held: processing is paused";

        /// <summary>The reason put on jobs held because their queue is paused; <c>{0}</c> is the queue.</summary>
        public const string QueueHeldReasonFormat = "Held: queue '{0}' is paused";

        /// <summary>The reason put on jobs a paused server hands back; <c>{0}</c> is the server.</summary>
        public const string ServerHeldReasonFormat = "Held: server '{0}' is paused";

        /// <summary>
        /// The job parameter that remembers the queue of a held job whose queue was given only by its Enqueued
        /// state (<c>new EnqueuedState("emails")</c> or <see cref="QueueAttribute"/>), so the job is enqueued to that
        /// queue again after the hold rather than to the default one. A job created with a queue of its own
        /// (<c>Enqueue("emails", ...)</c>) keeps it without this parameter. The parameter is cleared when the job is
        /// enqueued again after the hold, so later retries go where they would have gone without the hold. The
        /// holds of <see cref="RunWindowAttribute"/>, <see cref="ThrottleAttribute"/>, <see cref="RateLimitAttribute"/>
        /// and <see cref="CircuitBreakerAttribute"/> use it too, and each of them also puts its held jobs back in their
        /// queue, so that works without this filter. A job requeued while it was running is still in the Processing
        /// state, which names no queue: its queue is then taken from the last Enqueued state in its history.
        /// </summary>
        public const string HeldQueueParameter = "HeldQueue";

        /// <inheritdoc />
        public void OnStateElection(ElectStateContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (context.CandidateState is EnqueuedState enqueuedState)
            {
                HeldQueue.Restore(context, enqueuedState);
                return;
            }

            if (context.CandidateState is not ProcessingState processingState) return;

            var reason = GetHoldReason(context, processingState, out var queue);
            if (reason == null) return;

            HeldQueue.Remember(context, queue);
            context.CandidateState = new ScheduledState(DrainMode.HoldInterval) { Reason = reason };
        }

        private static string? GetHoldReason(ElectStateContext context, ProcessingState processingState, out string? queue)
        {
            queue = null;
            var status = DrainMode.GetStatus(context.Storage, context.Connection);

            if (status.Active) return HeldReason;

            var serverId = processingState.ServerId;
            if (status.PausedServers.Count > 0 && serverId != null && status.PausedServers.ContainsKey(serverId))
            {
                return String.Format(CultureInfo.InvariantCulture, ServerHeldReasonFormat, serverId);
            }

            if (status.PausedQueues.Count == 0) return null;

            // Only when some queue is paused: find the queue the job was fetched from
            queue = HeldQueue.GetQueue(context.Storage, context.Connection, context.BackgroundJob);
            if (queue == null || !status.PausedQueues.ContainsKey(queue)) return null;

            return String.Format(CultureInfo.InvariantCulture, QueueHeldReasonFormat, queue);
        }

    }
}
