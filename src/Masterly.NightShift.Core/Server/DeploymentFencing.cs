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
/// Every server refreshes its deployment's <c>Seen:{id}</c> field at each check. A deployment seen before (an old
/// version restarted, a rollback) takes over only once no server of the current deployment has checked in for
/// <see cref="TakeOverAfter"/>, so a crash-looping old server doesn't fence the new ones. To roll back on purpose,
/// deploy with a new deployment id.
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
    private const int MaxFieldLength = 100;

    /// <summary>The deployment of the servers of this process, or <c>null</c> when fencing is off.</summary>
    public static string? DeploymentId { get; internal set; }

    /// <summary>How often a server checks the deployment. 15 seconds.</summary>
    public static TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long the current deployment must be silent before a deployment seen before takes over. 30 seconds.</summary>
    public static TimeSpan TakeOverAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The deployment the cluster runs, or <c>null</c> when no fenced server has run yet.</summary>
    [CanBeNull]
    public static string? GetCurrentDeployment([NotNull] JobStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        using IStorageConnection connection = storage.GetConnection();
        return connection.GetAllEntriesFromHash(HashKey)?.GetValueOrDefault(CurrentField) is { Length: > 0 } current ? current : null;
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
}

/// <summary>The server process of <see cref="DeploymentFencing"/>.</summary>
internal sealed class DeploymentFencingProcess(string deploymentId) : IBackgroundProcess
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(DeploymentFencingProcess));

    public string DeploymentId { get; } = deploymentId;

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
        bool fenced;

        using (connection.AcquireDistributedLock($"lock:{DeploymentFencing.HashKey}", LockTimeout))
        {
            Dictionary<string, string> hash = connection.GetAllEntriesFromHash(DeploymentFencing.HashKey) ?? [];
            current = hash.GetValueOrDefault(DeploymentFencing.CurrentField) is { Length: > 0 } stored ? stored : null;
            bool seen = hash.ContainsKey(DeploymentFencing.SeenField(DeploymentId));

            // A deployment never seen is newer and claims at once; one seen before only once the current is silent
            fenced = current != null && current != DeploymentId && seen && IsAlive(hash, current, now);

            // Each check refreshes the deployment's last sign of life
            Dictionary<string, string> changes = new(StringComparer.Ordinal)
            {
                [DeploymentFencing.SeenField(DeploymentId)] = JobHelper.SerializeDateTime(now),
            };

            if (!fenced && current != DeploymentId)
            {
                Logger.Info($"Deployment '{DeploymentId}' is now the current one{(current != null ? $", after '{current}'" : string.Empty)}: servers of other deployments stop taking jobs.");
                changes[DeploymentFencing.CurrentField] = DeploymentId;
                current = DeploymentId;
            }

            using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
            transaction.SetRangeInHash(DeploymentFencing.HashKey, changes);
            transaction.Commit();
        }

        UpdatePause(connection, serverId, fenced, current);
        return fenced;
    }

    private static bool IsAlive(Dictionary<string, string> hash, string deployment, DateTime now)
    {
        if (!hash.TryGetValue(DeploymentFencing.SeenField(deployment), out string? value) || string.IsNullOrEmpty(value)) return false;

        try
        {
            return now - JobHelper.DeserializeDateTime(value) <= DeploymentFencing.TakeOverAfter;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return false;
        }
    }

    private void UpdatePause(IStorageConnection connection, string serverId, bool fenced, string? current)
    {
        DrainMode.InvalidateCache();
        DrainModeStatus status = DrainMode.Read(connection);
        bool paused = status.PausedServers.TryGetValue(serverId, out string? reason);
        bool pausedByFencing = paused && reason?.StartsWith(DeploymentFencing.FencedReasonPrefix, StringComparison.Ordinal) == true;

        if (fenced && !paused)
        {
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

    public override string ToString() => $"Deployment fencing ({DeploymentId})";
}
