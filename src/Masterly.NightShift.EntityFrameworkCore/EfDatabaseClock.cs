using System.Data.Common;
using System.Diagnostics;
using Masterly.NightShift.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Masterly.NightShift.EntityFrameworkCore;

/// <summary>
/// Tells the time of the database behind a storage, so the processes that share the storage go by one clock
/// instead of each by its own. Providers have no common way to ask for it, so the statement comes from
/// <see cref="EntityFrameworkCoreStorageOptions.DatabaseClockSql"/> or, for the providers listed in
/// <see cref="BuiltIn"/>, from here. SQLite runs inside the process and reads the machine's clock, so there the
/// process's clock (<see cref="JobClock"/>) is the database's, and no statement is run.
/// </summary>
/// <remarks>
/// <para>
/// Whether the database's clock is read is known from the configuration alone (<see cref="ReadsTheDatabase"/>),
/// without asking the database. What a read does when the statement fails depends on how well the statement is
/// known, and no failure is ever final.
/// </para>
/// <para>
/// A statement this project's tests run against a real database (SQL Server's) is never given up on: a read that
/// fails throws, as any other read of the storage does, and the next read asks again.
/// </para>
/// <para>
/// Any other statement (the other providers', and every statement from the options) is never trusted unseen.
/// Until it has answered once, a read that the database refuses throws like any other failed read: one error
/// may be a busy database, a lost deadlock or a failover, and says nothing about the statement. Only when the
/// database, in reach, has refused it <see cref="FailuresBeforeGivingUp"/> times, over <see cref="GiveUpAfter"/>
/// at least, is the statement taken for wrong. An answer that isn't a time, or that lies further than
/// <see cref="Tolerance"/> from this process's clock (a statement that answers local time instead of UTC is off
/// by an hour or more nearly everywhere), is taken for wrong at once: the database answered. A statement taken
/// for wrong is logged once as a warning, and reads answer this process's clock, as they did before the storage
/// read the database's. It is asked again every <see cref="RetryEvery"/>, by whoever reads the clock then, and
/// once it answers the UTC time the database's clock is taken up, which is logged.
/// </para>
/// <para>
/// A process changes clocks at most once while a storage lives, from its own to the database's, and never
/// back: once a statement has answered, a read that fails throws. So no caller sees the two clocks in turn.
/// </para>
/// </remarks>
internal sealed class EfDatabaseClock
{
    /// <summary>
    /// How far from this process's clock a statement this project's tests don't run may answer and still be
    /// taken for the UTC time. Clocks that differ by more are wrong on one side, and can't be told from a
    /// statement that answers local time.
    /// </summary>
    internal static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(30);

    /// <summary>How many times in a row the database must refuse a statement that never answered, before it is taken for wrong.</summary>
    internal const int FailuresBeforeGivingUp = 3;

    private const string OptionName = $"{nameof(EntityFrameworkCoreStorageOptions)}.{nameof(EntityFrameworkCoreStorageOptions.DatabaseClockSql)}";
    private const string WhatToSet = $"set {OptionName} to a statement that answers the database's current UTC time as a single value";
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    // What a statement that isn't known to work has shown so far
    private const int Unanswered = 0;
    private const int Answers = 1;
    private const int TakenForWrong = 2;

    // How old a reading may be for ApproximateUtcNow
    private static readonly TimeSpan RecentEnough = TimeSpan.FromSeconds(1);
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(EntityFrameworkCoreStorage));

    private readonly EntityFrameworkCoreStorage _storage;
    private readonly string _provider;
    private readonly Statement? _statement;
    private readonly bool _processClockIsTheDatabases;
    private readonly NightShiftLock _failuresLock = new();
    private int _state;
    private int _failures;
    private long _firstFailureAt;
    private long _askedAgainAt;
    private Reading? _recent;

    /// <summary>
    /// The clock of a storage on the database of a provider, by the name the provider gives itself
    /// (<c>DbContext.Database.ProviderName</c>). A provider without a statement, whose database's clock isn't
    /// this process's either, is logged here, once, as information that says what to configure.
    /// </summary>
    public EfDatabaseClock(EntityFrameworkCoreStorage storage, string? providerName)
    {
        _storage = storage;
        _provider = providerName ?? "(unknown)";

        string? configured = storage.Options.DatabaseClockSql;
        _statement = configured != null ? new Statement(configured, Verified: false) : BuiltIn(providerName);
        _processClockIsTheDatabases = _statement == null && providerName == SqliteProvider;

        if (_statement == null && !_processClockIsTheDatabases)
        {
            Logger.Info(
                $"The Entity Framework Core job storage doesn't know how to read the clock of the database of provider '{_provider}', so this process tells the time with its own clock; keep the clocks of the processes that share the storage synchronized. To use the database's clock instead, {WhatToSet}.");
        }
    }

    /// <summary>
    /// Whether the storage is set up to read the database's clock with a statement: the provider has one, or
    /// the options give one. Known from the configuration; the database isn't asked.
    /// </summary>
    public bool ReadsTheDatabase => _statement != null;

    /// <summary>
    /// Whether <see cref="UtcNow"/> is meant to be the database's time: it is read with a statement, or (SQLite)
    /// it is this process's clock.
    /// </summary>
    public bool TellsTheDatabasesTime => _statement != null || _processClockIsTheDatabases;

    /// <summary>
    /// The shortest time over which the database must have refused a statement that never answered, before it is
    /// taken for wrong: longer than a lock wait or a lost deadlock lasts, short enough that a server with a
    /// wrong statement starts within seconds. Replaceable for tests.
    /// </summary>
    internal TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often a statement taken for wrong is asked again. One cheap statement each time, as often as the
    /// delayed jobs are polled by default: a process that gave up on a statement that works (the database was
    /// in trouble for longer than <see cref="GiveUpAfter"/>) judges leases and heartbeats with its own clock for
    /// that long at most, half the default lifetime of a lock. Replaceable for tests.
    /// </summary>
    internal TimeSpan RetryEvery { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The database's time where it is read with a statement, one statement on a context of the storage; this
    /// process's clock otherwise (SQLite, a provider without a statement, a statement taken for wrong).
    /// </summary>
    /// <exception cref="Exception">The database's clock is read, and reading it failed.</exception>
    public DateTime UtcNow()
    {
        return _statement != null && TryReadTheDatabase(_statement, out DateTime databaseNow) ? databaseNow : JobClock.UtcNow;
    }

    /// <summary>
    /// The database's time as <see cref="UtcNow"/> tells it, from a reading at most a second old, counted on
    /// from there with this process's monotonic clock. For what is judged over minutes and asked for all the
    /// time (the invisibility of fetched jobs, which every waiting worker checks at every poll): one statement a
    /// second for the whole process instead of one per poll. Not for lock leases, which are as short as seconds.
    /// </summary>
    /// <exception cref="Exception">The database's clock is read, and reading it failed.</exception>
    public DateTime ApproximateUtcNow()
    {
        if (_statement == null)
        {
            return JobClock.UtcNow;
        }

        Reading? recent = Volatile.Read(ref _recent);
        if (recent != null)
        {
            TimeSpan age = Stopwatch.GetElapsedTime(recent.At);
            if (age < RecentEnough)
            {
                return recent.UtcNow + age;
            }
        }

        if (!TryReadTheDatabase(_statement, out DateTime databaseNow))
        {
            return JobClock.UtcNow;
        }

        // Only what the database answered is kept: this process's clock is cheap to read, and a test may move it
        Volatile.Write(ref _recent, new Reading(databaseNow, Stopwatch.GetTimestamp()));
        return databaseNow;
    }

    /// <summary>What the clock is, for the log of the storage's options.</summary>
    public string Source()
    {
        return _statement != null
            ? $"the database's, read with \"{_statement.Sql}\""
            : _processClockIsTheDatabases ? "this process's, which is SQLite's" : "this process's";
    }

    /// <summary>
    /// The statement that answers the UTC time on the database of a provider, with fractions of a second;
    /// <c>null</c> for a provider that isn't listed, and for SQLite, whose clock is this process's.
    /// </summary>
    internal static Statement? BuiltIn(string? providerName)
    {
        return providerName switch
        {
            "Microsoft.EntityFrameworkCore.SqlServer" => new Statement("select sysutcdatetime()", Verified: true),

            // clock_timestamp() is the time of the call, where now() is the time the transaction began. Converted
            // to a timestamp without a zone, so its value doesn't depend on how the driver reads time zones.
            "Npgsql.EntityFrameworkCore.PostgreSQL" => new Statement("select clock_timestamp() at time zone 'utc'", Verified: false),

            // MySQL and MariaDB, with microseconds. As text: the drivers read a date and time in ways their
            // connection strings change (stamped as local time, or as a type of their own).
            "Pomelo.EntityFrameworkCore.MySql" or "MySql.EntityFrameworkCore" => new Statement("select date_format(utc_timestamp(6), '%Y-%m-%d %H:%i:%s.%f')", Verified: false),
            "Oracle.EntityFrameworkCore" => new Statement("select sys_extract_utc(systimestamp) from dual", Verified: false),
            _ => null,
        };
    }

    /// <summary>
    /// Reads what a provider answers for a time as UTC: a date and time (local ones are converted, the others
    /// are UTC as the statement promises), one with an offset, or text.
    /// </summary>
    internal static bool TryConvert(object? value, out DateTime utc)
    {
        switch (value)
        {
            case DateTime { Kind: DateTimeKind.Local } local:
                utc = local.ToUniversalTime();
                return true;
            case DateTime dateTime:
                utc = DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
                return true;
            case DateTimeOffset offset:
                utc = offset.UtcDateTime;
                return true;
            case string text:
                return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
            default:
                utc = default;
                return false;
        }
    }

    /// <summary>Reads the database's clock, unless the statement is taken for wrong: then this process's clock is the answer.</summary>
    /// <exception cref="Exception">Reading the database's clock failed, and that doesn't make the statement wrong.</exception>
    private bool TryReadTheDatabase(Statement statement, out DateTime databaseNow)
    {
        if (statement.Verified)
        {
            databaseNow = Read(statement.Sql);
            return true;
        }

        switch (Volatile.Read(ref _state))
        {
            case Answers:
                databaseNow = Read(statement.Sql);
                return true;
            case TakenForWrong:
                return IsDueAgain() ? AskAgain(statement, out databaseNow) : No(out databaseNow);
            default:
                return AskForTheFirstTime(statement, out databaseNow);
        }
    }

    /// <summary>
    /// A read of a statement that has not answered yet. Not under a lock: against a database out of reach,
    /// callers would wait for each other's timeouts.
    /// </summary>
    private bool AskForTheFirstTime(Statement statement, out DateTime databaseNow)
    {
        string sql = statement.Sql;
        string? problem;

        try
        {
            problem = Ask(sql, out databaseNow);
        }
        catch (Exception ex) when (IsFromTheDatabase(ex) && CanConnect())
        {
            // The database refused the statement, and can be reached. Once says nothing: a busy database, a lost
            // deadlock and a failover refuse a statement that works. Any other failure surfaces as it is.
            if (!RefusedOftenEnough())
            {
                throw;
            }

            return GiveUp(sql, $"The Entity Framework Core job storage couldn't read the database's clock with \"{sql}\" (provider '{_provider}'): the database refused the statement {FailuresBeforeGivingUp} times or more.", ex, out databaseNow);
        }

        if (problem != null)
        {
            // The database answered, and not with the UTC time: nothing passing about that
            return GiveUp(sql, problem, null, out databaseNow);
        }

        TakeUp(sql);
        return true;
    }

    /// <summary>A read of a statement taken for wrong, when it is due again: answers this process's clock unless the statement works now.</summary>
    private bool AskAgain(Statement statement, out DateTime databaseNow)
    {
        string? problem;

        try
        {
            problem = Ask(statement.Sql, out databaseNow);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.DebugException($"The Entity Framework Core job storage asked the database's clock again with \"{statement.Sql}\", and it failed again; this process keeps telling the time with its own clock.", ex);
            return No(out databaseNow);
        }

        if (problem != null)
        {
            Logger.Debug($"{problem} This process keeps telling the time with its own clock.");
            return No(out databaseNow);
        }

        TakeUp(statement.Sql);
        return true;
    }

    /// <summary>Runs the statement, and says what is wrong with its answer, or <c>null</c> when it is the UTC time.</summary>
    private string? Ask(string sql, out DateTime databaseNow)
    {
        object? value;
        using (DbContext context = _storage.CreateContext())
        {
            value = ExecuteScalar(context, sql);
        }

        if (!TryConvert(value, out databaseNow))
        {
            return $"The Entity Framework Core job storage asked the database's clock with \"{sql}\" (provider '{_provider}') and got {Describe(value)}, which isn't a date and time.";
        }

        DateTime processNow = JobClock.UtcNow;
        return (databaseNow - processNow).Duration() > Tolerance
            ? $"The Entity Framework Core job storage asked the database's clock with \"{sql}\" (provider '{_provider}') and got {databaseNow:u}, while this process's clock reads {processNow:u}: either the statement doesn't answer UTC, or one of the two clocks is wrong by more than {Tolerance.TotalMinutes:0} minutes."
            : null;
    }

    /// <summary>Counts a refusal of the database, and tells whether there were enough of them, over a long enough time.</summary>
    private bool RefusedOftenEnough()
    {
        lock (_failuresLock)
        {
            if (_failures++ == 0)
            {
                _firstFailureAt = Stopwatch.GetTimestamp();
            }

            return _failures >= FailuresBeforeGivingUp && Stopwatch.GetElapsedTime(_firstFailureAt) >= GiveUpAfter;
        }
    }

    /// <summary>The database's clock is in use from here on, for as long as the storage lives.</summary>
    private void TakeUp(string sql)
    {
        if (Interlocked.Exchange(ref _state, Answers) == TakenForWrong)
        {
            Logger.Info($"The Entity Framework Core job storage reads the database's clock again: \"{sql}\" (provider '{_provider}') answers the UTC time now. This process tells the time with the database's clock from here on.");
        }
        else
        {
            Logger.Debug($"The Entity Framework Core job storage reads the database's clock with \"{sql}\" (provider '{_provider}').");
        }
    }

    /// <summary>
    /// Takes the statement for wrong, unless another read got an answer meanwhile: then the database's clock is
    /// in use, and is read.
    /// </summary>
    private bool GiveUp(string sql, string problem, Exception? exception, out DateTime databaseNow)
    {
        Volatile.Write(ref _askedAgainAt, Stopwatch.GetTimestamp());

        int before = Interlocked.CompareExchange(ref _state, TakenForWrong, Unanswered);
        if (before == Answers)
        {
            databaseNow = Read(sql);
            return true;
        }

        if (before == Unanswered)
        {
            string message = $"{problem} This process tells the time with its own clock until the statement answers the UTC time; it is asked again every {RetryEvery.TotalSeconds:0} seconds. Meanwhile keep the clocks of the processes that share the storage synchronized, or {WhatToSet}.";
            if (exception != null)
            {
                Logger.WarnException(message, exception);
            }
            else
            {
                Logger.Warn(message);
            }
        }

        return No(out databaseNow);
    }

    /// <summary>Whether a statement taken for wrong is due to be asked again; true for one caller at a time.</summary>
    private bool IsDueAgain()
    {
        long last = Volatile.Read(ref _askedAgainAt);
        return Stopwatch.GetElapsedTime(last) >= RetryEvery
            && Interlocked.CompareExchange(ref _askedAgainAt, Stopwatch.GetTimestamp(), last) == last;
    }

    private static bool No(out DateTime databaseNow)
    {
        databaseNow = default;
        return false;
    }

    private DateTime Read(string sql)
    {
        object? value;
        using (DbContext context = _storage.CreateContext())
        {
            value = ExecuteScalar(context, sql);
        }

        return TryConvert(value, out DateTime utcNow)
            ? utcNow
            : throw new InvalidOperationException($"The statement that reads the database's clock, \"{sql}\", answered {Describe(value)}, which isn't a date and time.");
    }

    /// <summary>Whether a failure is an error of the database or its driver, alone or behind the execution strategy's.</summary>
    private static bool IsFromTheDatabase(Exception? exception)
    {
        for (; exception != null; exception = exception.InnerException)
        {
            if (exception is DbException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the database can be reached at all, asked on a new context after a statement failed: a statement
    /// that failed for a database out of reach says nothing about the statement.
    /// </summary>
    private bool CanConnect()
    {
        try
        {
            using DbContext context = _storage.CreateContext();
            return context.Database.CanConnect();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.DebugException("Couldn't find out whether the database can be reached, after reading its clock failed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Runs the statement as the context runs its queries: on its connection, opened and closed around the
    /// statement when it isn't open, in its current transaction when it has one, through the provider's
    /// execution strategy, and logged as its commands are.
    /// </summary>
    private static object? ExecuteScalar(DbContext context, string sql)
    {
        IRelationalCommand command = context.GetService<IRawSqlCommandBuilder>().Build(sql);
        RelationalCommandParameterObject parameters = new(
            context.GetService<IRelationalConnection>(),
            parameterValues: null,
            readerColumns: null,
            context,
            context.GetService<IRelationalCommandDiagnosticsLogger>());

        return context.Database.CreateExecutionStrategy().Execute(() => command.ExecuteScalar(parameters));
    }

    private static string Describe(object? value)
    {
        return value switch
        {
            null or DBNull => "no value",
            string text => $"the text \"{(text.Length > 50 ? text[..50] : text)}\"",
            _ => $"a value of type {value.GetType().Name}",
        };
    }

    /// <summary>A statement that answers the UTC time, and whether this project's tests run it against a real database.</summary>
    internal sealed record Statement(string Sql, bool Verified);

    /// <summary>What the database answered, and when, by this process's monotonic clock.</summary>
    private sealed record Reading(DateTime UtcNow, long At);
}
