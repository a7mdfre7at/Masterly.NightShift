using Masterly.NightShift.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Masterly.NightShift.EntityFrameworkCore;

/// <summary>
/// Reads the clock of the database behind a storage, so the processes that share the storage tell the time with
/// one clock instead of each with its own. Providers have no common way to ask for it, so the statement comes
/// from <see cref="EntityFrameworkCoreStorageOptions.DatabaseClockSql"/> or, for the providers listed in
/// <see cref="BuiltIn"/>, from here.
/// </summary>
/// <remarks>
/// A statement is never trusted unseen: the first time the clock is asked for, the statement is run once, and the
/// clock is only used from then on when it answered a time. A statement this project's tests don't run against a
/// real database (every one but SQL Server's and SQLite's, and any statement from the options) must also answer
/// a time within <see cref="Tolerance"/> of this process's clock: one that answers local time instead of UTC is
/// off by an hour or more nearly everywhere. What the check finds holds for as long as the storage lives, so a
/// process never changes clocks while it runs: once the database's clock is in use, a read that fails throws,
/// as any other read of the storage does. Only a database that can't be reached at all leaves the question open:
/// the failure surfaces, and the next call checks again.
/// </remarks>
internal sealed class EfDatabaseClock(EntityFrameworkCoreStorage storage, Func<DbContext, string?>? providerNameOf = null)
{
    /// <summary>
    /// How far from this process's clock a statement this project's tests don't run may answer and still be
    /// taken for the UTC time. Clocks that differ by more are wrong on one side, and can't be told from a
    /// statement that answers local time.
    /// </summary>
    internal static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(30);

    private const string OptionName = $"{nameof(EntityFrameworkCoreStorageOptions)}.{nameof(EntityFrameworkCoreStorageOptions.DatabaseClockSql)}";
    private const string WhatToSet = $"set {OptionName} to a statement that answers the database's current UTC time as a single value";

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(EntityFrameworkCoreStorage));

    // By default, the name the provider gives itself. Replaceable for tests: only two providers are referenced there.
    private readonly Func<DbContext, string?> _providerNameOf = providerNameOf ?? (static context => context.Database.ProviderName);
    private Finding? _finding;

    /// <summary>
    /// Whether the database's clock is read. The first call asks the database; later calls answer from memory.
    /// </summary>
    /// <exception cref="Exception">The database couldn't be reached to find out, with the provider's exception.</exception>
    public bool IsRead() => Find().Sql != null;

    /// <summary>The database's time where it is read, this process's otherwise.</summary>
    /// <exception cref="Exception">The database's clock is read, and reading it failed.</exception>
    public DateTime UtcNow() => TryRead(out DateTime now) ? now : JobClock.UtcNow;

    /// <summary>Reads the database's clock, with one statement on a context of the storage.</summary>
    /// <returns>False when the database's clock isn't read: the provider is unknown, or its statement didn't pass the check.</returns>
    /// <exception cref="Exception">The database's clock is read, and reading it failed.</exception>
    public bool TryRead(out DateTime utcNow)
    {
        string? sql = Find().Sql;
        if (sql == null)
        {
            utcNow = default;
            return false;
        }

        using DbContext context = storage.CreateContext();
        object? value = ExecuteScalar(context, sql);
        if (!TryConvert(value, out utcNow))
        {
            throw new InvalidOperationException($"The statement that reads the database's clock, \"{sql}\", answered {Describe(value)}, which isn't a date and time.");
        }

        return true;
    }

    /// <summary>
    /// The statement that answers the UTC time on the database of a provider, by the name the provider gives
    /// itself (<c>DbContext.Database.ProviderName</c>), with fractions of a second where the database has them;
    /// <c>null</c> for a provider that isn't listed.
    /// </summary>
    internal static Statement? BuiltIn(string? providerName)
    {
        return providerName switch
        {
            "Microsoft.EntityFrameworkCore.SqlServer" => new Statement("select sysutcdatetime()", Verified: true),

            // SQLite runs in this process and reads this machine's clock; 'now' is UTC, %f seconds with milliseconds
            "Microsoft.EntityFrameworkCore.Sqlite" => new Statement("select strftime('%Y-%m-%d %H:%M:%f', 'now')", Verified: true),

            // clock_timestamp() is the time of the call, where now() is the time the transaction began. Converted
            // to a timestamp without a zone, so its value doesn't depend on how the driver reads time zones.
            "Npgsql.EntityFrameworkCore.PostgreSQL" => new Statement("select clock_timestamp() at time zone 'utc'", Verified: false),

            // MySQL and MariaDB: with microseconds
            "Pomelo.EntityFrameworkCore.MySql" or "MySql.EntityFrameworkCore" => new Statement("select utc_timestamp(6)", Verified: false),
            "Oracle.EntityFrameworkCore" => new Statement("select sys_extract_utc(systimestamp) from dual", Verified: false),
            _ => null,
        };
    }

    /// <summary>
    /// Reads what a provider answers for a time as UTC: a date and time (local ones are converted, the others
    /// are UTC as the statement promises), one with an offset, or text, as SQLite answers.
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

    private Finding Find()
    {
        Finding? known = Volatile.Read(ref _finding);
        if (known != null)
        {
            return known;
        }

        // Not under a lock: against a database out of reach, callers would wait for each other's timeouts. Of
        // those that check at once, the first to finish decides, and only its finding is logged.
        Finding found = Check();
        Finding? first = Interlocked.CompareExchange(ref _finding, found, null);
        if (first != null)
        {
            return first;
        }

        found.Log?.Invoke();
        return found;
    }

    private Finding Check()
    {
        using DbContext context = storage.CreateContext();
        string provider = _providerNameOf(context) ?? "(unknown)";
        string? configured = storage.Options.DatabaseClockSql;
        Statement? statement = configured != null ? new Statement(configured, Verified: false) : BuiltIn(provider);

        if (statement == null)
        {
            return new Finding(null, () => Logger.Info(
                $"The Entity Framework Core job storage doesn't know how to read the clock of the database of provider '{provider}', so this process tells the time with its own clock; keep the clocks of the processes that share the storage synchronized. To use the database's clock instead, {WhatToSet}."));
        }

        string sql = statement.Sql;
        string consequence = $"This process tells the time with its own clock, so keep the clocks of the processes that share the storage synchronized, or {WhatToSet}.";
        object? value;

        try
        {
            value = ExecuteScalar(context, sql);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && CanConnect())
        {
            // The database answers, but not to this statement
            return new Finding(null, () => Logger.WarnException(
                $"The Entity Framework Core job storage couldn't read the database's clock with \"{sql}\" (provider '{provider}'). {consequence}", ex));
        }

        if (!TryConvert(value, out DateTime databaseNow))
        {
            string answered = Describe(value);
            return new Finding(null, () => Logger.Warn(
                $"The Entity Framework Core job storage asked the database's clock with \"{sql}\" (provider '{provider}') and got {answered}, which isn't a date and time. {consequence}"));
        }

        DateTime processNow = JobClock.UtcNow;
        if (!statement.Verified && (databaseNow - processNow).Duration() > Tolerance)
        {
            return new Finding(null, () => Logger.Warn(
                $"The Entity Framework Core job storage asked the database's clock with \"{sql}\" (provider '{provider}') and got {databaseNow:u}, while this process's clock reads {processNow:u}: either the statement doesn't answer UTC, or one of the two clocks is wrong by more than {Tolerance.TotalMinutes:0} minutes. {consequence}"));
        }

        return new Finding(sql, () => Logger.Debug($"The Entity Framework Core job storage reads the database's clock with \"{sql}\" (provider '{provider}')."));
    }

    /// <summary>
    /// Whether the database can be reached at all, asked on a new context after a statement failed: a statement
    /// that failed for a database out of reach says nothing about the statement.
    /// </summary>
    private bool CanConnect()
    {
        try
        {
            using DbContext context = storage.CreateContext();
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

    /// <summary>What the check found: the statement to read the clock with, or none, and what to log about it.</summary>
    private sealed record Finding(string? Sql, Action? Log);
}
