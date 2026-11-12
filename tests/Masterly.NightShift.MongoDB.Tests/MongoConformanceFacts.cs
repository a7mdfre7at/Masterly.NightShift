using Masterly.NightShift.Storage.Conformance;

namespace Masterly.NightShift.MongoDB.Tests;

public class MongoConformanceFacts : StorageConformanceFacts
{
    private readonly Lazy<MongoStorage> _storage = new(static () => MongoTestServer.CreateStorage());

    // A test that fetches on another thread may ask for the storage from two threads at once: each storage has a
    // collection prefix of its own, so both must get the same one
    protected override JobStorage CreateStorage() => _storage.Value;

    public override void Dispose()
    {
        if (_storage.IsValueCreated)
        {
            MongoTestServer.Drop(_storage.Value);
        }

        GC.SuppressFinalize(this);
    }
}
