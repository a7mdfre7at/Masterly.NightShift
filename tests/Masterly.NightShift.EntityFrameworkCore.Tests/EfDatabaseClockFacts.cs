using System.Collections.Concurrent;
using System.Data.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Masterly.NightShift.EntityFrameworkCore.Tests;

/// <summary>The storage reads the database's clock where it knows how, and says so with the feature.</summary>
public sealed class EfDatabaseClockFacts : IDisposable
{
    private const string Feature = "Connection.GetUtcDateTime";
    private const string SqliteNow = "select strftime('%Y-%m-%d %H:%M:%f', 'now')";

    private readonly SqliteDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void Sqlite_HasTheFeature_AndReadsTheDatabasesClock_AsUtc()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();

        Assert.True(storage.HasFeature(Feature));
        Assert.True(storage.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime));
        AssertReadsTheMachinesTime(storage);
    }

    [Fact]
    public void SqlServer_HasTheFeature_AndReadsTheDatabasesClock_AsUtc()
    {
        EntityFrameworkCoreStorage storage = SqlServerDatabase.CreateStorage();

        Assert.True(storage.HasFeature(Feature));
        AssertReadsTheMachinesTime(storage);
    }

    [Fact]
    public void DatabaseClockSql_ReplacesTheProvidersStatement_OnSqlite()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions
        {
            DatabaseClockSql = "select strftime('%Y-%m-%d %H:%M:%f', 'now', '+90 seconds')",
        });

        Assert.True(storage.HasFeature(Feature));
        DateTime now = ReadClock(storage);
        Assert.Equal(DateTimeKind.Utc, now.Kind);
        Assert.InRange(now, DateTime.UtcNow.AddSeconds(60), DateTime.UtcNow.AddSeconds(120));
    }

    [Fact]
    public void DatabaseClockSql_ReplacesTheProvidersStatement_OnSqlServer()
    {
        EntityFrameworkCoreStorage storage = SqlServerDatabase.CreateStorage(new EntityFrameworkCoreStorageOptions
        {
            DatabaseClockSql = "select dateadd(second, 90, sysutcdatetime())",
        });

        Assert.True(storage.HasFeature(Feature));
        DateTime now = ReadClock(storage);
        Assert.Equal(DateTimeKind.Utc, now.Kind);
        Assert.InRange(now, DateTime.UtcNow.AddSeconds(60), DateTime.UtcNow.AddSeconds(120));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Ctor_RefusesAnEmptyDatabaseClockSql(string sql)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = sql }));

        Assert.Equal("options", exception.ParamName);
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "select sysutcdatetime()", true)]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", SqliteNow, true)]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "select clock_timestamp() at time zone 'utc'", false)]
    [InlineData("Pomelo.EntityFrameworkCore.MySql", "select date_format(utc_timestamp(6), '%Y-%m-%d %H:%i:%s.%f')", false)]
    [InlineData("MySql.EntityFrameworkCore", "select date_format(utc_timestamp(6), '%Y-%m-%d %H:%i:%s.%f')", false)]
    [InlineData("Oracle.EntityFrameworkCore", "select sys_extract_utc(systimestamp) from dual", false)]
    public void BuiltIn_KnowsTheStatementOfAProvider_AndWhetherItsTestsRanIt(string provider, string sql, bool verified)
    {
        EfDatabaseClock.Statement? statement = EfDatabaseClock.BuiltIn(provider);

        Assert.NotNull(statement);
        Assert.Equal(sql, statement.Sql);
        Assert.Equal(verified, statement.Verified);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Contoso.EntityFrameworkCore.Db")]
    [InlineData("microsoft.entityframeworkcore.sqlite")]
    public void BuiltIn_KnowsNoStatement_ForOtherProviders(string? provider)
    {
        Assert.Null(EfDatabaseClock.BuiltIn(provider));
    }

    [Fact]
    public void UnknownProvider_WithoutTheOption_KeepsTheProcessClock_AndSaysOnceWhatToConfigure()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        storage.Clock = new EfDatabaseClock(storage, static _ => "Contoso.EntityFrameworkCore.Db");

        Assert.False(storage.HasFeature(Feature));
        Assert.False(storage.HasFeature(Feature));
        using IStorageConnection connection = storage.GetConnection();
        Assert.Throws<NotSupportedException>(() => ((JobStorageConnection)connection).GetUtcDateTime());

        (LogLevel level, string message) = Assert.Single(logs.AboutTheClock());
        Assert.Equal(LogLevel.Info, level);
        Assert.Contains("Contoso.EntityFrameworkCore.Db", message, StringComparison.Ordinal);
        Assert.Contains("DatabaseClockSql", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownProvider_WithTheOption_ReadsTheDatabasesClock()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });
        storage.Clock = new EfDatabaseClock(storage, static _ => "Contoso.EntityFrameworkCore.Db");

        Assert.True(storage.HasFeature(Feature));
        AssertReadsTheMachinesTime(storage);
    }

    [Fact]
    public void StatementThatFails_KeepsTheProcessClock_AndWarnsOnce()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = "select no_such_function()" });

        Assert.False(storage.HasFeature(Feature));
        Assert.False(storage.HasFeature(Feature));
        using IStorageConnection connection = storage.GetConnection();
        Assert.Throws<NotSupportedException>(() => ((JobStorageConnection)connection).GetUtcDateTime());

        (LogLevel level, string message) = Assert.Single(logs.AboutTheClock());
        Assert.Equal(LogLevel.Warn, level);
        Assert.Contains("select no_such_function()", message, StringComparison.Ordinal);
        Assert.Contains("DatabaseClockSql", message, StringComparison.Ordinal);
    }

    [Fact]
    public void StatementThatFails_OnSqlServer_KeepsTheProcessClock_AndWarnsOnce()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        EntityFrameworkCoreStorage storage = SqlServerDatabase.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = "select dbo.no_such_function()" });

        Assert.False(storage.HasFeature(Feature));
        Assert.False(storage.HasFeature(Feature));

        (LogLevel level, string _) = Assert.Single(logs.AboutTheClock());
        Assert.Equal(LogLevel.Warn, level);
    }

    [Theory]
    [InlineData("select strftime('%Y-%m-%d %H:%M:%f', 'now', '+3 hours')")]
    [InlineData("select strftime('%Y-%m-%d %H:%M:%f', 'now', '-1 hours')")]
    [InlineData("select 'soon'")]
    [InlineData("select 42")]
    [InlineData("select null")]
    public void StatementThatDoesNotAnswerTheUtcTime_KeepsTheProcessClock_AndWarnsOnce(string sql)
    {
        using RecordingLogs logs = RecordingLogs.Use();
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = sql });

        Assert.False(storage.HasFeature(Feature));
        Assert.False(storage.HasFeature(Feature));

        (LogLevel level, string message) = Assert.Single(logs.AboutTheClock());
        Assert.Equal(LogLevel.Warn, level);
        Assert.Contains(sql, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clock_IsCheckedOnce_ThenReadOncePerCall()
    {
        CountingFactory factory = new(_database.ConnectionString);
        EntityFrameworkCoreStorage storage = new(factory.Create);
        int created = factory.Created;

        Assert.True(storage.HasFeature(Feature));
        int afterTheCheck = factory.Created;
        Assert.True(storage.HasFeature(Feature));
        Assert.True(storage.HasFeature(Feature));

        Assert.Equal(created + 1, afterTheCheck);
        Assert.Equal(afterTheCheck, factory.Created);

        ReadClock(storage);
        Assert.Equal(afterTheCheck + 1, factory.Created);
    }

    [Fact]
    public void DatabaseOutOfReach_AtTheFirstUse_Surfaces_AndTheClockIsCheckedAgain()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        CountingFactory factory = new(_database.ConnectionString);
        EntityFrameworkCoreStorage storage = new(factory.Create);

        factory.ConnectionString = OutOfReach();
        Assert.Throws<SqliteException>(() => storage.HasFeature(Feature));
        using (IStorageConnection connection = storage.GetConnection())
        {
            Assert.Throws<SqliteException>(() => ((JobStorageConnection)connection).GetUtcDateTime());
        }

        factory.ConnectionString = _database.ConnectionString;
        Assert.True(storage.HasFeature(Feature));
        AssertReadsTheMachinesTime(storage);
        Assert.Empty(logs.AboutTheClock());
    }

    [Fact]
    public void FailureAfterTheCheck_Surfaces_AndNeverFallsBackToTheProcessClock()
    {
        CountingFactory factory = new(_database.ConnectionString);
        EntityFrameworkCoreStorage storage = new(factory.Create);
        Assert.True(storage.HasFeature(Feature));

        factory.ConnectionString = OutOfReach();
        using IStorageConnection connection = storage.GetConnection();

        Assert.Throws<SqliteException>(() => ((JobStorageConnection)connection).GetUtcDateTime());
        Assert.True(storage.HasFeature(Feature));
        Assert.Throws<SqliteException>(() => connection.Heartbeat("server"));
    }

    [Fact]
    public void ApplicationsContext_OnSqlite_ReadsTheClock_AlsoThroughAStagedClient_WhileItsTransactionIsOpen()
    {
        _database.CreateStorage();
        EntityFrameworkCoreStorage storage = new(CreateSqliteContext);
        Assert.True(storage.HasFeature(Feature));
        AssertReadsTheMachinesTime(storage);

        using ClockContext context = CreateSqliteContext();
        using IDbContextTransaction transaction = context.Database.BeginTransaction();
        context.Set<SetRow>().Add(new SetRow { Key = "set", Value = "a" });
        context.SaveChanges();

        AssertStagedClientReadsTheClock(storage, context);
    }

    [Fact]
    public void ApplicationsContext_OnSqlServer_ReadsTheClock_AlsoThroughAStagedClient_WhileItsTransactionIsOpen()
    {
        string connectionString = SqlServerDatabase.ConnectionString("Storage");
        SqlServerDatabase.CreateStorage();
        EntityFrameworkCoreStorage storage = new(() => CreateSqlServerContext(o => o.UseSqlServer(connectionString)));
        Assert.True(storage.HasFeature(Feature));
        AssertReadsTheMachinesTime(storage);

        using ClockContext context = CreateSqlServerContext(o => o.UseSqlServer(connectionString));
        using IDbContextTransaction transaction = context.Database.BeginTransaction();
        context.Set<SetRow>().Add(new SetRow { Key = "set", Value = "a" });
        context.SaveChanges();

        AssertStagedClientReadsTheClock(storage, context);
    }

    [Fact]
    public void ContextsOnAConnectionWithATransaction_ReadTheClockInIt_OnSqlite()
    {
        _database.CreateStorage();
        using SqliteConnection connection = new(_database.ConnectionString);
        connection.Open();
        using DbTransaction transaction = connection.BeginTransaction();

        EntityFrameworkCoreStorage storage = new(() => InTransaction(new ClockContext(new DbContextOptionsBuilder<ClockContext>().UseSqlite(connection).Options, null), transaction));

        Assert.True(storage.HasFeature(Feature));
        AssertReadsTheMachinesTime(storage);
    }

    [Fact]
    public void ContextsOnAConnectionWithATransaction_ReadTheClockInIt_OnSqlServer()
    {
        string connectionString = SqlServerDatabase.ConnectionString("Storage");
        SqlServerDatabase.CreateStorage();
        using SqlConnection connection = new(connectionString);
        connection.Open();
        using DbTransaction transaction = connection.BeginTransaction();

        EntityFrameworkCoreStorage storage = new(() => InTransaction(CreateSqlServerContext(o => o.UseSqlServer(connection)), transaction));

        Assert.True(storage.HasFeature(Feature));
        AssertReadsTheMachinesTime(storage);
    }

    [Fact]
    public void Heartbeats_AreWrittenAndTimedOut_WithTheDatabasesClock()
    {
        // A database whose clock is 90 seconds behind this process's
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions
        {
            DatabaseClockSql = "select strftime('%Y-%m-%d %H:%M:%f', 'now', '-90 seconds')",
        });
        using IStorageConnection connection = storage.GetConnection();

        connection.AnnounceServer("server", new ServerContext { Queues = ["default"], WorkerCount = 1 });
        DateTime announced = Heartbeat(storage);
        connection.Heartbeat("server");
        DateTime beaten = Heartbeat(storage);

        Assert.InRange(announced, DateTime.UtcNow.AddSeconds(-120), DateTime.UtcNow.AddSeconds(-60));
        Assert.InRange(beaten, announced, DateTime.UtcNow.AddSeconds(-60));

        // By this process's clock the heartbeat is 90 seconds old; by the clock it was written with, it is new
        Assert.Equal(0, connection.RemoveTimedOutServers(TimeSpan.FromSeconds(30)));
        Assert.Single(storage.GetMonitoringApi().Servers());
    }

    [Fact]
    public void Heartbeats_AreWrittenAndTimedOut_WithTheProcessClock_WhenTheDatabasesIsNotRead()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        storage.Clock = new EfDatabaseClock(storage, static _ => "Contoso.EntityFrameworkCore.Db");
        using IStorageConnection connection = storage.GetConnection();

        connection.AnnounceServer("server", new ServerContext { Queues = ["default"], WorkerCount = 1 });
        connection.Heartbeat("server");

        Assert.InRange(Heartbeat(storage), DateTime.UtcNow.AddSeconds(-30), DateTime.UtcNow.AddSeconds(30));
        Assert.Equal(0, connection.RemoveTimedOutServers(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, connection.RemoveTimedOutServers(TimeSpan.Zero));
    }

    [Fact]
    public void TryConvert_ReadsWhatProvidersAnswer_AsUtc()
    {
        DateTime expected = new(2026, 11, 13, 10, 11, 12, 345, DateTimeKind.Utc);

        Assert.True(EfDatabaseClock.TryConvert(DateTime.SpecifyKind(expected, DateTimeKind.Unspecified), out DateTime unspecified));
        Assert.True(EfDatabaseClock.TryConvert(expected, out DateTime utc));
        Assert.True(EfDatabaseClock.TryConvert(expected.ToLocalTime(), out DateTime local));
        Assert.True(EfDatabaseClock.TryConvert(new DateTimeOffset(expected).ToOffset(TimeSpan.FromHours(5.5)), out DateTime offset));
        Assert.True(EfDatabaseClock.TryConvert("2026-11-13 10:11:12.345", out DateTime text));
        Assert.True(EfDatabaseClock.TryConvert("2026-11-13 10:11:12.345000", out DateTime textWithMicroseconds));
        Assert.True(EfDatabaseClock.TryConvert("2026-11-13T15:41:12.345+05:30", out DateTime textWithOffset));

        Assert.All(
            (DateTime[])[unspecified, utc, local, offset, text, textWithMicroseconds, textWithOffset],
            x =>
            {
                Assert.Equal(expected, x);
                Assert.Equal(DateTimeKind.Utc, x.Kind);
            });
    }

    [Fact]
    public void TryConvert_RefusesWhatIsNotATime()
    {
        Assert.False(EfDatabaseClock.TryConvert(null, out _));
        Assert.False(EfDatabaseClock.TryConvert(DBNull.Value, out _));
        Assert.False(EfDatabaseClock.TryConvert(42L, out _));
        Assert.False(EfDatabaseClock.TryConvert("soon", out _));
    }

    private static void AssertReadsTheMachinesTime(EntityFrameworkCoreStorage storage)
    {
        DateTime before = DateTime.UtcNow;
        List<DateTime> readings = [.. Enumerable.Range(0, 5).Select(_ => ReadClock(storage))];
        DateTime after = DateTime.UtcNow;

        Assert.All(readings, x => Assert.Equal(DateTimeKind.Utc, x.Kind));

        // The database is on this machine. SQLite answers whole milliseconds, and SQL Server's clock ticks every
        // few milliseconds, so a reading may lie that little before the time this process read first.
        Assert.All(readings, x => Assert.InRange(x, before.AddSeconds(-5), after.AddSeconds(5)));
        Assert.Contains(readings, static x => x.Ticks % TimeSpan.TicksPerSecond != 0);
    }

    private static void AssertStagedClientReadsTheClock(EntityFrameworkCoreStorage storage, DbContext context)
    {
        StagingStorage staging = new(storage, context);
        Assert.True(staging.HasFeature(Feature));

        using IStorageConnection connection = staging.GetConnection();
        DateTime now = ((JobStorageConnection)connection).GetUtcDateTime();

        Assert.Equal(DateTimeKind.Utc, now.Kind);
        Assert.InRange(now, DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }

    private static DateTime ReadClock(EntityFrameworkCoreStorage storage)
    {
        using IStorageConnection connection = storage.GetConnection();
        return ((JobStorageConnection)connection).GetUtcDateTime();
    }

    private static DateTime Heartbeat(EntityFrameworkCoreStorage storage)
    {
        ServerDto server = Assert.Single(storage.GetMonitoringApi().Servers());
        return Assert.NotNull(server.Heartbeat);
    }

    private static ClockContext InTransaction(ClockContext context, DbTransaction transaction)
    {
        context.Database.UseTransaction(transaction);
        return context;
    }

    // A file in a directory that doesn't exist: SQLite can't open it
    private static string OutOfReach() => $"Data Source={Path.Combine(Path.GetTempPath(), $"nightshift-missing-{Guid.NewGuid():N}", "none.db")}";

    private ClockContext CreateSqliteContext()
    {
        return new ClockContext(new DbContextOptionsBuilder<ClockContext>().UseSqlite(_database.ConnectionString).Options, null);
    }

    private static ClockContext CreateSqlServerContext(Action<DbContextOptionsBuilder<ClockContext>> configure)
    {
        DbContextOptionsBuilder<ClockContext> builder = new();
        configure(builder);
        return new ClockContext(builder.Options, NightShiftModelBuilderExtensions.DefaultSchema);
    }

    /// <summary>An application's context: the storage's tables, in the application's model.</summary>
    private sealed class ClockContext(DbContextOptions<ClockContext> options, string? schema) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyNightShiftModel(schema);
        }
    }

    /// <summary>Creates an application's contexts, on a database that can be changed, and counts them.</summary>
    private sealed class CountingFactory(string connectionString)
    {
        private int _created;

        public string ConnectionString { get; set; } = connectionString;

        public int Created => Volatile.Read(ref _created);

        public DbContext Create()
        {
            Interlocked.Increment(ref _created);
            return new ClockContext(new DbContextOptionsBuilder<ClockContext>().UseSqlite(ConnectionString).Options, null);
        }
    }

    /// <summary>Keeps what Masterly.NightShift logs while in use. Test assemblies run one test at a time.</summary>
    private sealed class RecordingLogs : ILogProvider, ILog, IDisposable
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _messages = new();

        public static RecordingLogs Use()
        {
            RecordingLogs logs = new();
            GlobalConfiguration.Configuration.UseLogProvider(logs);
            return logs;
        }

        /// <summary>What was logged about the database's clock, at information level or above.</summary>
        public IReadOnlyList<(LogLevel Level, string Message)> AboutTheClock()
        {
            return [.. _messages.Where(static x => x.Level >= LogLevel.Info && x.Message.Contains("clock", StringComparison.Ordinal))];
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

        // No logging library is referenced here, so this is what the loggers wrote to before
        public void Dispose() => GlobalConfiguration.Configuration.UseNoOpLogProvider();
    }
}
