using Masterly.NightShift.Storage.Conformance;

namespace Masterly.NightShift.Redis.Tests;

public class RedisConformanceFacts : StorageConformanceFacts
{
    private readonly List<RedisStorage> _storages = [];

    protected override JobStorage CreateStorage() => Track(RedisTestServer.CreateStorage());

    protected override JobStorage CreateStorageWithoutKeepAlive(TimeSpan invisibilityTimeout)
    {
        return Track(RedisTestServer.CreateStorage(new RedisStorageOptions
        {
            InvisibilityTimeout = invisibilityTimeout,
            FetchPollInterval = TimeSpan.FromMilliseconds(100),
            KeepFetchedJobsAlive = false,
        }));
    }

    protected override JobStorage CreateStorageWithLockLifetime(TimeSpan lifetime)
    {
        return Track(RedisTestServer.CreateStorage(new RedisStorageOptions { DistributedLockLifetime = lifetime }));
    }

    protected override Action<IWriteOnlyTransaction> PrepareWriteThatFailsTheCommit()
    {
        // A key of another type than the write expects: the script refuses the transaction before writing
        RedisStorage storage = (RedisStorage)Storage;
        storage.Database.StringSet(storage.Key("set:broken"), "not a sorted set");
        return static x => x.AddToSet("broken", "value");
    }

    public override void Dispose()
    {
        foreach (RedisStorage storage in _storages) RedisTestServer.Delete(storage);
        GC.SuppressFinalize(this);
    }

    private RedisStorage Track(RedisStorage storage)
    {
        _storages.Add(storage);
        return storage;
    }
}
