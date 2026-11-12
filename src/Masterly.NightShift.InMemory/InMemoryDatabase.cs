using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage.Monitoring;
using Masterly.NightShift.Storage.Shared;

namespace Masterly.NightShift.InMemory;

/// <summary>
/// Everything the in-memory storage holds, behind one lock. Reads return copies, so a reader never sees a
/// transaction half applied, and a transaction is applied as a whole under the lock.
/// </summary>
internal sealed class InMemoryDatabase(InMemoryStorageOptions options) : ILeaseStore
{
    // At most this many expired jobs are removed under the lock at once; a sweep that hits it sweeps again soon
    internal const int MaxJobsPerSweep = 1000;

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(InMemoryDatabase));

    private readonly NightShiftLock _lock = new();
    private readonly Dictionary<long, JobEntry> _jobs = [];
    private readonly Dictionary<string, SortedSet<long>> _jobsByState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SetEntry> _sets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ListEntry> _lists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashEntry> _hashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CounterEntry> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<long, QueueEntry>> _queues = new(StringComparer.Ordinal);

    // Where each job is queued, so removing an expired job doesn't scan every queue
    private readonly Dictionary<long, List<(string Queue, long EntryId)>> _queueEntriesByJob = [];
    private readonly Dictionary<string, ServerEntry> _servers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Owner, DateTime ExpireAt)> _leases = new(StringComparer.Ordinal);
    private TaskCompletionSource _enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextJobId;
    private long _nextQueueEntryId;
    private DateTime _nextSweep = DateTime.MinValue;

    // Jobs

    public long CreateJob(Job job, IDictionary<string, string?> parameters, DateTime createdAt, TimeSpan expireIn)
    {
        lock (_lock)
        {
            SweepIfDue();
            long id = ++_nextJobId;
            _jobs[id] = new JobEntry(StoredJob.Create(id, job, parameters, createdAt, CapExpiration(StorageValues.ExpireAt(createdAt, expireIn))));
            return id;
        }
    }

    public StoredJob? GetJob(long id, bool withHistory)
    {
        lock (_lock)
        {
            return _jobs.TryGetValue(id, out JobEntry? entry) ? entry.Copy(withHistory) : null;
        }
    }

    public string? GetJobParameter(long id, string name)
    {
        lock (_lock)
        {
            return _jobs.TryGetValue(id, out JobEntry? entry) && entry.Job.Parameters.TryGetValue(name, out string? value) ? value : null;
        }
    }

    public void SetJobParameter(long id, string name, string? value)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(id, out JobEntry? entry))
            {
                entry.Job.Parameters[name] = value;
            }
        }
    }

    // Sets

    public HashSet<string> GetAllItemsFromSet(string key)
    {
        lock (_lock)
        {
            return _sets.TryGetValue(key, out SetEntry? set) ? [.. set.Scores.Keys] : [];
        }
    }

    public List<string> GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore, int count)
    {
        lock (_lock)
        {
            if (!_sets.TryGetValue(key, out SetEntry? set)) return [];

            return [.. set.Ordered
                .GetViewBetween((fromScore, string.Empty), (toScore, SetEntry.AfterEveryValue))
                .Take(count)
                .Select(static x => x.Value)];
        }
    }

    public long GetSetCount(string key)
    {
        lock (_lock)
        {
            return _sets.TryGetValue(key, out SetEntry? set) ? set.Scores.Count : 0;
        }
    }

    public long GetSetCount(IEnumerable<string> keys, int limit)
    {
        lock (_lock)
        {
            long total = 0;
            foreach (string key in keys.Distinct(StringComparer.Ordinal))
            {
                if (_sets.TryGetValue(key, out SetEntry? set)) total += set.Scores.Count;
                if (total >= limit) return limit;
            }

            return total;
        }
    }

    public bool GetSetContains(string key, string value)
    {
        lock (_lock)
        {
            return _sets.TryGetValue(key, out SetEntry? set) && set.Scores.ContainsKey(value);
        }
    }

    public List<string> GetRangeFromSet(string key, int startingFrom, int endingAt)
    {
        lock (_lock)
        {
            if (!_sets.TryGetValue(key, out SetEntry? set)) return [];
            (int skip, int take) = Range(startingFrom, endingAt);
            return [.. set.Ordered.Skip(skip).Take(take).Select(static x => x.Value)];
        }
    }

    public TimeSpan GetSetTtl(string key)
    {
        lock (_lock)
        {
            return StorageValues.TimeToLive(_sets.TryGetValue(key, out SetEntry? set) ? set.ExpireAt : null);
        }
    }

    // Hashes

    public void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> fields)
    {
        lock (_lock)
        {
            WriteHash(key, fields);
        }
    }

    public Dictionary<string, string>? GetAllEntriesFromHash(string key)
    {
        lock (_lock)
        {
            return _hashes.TryGetValue(key, out HashEntry? hash) ? new Dictionary<string, string>(hash.Fields, StringComparer.Ordinal) : null;
        }
    }

    public string? GetValueFromHash(string key, string name)
    {
        lock (_lock)
        {
            return _hashes.TryGetValue(key, out HashEntry? hash) && hash.Fields.TryGetValue(name, out string? value) ? value : null;
        }
    }

    public long GetHashCount(string key)
    {
        lock (_lock)
        {
            return _hashes.TryGetValue(key, out HashEntry? hash) ? hash.Fields.Count : 0;
        }
    }

    public TimeSpan GetHashTtl(string key)
    {
        lock (_lock)
        {
            return StorageValues.TimeToLive(_hashes.TryGetValue(key, out HashEntry? hash) ? hash.ExpireAt : null);
        }
    }

    // Lists

    public long GetListCount(string key)
    {
        lock (_lock)
        {
            return _lists.TryGetValue(key, out ListEntry? list) ? list.Items.Count : 0;
        }
    }

    public List<string> GetAllItemsFromList(string key)
    {
        lock (_lock)
        {
            return _lists.TryGetValue(key, out ListEntry? list) ? [.. list.Items] : [];
        }
    }

    public List<string> GetRangeFromList(string key, int startingFrom, int endingAt)
    {
        lock (_lock)
        {
            if (!_lists.TryGetValue(key, out ListEntry? list)) return [];
            (int skip, int take) = Range(startingFrom, endingAt);
            return [.. list.Items.Skip(skip).Take(take)];
        }
    }

    public TimeSpan GetListTtl(string key)
    {
        lock (_lock)
        {
            return StorageValues.TimeToLive(_lists.TryGetValue(key, out ListEntry? list) ? list.ExpireAt : null);
        }
    }

    // Counters

    public long GetCounter(string key)
    {
        lock (_lock)
        {
            return _counters.TryGetValue(key, out CounterEntry? counter) ? counter.Value : 0;
        }
    }

    public Dictionary<string, long> GetCounters(IEnumerable<string> keys)
    {
        lock (_lock)
        {
            Dictionary<string, long> result = new(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                if (_counters.TryGetValue(key, out CounterEntry? counter)) result[key] = counter.Value;
            }

            return result;
        }
    }

    // Servers

    public void AnnounceServer(string serverId, string data)
    {
        lock (_lock)
        {
            _servers[serverId] = new ServerEntry(data, JobClock.UtcNow);
        }
    }

    public void RemoveServer(string serverId)
    {
        lock (_lock)
        {
            _servers.Remove(serverId);
        }
    }

    public bool Heartbeat(string serverId)
    {
        lock (_lock)
        {
            if (!_servers.TryGetValue(serverId, out ServerEntry? server)) return false;
            _servers[serverId] = server with { LastHeartbeat = JobClock.UtcNow };
            return true;
        }
    }

    public int RemoveTimedOutServers(TimeSpan timeOut)
    {
        lock (_lock)
        {
            DateTime cutoff = JobClock.UtcNow - timeOut;
            List<string> timedOut = [.. _servers.Where(x => x.Value.LastHeartbeat < cutoff).Select(static x => x.Key)];
            timedOut.ForEach(x => _servers.Remove(x));
            return timedOut.Count;
        }
    }

    public List<ServerDto> GetServers()
    {
        lock (_lock)
        {
            return [.. _servers.Select(static x =>
            {
                StoredServerData data = StorageValues.DeserializeServer(x.Value.Data);
                return new ServerDto
                {
                    Name = x.Key,
                    Heartbeat = x.Value.LastHeartbeat,
                    Queues = data.Queues ?? [],
                    StartedAt = data.StartedAt ?? DateTime.MinValue,
                    WorkersCount = data.WorkerCount,
                };
            })];
        }
    }

    // Queues

    /// <summary>
    /// Takes the first visible entry of the first queue that has one: an entry nobody fetched, or one whose
    /// fetcher stopped keeping it alive longer than the invisibility timeout ago. Waits for an enqueue otherwise.
    /// </summary>
    public (long EntryId, long JobId, string Queue, string Token) Fetch(string[] queues, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task enqueued;

            lock (_lock)
            {
                SweepIfDue();
                DateTime now = JobClock.UtcNow;
                DateTime staleBefore = now - options.InvisibilityTimeout;

                foreach (string queue in queues)
                {
                    if (!_queues.TryGetValue(queue, out SortedDictionary<long, QueueEntry>? entries)) continue;

                    foreach (QueueEntry entry in entries.Values)
                    {
                        if (entry.FetchedAt == null || entry.FetchedAt < staleBefore)
                        {
                            entry.FetchedAt = now;
                            entry.Token = Guid.NewGuid().ToString("N");
                            return (entry.Id, entry.JobId, queue, entry.Token);
                        }
                    }
                }

                enqueued = _enqueued.Task;
            }

            // The task only ever completes successfully, so this returns or throws OperationCanceledException
            enqueued.Wait((int)options.QueuePollInterval.TotalMilliseconds, cancellationToken);
        }
    }

    /// <summary>Acts on a fetched entry, if the fetch that took it still owns it.</summary>
    public void UpdateFetched(string queue, long entryId, string token, FetchedAction action)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(queue, out SortedDictionary<long, QueueEntry>? entries)
                || !entries.TryGetValue(entryId, out QueueEntry? entry)
                || entry.Token != token)
            {
                return;
            }

            switch (action)
            {
                case FetchedAction.Remove:
                    RemoveQueueEntry(queue, entries, entryId);
                    break;
                case FetchedAction.Requeue:
                    entry.FetchedAt = null;
                    entry.Token = null;
                    Signal();
                    break;
                case FetchedAction.KeepAlive:
                    entry.FetchedAt = JobClock.UtcNow;
                    break;
            }
        }
    }

    public List<string> GetQueues()
    {
        lock (_lock)
        {
            return [.. _queues.Keys];
        }
    }

    public List<long> GetQueueJobIds(string queue, bool fetched, int from, int count)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(queue, out SortedDictionary<long, QueueEntry>? entries)) return [];
            return [.. entries.Values.Where(x => (x.FetchedAt != null) == fetched).Skip(from).Take(count).Select(static x => x.JobId)];
        }
    }

    public (long Enqueued, long Fetched) GetQueueCounts(string queue)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(queue, out SortedDictionary<long, QueueEntry>? entries)) return (0, 0);
            long fetched = entries.Values.Count(static x => x.FetchedAt != null);
            return (entries.Count - fetched, fetched);
        }
    }

    // Monitoring

    public long CountJobs(string stateName)
    {
        lock (_lock)
        {
            return _jobsByState.TryGetValue(stateName, out SortedSet<long>? ids) ? ids.Count : 0;
        }
    }

    public List<StoredJob> GetJobs(string stateName, int from, int count, bool descending)
    {
        lock (_lock)
        {
            if (!_jobsByState.TryGetValue(stateName, out SortedSet<long>? ids)) return [];
            IEnumerable<long> ordered = descending ? ids.Reverse() : ids;
            return [.. ordered.Skip(from).Take(count).Select(id => _jobs[id].Copy(withHistory: false))];
        }
    }

    public List<StoredJob> GetJobs(IEnumerable<long> ids)
    {
        lock (_lock)
        {
            return [.. ids.Where(_jobs.ContainsKey).Select(id => _jobs[id].Copy(withHistory: false))];
        }
    }

    // Transactions

    /// <summary>Applies every operation, or none: an operation this storage can't apply is refused before any is.</summary>
    /// <exception cref="NotSupportedException">An operation is unknown, or removes a job another storage fetched.</exception>
    public void Apply(IReadOnlyList<StorageOperation> operations)
    {
        foreach (StorageOperation operation in operations)
        {
            Validate(operation);
        }

        lock (_lock)
        {
            SweepIfDue();
            bool enqueued = false;

            foreach (StorageOperation operation in operations)
            {
                enqueued |= operation is AddToQueueOperation;
                ApplyOne(operation);
            }

            if (enqueued) Signal();
        }
    }

    public long Purge(StoragePurgeOptions purge)
    {
        lock (_lock)
        {
            long removed = _jobs.Count + _queues.Values.Sum(static x => x.Count) + _counters.Count;
            _jobs.Clear();
            _jobsByState.Clear();
            _queues.Clear();
            _queueEntriesByJob.Clear();
            _counters.Clear();
            removed += RemoveWhere(_sets, key => !StorageRules.PurgeKeepsSet(key, purge));
            removed += RemoveWhere(_lists, key => !StorageRules.PurgeKeepsList(key, purge));
            removed += RemoveWhere(_hashes, key => !StorageRules.PurgeKeepsHash(key, purge));

            if (!purge.KeepServers)
            {
                removed += _servers.Count;
                _servers.Clear();
            }

            return removed;
        }
    }

    /// <summary>
    /// Removes everything whose time has come: jobs with their queue entries (at most <see cref="MaxJobsPerSweep"/>
    /// at once), sets, lists, hashes and counters.
    /// </summary>
    public void RemoveExpired()
    {
        lock (_lock)
        {
            DateTime now = JobClock.UtcNow;
            List<long> expired = [.. _jobs.Where(x => x.Value.Job.ExpireAt <= now).Select(static x => x.Key).Take(MaxJobsPerSweep)];

            // When more jobs are left, the next call sweeps again instead of waiting a whole interval
            _nextSweep = expired.Count == MaxJobsPerSweep ? now : now + options.ExpirationCheckInterval;

            foreach (long id in expired)
            {
                RemoveJob(id);
            }

            RemoveWhere(_sets, key => _sets[key].ExpireAt <= now);
            RemoveWhere(_lists, key => _lists[key].ExpireAt <= now);
            RemoveWhere(_hashes, key => _hashes[key].ExpireAt <= now);
            RemoveWhere(_counters, key => _counters[key].ExpireAt <= now);
        }
    }

    // Leases (distributed locks)

    public bool TryTake(string resource, string owner, DateTime expireAt)
    {
        lock (_lock)
        {
            if (_leases.TryGetValue(resource, out (string Owner, DateTime ExpireAt) lease) && lease.ExpireAt > JobClock.UtcNow)
            {
                return false;
            }

            _leases[resource] = (owner, expireAt);
            return true;
        }
    }

    public bool TryRenew(string resource, string owner, DateTime expireAt)
    {
        lock (_lock)
        {
            if (!_leases.TryGetValue(resource, out (string Owner, DateTime ExpireAt) lease) || lease.Owner != owner) return false;
            _leases[resource] = (owner, expireAt);
            return true;
        }
    }

    public void Release(string resource, string owner)
    {
        lock (_lock)
        {
            if (_leases.TryGetValue(resource, out (string Owner, DateTime ExpireAt) lease) && lease.Owner == owner)
            {
                _leases.Remove(resource);
            }
        }
    }

    private void ApplyOne(StorageOperation operation)
    {
        switch (operation)
        {
            case ExpireJobOperation expire:
                if (FindJob(expire.JobId, "the expiration") is { } expiring)
                {
                    expiring.Job.ExpireAt = expire.ExpireAt.HasValue ? CapExpiration(expire.ExpireAt.Value) : null;
                }

                break;

            case AddJobStateOperation add:
                if (FindJob(add.JobId, "a state") is { } job)
                {
                    job.History.Add(add.State);
                    if (add.SetCurrent)
                    {
                        if (job.Job.StateName != null) Unindex(job.Job.StateName, job.Job.Id);
                        job.Job.StateName = add.State.Name;
                        job.Job.State = add.State;
                        Index(add.State.Name, job.Job.Id);
                    }
                }

                break;

            case AddToQueueOperation queued:
                if (!_queues.TryGetValue(queued.Queue, out SortedDictionary<long, QueueEntry>? entries))
                {
                    _queues[queued.Queue] = entries = [];
                }

                long entryId = ++_nextQueueEntryId;
                entries[entryId] = new QueueEntry(entryId, queued.JobId);
                if (!_queueEntriesByJob.TryGetValue(queued.JobId, out List<(string Queue, long EntryId)>? queuedAt))
                {
                    _queueEntriesByJob[queued.JobId] = queuedAt = [];
                }

                queuedAt.Add((queued.Queue, entryId));
                break;

            case RemoveFromQueueOperation removal:
                InMemoryFetchedJob fetched = (InMemoryFetchedJob)removal.FetchedJob; // Checked by Validate before the lock
                if (_queues.TryGetValue(fetched.Queue, out SortedDictionary<long, QueueEntry>? fromQueue)
                    && fromQueue.TryGetValue(fetched.EntryId, out QueueEntry? owned)
                    && owned.Token == fetched.Token)
                {
                    RemoveQueueEntry(fetched.Queue, fromQueue, fetched.EntryId);
                }

                break;

            case SetJobParameterOperation parameter:
                if (FindJob(parameter.JobId, "a parameter") is { } withParameter)
                {
                    withParameter.Job.Parameters[parameter.Name] = parameter.Value;
                }

                break;

            case CounterOperation counter:
                if (!_counters.TryGetValue(counter.Key, out CounterEntry? existing))
                {
                    _counters[counter.Key] = existing = new CounterEntry();
                }

                existing.Value += counter.Value;
                if (counter.ExpireAt.HasValue && (existing.ExpireAt == null || existing.ExpireAt < counter.ExpireAt))
                {
                    existing.ExpireAt = CapExpiration(counter.ExpireAt.Value);
                }

                break;

            case AddToSetOperation add:
                GetOrAddSet(add.Key).Add(add.Value, add.Score);
                break;

            case AddRangeToSetOperation range:
                SetEntry rangeSet = GetOrAddSet(range.Key);
                foreach (string value in range.Values) rangeSet.Add(value, 0.0);
                break;

            case RemoveFromSetOperation remove:
                if (_sets.TryGetValue(remove.Key, out SetEntry? fromSet) && fromSet.Remove(remove.Value) && fromSet.Scores.Count == 0)
                {
                    _sets.Remove(remove.Key);
                }

                break;

            case RemoveSetOperation removeSet:
                _sets.Remove(removeSet.Key);
                break;

            case InsertToListOperation insert:
                if (!_lists.TryGetValue(insert.Key, out ListEntry? list))
                {
                    _lists[insert.Key] = list = new ListEntry();
                }

                list.Items.Insert(0, insert.Value);
                break;

            case RemoveFromListOperation removeFromList:
                if (_lists.TryGetValue(removeFromList.Key, out ListEntry? fromList))
                {
                    fromList.Items.RemoveAll(x => x == removeFromList.Value);
                    if (fromList.Items.Count == 0) _lists.Remove(removeFromList.Key);
                }

                break;

            case TrimListOperation trim:
                if (_lists.TryGetValue(trim.Key, out ListEntry? trimmed))
                {
                    List<string> kept = [.. trimmed.Items.Where((_, index) => index >= trim.KeepStartingFrom && index <= trim.KeepEndingAt)];
                    trimmed.Items.Clear();
                    trimmed.Items.AddRange(kept);
                    if (kept.Count == 0) _lists.Remove(trim.Key);
                }

                break;

            case SetRangeInHashOperation hash:
                WriteHash(hash.Key, hash.Fields);
                break;

            case RemoveHashOperation removeHash:
                _hashes.Remove(removeHash.Key);
                break;

            case ExpireKeyOperation expireKey:
                DateTime? expireAt = expireKey.ExpireAt.HasValue ? CapExpiration(expireKey.ExpireAt.Value) : null;
                switch (expireKey.Kind)
                {
                    case KeyKind.Set when _sets.TryGetValue(expireKey.Key, out SetEntry? set):
                        set.ExpireAt = expireAt;
                        break;
                    case KeyKind.List when _lists.TryGetValue(expireKey.Key, out ListEntry? expiringList):
                        expiringList.ExpireAt = expireAt;
                        break;
                    case KeyKind.Hash when _hashes.TryGetValue(expireKey.Key, out HashEntry? expiringHash):
                        expiringHash.ExpireAt = expireAt;
                        break;
                }

                break;

            default:
                throw new NotSupportedException($"Unknown storage operation {operation.GetType().Name}.");
        }
    }

    private void WriteHash(string key, IEnumerable<KeyValuePair<string, string>> fields)
    {
        if (!_hashes.TryGetValue(key, out HashEntry? hash))
        {
            hash = new HashEntry();
        }

        foreach (KeyValuePair<string, string> field in fields)
        {
            hash.Fields[field.Key] = field.Value;
        }

        if (hash.Fields.Count > 0) _hashes[key] = hash;
    }

    private SetEntry GetOrAddSet(string key)
    {
        if (!_sets.TryGetValue(key, out SetEntry? set))
        {
            _sets[key] = set = new SetEntry();
        }

        return set;
    }

    private void RemoveJob(long id)
    {
        if (!_jobs.Remove(id, out JobEntry? entry)) return;
        if (entry.Job.StateName != null) Unindex(entry.Job.StateName, id);
        if (!_queueEntriesByJob.Remove(id, out List<(string Queue, long EntryId)>? queuedAt)) return;

        foreach ((string queue, long entryId) in queuedAt)
        {
            if (_queues.TryGetValue(queue, out SortedDictionary<long, QueueEntry>? entries) && entries.Remove(entryId) && entries.Count == 0)
            {
                _queues.Remove(queue);
            }
        }
    }

    private void RemoveQueueEntry(string queue, SortedDictionary<long, QueueEntry> entries, long entryId)
    {
        if (!entries.Remove(entryId, out QueueEntry? removed)) return;
        if (entries.Count == 0) _queues.Remove(queue);

        if (_queueEntriesByJob.TryGetValue(removed.JobId, out List<(string Queue, long EntryId)>? queuedAt))
        {
            queuedAt.Remove((queue, entryId));
            if (queuedAt.Count == 0) _queueEntriesByJob.Remove(removed.JobId);
        }
    }

    private static void Validate(StorageOperation operation)
    {
        switch (operation)
        {
            case RemoveFromQueueOperation { FetchedJob: not InMemoryFetchedJob } removal:
                throw new NotSupportedException($"Only the jobs this storage fetched can be removed in a transaction, not a {removal.FetchedJob.GetType().Name}.");
            case ExpireJobOperation or AddJobStateOperation or AddToQueueOperation or RemoveFromQueueOperation or SetJobParameterOperation
                or CounterOperation or AddToSetOperation or AddRangeToSetOperation or RemoveFromSetOperation or RemoveSetOperation
                or InsertToListOperation or RemoveFromListOperation or TrimListOperation or SetRangeInHashOperation or RemoveHashOperation
                or ExpireKeyOperation:
                return;
            default:
                throw new NotSupportedException($"Unknown storage operation {operation.GetType().Name}.");
        }
    }

    /// <summary>Finds a job a write targets; a write to a job that doesn't exist (any more) does nothing, as on SQL Server.</summary>
    private JobEntry? FindJob(long id, string write)
    {
        if (_jobs.TryGetValue(id, out JobEntry? job)) return job;

        Logger.Debug($"Ignored {write} of job #{id}: the job doesn't exist, or it expired.");
        return null;
    }

    /// <summary>
    /// The items a range of positions reads, as SQL Server numbers them: from 0, newest or lowest first. Negative
    /// positions come before the first item, so they read nothing; a range that ends before it starts reads nothing.
    /// </summary>
    private static (int Skip, int Take) Range(int startingFrom, int endingAt)
    {
        int skip = Math.Max(0, startingFrom);
        long take = (long)endingAt - skip + 1;
        return (skip, (int)Math.Clamp(take, 0, int.MaxValue));
    }

    private void Index(string stateName, long id)
    {
        if (!_jobsByState.TryGetValue(stateName, out SortedSet<long>? ids))
        {
            _jobsByState[stateName] = ids = [];
        }

        ids.Add(id);
    }

    private void Unindex(string stateName, long id)
    {
        if (_jobsByState.TryGetValue(stateName, out SortedSet<long>? ids) && ids.Remove(id) && ids.Count == 0)
        {
            _jobsByState.Remove(stateName);
        }
    }

    private void Signal()
    {
        Interlocked.Exchange(ref _enqueued, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private void SweepIfDue()
    {
        if (JobClock.UtcNow >= _nextSweep) RemoveExpired();
    }

    private DateTime CapExpiration(DateTime expireAt)
    {
        if (!options.MaxExpirationTime.HasValue) return expireAt;
        DateTime cap = StorageValues.ExpireAt(JobClock.UtcNow, options.MaxExpirationTime.Value);
        return expireAt < cap ? expireAt : cap;
    }

    private static int RemoveWhere<TValue>(Dictionary<string, TValue> dictionary, Func<string, bool> predicate)
    {
        List<string> keys = [.. dictionary.Keys.Where(predicate)];
        keys.ForEach(key => dictionary.Remove(key));
        return keys.Count;
    }

    private sealed class JobEntry(StoredJob job)
    {
        public StoredJob Job { get; } = job;

        public List<StoredState> History { get; } = [];

        public StoredJob Copy(bool withHistory)
        {
            return new StoredJob
            {
                Id = Job.Id,
                InvocationData = Job.InvocationData,
                Arguments = Job.Arguments,
                CreatedAt = Job.CreatedAt,
                ExpireAt = Job.ExpireAt,
                StateName = Job.StateName,
                State = Job.State,
                Parameters = new Dictionary<string, string?>(Job.Parameters, StringComparer.Ordinal),
                History = withHistory ? [.. Enumerable.Reverse(History)] : null,
            };
        }
    }

    private sealed class SetEntry
    {
        /// <summary>
        /// The upper bound of a range of scores: the comparer orders it after every value of its score. It is found
        /// by reference, and a string built at run time is never the same instance as a value read or written.
        /// </summary>
        public static readonly string AfterEveryValue = new(char.MaxValue, 1);

        public Dictionary<string, double> Scores { get; } = new(StringComparer.Ordinal);

        public SortedSet<(double Score, string Value)> Ordered { get; } = new(Comparer<(double Score, string Value)>.Create(static (x, y) =>
        {
            int byScore = x.Score.CompareTo(y.Score);
            if (byScore != 0 || ReferenceEquals(x.Value, y.Value)) return byScore;
            if (ReferenceEquals(x.Value, AfterEveryValue)) return 1;
            if (ReferenceEquals(y.Value, AfterEveryValue)) return -1;
            return string.CompareOrdinal(x.Value, y.Value);
        }));

        public DateTime? ExpireAt { get; set; }

        public void Add(string value, double score)
        {
            if (Scores.TryGetValue(value, out double old)) Ordered.Remove((old, value));
            Scores[value] = score;
            Ordered.Add((score, value));
        }

        public bool Remove(string value)
        {
            if (!Scores.Remove(value, out double old)) return false;
            Ordered.Remove((old, value));
            return true;
        }
    }

    private sealed class ListEntry
    {
        /// <summary>Newest first.</summary>
        public List<string> Items { get; } = [];

        public DateTime? ExpireAt { get; set; }
    }

    private sealed class HashEntry
    {
        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);

        public DateTime? ExpireAt { get; set; }
    }

    private sealed class CounterEntry
    {
        public long Value { get; set; }

        public DateTime? ExpireAt { get; set; }
    }

    private sealed class QueueEntry(long id, long jobId)
    {
        public long Id { get; } = id;

        public long JobId { get; } = jobId;

        public DateTime? FetchedAt { get; set; }

        public string? Token { get; set; }
    }

    private sealed record ServerEntry(string Data, DateTime LastHeartbeat);
}

/// <summary>What a fetcher does with the queue entry it took.</summary>
internal enum FetchedAction
{
    Remove,
    Requeue,
    KeepAlive,
}
