using System.Diagnostics;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Server;

/// <summary>
/// Decides, before a worker of a server fetches, whether the server may take jobs now. It may not while the server
/// is paused on its own (<see cref="DrainMode.PauseServer"/>), while its deployment is fenced
/// (<see cref="DeploymentFencing"/>), and, with deployment fencing, before a deployment check has decided for the
/// id the server has now. A worker that fetched all the same would only hand the job back to the Scheduled state
/// for <see cref="DrainMode.HoldInterval"/>, which takes a few milliseconds while a real run takes the job's
/// duration: a paused server would get most of the jobs of the queues it shares with the servers that may work,
/// and delay each of them by the hold.
/// </summary>
/// <remarks>
/// <para>The decision of the deployment check is kept for one server id. A server that gets a new id (its heartbeat
/// found it removed, or failed for the server timeout) is undecided again, although its processes are the same
/// objects: the pause of a fenced server is on the old id. A check closes the gate as soon as it finds the server
/// fenced, before it writes anything.</para>
/// <para>A decision that the server may work holds for <see cref="DeploymentFencing.TakeOverAfter"/> (at least two
/// check intervals) and the time a check may wait for the lock of the checks, counted from the last moment the
/// server knew that no deployment could have taken over: when a check took that lock, or found that another
/// server held it all the time it waited (<see cref="Confirm"/>), and when a check decided. A takeover is written
/// under that lock, so a server whose checks can't take it doesn't stop because of it, and a check that is slow
/// but gets the lock doesn't close the gate. When the checks fail for any other reason (the storage is down) for
/// that long, another deployment may have taken over meanwhile, so the gate closes until a check succeeds.</para>
/// <para>A pause set by hand is read through the cache of <see cref="DrainMode"/>, so it costs no storage read for
/// each fetch: the storage is asked when the cached answer is older than <see cref="DrainMode.CacheDuration"/>, as
/// <see cref="DrainModeFilter"/> does for each job anyway. A worker that waits inside a fetch when its server is
/// paused doesn't notice: the job it gets is held by <see cref="DrainModeFilter"/>, which stays the safety net.
/// The cluster-wide switch (<see cref="DrainMode.Enable"/>) isn't looked at here: with every server paused nobody
/// competes for the jobs, and they are held as before.</para>
/// </remarks>
internal sealed class WorkerGate
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(WorkerGate));

    // The deployment of the server, when a deployment check must have decided before its workers fetch
    private readonly string? _deploymentId;

    // What the last deployment check decided, and for which server id
    private Decision? _decision;

    // Completed and replaced whenever the decision changes, so the workers that wait look again at once
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The server id and the reason the workers were last told to wait for, so it is logged once
    private string? _waitLogged;

    /// <summary>The gate of a server without deployment fencing: closed only while the server is paused.</summary>
    public WorkerGate()
    {
    }

    /// <summary>
    /// The gate of a server with deployment fencing: closed until a deployment check has decided for the server's
    /// id, and while its deployment is fenced.
    /// </summary>
    public WorkerGate(string deploymentId)
    {
        _deploymentId = deploymentId;
    }

    /// <summary>
    /// How long a worker of a server that may not take jobs waits before it looks again. A pause taken back from
    /// another process is noticed after this, and after the time <see cref="DrainMode"/> caches its answer. Never
    /// longer than <see cref="DrainMode.HoldInterval"/>.
    /// </summary>
    internal static TimeSpan ClosedCheckInterval
    {
        get => field < DrainMode.HoldInterval || DrainMode.HoldInterval <= TimeSpan.Zero ? field : DrainMode.HoldInterval;
        set;
    } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Records what a deployment check decided for the server it ran on: that it is fenced, as soon as the check
    /// knows it, or, once the check went all the way, that it may work.
    /// </summary>
    public void Decide(string serverId, bool fenced)
    {
        // Written again by every check, which also renews how long a decision that the server may work holds
        Volatile.Write(ref _decision, new Decision(serverId, fenced, Stopwatch.GetTimestamp()));
        Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>
    /// Records that the last decision for <paramref name="serverId"/> still stands, as no deployment could have taken
    /// over since: a check took the lock of the checks, or another server held it all the time a check waited. Does
    /// nothing when no check has decided for that id.
    /// </summary>
    public void Confirm(string serverId)
    {
        Decision? decision = Volatile.Read(ref _decision);
        if (decision != null && decision.ServerId == serverId)
        {
            Interlocked.CompareExchange(ref _decision, decision with { At = Stopwatch.GetTimestamp() }, decision);
        }
    }

    /// <summary>Whether a deployment check has decided for this server id, or none is needed.</summary>
    public bool IsDecided(string serverId)
    {
        return _deploymentId == null || Volatile.Read(ref _decision)?.ServerId == serverId;
    }

    /// <summary>
    /// Whether the server of <paramref name="context"/> may fetch a job now. When it may not, the call returns
    /// <c>false</c> after it has waited for the decision to change, for <see cref="ClosedCheckInterval"/> at most:
    /// the caller asks again.
    /// </summary>
    /// <exception cref="OperationCanceledException">The server is stopping.</exception>
    public bool WaitToFetch(BackgroundProcessContext context)
    {
        // Read before the state: a change made after it ends the wait at once
        Task changed = Volatile.Read(ref _changed).Task;
        Reason? reason = GetReasonToWait(context);

        if (reason == null)
        {
            // Said again when the server has to wait again
            if (Volatile.Read(ref _waitLogged) != null)
            {
                Volatile.Write(ref _waitLogged, null);
            }

            return true;
        }

        string logged = $"{context.ServerId}: {reason.Text}";
        if (Interlocked.Exchange(ref _waitLogged, logged) != logged)
        {
            if (reason.Warn)
            {
                Logger.Warn(reason.Text);
            }
            else
            {
                Logger.Debug(reason.Text);
            }
        }

        changed.Wait(ClosedCheckInterval, context.StoppingToken);
        return false;
    }

    private Reason? GetReasonToWait(BackgroundProcessContext context)
    {
        string serverId = context.ServerId;

        if (_deploymentId != null)
        {
            Decision? decision = Volatile.Read(ref _decision);
            if (decision == null || decision.ServerId != serverId)
            {
                return new Reason($"Server '{serverId}' takes no jobs until its first deployment check has decided whether deployment '{_deploymentId}' is fenced.");
            }

            if (decision.Fenced)
            {
                return new Reason($"Server '{serverId}' of deployment '{_deploymentId}' is fenced: it takes no jobs until its deployment is the current one.");
            }

            TimeSpan since = Stopwatch.GetElapsedTime(decision.At);
            TimeSpan holds = DeploymentFencing.EffectiveTakeOverAfter + DeploymentFencing.LockTimeout;
            if (since > holds)
            {
                return new Reason(
                    // No elapsed time in the text: the text decides whether it is logged again, once per change
                    $"Server '{serverId}' takes no jobs: no deployment check of deployment '{_deploymentId}' has succeeded, or found the lock of the checks held by another server, for longer than {holds}, after which another deployment may have taken over, so it can't tell whether it is still the current one. It takes jobs again once a check succeeds.",
                    Warn: true);
            }
        }

        return IsPaused(context)
            ? new Reason($"Server '{serverId}' is paused: it takes no jobs until it is resumed.")
            : null;
    }

    // Only whether this server is paused on its own. A storage that can't be read doesn't close the gate: the
    // fetch that follows says what is wrong with it, and DrainModeFilter still holds the job of a paused server
    private static bool IsPaused(BackgroundProcessContext context)
    {
        try
        {
            DrainModeStatus? status = DrainMode.GetCachedStatus(context.Storage);
            if (status == null)
            {
                using IStorageConnection connection = context.Storage.GetConnection();
                status = DrainMode.GetStatus(context.Storage, connection);
            }

            return status.PausedServers.Count > 0 && status.PausedServers.ContainsKey(context.ServerId);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException($"Could not read whether server '{context.ServerId}' is paused, so its workers go on.", ex);
            return false;
        }
    }

    // At: when it was decided or last confirmed, as a Stopwatch timestamp
    private sealed record Decision(string ServerId, bool Fenced, long At);

    private sealed record Reason(string Text, bool Warn = false);
}

/// <summary>
/// A worker behind the <see cref="WorkerGate"/> of its server: it fetches only while the server may take jobs.
/// While it may, the gate costs a read of two fields and of a cached answer for each job.
/// </summary>
internal sealed class GatedWorker(IBackgroundProcess worker, WorkerGate gate) : IBackgroundProcess
{
    public void Execute(BackgroundProcessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (gate.WaitToFetch(context))
        {
            worker.Execute(context);
        }
    }
}
