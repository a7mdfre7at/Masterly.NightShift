// This file is part of Hangfire. Copyright © 2017 Hangfire OÜ.
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

using System.Diagnostics;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Processing;

internal sealed class BackgroundExecution : IBackgroundExecution
{
    // This fallback strategy is used for defensive purposes, when there are
    // problems with obtaining retry delays we should not under any circumstances
    // fall into constant immediate retries, consuming 100% of CPU.
    private static readonly TimeSpan FallbackRetryDelay = TimeSpan.FromSeconds(5);

    // Execution can be in one of three states: Running, Faulted or Failed. Each
    // one defines its own logging rules to lower the number of logged messages,
    // to not to make stress on logging subsystem with thousands of messages in
    // case of transient faults.
    private readonly ManualResetEvent _stopped = new(false);
    private Stopwatch? _faultedSince;
    private Stopwatch? _failedSince;
    private Stopwatch? _lastException;
    private int _exceptionsCount;

    private readonly CancellationToken _stopToken;
    private readonly BackgroundExecutionOptions _options;
    private readonly ILog _logger;

    private readonly Stopwatch _createdAt;
    private Stopwatch? _stoppedAt;
    private readonly CancellationTokenRegistration _stopRegistration;

    private volatile bool _disposed;

    public BackgroundExecution([NotNull] BackgroundExecutionOptions options, CancellationToken stopToken)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        _logger = LogProvider.GetLogger(GetType());
        _createdAt = Stopwatch.StartNew();

        _stopToken = stopToken;

        _stopRegistration = _stopToken.Register(SetStoppedAt);
    }

    public bool StopRequested => _disposed || _stopToken.IsCancellationRequested;

    public void Run(Action<Guid, object?> callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Guid executionId = Guid.NewGuid();

        // ExecutionId correlates the log entries of one execution loop. Thread.ManagedThreadId can't, because
        // the async loop runs its continuations on different threads.
        HandleStarted(executionId, out TimeSpan nextDelay);

        // Every exception is handled inside the loop, so only the stop token ends it
        while (!StopRequested)
        {
            try
            {
                // Being unable to wait means the execution was stopped meanwhile
                if (nextDelay != TimeSpan.Zero && !HandleDelay(executionId, nextDelay))
                {
                    break;
                }

                callback(executionId, state);
                HandleSuccess(out nextDelay);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken.Equals(_stopToken) || StopRequested)
            {
                // The token of the exception may be a linked one, so StopRequested is checked too
                break;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                HandleException(executionId, ex, out nextDelay);
            }
        }

        HandleStop(executionId);
    }

    public async Task RunAsync(Func<Guid, object?, Task> callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Guid executionId = Guid.NewGuid();

        HandleStarted(executionId, out TimeSpan nextDelay);

        // Every exception is handled inside the loop, so only the stop token ends it
        while (!StopRequested)
        {
            try
            {
                // Being unable to wait means the execution was stopped meanwhile
                if (nextDelay != TimeSpan.Zero && !await HandleDelayAsync(executionId, nextDelay).ConfigureAwait(true))
                {
                    break;
                }

                await callback(executionId, state).ConfigureAwait(true);
                HandleSuccess(out nextDelay);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken.Equals(_stopToken) || StopRequested)
            {
                // The token of the exception may be a linked one, so StopRequested is checked too
                break;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                HandleException(executionId, ex, out nextDelay);
            }
        }

        HandleStop(executionId);
    }

    public void Dispose()
    {
        lock (_stopped)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _stopRegistration.Dispose();

            _stopped.Set();
            _stopped.Dispose();
        }
    }

    public void NotifySucceeded()
    {
        if (StopRequested)
        {
            return;
        }

        // This is an optimization to avoid lock acquisitions on every
        // loop iteration. It is possible that value will become null
        // upon entering the lock, but this race condition is benign.
        if (Volatile.Read(ref _faultedSince) != null)
        {
            ToRunningState();
        }
    }

    public override string ToString()
    {
        return _options?.Name ?? GetType().Name;
    }

    private void HandleStarted(Guid executionId, out TimeSpan initialDelay)
    {
        _logger.Debug($"{GetExecutionLoopTemplate(executionId)} has started in {_createdAt.Elapsed.TotalMilliseconds} ms");

        // Looks weird, but several times I was initializing the nextDelay variable
        // inside the execution loop by mistake. This lead to immediate looped invocation
        // with no delays and 100% of CPU consumption on transient exceptions, that is
        // completely unacceptable. So this is just a defensive technique.
        initialDelay = TimeSpan.Zero;
    }

    private bool HandleDelay(Guid executionId, TimeSpan delay)
    {
        try
        {
            LogRetry(executionId, delay);
            return !_stopped.WaitOne(delay, _stopToken);
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken.Equals(_stopToken) || StopRequested)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            LogUnableWait(executionId, delay, ex);
            return false;
        }
    }

    private async Task<bool> HandleDelayAsync(Guid executionId, TimeSpan delay)
    {
        try
        {
            LogRetry(executionId, delay);
            return !await _stopped.WaitOneAsync(delay, _stopToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken.Equals(_stopToken) || StopRequested)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            LogUnableWait(executionId, delay, ex);
            return false;
        }
    }

    private void LogUnableWait(Guid executionId, TimeSpan delay, Exception ex)
    {
        _logger.FatalException($"{GetExecutionLoopTemplate(executionId)} was unable to wait for '{delay}' delay due to an exception. Execution will be stopped.", ex);
    }

    private void LogRetry(Guid executionId, TimeSpan delay)
    {
        _logger.Debug($"{GetExecutionLoopTemplate(executionId)} will be retried in {delay}...");
    }

    private void NormalizeDelay(ref TimeSpan retryDelay)
    {
        if (retryDelay <= TimeSpan.Zero)
        {
            _logger.Warn($"{GetExecutionTemplate()} adjusted the retry delay from {retryDelay} to {FallbackRetryDelay}");
            retryDelay = FallbackRetryDelay;
        }
    }

    private void HandleSuccess(out TimeSpan nextDelay)
    {
        nextDelay = TimeSpan.Zero;
        NotifySucceeded();
    }

    private void HandleException(Guid executionId, Exception exception, out TimeSpan delay)
    {
        if (StopRequested)
        {
            delay = FallbackRetryDelay;
            return;
        }

        try
        {
            // Some code might cache exception object and throw the same instance
            // from multiple threads, despite it's not recommended to do. However
            // bad things happen, and we should have some diagnostic tools to
            // understand what's happened and what was the original exception which
            // is being modified.
            if (!exception.Data.Contains("ExecutionId"))
            {
                exception.Data.Add("ExecutionId", executionId);
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            _logger.WarnException($"Was unable to add the ExecutionId property to the exception object, please see inner exception for details. Original exception: ${exception.GetType()} (${exception.Message})", ex);
        }

        ToFailedState(exception, out delay);

        _logger.DebugException($"{GetExecutionLoopTemplate(executionId)} caught an exception and will be retried in {delay}", exception);
    }

    private void HandleStop(Guid executionId)
    {
        Stopwatch? stoppedAt = Volatile.Read(ref _stoppedAt);
        _logger.Debug($"{GetExecutionLoopTemplate(executionId)} stopped in {stoppedAt?.Elapsed.TotalMilliseconds ?? 0} ms");
    }

    private void ToRunningState()
    {
        lock (_stopped)
        {
            if (_disposed)
            {
                return;
            }

            if (_failedSince != null)
            {
                // Since we are moving from the Failed state, one or more error messages were
                // logged, and we should notify administrators that operations are restored.
                _logger.Info($"{GetExecutionTemplate()} recovered from the Failed state after {_failedSince?.Elapsed} and is in the Running state now");
            }
            else if (_faultedSince != null)
            {
                // We are moving from Faulted state, and there may be no any log messages (unless
                // DEBUG level is enabled). But since some operations were delayed, we should notify
                // administrators about this event, if a configured threshold is reached (to not to
                // log thousands of messages).
                _logger.Log(
                    _faultedSince.Elapsed > _options.WarningThreshold ? LogLevel.Info : LogLevel.Debug,
                    () => $"{GetExecutionTemplate()} recovered from the Faulted state after {_faultedSince?.Elapsed} and is in the Running state now");
            }

            _exceptionsCount = 0;

            _faultedSince = null;
            _failedSince = null;
            _lastException = null;
        }
    }

    private void ToFailedState(Exception exception, out TimeSpan retryDelay)
    {
        lock (_stopped)
        {
            retryDelay = FallbackRetryDelay;

            if (_disposed)
            {
                return;
            }

            _exceptionsCount++;
            _lastException = Stopwatch.StartNew();

            Func<int, TimeSpan> optionsRetryDelay = _options.RetryDelay;
            if (optionsRetryDelay != null)
            {
                retryDelay = optionsRetryDelay(_exceptionsCount);
            }

            NormalizeDelay(ref retryDelay);

            if (_faultedSince == null)
            {
                _faultedSince = Stopwatch.StartNew();

                // If threshold is zero, we'll go to the Failed state directly and log error anyway.
                if (_options.ErrorThreshold > TimeSpan.Zero)
                {
                    _logger.DebugException($"{GetExecutionTemplate()} is in the Faulted state now due to an exception, execution will be retried in no more than {retryDelay}", exception);
                }
            }

            if (_failedSince == null && _faultedSince.Elapsed > _options.ErrorThreshold)
            {
                // Transition to Failed state, we should log the error message.
                _logger.ErrorException($"{GetExecutionTemplate()} is in the Failed state now due to an exception, execution will be retried in no more than {retryDelay}", exception);
                _failedSince = Stopwatch.StartNew();
            }
            else if (_failedSince != null && _lastException.Elapsed >= _options.StillErrorThreshold)
            {
                // Still in the Failed state, we should log the error message as a reminder,
                // but shouldn't do this too often, especially for short retry intervals.
                _logger.ErrorException($"{GetExecutionTemplate()} is still in the Failed state for {_failedSince?.Elapsed} due to an exception, will be retried in no more than {retryDelay}", exception);
            }
        }
    }

    private string GetExecutionLoopTemplate(Guid executionId)
    {
        return $"Execution loop {ToString()}:{executionId.ToString()[..8]}";
    }

    private string GetExecutionTemplate()
    {
        return $"Execution {ToString()}";
    }

    private void SetStoppedAt()
    {
        Interlocked.CompareExchange(ref _stoppedAt, Stopwatch.StartNew(), null);
    }
}
