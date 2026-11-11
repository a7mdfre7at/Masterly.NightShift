// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
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

using System.ComponentModel;
using Masterly.NightShift.Client;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Processes background jobs: runs the workers that perform enqueued jobs, the schedulers of delayed and recurring
/// jobs, and the server's maintenance processes, as set by <see cref="BackgroundJobServerOptions"/>. It starts when
/// created; dispose it to stop it.
/// </summary>
public class BackgroundJobServer : IBackgroundProcessingServer, IAsyncDisposable
{
    private readonly ILog _logger = LogProvider.For<BackgroundJobServer>();
    private int _disposed;

    private readonly BackgroundJobServerOptions _options;
    private readonly BackgroundProcessingServer _processingServer;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundJobServer"/> class
    /// with default options and <see cref="JobStorage.Current"/> storage.
    /// </summary>
    public BackgroundJobServer()
        : this(new BackgroundJobServerOptions())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundJobServer"/> class
    /// with default options and the given storage.
    /// </summary>
    /// <param name="storage">The storage</param>
    public BackgroundJobServer([NotNull] JobStorage storage)
        : this(new BackgroundJobServerOptions(), storage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundJobServer"/> class
    /// with the given options and <see cref="JobStorage.Current"/> storage.
    /// </summary>
    /// <param name="options">Server options</param>
    public BackgroundJobServer([NotNull] BackgroundJobServerOptions options)
        : this(options, JobStorage.Current)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundJobServer"/> class
    /// with the specified options and the given storage.
    /// </summary>
    /// <param name="options">Server options</param>
    /// <param name="storage">The storage</param>
    public BackgroundJobServer([NotNull] BackgroundJobServerOptions options, [NotNull] JobStorage storage)
        : this(options, storage, [])
    {
    }

    public BackgroundJobServer(
        [NotNull] BackgroundJobServerOptions options,
        [NotNull] JobStorage storage,
        [NotNull] IEnumerable<IBackgroundProcess> additionalProcesses)
        : this(options, storage, additionalProcesses, null, null, null, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundJobServer"/> class with the specified options,
    /// storage and additional processes, and the services that create, run and move jobs. A <c>null</c> service
    /// means the default one. <c>AddNightShiftServer</c> uses this constructor to pass the services registered
    /// in the container.
    /// </summary>
    /// <param name="options">Server options.</param>
    /// <param name="storage">The storage.</param>
    /// <param name="additionalProcesses">Background processes to run next to the workers.</param>
    /// <param name="filterProvider">The job filter provider, or <c>null</c> for <see cref="BackgroundJobServerOptions.FilterProvider"/>,
    /// or <see cref="JobFilterProviders.Providers"/> when that isn't set either.</param>
    /// <param name="activator">The job activator, or <c>null</c> for <see cref="BackgroundJobServerOptions.Activator"/>,
    /// or <see cref="JobActivator.Current"/> when that isn't set either.</param>
    /// <param name="factory">The background job factory, or <c>null</c> for the default one.</param>
    /// <param name="performer">The background job performer, or <c>null</c> for the default one.</param>
    /// <param name="stateChanger">The background job state changer, or <c>null</c> for the default one.</param>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public BackgroundJobServer(
        [NotNull] BackgroundJobServerOptions options,
        [NotNull] JobStorage storage,
        [NotNull] IEnumerable<IBackgroundProcess> additionalProcesses,
        [CanBeNull] IJobFilterProvider? filterProvider,
        [CanBeNull] JobActivator? activator,
        [CanBeNull] IBackgroundJobFactory? factory,
        [CanBeNull] IBackgroundJobPerformer? performer,
        [CanBeNull] IBackgroundJobStateChanger? stateChanger)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(additionalProcesses);

        _options = options;

        List<IBackgroundProcessDispatcherBuilder> processes =
        [
            .. GetRequiredProcesses(filterProvider, activator, factory, performer, stateChanger),
            .. additionalProcesses.Select(static x => x.UseBackgroundPool(1)),
        ];

        Dictionary<string, object> properties = new()
        {
            { "Queues", options.Queues },
            { "WorkerCount", options.WorkerCount }
        };

        if (options.LongRunningWorkerCount > 0)
        {
            properties.Add("LongRunningWorkerCount", options.LongRunningWorkerCount);
        }

        if (DeploymentFencing.DeploymentId is { } deployment)
        {
            properties.Add("Deployment", deployment);
        }

        _logger.Info($"Starting Masterly.NightShift Server using job storage: '{storage}'");

        storage.WriteOptionsToLog(_logger);

        _logger.Info("Using the following options for Masterly.NightShift Server:\r\n" +
            $"    Worker count: {options.WorkerCount}\r\n" +
            $"    Listening queues: {string.Join(", ", options.Queues.Select(static x => "'" + x + "'"))}\r\n" +
            $"    Shutdown timeout: {options.ShutdownTimeout}\r\n" +
            $"    Schedule polling interval: {options.SchedulePollingInterval}");

        HashSet<string> wrongQueues = new(StringComparer.Ordinal);
        foreach (string queue in options.Queues)
        {
            if (!EnqueuedState.TryValidateQueueName(queue))
            {
                wrongQueues.Add(queue);
            }
        }

        if (wrongQueues.Count > 0)
        {
            _logger.Warn($"These queues fail to match the naming format: {string.Join(", ", wrongQueues.Select(static x => $"'{x}'"))}. A queue name must consist of lowercase letters, digits, underscore, and dash characters only.");
        }

        _processingServer = new BackgroundProcessingServer(
            storage,
            processes,
            properties,
            GetProcessingServerOptions());
    }

    public void SendStop()
    {
        _logger.Debug("Masterly.NightShift Server is stopping...");
        _processingServer.SendStop();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _processingServer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Stops the server and waits for it to shut down without blocking a thread. Calling it again does nothing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _processingServer.SendStop();
        await _processingServer.WaitForShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        _processingServer.Dispose();
        GC.SuppressFinalize(this);
    }

    public bool WaitForShutdown(TimeSpan timeout)
    {
        return _processingServer.WaitForShutdown(timeout);
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        return _processingServer.WaitForShutdownAsync(cancellationToken);
    }

    private IEnumerable<IBackgroundProcessDispatcherBuilder> GetRequiredProcesses(
        [CanBeNull] IJobFilterProvider? filterProvider,
        [CanBeNull] JobActivator? activator,
        [CanBeNull] IBackgroundJobFactory? factory,
        [CanBeNull] IBackgroundJobPerformer? performer,
        [CanBeNull] IBackgroundJobStateChanger? stateChanger)
    {
        List<IBackgroundProcessDispatcherBuilder> processes = [];
        ITimeZoneResolver timeZoneResolver = _options.TimeZoneResolver ?? GlobalTimeZoneResolver.Current;

        if (factory == null && performer == null && stateChanger == null)
        {
            filterProvider ??= _options.FilterProvider ?? JobFilterProviders.Providers;
            activator ??= _options.Activator ?? JobActivator.Current;

            factory = new BackgroundJobFactory(filterProvider);
            performer = new BackgroundJobPerformer(filterProvider, activator, _options.TaskScheduler);
            stateChanger = new BackgroundJobStateChanger(filterProvider);
        }
        else
        {
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentNullException.ThrowIfNull(performer);
            ArgumentNullException.ThrowIfNull(stateChanger);
        }

        processes.Add(new Worker(_options.Queues, performer, stateChanger).UseBackgroundPool(_options.WorkerCount, _options.WorkerThreadConfigurationAction));

        // A pool of its own for [LongRunning] jobs, so they never take every worker from the short ones
        if (_options.LongRunningWorkerCount > 0)
        {
            processes.Add(new Worker([LongRunningAttribute.Queue], performer, stateChanger).UseBackgroundPool(_options.LongRunningWorkerCount, _options.WorkerThreadConfigurationAction));
        }

        if (DeploymentFencing.DeploymentId is { } deploymentId)
        {
            processes.Add(new DeploymentFencingProcess(deploymentId).UseBackgroundPool(1));
        }

        // Checks the job monitors, one server at a time across the cluster
        if (JobMonitors.Any)
        {
            processes.Add(new JobMonitorProcess().UseBackgroundPool(1));
        }

        if (!_options.IsLightweightServer)
        {
            processes.Add(
                new DelayedJobScheduler(_options.SchedulePollingInterval, stateChanger)
                {
                    TaskScheduler = _options.TaskScheduler,
                    MaxDegreeOfParallelism = _options.MaxDegreeOfParallelismForSchedulers
                }
                .UseBackgroundPool(1));

            processes.Add(
                new RecurringJobScheduler(factory, _options.SchedulePollingInterval, timeZoneResolver)
                {
                    TaskScheduler = _options.TaskScheduler,
                    MaxDegreeOfParallelism = _options.MaxDegreeOfParallelismForSchedulers
                }
                    .UseBackgroundPool(1));
        }

        return processes;
    }

    private BackgroundProcessingServerOptions GetProcessingServerOptions()
    {
        return new BackgroundProcessingServerOptions
        {
            StopTimeout = _options.StopTimeout,
            ShutdownTimeout = _options.ShutdownTimeout,
            HeartbeatInterval = _options.HeartbeatInterval,
            ServerCheckInterval = _options.ServerCheckInterval,
            ServerTimeout = _options.ServerTimeout,
            CancellationCheckInterval = _options.CancellationCheckInterval,
            ServerName = _options.ServerName,
            ExcludeStorageProcesses = _options.IsLightweightServer
        };
    }
}
