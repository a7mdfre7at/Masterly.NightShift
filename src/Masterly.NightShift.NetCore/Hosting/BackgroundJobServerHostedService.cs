// This file is part of Hangfire. Copyright © 2019 Hangfire OÜ.
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

using Masterly.NightShift.Client;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Microsoft.Extensions.Hosting;

namespace Masterly.NightShift;

/// <summary>
/// Runs a <see cref="BackgroundJobServer"/> as an <see cref="IHostedService"/>. <c>AddNightShiftServer</c>
/// registers it. With an <see cref="IHostApplicationLifetime"/>, the server starts once the application has
/// started and gets the stop signal when the application begins to stop; without one it starts at once.
/// </summary>
public class BackgroundJobServerHostedService : IHostedLifecycleService, IDisposable
{
    private readonly BackgroundJobServerOptions _options;
    private readonly JobStorage _storage;
    private readonly IEnumerable<IBackgroundProcess> _additionalProcesses;
    private readonly IHostApplicationLifetime? _hostApplicationLifetime;
    private readonly IBackgroundJobFactory? _factory;
    private readonly IBackgroundJobPerformer? _performer;
    private readonly IBackgroundJobStateChanger? _stateChanger;

    // Guards the server and the flags below: the host, the application lifetime's callbacks and
    // IBackgroundJobServerControl may start and stop it from different threads
    private readonly NightShiftLock _startLock = new();
    private IBackgroundProcessingServer? _processingServer;

    // The shutdowns IBackgroundJobServerControl started, which end with the server's disposal. The host's StopAsync
    // and Dispose wait for them, so the container isn't disposed under running jobs
    private readonly List<Task> _shutdowns = [];

    // The control this service joins when the host starts it, so services the host never starts (anything else that
    // resolves IEnumerable<IHostedService> creates more) are never started by the control
    private BackgroundJobServerControl? _control;
    private bool _started;
    private CancellationTokenRegistration _stoppingRegistration;

    // Set when IBackgroundJobServerControl stopped the server, so the ApplicationStarted callback doesn't start it again
    private bool _stoppedByControl;

    // Set when the host stops or disposes the service, so a late ApplicationStarted callback starts nothing
    private bool _hostStopped;

    /// <summary>
    /// Initializes the service, which starts the server at once.
    /// </summary>
    /// <param name="storage">The storage the server processes jobs from.</param>
    /// <param name="options">The server options.</param>
    /// <param name="additionalProcesses">Processes the server runs besides its own.</param>
    public BackgroundJobServerHostedService(
        [NotNull] JobStorage storage,
        [NotNull] BackgroundJobServerOptions options,
        [NotNull] IEnumerable<IBackgroundProcess> additionalProcesses)
        : this(storage, options, additionalProcesses, null, null, null)
    {
    }

    /// <summary>
    /// Initializes the service, which starts the server when the application has started.
    /// </summary>
    /// <param name="storage">The storage the server processes jobs from.</param>
    /// <param name="options">The server options.</param>
    /// <param name="additionalProcesses">Processes the server runs besides its own.</param>
    /// <param name="hostApplicationLifetime">The application's lifetime, or <c>null</c> to start the server at once.</param>
    public BackgroundJobServerHostedService(
        [NotNull] JobStorage storage,
        [NotNull] BackgroundJobServerOptions options,
        [NotNull] IEnumerable<IBackgroundProcess> additionalProcesses,
        [CanBeNull] IHostApplicationLifetime? hostApplicationLifetime)
        : this(storage, options, additionalProcesses, null, null, null, hostApplicationLifetime)
    {
    }

    /// <summary>
    /// Initializes the service with the job factory, performer and state changer the server uses, which
    /// starts the server at once.
    /// </summary>
    /// <param name="storage">The storage the server processes jobs from.</param>
    /// <param name="options">The server options.</param>
    /// <param name="additionalProcesses">Processes the server runs besides its own.</param>
    /// <param name="factory">The job factory, or <c>null</c>.</param>
    /// <param name="performer">The job performer, or <c>null</c>.</param>
    /// <param name="stateChanger">The state changer, or <c>null</c>.</param>
    public BackgroundJobServerHostedService(
        [NotNull] JobStorage storage,
        [NotNull] BackgroundJobServerOptions options,
        [NotNull] IEnumerable<IBackgroundProcess> additionalProcesses,
        [CanBeNull] IBackgroundJobFactory? factory,
        [CanBeNull] IBackgroundJobPerformer? performer,
        [CanBeNull] IBackgroundJobStateChanger? stateChanger)
        : this(storage, options, additionalProcesses, factory, performer, stateChanger, null)
    {
    }

    /// <summary>
    /// Initializes the service with the job factory, performer and state changer the server uses (all three,
    /// or they are ignored), which starts the server when the application has started.
    /// </summary>
    /// <param name="storage">The storage the server processes jobs from.</param>
    /// <param name="options">The server options.</param>
    /// <param name="additionalProcesses">Processes the server runs besides its own.</param>
    /// <param name="factory">The job factory, or <c>null</c>.</param>
    /// <param name="performer">The job performer, or <c>null</c>.</param>
    /// <param name="stateChanger">The state changer, or <c>null</c>.</param>
    /// <param name="hostApplicationLifetime">The application's lifetime, or <c>null</c> to start the server at once.</param>
    public BackgroundJobServerHostedService(
        [NotNull] JobStorage storage,
        [NotNull] BackgroundJobServerOptions options,
        [NotNull] IEnumerable<IBackgroundProcess> additionalProcesses,
        [CanBeNull] IBackgroundJobFactory? factory,
        [CanBeNull] IBackgroundJobPerformer? performer,
        [CanBeNull] IBackgroundJobStateChanger? stateChanger,
        [CanBeNull] IHostApplicationLifetime? hostApplicationLifetime)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(additionalProcesses);

        _options = options;
        _storage = storage;
        _additionalProcesses = additionalProcesses;

        _factory = factory;
        _performer = performer;
        _stateChanger = stateChanger;

        _hostApplicationLifetime = hostApplicationLifetime;
    }

    /// <summary>
    /// Starts the server, or arranges for it to start when the application has started.
    /// </summary>
    /// <param name="cancellationToken">Not used.</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        JoinControl();

        lock (_startLock)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            _started = true;
            _stoppingRegistration = _hostApplicationLifetime?.ApplicationStopping.Register(SendStopSignal) ?? default;
        }

        // A manual server waits for IBackgroundJobServerControl.StartAsync
        if (_options.StartMode == ServerStartMode.Manual)
        {
            return Task.CompletedTask;
        }

        if (_hostApplicationLifetime != null)
        {
            // https://github.com/HangfireIO/Hangfire/issues/2117
            _hostApplicationLifetime.ApplicationStarted.Register(StartForHost);
        }
        else
        {
            StartForHost();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends the stop signal to the server and waits for it to shut down.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        IBackgroundProcessingServer? server;
        Task[] shutdowns;
        lock (_startLock)
        {
            _hostStopped = true;
            server = _processingServer;
            shutdowns = [.. _shutdowns];
        }

        if (server != null)
        {
            await StopRunningServerAsync(server, cancellationToken);
        }

        // Servers IBackgroundJobServerControl is stopping: their jobs may still run
        if (shutdowns.Length > 0)
        {
            await Task.WhenAll(shutdowns).WaitAsync(cancellationToken);
        }
    }

    private static async Task StopRunningServerAsync(IBackgroundProcessingServer server, CancellationToken cancellationToken)
    {
        try
        {
            server.SendStop();
            await server.WaitForShutdownAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            // Due to a bug in ASP.NET Core's Testing package, the StopAsync method
            // can be called twice and from different threads, please see
            // https://github.com/dotnet/aspnetcore/issues/40271 for details.
            // This is not a case for regular applications but can fail integration tests
            // when the second call to the StopAsync method attempts to be performed on
            // an already disposed object.
            // This is not a big deal, however, it's still a workaround that should be
            // removed one day.
            // TODO: Remove this workaround and don't rely on this behavior for simplicity.
        }
    }

    /// <summary>
    /// Makes the server known to <see cref="IBackgroundJobServerControl"/> before any hosted service starts.
    /// </summary>
    /// <param name="cancellationToken">Not used.</param>
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        JoinControl();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Sends the stop signal before any hosted service stops, so workers take no new jobs while the others shut down.
    /// </summary>
    /// <param name="cancellationToken">Not used.</param>
    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        SendStopSignal();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Disposes the server, and waits for the shutdowns <see cref="IBackgroundJobServerControl"/> started.
    /// </summary>
    public void Dispose()
    {
        IBackgroundProcessingServer? server;
        Task[] shutdowns;
        lock (_startLock)
        {
            _hostStopped = true;
            server = _processingServer;
            Volatile.Write(ref _processingServer, null);
            shutdowns = [.. _shutdowns];
            _shutdowns.Clear();
        }

        _stoppingRegistration.Dispose();
        server?.Dispose();
        WaitForShutdowns(shutdowns);
        GC.SuppressFinalize(this);
    }

    // Each shutdown is bounded by the server's shutdown timeout
    private static void WaitForShutdowns(Task[] shutdowns)
    {
        if (shutdowns.Length == 0)
        {
            return;
        }

        try
        {
            Task.WaitAll(shutdowns);
        }
        catch (AggregateException ex)
        {
            // The control's StopAsync, if its caller still waited, has reported it already
            LogProvider.GetLogger(typeof(BackgroundJobServerHostedService)).WarnException(
                "A NightShift server stopped by IBackgroundJobServerControl failed to shut down.", ex);
        }
    }

    /// <summary>Whether the server runs.</summary>
    internal bool IsRunning => Volatile.Read(ref _processingServer) != null;

    /// <summary>Starts the server when it doesn't run, for <see cref="IBackgroundJobServerControl"/>.</summary>
    /// <exception cref="InvalidOperationException">The host stopped or disposed this service: nothing would stop the server.</exception>
    internal void StartServer()
    {
        lock (_startLock)
        {
            if (_hostStopped)
            {
                throw new InvalidOperationException("The host has stopped the NightShift server; it can't be started again.");
            }

            _stoppedByControl = false;
            if (_processingServer == null)
            {
                InitializeProcessingServer();
            }
        }
    }

    /// <summary>Stops the server and forgets it, so it can start again, for <see cref="IBackgroundJobServerControl"/>.</summary>
    /// <remarks>
    /// A canceled <paramref name="cancellationToken"/> stops only the waiting: the server goes on shutting down with
    /// its stop and shutdown timeouts, and the host's <see cref="StopAsync"/> and <see cref="Dispose"/> wait for it.
    /// </remarks>
    internal async Task StopServerAsync(CancellationToken cancellationToken)
    {
        Task shutdown;
        lock (_startLock)
        {
            // Also before the application started: the ApplicationStarted callback then leaves it stopped
            _stoppedByControl = true;
            _shutdowns.RemoveAll(static x => x.IsCompleted);
            IBackgroundProcessingServer? server = _processingServer;
            if (server == null)
            {
                // A stop whose waiting was canceled may still be shutting the server down
                shutdown = Task.WhenAll(_shutdowns);
            }
            else
            {
                Volatile.Write(ref _processingServer, null);

                // Task.Run: nothing of the shutdown runs under the lock
                shutdown = Task.Run(() => ShutDownAsync(server), CancellationToken.None);
                _shutdowns.Add(shutdown);
            }
        }

        await shutdown.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Joins the control the host's start makes this service known to.</summary>
    internal void JoinOnStart([NotNull] BackgroundJobServerControl control)
    {
        Volatile.Write(ref _control, control);
    }

    private void JoinControl()
    {
        Volatile.Read(ref _control)?.Register(this);
    }

    private static async Task ShutDownAsync(IBackgroundProcessingServer server)
    {
        try
        {
            server.SendStop();
            await server.WaitForShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            server.Dispose();
        }
    }

    // Starts the server for the host, unless it runs already (IBackgroundJobServerControl started it before the
    // application did), the control stopped it, or the host is stopping
    private void StartForHost()
    {
        lock (_startLock)
        {
            if (_processingServer == null && !_stoppedByControl && !_hostStopped)
            {
                InitializeProcessingServer();
            }
        }
    }

    // Called under _startLock
    private void InitializeProcessingServer()
    {
        IBackgroundProcessingServer server = _factory != null && _performer != null && _stateChanger != null
            ? new BackgroundJobServer(_options, _storage, _additionalProcesses, null, null, _factory, _performer,
                _stateChanger)
            : new BackgroundJobServer(_options, _storage, _additionalProcesses);

        Volatile.Write(ref _processingServer, server);
    }

    private void SendStopSignal()
    {
        try
        {
            Volatile.Read(ref _processingServer)?.SendStop();
        }
        catch (ObjectDisposedException)
        {
            // Please see the comment regarding this exception above.
        }
    }
}
