extern alias ReferencedDapper;

using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using ReferencedDapper::Dapper;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>Lets a test go on once the sessions it started wait where it wants them.</summary>
public static class LockWaits
{
    /// <summary>
    /// Waits until that many sessions wait for a lock in the test database. Where the locks can't be listed
    /// (no VIEW SERVER STATE), it waits a moment instead.
    /// </summary>
    public static void WaitForSessionsThatWait(int count)
    {
        using DbConnection sql = ConnectionUtils.CreateConnection();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            while (sql.ExecuteScalar<int>(
                "select count(distinct request_session_id) from sys.dm_tran_locks where request_status = N'WAIT' and resource_database_id = db_id()") < count)
            {
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"{count} sessions should be waiting for a lock.");
                Thread.Sleep(20);
            }
        }
        catch (SqlException)
        {
            // No permission to see the locks
            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
    }
}
