using Masterly.NightShift.Logging;
using Masterly.NightShift.States;

namespace Masterly.NightShift.Storage.Shared;

/// <summary>The kinds of keyed collections a transaction can expire or persist.</summary>
internal enum KeyKind
{
    Set,
    List,
    Hash,
}

/// <summary>One write of a transaction, recorded until <see cref="RecordingTransaction.Commit"/>.</summary>
internal abstract record StorageOperation;

/// <summary>Sets when a job expires; <c>null</c> keeps it.</summary>
internal sealed record ExpireJobOperation(long JobId, DateTime? ExpireAt) : StorageOperation;

/// <summary>Adds a state to a job's history and, when <see cref="SetCurrent"/>, makes it the job's state.</summary>
internal sealed record AddJobStateOperation(long JobId, StoredState State, bool SetCurrent) : StorageOperation;

internal sealed record AddToQueueOperation(string Queue, long JobId) : StorageOperation;

internal sealed record RemoveFromQueueOperation(FetchedJobBase FetchedJob) : StorageOperation;

internal sealed record SetJobParameterOperation(long JobId, string Name, string? Value) : StorageOperation;

internal sealed record CounterOperation(string Key, long Value, DateTime? ExpireAt) : StorageOperation;

internal sealed record AddToSetOperation(string Key, string Value, double Score) : StorageOperation;

internal sealed record AddRangeToSetOperation(string Key, IReadOnlyList<string> Values) : StorageOperation;

internal sealed record RemoveFromSetOperation(string Key, string Value) : StorageOperation;

internal sealed record RemoveSetOperation(string Key) : StorageOperation;

internal sealed record InsertToListOperation(string Key, string Value) : StorageOperation;

internal sealed record RemoveFromListOperation(string Key, string Value) : StorageOperation;

internal sealed record TrimListOperation(string Key, int KeepStartingFrom, int KeepEndingAt) : StorageOperation;

internal sealed record SetRangeInHashOperation(string Key, IReadOnlyList<KeyValuePair<string, string>> Fields) : StorageOperation;

internal sealed record RemoveHashOperation(string Key) : StorageOperation;

/// <summary>Sets when every item of a set, list or hash expires; <c>null</c> keeps them.</summary>
internal sealed record ExpireKeyOperation(KeyKind Kind, string Key, DateTime? ExpireAt) : StorageOperation;

/// <summary>
/// A write transaction that records its writes and hands them to <see cref="Apply"/> on commit, which writes
/// them all or none. Locks taken through <see cref="AcquireDistributedLock"/> are held until the transaction
/// is disposed. A queue entry removed in a committed transaction isn't put back when its fetched job is disposed.
/// </summary>
internal abstract class RecordingTransaction : JobStorageTransaction
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(RecordingTransaction));

    private readonly List<StorageOperation> _operations = [];
    private readonly List<IDisposable> _locks = [];
    private bool _committed;

    public override void ExpireJob(string jobId, TimeSpan expireIn)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        if (TryParseJobId(jobId, out long id))
        {
            _operations.Add(new ExpireJobOperation(id, StorageValues.ExpireAt(JobClock.UtcNow, expireIn)));
        }
    }

    public override void PersistJob(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        if (TryParseJobId(jobId, out long id))
        {
            _operations.Add(new ExpireJobOperation(id, null));
        }
    }

    public override void SetJobState(string jobId, IState state) => AddState(jobId, state, setCurrent: true);

    public override void AddJobState(string jobId, IState state) => AddState(jobId, state, setCurrent: false);

    public override void AddToQueue(string queue, string jobId)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(jobId);
        if (TryParseJobId(jobId, out long id))
        {
            _operations.Add(new AddToQueueOperation(queue, id));
        }
    }

    public override void RemoveFromQueue(IFetchedJob fetchedJob)
    {
        ArgumentNullException.ThrowIfNull(fetchedJob);
        if (fetchedJob is not FetchedJobBase ours || !IsOwnFetchedJob(ours))
        {
            throw new NotSupportedException($"Only the jobs this storage fetched can be removed in a transaction, not a {fetchedJob.GetType().Name}.");
        }

        _operations.Add(new RemoveFromQueueOperation(ours));
    }

    public override void SetJobParameter(string jobId, string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(name);
        if (TryParseJobId(jobId, out long id))
        {
            _operations.Add(new SetJobParameterOperation(id, name, value));
        }
    }

    public override void IncrementCounter(string key) => AddCounter(key, 1, null);

    public override void IncrementCounter(string key, TimeSpan expireIn) => AddCounter(key, 1, expireIn);

    public override void DecrementCounter(string key) => AddCounter(key, -1, null);

    public override void DecrementCounter(string key, TimeSpan expireIn) => AddCounter(key, -1, expireIn);

    public override void AddToSet(string key, string value) => AddToSet(key, value, 0.0);

    public override void AddToSet(string key, string value, double score)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _operations.Add(new AddToSetOperation(key, value, score));
    }

    public override void AddRangeToSet(string key, IList<string> items)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(items);
        foreach (string item in items)
        {
            if (item == null)
            {
                throw new ArgumentException("The items can't contain null.", nameof(items));
            }
        }

        _operations.Add(new AddRangeToSetOperation(key, [.. items]));
    }

    public override void RemoveFromSet(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _operations.Add(new RemoveFromSetOperation(key, value));
    }

    public override void RemoveSet(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _operations.Add(new RemoveSetOperation(key));
    }

    public override void InsertToList(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _operations.Add(new InsertToListOperation(key, value));
    }

    public override void RemoveFromList(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _operations.Add(new RemoveFromListOperation(key, value));
    }

    public override void TrimList(string key, int keepStartingFrom, int keepEndingAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        _operations.Add(new TrimListOperation(key, keepStartingFrom, keepEndingAt));
    }

    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(keyValuePairs);
        _operations.Add(new SetRangeInHashOperation(key, [.. keyValuePairs]));
    }

    public override void RemoveHash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _operations.Add(new RemoveHashOperation(key));
    }

    public override void ExpireSet(string key, TimeSpan expireIn) => ExpireKey(KeyKind.Set, key, expireIn);

    public override void ExpireList(string key, TimeSpan expireIn) => ExpireKey(KeyKind.List, key, expireIn);

    public override void ExpireHash(string key, TimeSpan expireIn) => ExpireKey(KeyKind.Hash, key, expireIn);

    public override void PersistSet(string key) => ExpireKey(KeyKind.Set, key, null);

    public override void PersistList(string key) => ExpireKey(KeyKind.List, key, null);

    public override void PersistHash(string key) => ExpireKey(KeyKind.Hash, key, null);

    public override void AcquireDistributedLock(string resource, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _locks.Add(AcquireLock(resource, timeout));
    }

    public sealed override void Commit()
    {
        if (_committed)
        {
            throw new InvalidOperationException("The transaction is already committed.");
        }

        try
        {
            Apply(_operations);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The caller decides what the failure means; this only tells what was being written
            Logger.DebugException($"Couldn't commit a transaction of {_operations.Count} operations in {GetType().Name}.", ex);
            throw;
        }

        _committed = true;

        foreach (StorageOperation operation in _operations)
        {
            if (operation is RemoveFromQueueOperation removal)
            {
                removal.FetchedJob.MarkRemoved();
            }
        }

        Committed();
    }

    public override void Dispose()
    {
        // Every lock is released even when another fails to: a lease left behind runs out by itself
        for (int i = _locks.Count - 1; i >= 0; i--)
        {
            try
            {
                _locks[i].Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Logger.WarnException("Couldn't release a lock taken in a transaction.", ex);
            }
        }

        _locks.Clear();
        base.Dispose();
    }

    /// <summary>Writes every operation, all or none.</summary>
    protected abstract void Apply(IReadOnlyList<StorageOperation> operations);

    /// <summary>
    /// Whether <paramref name="fetchedJob"/> was fetched by this storage, so <see cref="Apply"/> can act on it.
    /// <see cref="RemoveFromQueue"/> refuses the others before anything is recorded, so a commit never fails
    /// half way on a job another storage fetched.
    /// </summary>
    protected virtual bool IsOwnFetchedJob(FetchedJobBase fetchedJob) => true;

    /// <summary>Takes a lock held until the transaction is disposed.</summary>
    protected abstract IDisposable AcquireLock(string resource, TimeSpan timeout);

    /// <summary>Runs after a commit, for example to wake the workers waiting for the queues written to.</summary>
    protected virtual void Committed()
    {
    }

    /// <summary>The queues the committed operations added jobs to.</summary>
    protected IEnumerable<string> QueuesWritten()
    {
        return _operations.OfType<AddToQueueOperation>().Select(static x => x.Queue).Distinct(StringComparer.Ordinal);
    }

    private static bool TryParseJobId(string jobId, out long id)
    {
        if (StorageValues.TryParseJobId(jobId, out id))
        {
            return true;
        }

        // Ids are numbers, so this one names no job and the write does nothing, as on SQL Server
        Logger.Debug($"Ignored a write to job '{jobId}': this storage's job ids are numbers.");
        return false;
    }

    private void AddState(string jobId, IState state, bool setCurrent)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(state);
        if (TryParseJobId(jobId, out long id))
        {
            _operations.Add(new AddJobStateOperation(id, StoredState.From(state), setCurrent));
        }
    }

    private void AddCounter(string key, long value, TimeSpan? expireIn)
    {
        ArgumentNullException.ThrowIfNull(key);
        _operations.Add(new CounterOperation(key, value, expireIn.HasValue ? StorageValues.ExpireAt(JobClock.UtcNow, expireIn.Value) : null));
    }

    private void ExpireKey(KeyKind kind, string key, TimeSpan? expireIn)
    {
        ArgumentNullException.ThrowIfNull(key);
        _operations.Add(new ExpireKeyOperation(kind, key, expireIn.HasValue ? StorageValues.ExpireAt(JobClock.UtcNow, expireIn.Value) : null));
    }
}
