using System.Globalization;
using System.Reflection;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Keeps servers of an older deployment from taking jobs once a newer one runs, turned on with
/// <c>UseDeploymentFencing</c>. The first server of a deployment the cluster hasn't seen claims the current deployment
/// (the <c>Current</c> field of the hash <see cref="HashKey"/>), and servers of the others pause themselves, as
/// <see cref="DrainMode.PauseServer"/> does, with the reason <c>Fenced: ...</c>: jobs they pick up are held and taken
/// by the current servers, and their running jobs finish.
/// </summary>
/// <remarks>
/// <para>Every server refreshes its deployment's <c>Seen:{id}</c> field at each check. A deployment id the cluster
/// has never seen takes over at once, even while servers of the current deployment are alive: fencing can't tell
/// versions apart, so an unseen id is taken to be the newer one. A server started by mistake with an id never used
/// (an old build with a new id, a developer's machine pointed at the wrong storage) fences the current servers, and
/// a warning is logged when such a takeover pauses live servers. A deployment seen before (an old version
/// restarted, a rollback) takes over only once no server of the current deployment has checked in for
/// <see cref="TakeOverAfter"/>, so a crash-looping old server doesn't fence the new ones.</para>
/// <para>A deployment order (<see cref="DeploymentOrder"/>, the second argument of <c>UseDeploymentFencing</c>)
/// tells versions apart: a number that grows with each deploy, kept in the <c>Order:{id}</c> field. When this
/// deployment and the current one both have an order, the higher one wins: a deployment with a higher order takes
/// over at once, seen before or not, and one with a lower order (a stray instance of an old image, an old build
/// deployed by accident) never takes over while servers of the current deployment are alive, only once they have
/// been silent for <see cref="TakeOverAfter"/>. With equal orders, or when either deployment has none, the rules
/// above apply.</para>
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
    private const string SeenPrefix = "Seen:";
    private const string OrderPrefix = "Order:";
    private const int MaxFieldLength = 100;

    /// <summary>The deployment of the servers of this process, or <c>null</c> when fencing is off.</summary>
    public static string? DeploymentId { get; internal set; }

    /// <summary>
    /// The order of the deployment of this process among the deployments, or <c>null</c> when none was given: a
    /// number that grows with each deploy, such as the pipeline's run number.
    /// </summary>
    public static long? DeploymentOrder { get; internal set; }

    /// <summary>How often a server checks the deployment. 15 seconds.</summary>
    public static TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the current deployment must be silent before a deployment seen before, or one with a lower order,
    /// takes over. 30 seconds.
    /// </summary>
    public static TimeSpan TakeOverAfter { get; set; } = TimeSpan.FromSeconds(30);

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

    internal static string SeenField(string deploymentId) => ConcurrencyControl.ShortenResource(SeenPrefix + deploymentId, MaxFieldLength);

    internal static string OrderField(string deploymentId) => ConcurrencyControl.ShortenResource(OrderPrefix + deploymentId, MaxFieldLength);

    /// <summary>The order a deployment was last seen with, or <c>null</c> when it has none.</summary>
    internal static long? ReadOrder(Dictionary<string, string> hash, string deploymentId)
    {
        return hash.TryGetValue(OrderField(deploymentId), out string? value) &&
               long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long order)
            ? order
            : null;
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
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(DeploymentFencingProcess));

    // The newer deployment this one was last reported fenced by, so the warning is logged once, not at every check
    private string? _fencedByNewer;

    /// <summary>The process of a deployment with the order given to <c>UseDeploymentFencing</c>.</summary>
    public DeploymentFencingProcess(string deploymentId)
        : this(deploymentId, DeploymentFencing.DeploymentOrder)
    {
    }

    public string DeploymentId { get; } = deploymentId;

    public long? DeploymentOrder { get; } = deploymentOrder;

    public void Execute(BackgroundProcessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        using (IStorageConnection connection = context.Storage.GetConnection())
        {
            Check(connection, context.ServerId, JobClock.UtcNow);
        }

        context.Wait(DeploymentFencing.CheckInterval);
    }

    /// <summary>Records this deployment, claims the cluster when it may, and pauses or resumes the server. Returns whether it is fenced.</summary>
    internal bool Check(IStorageConnection connection, string serverId, DateTime now)
    {
        string? current;
        long? currentOrder;
        bool fenced;
        bool older = false;

        using (connection.AcquireDistributedLock($"lock:{DeploymentFencing.HashKey}", LockTimeout))
        {
            Dictionary<string, string> hash = connection.GetAllEntriesFromHash(DeploymentFencing.HashKey) ?? [];
            current = hash.GetValueOrDefault(DeploymentFencing.CurrentField) is { Length: > 0 } stored ? stored : null;
            currentOrder = current != null ? DeploymentFencing.ReadOrder(hash, current) : null;
            bool seen = hash.ContainsKey(DeploymentFencing.SeenField(DeploymentId));
            bool currentAlive = current != null && current != DeploymentId && IsAlive(hash, current, now);

            // The orders decide when both deployments have one and they differ: the ids can't be compared
            bool newer = false;
            if (current != null && current != DeploymentId && DeploymentOrder is { } own && currentOrder is { } other && own != other)
            {
                newer = own > other;
                older = !newer;
            }

            // A deployment with a higher order, or without orders one never seen, is newer and claims at once; one
            // with a lower order, or seen before, only once the current is silent
            fenced = currentAlive && (older || (!newer && seen));

            // Each check refreshes the deployment's last sign of life
            Dictionary<string, string> changes = new(StringComparer.Ordinal)
            {
                [DeploymentFencing.SeenField(DeploymentId)] = JobHelper.SerializeDateTime(now),
            };

            // The order goes with the deployment, so a current deployment claimed by a server without one has none
            string orderField = DeploymentFencing.OrderField(DeploymentId);
            string order = DeploymentOrder?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            if (hash.GetValueOrDefault(orderField, string.Empty) != order)
            {
                changes[orderField] = order;
            }

            if (!fenced && current != DeploymentId)
            {
                if (currentAlive && newer)
                {
                    Logger.Info(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Deployment '{DeploymentId}' (order {DeploymentOrder}) takes over from '{current}' (order {currentOrder}), whose servers are still running: they stop taking jobs."));
                }
                else if (currentAlive)
                {
                    // Without orders that decide, only a deployment never seen gets here while the current one is alive
                    Logger.Warn($"Deployment '{DeploymentId}', never seen before, takes over from '{current}', whose servers are still running: they stop taking jobs. If '{DeploymentId}' isn't the newer build, stop its servers; '{current}' takes over again once they have been gone for {DeploymentFencing.TakeOverAfter}.");
                }

                Logger.Info($"Deployment '{DeploymentId}' is now the current one{(current != null ? $", after '{current}'" : string.Empty)}: servers of other deployments stop taking jobs.");
                changes[DeploymentFencing.CurrentField] = DeploymentId;
                current = DeploymentId;
                currentOrder = DeploymentOrder;
            }

            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            transaction.SetRangeInHash(DeploymentFencing.HashKey, changes);
            transaction.Commit();
        }

        bool fencedByNewer = fenced && older;
        WarnOnceWhenFencedByNewer(fencedByNewer, current, currentOrder);
        UpdatePause(connection, serverId, fenced, current, fencedByNewer ? currentOrder : null);
        return fenced;
    }

    private static bool IsAlive(Dictionary<string, string> hash, string deployment, DateTime now)
    {
        if (!hash.TryGetValue(DeploymentFencing.SeenField(deployment), out string? value) || string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            return now - JobHelper.DeserializeDateTime(value) <= DeploymentFencing.TakeOverAfter;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return false;
        }
    }

    private void WarnOnceWhenFencedByNewer(bool fencedByNewer, string? current, long? currentOrder)
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
            $"Deployment '{DeploymentId}' (order {DeploymentOrder}) is fenced, because the newer deployment '{current}' (order {currentOrder}) is the current one: its servers take no jobs while servers of '{current}' are running, and it takes over only once they have been gone for {DeploymentFencing.TakeOverAfter}. To make '{DeploymentId}' the current deployment, deploy it with an order above {currentOrder}."));
    }

    private void UpdatePause(IStorageConnection connection, string serverId, bool fenced, string? current, long? newerOrder)
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
                DrainMode.PauseServer(connection, serverId, string.Create(CultureInfo.InvariantCulture, $"{DeploymentFencing.FencedReasonPrefix}deployment '{DeploymentId}' (order {DeploymentOrder}) is older than '{current}' (order {newerOrder.Value})"));
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
}
