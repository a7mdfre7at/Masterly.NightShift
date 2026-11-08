using System;
using System.Data.Common;
using System.Reflection;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Logging;

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
        /// Calls the static <c>ClearPool</c> method of the connection's type: <c>SqlConnection.ClearPool</c> of
        /// <c>Microsoft.Data.SqlClient</c> or <c>System.Data.SqlClient</c>. It is found by reflection, so that neither
        /// package becomes a dependency. A connection of another type is left alone.
        /// </summary>
        /// <returns><c>true</c> when the pool was cleared.</returns>
        public static bool TryClear([NotNull] DbConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            try
            {
                var type = connection.GetType();
                var clearPool = type.GetMethod("ClearPool", BindingFlags.Public | BindingFlags.Static, null, new[] { type }, null);
                if (clearPool == null) return false;

                clearPool.Invoke(null, new object[] { connection });
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
