using Masterly.NightShift.Storage.Conformance;

namespace Masterly.NightShift.EntityFrameworkCore.Tests;

public class EfSqliteConformanceFacts : StorageConformanceFacts
{
    private readonly SqliteDatabase _database = new();

    protected override JobStorage CreateStorage() => _database.CreateStorage();

    public override void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class EfSqlServerConformanceFacts : StorageConformanceFacts
{
    protected override JobStorage CreateStorage() => SqlServerDatabase.CreateStorage();
}
