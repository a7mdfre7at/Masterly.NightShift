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
    /// The job parameter with the times a job was held because the server that fetched it was paused on its own or
    /// fenced: the holds, oldest first and separated by <c>;</c>, each the Unix time in milliseconds at which it
    /// began, a <c>-</c>, and the time it ended, missing while it lasts. A hold ends when the job leaves the
    /// Scheduled state it was held in, whatever state follows. <see cref="PerformContext.Lateness"/> leaves out the
    /// part of the holds after the moment the job was meant to run, so <see cref="SkipIfLateAttribute"/> and
    /// <c>SkipAfter</c> don't skip a job for it. The holds of the cluster-wide switch and of a paused queue aren't
    /// kept: they count as lateness.
    /// </summary>
    public const string ServerHoldParameter = "ServerHold";

    /// <inheritdoc />
    public void OnStateElection(ElectStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.CandidateState is EnqueuedState enqueuedState)
        {
            HeldQueue.Restore(context, enqueuedState);
        }

        string? reason = null;
        string? queue = null;
        bool byPausedServer = false;
        if (context.CandidateState is ProcessingState processingState)
        {
            reason = GetHoldReason(context, processingState, out queue, out byPausedServer);
        }

        // Only this server is in the way: the others could have run the job, so it isn't late for the hold. In
        // every other case the job leaves the Scheduled state a paused server may have held it in, whatever
        // follows: enqueued again, moved by its schedule policy (which runs before this filter), deleted, taken by
        // a server, or held by the cluster-wide switch
        if (byPausedServer)
        {
            ServerHold.Begin(context);
        }
        else
        {
            ServerHold.End(context);
        }

        if (reason == null)
        {
            return;
        }

        HeldQueue.Remember(context, queue);

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
