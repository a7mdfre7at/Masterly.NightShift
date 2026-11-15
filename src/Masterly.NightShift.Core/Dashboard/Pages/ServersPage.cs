using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard.Pages;

internal partial class ServersPage
{
    // Enough rows to count the busy workers of a large cluster, few enough to keep the page light
    internal const int MaxProcessingJobsRead = 500;

    /// <summary>
    /// How many of each server's workers run a job. With one server it is the number of processing jobs the
    /// statistics hold already. With more, each server's own report in <see cref="ServerBusyWorkers.HashKey"/>
    /// (one read of the hash), when every server has one taken close to its last heartbeat
    /// (<see cref="ServerBusyWorkers.IsFresh"/>); otherwise one read of the processing jobs (at most
    /// <see cref="MaxProcessingJobsRead"/>) tells them apart by the server that runs them. Never more than the
    /// server's workers.
    /// </summary>
    internal static Dictionary<string, int> CountBusyWorkers(
        IMonitoringApi monitor,
        IStorageConnection connection,
        IList<ServerDto> servers,
        long processing)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(servers);

        Dictionary<string, int> busy = new(StringComparer.Ordinal);
        if (servers.Count == 0 || processing <= 0)
        {
            return busy;
        }

        if (servers.Count == 1)
        {
            busy[servers[0].Name] = (int)Math.Min(processing, servers[0].WorkersCount);
            return busy;
        }

        if (!TryReadReports(connection, servers, busy))
        {
            CountProcessingJobs(monitor, processing, busy);
        }

        foreach (ServerDto server in servers)
        {
            if (busy.TryGetValue(server.Name, out int count))
            {
                busy[server.Name] = Math.Min(count, server.WorkersCount);
            }
        }

        return busy;
    }

    /// <summary>The share of a server's workers that are busy, from 0 to 100.</summary>
    internal static double BusyPercent(int busy, int workers)
    {
        return workers <= 0 ? 0 : Math.Min(100, 100.0 * busy / workers);
    }

    // Every listed server's own report, or nothing: a server without a fresh one (an earlier build, a report that
    // failed) would show none of its busy workers, so the processing jobs count for all of them then
    private static bool TryReadReports(IStorageConnection connection, IList<ServerDto> servers, Dictionary<string, int> busy)
    {
        Dictionary<string, string>? reports;
        try
        {
            reports = connection.GetAllEntriesFromHash(ServerBusyWorkers.HashKey);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return false;
        }

        if (reports == null)
        {
            return false;
        }

        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (ServerDto server in servers)
        {
            if (!reports.TryGetValue(ServerBusyWorkers.GetField(server.Name), out string? value) ||
                !ServerBusyWorkers.TryParse(value, out int count, out DateTime takenAt, out TimeSpan interval) ||
                !ServerBusyWorkers.IsFresh(takenAt, interval, server.Heartbeat))
            {
                return false;
            }

            counts[server.Name] = count;
        }

        foreach (KeyValuePair<string, int> count in counts)
        {
            busy[count.Key] = count.Value;
        }

        return true;
    }

    private static void CountProcessingJobs(IMonitoringApi monitor, long processing, Dictionary<string, int> busy)
    {
        JobList<ProcessingJobDto?> jobs = monitor.ProcessingJobs(0, (int)Math.Min(processing, MaxProcessingJobsRead));
        foreach (KeyValuePair<string, ProcessingJobDto?> job in jobs)
        {
            if (job.Value is { InProcessingState: true, ServerId: not null })
            {
                busy[job.Value.ServerId] = busy.TryGetValue(job.Value.ServerId, out int count) ? count + 1 : 1;
            }
        }
    }
}
