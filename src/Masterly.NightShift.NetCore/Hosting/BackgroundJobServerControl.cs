namespace Masterly.NightShift;

/// <summary>
/// Starts and stops the background job servers added with <c>AddNightShiftServer</c>, from the application: a server
/// with <see cref="BackgroundJobServerOptions.StartMode"/> set to <see cref="ServerStartMode.Manual"/> waits for
/// <see cref="StartAsync"/>, and any server can be stopped and started again, for example while a maintenance window
/// is open. Resolve it from the container.
/// </summary>
public interface IBackgroundJobServerControl
{
    /// <summary>Whether at least one server is running.</summary>
    bool IsRunning { get; }

    /// <summary>Starts the servers that aren't running. A stopped server starts with a new server id.</summary>
    /// <param name="cancellationToken">Not used: starting doesn't wait.</param>
    /// <exception cref="InvalidOperationException">The host has stopped or disposed a server: it can't start again.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the running servers and waits until they shut down, or until <paramref name="cancellationToken"/> is
    /// canceled. Their running jobs get the server's stop and shutdown timeouts, as when the host stops.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>The servers of a container, registered as they are created.</summary>
internal sealed class BackgroundJobServerControl : IBackgroundJobServerControl
{
    private readonly List<BackgroundJobServerHostedService> _servers = [];
    private readonly NightShiftLock _lock = new();

    public bool IsRunning => Servers.Any(static x => x.IsRunning);

    private BackgroundJobServerHostedService[] Servers
    {
        get
        {
            lock (_lock)
            {
                return [.. _servers];
            }
        }
    }

    public void Register(BackgroundJobServerHostedService server)
    {
        lock (_lock)
        {
            _servers.Add(server);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (BackgroundJobServerHostedService server in Servers)
        {
            server.StartServer();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.WhenAll(Servers.Select(x => x.StopServerAsync(cancellationToken)));
    }
}
