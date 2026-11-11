using Masterly.NightShift.Storage.Conformance;

namespace Masterly.NightShift.Redis.Tests;

public class RedisConformanceFacts : StorageConformanceFacts
{
    private RedisStorage? _storage;

    protected override JobStorage CreateStorage() => _storage = RedisTestServer.CreateStorage();

    public override void Dispose()
    {
        if (_storage != null) RedisTestServer.Delete(_storage);
        GC.SuppressFinalize(this);
    }
}
