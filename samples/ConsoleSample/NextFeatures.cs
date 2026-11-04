using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;

namespace ConsoleSample
{
    /// <summary>
    /// Jobs showing the features described in docs/whats-new-next.md. Every method here gets a generated
    /// invoker because the sample references the Hangfire.SourceGenerator analyzer.
    /// </summary>
    [GenerateJobInvokers]
    public class NextFeatures
    {
        private static readonly Random Random = new Random();

        /// <summary>Reports progress, lines and checkpoints: open the job in the dashboard while it runs.</summary>
        [LogInvocation]
        public async Task Import(int rows, IJobConsole console, CancellationToken cancellationToken)
        {
            console.Checkpoint("download");
            await Task.Delay(500, cancellationToken);

            console.Checkpoint("parse");
            for (var i = 1; i <= rows; i++)
            {
                await Task.Delay(100, cancellationToken);

                if (i % 5 == 0)
                {
                    console.SetProgress(100.0 * i / rows, $"row {i} of {rows}");
                }

                if (i % 7 == 0)
                {
                    console.WriteLine(JobConsoleLevel.Warning, $"row {i} has an empty e-mail, skipped");
                }
            }

            console.Checkpoint("save");
            await Task.Delay(300, cancellationToken);
            console.SetProgress(100, "done");
        }

        /// <summary>Only two of these run at a time per tenant; the others go back to the schedule.</summary>
        [Throttle("tenant-{0}", MaxConcurrency = 2, RetryDelaySeconds = 5)]
        public async Task PerTenant(string tenant, IJobConsole console)
        {
            console.WriteLine($"working for {tenant}");
            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        /// <summary>At most 3 starts per 30 seconds, wherever the servers are.</summary>
        [RateLimit("sms", limit: 3, perSeconds: 30)]
        public Task SendSms(string number)
        {
            Console.WriteLine($"SMS to {number}");
            return Task.CompletedTask;
        }

        /// <summary>Fails often; after 3 failures in a minute the circuit opens and jobs are rescheduled for 2 minutes.</summary>
        [CircuitBreaker("flaky-api", failureThreshold: 3, windowSeconds: 60, openSeconds: 120)]
        [AutomaticRetry(Attempts = 0)]
        public void CallFlakyApi()
        {
            if (Random.Next(2) == 0) throw new TimeoutException("The flaky API did not answer");
            Console.WriteLine("The flaky API answered");
        }

        /// <summary>Transient errors are retried in place, three times, before the job fails.</summary>
        [RetryInProcess(3, DelayMilliseconds = 200)]
        public void Transient(IJobConsole console)
        {
            if (Random.Next(3) != 0)
            {
                console.WriteLine(JobConsoleLevel.Warning, "deadlock, retrying in process");
                throw new InvalidOperationException("Deadlock victim");
            }

            console.WriteLine("committed");
        }

        /// <summary>Canceled after 5 seconds through its cancellation token, then fails with TimeoutException.</summary>
        [JobTimeout(5)]
        public async Task TooSlow(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
        }
    }
}
