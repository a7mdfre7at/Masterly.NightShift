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

using Masterly.NightShift.Logging;
using Masterly.NightShift.Processing;

namespace Masterly.NightShift.Server;

/// <summary>
/// Responsible for running the given collection background processes.
/// </summary>
/// 
/// <remarks>
/// Immediately starts the processes in a background thread.
/// Responsible for announcing/removing a server, bound to a storage.
/// Wraps all the processes with an infinite loop and automatic retry.
/// Executes all the processes in a single context.
/// Uses timeout in dispose method, waits for all the components, cancel signals shutdown
/// Contains some required processes and uses storage processes.
/// Generates unique id.
/// Properties are still bad.
/// </remarks>
public sealed class BackgroundProcessingServer : IBackgroundProcessingServer
{
    public static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(15);
    private static int _lastThreadId;

    private readonly ILog _logger = LogProvider.GetLogger(typeof(BackgroundProcessingServer));

    private readonly CancellationTokenSource _stoppingCts = new();
    private readonly CancellationTokenSource _stoppedCts = new();
    private readonly CancellationTokenSource _shutdownCts = new();

    private readonly IBackgroundServerProcess _process;
    private readonly BackgroundProcessingServerOptions _options;
    private readonly IBackgroundDispatcher _dispatcher;

    private int _disposed;
    private bool _awaited;

    public BackgroundProcessingServer([NotNull] IEnumerable<IBackgroundProcess> processes)
        : this(JobStorage.Current, processes)
    {
    }

    public BackgroundProcessingServer(
        [NotNull] IEnumerable<IBackgroundProcess> processes,
        [NotNull] IDictionary<string, object> properties)
        : this(JobStorage.Current, processes, properties)
    {
    }

    public BackgroundProcessingServer(
        [NotNull] JobStorage storage,
        [NotNull] IEnumerable<IBackgroundProcess> processes)
        : this(storage, processes, new Dictionary<string, object>())
    {
    }

    public BackgroundProcessingServer(
        [NotNull] JobStorage storage,
        [NotNull] IEnumerable<IBackgroundProcess> processes,
        [NotNull] IDictionary<string, object> properties)
        : this(storage, processes, properties, new BackgroundProcessingServerOptions())
    {
    }

    public BackgroundProcessingServer(
        [NotNull] JobStorage storage,
        [NotNull] IEnumerable<IBackgroundProcess> processes,
        [NotNull] IDictionary<string, object> properties,
        [NotNull] BackgroundProcessingServerOptions options)
        : this(storage, GetProcesses(processes), properties, options)
    {
    }

    public BackgroundProcessingServer(
        [NotNull] JobStorage storage,
        [NotNull] IEnumerable<IBackgroundProcessDispatcherBuilder> dispatcherBuilders,
        [NotNull] IDictionary<string, object> properties,
        [NotNull] BackgroundProcessingServerOptions options)
        : this(new BackgroundServerProcess(storage, dispatcherBuilders, options, properties), options)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundProcessingServer"/>
    /// class and immediately starts all the given background processes.
    /// </summary>
    internal BackgroundProcessingServer(
        [NotNull] BackgroundServerProcess process,
        [NotNull] BackgroundProcessingServerOptions options)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        _dispatcher = CreateDispatcher();

        AppDomain.CurrentDomain.DomainUnload += OnCurrentDomainUnload;
        AppDomain.CurrentDomain.ProcessExit += OnCurrentDomainUnload;
    }

    public void SendStop()
    {
        ThrowIfDisposed();

        _stoppingCts.Cancel();
        _stoppedCts.CancelAfter(_options.StopTimeout);
        _shutdownCts.CancelAfter(_options.ShutdownTimeout);
    }

    public bool WaitForShutdown(TimeSpan timeout)
    {
        ThrowIfDisposed();

        // Set only once the server has shut down: Dispose after a wait that ended sooner still waits (for the
        // shutdown timeout at most), or it would dispose the timers that bring the stopped and shutdown signals
        bool shutDown = _dispatcher.Wait(timeout);
        if (shutDown)
        {
            Volatile.Write(ref _awaited, true);
        }

        return shutDown;
    }

    public async Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await _dispatcher.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _awaited, true);
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (!_stoppingCts.IsCancellationRequested)
        {
            SendStop();
        }

        if (!Volatile.Read(ref _awaited))
        {
            WaitForShutdown(Timeout.InfiniteTimeSpan);
        }

        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.DomainUnload -= OnCurrentDomainUnload;
        AppDomain.CurrentDomain.ProcessExit -= OnCurrentDomainUnload;

        _dispatcher.Dispose();
        _stoppingCts.Dispose();
        _stoppedCts.Dispose();
        _shutdownCts.Dispose();
    }

    private void OnCurrentDomainUnload(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        _logger.Warn("Stopping the server due to DomainUnload or ProcessExit event...");

        _stoppingCts.Cancel();
        _stoppedCts.Cancel();
        _shutdownCts.Cancel();

        WaitForShutdown(_options.LastChanceTimeout);
    }

    private static IBackgroundProcessDispatcherBuilder[] GetProcesses([NotNull] IEnumerable<IBackgroundProcess> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        return [.. processes.Select(static x => x.UseBackgroundPool(threadCount: 1))];
    }

    private IBackgroundDispatcher CreateDispatcher()
    {
        BackgroundExecution execution = new(
            new BackgroundExecutionOptions
            {
                Name = nameof(BackgroundServerProcess),
                ErrorThreshold = TimeSpan.Zero,
                StillErrorThreshold = TimeSpan.Zero,
                RetryDelay = retry => _options.RestartDelay
            },
            _stoppingCts.Token);

        return new BackgroundDispatcher(
            execution,
            RunServer,
            execution,
            ThreadFactory);
    }

    private void RunServer(Guid executionId, object? state)
    {
        _process.Execute(executionId, (BackgroundExecution)state!, _stoppingCts.Token, _stoppedCts.Token, _shutdownCts.Token); // The execution passed when creating the dispatcher
    }

    private static IEnumerable<Thread> ThreadFactory(ThreadStart threadStart)
    {
        yield return new Thread(threadStart)
        {
            IsBackground = true,
            Name = $"{nameof(BackgroundServerProcess)} #{Interlocked.Increment(ref _lastThreadId)}",
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
    }
}
