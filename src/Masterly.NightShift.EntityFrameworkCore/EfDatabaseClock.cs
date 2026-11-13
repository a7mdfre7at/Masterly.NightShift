using System.Data.Common;
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
/// without asking the database. A statement is never trusted unseen, though: the first read checks what it
/// answers. A statement this project's tests don't run against a real database (every one but SQL Server's, and
/// any statement from the options) must answer a time within <see cref="Tolerance"/> of this process's clock:
/// one that answers local time instead of UTC is off by an hour or more nearly everywhere.
/// </para>
/// <para>
/// What the check finds holds for as long as the storage lives, and is found before any time is handed out, so
/// a process never changes clocks while it runs. A statement the database refuses, or whose answer isn't the UTC
/// time, is logged once as a warning, and every read answers this process's clock from then on, as it did
/// before the storage read the database's. Once the statement passed, a read that fails throws, as any other
/// read of the storage does. A check the database didn't answer (it is out of reach, or the statement never got
/// to it) decides nothing: the failure surfaces, and the next read checks again.
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

    private const string OptionName = $"{nameof(EntityFrameworkCoreStorageOptions)}.{nameof(EntityFrameworkCoreStorageOptions.DatabaseClockSql)}";
    private const string WhatToSet = $"set {OptionName} to a statement that answers the database's current UTC time as a single value";
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(EntityFrameworkCoreStorage));

    private readonly EntityFrameworkCoreStorage _storage;
    private readonly string _provider;
    private readonly Statement? _statement;
    private readonly bool _processClockIsTheDatabases;
    private Finding? _finding;

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
    /// The database's time where it is read with a statement, one statement on a context of the storage; this
    /// process's clock otherwise (SQLite, a provider without a statement, a statement that didn't pass the check).
    /// </summary>
    /// <exception cref="Exception">The database's clock is read, and reading it failed.</exception>
    public DateTime UtcNow()
    {
        if (_statement == null)
        {
            return JobClock.UtcNow;
        }

        Finding? finding = Volatile.Read(ref _finding);
        if (finding == null)
        {
            return CheckAndRead(_statement);
        }

        return finding.UseTheDatabase ? Read(_statement.Sql) : JobClock.UtcNow;
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

    /// <summary>
    /// The first read: checks the statement, and answers the time by what the check found. Not under a lock:
    /// against a database out of reach, callers would wait for each other's timeouts. Of those that check at
    /// once, the first to finish decides for all, and only its finding is logged.
    /// </summary>
    private DateTime CheckAndRead(Statement statement)
    {
        Finding found = Check(statement, out DateTime databaseNow);
        Finding decided = Interlocked.CompareExchange(ref _finding, found, null) ?? found;

        if (ReferenceEquals(decided, found))
        {
            found.Log();
        }

        if (!decided.UseTheDatabase)
        {
            return JobClock.UtcNow;
        }

        // The time this check read, unless it found against a statement that another check passed
        return found.UseTheDatabase ? databaseNow : Read(statement.Sql);
    }

    private Finding Check(Statement statement, out DateTime databaseNow)
    {
        string sql = statement.Sql;
        string provider = _provider;
        string consequence = $"This process tells the time with its own clock for as long as the storage lives, so keep the clocks of the processes that share the storage synchronized, or {WhatToSet}.";
        object? value;
        databaseNow = default;

        try
        {
            using DbContext context = _storage.CreateContext();
            value = ExecuteScalar(context, sql);
        }
        catch (Exception ex) when (IsFromTheDatabase(ex) && CanConnect())
        {
            // The database refused the statement, and can be reached: the statement is at fault. Any other
            // failure says nothing about the statement, and surfaces.
            return new Finding(false, () => Logger.WarnException(
                $"The Entity Framework Core job storage couldn't read the database's clock with \"{sql}\" (provider '{provider}'). {consequence}", ex));
        }

        if (!TryConvert(value, out DateTime answered))
        {
            string description = Describe(value);
            return new Finding(false, () => Logger.Warn(
                $"The Entity Framework Core job storage asked the database's clock with \"{sql}\" (provider '{provider}') and got {description}, which isn't a date and time. {consequence}"));
        }

        DateTime processNow = JobClock.UtcNow;
        if (!statement.Verified && (answered - processNow).Duration() > Tolerance)
        {
            return new Finding(false, () => Logger.Warn(
                $"The Entity Framework Core job storage asked the database's clock with \"{sql}\" (provider '{provider}') and got {answered:u}, while this process's clock reads {processNow:u}: either the statement doesn't answer UTC, or one of the two clocks is wrong by more than {Tolerance.TotalMinutes:0} minutes. {consequence}"));
        }

        databaseNow = answered;
        return new Finding(true, () => Logger.Debug($"The Entity Framework Core job storage reads the database's clock with \"{sql}\" (provider '{provider}')."));
    }

    private DateTime Read(string sql)
    {
        using DbContext context = _storage.CreateContext();
        object? value = ExecuteScalar(context, sql);

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

    /// <summary>What the check found: whether the statement tells the database's time, and what to log about it.</summary>
    private sealed record Finding(bool UseTheDatabase, Action Log);
}
