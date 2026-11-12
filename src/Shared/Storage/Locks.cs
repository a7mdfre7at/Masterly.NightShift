using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Storage.Shared;

/// <summary>
/// Makes a connection's distributed locks re-entrant: a connection that already holds a resource takes it again
/// at once, and the lock is released when the last of its handles is disposed. That is how SQL Server's
/// application locks behave on one connection.
/// </summary>
internal sealed class ReentrantLocks
{
    private readonly NightShiftLock _syncRoot = new();
    private readonly Dictionary<string, Entry> _held = new(StringComparer.Ordinal);

    public IDisposable Acquire(string resource, Func<IDisposable> acquire)
    {
        lock (_syncRoot)
        {
            if (_held.TryGetValue(resource, out Entry? entry))
            {
                entry.Count++;
                return new Handle(this, resource);
            }
        }

        IDisposable taken = acquire();

        lock (_syncRoot)
        {
            _held[resource] = new Entry(taken);
        }

        return new Handle(this, resource);
    }

    private void Release(string resource)
    {
        IDisposable? toRelease = null;

        lock (_syncRoot)
        {
            if (_held.TryGetValue(resource, out Entry? entry) && --entry.Count == 0)
            {
                _held.Remove(resource);
                toRelease = entry.Lock;
            }
        }

        toRelease?.Dispose();
    }

    private sealed class Entry(IDisposable taken)
    {
        public IDisposable Lock { get; } = taken;

        public int Count { get; set; } = 1;
    }

    private sealed class Handle(ReentrantLocks owner, string resource) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(resource);
            }
        }
    }
}

/// <summary>What a storage does to take, keep and release a lease on a lock resource.</summary>
internal interface ILeaseStore
{
    /// <summary>Takes the resource for <paramref name="owner"/> until <paramref name="expireAt"/>, if nobody holds it or the last lease ran out.</summary>
    bool TryTake(string resource, string owner, DateTime expireAt);

    /// <summary>Extends the lease, if <paramref name="owner"/> still holds it.</summary>
    bool TryRenew(string resource, string owner, DateTime expireAt);

    /// <summary>Releases the lease, if <paramref name="owner"/> still holds it.</summary>
    void Release(string resource, string owner);
}

/// <summary>
/// A distributed lock held as a lease that a timer renews every third of its time to live, so a process that
/// dies loses its locks once the lease runs out, while a live one keeps them as long as it needs. Once a renewal
/// finds the lease gone (another owner took it after it ran out), the lock is <see cref="Lost"/> and the timer
/// stops: renewing it again could only take the lease back from its new owner.
/// </summary>
internal sealed class LeaseLock : IDisposable
{
    // Renewing more often than this would only load the store
    private static readonly TimeSpan MinRenewalPeriod = TimeSpan.FromMilliseconds(100);
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(LeaseLock));

    private readonly NightShiftLock _syncRoot = new();
    private readonly ILeaseStore _store;
    private readonly string _resource;
    private readonly string _owner;
    private readonly TimeSpan _timeToLive;
    private readonly Timer _renewal;
    private bool _disposed;
    private bool _lost;

    private LeaseLock(ILeaseStore store, string resource, string owner, TimeSpan timeToLive)
    {
        _store = store;
        _resource = resource;
        _owner = owner;
        _timeToLive = timeToLive;
        TimeSpan period = TimeSpan.FromTicks(Math.Max(MinRenewalPeriod.Ticks, timeToLive.Ticks / 3));
        _renewal = new Timer(static state => ((LeaseLock)state!).Renew(), this, period, period); // The state is this lock
    }

    /// <summary>Whether a renewal found the lease taken by another owner. The lock no longer excludes anyone.</summary>
    internal bool Lost
    {
        get
        {
            lock (_syncRoot)
            {
                return _lost;
            }
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the resource, polling with a growing pause.</summary>
    /// <exception cref="DistributedLockTimeoutException">Another owner held the resource for the whole timeout.</exception>
    public static LeaseLock Acquire(ILeaseStore store, string resource, TimeSpan timeout, TimeSpan timeToLive)
    {
        string owner = Guid.NewGuid().ToString("N");
        long started = Environment.TickCount64;
        int pause = 10;

        while (true)
        {
            if (store.TryTake(resource, owner, StorageValues.ExpireAt(JobClock.UtcNow, timeToLive)))
            {
                return new LeaseLock(store, resource, owner, timeToLive);
            }

            long left = (long)timeout.TotalMilliseconds - (Environment.TickCount64 - started);
            if (left <= 0)
            {
                throw new DistributedLockTimeoutException(resource);
            }

            Thread.Sleep((int)Math.Min(left, pause));
            pause = Math.Min(pause * 2, 500);
        }
    }

    /// <summary>Stops renewing the lease and releases it. Never throws for a storage error: the lease runs out by itself.</summary>
    public void Dispose()
    {
        bool lost;

        // Taken by a renewal in progress too, so a renewal never runs (or reports a lost lease) after this
        lock (_syncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            lost = _lost;
        }

        _renewal.Dispose();
        if (lost) return;

        try
        {
            _store.Release(_resource, _owner);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The lease runs out by itself; another owner takes the lock once it does
            Logger.WarnException($"Couldn't release the lock on '{_resource}'.", ex);
        }
    }

    private void Renew()
    {
        lock (_syncRoot)
        {
            if (_disposed || _lost) return;

            try
            {
                if (!_store.TryRenew(_resource, _owner, StorageValues.ExpireAt(JobClock.UtcNow, _timeToLive)))
                {
                    _lost = true;
                    _renewal.Change(Timeout.Infinite, Timeout.Infinite);
                    Logger.Warn($"Lost the lock on '{_resource}': its lease ran out and another owner may have taken it.");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The next tick tries again, before the lease runs out
                Logger.WarnException($"Couldn't renew the lock on '{_resource}'.", ex);
            }
        }
    }
}
