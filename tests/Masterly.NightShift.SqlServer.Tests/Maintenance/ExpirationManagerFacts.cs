extern alias ReferencedDapper;
using System.Collections.Concurrent;
using System.Data.Common;
using Masterly.NightShift.Logging;
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
    public void Execute_ExpiresJobs_WhenTheSchemaNameContainsBraces()
    {
        SqlServerStorage storage = new(ConnectionUtils.CreateConnection, new SqlServerStorageOptions { SchemaName = "Bk{x}" });
        using DbConnection connection = CreateConnection();
        connection.Execute(
            """
            delete from [Bk{x}].Job;
            insert into [Bk{x}].Job (InvocationData, Arguments, CreatedAt, ExpireAt) values (N'', N'', getutcdate(), @expireAt);
            """,
            new { expireAt = DateTime.UtcNow.AddMonths(-1) });
        ExpirationManager manager = new(storage, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

        manager.Execute(_cts.Token);

        Assert.Equal(0, connection.ExecuteScalar<int>("select count(*) from [Bk{x}].Job"));
    }

    // Error 1222 is reported in the session's language: the message alone doesn't tell it
    [Fact, CleanDatabase]
    public void Execute_LogsNoError_WhenARowIsLocked_InASessionWithAnotherLanguage()
    {
        using DbConnection connection = CreateConnection();
        connection.Execute(
            $"insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt, ExpireAt) values (N'', N'', getutcdate(), @expireAt)",
            new { expireAt = DateTime.UtcNow.AddMonths(-1) });
        using DbConnection blocker = CreateConnection();
        using DbTransaction blocking = blocker.BeginTransaction();
        blocker.Execute($"update [{Constants.DefaultSchema}].Job set ExpireAt = ExpireAt", transaction: blocking);
        SqlServerStorage storage = new(static () =>
        {
            DbConnection german = ConnectionUtils.CreateConnection();
            german.Execute("set language Deutsch");
            return german;
        });
        ExpirationManager manager = new(storage, TimeSpan.Zero, TimeSpan.FromTicks(1));
        using RecordingLogs logs = RecordingLogs.Use();

        manager.Execute(_cts.Token);

        blocking.Rollback();
        Assert.DoesNotContain(logs.Messages, static x => x.Level >= LogLevel.Warn);
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

    [Fact, CleanDatabase]
    public void Execute_KeepsStatesYoungerThanTheTimeout_WhenItIsUnderAMinute()
    {
        using DbConnection connection = CreateConnection();
        long jobId = CreateJobWithStates(connection, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5), TimeSpan.Zero);
        ExpirationManager manager = CreateManager(TimeSpan.FromSeconds(30));

        manager.Execute(_cts.Token);

        Assert.Equal(new[] { "state-1", "state-2" }, GetStateNames(connection, jobId));
    }

    [Fact, CleanDatabase]
    public void Execute_KeepsTheHistory_WithoutAnError_WhenTheTimeoutIsLongerThanDateAddTakes()
    {
        using DbConnection connection = CreateConnection();
        long jobId = CreateJobWithStateHistory(connection);
        ExpirationManager manager = CreateManager(TimeSpan.MaxValue);
        using RecordingLogs logs = RecordingLogs.Use();

        manager.Execute(_cts.Token);

        Assert.Equal(new[] { "old-state-1", "old-state-2", "current-state" }, GetStateNames(connection, jobId));
        Assert.DoesNotContain(logs.Messages, static x => x.Level >= LogLevel.Warn);
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

    // A job with one state per age, oldest first, named state-0, state-1...; the last one is the current state
    private static long CreateJobWithStates(DbConnection connection, params TimeSpan[] ages)
    {
        long jobId = connection.ExecuteScalar<long>(
            $"insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, StateName, CreatedAt) values (N'', N'', N'', getutcdate()); select scope_identity();");
        long stateId = 0;
        for (int i = 0; i < ages.Length; i++)
        {
            stateId = connection.ExecuteScalar<long>(
                $"insert into [{Constants.DefaultSchema}].State (JobId, Name, CreatedAt) values (@jobId, @name, @createdAt); select scope_identity();",
                new { jobId, name = $"state-{i}", createdAt = DateTime.UtcNow - ages[i] });
        }

        connection.Execute($"update [{Constants.DefaultSchema}].Job set StateId = @stateId where Id = @jobId", new { stateId, jobId });
        return jobId;
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

    /// <summary>Keeps what Masterly.NightShift logs while in use. Test assemblies run one test at a time.</summary>
    private sealed class RecordingLogs : ILogProvider, ILog, IDisposable
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _messages = new();

        public IReadOnlyCollection<(LogLevel Level, string Message)> Messages => _messages;

        public static RecordingLogs Use()
        {
            RecordingLogs logs = new();
            GlobalConfiguration.Configuration.UseLogProvider(logs);
            return logs;
        }

        public ILog GetLogger(string name) => this;

        public bool Log(LogLevel logLevel, Func<string> messageFunc, Exception exception = null)
        {
            // Without a message, the call only asks whether the level is enabled
            if (messageFunc != null)
            {
                _messages.Enqueue((logLevel, messageFunc()));
            }

            return true;
        }

        public void Dispose() => GlobalConfiguration.Configuration.UseNoOpLogProvider();
    }
}
