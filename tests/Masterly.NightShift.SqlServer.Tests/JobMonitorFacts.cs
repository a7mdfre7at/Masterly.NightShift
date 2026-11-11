using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>A monitor records the outcomes of real jobs, and a server's monitor process raises the incident.</summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class JobMonitorFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact, CleanDatabase]
    public async Task RepeatedFailures_RaiseAnIncident_FromTheServersMonitorProcess()
    {
        SqlServerStorage storage = CreateStorage();
        Recording sink = new();
        TimeSpan interval = JobMonitors.CheckInterval;

        try
        {
            JobMonitors.CheckInterval = TimeSpan.FromSeconds(5);
            GlobalConfiguration.Configuration
                .UseJobMonitor(new RepeatedFailuresMonitor("sql-failing", "JobMonitorFacts.Fail", 2, TimeSpan.FromHours(1)))
                .UseAlerts(sink);

            BackgroundJobClient client = new(storage);
            string first = client.Enqueue(() => Fail());
            string second = client.Enqueue(() => Fail());

            using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1 }, storage))
            {
                await storage.WaitForCompletionAsync(first, Timeout, TestContext.Current.CancellationToken);
                await storage.WaitForCompletionAsync(second, Timeout, TestContext.Current.CancellationToken);

                Stopwatch stopwatch = Stopwatch.StartNew();
                while (sink.Alerts.IsEmpty && stopwatch.Elapsed < Timeout)
                {
                    await Task.Delay(100, TestContext.Current.CancellationToken);
                }
            }

            Alert alert = Assert.Single(sink.Alerts);
            Assert.Equal(AlertKind.MonitorIncident, alert.Kind);
            Assert.Equal("sql-failing", alert.Monitor);
            JobMonitorStatus status = Assert.Single(JobMonitors.GetStatus(storage), static x => x.Name == "sql-failing");
            Assert.Equal(JobMonitorState.NeedsAttention, status.State);
            Assert.Equal(2, status.ConsecutiveFailures);
        }
        finally
        {
            JobMonitors.Remove("sql-failing");
            JobMonitors.CheckInterval = interval;
            Alerts.Clear();
        }
    }

    [AutomaticRetry(Attempts = 0)]
    public static void Fail()
    {
        throw new InvalidOperationException("Failing on purpose");
    }

    private sealed class Recording : IAlertSink
    {
        public ConcurrentQueue<Alert> Alerts { get; } = new();

        public void Publish(Alert alert)
        {
            if (alert.Kind is AlertKind.MonitorIncident or AlertKind.MonitorRecovered) Alerts.Enqueue(alert);
        }
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
    }
}
