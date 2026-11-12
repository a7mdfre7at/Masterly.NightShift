using System.Data.Common;
using System.Linq.Expressions;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage.Monitoring;
using Masterly.NightShift.Storage.Shared;
using Microsoft.EntityFrameworkCore;

namespace Masterly.NightShift.EntityFrameworkCore;

internal sealed class EfConnection(EntityFrameworkCoreStorage storage) : JobStorageConnection, ILeaseStore
{
    private readonly ReentrantLocks _locks = new();

    public override IWriteOnlyTransaction CreateWriteTransaction() => new EfTransaction(storage, this);

    public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(resource);
        string stored = EfLimits.LockResource(resource);
        return _locks.Acquire(stored, () => LeaseLock.Acquire(this, stored, timeout, storage.Options.DistributedLockLifetime));
    }

    public override string? CreateExpiredJob(Job job, IDictionary<string, string?> parameters, DateTime createdAt, TimeSpan expireIn)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (string name in parameters.Keys)
        {
            EfLimits.EnsureJobParameterNameFits(name);
        }

        StoredJob stored = StoredJob.Create(0, job, parameters, createdAt, StorageValues.ExpireAt(createdAt, expireIn));
        JobRow row = new()
        {
            InvocationData = stored.InvocationData,
            Arguments = stored.Arguments,
            CreatedAt = createdAt,
            ExpireAt = stored.ExpireAt,
            Parameters = [.. stored.Parameters.Select(static x => new JobParameterRow { Name = x.Key, Value = x.Value })],
        };

        using DbContext context = storage.CreateContext();
        context.Add(row);
        context.SaveChanges();
        return StorageValues.FormatJobId(row.Id);
    }

    public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queues);
        if (queues.Length == 0)
        {
            throw new ArgumentException("Queue array must be non-empty.", nameof(queues));
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task enqueued = storage.Enqueued;

            if (TryFetch(queues, out bool contended) is { } fetched)
            {
                return fetched;
            }

            // Entries were there, but other workers claimed every one first: more may be waiting behind them
            if (contended)
            {
                continue;
            }

            try
            {
                enqueued.Wait((int)storage.Options.QueuePollInterval.TotalMilliseconds, cancellationToken);
            }
            catch (AggregateException)
            {
                // The task only ever completes successfully; nothing to report
            }
        }
    }

    public override void SetJobParameter(string id, string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        if (!StorageValues.TryParseJobId(id, out long jobId))
        {
            return;
        }

        EfTransaction.Apply(storage, [new SetJobParameterOperation(jobId, name, value)]);
    }

    public override string? GetJobParameter(string id, string name)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        if (!StorageValues.TryParseJobId(id, out long jobId))
        {
            return null;
        }

        return Read(context => context.Set<JobParameterRow>().Where(x => x.JobId == jobId && x.Name == name).Select(static x => x.Value).FirstOrDefault());
    }

    public override JobData? GetJobData(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        if (!StorageValues.TryParseJobId(jobId, out long id))
        {
            return null;
        }

        return Read(context =>
        {
            JobRow? row = context.Set<JobRow>().AsNoTracking().Include(static x => x.Parameters).FirstOrDefault(x => x.Id == id);
            return row != null ? EfRows.ToJob(row).ToJobData() : null;
        });
    }

    public override StateData? GetStateData(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        if (!StorageValues.TryParseJobId(jobId, out long id))
        {
            return null;
        }

        return Read(context =>
        {
            JobRow? row = context.Set<JobRow>().AsNoTracking().FirstOrDefault(x => x.Id == id);
            return row != null ? EfRows.ToJob(row).ToStateData() : null;
        });
    }

    public override void AnnounceServer(string serverId, ServerContext context)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        ArgumentNullException.ThrowIfNull(context);
        EfLimits.EnsureServerIdFits(serverId);

        string data = StorageValues.SerializeServer(context);
        storage.InTransaction(db =>
        {
            ServerRow? row = db.Set<ServerRow>().Find(serverId);
            if (row == null)
            {
                db.Add(row = new ServerRow { Id = serverId });
            }

            row.Data = data;
            row.LastHeartbeat = JobClock.UtcNow;
        });
    }

    public override void RemoveServer(string serverId)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        Read(context => context.Set<ServerRow>().Where(x => x.Id == serverId).ExecuteDelete());
    }

    public override void Heartbeat(string serverId)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        DateTime now = JobClock.UtcNow;

        if (Read(context => context.Set<ServerRow>().Where(x => x.Id == serverId).ExecuteUpdate(s => s.SetProperty(x => x.LastHeartbeat, now))) == 0)
        {
            throw new BackgroundServerGoneException();
        }
    }

    public override int RemoveTimedOutServers(TimeSpan timeOut)
    {
        if (timeOut.Duration() != timeOut)
        {
            throw new ArgumentException("The `timeOut` value must be positive.", nameof(timeOut));
        }

        DateTime cutoff = JobClock.UtcNow - timeOut;
        return Read(context => context.Set<ServerRow>().Where(x => x.LastHeartbeat < cutoff).ExecuteDelete());
    }

    public override HashSet<string> GetAllItemsFromSet(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Read(context => context.Set<SetRow>().Where(x => x.Key == key).Select(static x => x.Value).ToHashSet());
    }

    public override string? GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore)
    {
        return GetFirstByLowestScoreFromSet(key, fromScore, toScore, 1).FirstOrDefault();
    }

    public override List<string> GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore, int count)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (count <= 0)
        {
            throw new ArgumentException("The value must be a positive number", nameof(count));
        }

        if (toScore < fromScore)
        {
            throw new ArgumentException("The `toScore` value must be higher or equal to the `fromScore` value.", nameof(toScore));
        }

        return Read(context => context.Set<SetRow>()
            .Where(x => x.Key == key && x.Score >= fromScore && x.Score <= toScore)
            .OrderBy(static x => x.Score)
            .ThenBy(static x => x.Value)
            .Take(count)
            .Select(static x => x.Value)
            .ToList());
    }

    public override long GetSetCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Read(context => context.Set<SetRow>().LongCount(x => x.Key == key));
    }

    public override long GetSetCount(IEnumerable<string> keys, int limit)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        string[] names = [.. keys];
        return Read(context => context.Set<SetRow>().Where(x => names.Contains(x.Key)).Take(limit).LongCount());
    }

    public override bool GetSetContains(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return Read(context => context.Set<SetRow>().Any(x => x.Key == key && x.Value == value));
    }

    public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        // Positions count from 0, as on SQL Server: what lies before the first item, or an empty range, reads nothing
        int start = Math.Max(0, startingFrom);
        if (endingAt < start)
        {
            return [];
        }

        return Read(context => context.Set<SetRow>()
            .Where(x => x.Key == key)
            .OrderBy(static x => x.Score)
            .ThenBy(static x => x.Value)
            .Skip(start)
            .Take((int)Math.Min((long)endingAt - start + 1, int.MaxValue))
            .Select(static x => x.Value)
            .ToList());
    }

    public override TimeSpan GetSetTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TimeToLive<SetRow>(x => x.Key == key && x.ExpireAt != null, static x => x.ExpireAt);
    }

    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(keyValuePairs);
        EfTransaction.Apply(storage, [new SetRangeInHashOperation(key, [.. keyValuePairs])]);
    }

    public override Dictionary<string, string>? GetAllEntriesFromHash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        List<HashRow> rows = Read(context => context.Set<HashRow>().AsNoTracking().Where(x => x.Key == key).ToList());
        return rows.Count == 0 ? null : rows.ToDictionary(static x => x.Field, static x => x.Value!, StringComparer.Ordinal); // A field set to null reads back as null
    }

    public override string? GetValueFromHash(string key, string name)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(name);
        return Read(context => context.Set<HashRow>().Where(x => x.Key == key && x.Field == name).Select(static x => x.Value).FirstOrDefault());
    }

    public override long GetHashCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Read(context => context.Set<HashRow>().LongCount(x => x.Key == key));
    }

    public override TimeSpan GetHashTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TimeToLive<HashRow>(x => x.Key == key && x.ExpireAt != null, static x => x.ExpireAt);
    }

    public override long GetListCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Read(context => context.Set<ListRow>().LongCount(x => x.Key == key));
    }

    public override List<string> GetAllItemsFromList(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Read(context => context.Set<ListRow>().Where(x => x.Key == key).OrderByDescending(static x => x.Id).Select(static x => x.Value).ToList());
    }

    public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        // Positions count from 0, as on SQL Server: what lies before the first item, or an empty range, reads nothing
        int start = Math.Max(0, startingFrom);
        if (endingAt < start)
        {
            return [];
        }

        return Read(context => context.Set<ListRow>()
            .Where(x => x.Key == key)
            .OrderByDescending(static x => x.Id)
            .Skip(start)
            .Take((int)Math.Min((long)endingAt - start + 1, int.MaxValue))
            .Select(static x => x.Value)
            .ToList());
    }

    public override TimeSpan GetListTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TimeToLive<ListRow>(x => x.Key == key && x.ExpireAt != null, static x => x.ExpireAt);
    }

    public override long GetCounter(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Read(context => EfRows.ReadCounters(context, [key])).GetValueOrDefault(key);
    }

    bool ILeaseStore.TryTake(string resource, string owner, DateTime expireAt)
    {
        DateTime now = JobClock.UtcNow;
        using DbContext context = storage.CreateContext();
        context.Set<LockRow>().Where(x => x.Resource == resource && x.ExpireAt < now).ExecuteDelete();

        // Providers report a duplicate key with exceptions of their own: the lease is held when its row is there
        // after the insert failed. A row released in between is taken again once; other failures surface.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                context.Add(new LockRow { Resource = resource, Owner = owner, ExpireAt = expireAt });
                context.SaveChanges();
                return true;
            }
            catch (DbUpdateException ex)
            {
                context.ChangeTracker.Clear();
                if (context.Set<LockRow>().Any(x => x.Resource == resource))
                {
                    return false;
                }

                if (attempt == 2)
                {
                    LogProvider.GetLogger(typeof(EfConnection)).ErrorException($"Couldn't take the lock on '{resource}'.", ex);
                    throw;
                }
            }
        }
    }

    bool ILeaseStore.TryRenew(string resource, string owner, DateTime expireAt)
    {
        return Read(context => context.Set<LockRow>().Where(x => x.Resource == resource && x.Owner == owner).ExecuteUpdate(s => s.SetProperty(x => x.ExpireAt, expireAt))) > 0;
    }

    void ILeaseStore.Release(string resource, string owner)
    {
        try
        {
            Read(context => context.Set<LockRow>().Where(x => x.Resource == resource && x.Owner == owner).ExecuteDelete());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The lease runs out by itself; another owner takes the lock once it does
            LogProvider.GetLogger(typeof(EfConnection)).WarnException($"Couldn't release the lock on '{resource}'.", ex);
        }
    }

    /// <summary>
    /// Claims the oldest visible entry of the first queue that has one. <paramref name="contended"/> tells that
    /// nothing was claimed only because other workers claimed the entries seen first.
    /// </summary>
    private EfFetchedJob? TryFetch(string[] queues, out bool contended)
    {
        DateTime now = JobClock.UtcNow;
        DateTime staleBefore = now - storage.Options.InvisibilityTimeout;
        using DbContext context = storage.CreateContext();
        contended = false;

        foreach (string queue in queues)
        {
            // When another worker claimed the entry first, tries the next one
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var candidate = context.Set<JobQueueRow>()
                    .Where(x => x.Queue == queue && (x.FetchedAt == null || x.FetchedAt < staleBefore))
                    .OrderBy(static x => x.Id)
                    .Select(static x => new { x.Id, x.JobId })
                    .FirstOrDefault();
                if (candidate == null)
                {
                    break;
                }

                string token = Guid.NewGuid().ToString("N");
                int claimed = context.Set<JobQueueRow>()
                    .Where(x => x.Id == candidate.Id && (x.FetchedAt == null || x.FetchedAt < staleBefore))
                    .ExecuteUpdate(s => s.SetProperty(x => x.FetchedAt, now).SetProperty(x => x.FetchToken, token));

                if (claimed == 1)
                {
                    return new EfFetchedJob(storage, candidate.Id, candidate.JobId, queue, token);
                }

                contended = true;
            }
        }

        return null;
    }

    private TimeSpan TimeToLive<TRow>(Expression<Func<TRow, bool>> predicate, Expression<Func<TRow, DateTime?>> expireAt)
        where TRow : class
    {
        return StorageValues.TimeToLive(Read(context => context.Set<TRow>().Where(predicate).Min(expireAt)));
    }

    private T Read<T>(Func<DbContext, T> read)
    {
        using DbContext context = storage.CreateContext();
        return read(context);
    }
}

/// <summary>Turns rows into the shared storage model, and reads counters.</summary>
internal static class EfRows
{
    public static StoredJob ToJob(JobRow row)
    {
        StoredJob job = new()
        {
            Id = row.Id,
            InvocationData = row.InvocationData,
            Arguments = row.Arguments,
            CreatedAt = row.CreatedAt,
            ExpireAt = row.ExpireAt,
            StateName = row.StateName,
            State = row.StateName != null ? new StoredState(row.StateName, row.StateReason, row.StateChangedAt ?? row.CreatedAt, row.StateData) : null,
        };

        foreach (JobParameterRow parameter in row.Parameters)
        {
            job.Parameters[parameter.Name] = parameter.Value;
        }

        return job;
    }

    /// <summary>
    /// The values of counters: their increments not merged yet, plus their merged values. Both tables are read by
    /// one statement, so an aggregator that commits a merge meanwhile isn't seen half done (counted twice or not
    /// at all) where the database reads one statement from one snapshot.
    /// </summary>
    public static Dictionary<string, long> ReadCounters(DbContext context, IReadOnlyCollection<string> keys)
    {
        string[] names = [.. keys];
        if (names.Length == 0)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        return context.Set<CounterRow>()
            .Where(x => names.Contains(x.Key))
            .Select(static x => new { x.Key, x.Value })
            .Concat(context.Set<AggregatedCounterRow>().Where(x => names.Contains(x.Key)).Select(static x => new { x.Key, x.Value }))
            .GroupBy(static x => x.Key)
            .Select(static x => new { x.Key, Value = x.Sum(static y => y.Value) })
            .ToDictionary(static x => x.Key, static x => x.Value, StringComparer.Ordinal);
    }
}

internal sealed class EfTransaction(EntityFrameworkCoreStorage storage, EfConnection connection) : RecordingTransaction
{
    private const int Attempts = 5;
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(EfTransaction));

    /// <summary>
    /// Applies operations in one database transaction. Two transactions that add the same row at once conflict;
    /// the loser starts again from scratch after a short random pause, so it updates the row the winner added.
    /// </summary>
    /// <exception cref="ArgumentException">A key, value or name doesn't fit its column; nothing was written.</exception>
    public static void Apply(EntityFrameworkCoreStorage storage, IReadOnlyList<StorageOperation> operations)
    {
        EfLimits.EnsureFits(operations);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                storage.InTransaction(context =>
                {
                    EfOperationWriter writer = new(context, immediate: true);
                    foreach (StorageOperation operation in operations)
                    {
                        writer.Write(operation);
                    }
                });
                return;
            }
            catch (DbUpdateException ex) when (attempt < Attempts)
            {
                // Another transaction added the same row first: the next attempt finds and updates it. The pause
                // grows and differs between the losers, so they don't collide again at once.
                Logger.DebugException($"A transaction conflicted with another one (attempt {attempt} of {Attempts}); trying again.", ex);
                Thread.Sleep(Random.Shared.Next(5, 20) * attempt);
            }
        }
    }

    protected override IDisposable AcquireLock(string resource, TimeSpan timeout) => connection.AcquireDistributedLock(resource, timeout);

    protected override void Apply(IReadOnlyList<StorageOperation> operations)
    {
        if (operations.Count > 0)
        {
            Apply(storage, operations);
        }
    }

    protected override void Committed()
    {
        if (QueuesWritten().Any())
        {
            storage.Signal();
        }
    }
}

internal sealed class EfFetchedJob(EntityFrameworkCoreStorage storage, long entryId, long jobId, string queue, string token)
    : FetchedJobBase(StorageValues.FormatJobId(jobId), queue, storage.Options.InvisibilityTimeout / 5)
{
    public long EntryId { get; } = entryId;

    public string Token { get; } = token;

    protected override void Remove()
    {
        using DbContext context = storage.CreateContext();
        Owned(context).ExecuteDelete();
    }

    protected override void ReturnToQueue()
    {
        using DbContext context = storage.CreateContext();
        Owned(context).ExecuteUpdate(s => s.SetProperty(x => x.FetchedAt, (DateTime?)null).SetProperty(x => x.FetchToken, (string?)null));
        storage.Signal();
    }

    protected override void KeepAlive()
    {
        DateTime now = JobClock.UtcNow;
        using DbContext context = storage.CreateContext();
        Owned(context).ExecuteUpdate(s => s.SetProperty(x => x.FetchedAt, now));
    }

    private IQueryable<JobQueueRow> Owned(DbContext context) => context.Set<JobQueueRow>().Where(x => x.Id == EntryId && x.FetchToken == Token);
}

internal sealed class EfMonitoringApi(EntityFrameworkCoreStorage storage) : SharedMonitoringApi
{
    protected override long CountJobs(string stateName) => Read(context => context.Set<JobRow>().LongCount(x => x.StateName == stateName));

    protected override IReadOnlyList<StoredJob> GetJobs(string stateName, int from, int count, bool descending)
    {
        if (count <= 0)
        {
            return [];
        }

        return Read(context =>
        {
            IQueryable<JobRow> jobs = context.Set<JobRow>().AsNoTracking().Where(x => x.StateName == stateName);
            jobs = descending ? jobs.OrderByDescending(static x => x.Id) : jobs.OrderBy(static x => x.Id);
            return jobs.Skip(from).Take(count).ToList().Select(EfRows.ToJob).ToList();
        });
    }

    protected override IReadOnlyList<StoredJob> GetJobs(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        long[] keys = [.. ids];
        return Read(context => context.Set<JobRow>().AsNoTracking().Where(x => keys.Contains(x.Id)).ToList().Select(EfRows.ToJob).ToList());
    }

    protected override StoredJob? GetJobWithHistory(long id)
    {
        return Read(context =>
        {
            JobRow? row = context.Set<JobRow>().AsNoTracking().Include(static x => x.Parameters).FirstOrDefault(x => x.Id == id);
            if (row == null)
            {
                return null;
            }

            StoredJob job = EfRows.ToJob(row);
            job.History = [.. context.Set<StateRow>().AsNoTracking()
                .Where(x => x.JobId == id)
                .OrderByDescending(static x => x.Id)
                .ToList()
                .Select(static x => new StoredState(x.Name, x.Reason, x.CreatedAt, x.Data))];
            return job;
        });
    }

    protected override IReadOnlyList<string> GetQueues() => Read(context => context.Set<JobQueueRow>().Select(static x => x.Queue).Distinct().ToList());

    protected override IReadOnlyList<long> GetEnqueuedJobIds(string queue, int from, int count) => QueueJobIds(queue, fetched: false, from, count);

    protected override IReadOnlyList<long> GetFetchedJobIds(string queue, int from, int count) => QueueJobIds(queue, fetched: true, from, count);

    protected override (long Enqueued, long Fetched) GetQueueCounts(string queue)
    {
        return Read(context => (
            context.Set<JobQueueRow>().LongCount(x => x.Queue == queue && x.FetchedAt == null),
            context.Set<JobQueueRow>().LongCount(x => x.Queue == queue && x.FetchedAt != null)));
    }

    protected override IReadOnlyList<ServerDto> GetServers()
    {
        return Read(context => context.Set<ServerRow>().AsNoTracking().ToList().Select(static x =>
        {
            StoredServerData data = StorageValues.DeserializeServer(x.Data);
            return new ServerDto
            {
                Name = x.Id,
                Heartbeat = x.LastHeartbeat,
                Queues = data.Queues ?? [],
                StartedAt = data.StartedAt ?? DateTime.MinValue,
                WorkersCount = data.WorkerCount,
            };
        }).ToList());
    }

    protected override IReadOnlyDictionary<string, long> GetCounters(IReadOnlyCollection<string> keys) => Read(context => EfRows.ReadCounters(context, keys));

    protected override long GetSetCount(string key) => Read(context => context.Set<SetRow>().LongCount(x => x.Key == key));

    private List<long> QueueJobIds(string queue, bool fetched, int from, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        return Read(context => context.Set<JobQueueRow>()
            .Where(x => x.Queue == queue && (x.FetchedAt != null) == fetched)
            .OrderBy(static x => x.Id)
            .Skip(from)
            .Take(count)
            .Select(static x => x.JobId)
            .ToList());
    }

    private T Read<T>(Func<DbContext, T> read)
    {
        using DbContext context = storage.CreateContext();
        return read(context);
    }
}

/// <summary>Deletes expired rows, jobs with their states and parameters, in batches.</summary>
internal sealed class EfExpirationManager(EntityFrameworkCoreStorage storage) : IBackgroundProcess
{
    private const int BatchSize = 1000;
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(EfExpirationManager));

    public void Execute(BackgroundProcessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            RemoveExpired(context.StoppingToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or DbException)
        {
            Logger.ErrorException("Couldn't delete the expired rows of the Entity Framework Core storage.", ex);
        }

        context.Wait(storage.Options.JobExpirationCheckInterval);
    }

    public override string ToString() => "Entity Framework Core expiration manager";

    internal void RemoveExpired(CancellationToken cancellationToken)
    {
        DateTime now = JobClock.UtcNow;
        using DbContext db = storage.CreateContext();

        while (!cancellationToken.IsCancellationRequested)
        {
            List<long> ids = [.. db.Set<JobRow>().Where(x => x.ExpireAt < now).OrderBy(static x => x.Id).Select(static x => x.Id).Take(BatchSize)];
            if (ids.Count == 0)
            {
                break;
            }

            db.Set<JobQueueRow>().Where(x => ids.Contains(x.JobId)).ExecuteDelete();
            db.Set<StateRow>().Where(x => ids.Contains(x.JobId)).ExecuteDelete();
            db.Set<JobParameterRow>().Where(x => ids.Contains(x.JobId)).ExecuteDelete();
            db.Set<JobRow>().Where(x => ids.Contains(x.Id)).ExecuteDelete();
        }

        RemoveInBatches(db.Set<SetRow>().Where(x => x.ExpireAt < now).OrderBy(static x => x.ExpireAt), cancellationToken);
        RemoveInBatches(db.Set<ListRow>().Where(x => x.ExpireAt < now).OrderBy(static x => x.ExpireAt), cancellationToken);
        RemoveInBatches(db.Set<HashRow>().Where(x => x.ExpireAt < now).OrderBy(static x => x.ExpireAt), cancellationToken);
        RemoveInBatches(db.Set<AggregatedCounterRow>().Where(x => x.ExpireAt < now).OrderBy(static x => x.ExpireAt), cancellationToken);
    }

    // A batch per statement, so a large backlog never holds its locks, or grows the log, in one statement
    private static void RemoveInBatches<TRow>(IQueryable<TRow> expired, CancellationToken cancellationToken)
        where TRow : class
    {
        while (!cancellationToken.IsCancellationRequested && expired.Take(BatchSize).ExecuteDelete() == BatchSize)
        {
        }
    }
}

/// <summary>
/// Merges counter increments into one row per counter, so state changes only ever insert increments and never
/// wait for each other on a counter. One server merges at a time, under a distributed lock.
/// </summary>
internal sealed class EfCountersAggregator(EntityFrameworkCoreStorage storage) : IBackgroundProcess
{
    private const int BatchSize = 1000;
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(EfCountersAggregator));

    public void Execute(BackgroundProcessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            using IStorageConnection connection = storage.GetConnection();
            using (connection.AcquireDistributedLock("counters:aggregator", TimeSpan.Zero))
            {
                while (AggregateBatch() == BatchSize && !context.IsStopping)
                {
                }
            }
        }
        catch (DistributedLockTimeoutException)
        {
            // Another server is merging
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or DbException)
        {
            Logger.ErrorException("Couldn't merge the counters of the Entity Framework Core storage.", ex);
        }

        context.Wait(storage.Options.CountersAggregateInterval);
    }

    public override string ToString() => "Entity Framework Core counters aggregator";

    /// <summary>Merges up to a batch of increments; returns how many.</summary>
    internal int AggregateBatch()
    {
        int merged = 0;

        storage.InTransaction(context =>
        {
            List<CounterRow> increments = [.. context.Set<CounterRow>().AsNoTracking().OrderBy(static x => x.Id).Take(BatchSize)];
            merged = increments.Count;
            if (merged == 0)
            {
                return;
            }

            long[] ids = [.. increments.Select(static x => x.Id)];
            if (context.Set<CounterRow>().Where(x => ids.Contains(x.Id)).ExecuteDelete() != ids.Length)
            {
                throw new InvalidOperationException("Counter increments were merged by another process at the same time.");
            }

            // Every merged value of the batch in one query. A key the database matches under another case (a
            // case-insensitive collation) isn't in the dictionary: Find asks the database for it.
            string[] keys = [.. increments.Select(static x => x.Key).Distinct(StringComparer.Ordinal)];
            Dictionary<string, AggregatedCounterRow> existing = context.Set<AggregatedCounterRow>()
                .Where(x => keys.Contains(x.Key))
                .ToDictionary(static x => x.Key, StringComparer.Ordinal);

            foreach (IGrouping<string, CounterRow> counter in increments.GroupBy(static x => x.Key, StringComparer.Ordinal))
            {
                AggregatedCounterRow? row = existing.GetValueOrDefault(counter.Key) ?? context.Set<AggregatedCounterRow>().Find(counter.Key);
                if (row == null)
                {
                    context.Add(row = new AggregatedCounterRow { Key = counter.Key });
                }

                row.Value += counter.Sum(static x => x.Value);
                DateTime? expireAt = counter.Max(static x => x.ExpireAt);
                if (expireAt.HasValue && (row.ExpireAt == null || row.ExpireAt < expireAt))
                {
                    row.ExpireAt = expireAt;
                }
            }
        });

        return merged;
    }
}
