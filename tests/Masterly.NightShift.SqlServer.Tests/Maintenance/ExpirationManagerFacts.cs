extern alias ReferencedDapper;
using System.Data.Common;
using ReferencedDapper::Dapper;

namespace Masterly.NightShift.SqlServer.Tests;

public class ExpirationManagerFacts
{
    private readonly CancellationTokenSource _cts = new();

    [Fact]
    public void Ctor_ThrowsAnException_WhenStorageIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ExpirationManager(null, TimeSpan.Zero, TimeSpan.FromTicks(1)));
    }

    [Fact, CleanDatabase]
    public void Execute_RemovesOutdatedRecords()
    {
        using DbConnection connection = CreateConnection();
        CreateExpirationEntry(connection, DateTime.UtcNow.AddMonths(-1));
        ExpirationManager manager = CreateManager();

        manager.Execute(_cts.Token);

        Assert.True(IsEntryExpired(connection));
    }

    [Fact, CleanDatabase]
    public void Execute_DoesNotRemoveEntries_WithNoExpirationTimeSet()
    {
        using DbConnection connection = CreateConnection();
        CreateExpirationEntry(connection, null);
        ExpirationManager manager = CreateManager();

        manager.Execute(_cts.Token);

        Assert.False(IsEntryExpired(connection));
    }

    [Fact, CleanDatabase]
    public void Execute_DoesNotRemoveEntries_WithFreshExpirationTime()
    {
        using DbConnection connection = CreateConnection();
        CreateExpirationEntry(connection, DateTime.UtcNow.AddMonths(1));
        ExpirationManager manager = CreateManager();

        manager.Execute(_cts.Token);

        Assert.False(IsEntryExpired(connection));
    }

    [Fact, CleanDatabase]
    public void Execute_Processes_AggregatedCounterTable()
    {
        using DbConnection connection = CreateConnection();
        // Arrange
        string createSql = $@"
insert into [{Constants.DefaultSchema}].AggregatedCounter ([Key], [Value], ExpireAt) 
values ('key', 1, @expireAt)";
        connection.Execute(createSql, new { expireAt = DateTime.UtcNow.AddMonths(-1) });

        ExpirationManager manager = CreateManager();

        // Act
        manager.Execute(_cts.Token);

        // Assert
        Assert.Equal(0, connection.Query<int>($"select count(*) from [{Constants.DefaultSchema}].Counter").Single());
    }

    [Fact, CleanDatabase]
    public void Execute_Processes_JobTable()
    {
        using DbConnection connection = CreateConnection();
        // Arrange
        string createSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt, ExpireAt) 
values ('', '', getutcdate(), @expireAt)";
        connection.Execute(createSql, new { expireAt = DateTime.UtcNow.AddMonths(-1) });

        ExpirationManager manager = CreateManager();

        // Act
        manager.Execute(_cts.Token);

        // Assert
        Assert.Equal(0, connection.Query<int>($"select count(*) from [{Constants.DefaultSchema}].Job").Single());
    }

    [Fact, CleanDatabase]
    public void Execute_Processes_StateTable_WhenOptionIsConfigured()
    {
        using DbConnection connection = CreateConnection();
        // Arrange
        long jobId = CreateJobWithStateHistory(connection);
        ExpirationManager manager = CreateManager(TimeSpan.FromDays(7));

        // Act
        manager.Execute(_cts.Token);

        // Assert
        string[] states = GetStateNames(connection, jobId);

        Assert.Equal("old-state-1", states[0]);
        Assert.Equal("current-state", states[1]);
    }

    // Before schema 9 there's no [State].[CreatedAt] index, and the cleanup query, which seeks on it, fails with
    // error 8622 at every check interval
    [Fact, CleanDatabase]
    public void Execute_SkipsTheStateCleanup_OnASchemaOlderThan9()
    {
        using DbConnection connection = CreateConnection();
        long jobId = CreateJobWithStateHistory(connection);
        SqlServerStorage storage = new(ConnectionUtils.CreateConnection);

        // The test database is at the latest schema; pretend detection found version 8
        typeof(SqlServerStorage)
            .GetField("_detectedSchemaVersion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SetValue(storage, 8);

        ExpirationManager manager = new(storage, TimeSpan.FromDays(7), TimeSpan.FromTicks(1));

        manager.Execute(_cts.Token);
        manager.Execute(_cts.Token);

        Assert.Equal(new[] { "old-state-1", "old-state-2", "current-state" }, GetStateNames(connection, jobId));
    }

    [Fact, CleanDatabase]
    public void Execute_CleansUpStates_WhenTheSchemaVersionIsNotKnown()
    {
        using DbConnection connection = CreateConnection();
        long jobId = CreateJobWithStateHistory(connection);
        SqlServerStorage storage = new(
            ConnectionUtils.CreateConnection,
            new SqlServerStorageOptions { PrepareSchemaIfNecessary = false, TryAutoDetectSchemaDependentOptions = false });
        ExpirationManager manager = new(storage, TimeSpan.FromDays(7), TimeSpan.FromTicks(1));

        manager.Execute(_cts.Token);

        Assert.Null(storage.DetectedSchemaVersion);
        Assert.Equal(new[] { "old-state-1", "current-state" }, GetStateNames(connection, jobId));
    }

    [Fact]
    public void GetDistributedLockKey_IsPrefixedWithTheSchemaName_SoThatStoragesWithDifferentSchemasDoNotShareIt()
    {
        SqlServerStorageOptions options = new() { PrepareSchemaIfNecessary = false, TryAutoDetectSchemaDependentOptions = false };
        SqlServerStorage defaultStorage = new(ConnectionUtils.GetConnectionString(), options);
        SqlServerStorage reportsStorage = new(
            ConnectionUtils.GetConnectionString(),
            new SqlServerStorageOptions { SchemaName = "Reports", PrepareSchemaIfNecessary = false, TryAutoDetectSchemaDependentOptions = false });

        Assert.Equal("HangFire:locks:expirationmanager", ExpirationManager.GetDistributedLockKey(defaultStorage));
        Assert.Equal("Reports:locks:expirationmanager", ExpirationManager.GetDistributedLockKey(reportsStorage));
    }

    [Fact, CleanDatabase]
    public void Execute_Processes_ListTable()
    {
        using DbConnection connection = CreateConnection();
        // Arrange
        string createSql = $@"
insert into [{Constants.DefaultSchema}].List ([Key], ExpireAt) 
values ('key', @expireAt)";
        connection.Execute(createSql, new { expireAt = DateTime.UtcNow.AddMonths(-1) });

        ExpirationManager manager = CreateManager();

        // Act
        manager.Execute(_cts.Token);

        // Assert
        Assert.Equal(0, connection.Query<int>($"select count(*) from [{Constants.DefaultSchema}].List").Single());
    }

    [Fact, CleanDatabase]
    public void Execute_Processes_SetTable()
    {
        using DbConnection connection = CreateConnection();
        // Arrange
        string createSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Score], [Value], ExpireAt) 
values ('key', 0, '', @expireAt)";
        connection.Execute(createSql, new { expireAt = DateTime.UtcNow.AddMonths(-1) });

        ExpirationManager manager = CreateManager();

        // Act
        manager.Execute(_cts.Token);

        // Assert
        Assert.Equal(0, connection.Query<int>($"select count(*) from [{Constants.DefaultSchema}].[Set]").Single());
    }

    [Fact, CleanDatabase]
    public void Execute_Processes_HashTable()
    {
        using DbConnection connection = CreateConnection();
        // Arrange
        string createSql = $@"
insert into [{Constants.DefaultSchema}].Hash ([Key], [Field], [Value], ExpireAt) 
values ('key', 'field', '', @expireAt)";
        connection.Execute(createSql, new { expireAt = DateTime.UtcNow.AddMonths(-1) });

        ExpirationManager manager = CreateManager();

        // Act
        manager.Execute(_cts.Token);

        // Assert
        Assert.Equal(0, connection.Query<int>($"select count(*) from [{Constants.DefaultSchema}].Hash").Single());
    }

    // A job whose current state is a month old, with two older states: one a day old, one a month old
    private static long CreateJobWithStateHistory(DbConnection connection)
    {
        DateTime now = DateTime.UtcNow;
        string createSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, StateName, CreatedAt)
values ('', '', '', getutcdate());
declare @JobId bigint;
set @JobId = scope_identity();
insert into [{Constants.DefaultSchema}].State (JobId, Name, CreatedAt)
values (@JobId, 'old-state-1', @createdAt1);
insert into [{Constants.DefaultSchema}].State (JobId, Name, CreatedAt)
values (@JobId, 'old-state-2', @createdAt2);
insert into [{Constants.DefaultSchema}].State (JobId, Name, CreatedAt)
values (@JobId, 'current-state', @createdAt3);
declare @StateId bigint;
set @StateId = scope_identity();
update [{Constants.DefaultSchema}].Job set StateId = @StateId;
select @JobId as Id;";

        return connection
            .Query<long>(createSql, new { createdAt1 = now.AddDays(-1), createdAt2 = now.AddMonths(-1), createdAt3 = now.AddMonths(-1) })
            .Single();
    }

    private static string[] GetStateNames(DbConnection connection, long jobId)
    {
        return [.. connection.Query<string>($"select [Name] from [{Constants.DefaultSchema}].State where JobId = @jobId order by Id", new { jobId })];
    }

    private static void CreateExpirationEntry(DbConnection connection, DateTime? expireAt)
    {
        string insertSql = $@"
insert into [{Constants.DefaultSchema}].AggregatedCounter ([Key], [Value], [ExpireAt])
values (N'key', 1, @expireAt)";

        connection.Execute(insertSql, new { expireAt });
    }

    private static bool IsEntryExpired(DbConnection connection)
    {
        int count = connection.Query<int>(
                $"select count(*) from [{Constants.DefaultSchema}].AggregatedCounter where [Key] = N'key'").Single();
        return count == 0;
    }

    private DbConnection CreateConnection()
    {
        return ConnectionUtils.CreateConnection();
    }

    private ExpirationManager CreateManager(TimeSpan? stateExpirationTimeout = null)
    {
        SqlServerStorage storage = new(ConnectionUtils.CreateConnection);
        return new ExpirationManager(storage, stateExpirationTimeout ?? TimeSpan.Zero, TimeSpan.FromTicks(1));
    }
}
