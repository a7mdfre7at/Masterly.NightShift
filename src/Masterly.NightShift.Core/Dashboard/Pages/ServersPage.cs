using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard.Pages;

internal partial class ServersPage
{
    // Enough rows to count the busy workers of a large cluster, few enough to keep the page light
    internal const int MaxProcessingJobsRead = 500;

    /// <summary>
    /// How many of each server's workers run a job. With one server it is the number of processing jobs the
    /// statistics hold already; with more, one read of the processing jobs (at most <see cref="MaxProcessingJobsRead"/>)
    /// tells them apart by the server that runs them. Never more than the server's workers.
    /// </summary>
    internal static Dictionary<string, int> CountBusyWorkers(IMonitoringApi monitor, IList<ServerDto> servers, long processing)
    {
        ArgumentNullException.ThrowIfNull(monitor);
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

        JobList<ProcessingJobDto?> jobs = monitor.ProcessingJobs(0, (int)Math.Min(processing, MaxProcessingJobsRead));
        foreach (KeyValuePair<string, ProcessingJobDto?> job in jobs)
        {
            if (job.Value is { InProcessingState: true, ServerId: not null })
            {
                busy[job.Value.ServerId] = busy.TryGetValue(job.Value.ServerId, out int count) ? count + 1 : 1;
            }
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
}
