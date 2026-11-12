using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage.Monitoring;
using Masterly.NightShift.Storage.Shared;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Masterly.NightShift.MongoDB;

/// <summary>How the storage's documents are read and written.</summary>
internal static class MongoDocuments
{
    public static FilterDefinitionBuilder<BsonDocument> Filter => Builders<BsonDocument>.Filter;

    public static UpdateDefinitionBuilder<BsonDocument> Update => Builders<BsonDocument>.Update;

    public static BsonValue Text(string? value) => value != null ? new BsonString(value) : BsonNull.Value;

    public static BsonValue Date(DateTime? value) => value.HasValue ? new BsonDateTime(value.Value) : BsonNull.Value;

    public static string? TextOf(BsonDocument document, string name)
    {
        return document.TryGetValue(name, out BsonValue value) && !value.IsBsonNull ? value.AsString : null;
    }

    public static DateTime? DateOf(BsonDocument document, string name)
    {
        return document.TryGetValue(name, out BsonValue value) && !value.IsBsonNull ? value.ToUniversalTime() : null;
    }

    public static BsonDocument FromState(StoredState state)
    {
        return new BsonDocument
        {
            ["Name"] = state.Name,
            ["Reason"] = Text(state.Reason),
            ["CreatedAt"] = new BsonDateTime(state.CreatedAt),
            ["Data"] = Text(state.Data),
        };
    }

    public static StoredState ToState(BsonDocument document)
    {
        return new StoredState(document["Name"].AsString, TextOf(document, "Reason"), document["CreatedAt"].ToUniversalTime(), TextOf(document, "Data"));
    }

    public static BsonArray FromParameters(IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        return [.. parameters.Select(static x => new BsonDocument { ["N"] = x.Key, ["V"] = Text(x.Value) })];
    }

    public static StoredJob ToJob(BsonDocument document)
    {
        StoredJob job = new()
        {
            Id = document["_id"].ToInt64(),
            InvocationData = document["InvocationData"].AsString,
            Arguments = TextOf(document, "Arguments"),
            CreatedAt = document["CreatedAt"].ToUniversalTime(),
            ExpireAt = DateOf(document, "ExpireAt"),
            StateName = TextOf(document, "StateName"),
            State = document.TryGetValue("State", out BsonValue state) && state.IsBsonDocument ? ToState(state.AsBsonDocument) : null,
        };

        if (document.TryGetValue("Parameters", out BsonValue parameters) && parameters.IsBsonArray)
        {
            foreach (BsonDocument parameter in parameters.AsBsonArray.Select(static x => x.AsBsonDocument))
            {
                job.Parameters[parameter["N"].AsString] = TextOf(parameter, "V");
            }
        }

        if (document.TryGetValue("History", out BsonValue history) && history.IsBsonArray)
        {
            job.History = [.. history.AsBsonArray.Select(static x => ToState(x.AsBsonDocument)).Reverse()];
        }

        return job;
    }

    /// <summary>Sets a job parameter in one atomic update: the others are kept, this one replaced or added.</summary>
    public static PipelineDefinition<BsonDocument, BsonDocument> SetParameter(string name, string? value)
    {
        BsonDocument stage = new("$set", new BsonDocument("Parameters", new BsonDocument("$concatArrays", new BsonArray
        {
            new BsonDocument("$filter", new BsonDocument
            {
                ["input"] = new BsonDocument("$ifNull", new BsonArray { "$Parameters", new BsonArray() }),
                ["cond"] = new BsonDocument("$ne", new BsonArray { "$$this.N", new BsonDocument("$literal", name) }),
            }),
            // Names and values are literals: in a pipeline, a string starting with $ would be read as a field path
            new BsonArray { new BsonDocument { ["N"] = new BsonDocument("$literal", name), ["V"] = new BsonDocument("$literal", Text(value)) } },
        })));

        return new BsonDocumentStagePipelineDefinition<BsonDocument, BsonDocument>([stage]);
    }

    /// <summary>An update that sets <paramref name="fields"/>, whose values are literals or expressions such as <see cref="ServerTime"/>.</summary>
    public static UpdateDefinition<BsonDocument> SetFields(BsonDocument fields)
    {
        return new PipelineUpdateDefinition<BsonDocument>(new BsonDocumentStagePipelineDefinition<BsonDocument, BsonDocument>([new BsonDocument("$set", fields)]));
    }

    /// <summary>The server's clock, plus <paramref name="offset"/>, in an update pipeline or an <c>$expr</c>.</summary>
    public static BsonValue ServerTime(TimeSpan offset)
    {
        return offset == TimeSpan.Zero ? "$$NOW" : new BsonDocument("$add", new BsonArray { "$$NOW", (long)offset.TotalMilliseconds });
    }

    /// <summary>Matches documents whose date <paramref name="field"/> is earlier than the server's clock minus <paramref name="age"/>.</summary>
    public static FilterDefinition<BsonDocument> OlderThan(string field, TimeSpan age)
    {
        return new BsonDocument("$expr", new BsonDocument("$lt", new BsonArray { $"${field}", ServerTime(-age) }));
    }
}

internal sealed class MongoConnection(MongoStorage storage) : JobStorageConnection, ILeaseStore
{
    private readonly ReentrantLocks _locks = new();
    private readonly MongoCollections _collections = storage.Collections;

    private static FilterDefinitionBuilder<BsonDocument> Filter => MongoDocuments.Filter;

    private static UpdateDefinitionBuilder<BsonDocument> Update => MongoDocuments.Update;

    public override IWriteOnlyTransaction CreateWriteTransaction() => new MongoTransaction(storage, this);

    public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return _locks.Acquire(resource, () => LeaseLock.Acquire(this, resource, timeout, storage.Options.DistributedLockLifetime));
    }

    public override string? CreateExpiredJob(Job job, IDictionary<string, string?> parameters, DateTime createdAt, TimeSpan expireIn)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(parameters);

        long id = _collections.Sequences.FindOneAndUpdate(
            Filter.Eq("_id", "jobs"),
            Update.Inc("Value", 1L),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After })["Value"].ToInt64();

        StoredJob stored = StoredJob.Create(id, job, parameters, createdAt, StorageValues.ExpireAt(createdAt, expireIn));
        _collections.Jobs.InsertOne(new BsonDocument
        {
            ["_id"] = id,
            ["InvocationData"] = stored.InvocationData,
            ["Arguments"] = MongoDocuments.Text(stored.Arguments),
            ["CreatedAt"] = new BsonDateTime(createdAt),
            ["ExpireAt"] = MongoDocuments.Date(stored.ExpireAt),
            ["StateName"] = BsonNull.Value,
            ["Parameters"] = MongoDocuments.FromParameters(stored.Parameters),
            ["History"] = new BsonArray(),
        });

        return StorageValues.FormatJobId(id);
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

            foreach (string queue in queues)
            {
                if (TryClaim(queue, cancellationToken) is { } fetched)
                {
                    return fetched;
                }
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

        _collections.Jobs.UpdateOne(Filter.Eq("_id", jobId), MongoDocuments.SetParameter(name, value));
    }

    public override string? GetJobParameter(string id, string name)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        if (!StorageValues.TryParseJobId(id, out long jobId))
        {
            return null;
        }

        StoredJob? job = ReadJob(jobId);
        return job != null && job.Parameters.TryGetValue(name, out string? value) ? value : null;
    }

    public override JobData? GetJobData(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return StorageValues.TryParseJobId(jobId, out long id) ? ReadJob(id)?.ToJobData() : null;
    }

    public override StateData? GetStateData(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return StorageValues.TryParseJobId(jobId, out long id) ? ReadJob(id)?.ToStateData() : null;
    }

    public override void AnnounceServer(string serverId, ServerContext context)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        ArgumentNullException.ThrowIfNull(context);

        _collections.Servers.UpdateOne(
            Filter.Eq("_id", serverId),
            Update.Set("Data", StorageValues.SerializeServer(context)).Set("LastHeartbeat", JobClock.UtcNow),
            new UpdateOptions { IsUpsert = true });
    }

    public override void RemoveServer(string serverId)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        _collections.Servers.DeleteOne(Filter.Eq("_id", serverId));
    }

    public override void Heartbeat(string serverId)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        UpdateResult result = _collections.Servers.UpdateOne(Filter.Eq("_id", serverId), Update.Set("LastHeartbeat", JobClock.UtcNow));
        if (result.MatchedCount == 0)
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

        return (int)_collections.Servers.DeleteMany(Filter.Lt("LastHeartbeat", JobClock.UtcNow - timeOut)).DeletedCount;
    }

    public override HashSet<string> GetAllItemsFromSet(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return [.. Values(_collections.Sets.Find(Filter.Eq("Key", key)))];
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

        return Values(_collections.Sets
            .Find(Filter.And(Filter.Eq("Key", key), Filter.Gte("Score", fromScore), Filter.Lte("Score", toScore)))
            .Sort(Builders<BsonDocument>.Sort.Ascending("Score").Ascending("Value"))
            .Limit(count));
    }

    public override long GetSetCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _collections.Sets.CountDocuments(Filter.Eq("Key", key));
    }

    public override long GetSetCount(IEnumerable<string> keys, int limit)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (limit == 0)
        {
            return 0;
        }

        return _collections.Sets.CountDocuments(Filter.In("Key", keys), new CountOptions { Limit = limit });
    }

    public override bool GetSetContains(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return _collections.Sets.CountDocuments(Filter.And(Filter.Eq("Key", key), Filter.Eq("Value", value)), new CountOptions { Limit = 1 }) > 0;
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

        return Values(_collections.Sets
            .Find(Filter.Eq("Key", key))
            .Sort(Builders<BsonDocument>.Sort.Ascending("Score").Ascending("Value"))
            .Skip(start)
            .Limit((int)Math.Min((long)endingAt - start + 1, int.MaxValue)));
    }

    public override TimeSpan GetSetTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TimeToLive(_collections.Sets, key);
    }

    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(keyValuePairs);

        List<WriteModel<BsonDocument>> writes = [.. MongoTransaction.HashWrites(key, keyValuePairs)];
        if (writes.Count > 0)
        {
            _collections.Hashes.BulkWrite(writes);
        }
    }

    public override Dictionary<string, string>? GetAllEntriesFromHash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        List<BsonDocument> fields = _collections.Hashes.Find(Filter.Eq("Key", key)).ToList();
        return fields.Count == 0
            ? null
            : fields.ToDictionary(static x => x["Field"].AsString, static x => MongoDocuments.TextOf(x, "Value")!, StringComparer.Ordinal); // A field set to null reads back as null
    }

    public override string? GetValueFromHash(string key, string name)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(name);
        BsonDocument? field = _collections.Hashes.Find(Filter.And(Filter.Eq("Key", key), Filter.Eq("Field", name))).FirstOrDefault();
        return field != null ? MongoDocuments.TextOf(field, "Value") : null;
    }

    public override long GetHashCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _collections.Hashes.CountDocuments(Filter.Eq("Key", key));
    }

    public override TimeSpan GetHashTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TimeToLive(_collections.Hashes, key);
    }

    public override long GetListCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _collections.Lists.CountDocuments(Filter.Eq("Key", key));
    }

    public override List<string> GetAllItemsFromList(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Values(_collections.Lists.Find(Filter.Eq("Key", key)).Sort(Builders<BsonDocument>.Sort.Descending("_id")));
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

        return Values(_collections.Lists
            .Find(Filter.Eq("Key", key))
            .Sort(Builders<BsonDocument>.Sort.Descending("_id"))
            .Skip(start)
            .Limit((int)Math.Min((long)endingAt - start + 1, int.MaxValue)));
    }

    public override TimeSpan GetListTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return TimeToLive(_collections.Lists, key);
    }

    public override long GetCounter(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return MongoMonitoringApi.ReadCounters(_collections, [key]).GetValueOrDefault(key);
    }

    public override DateTime GetUtcDateTime()
    {
        BsonDocument hello = storage.Database.RunCommand<BsonDocument>(new BsonDocument("hello", 1));
        return hello["localTime"].ToUniversalTime();
    }

    // Leases run out by the server's clock: the time to live asked for is added to it, and a lease is taken over
    // once the server's clock passed it, so holders whose clocks differ never take a live lease from each other.
    // An upsert can't filter with $expr, so the update itself keeps a live lease unchanged: nothing is modified.
    bool ILeaseStore.TryTake(string resource, string owner, DateTime expireAt)
    {
        BsonDocument free = new("$or", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$ExpireAt"), "missing" }),
            new BsonDocument("$lt", new BsonArray { "$ExpireAt", "$$NOW" }),
        });

        try
        {
            UpdateResult result = _collections.Locks.UpdateOne(
                Filter.Eq("_id", resource),
                MongoDocuments.SetFields(new BsonDocument
                {
                    ["Owner"] = new BsonDocument("$cond", new BsonArray { free, new BsonDocument("$literal", owner), "$Owner" }),
                    ["ExpireAt"] = new BsonDocument("$cond", new BsonArray { free, MongoDocuments.ServerTime(TimeToLive(expireAt)), "$ExpireAt" }),
                }),
                new UpdateOptions { IsUpsert = true });
            return result.ModifiedCount > 0 || result.UpsertedId != null;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another owner's upsert inserted the same id at the same moment
            return false;
        }
    }

    bool ILeaseStore.TryRenew(string resource, string owner, DateTime expireAt)
    {
        return _collections.Locks.UpdateOne(
            Filter.And(Filter.Eq("_id", resource), Filter.Eq("Owner", owner)),
            MongoDocuments.SetFields(new BsonDocument("ExpireAt", MongoDocuments.ServerTime(TimeToLive(expireAt))))).MatchedCount > 0;
    }

    void ILeaseStore.Release(string resource, string owner)
    {
        try
        {
            _collections.Locks.DeleteOne(Filter.And(Filter.Eq("_id", resource), Filter.Eq("Owner", owner)));
        }
        catch (MongoException ex)
        {
            // The lease runs out by itself; another owner takes the lock once it does
            LogProvider.GetLogger(typeof(MongoConnection)).WarnException($"Couldn't release the lock on '{resource}'.", ex);
        }
    }

    /// <summary>
    /// Claims the oldest visible entry of a queue, else the entry whose worker stopped keeping it alive longest
    /// ago. Each claim is one atomic findAndModify, so an entry goes to one worker only. They are two, because
    /// one query matching both kinds sorted by <c>_id</c> can't walk the (Queue, FetchedAt, _id) index for the
    /// stale ones; the second runs only when the queue has no visible entry, so it reads the fetched ones only.
    /// Times are the server's, so workers whose clocks differ agree on what is stale.
    /// </summary>
    private MongoFetchedJob? TryClaim(string queue, CancellationToken cancellationToken)
    {
        string token = Guid.NewGuid().ToString("N");
        UpdateDefinition<BsonDocument> claim = MongoDocuments.SetFields(new BsonDocument
        {
            ["FetchedAt"] = MongoDocuments.ServerTime(TimeSpan.Zero),
            ["Token"] = new BsonDocument("$literal", token),
        });

        BsonDocument? entry = _collections.JobQueue.FindOneAndUpdate(
            Filter.And(Filter.Eq("Queue", queue), Filter.Eq("FetchedAt", BsonNull.Value)),
            claim,
            new FindOneAndUpdateOptions<BsonDocument> { Sort = Builders<BsonDocument>.Sort.Ascending("_id") },
            cancellationToken);

        entry ??= _collections.JobQueue.FindOneAndUpdate(
            Filter.And(
                Filter.Eq("Queue", queue),
                Filter.Type("FetchedAt", BsonType.DateTime),
                MongoDocuments.OlderThan("FetchedAt", storage.Options.InvisibilityTimeout)),
            claim,
            new FindOneAndUpdateOptions<BsonDocument> { Sort = Builders<BsonDocument>.Sort.Ascending("FetchedAt").Ascending("_id") },
            cancellationToken);

        return entry != null ? new MongoFetchedJob(storage, entry["_id"].AsObjectId, entry["JobId"].ToInt64(), queue, token) : null;
    }

    // A lease's time to live is what is left until expireAt by the local clock, counted from the server's
    private static TimeSpan TimeToLive(DateTime expireAt)
    {
        TimeSpan timeToLive = expireAt - JobClock.UtcNow;
        return timeToLive > TimeSpan.Zero ? timeToLive : TimeSpan.Zero;
    }

    private StoredJob? ReadJob(long id)
    {
        BsonDocument? document = _collections.Jobs.Find(Filter.Eq("_id", id)).Project(Builders<BsonDocument>.Projection.Exclude("History")).FirstOrDefault();
        return document != null ? MongoDocuments.ToJob(document) : null;
    }

    private static List<string> Values(IFindFluent<BsonDocument, BsonDocument> find)
    {
        return [.. find.Project(Builders<BsonDocument>.Projection.Include("Value")).ToList().Select(static x => x["Value"].AsString)];
    }

    private static TimeSpan TimeToLive(IMongoCollection<BsonDocument> collection, string key)
    {
        BsonDocument? first = collection
            .Find(Filter.And(Filter.Eq("Key", key), Filter.Ne("ExpireAt", BsonNull.Value)))
            .Sort(Builders<BsonDocument>.Sort.Ascending("ExpireAt"))
            .Limit(1)
            .FirstOrDefault();
        return StorageValues.TimeToLive(first != null ? MongoDocuments.DateOf(first, "ExpireAt") : null);
    }
}

internal sealed class MongoTransaction(MongoStorage storage, MongoConnection connection) : RecordingTransaction
{
    private static FilterDefinitionBuilder<BsonDocument> Filter => MongoDocuments.Filter;

    private static UpdateDefinitionBuilder<BsonDocument> Update => MongoDocuments.Update;

    public static IEnumerable<WriteModel<BsonDocument>> HashWrites(string key, IEnumerable<KeyValuePair<string, string>> fields)
    {
        return fields.Select(field => new UpdateOneModel<BsonDocument>(
            Filter.And(Filter.Eq("Key", key), Filter.Eq("Field", field.Key)),
            Update.Set("Value", MongoDocuments.Text(field.Value)))
        { IsUpsert = true });
    }

    protected override IDisposable AcquireLock(string resource, TimeSpan timeout) => connection.AcquireDistributedLock(resource, timeout);

    protected override void Apply(IReadOnlyList<StorageOperation> operations)
    {
        if (operations.Count == 0)
        {
            return;
        }

        storage.InTransaction(session =>
        {
            foreach (StorageOperation operation in operations)
            {
                ApplyOne(session, operation);
            }
        });
    }

    protected override void Committed()
    {
        if (QueuesWritten().Any())
        {
            storage.Signal();
        }
    }

    private void ApplyOne(IClientSessionHandle session, StorageOperation operation)
    {
        MongoCollections collections = storage.Collections;

        switch (operation)
        {
            case ExpireJobOperation expire:
                collections.Jobs.UpdateOne(session, Filter.Eq("_id", expire.JobId), Update.Set("ExpireAt", MongoDocuments.Date(expire.ExpireAt)));
                break;

            case AddJobStateOperation state:
                BsonDocument document = MongoDocuments.FromState(state.State);
                UpdateDefinition<BsonDocument> update = state.SetCurrent
                    ? Update.Push("History", document).Set("StateName", state.State.Name).Set("State", document)
                    : Update.Push("History", document);
                collections.Jobs.UpdateOne(session, Filter.Eq("_id", state.JobId), update);
                break;

            case AddToQueueOperation queue:
                collections.JobQueue.InsertOne(session, new BsonDocument { ["Queue"] = queue.Queue, ["JobId"] = queue.JobId, ["FetchedAt"] = BsonNull.Value });
                break;

            case RemoveFromQueueOperation removal:
                MongoFetchedJob fetched = (MongoFetchedJob)removal.FetchedJob; // This storage only fetches its own jobs
                collections.JobQueue.DeleteOne(session, Filter.And(Filter.Eq("_id", fetched.EntryId), Filter.Eq("Token", fetched.Token)));
                break;

            case SetJobParameterOperation parameter:
                collections.Jobs.UpdateOne(session, Filter.Eq("_id", parameter.JobId), MongoDocuments.SetParameter(parameter.Name, parameter.Value));
                break;

            case CounterOperation counter:
                collections.Counters.InsertOne(session, new BsonDocument { ["Key"] = counter.Key, ["Value"] = counter.Value, ["ExpireAt"] = MongoDocuments.Date(counter.ExpireAt) });
                break;

            case AddToSetOperation add:
                collections.Sets.UpdateOne(session, SetItem(add.Key, add.Value), Update.Set("Score", add.Score), new UpdateOptions { IsUpsert = true });
                break;

            case AddRangeToSetOperation range:
                if (range.Values.Count > 0)
                {
                    collections.Sets.BulkWrite(session, range.Values.Select(value =>
                        new UpdateOneModel<BsonDocument>(SetItem(range.Key, value), Update.SetOnInsert("Score", 0.0)) { IsUpsert = true }));
                }

                break;

            case RemoveFromSetOperation remove:
                collections.Sets.DeleteOne(session, SetItem(remove.Key, remove.Value));
                break;

            case RemoveSetOperation removeSet:
                collections.Sets.DeleteMany(session, Filter.Eq("Key", removeSet.Key));
                break;

            case InsertToListOperation insert:
                // A new item expires with the list, as on SQL Server
                BsonDocument? existing = collections.Lists
                    .Find(session, Filter.And(Filter.Eq("Key", insert.Key), Filter.Ne("ExpireAt", BsonNull.Value)))
                    .Limit(1)
                    .FirstOrDefault();
                collections.Lists.InsertOne(session, new BsonDocument
                {
                    ["Key"] = insert.Key,
                    ["Value"] = insert.Value,
                    ["ExpireAt"] = MongoDocuments.Date(existing != null ? MongoDocuments.DateOf(existing, "ExpireAt") : null),
                });
                break;

            case RemoveFromListOperation removeFromList:
                collections.Lists.DeleteMany(session, Filter.And(Filter.Eq("Key", removeFromList.Key), Filter.Eq("Value", removeFromList.Value)));
                break;

            case TrimListOperation trim:
                TrimList(session, collections.Lists, trim);
                break;

            case SetRangeInHashOperation hash:
                List<WriteModel<BsonDocument>> writes = [.. HashWrites(hash.Key, hash.Fields)];
                if (writes.Count > 0)
                {
                    collections.Hashes.BulkWrite(session, writes);
                }

                break;

            case RemoveHashOperation removeHash:
                collections.Hashes.DeleteMany(session, Filter.Eq("Key", removeHash.Key));
                break;

            case ExpireKeyOperation expireKey:
                IMongoCollection<BsonDocument> target = expireKey.Kind switch
                {
                    KeyKind.Set => collections.Sets,
                    KeyKind.List => collections.Lists,
                    _ => collections.Hashes,
                };
                target.UpdateMany(session, Filter.Eq("Key", expireKey.Key), Update.Set("ExpireAt", MongoDocuments.Date(expireKey.ExpireAt)));
                break;

            default:
                throw new NotSupportedException($"Unknown storage operation {operation.GetType().Name}.");
        }
    }

    private static FilterDefinition<BsonDocument> SetItem(string key, string value) => Filter.And(Filter.Eq("Key", key), Filter.Eq("Value", value));

    /// <summary>
    /// Keeps the items from position <c>KeepStartingFrom</c> to <c>KeepEndingAt</c>, newest (highest
    /// <c>_id</c>) first. The kept items are those between two ids, read through the (Key, _id) index without
    /// loading the list, so the others are deleted by comparing ids, whatever the length of the list.
    /// </summary>
    private static void TrimList(IClientSessionHandle session, IMongoCollection<BsonDocument> lists, TrimListOperation trim)
    {
        FilterDefinition<BsonDocument> list = Filter.Eq("Key", trim.Key);
        BsonValue? newest = IdAt(session, lists, list, 0);
        if (newest == null)
        {
            return;
        }

        // Nothing is kept from a range that is empty or starts past the end; everything from the newest kept item
        // on is kept when the range ends past the end
        int keepFrom = Math.Max(0, trim.KeepStartingFrom);
        BsonValue? newestKept = trim.KeepEndingAt < keepFrom ? null : keepFrom == 0 ? newest : IdAt(session, lists, list, keepFrom);
        if (newestKept == null)
        {
            lists.DeleteMany(session, Filter.And(list, Filter.Lte("_id", newest)));
            return;
        }

        BsonValue? oldestKept = IdAt(session, lists, list, trim.KeepEndingAt);
        if (newestKept != newest)
        {
            lists.DeleteMany(session, Filter.And(list, Filter.Gt("_id", newestKept), Filter.Lte("_id", newest)));
        }

        if (oldestKept != null)
        {
            lists.DeleteMany(session, Filter.And(list, Filter.Lt("_id", oldestKept)));
        }
    }

    // The id of the item at a position, newest first
    private static BsonValue? IdAt(IClientSessionHandle session, IMongoCollection<BsonDocument> lists, FilterDefinition<BsonDocument> list, int position)
    {
        return lists
            .Find(session, list)
            .Sort(Builders<BsonDocument>.Sort.Descending("_id"))
            .Skip(position)
            .Limit(1)
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .FirstOrDefault()?["_id"];
    }
}

internal sealed class MongoFetchedJob(MongoStorage storage, ObjectId entryId, long jobId, string queue, string token)
    : FetchedJobBase(StorageValues.FormatJobId(jobId), queue, storage.Options.InvisibilityTimeout / 5)
{
    public ObjectId EntryId { get; } = entryId;

    public string Token { get; } = token;

    private FilterDefinition<BsonDocument> Owned => MongoDocuments.Filter.And(MongoDocuments.Filter.Eq("_id", EntryId), MongoDocuments.Filter.Eq("Token", Token));

    protected override void Remove() => storage.Collections.JobQueue.DeleteOne(Owned);

    protected override void ReturnToQueue()
    {
        storage.Collections.JobQueue.UpdateOne(Owned, MongoDocuments.Update.Set("FetchedAt", BsonNull.Value).Unset("Token"));
        storage.Signal();
    }

    protected override void KeepAlive()
    {
        storage.Collections.JobQueue.UpdateOne(Owned, MongoDocuments.SetFields(new BsonDocument("FetchedAt", MongoDocuments.ServerTime(TimeSpan.Zero))));
    }
}

internal sealed class MongoMonitoringApi(MongoStorage storage) : SharedMonitoringApi
{
    private static FilterDefinitionBuilder<BsonDocument> Filter => MongoDocuments.Filter;

    private MongoCollections Collections => storage.Collections;

    /// <summary>
    /// The values of counters: their increments not merged yet, plus their merged values. Both collections are
    /// read from one snapshot, so a merge committed between the two reads is never seen half done (counted twice
    /// or not at all).
    /// </summary>
    public static Dictionary<string, long> ReadCounters(MongoCollections collections, IReadOnlyCollection<string> keys)
    {
        Dictionary<string, long> result = new(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return result;
        }

        BsonDocument group = new("$group", new BsonDocument { ["_id"] = "$Key", ["Value"] = new BsonDocument("$sum", "$Value") });
        using IClientSessionHandle snapshot = collections.Client.StartSession(new ClientSessionOptions { Snapshot = true });

        foreach (BsonDocument row in collections.Counters.Aggregate(snapshot).Match(Filter.In("Key", keys)).AppendStage<BsonDocument>(group).ToList())
        {
            result[row["_id"].AsString] = row["Value"].ToInt64();
        }

        foreach (BsonDocument row in collections.AggregatedCounters.Find(snapshot, Filter.In("_id", keys)).ToList())
        {
            string key = row["_id"].AsString;
            result[key] = result.GetValueOrDefault(key) + row["Value"].ToInt64();
        }

        return result;
    }

    protected override long CountJobs(string stateName) => Collections.Jobs.CountDocuments(Filter.Eq("StateName", stateName));

    protected override IReadOnlyList<StoredJob> GetJobs(string stateName, int from, int count, bool descending)
    {
        if (count <= 0)
        {
            return [];
        }

        SortDefinition<BsonDocument> sort = descending ? Builders<BsonDocument>.Sort.Descending("_id") : Builders<BsonDocument>.Sort.Ascending("_id");
        return ReadJobs(Collections.Jobs.Find(Filter.Eq("StateName", stateName)).Sort(sort).Skip(from).Limit(count));
    }

    protected override IReadOnlyList<StoredJob> GetJobs(IReadOnlyCollection<long> ids)
    {
        return ids.Count == 0 ? [] : ReadJobs(Collections.Jobs.Find(Filter.In("_id", ids)));
    }

    protected override StoredJob? GetJobWithHistory(long id)
    {
        BsonDocument? document = Collections.Jobs.Find(Filter.Eq("_id", id)).FirstOrDefault();
        return document != null ? MongoDocuments.ToJob(document) : null;
    }

    protected override IReadOnlyList<string> GetQueues()
    {
        return [.. Collections.JobQueue.Distinct<string>("Queue", Filter.Empty).ToList()];
    }

    protected override IReadOnlyList<long> GetEnqueuedJobIds(string queue, int from, int count) => QueueJobIds(queue, fetched: false, from, count);

    protected override IReadOnlyList<long> GetFetchedJobIds(string queue, int from, int count) => QueueJobIds(queue, fetched: true, from, count);

    protected override (long Enqueued, long Fetched) GetQueueCounts(string queue)
    {
        return (Collections.JobQueue.CountDocuments(QueueFilter(queue, fetched: false)), Collections.JobQueue.CountDocuments(QueueFilter(queue, fetched: true)));
    }

    protected override IReadOnlyList<ServerDto> GetServers()
    {
        return [.. Collections.Servers.Find(Filter.Empty).ToList().Select(static x =>
        {
            StoredServerData data = StorageValues.DeserializeServer(MongoDocuments.TextOf(x, "Data"));
            return new ServerDto
            {
                Name = x["_id"].AsString,
                Heartbeat = MongoDocuments.DateOf(x, "LastHeartbeat"),
                Queues = data.Queues ?? [],
                StartedAt = data.StartedAt ?? DateTime.MinValue,
                WorkersCount = data.WorkerCount,
            };
        })];
    }

    protected override IReadOnlyDictionary<string, long> GetCounters(IReadOnlyCollection<string> keys) => ReadCounters(Collections, keys);

    protected override long GetSetCount(string key) => Collections.Sets.CountDocuments(Filter.Eq("Key", key));

    private List<long> QueueJobIds(string queue, bool fetched, int from, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        return [.. Collections.JobQueue
            .Find(QueueFilter(queue, fetched))
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .Skip(from)
            .Limit(count)
            .ToList()
            .Select(static x => x["JobId"].ToInt64())];
    }

    private static FilterDefinition<BsonDocument> QueueFilter(string queue, bool fetched)
    {
        return Filter.And(Filter.Eq("Queue", queue), fetched ? Filter.Ne("FetchedAt", BsonNull.Value) : Filter.Eq("FetchedAt", BsonNull.Value));
    }

    private static List<StoredJob> ReadJobs(IFindFluent<BsonDocument, BsonDocument> find)
    {
        return [.. find.Project(Builders<BsonDocument>.Projection.Exclude("History")).ToList().Select(MongoDocuments.ToJob)];
    }
}

/// <summary>
/// Merges counter increments into one document per counter, so state changes only ever insert increments and
/// never wait for each other on a counter. Two servers merging at once conflict and one retries, so nothing
/// is counted twice.
/// </summary>
internal sealed class MongoCountersAggregator(MongoStorage storage) : IBackgroundProcess
{
    private const int BatchSize = 1000;
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(MongoCountersAggregator));

    public void Execute(BackgroundProcessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            int merged;
            do
            {
                merged = AggregateBatch();
            }
            while (merged == BatchSize && !context.IsStopping);
        }
        catch (MongoException ex)
        {
            Logger.ErrorException("Couldn't merge the counters of the MongoDB storage.", ex);
        }

        context.Wait(storage.Options.CountersAggregateInterval);
    }

    public override string ToString() => "MongoDB counters aggregator";

    /// <summary>Merges up to a batch of increments; returns how many.</summary>
    internal int AggregateBatch()
    {
        int merged = 0;
        MongoCollections collections = storage.Collections;

        storage.InTransaction(session =>
        {
            List<BsonDocument> increments = collections.Counters.Find(session, MongoDocuments.Filter.Empty).Limit(BatchSize).ToList();
            merged = increments.Count;
            if (merged == 0)
            {
                return;
            }

            foreach (IGrouping<string, BsonDocument> counter in increments.GroupBy(static x => x["Key"].AsString, StringComparer.Ordinal))
            {
                long value = counter.Sum(static x => x["Value"].ToInt64());
                DateTime? expireAt = counter.Max(static x => MongoDocuments.DateOf(x, "ExpireAt"));
                UpdateDefinition<BsonDocument> update = MongoDocuments.Update.Inc("Value", value);
                if (expireAt.HasValue)
                {
                    update = update.Max("ExpireAt", expireAt.Value);
                }

                collections.AggregatedCounters.UpdateOne(session, MongoDocuments.Filter.Eq("_id", counter.Key), update, new UpdateOptions { IsUpsert = true });
            }

            collections.Counters.DeleteMany(session, MongoDocuments.Filter.In("_id", increments.Select(static x => x["_id"])));
        });

        return merged;
    }
}
