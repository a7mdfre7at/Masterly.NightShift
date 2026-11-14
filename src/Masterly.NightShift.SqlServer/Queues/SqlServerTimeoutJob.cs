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

using Masterly.NightShift.Logging;

namespace Masterly.NightShift.SqlServer;

internal sealed class SqlServerTimeoutJob : IFetchedJob
{
    private readonly ILog _logger = LogProvider.GetLogger(typeof(SqlServerTimeoutJob));

    private readonly NightShiftLock _syncRoot = new();
    private readonly SqlServerStorage _storage;
    private bool _disposed;
    private bool _removedFromQueue;
    private bool _requeued;
    private SqlServerWriteOnlyTransaction? _transaction;

    private long _lastHeartbeat;
    private readonly TimeSpan _interval;

    public SqlServerTimeoutJob(
        [NotNull] SqlServerStorage storage,
        long id,
        [NotNull] string jobId,
        [NotNull] string queue,
        [NotNull] DateTime? fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(queue);
        if (fetchedAt is null)
        {
            throw new ArgumentNullException(nameof(fetchedAt)); // a DateTime?: ThrowIfNull would box it
        }

        _storage = storage;

        Id = id;
        JobId = jobId;
        Queue = queue;
        FetchedAt = fetchedAt.Value;

        if (storage.SlidingInvisibilityTimeout.HasValue)
        {
            _lastHeartbeat = TimestampHelper.GetTimestamp();
            _interval = TimeSpan.FromSeconds(storage.SlidingInvisibilityTimeout.Value.TotalSeconds / 5);
            storage.HeartbeatProcess.Track(this);
        }
    }

    public long Id { get; }
    public string JobId { get; }
    public string Queue { get; }

    internal DateTime? FetchedAt { get; private set; }

    public void RemoveFromQueue()
    {
        lock (_syncRoot)
        {
            if (_transaction != null && _transaction.Committed)
            {
                return;
            }

            if (!FetchedAt.HasValue)
            {
                return;
            }

            _storage.UseConnection(
                null,
                static (storage, connection, ctx) => connection.Execute(
                    storage.GetQueryFromTemplate(static schemaName =>
                        $@"delete JQ from [{schemaName}].JobQueue JQ with (forceseek, rowlock) where Queue = @queue and Id = @id and FetchedAt = @fetchedAt"),
                    new { queue = ctx.Queue, id = ctx.Id, fetchedAt = ctx.FetchedAt },
                    commandTimeout: storage.CommandTimeout),
                this);

            _removedFromQueue = true;
        }
    }

    public void Requeue()
    {
        lock (_syncRoot)
        {
            if (_transaction != null && _transaction.Committed)
            {
                return;
            }

            if (!FetchedAt.HasValue)
            {
                return;
            }

            _storage.UseConnection(
                null,
                static (storage, connection, ctx) => connection.Execute(
                    storage.GetQueryFromTemplate(static schemaName =>
                        $@"update JQ set FetchedAt = null from [{schemaName}].JobQueue JQ with (forceseek, rowlock) where Queue = @queue and Id = @id and FetchedAt = @fetchedAt"),
                     new { queue = ctx.Queue, id = ctx.Id, fetchedAt = ctx.FetchedAt },
                     commandTimeout: storage.CommandTimeout),
                this);

            FetchedAt = null;
            _requeued = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        DisposeTimer();

        lock (_syncRoot)
        {
            if (!_removedFromQueue && !_requeued && (_transaction == null || !_transaction.Committed))
            {
                Requeue();
            }
        }
    }

    internal void SetTransaction(SqlServerWriteOnlyTransaction transaction)
    {
        lock (_syncRoot)
        {
            _transaction = transaction;
        }
    }

    internal void DisposeTimer()
    {
        _storage.HeartbeatProcess.Untrack(this);
    }

    internal void ExecuteKeepAliveQueryIfRequired()
    {
        _storage.HeartbeatProcess.KeepAlive([this]);
    }

    /// <summary>
    /// Takes the job's lock for a keep-alive when one is due and may be sent, without waiting: a job whose lock is
    /// held (it is being removed from its queue or put back, or a transaction that removes it is committing) is
    /// left for the next pass, so it delays no other job. On <c>true</c> the caller sends the keep-alive with
    /// <paramref name="fetchedAt"/>, reports what it found, and calls <see cref="EndKeepAlive"/> on the same thread.
    /// </summary>
    internal bool TryBeginKeepAlive(long now, out DateTime fetchedAt)
    {
        fetchedAt = default;

        if (TimestampHelper.Elapsed(now, Interlocked.Read(ref _lastHeartbeat)) < _interval)
        {
            return false;
        }

        if (!NightShiftLocks.TryEnter(_syncRoot))
        {
            return false;
        }

        if (FetchedAt is not { } current || _requeued || _removedFromQueue || _transaction is { Committed: true })
        {
            NightShiftLocks.Exit(_syncRoot);
            return false;
        }

        fetchedAt = current;
        return true;
    }

    /// <summary>The keep-alive renewed the row: it is now fetched at <paramref name="fetchedAt"/>.</summary>
    internal void KeepAliveRenewed(long now, DateTime fetchedAt)
    {
        FetchedAt = fetchedAt;
        Interlocked.Exchange(ref _lastHeartbeat, now);
        _logger.Trace($"Keep-alive query for message {Id} sent");
    }

    /// <summary>The row was fetched again since this worker fetched it: no more keep-alives.</summary>
    internal void KeepAliveFoundTakenOver(long now)
    {
        FetchedAt = null;
        Interlocked.Exchange(ref _lastHeartbeat, now);
        _logger.Warn($"Background job identifier '{JobId}' was fetched by another worker, will not execute keep alive.");
    }

    /// <summary>Releases the lock <see cref="TryBeginKeepAlive"/> took.</summary>
    internal void EndKeepAlive()
    {
        NightShiftLocks.Exit(_syncRoot);
    }

    /// <summary>
    /// Holds off keep-alives while a transaction that removes the job from its queue commits: its delete matches
    /// the row by the <see cref="FetchedAt"/> read when the command is built, which a keep-alive would change.
    /// </summary>
    internal void EnterCommit()
    {
        NightShiftLocks.Enter(_syncRoot);
    }

    /// <summary>Lets keep-alives run again after <see cref="EnterCommit"/>, on the same thread.</summary>
    internal void ExitCommit()
    {
        NightShiftLocks.Exit(_syncRoot);
    }
}