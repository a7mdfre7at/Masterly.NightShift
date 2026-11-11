using Masterly.NightShift.Storage.Conformance;

namespace Masterly.NightShift.MongoDB.Tests;

public class MongoConformanceFacts : StorageConformanceFacts
{
    private MongoStorage? _storage;

    protected override JobStorage CreateStorage() => _storage = MongoTestServer.CreateStorage();

    public override void Dispose()
    {
        if (_storage != null) MongoTestServer.Drop(_storage);
        GC.SuppressFinalize(this);
    }
}
