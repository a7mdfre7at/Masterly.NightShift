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

using System.Collections.Frozen;
using System.Diagnostics;

namespace Masterly.NightShift.SqlServer;

/// <summary>
/// Takes and releases session-scoped application locks with <c>sp_getapplock</c>. The locks users take go through
/// <see cref="IStorageConnection.AcquireDistributedLock"/>, which holds them on the connection's dedicated
/// connection.
/// </summary>
internal static class SqlServerDistributedLock
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(1);

    private const string LockMode = "Exclusive";
    private const string LockOwner = "Session";

    private static readonly FrozenDictionary<int, string> LockErrorMessages
        = new Dictionary<int, string>
        {
            { -1, "The lock request timed out" },
            { -2, "The lock request was canceled" },
            { -3, "The lock request was chosen as a deadlock victim" },
            { -999, "Indicates a parameter validation or other call error" }
        }.ToFrozenDictionary();

    internal static void Acquire(DbConnection connection, string resource, TimeSpan timeout)
    {
        if (connection.State != ConnectionState.Open)
        {
            // When we are passing a closed connection to Dapper's Execute method,
            // it kindly opens it for us, but after command execution, it will be closed
            // automatically, and our just-acquired application lock will immediately
            // be released. This is not behavior we want to achieve, so let's throw an
            // exception instead.
            throw new InvalidOperationException("Connection must be open before acquiring a distributed lock.");
        }

        Stopwatch started = Stopwatch.StartNew();

        // We can't pass our timeout directly to the sp_getapplock stored procedure, because
        // high values, such as minute or more, may cause SQL Server's thread pool starvation,
        // when the number of connections that try to acquire a lock is more than the number of 
        // available threads in SQL Server. In this case a deadlock will occur, when SQL Server 
        // tries to schedule some more work for a connection that acquired a lock, but all the 
        // available threads in a pool waiting for that lock to be released.
        //
        // So we are trying to acquire a lock multiple times instead, with timeout that's equal
        // to seconds, not minutes.
        long lockTimeout = (long)Math.Min(LockTimeout.TotalMilliseconds, timeout.TotalMilliseconds);
        string lockResource = SqlServerLimits.GetLockResource(resource);

        do
        {
            using DbCommand command = connection
                .Create("sp_getapplock", CommandType.StoredProcedure, timeout: (int)(lockTimeout / 1000) + 5)
                .AddParameter("@Resource", lockResource, DbType.String, size: SqlServerLimits.LockResourceLength)
                .AddParameter("@DbPrincipal", "public", DbType.String, size: 32)
                .AddParameter("@LockMode", LockMode, DbType.String, size: 32)
                .AddParameter("@LockOwner", LockOwner, DbType.String, size: 32)
                .AddParameter("@LockTimeout", lockTimeout, DbType.Int32)
                .AddReturnParameter("@Result", out DbParameter? resultParameter, DbType.Int32);

            command.ExecuteNonQuery();

            int lockResult = (int)resultParameter.Value!; // An output parameter the procedure always sets

            if (lockResult >= 0)
            {
                // The lock has been successfully obtained on the specified resource.
                return;
            }

            if (lockResult == -999 /* Indicates a parameter validation or other call error. */)
            {
                throw new SqlServerDistributedLockException(
                    $"Could not place a lock on the resource '{resource}': {(LockErrorMessages.TryGetValue(lockResult, out string? message) ? message : $"Server returned the '{lockResult}' error.")}.");
            }
        } while (started.Elapsed < timeout);

        throw new DistributedLockTimeoutException(resource);
    }

    /// <summary>
    /// Whether the lock may be held although <see cref="Acquire"/> threw <paramref name="exception"/>. An answer of
    /// the server that refused it (a timeout, an error result) means it wasn't granted; anything else, such as a
    /// command timeout or a broken connection, may have come after the server granted it.
    /// </summary>
    internal static bool MayHaveBeenGranted([NotNull] Exception exception)
    {
        return exception is not (DistributedLockTimeoutException or SqlServerDistributedLockException);
    }

    internal static void Release(DbConnection connection, string resource)
    {
        using DbCommand command = CreateReleaseCommand(connection, resource, out DbParameter? resultParameter);
        command.ExecuteNonQuery();

        int releaseResult = (int)resultParameter.Value!; // An output parameter the procedure always sets

        if (releaseResult < 0)
        {
            throw new SqlServerDistributedLockException(
                $"Could not release a lock on the resource '{resource}': Server returned the '{releaseResult}' error.");
        }
    }

    internal static DbCommand CreateReleaseCommand(
        DbConnection connection,
        string resource,
        out DbParameter resultParameter)
    {
        return connection.Create("sp_releaseapplock", CommandType.StoredProcedure)
            .AddParameter("@Resource", SqlServerLimits.GetLockResource(resource), DbType.String, size: SqlServerLimits.LockResourceLength)
            .AddParameter("@LockOwner", LockOwner, DbType.String, size: 32)
            .AddReturnParameter("@Result", out resultParameter, DbType.Int32);
    }
}
