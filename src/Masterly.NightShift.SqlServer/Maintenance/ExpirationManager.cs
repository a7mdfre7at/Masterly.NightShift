// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Microsoft.Data.SqlClient;

namespace Masterly.NightShift.SqlServer;

internal sealed class ExpirationManager : IBackgroundProcess
{
    // Prefixed with the schema name, like the other locks, so that storages with different schemas in one
    // database clean up independently
    private const string DistributedLockResource = "locks:expirationmanager";
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMinutes(5);

    // The state cleanup seeks on the [State].[CreatedAt] index, which comes with this schema version
    private const int StateCleanupSchemaVersion = 9;

    private const int LockTimeoutErrorNumber = 1222;

    private static readonly string[] ProcessedTables =
    [
        "AggregatedCounter",
        "Job",
        "List",
        "Set",
        "Hash",
    ];

    private readonly ILog _logger = LogProvider.For<ExpirationManager>();
    private readonly SqlServerStorage _storage;
    private readonly TimeSpan _stateExpirationTimeout;
    private readonly TimeSpan _checkInterval;
    private int _oldSchemaWarningLogged;

    public ExpirationManager(SqlServerStorage storage, TimeSpan stateExpirationTimeout, TimeSpan checkInterval)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (stateExpirationTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stateExpirationTimeout), "Timeout value should be equal to or greater than zero.");
        }

        if (checkInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(checkInterval), "Timeout value should be greater than zero.");
        }

        _storage = storage;
        _stateExpirationTimeout = stateExpirationTimeout;
        _checkInterval = checkInterval;
    }

    public void Execute(CancellationToken cancellationToken)
    {
        ExecuteCore(_storage, cancellationToken);
    }

    public void Execute(BackgroundProcessContext context)
    {
        if (context.Storage is not SqlServerStorage storage)
        {
            return;
        }

        ExecuteCore(storage, context.StoppingToken);
    }

    private void ExecuteCore(SqlServerStorage storage, CancellationToken cancellationToken)
    {
        int numberOfRecordsInSinglePass = storage.EffectiveDeleteBatchSize;

        foreach (string table in ProcessedTables)
        {
            try
            {
                CleanupTable(storage, GetExpireQuery(storage, table), table, numberOfRecordsInSinglePass, cancellationToken);
            }
            catch (DbException ex)
            {
                _logger.ErrorException($"Error occurred while cleaning up the '{table}' table: {ex.Message}", ex);
            }
        }

        if (_stateExpirationTimeout > TimeSpan.Zero && CanCleanUpStates(storage))
        {
            int expireSeconds = -GetStateExpirationSeconds(_stateExpirationTimeout);

            try
            {
                CleanupTable(storage, GetStateCleanupQuery(storage), "State", numberOfRecordsInSinglePass,
                    cancellationToken,
                    command => command.AddParameter("@expireSeconds", expireSeconds, DbType.Int32));
            }
            catch (DbException ex)
            {
                _logger.ErrorException($"Error occurred while cleaning up the 'State' table: {ex.Message}", ex);
            }
        }

        cancellationToken.Wait(_checkInterval);
    }

    public override string ToString()
    {
        return GetType().ToString();
    }

    // On an older schema the query can't run: SQL Server refuses its index hint (error 8622). When the version
    // isn't known (auto-detection is off, or the version couldn't be read yet), the cleanup is tried.
    private bool CanCleanUpStates(SqlServerStorage storage)
    {
        int? version = storage.DetectedSchemaVersion;
        if (version is not < StateCleanupSchemaVersion)
        {
            return true;
        }

        if (Interlocked.Exchange(ref _oldSchemaWarningLogged, 1) == 0)
        {
            _logger.Warn(
                $"InactiveStateExpirationTimeout is set, but the schema is at version {version}: deleting old state history needs the [State].[CreatedAt] index of schema version {StateCleanupSchemaVersion}. The state history isn't cleaned up until the schema is upgraded (PrepareSchemaIfNecessary, or the install script).");
        }

        return false;
    }

    internal static string GetDistributedLockKey(SqlServerStorage storage)
    {
        return $"{storage.SchemaName}:{DistributedLockResource}";
    }

    private void CleanupTable(SqlServerStorage storage, string query, string table, int numberOfRecordsInSinglePass, CancellationToken cancellationToken, Action<DbCommand>? additionalActions = null)
    {
        _logger.Debug($"Removing outdated records from the '{table}' table...");

        UseConnectionDistributedLock(storage, connection =>
        {
            int affected;

            do
            {
                affected = ExecuteNonQuery(
                    connection,
                    query,
                    numberOfRecordsInSinglePass,
                    additionalActions,
                    cancellationToken);
            } while (affected == numberOfRecordsInSinglePass);

            return affected;
        });

        _logger.Trace($"Outdated records removed from the '{table}' table.");
    }

    private T? UseConnectionDistributedLock<T>(SqlServerStorage storage, Func<DbConnection, T> action)
    {
        string lockKey = GetDistributedLockKey(storage);

        try
        {
            return storage.UseConnection(null, static (_, connection, ctx) =>
            {
                // A session lock that may still be held must not go back to the pool with its connection
                try
                {
                    SqlServerDistributedLock.Acquire(connection, ctx.Key, DefaultLockTimeout);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType() && SqlServerDistributedLock.MayHaveBeenGranted(ex))
                {
                    ConnectionPools.DiscardWhenClosed(connection);
                    throw;
                }

                try
                {
                    return ctx.Value(connection);
                }
                finally
                {
                    ReleaseLock(connection, ctx.Key);
                }
            }, new KeyValuePair<string, Func<DbConnection, T>>(lockKey, action));
        }
        catch (DistributedLockTimeoutException e) when (e.Resource == lockKey)
        {
            // DistributedLockTimeoutException here doesn't mean that outdated records weren't removed.
            // It just means another Masterly.NightShift server did this work.
            _logger.Log(
                LogLevel.Debug,
                () => $"""
                    An exception was thrown during acquiring distributed lock on the {lockKey} resource within {DefaultLockTimeout.TotalSeconds} seconds. Outdated records were not removed.
                    It will be retried in {_checkInterval.TotalSeconds} seconds.
                    """,
                e);
            return default;
        }
    }

    private static void ReleaseLock(DbConnection connection, string resource)
    {
        try
        {
            SqlServerDistributedLock.Release(connection, resource);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            ConnectionPools.DiscardWhenClosed(connection);
            throw;
        }
    }

    // Built by interpolation, never by string.Format: the schema name may contain braces
    private static string GetExpireQuery(SqlServerStorage storage, string table)
    {
        // Schema 5, which still should be supported by Masterly.NightShift doesn't have an index that covers
        // the `ExpireAt` column of AggregatedCounter, making it impossible to seek on it.
        string hint = table.Equals("AggregatedCounter", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : " with (forceseek)";

        return $"""
            set deadlock_priority low;
            set transaction isolation level read committed;
            set xact_abort on;
            set lock_timeout 1000;
            delete top (@count) T from [{storage.SchemaName}].[{table}] T{hint}
            where ExpireAt < @now
            option (loop join, optimize for (@count = 20000));
            """;
    }

    // In whole seconds, which dateadd takes as an int: a part of a second counts as one, and anything longer
    // than int.MaxValue seconds (68 years) as that
    private static int GetStateExpirationSeconds(TimeSpan timeout)
    {
        return (int)Math.Ceiling(Math.Min(timeout.TotalSeconds, int.MaxValue));
    }

    private static string GetStateCleanupQuery(SqlServerStorage storage)
    {
        // TODO: Make expiration condition configurable
        return storage.GetQueryFromTemplate(static schemaName => $"""
            set deadlock_priority low;
            set transaction isolation level read committed;
            set xact_abort on;
            set lock_timeout 1000;

            ;with cte as (
            	select s.[JobId], s.[Id]
            	from [{schemaName}].[State] s with (forceseek)
            	where s.[CreatedAt] < dateadd(second, @expireSeconds, @now)
            	and exists (
            		select * from [{schemaName}].[Job] j with (forceseek)
            		where j.[Id] = s.[JobId] and j.[StateId] != s.[Id]))
            delete top(@count) from cte option (maxdop 1);
            """);
    }

    private static int ExecuteNonQuery(
        DbConnection connection,
        string commandText,
        int numberOfRecordsInSinglePass,
        Action<DbCommand>? additionalActions,
        CancellationToken cancellationToken)
    {
        using DbCommand command = connection.Create(commandText, timeout: 0)
            .AddParameter("@count", numberOfRecordsInSinglePass, DbType.Int32)
            .AddParameter("@now", JobClock.UtcNow, DbType.DateTime);

        additionalActions?.Invoke(command);

        using (cancellationToken.Register(static state => ((DbCommand)state!).Cancel(), command)) // The command passed when registering
        {
            try
            {
                return command.ExecuteNonQuery();
            }
            catch (DbException ex) when (cancellationToken.IsCancellationRequested || IsLockTimeout(ex))
            {
                // Triggered by the Cancel method call, or another session holds a row past "set lock_timeout":
                // the rest is removed on the next run
                return 0;
            }
        }
    }

    // Error 1222. Its message is in the session's language, so it is recognised by its number, and by the
    // English message only for a client other than SqlClient
    private static bool IsLockTimeout(DbException exception)
    {
        if (exception is SqlException sqlException)
        {
            foreach (SqlError error in sqlException.Errors)
            {
                if (error.Number == LockTimeoutErrorNumber)
                {
                    return true;
                }
            }

            return false;
        }

        return exception.Message.Contains("Lock request time out period exceeded", StringComparison.Ordinal);
    }
}
