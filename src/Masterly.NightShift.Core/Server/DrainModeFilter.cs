using System.Globalization;

namespace Masterly.NightShift.Server;

/// <summary>
/// Enforces <see cref="DrainMode"/>: when a worker elects the Processing state for a job while the switch is
/// on, the job goes to the Scheduled state for <see cref="DrainMode.HoldInterval"/> instead, and the worker
/// moves on. When the hold is over, the job goes back to the queue it was in. Registered globally by default.
/// </summary>
/// <remarks>
/// The workers of a server that is paused on its own, or fenced, don't fetch (<see cref="WorkerGate"/>), so for
/// such a server this filter is the safety net: it holds the job a worker was already waiting for when the server
/// was paused. The time of that hold doesn't count as the job's lateness, see <see cref="ServerHoldParameter"/>.
/// </remarks>
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

    /// <summary>
    /// The job parameter with the time a job was held because the server that fetched it was paused on its own or
    /// fenced: the milliseconds so far, and, while such a hold lasts, a <c>|</c> and the Unix time in milliseconds
    /// at which it began. <see cref="PerformContext.Lateness"/> leaves that time out, so
    /// <see cref="SkipIfLateAttribute"/> and <c>SkipAfter</c> don't skip a job for it. The holds of the
    /// cluster-wide switch and of a paused queue aren't kept: they count as lateness.
    /// </summary>
    public const string ServerHoldParameter = "ServerHold";

    /// <inheritdoc />
    public void OnStateElection(ElectStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.CandidateState is EnqueuedState enqueuedState)
        {
            HeldQueue.Restore(context, enqueuedState);
            ServerHold.End(context);
            return;
        }

        if (context.CandidateState is not ProcessingState processingState)
        {
            return;
        }

        string? reason = GetHoldReason(context, processingState, out string? queue, out bool byPausedServer);
        if (reason == null)
        {
            return;
        }

        HeldQueue.Remember(context, queue);

        // Only this server is in the way: the others could have run the job, so it isn't late for the hold
        if (byPausedServer)
        {
            ServerHold.Begin(context);
        }

        context.CandidateState = new ScheduledState(DrainMode.HoldInterval) { Reason = reason };
    }

    private static string? GetHoldReason(ElectStateContext context, ProcessingState processingState, out string? queue, out bool byPausedServer)
    {
        queue = null;
        byPausedServer = false;
        DrainModeStatus status = DrainMode.GetStatus(context.Storage, context.Connection);

        if (status.Active)
        {
            return HeldReason;
        }

        string serverId = processingState.ServerId;
        if (status.PausedServers.Count > 0 && serverId != null && status.PausedServers.ContainsKey(serverId))
        {
            byPausedServer = true;
            return string.Format(CultureInfo.InvariantCulture, ServerHeldReasonFormat, serverId);
        }

        if (status.PausedQueues.Count == 0)
        {
            return null;
        }

        // Only when some queue is paused: find the queue the job was fetched from
        queue = HeldQueue.GetQueue(context.Storage, context.Connection, context.BackgroundJob);
        if (queue == null || !status.PausedQueues.ContainsKey(queue))
        {
            return null;
        }

        return string.Format(CultureInfo.InvariantCulture, QueueHeldReasonFormat, queue);
    }

}
