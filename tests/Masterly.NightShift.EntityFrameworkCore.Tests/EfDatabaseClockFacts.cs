using System.Collections.Concurrent;
using System.Data.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Masterly.NightShift.EntityFrameworkCore.Tests;

/// <summary>
/// The storage reads the database's clock where it knows how, and says so with the feature. SQLite's clock is
/// the process's own; a statement given in the options makes SQLite a database with a clock of its own here.
/// </summary>
public sealed class EfDatabaseClockFacts : IDisposable
{
    private const string Feature = "Connection.GetUtcDateTime";
    private const string SqliteNow = "select strftime('%Y-%m-%d %H:%M:%f', 'now')";
    private const string UnknownProvider = "Contoso.EntityFrameworkCore.Db";

    private readonly SqliteDatabase _database = new();

    public void Dispose()
    {
        JobClock.TimeProvider = TimeProvider.System;
        _database.Dispose();
    }

    [Fact]
    public void HasFeature_AnswersFromTheConfiguration_AndNeverAsksTheDatabase()
    {
        CountingFactory factory = new(OutOfReach());
        EntityFrameworkCoreStorage storage = new(factory.Create, new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });
        int created = factory.Created;

        Assert.True(storage.HasFeature(Feature));
        Assert.True(storage.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime));
        Assert.Equal(created, factory.Created);
    }

    [Fact]
    public void HasFeature_IsTrue_ForAProviderWithAStatement_WithoutADatabase()
    {
        // No server answers here, and none is asked
        EntityFrameworkCoreStorage storage = new(
            static o => o.UseSqlServer("Server=nightshift-no-such-server.invalid;Database=None;Connect Timeout=1;TrustServerCertificate=True;"),
            new EntityFrameworkCoreStorageOptions { PrepareSchemaIfNecessary = false });

        Assert.True(storage.HasFeature(Feature));
    }

    [Fact]
    public void Sqlite_KeepsTheProcessClock_WithoutAStatement_SoATimeProviderStillDrivesIt()
    {
        CountingFactory factory = new(_database.ConnectionString);
        EntityFrameworkCoreStorage storage = new(factory.Create);
        int created = factory.Created;
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromDays(3));

        DateTime now = ReadClock(storage);

        Assert.Equal(DateTimeKind.Utc, now.Kind);
        Assert.InRange(now, DateTime.UtcNow.AddDays(3).AddSeconds(-5), DateTime.UtcNow.AddDays(3).AddSeconds(5));
        Assert.Equal(created, factory.Created);
        Assert.False(storage.HasFeature(Feature));
    }

    [Fact]
    public void Sqlite_SaysNothingAboutItsClock_InTheLog()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        EntityFrameworkCoreStorage storage = _database.CreateStorage();

        ReadClock(storage);

        Assert.Empty(logs.AboutTheClock());
    }

    [Fact]
    public void SqlServer_HasTheFeature_AndReadsTheDatabasesClock_AsUtc()
    {
        EntityFrameworkCoreStorage storage = SqlServerDatabase.CreateStorage();

        Assert.True(storage.HasFeature(Feature));
        AssertReadsTheMachinesTime(storage);
    }

    [Fact]
    public void SqlServer_ReadsTheDatabasesClock_WhateverTheTimeProviderSays()
    {
        EntityFrameworkCoreStorage storage = SqlServerDatabase.CreateStorage();
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromDays(3));

        AssertReadsTheMachinesTime(storage);
    }

    [Fact]
    public void DatabaseClockSql_MakesTheStorageAskSqlite()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });

        Assert.True(storage.HasFeature(Feature));
        AssertReadsTheMachinesTime(storage);
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
    [InlineData(UnknownProvider)]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite")]
    [InlineData("microsoft.entityframeworkcore.sqlserver")]
    public void BuiltIn_KnowsNoStatement_ForOtherProviders_NorForSqlite(string? provider)
    {
        Assert.Null(EfDatabaseClock.BuiltIn(provider));
    }

    [Theory]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData("Pomelo.EntityFrameworkCore.MySql")]
    [InlineData("MySql.EntityFrameworkCore")]
    [InlineData("Oracle.EntityFrameworkCore")]
    public void ProviderWithAStatement_HasTheFeature(string provider)
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();

        storage.Clock = new EfDatabaseClock(storage, provider);

        Assert.True(storage.HasFeature(Feature));
    }

    [Fact]
    public void UnknownProvider_WithoutTheOption_KeepsTheProcessClock_AndSaysOnceWhatToConfigure()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        storage.Clock = new EfDatabaseClock(storage, UnknownProvider);

        Assert.False(storage.HasFeature(Feature));
        Assert.False(storage.HasFeature(Feature));
        using IStorageConnection connection = storage.GetConnection();
        Assert.Throws<NotSupportedException>(() => ((JobStorageConnection)connection).GetUtcDateTime());

        (LogLevel level, string message) = Assert.Single(logs.AboutTheClock());
        Assert.Equal(LogLevel.Info, level);
        Assert.Contains(UnknownProvider, message, StringComparison.Ordinal);
        Assert.Contains("DatabaseClockSql", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownProvider_WithTheOption_ReadsTheDatabasesClock()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });
        storage.Clock = new EfDatabaseClock(storage, UnknownProvider);

        Assert.True(storage.HasFeature(Feature));
        AssertReadsTheMachinesTime(storage);
        Assert.Empty(logs.AboutTheClock());
    }

    [Fact]
    public void OneTransientErrorAtTheFirstRead_Surfaces_AndTheNextReadAnswersTheDatabasesClock()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        Fails busy = new(SqliteNow, times: 1, static () => new SqliteException("SQLite Error 5: 'database is locked'.", 5));
        _database.CreateStorage();
        EntityFrameworkCoreStorage storage = new(
            () => new ClockContext(new DbContextOptionsBuilder<ClockContext>().UseSqlite(_database.ConnectionString).AddInterceptors(busy).Options, null),
            new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });

        // This process's clock is seven minutes ahead of the database's (inside the 30 minutes that are tolerated)
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(7));

        // The first read meets a busy database, once: it fails as a read does, and decides nothing
        Assert.Throws<SqliteException>(() => ReadClock(storage));
        Assert.Equal(1, busy.Failed);

        AssertReadsTheMachinesTime(storage);
        Assert.Empty(logs.AboutTheClock());
    }

    [Fact]
    public void ErrorsThatStop_NeverMakeTheProcessClockTheAnswer_HoweverLongTheyLasted()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        Fails busy = new(SqliteNow, times: EfDatabaseClock.FailuresBeforeGivingUp - 1, static () => new SqliteException("SQLite Error 5: 'database is locked'.", 5));
        _database.CreateStorage();
        EntityFrameworkCoreStorage storage = new(
            () => new ClockContext(new DbContextOptionsBuilder<ClockContext>().UseSqlite(_database.ConnectionString).AddInterceptors(busy).Options, null),
            new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });
        storage.Clock.GiveUpAfter = TimeSpan.Zero;
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(7));

        for (int i = 0; i < EfDatabaseClock.FailuresBeforeGivingUp - 1; i++)
        {
            Assert.Throws<SqliteException>(() => ReadClock(storage));
        }

        AssertReadsTheMachinesTime(storage);
        Assert.Empty(logs.AboutTheClock());
    }

    [Fact]
    public void StatementTheDatabaseKeepsRefusing_Surfaces_ThenIsTakenForWrong_AndWarnsOnce()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = "select no_such_function()" });
        storage.Clock.GiveUpAfter = TimeSpan.Zero;
        storage.Clock.RetryEvery = TimeSpan.FromDays(1);
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(7));

        Assert.True(storage.HasFeature(Feature));
        for (int i = 0; i < EfDatabaseClock.FailuresBeforeGivingUp - 1; i++)
        {
            Assert.Throws<SqliteException>(() => ReadClock(storage));
        }

        Assert.Empty(logs.AboutTheClock());

        AssertReadsTheProcessClock(storage, TimeSpan.FromMinutes(7));
        AssertReadsTheProcessClock(storage, TimeSpan.FromMinutes(7));
        Assert.True(storage.HasFeature(Feature));

        (LogLevel level, string message) = Assert.Single(logs.AboutTheClock());
        Assert.Equal(LogLevel.Warn, level);
        Assert.Contains("select no_such_function()", message, StringComparison.Ordinal);
        Assert.Contains("DatabaseClockSql", message, StringComparison.Ordinal);
        Assert.Contains("asked again", message, StringComparison.Ordinal);
    }

    [Fact]
    public void StatementTheDatabaseRefuses_IsNotTakenForWrong_BeforeItWasRefusedForLongEnough()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = "select no_such_function()" });

        // Many refusals within a moment are one trouble of the database, not a wrong statement
        for (int i = 0; i < EfDatabaseClock.FailuresBeforeGivingUp * 3; i++)
        {
            Assert.Throws<SqliteException>(() => ReadClock(storage));
        }

        Assert.Empty(logs.AboutTheClock());
    }

    [Fact]
    public void StatementTheDatabaseKeepsRefusing_OnSqlServer_IsTakenForWrong_AndWarnsOnce()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        EntityFrameworkCoreStorage storage = SqlServerDatabase.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = "select dbo.no_such_function()" });
        storage.Clock.GiveUpAfter = TimeSpan.Zero;
        storage.Clock.RetryEvery = TimeSpan.FromDays(1);
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromMinutes(7));

        for (int i = 0; i < EfDatabaseClock.FailuresBeforeGivingUp - 1; i++)
        {
            Assert.ThrowsAny<DbException>(() => ReadClock(storage));
        }

        AssertReadsTheProcessClock(storage, TimeSpan.FromMinutes(7));
        AssertReadsTheProcessClock(storage, TimeSpan.FromMinutes(7));

        (LogLevel level, string _) = Assert.Single(logs.AboutTheClock());
        Assert.Equal(LogLevel.Warn, level);
    }

    [Fact]
    public void SqlServersOwnStatement_IsNeverGivenUpOn_HoweverOftenItFails()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        string connectionString = SqlServerDatabase.ConnectionString("Storage");
        SqlServerDatabase.CreateStorage();
        Fails failing = new("select sysutcdatetime()", times: EfDatabaseClock.FailuresBeforeGivingUp * 2, static () => new DatabaseTrouble());
        EntityFrameworkCoreStorage storage = new(o => o.UseSqlServer(connectionString).AddInterceptors(failing));
        storage.Clock.GiveUpAfter = TimeSpan.Zero;
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromDays(3));

        for (int i = 0; i < EfDatabaseClock.FailuresBeforeGivingUp * 2; i++)
        {
            Assert.Throws<DatabaseTrouble>(() => ReadClock(storage));
        }

        AssertReadsTheMachinesTime(storage);
        Assert.Empty(logs.AboutTheClock());
    }

    [Fact]
    public void StatementTakenForWrong_IsAskedAgain_AndTheDatabasesClockIsTakenUpForGood_OnceItAnswers()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });
        storage.Clock.RetryEvery = TimeSpan.Zero;

        // This process's clock is three hours off: the statement's answer can't be told from local time
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromHours(3));
        AssertReadsTheProcessClock(storage, TimeSpan.FromHours(3));
        AssertReadsTheProcessClock(storage, TimeSpan.FromHours(3));
        Assert.Equal(LogLevel.Warn, Assert.Single(logs.AboutTheClock()).Level);

        // The clock is set right: the next read finds the statement answering the UTC time
        JobClock.TimeProvider = TimeProvider.System;
        AssertReadsTheMachinesTime(storage);

        (LogLevel level, string message) = logs.AboutTheClock()[^1];
        Assert.Equal(2, logs.AboutTheClock().Count);
        Assert.Equal(LogLevel.Info, level);
        Assert.Contains("again", message, StringComparison.Ordinal);

        // And from here on the database's clock is the answer, whatever this process's clock does
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromHours(3));
        AssertReadsTheMachinesTime(storage);
        Assert.Equal(2, logs.AboutTheClock().Count);
    }

    [Fact]
    public void StatementTakenForWrong_IsNotRun_UntilItIsDueAgain()
    {
        CountingFactory factory = new(_database.ConnectionString);
        _database.CreateStorage();
        EntityFrameworkCoreStorage storage = new(factory.Create, new EntityFrameworkCoreStorageOptions { DatabaseClockSql = "select 42" });

        ReadClock(storage);
        int afterTheFirstRead = factory.Created;
        ReadClock(storage);
        ReadClock(storage);
        Assert.Equal(afterTheFirstRead, factory.Created);

        storage.Clock.RetryEvery = TimeSpan.Zero;
        AssertReadsTheProcessClock(storage, TimeSpan.Zero);
        Assert.Equal(afterTheFirstRead + 1, factory.Created);
    }

    [Theory]
    [InlineData("select strftime('%Y-%m-%d %H:%M:%f', 'now', '+3 hours')")]
    [InlineData("select strftime('%Y-%m-%d %H:%M:%f', 'now', '-1 hours')")]
    [InlineData("select 'soon'")]
    [InlineData("select 42")]
    [InlineData("select null")]
    public void StatementThatDoesNotAnswerTheUtcTime_AnswersWithTheProcessClock_AndWarnsOnce(string sql)
    {
        using RecordingLogs logs = RecordingLogs.Use();
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions { DatabaseClockSql = sql });

        AssertReadsTheProcessClock(storage, TimeSpan.Zero);
        AssertReadsTheProcessClock(storage, TimeSpan.Zero);

        (LogLevel level, string message) = Assert.Single(logs.AboutTheClock());
        Assert.Equal(LogLevel.Warn, level);
        Assert.Contains(sql, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Statement_IsCheckedByTheFirstRead_WhichAnswersWhatItRead_ThenRunOncePerRead()
    {
        CountingFactory factory = new(_database.ConnectionString);
        EntityFrameworkCoreStorage storage = new(factory.Create, new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });
        int created = factory.Created;

        ReadClock(storage);
        Assert.Equal(created + 1, factory.Created);

        ReadClock(storage);
        ReadClock(storage);
        Assert.Equal(created + 3, factory.Created);
    }

    [Fact]
    public void DatabaseOutOfReach_AtTheFirstRead_Surfaces_AndTheNextReadChecksAgain()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        CountingFactory factory = new(OutOfReach());
        EntityFrameworkCoreStorage storage = new(factory.Create, new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });
        using IStorageConnection connection = storage.GetConnection();

        Assert.Throws<SqliteException>(() => ((JobStorageConnection)connection).GetUtcDateTime());
        Assert.Throws<SqliteException>(() => ((JobStorageConnection)connection).GetUtcDateTime());

        factory.ConnectionString = _database.ConnectionString;
        AssertReadsTheMachinesTime(storage);
        Assert.Empty(logs.AboutTheClock());
    }

    [Fact]
    public void FailureThatIsNotTheDatabases_AtTheFirstRead_Surfaces_AndTheNextReadChecksAgain()
    {
        using RecordingLogs logs = RecordingLogs.Use();
        _database.CreateStorage();

        // A failure before the statement reaches the database, as an interceptor or the application may cause
        Fails before = new(SqliteNow, times: EfDatabaseClock.FailuresBeforeGivingUp * 2, static () => new InvalidOperationException("Not the database's."));
        EntityFrameworkCoreStorage storage = new(
            () => new ClockContext(new DbContextOptionsBuilder<ClockContext>().UseSqlite(_database.ConnectionString).AddInterceptors(before).Options, null),
            new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });
        storage.Clock.GiveUpAfter = TimeSpan.Zero;

        for (int i = 0; i < EfDatabaseClock.FailuresBeforeGivingUp * 2; i++)
        {
            Assert.Throws<InvalidOperationException>(() => ReadClock(storage));
        }

        AssertReadsTheMachinesTime(storage);
        Assert.Empty(logs.AboutTheClock());
    }

    [Fact]
    public void FailureAfterTheCheck_Surfaces_AndNeverFallsBackToTheProcessClock()
    {
        CountingFactory factory = new(_database.ConnectionString);
        EntityFrameworkCoreStorage storage = new(factory.Create, new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });
        ReadClock(storage);

        factory.ConnectionString = OutOfReach();
        using IStorageConnection connection = storage.GetConnection();

        Assert.Throws<SqliteException>(() => ((JobStorageConnection)connection).GetUtcDateTime());
        Assert.Throws<SqliteException>(() => ((JobStorageConnection)connection).GetUtcDateTime());
        Assert.Throws<SqliteException>(() => connection.Heartbeat("server"));
    }

    [Fact]
    public void ApplicationsContext_OnSqlite_ReadsTheClock_AlsoThroughAStagedClient_WhileItsTransactionIsOpen()
    {
        _database.CreateStorage();
        EntityFrameworkCoreStorage storage = new(CreateSqliteContext, new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });
        Assert.True(storage.HasFeature(Feature));
        AssertReadsTheMachinesTime(storage);

        using ClockContext context = CreateSqliteContext();
        using IDbContextTransaction transaction = context.Database.BeginTransaction();
        context.Set<SetRow>().Add(new SetRow { Key = "set", Value = "a" });
        context.SaveChanges();

        AssertStagedClientReadsTheClock(storage, context, hasTheFeature: true);
    }

    [Fact]
    public void ApplicationsContext_OnSqlite_WithoutAStatement_AnswersTheProcessClock_AlsoThroughAStagedClient()
    {
        _database.CreateStorage();
        EntityFrameworkCoreStorage storage = new(CreateSqliteContext);
        using ClockContext context = CreateSqliteContext();

        AssertStagedClientReadsTheClock(storage, context, hasTheFeature: false);
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

        AssertStagedClientReadsTheClock(storage, context, hasTheFeature: true);
    }

    [Fact]
    public void ContextsOnAConnectionWithATransaction_ReadTheClockInIt_OnSqlite()
    {
        _database.CreateStorage();
        using SqliteConnection connection = new(_database.ConnectionString);
        connection.Open();
        using DbTransaction transaction = connection.BeginTransaction();

        EntityFrameworkCoreStorage storage = new(
            () => InTransaction(new ClockContext(new DbContextOptionsBuilder<ClockContext>().UseSqlite(connection).Options, null), transaction),
            new EntityFrameworkCoreStorageOptions { DatabaseClockSql = SqliteNow });

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

        AssertReadsTheMachinesTime(storage);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    public void Heartbeats_AreWrittenAndTimedOut_WithTheDatabasesClock(int secondsAhead)
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions
        {
            DatabaseClockSql = $"select strftime('%Y-%m-%d %H:%M:%f', 'now', '{secondsAhead:+0;-0} seconds')",
        });
        using IStorageConnection connection = storage.GetConnection();

        connection.AnnounceServer("server", new ServerContext { Queues = ["default"], WorkerCount = 1 });
        DateTime announced = Heartbeat(storage);
        connection.Heartbeat("server");
        DateTime beaten = Heartbeat(storage);

        Assert.InRange(announced, DateTime.UtcNow.AddSeconds(secondsAhead - 30), DateTime.UtcNow.AddSeconds(secondsAhead + 30));
        Assert.InRange(beaten, announced, DateTime.UtcNow.AddSeconds(secondsAhead + 30));

        // By this process's clock the heartbeat is 90 seconds off; by the clock it was written with, it is new
        Assert.Equal(0, connection.RemoveTimedOutServers(TimeSpan.FromSeconds(30)));
        Assert.Single(storage.GetMonitoringApi().Servers());
    }

    [Fact]
    public void RemoveTimedOutServers_TellsASilentServer_WithTheDatabasesClock()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage(new EntityFrameworkCoreStorageOptions
        {
            DatabaseClockSql = "select strftime('%Y-%m-%d %H:%M:%f', 'now', '+90 seconds')",
        });
        using IStorageConnection connection = storage.GetConnection();
        connection.AnnounceServer("server", new ServerContext { Queues = ["default"], WorkerCount = 1 });

        // Silent for a minute by the database's clock, though this process's clock hasn't reached its heartbeat yet
        DateTime silentSince = DateTime.UtcNow.AddSeconds(30);
        using (DbContext context = storage.CreateContext())
        {
            context.Set<ServerRow>().ExecuteUpdate(s => s.SetProperty(x => x.LastHeartbeat, silentSince));
        }

        Assert.Equal(0, connection.RemoveTimedOutServers(TimeSpan.FromMinutes(5)));
        Assert.Equal(1, connection.RemoveTimedOutServers(TimeSpan.FromSeconds(30)));
        Assert.Empty(storage.GetMonitoringApi().Servers());
    }

    [Fact]
    public void Heartbeats_AreWrittenAndTimedOut_WithTheProcessClock_OnSqlite()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        JobClock.TimeProvider = new ShiftedClock(TimeSpan.FromDays(3));
        using IStorageConnection connection = storage.GetConnection();

        connection.AnnounceServer("server", new ServerContext { Queues = ["default"], WorkerCount = 1 });
        connection.Heartbeat("server");

        Assert.InRange(Heartbeat(storage), DateTime.UtcNow.AddDays(3).AddSeconds(-30), DateTime.UtcNow.AddDays(3).AddSeconds(30));
        Assert.Equal(0, connection.RemoveTimedOutServers(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, connection.RemoveTimedOutServers(TimeSpan.Zero));
    }

    [Fact]
    public void Heartbeats_AreWrittenAndTimedOut_WithTheProcessClock_OnAProviderWithoutAStatement()
    {
        EntityFrameworkCoreStorage storage = _database.CreateStorage();
        storage.Clock = new EfDatabaseClock(storage, UnknownProvider);
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

    // The process's clock is moved by a known time in these tests, so it can be told from the machine's
    private static void AssertReadsTheProcessClock(EntityFrameworkCoreStorage storage, TimeSpan shift)
    {
        DateTime now = ReadClock(storage);

        Assert.Equal(DateTimeKind.Utc, now.Kind);
        Assert.InRange(now, DateTime.UtcNow.Add(shift).AddSeconds(-5), DateTime.UtcNow.Add(shift).AddSeconds(5));
    }

    private static void AssertStagedClientReadsTheClock(EntityFrameworkCoreStorage storage, DbContext context, bool hasTheFeature)
    {
        StagingStorage staging = new(storage, context, new StagingSubscription(storage));
        Assert.Equal(hasTheFeature, staging.HasFeature(Feature));

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

    /// <summary>The system clock, moved by a fixed time.</summary>
    private sealed class ShiftedClock(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }

    /// <summary>An error of a database or its driver that passes: a busy database, a lost deadlock, a failover.</summary>
    private sealed class DatabaseTrouble() : DbException("The database is busy.");

    /// <summary>Fails the first executions of a statement as a database in trouble does, then lets it through.</summary>
    private sealed class Fails(string sql, int times, Func<Exception> error) : DbCommandInterceptor
    {
        private int _failed;

        public int Failed => Volatile.Read(ref _failed);

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            if (command.CommandText == sql && Interlocked.Increment(ref _failed) <= times)
            {
                throw error();
            }

            return result;
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
