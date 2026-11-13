using Masterly.NightShift.Storage.Conformance;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>The storage conformance suite, run against SQL Server, the reference storage.</summary>
[CleanDatabase]
public class SqlServerConformanceFacts : StorageConformanceFacts
{
    // SQL Server's graphs read counters only once the aggregator has moved them
    protected override bool TimelineReadsCountersAtOnce => false;

    protected override JobStorage CreateStorage()
    {
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
    }

    protected override Action<IWriteOnlyTransaction> PrepareWriteThatFailsTheCommit()
    {
        // A state for a job that doesn't exist breaks the foreign key
        return static x => x.SetJobState("987654321", new EnqueuedState());
    }
}
