using System;
using System.Data.Common;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Logging;
using Microsoft.Data.SqlClient;

namespace Masterly.NightShift.SqlServer
{
    /// <summary>
    /// Clears the SQL client's connection pool of a connection string. After an open fails, both SQL clients fail
    /// every new open of the same pooled connection string at once, with the same error, for 5 seconds, and for up
    /// to a minute after further failures (the pool blocking period). Clearing the pool ends that, so the next open
    /// tries the server again.
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
            ArgumentNullException.ThrowIfNull(connection);

            try
            {
                if (connection is not SqlConnection sqlConnection) return false;

                SqlConnection.ClearPool(sqlConnection);
                return true;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.DebugException("Unable to clear the connection pool after a failed open. The next open may fail at once with the same error.", ex);
                return false;
            }
        }
    }
}
