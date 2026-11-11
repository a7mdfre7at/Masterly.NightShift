using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Storage.Shared;

/// <summary>
/// A job taken from a queue. It is invisible to other workers while a timer keeps it alive (every
/// <c>keepAliveInterval</c>), and goes back to its queue when disposed without being removed or requeued, so a
/// worker that stops in the middle of a job doesn't lose it. When the process dies, the storage puts the job back
/// once its invisibility timeout passes.
/// </summary>
internal abstract class FetchedJobBase : IFetchedJob
{
    private readonly NightShiftLock _syncRoot = new();
    private readonly Timer? _keepAlive;
    private bool _done;
    private bool _disposed;

    protected FetchedJobBase(string jobId, string queue, TimeSpan? keepAliveInterval)
    {
        JobId = jobId;
        Queue = queue;

        if (keepAliveInterval.HasValue && keepAliveInterval.Value > TimeSpan.Zero)
        {
            _keepAlive = new Timer(static state => ((FetchedJobBase)state!).RunKeepAlive(), this, keepAliveInterval.Value, keepAliveInterval.Value);
        }
    }

    public string JobId { get; }

    public string Queue { get; }

    public void RemoveFromQueue()
    {
        lock (_syncRoot)
        {
            if (_done) return;
            Remove();
            _done = true;
        }
    }

    public void Requeue()
    {
        lock (_syncRoot)
        {
            if (_done) return;
            ReturnToQueue();
            _done = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _keepAlive?.Dispose();

        lock (_syncRoot)
        {
            if (!_done)
            {
                ReturnToQueue();
                _done = true;
            }
        }
    }

    /// <summary>Called after a committed transaction removed the entry, so disposing doesn't put it back.</summary>
    internal void MarkRemoved()
    {
        lock (_syncRoot)
        {
            _done = true;
        }
    }

    /// <summary>Deletes the queue entry, if this worker still owns it.</summary>
    protected abstract void Remove();

    /// <summary>Makes the entry visible again, if this worker still owns it.</summary>
    protected abstract void ReturnToQueue();

    /// <summary>Tells the storage this worker still owns the entry, so it doesn't go back to the queue.</summary>
    protected virtual void KeepAlive()
    {
    }

    private void RunKeepAlive()
    {
        try
        {
            lock (_syncRoot)
            {
                if (_done || _disposed) return;
                KeepAlive();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The next tick tries again; past the invisibility timeout another worker may take the job
            LogProvider.GetLogger(GetType()).WarnException($"Couldn't keep job #{JobId} invisible in queue '{Queue}'.", ex);
        }
    }
}
