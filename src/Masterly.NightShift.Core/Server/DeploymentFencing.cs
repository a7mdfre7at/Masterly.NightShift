using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Keeps servers of an older deployment from taking jobs once a newer one runs, turned on with
/// <c>UseDeploymentFencing</c>. The first server of a deployment the cluster hasn't seen claims the current deployment
/// (the <c>Current</c> field of the hash <see cref="HashKey"/>), and servers of the others pause themselves, as
/// <see cref="DrainMode.PauseServer"/> does, with the reason <c>Fenced: ...</c>: they fetch no jobs, which stay in
/// their queues for the current servers, and their running jobs finish.
/// </summary>
/// <remarks>
/// <para>Every server writes a new value to its deployment's <c>Seen:{id}</c> field at each check. A deployment id
/// the cluster has never seen takes over at once, even while servers of the current deployment are alive: fencing
/// can't tell versions apart, so an unseen id is taken to be the newer one. A server started by mistake with an id
/// never used (an old build with a new id, a developer's machine pointed at the wrong storage) fences the current
/// servers, and a warning is logged when such a takeover pauses live servers. A deployment seen before (an old
/// version restarted, a rollback) takes over only once the current deployment has been silent for
/// <see cref="TakeOverAfter"/>, so a crash-looping old server doesn't fence the new ones.</para>
/// <para>Whether a deployment is alive doesn't depend on the clocks of the servers: each server watches the
/// <c>Seen</c> value of the current deployment and measures, with its own monotonic clock, how long it has stayed
/// the same. A server that has just started hasn't watched yet, so it takes the current deployment to be alive
/// until it has seen its value unchanged for <see cref="TakeOverAfter"/>; a deployment never seen, or one with a
/// higher order, still takes over at once. Silence only counts while the server itself was checking: when more
/// than one and a half times <see cref="CheckInterval"/> passed since its last check that succeeded (the storage
/// was down, the process was suspended), it couldn't see what the others did meanwhile, so it starts watching
/// again, as a server that has just started does. A fenced server whose checks are always that far apart (each
/// takes more than half a <see cref="CheckInterval"/>, because the storage is slow or the process gets too little
/// processor time) therefore never takes over: after three such checks in a row it logs a warning that says so,
/// and repeats it at most once an hour. Raising <see cref="CheckInterval"/>, and <see cref="TakeOverAfter"/> with
/// it, or making the storage answer faster resolves it.</para>
/// <para>A fenced server doesn't fetch: its workers wait until a check finds its deployment to be the current one
/// (<see cref="WorkerGate"/>). A fenced server that kept fetching would hand each job back for
/// <see cref="DrainMode.HoldInterval"/>, which is so much faster than running one that it would get most of the
/// jobs of the queues it shares with the current servers. A worker that was waiting for a job when its server
/// was fenced takes one more, which is held that way; the time it is held doesn't count as lateness. Nor does a
/// server fetch before a check has decided for the id it has now: not when it starts, and not when it got a new
/// id because its heartbeat found it removed or failed for the server timeout. While that check fails (the
/// storage is down), the server keeps taking no jobs, logs a warning, and tries the check again. The pause of
/// an id that is given up, on a restart or when the server stops, is taken back.</para>
/// <para>Deployment ids are compared without regard to case, and their fields are named after the id in lower
/// case: <c>Release-7</c> and <c>release-7</c> are one deployment on every storage, also where the storage
/// itself tells field names apart by case or, as SQL Server with a case-insensitive collation, doesn't.</para>
/// <para>A deployment order (<see cref="DeploymentOrder"/>, the second argument of <c>UseDeploymentFencing</c>)
/// tells versions apart: a number that grows with each deploy, kept in the <c>Order:{id}</c> field. When this
/// deployment and the current one both have an order, the higher one wins: a deployment with a higher order takes
/// over at once, seen before or not, and one with a lower order (a stray instance of an old image, an old build
/// deployed by accident) never takes over while servers of the current deployment are alive, only once they have
/// been silent for <see cref="TakeOverAfter"/>. With equal orders, or when either deployment has none, the rules
/// above apply. A deployment id has one order: when servers of the same id are started with different orders, or
/// some without one, the highest applies to all of them, and each server that was given another logs a warning.</para>
/// <para>The fields of a deployment that isn't the current one, shows no sign of life and was last seen more than
/// seven days ago are removed, so the hash doesn't grow with every release. Such a deployment counts as never seen
/// when it comes back.</para>
/// <para>To roll back on purpose, deploy with a new deployment id and, with orders, a higher order.</para>
/// </remarks>
public static class DeploymentFencing
{
    /// <summary>The hash with the current deployment and the last check of each deployment.</summary>
    public const string HashKey = "nightshift:deployment";

    /// <summary>The start of the reason a fenced server is paused with.</summary>
    public const string FencedReasonPrefix = "Fenced: ";

    /// <summary>The longest deployment id.</summary>
    public const int MaxDeploymentIdLength = 64;

    internal const string CurrentField = "Current";

    // A deployment id has at most 64 characters, so these fields are never shortened and end with the id
    internal const string SeenPrefix = "Seen:";
    private const string OrderPrefix = "Order:";
    private const int MaxFieldLength = 100;
    private const char SeenSeparator = '|';

    /// <summary>How long a deployment must have been gone before its fields are removed from the hash.</summary>
    internal static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(7);

    // A check interval read from the storage counts up to this; a longer one is taken for damaged data
    private static readonly TimeSpan LongestPublishedCheckInterval = TimeSpan.FromDays(1);

    /// <summary>The deployment of the servers of this process, or <c>null</c> when fencing is off.</summary>
    public static string? DeploymentId { get; internal set; }

    /// <summary>
    /// The order the deployment of this process was given among the deployments, or <c>null</c> when none was
    /// given: a number that grows with each deploy, such as the pipeline's run number.
    /// </summary>
    public static long? DeploymentOrder { get; internal set; }

    /// <summary>
    /// How often a server checks the deployment. 15 seconds. Each server publishes its interval with its check, so
    /// the servers of other deployments wait for at least two of them before they take it to be silent.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public static TimeSpan CheckInterval
    {
        get;
        set => field = value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "CheckInterval must be greater than zero.");
    } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the current deployment must be silent before a deployment seen before, or one with a lower order,
    /// takes over. 30 seconds. At least two checks of the current deployment must be missed, so a value below twice
    /// <see cref="CheckInterval"/> isn't refused but counts as twice <see cref="CheckInterval"/>, and the server
    /// logs a warning that says so.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public static TimeSpan TakeOverAfter
    {
        get;
        set => field = value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "TakeOverAfter must be greater than zero.");
    } = TimeSpan.FromSeconds(30);

    /// <summary>How long a check waits for the lock of <see cref="HashKey"/>. 15 seconds. Replaceable for tests.</summary>
    internal static TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary><see cref="TakeOverAfter"/>, or twice <see cref="CheckInterval"/> when that is longer.</summary>
    internal static TimeSpan EffectiveTakeOverAfter
    {
        get
        {
            TimeSpan twoChecks = CheckInterval + CheckInterval;
            return TakeOverAfter > twoChecks ? TakeOverAfter : twoChecks;
        }
    }

    /// <summary>The deployment the cluster runs, or <c>null</c> when no fenced server has run yet.</summary>
    [CanBeNull]
    public static string? GetCurrentDeployment([NotNull] JobStorage storage)
    {
        return ReadCurrent(storage).Id;
    }

    /// <summary>
    /// The order of the deployment the cluster runs, or <c>null</c> when it was deployed without one or no fenced
    /// server has run yet.
    /// </summary>
    [CanBeNull]
    public static long? GetCurrentDeploymentOrder([NotNull] JobStorage storage)
    {
        return ReadCurrent(storage).Order;
    }

    /// <summary>The deployment the cluster runs and its order, in one read.</summary>
    internal static (string? Id, long? Order) ReadCurrent([NotNull] JobStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        using IStorageConnection connection = storage.GetConnection();
        Dictionary<string, string>? hash = connection.GetAllEntriesFromHash(HashKey);

        return hash?.GetValueOrDefault(CurrentField) is { Length: > 0 } current
            ? (current, ReadOrder(hash, current))
            : (null, null);
    }

    /// <summary>The deployment id of the entry assembly: its informational version, or its version.</summary>
    internal static string GetDefaultDeploymentId()
    {
        Assembly assembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("The entry assembly isn't known, so its version can't be the deployment id. Pass one to UseDeploymentFencing.");

        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();

        return string.IsNullOrWhiteSpace(version)
            ? throw new InvalidOperationException($"The entry assembly {assembly.GetName().Name} has no version. Pass a deployment id to UseDeploymentFencing.")
            : NormalizeDeploymentId(version, nameof(version));
    }

    internal static string NormalizeDeploymentId(string deploymentId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId, parameterName);

        string trimmed = deploymentId.Trim();
        return trimmed.Length > MaxDeploymentIdLength || trimmed.Contains('\n', StringComparison.Ordinal)
            ? throw new ArgumentException($"A deployment id has at most {MaxDeploymentIdLength} characters, on one line.", parameterName)
            : trimmed;
    }

    // In lower case, so ids that differ only by case share their fields whatever the storage does with case
    internal static string SeenField(string deploymentId) => ConcurrencyControl.ShortenResource(SeenPrefix + deploymentId.ToLowerInvariant(), MaxFieldLength);

    internal static string OrderField(string deploymentId) => ConcurrencyControl.ShortenResource(OrderPrefix + deploymentId.ToLowerInvariant(), MaxFieldLength);

    /// <summary>Whether two deployment ids name the same deployment: they are compared without regard to case.</summary>
    internal static bool IsSame(string? deploymentId, string? other) => string.Equals(deploymentId, other, StringComparison.OrdinalIgnoreCase);

    /// <summary>The order a deployment was last seen with, or <c>null</c> when it has none.</summary>
    internal static long? ReadOrder(Dictionary<string, string> hash, string deploymentId)
    {
        return hash.TryGetValue(OrderField(deploymentId), out string? value) &&
               long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long order)
            ? order
            : null;
    }

    /// <summary>
    /// The value a check writes to its deployment's <c>Seen</c> field. Other servers only look at whether it
    /// changed, so it must differ from the one before: the number counts the checks of the deployment. The date
    /// and the interval are hints, for removing deployments long gone and for servers with another interval.
    /// </summary>
    internal static string FormatSeen(DateTime now, long sequence, TimeSpan checkInterval)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{JobHelper.SerializeDateTime(now)}{SeenSeparator}{sequence}{SeenSeparator}{(long)checkInterval.TotalMilliseconds}");
    }

    /// <summary>Reads what it can of a <c>Seen</c> value; a part that is missing or damaged is <c>null</c> or zero.</summary>
    internal static (DateTime? LastSeen, long Sequence, TimeSpan? CheckInterval) ReadSeen(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return (null, 0, null);
        }

        string[] parts = value.Split(SeenSeparator);

        DateTime? lastSeen = null;
        try
        {
            lastSeen = JobHelper.DeserializeDateTime(parts[0]);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            // Not a date: the value still shows whether the deployment is alive, by changing
        }

        long sequence = parts.Length > 1 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed < long.MaxValue
            ? parsed
            : 0;

        TimeSpan? checkInterval = parts.Length > 2 &&
            long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long milliseconds) &&
            milliseconds > 0 && milliseconds <= LongestPublishedCheckInterval.TotalMilliseconds
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;

        return (lastSeen, sequence, checkInterval);
    }

    /// <summary>A deployment with its order, when it has one: <c>2026.11.3 (order 42)</c>.</summary>
    internal static string Describe(string deploymentId, long? order)
    {
        return order.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{deploymentId} (order {order.Value})")
            : deploymentId;
    }
}

/// <summary>The server process of <see cref="DeploymentFencing"/>.</summary>
internal sealed class DeploymentFencingProcess(string deploymentId, long? deploymentOrder) : IBackgroundProcess
{
    private static readonly TimeSpan RetryForgettingAfter = TimeSpan.FromHours(1);

    // How soon a check that couldn't take the lock is tried again, at most: it has waited LockTimeout for it already
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromSeconds(1);

    // Checks further apart than this many check intervals leave a gap in which this server saw nothing
    private const double LongestGapInCheckIntervals = 1.5;

    // A fenced server says that it can't take over once this many of its checks in a row came after such a gap,
    // and again after this long while it lasts
    private const int GapsInARowBeforeWarning = 3;
    private static readonly TimeSpan RepeatGapWarningAfter = TimeSpan.FromHours(1);

    private static readonly long Started = Stopwatch.GetTimestamp();
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(DeploymentFencingProcess));

    // What this server has watched of each deployment's Seen field: the last value it read, and when, by its own
    // monotonic clock, it saw that value for the first time. The clocks of the other servers play no part
    private readonly Dictionary<string, Observation> _observations = new(StringComparer.Ordinal);

    // When, by its monotonic clock, this server last completed a check. What it watched only counts while its
    // checks follow one another: after a longer gap it can't tell what the other servers did meanwhile
    private TimeSpan? _lastCheck;

    // How many checks in a row, while fenced, came after a gap, and when, by the monotonic clock, the warning about
    // them was last logged. A server whose every check comes after a gap watches afresh each time, so it never sees
    // the current deployment silent for long enough to take over
    private int _gapsInARow;
    private TimeSpan? _warnedOfGaps;

    // The newer deployment this one was last reported fenced by, so the warning is logged once, not at every check
    private string? _fencedByNewer;

    // The server id the warning that the server takes no jobs because its first check failed was logged for
    private string? _warnedOfFirstCheck;

    // Held by each check, see Check
    private readonly NightShiftLock _checkLock = new();

    // Whether the warning that the lock of the checks can't be taken was logged since a check last took it
    private bool _warnedOfLock;
    private bool _warnedOfPauseLock;

    // What the warnings about the configuration were last logged for, so each is logged once
    private (long? Given, long? Stored)? _warnedOrders;
    private (TimeSpan TakeOverAfter, TimeSpan CheckInterval)? _warnedTimes;

    // After a failure, the fields of deployments long gone are left alone for a while
    private TimeSpan _forgetNotBefore;

    /// <summary>The process of a deployment with the order given to <c>UseDeploymentFencing</c>.</summary>
    public DeploymentFencingProcess(string deploymentId)
        : this(deploymentId, DeploymentFencing.DeploymentOrder)
    {
    }

    public string DeploymentId { get; } = deploymentId;

    /// <summary>The order this server was started with. The one that applies is the highest given to the id.</summary>
    public long? DeploymentOrder { get; } = deploymentOrder;

    /// <summary>
    /// Told what each check that went all the way decided, and for which server id. The workers of the server ask
    /// it before they fetch, see <see cref="GatedWorker"/>.
    /// </summary>
    public WorkerGate Gate { get; } = new(deploymentId);

    public void Execute(BackgroundProcessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            using IStorageConnection connection = context.Storage.GetConnection();
            Check(connection, context.ServerId, JobClock.UtcNow, Stopwatch.GetElapsedTime(Started), context.StoppingToken);
        }
        catch (DistributedLockTimeoutException ex) when (!context.IsStopping)
        {
            WaitForTheLock(context, ex);
            return;
        }
        catch (PauseLockTimeoutException ex) when (!context.IsStopping)
        {
            WaitForThePauseLock(context, ex);
            return;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            WarnOnceThatTheServerTakesNoJobs(context);
            throw;
        }

        context.Wait(DeploymentFencing.CheckInterval);
    }

    // Another server held the lock of the checks all the time this one waited for it: it froze inside it, or its
    // storage is slow. No deployment can take over without that lock, so nothing this server decided last can have
    // changed meanwhile: its workers go on as they were (WorkerGate.Confirm), and it checks again soon, not after
    // the growing delay of a process that failed. A holder that is gone for good loses the lock: SQL Server
    // releases it with the holder's session, the other storages when its lease runs out. A real storage error
    // isn't this exception, and closes the gate once a decision is too old
    private void WaitForTheLock(BackgroundProcessContext context, DistributedLockTimeoutException exception)
    {
        Gate.Confirm(context.ServerId);
        WarnOnceThatTheServerTakesNoJobs(context);

        TimeSpan retryIn = DeploymentFencing.CheckInterval < LockRetryDelay ? DeploymentFencing.CheckInterval : LockRetryDelay;
        if (!_warnedOfLock)
        {
            _warnedOfLock = true;
            Logger.WarnException(
                $"The deployment check of server '{context.ServerId}' couldn't take the lock '{exception.Resource}' within {DeploymentFencing.LockTimeout}: another server holds it. No deployment can take over while nobody can take that lock, so the server goes on as its last check decided, and checks again every {retryIn} until it gets the lock.",
                exception);
        }

        context.Wait(retryIn);
    }

    // The check went all the way under the lock of the checks, which confirmed the gate's last decision, and a
    // fenced server closed its gate before it wrote anything; only the pause or resume the check decided waits for
    // the drain-mode switch, whose writes are short. A server to be resumed stays paused until then, so the check
    // is tried again soon, not after the growing delay of a process that failed
    private void WaitForThePauseLock(BackgroundProcessContext context, PauseLockTimeoutException exception)
    {
        TimeSpan retryIn = DeploymentFencing.CheckInterval < LockRetryDelay ? DeploymentFencing.CheckInterval : LockRetryDelay;
        if (!_warnedOfPauseLock)
        {
            _warnedOfPauseLock = true;
            Logger.WarnException($"{exception.Message} It is tried again every {retryIn} until the lock is free.", exception);
        }

        context.Wait(retryIn);
    }

    /// <summary>
    /// A check decided, but couldn't take the drain-mode switch's lock to pause or resume the server. Not the lock
    /// of the checks: a timeout on that one means no deployment can take over, which this doesn't.
    /// </summary>
    internal sealed class PauseLockTimeoutException(string serverId, bool fenced, DistributedLockTimeoutException inner)
        : InvalidOperationException($"The deployment check of server '{serverId}' couldn't take the lock '{inner.Resource}' to {(fenced ? "pause" : "resume")} the server.", inner);

    // The error itself is logged by the loop that runs this process and tries it again. This says what it means
    // while no check has succeeded yet for the id the server has now: the workers wait, so nothing runs on a
    // server that may be fenced
    private void WarnOnceThatTheServerTakesNoJobs(BackgroundProcessContext context)
    {
        if (Gate.IsDecided(context.ServerId) || _warnedOfFirstCheck == context.ServerId || context.IsStopping)
        {
            return;
        }

        _warnedOfFirstCheck = context.ServerId;
        Logger.Warn($"The first deployment check of server '{context.ServerId}' failed, so it isn't known whether deployment '{DeploymentId}' is fenced: the server takes no jobs until a check succeeds. The check is tried again.");
    }

    /// <summary>Records this deployment, claims the cluster when it may, and pauses or resumes the server. Returns whether it is fenced.</summary>
    /// <param name="connection">The connection to read and write the hash with.</param>
    /// <param name="serverId">The server to pause or resume.</param>
    /// <param name="now">The time of this server's clock, written as a hint only: nothing is decided by it but when a deployment long gone is forgotten.</param>
    /// <param name="uptime">A reading of this server's monotonic clock, which says how long a deployment has been silent.</param>
    /// <param name="givenUp">
    /// Canceled once the server has given up <paramref name="serverId"/>: when it stops, or when its heartbeat
    /// restarts it with a new id. A check of an id given up neither pauses that id nor tells the workers anything.
    /// </param>
    internal bool Check(IStorageConnection connection, string serverId, DateTime now, TimeSpan uptime, CancellationToken givenUp = default)
    {
        // One check at a time. A server restarted by its heartbeat runs this process under its new id while the
        // thread of the old id may still be inside a check: what the process watched isn't safe to share
        lock (_checkLock)
        {
            return CheckOnce(connection, serverId, now, uptime, givenUp);
        }
    }

    private bool CheckOnce(IStorageConnection connection, string serverId, DateTime now, TimeSpan uptime, CancellationToken givenUp)
    {
        WarnOnceAboutTheTakeOverTime();

        string? current;
        long? currentOrder;
        long? order;
        bool fenced;
        bool older = false;
        TimeSpan? gap;

        using (connection.AcquireDistributedLock($"lock:{DeploymentFencing.HashKey}", DeploymentFencing.LockTimeout))
        {
            // No deployment can take over while this check holds the lock, and before it, whoever held it couldn't
            // either: what the last check decided still stands, for as long as this check takes
            _warnedOfLock = false;
            if (!givenUp.IsCancellationRequested)
            {
                Gate.Confirm(serverId);
            }

            Dictionary<string, string> hash = connection.GetAllEntriesFromHash(DeploymentFencing.HashKey) ?? [];
            current = hash.GetValueOrDefault(DeploymentFencing.CurrentField) is { Length: > 0 } stored ? stored : null;
            gap = WatchAfreshAfterAGap(uptime);
            Observe(hash, current, uptime);

            // One id has one order: the highest any of its servers was started with
            long? storedOrder = DeploymentFencing.ReadOrder(hash, DeploymentId);
            order = Highest(DeploymentOrder, storedOrder);
            WarnOnceAboutAnotherOrder(storedOrder, order);

            currentOrder = current != null ? DeploymentFencing.ReadOrder(hash, current) : null;
            string seenField = DeploymentFencing.SeenField(DeploymentId);
            bool seen = hash.ContainsKey(seenField);
            string? another = current != null && !DeploymentFencing.IsSame(current, DeploymentId) ? current : null;
            bool currentAlive = another != null && IsAlive(DeploymentFencing.SeenField(another), uptime);

            // The orders decide when both deployments have one and they differ: the ids can't be compared
            bool newer = false;
            if (another != null && order is { } own && currentOrder is { } other && own != other)
            {
                newer = own > other;
                older = !newer;
            }

            // A deployment with a higher order, or without orders one never seen, is newer and claims at once; one
            // with a lower order, or seen before, only once the current is silent
            fenced = currentAlive && (older || (!newer && seen));

            // The workers stop fetching as soon as the server is known to be fenced: a write below that fails,
            // its pause included, must not leave them taking jobs
            if (fenced && !givenUp.IsCancellationRequested)
            {
                Gate.Decide(serverId, fenced: true);
            }

            // Each check writes a value the deployment's field didn't hold: its sign of life
            Dictionary<string, string> changes = new(StringComparer.Ordinal)
            {
                [seenField] = DeploymentFencing.FormatSeen(
                    now,
                    DeploymentFencing.ReadSeen(hash.GetValueOrDefault(seenField)).Sequence + 1,
                    DeploymentFencing.CheckInterval),
            };

            // Only ever raised: a server started with a lower order, or without one, doesn't take it from the others
            if (order.HasValue && order != storedOrder)
            {
                changes[DeploymentFencing.OrderField(DeploymentId)] = order.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (!fenced && !DeploymentFencing.IsSame(current, DeploymentId))
            {
                // What the log says only: whether the servers of the current deployment were seen to be running
                bool running = another != null && currentAlive && IsLikelyRunning(DeploymentFencing.SeenField(another), now);

                if (running && newer)
                {
                    Logger.Info(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Deployment '{DeploymentId}' (order {order}) takes over from '{current}' (order {currentOrder}), whose servers are still running: they stop taking jobs."));
                }
                else if (running)
                {
                    // Without orders that decide, only a deployment never seen gets here while the current one is alive
                    Logger.Warn($"Deployment '{DeploymentId}', never seen before, takes over from '{current}', whose servers are still running: they stop taking jobs. If '{DeploymentId}' isn't the newer build, stop its servers; '{current}' takes over again once they have been gone for {DeploymentFencing.EffectiveTakeOverAfter}.");
                }

                Logger.Info($"Deployment '{DeploymentId}' is now the current one{(current != null ? $", after '{current}'" : string.Empty)}: servers of other deployments stop taking jobs.");
                changes[DeploymentFencing.CurrentField] = DeploymentId;
                current = DeploymentId;
                currentOrder = order;
            }

            Write(connection, hash, changes, FindForgotten(hash, current, now, uptime), uptime);
        }

        // Only a check that went all the way counts: one that failed leaves the gap open
        _lastCheck = uptime;

        // The server gave this id up meanwhile: its pause would only stay behind, and its decision would close the
        // gate of the id the server has now
        if (givenUp.IsCancellationRequested)
        {
            return fenced;
        }

        bool fencedByNewer = fenced && older;
        WarnOnceWhenFencedByNewer(fencedByNewer, order, current, currentOrder);
        try
        {
            UpdatePause(connection, serverId, fenced, order, current, fencedByNewer ? currentOrder : null);
        }
        catch (DistributedLockTimeoutException ex)
        {
            // The drain-mode switch's lock, not the lock of the checks: Execute takes a timeout on that one to mean
            // that no deployment can take over, and tries this again soon
            throw new PauseLockTimeoutException(serverId, fenced, ex);
        }

        _warnedOfPauseLock = false;
        WarnWhenGapsKeepItFenced(fenced, gap, current, uptime);

        // A fenced server is paused by now. One that isn't may work, once this check went all the way, and for as
        // long as another deployment would wait for it to fall silent
        Gate.Decide(serverId, fenced);
        return fenced;
    }

    // A server that couldn't check for a while (the storage was down, the process was suspended) saw nothing of
    // that time, and the servers of the other deployments may not have been able to check either: a value that is
    // still the one read before the gap doesn't say they are gone. Counting the gap as silence would let a fenced
    // deployment take over from a live one right after an outage, so what was watched before is dropped and the
    // server watches as one that just started. The same goes for what a check that failed has read, when no check
    // was ever completed. Returns the gap since the last completed check when it was too long, or null
    private TimeSpan? WatchAfreshAfterAGap(TimeSpan uptime)
    {
        TimeSpan longestGap = LongestGap;
        TimeSpan? gap = _lastCheck is { } lastCheck ? uptime - lastCheck : null;
        if (gap <= longestGap)
        {
            return null;
        }

        if (_observations.Count == 0)
        {
            return gap;
        }

        _observations.Clear();

        string completedNoCheck = gap is { } measured ? $"for {measured}" : "yet";
        Logger.Info($"This server of deployment '{DeploymentId}' completed no check {completedNoCheck}, with at most {longestGap} between two checks to watch without a gap, so it can't tell which deployments ran meanwhile: it watches them afresh, and takes over from a silent one only after {DeploymentFencing.EffectiveTakeOverAfter} more.");
        return gap;
    }

    // The longest time between two checks that still counts as watching without a gap
    private static TimeSpan LongestGap => DeploymentFencing.CheckInterval * LongestGapInCheckIntervals;

    // One gap is an outage or a pause, and the server takes over later. A gap before every check (each check takes
    // more than half a check interval because the storage is slow, or the process gets too little processor time)
    // means that it never will, however long the current deployment is gone, and nothing else would say so. Only
    // told while fenced: a server of the current deployment has nothing to take over. A check in time, or one
    // that finds the server no longer fenced, starts the count again; the warning itself comes at most once in
    // RepeatGapWarningAfter, also when the gaps stop and come back, so checks that are only sometimes in time
    // don't repeat it every few checks
    private void WarnWhenGapsKeepItFenced(bool fenced, TimeSpan? gap, string? current, TimeSpan uptime)
    {
        if (!fenced || gap is not { } measured)
        {
            _gapsInARow = 0;
            return;
        }

        _gapsInARow++;
        if (_gapsInARow < GapsInARowBeforeWarning ||
            (_warnedOfGaps is { } warned && uptime - warned < RepeatGapWarningAfter))
        {
            return;
        }

        _warnedOfGaps = uptime;
        Logger.Warn(string.Create(
            CultureInfo.InvariantCulture,
            $"The last {_gapsInARow} checks of this server of deployment '{DeploymentId}' each came too long after the one before: the last one after {measured}, with a check interval of {DeploymentFencing.CheckInterval} and at most {LongestGap} between two checks to watch without a gap. It therefore can't judge whether the current deployment '{current}' is silent, and will not take over from it while this lasts. Raise DeploymentFencing.CheckInterval, and TakeOverAfter with it, or fix the latency of the storage, so that a check takes less than half a check interval."));
    }

    private static long? Highest(long? first, long? second)
    {
        if (first.HasValue && second.HasValue)
        {
            return Math.Max(first.Value, second.Value);
        }

        return first ?? second;
    }

    // Remembers each deployment's Seen value and when it was first read. A value read for the first time, also
    // the very first one after this server started, is a sign of life at that moment: not knowing better, the
    // deployment is alive until it has been watched without one for long enough
    private void Observe(Dictionary<string, string> hash, string? current, TimeSpan uptime)
    {
        HashSet<string> fields = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> entry in hash)
        {
            if (entry.Key.StartsWith(DeploymentFencing.SeenPrefix, StringComparison.Ordinal))
            {
                fields.Add(entry.Key);
                ObserveValue(entry.Key, entry.Value ?? string.Empty, uptime);
            }
        }

        // A current deployment without the field is watched too: it is silent once the field has stayed away
        if (current != null && fields.Add(DeploymentFencing.SeenField(current)))
        {
            ObserveValue(DeploymentFencing.SeenField(current), string.Empty, uptime);
        }

        foreach (string gone in _observations.Keys.Where(x => !fields.Contains(x)).ToArray())
        {
            _observations.Remove(gone);
        }
    }

    private void ObserveValue(string field, string value, TimeSpan uptime)
    {
        if (!_observations.TryGetValue(field, out Observation observation))
        {
            _observations[field] = new Observation(value, uptime, SawChange: false);
        }
        else if (observation.Value != value)
        {
            _observations[field] = new Observation(value, uptime, SawChange: true);
        }
    }

    // Alive while its value changed recently, by this server's own measure of time
    private bool IsAlive(string seenField, TimeSpan uptime)
    {
        return _observations.TryGetValue(seenField, out Observation observation) &&
               uptime - observation.Since <= GetSilenceLimit(observation.Value);
    }

    // For the wording of a log line only. A server that has watched the value change knows the deployment runs;
    // one that just started can only compare the date the value holds with its own clock, as a guess
    private bool IsLikelyRunning(string seenField, DateTime now)
    {
        if (!_observations.TryGetValue(seenField, out Observation observation))
        {
            return false;
        }

        if (observation.SawChange)
        {
            return true;
        }

        return DeploymentFencing.ReadSeen(observation.Value).LastSeen is { } lastSeen &&
               (now - lastSeen).Duration() <= GetSilenceLimit(observation.Value);
    }

    // How long a deployment may show no sign of life before it is silent: at least two of its own checks, by the
    // interval it published, so a deployment that checks less often than this one expects isn't taken for dead
    private static TimeSpan GetSilenceLimit(string seenValue)
    {
        TimeSpan limit = DeploymentFencing.EffectiveTakeOverAfter;

        if (DeploymentFencing.ReadSeen(seenValue).CheckInterval is { } interval && interval + interval > limit)
        {
            limit = interval + interval;
        }

        return limit;
    }

    // The fields of the deployments that are gone for good: not the current one or this one, without a sign of
    // life for as long as this server has watched, and last seen long ago by the date their servers wrote. The date
    // alone decides nothing: a deployment whose servers are alive is never forgotten, whatever the clocks say
    private List<string> FindForgotten(Dictionary<string, string> hash, string? current, DateTime now, TimeSpan uptime)
    {
        List<string> forgotten = [];
        if (uptime < _forgetNotBefore)
        {
            return forgotten;
        }

        foreach (KeyValuePair<string, Observation> entry in _observations)
        {
            string deployment = entry.Key[DeploymentFencing.SeenPrefix.Length..];
            if (DeploymentFencing.IsSame(deployment, current) || DeploymentFencing.IsSame(deployment, DeploymentId) ||
                !hash.ContainsKey(entry.Key) || IsAlive(entry.Key, uptime))
            {
                continue;
            }

            if (DeploymentFencing.ReadSeen(entry.Value.Value).LastSeen is { } lastSeen && now - lastSeen > DeploymentFencing.ForgetAfter)
            {
                forgotten.Add(entry.Key);

                string orderField = DeploymentFencing.OrderField(deployment);
                if (hash.ContainsKey(orderField))
                {
                    forgotten.Add(orderField);
                }
            }
        }

        return forgotten;
    }

    private void Write(
        IStorageConnection connection,
        Dictionary<string, string> hash,
        Dictionary<string, string> changes,
        List<string> forgotten,
        TimeSpan uptime)
    {
        if (forgotten.Count == 0)
        {
            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            transaction.SetRangeInHash(DeploymentFencing.HashKey, changes);
            transaction.Commit();
            return;
        }

        // A field can't be removed alone, so the hash is written again without the forgotten ones, in one
        // transaction and under the lock every check holds
        Dictionary<string, string> kept = new(hash, StringComparer.Ordinal);
        foreach (string field in forgotten)
        {
            kept.Remove(field);
        }

        foreach (KeyValuePair<string, string> change in changes)
        {
            kept[change.Key] = change.Value;
        }

        try
        {
            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            transaction.RemoveHash(DeploymentFencing.HashKey);
            transaction.SetRangeInHash(DeploymentFencing.HashKey, kept);
            transaction.Commit();
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Forgetting is housekeeping: the check itself goes on. Every field that must stay is written, so a
            // transaction that removed the hash and then failed leaves nothing missing
            _forgetNotBefore = uptime + RetryForgettingAfter;
            Logger.WarnException($"The deployments long gone couldn't be removed from '{DeploymentFencing.HashKey}'. It is tried again in {RetryForgettingAfter}.", ex);

            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            transaction.SetRangeInHash(DeploymentFencing.HashKey, kept);
            transaction.Commit();
            return;
        }

        string[] deployments = [.. forgotten
            .Where(static x => x.StartsWith(DeploymentFencing.SeenPrefix, StringComparison.Ordinal))
            .Select(static x => x[DeploymentFencing.SeenPrefix.Length..])];

        foreach (string field in forgotten)
        {
            _observations.Remove(field);
        }

        Logger.Info($"Forgot {deployments.Length} deployment(s) not seen for {DeploymentFencing.ForgetAfter.TotalDays:0} days: {string.Join(", ", deployments.Take(10))}{(deployments.Length > 10 ? ", ..." : string.Empty)}. One that comes back counts as never seen.");
    }

    private void WarnOnceAboutTheTakeOverTime()
    {
        TimeSpan takeOverAfter = DeploymentFencing.TakeOverAfter;
        TimeSpan checkInterval = DeploymentFencing.CheckInterval;

        if (takeOverAfter >= checkInterval + checkInterval || _warnedTimes == (takeOverAfter, checkInterval))
        {
            return;
        }

        _warnedTimes = (takeOverAfter, checkInterval);
        Logger.Warn($"DeploymentFencing.TakeOverAfter ({takeOverAfter}) is less than twice DeploymentFencing.CheckInterval ({checkInterval}), so a deployment that misses a single check would look silent: {checkInterval + checkInterval} is used instead. Set TakeOverAfter to at least twice CheckInterval.");
    }

    private void WarnOnceAboutAnotherOrder(long? storedOrder, long? order)
    {
        // Nothing stored yet: this server is the first of its deployment to bring an order
        if (!storedOrder.HasValue || storedOrder == DeploymentOrder || _warnedOrders == (DeploymentOrder, storedOrder))
        {
            return;
        }

        _warnedOrders = (DeploymentOrder, storedOrder);
        string given = DeploymentOrder.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"with order {DeploymentOrder.Value}")
            : "without an order";

        Logger.Warn(string.Create(
            CultureInfo.InvariantCulture,
            $"This server of deployment '{DeploymentId}' was started {given}, but other servers of '{DeploymentId}' were started with order {storedOrder.Value}. A deployment id has one order, so the highest, {order}, applies to all of them. Start every server of a deployment with the same order, and give a new deployment a new id."));
    }

    private void WarnOnceWhenFencedByNewer(bool fencedByNewer, long? order, string? current, long? currentOrder)
    {
        if (!fencedByNewer)
        {
            _fencedByNewer = null;
            return;
        }

        if (_fencedByNewer == current)
        {
            return;
        }

        _fencedByNewer = current;
        Logger.Warn(string.Create(
            CultureInfo.InvariantCulture,
            $"Deployment '{DeploymentId}' (order {order}) is fenced, because the newer deployment '{current}' (order {currentOrder}) is the current one: its servers take no jobs while servers of '{current}' are running, and it takes over only once they have been gone for {DeploymentFencing.EffectiveTakeOverAfter}. To make '{DeploymentId}' the current deployment, deploy it with an order above {currentOrder}."));
    }

    private void UpdatePause(IStorageConnection connection, string serverId, bool fenced, long? order, string? current, long? newerOrder)
    {
        DrainMode.InvalidateCache();
        DrainModeStatus status = DrainMode.Read(connection);
        bool paused = status.PausedServers.TryGetValue(serverId, out string? reason);
        bool pausedByFencing = paused && reason?.StartsWith(DeploymentFencing.FencedReasonPrefix, StringComparison.Ordinal) == true;

        if (fenced && !paused)
        {
            if (newerOrder.HasValue)
            {
                // The warning about the newer deployment said why; this is the server's part of it
                Logger.Info($"Server '{serverId}' of deployment '{DeploymentId}' stops taking jobs: the newer deployment '{current}' is the current one.");
                DrainMode.PauseServer(connection, serverId, string.Create(CultureInfo.InvariantCulture, $"{DeploymentFencing.FencedReasonPrefix}deployment '{DeploymentId}' (order {order}) is older than '{current}' (order {newerOrder.Value})"));
                return;
            }

            Logger.Warn($"Server '{serverId}' of deployment '{DeploymentId}' stops taking jobs: deployment '{current}' is the current one.");
            DrainMode.PauseServer(connection, serverId, string.Create(CultureInfo.InvariantCulture, $"{DeploymentFencing.FencedReasonPrefix}deployment '{DeploymentId}' was replaced by '{current}'"));
        }
        else if (!fenced && pausedByFencing)
        {
            // Only a pause of its own: one set by hand stays
            Logger.Info($"Server '{serverId}' of deployment '{DeploymentId}' takes jobs again: its deployment is the current one.");
            DrainMode.ResumeServer(connection, serverId);
        }
    }

    public override string ToString() => $"Deployment fencing ({DeploymentFencing.Describe(DeploymentId, DeploymentOrder)})";

    // Since: this server's monotonic time when it first read the value
    private readonly record struct Observation(string Value, TimeSpan Since, bool SawChange);
}
