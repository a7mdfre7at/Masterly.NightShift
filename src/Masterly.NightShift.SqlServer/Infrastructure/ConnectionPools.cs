using Masterly.NightShift.Logging;
using Microsoft.Data.SqlClient;

namespace Masterly.NightShift.SqlServer;

/// <summary>
/// Clears the SQL client's connection pool of a connection string. After an open fails, both SQL clients fail
/// every new open of the same pooled connection string at once, with the same error, for 5 seconds, and for up
/// to a minute after further failures (the pool blocking period). Clearing the pool ends that, so the next open
/// tries the server again. It also keeps a connection whose session may still hold a lock out of the pool.
/// </summary>
internal static class ConnectionPools
{
    private static readonly ILog Logger = LogProvider.GetLogger(typeof(ConnectionPools));

    /// <summary>
    /// Clears the connection pool of a <c>Microsoft.Data.SqlClient</c> connection. A connection of another type, for
    /// example one wrapped by a profiler, is left alone.
    /// </summary>
    /// <returns><c>true</c> when the pool was cleared.</returns>
    public static bool TryClear([NotNull] DbConnection connection)
    {
        return TryClear(connection, "Unable to clear the connection pool after a failed open. The next open may fail at once with the same error.");
    }

    /// <summary>
    /// Closes the session of <paramref name="connection"/> when it is disposed, instead of returning it to the pool:
    /// a session application lock that may still be held there would stay held until the pooled connection is
    /// used again or pruned, because the client resets a session only when it hands it out again. The client offers
    /// that only for a whole pool: the other idle connections of the pool are closed too, and the ones in use are
    /// closed when they are disposed, so new ones are opened for the next callers. A connection of another type
    /// (wrapped by a profiler, for example) is left alone, with a warning.
    /// </summary>
    public static void DiscardWhenClosed([NotNull] DbConnection connection)
    {
        if (!TryClear(connection, "Unable to keep a connection whose session may still hold a lock out of the pool."))
        {
            Logger.Warn($"A connection of type '{connection.GetType()}' whose session may still hold an application lock goes back to its pool: SQL Server keeps the lock until the pooled connection is used again or closed.");
        }
    }

    private static bool TryClear(DbConnection connection, string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            if (connection is not SqlConnection sqlConnection)
            {
                return false;
            }

            SqlConnection.ClearPool(sqlConnection);
            return true;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            Logger.DebugException(failureMessage, ex);
            return false;
        }
    }
}
