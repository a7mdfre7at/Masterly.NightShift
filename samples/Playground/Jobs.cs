using System.Collections.Concurrent;
using Masterly.NightShift;
using Masterly.NightShift.Server;

namespace Playground;

// Jobs are ordinary classes. The server creates them through dependency injection, so they can take
// ILogger<T> and other services in their constructors.

public class BasicJobs(ILogger<BasicJobs> logger)
{
    public void Hello(string name) => logger.LogInformation("Hello, {Name}!", name);

    public void Remind(string what) => logger.LogInformation("Reminder: {What}", what);

    public int CountWords(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    public void Log(string order)
    {
        logger.LogInformation("Order {Order}: checking stock", order);
        logger.LogWarning("Order {Order}: one item is back-ordered", order);
        logger.LogInformation("Order {Order}: done", order);
    }
}

public class ProgressJobs
{
    public async Task ImportAsync(int rows, IJobConsole console, CancellationToken token)
    {
        console.WriteLine($"Importing {rows} rows");
        for (int step = 1; step <= 10; step++)
        {
            await Task.Delay(1500, token);
            console.SetProgress(step * 10, $"Row {step * rows / 10} of {rows}");
            if (step == 5)
            {
                console.WriteLine(JobConsoleLevel.Warning, "3 rows skipped: missing email");
            }
        }

        console.WriteLine("Import finished");
    }

    // The first attempt crashes in "render"; the retry skips "collect", which already has its checkpoint
    [AutomaticRetry(Attempts = 1, DelaysInSeconds = [5])]
    public void ResumableExport(IJobConsole console, PerformContext context)
    {
        console.Resume("collect", () =>
        {
            console.WriteLine("Collecting data (the slow step)");
            Thread.Sleep(2000);
        });
        console.Resume("render", () =>
        {
            if (context.GetJobParameter<int>("RetryCount") == 0)
            {
                throw new InvalidOperationException("The renderer crashed: the retry resumes here");
            }

            console.WriteLine("Rendering");
        });
        console.Resume("upload", () => console.WriteLine("Uploading"));
    }

    public async Task LongTaskAsync(IJobConsole console, CancellationToken token)
    {
        for (int second = 1; second <= 120; second++)
        {
            token.ThrowIfCancellationRequested();
            console.SetProgress(second * 100.0 / 120, $"Working: {second} s of 120");
            await Task.Delay(1000, token);
        }
    }
}

public class FailureJobs
{
    private static readonly ConcurrentDictionary<string, int> Attempts = new();

    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [5, 10])]
    public void FlakyApi(string endpoint) => throw new HttpRequestException($"{endpoint} answered 503 Service Unavailable");

    [AutomaticRetry(Attempts = 0)]
    public void Broken(string reason) => throw new InvalidOperationException(reason);

    // RetryLaterException doesn't use up a retry: the job is scheduled again with the given delay
    public void RateLimitedApi(string key)
    {
        int attempt = Attempts.AddOrUpdate(key, 1, static (_, n) => n + 1);
        if (attempt < 3)
        {
            throw new RetryLaterException(TimeSpan.FromSeconds(5), $"The API's rate limit is reached (call {attempt})");
        }
    }

    public void AlreadyPaid(int invoiceId) => throw new TerminateJobException($"Invoice {invoiceId} is already paid");

    public void UserStopped() => throw new TerminateJobException("The user stopped the export", TerminateJobOutcome.Canceled);

    public void InvalidInput(string input) => throw new TerminateJobException($"'{input}' isn't a date", TerminateJobOutcome.Failed);

    // Retried inside the same run: no Failed state is ever written
    [RetryInProcess(3, DelayMilliseconds = 500)]
    public void Transient(string key)
    {
        int attempt = Attempts.AddOrUpdate(key, 1, static (_, n) => n + 1);
        if (attempt < 3)
        {
            throw new TimeoutException($"Attempt {attempt} timed out");
        }
    }

    [JobTimeout(5, OnTimeout = JobTimeoutAction.Cancel)]
    public Task HangsAsync(CancellationToken token) => Task.Delay(Timeout.Infinite, token);
}

public class OrderJobs
{
    public void Charge(int orderId, IJobConsole console)
    {
        console.WriteLine($"Charging order {orderId}");
        Thread.Sleep(1500);
    }

    [AutomaticRetry(Attempts = 0)]
    public void ChargeDeclined(int orderId) => throw new InvalidOperationException($"Card declined for order {orderId}");

    public void SendReceipt(int orderId, IJobConsole console) => console.WriteLine($"Receipt sent for order {orderId}");

    public void AlertSupport(int orderId, IJobConsole console) => console.WriteLine(JobConsoleLevel.Error, $"Payment failed for order {orderId}: support notified");

    public void WatchProgress(int orderId, IJobConsole console) => console.WriteLine($"Started watching order {orderId} while it is charged");

    public int CountRows(string file)
    {
        Thread.Sleep(1000);
        return file.Length * 100;
    }

    public void Report(string file, [FromResult] int rows, IJobConsole console) => console.WriteLine($"{file} has {rows} rows (the parent's return value)");

    public void ExportRegion(string region, IJobConsole console)
    {
        Thread.Sleep(Random.Shared.Next(1000, 4000));
        console.WriteLine($"Exported {region}");
    }

    [AutomaticRetry(Attempts = 0)]
    public void ExportRegionFailing(string region) => throw new IOException($"Disk full while exporting {region}");

    public void Merge(IJobConsole console) => console.WriteLine("All regions merged");

    public void NotifyFirst(IJobConsole console) => console.WriteLine("The first region is ready");

    public void AlertExportFailed(IJobConsole console) => console.WriteLine(JobConsoleLevel.Error, "An export failed");

    public void Process(int orderId, IJobConsole console)
    {
        console.SetProgress(50, "Checking stock");
        Thread.Sleep(800);
        console.SetProgress(100, "Done");
    }

    public void RenderInvoice(int orderId) => Thread.Sleep(600);

    public void SendConfirmation(int orderId, IJobConsole console) => console.WriteLine($"Confirmation sent for order {orderId}");

    public void PostToErp(int orderId) => Thread.Sleep(400);

    [AutomaticRetry(Attempts = 0)]
    public void PostToErpFailing(int orderId) => throw new InvalidOperationException($"ERP rejected order {orderId}: unknown customer");

    public void NotifyWarehouse(int orderId) => Thread.Sleep(200);

    public void AlertOperations(int orderId, IJobConsole console) => console.WriteLine(JobConsoleLevel.Error, $"Order {orderId} needs attention");

    public void CloseLedger(string month) => Thread.Sleep(500);

    public void BuildReport(string report) => Thread.Sleep(500);

    public void Publish(string month)
    {
    }

    [AutomaticRetry(Attempts = 0)]
    public void RenderInvoiceNumber(int number)
    {
        Thread.Sleep(Random.Shared.Next(300, 1500));
        if (number == 7)
        {
            throw new InvalidOperationException("Invoice 7 has no customer address");
        }
    }

    public void BatchReport(string batchId, IJobConsole console) => console.WriteLine($"Batch {batchId} finished");
}

// A job class: the whole class is the job, created through dependency injection
public sealed class NightlyCleanup(ILogger<NightlyCleanup> logger) : IJob
{
    public async Task RunAsync(PerformContext context, CancellationToken token)
    {
        logger.LogInformation("Cleaning up (job #{JobId})", context.BackgroundJob.Id);
        await Task.Delay(1000, token);
    }
}

// A typed handler: the job's arguments are a record, and its handler is found by the record's type
public sealed record SendInvoice(int InvoiceId, string Email);

public sealed class SendInvoiceHandler(ILogger<SendInvoiceHandler> logger) : IJobHandler<SendInvoice>
{
    public Task HandleAsync(SendInvoice args, PerformContext context, CancellationToken token)
    {
        logger.LogInformation("Invoice {InvoiceId} sent to {Email}", args.InvoiceId, args.Email);
        return Task.CompletedTask;
    }
}

public class MaintenanceJobs(ILogger<MaintenanceJobs> logger)
{
    // A job function: other code (or another service) can create it by name, without referencing this class
    [JobFunction("maintenance.cleanup", Description = "Deletes temporary files older than the given number of days")]
    public Task CleanupAsync(int days, CancellationToken token)
    {
        logger.LogInformation("Deleting temporary files older than {Days} days", days);
        return Task.CompletedTask;
    }

    public void RebuildIndex(string index, bool online) => logger.LogInformation("Rebuilding {Index} (online: {Online})", index, online);

    public void ArchiveOrders(int olderThanDays, string destination) =>
        logger.LogInformation("Archiving orders older than {Days} days to {Destination}", olderThanDays, destination);
}

// Slow work for the two servers: each run keeps one worker busy for the given time
public class ReportJobs(ILogger<ReportJobs> logger)
{
    public void Render(string report, int seconds)
    {
        logger.LogInformation("Rendering the {Report} report", report);
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
    }
}

public class RuleJobs(ILogger<RuleJobs> logger)
{
    [SkipIfLate(60)]
    public void TimeSensitive(string what) => logger.LogInformation("{What} ran in time", what);

    [RunWindow("03:00-03:30")]
    public void NightlyOnly(string what) => logger.LogInformation("{What} ran in its window", what);

    [Throttle("thumbnails", MaxConcurrency = 2)]
    public void Thumbnail(int image) => Thread.Sleep(4000);

    [RateLimit("sms", 3, 30)]
    public void SendSms(int message) => logger.LogInformation("SMS {Message} sent", message);

    [PreventDuplicates]
    public void SyncCustomer(int customerId) => Thread.Sleep(8000);

    [PreventDuplicates("price:{0}", OnDuplicate = DuplicateHandling.ReplaceExisting)]
    public void RecalculatePrice(string sku, decimal price) => logger.LogInformation("{Sku} now costs {Price}", sku, price);

    [CircuitBreaker("payments-api", failureThreshold: 3, windowSeconds: 60, openSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public void CallPayments(int call) => throw new HttpRequestException($"payments-api timed out (call {call})");

    [Deadline("00:00:03")]
    public void SlowReport() => Thread.Sleep(6000);

    [LongRunning]
    public async Task ReindexAsync(IJobConsole console, CancellationToken token)
    {
        for (int step = 1; step <= 10; step++)
        {
            await Task.Delay(2000, token);
            console.SetProgress(step * 10, "Reindexing");
        }
    }

    [Queue("critical")]
    [JobDisplayName("Send a password reset to {0}")]
    [LogInvocation]
    public void PasswordReset(string email, [Redacted] string resetToken) => Thread.Sleep(300);

    [Tag("invoices")]
    [Tag("customer:{0}")]
    public void InvoiceCustomer(string customer, int amount) => logger.LogInformation("Invoiced {Customer} {Amount}", customer, amount);
}

public class MonitoredJobs
{
    public void SlowReport() => Thread.Sleep(15000);

    [AutomaticRetry(Attempts = 0)]
    public void FlakySync() => throw new TimeoutException("The partner API didn't answer");

    public void DailyClose()
    {
    }
}

public class NewsletterJobs(ILogger<NewsletterJobs> logger)
{
    public void Send(string edition) => logger.LogInformation("Newsletter {Edition} sent", edition);
}

public class RecurringJobs(ILogger<RecurringJobs> logger)
{
    public void Heartbeat() => logger.LogInformation("Still alive");

    public void BusinessReport() => logger.LogInformation("Business report built");

    public void Digest(PerformContext context) =>
        logger.LogInformation("Digest for the occurrence of {Time}", context.GetJobParameter<string>("Time"));

    public void Extract() => Thread.Sleep(1000);

    public void Transform() => Thread.Sleep(1000);

    public void Load() => Thread.Sleep(1000);
}
