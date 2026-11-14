namespace Masterly.NightShift.Storage.Shared;

/// <summary>
/// Wakes the workers waiting for a job in a queue when a job is enqueued to it, and only them: a worker listens
/// to its own queues, so jobs enqueued to a busy queue don't make the workers of the other queues look at theirs
/// again. A worker that misses a signal (a storage whose signals cross processes may lose one) still looks at
/// its queues every poll interval.
/// </summary>
internal sealed class QueueSignals
{
    private readonly NightShiftLock _syncRoot = new();
    private readonly Dictionary<string, List<Listener>> _listeners = new(StringComparer.Ordinal);

    /// <summary>The number of queues somebody listens to.</summary>
    internal int ListenedQueueCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _listeners.Count;
            }
        }
    }

    /// <summary>Starts listening to <paramref name="queues"/>. Dispose the listener to stop.</summary>
    public Listener Listen(IEnumerable<string> queues)
    {
        Listener listener = new(this, [.. queues.Distinct(StringComparer.Ordinal)]);

        lock (_syncRoot)
        {
            foreach (string queue in listener.Queues)
            {
                if (!_listeners.TryGetValue(queue, out List<Listener>? listeners))
                {
                    listeners = [];
                    _listeners.Add(queue, listeners);
                }

                listeners.Add(listener);
            }
        }

        return listener;
    }

    /// <summary>Wakes the listeners of <paramref name="queue"/>.</summary>
    public void Signal(string queue)
    {
        lock (_syncRoot)
        {
            if (_listeners.TryGetValue(queue, out List<Listener>? listeners))
            {
                foreach (Listener listener in listeners)
                {
                    // Its continuations run asynchronously, so nothing runs under the lock
                    listener.Signaled.TrySetResult();
                }
            }
        }
    }

    private void Remove(Listener listener)
    {
        lock (_syncRoot)
        {
            foreach (string queue in listener.Queues)
            {
                if (_listeners.TryGetValue(queue, out List<Listener>? listeners) && listeners.Remove(listener) && listeners.Count == 0)
                {
                    _listeners.Remove(queue);
                }
            }
        }
    }

    /// <summary>
    /// A worker's ear on its queues. Call <see cref="Reset"/> before each look at the queues and
    /// <see cref="Wait"/> after a look that found nothing: a signal sent in between ends the wait at once, so a
    /// job enqueued while the worker looked is never left waiting for the next poll.
    /// </summary>
    internal sealed class Listener : IDisposable
    {
        private readonly QueueSignals _owner;
        private int _disposed;

        internal Listener(QueueSignals owner, IReadOnlyList<string> queues)
        {
            _owner = owner;
            Queues = queues;
        }

        public IReadOnlyList<string> Queues { get; }

        /// <summary>Completed by a signal; replaced by <see cref="Reset"/> once completed. Guarded by the owner's lock.</summary>
        internal TaskCompletionSource Signaled { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Forgets the signals sent so far: the look at the queues that follows sees the jobs they announced.</summary>
        public void Reset()
        {
            lock (_owner._syncRoot)
            {
                if (Signaled.Task.IsCompleted)
                {
                    Signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        /// <summary>Waits for a signal sent since the last <see cref="Reset"/>, up to <paramref name="timeout"/>.</summary>
        /// <returns>Whether a signal came.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        public bool Wait(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task signaled;
            lock (_owner._syncRoot)
            {
                signaled = Signaled.Task;
            }

            // The task only ever completes successfully, so this returns or throws OperationCanceledException
            return signaled.Wait((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue), cancellationToken);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Remove(this);
            }
        }
    }
}
