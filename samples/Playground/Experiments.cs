using System.Net;
using Masterly.NightShift;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Playground;

/// <summary>
/// One step of the guide. <see cref="What"/> says what the feature is, <see cref="Look"/> where to look and what to
/// notice, <see cref="Code"/> the call that does it. <see cref="Run"/> is null for steps you take in the dashboard.
/// </summary>
public sealed record Experiment(string Id, string Section, string Title, string What, string Look, string Code, Func<PlaygroundContext, Task<string>>? Run = null, string Button = "Run it");

public static class Experiments
{
    public static IReadOnlyList<Experiment> All { get; } =
    [
        // ------------------------------------------------------------------ 1. Start here
        new("tour", "1. Start here", "Open the dashboard",
            "The dashboard shows every job, its state and history. Keep it open in a second tab while you work through this page.",
            $"{D("/", "Home")}: the realtime graph and the live activity. The left menu has a page for each state. Press <kbd>Ctrl</kbd>+<kbd>K</kbd> anywhere to search, and <kbd>?</kbd> for the keyboard shortcuts.",
            "app.MapNightShiftDashboard();   // at /nightshift"),

        // ------------------------------------------------------------------ 2. Jobs
        new("enqueue", "2. Creating jobs", "Fire and forget",
            "A job runs once, as soon as a worker is free. Its method call is stored, so it survives a restart of the app (with a persistent storage).",
            "Open the job: its history shows Enqueued, Processing, Succeeded, each with its time. The console shows the ILogger line the job wrote.",
            "jobs.Enqueue<BasicJobs>(x => x.Hello(\"world\"));",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<BasicJobs>(x => x.Hello("world"))))),

        new("schedule", "2. Creating jobs", "Delayed job",
            "A job that runs later: after a delay or at a moment.",
            $"It waits on {D("/jobs/scheduled", "Scheduled")} for 20 seconds, then moves to Enqueued and runs.",
            "jobs.Schedule<BasicJobs>(x => x.Remind(\"call the bank\"), TimeSpan.FromSeconds(20));",
            static c => Task.FromResult(JobLink(c.Jobs.Schedule<BasicJobs>(x => x.Remind("call the bank"), TimeSpan.FromSeconds(20))))),

        new("schedule-text", "2. Creating jobs", "Delayed job, in plain English",
            "The delay can be a phrase: \"in 1 minute\", \"tomorrow at 9am\", \"next monday 14:00\".",
            $"On {D("/jobs/scheduled", "Scheduled")}, the job is due in about a minute.",
            "jobs.Schedule<BasicJobs>(x => x.Remind(\"stand-up\"), \"in 1 minute\");",
            static c => Task.FromResult(JobLink(c.Jobs.Schedule<BasicJobs>(x => x.Remind("stand-up"), "in 1 minute")))),

        new("queues", "2. Creating jobs", "Queues",
            "Jobs go to named queues; each server says which queues it takes, in priority order. This server takes \"critical\" then \"default\", and nobody takes \"nowhere\".",
            $"The \"critical\" job runs at once. The \"nowhere\" job stays on {D("/jobs/enqueued", "Enqueued")} forever, and {D("/health", "/health")} reports the storage as degraded: a queue has jobs and no server listens to it.",
            "jobs.Enqueue<BasicJobs>(\"critical\", x => x.Hello(\"VIP\"));\njobs.Enqueue<BasicJobs>(\"nowhere\", x => x.Hello(\"anyone?\"));",
            static c => Task.FromResult(
                JobLink(c.Jobs.Enqueue<BasicJobs>("critical", x => x.Hello("VIP")), "critical") + " · " +
                JobLink(c.Jobs.Enqueue<BasicJobs>("nowhere", x => x.Hello("anyone?")), "nowhere"))),

        new("bulk", "2. Creating jobs", "Many jobs at once",
            "EnqueueMany creates jobs in groups of 250, each group in one storage transaction.",
            $"25 jobs appear on {D("/jobs/succeeded", "Succeeded")} within a second or two. The realtime graph on {D("/", "Home")} jumps.",
            "jobs.EnqueueMany<BasicJobs, int>(Enumerable.Range(1, 25), (x, n) => x.Hello($\"user {n}\"));",
            static c =>
            {
                IReadOnlyList<string?> ids = c.Jobs.EnqueueMany<BasicJobs, int>(Enumerable.Range(1, 25), (x, n) => x.Hello($"user {n}"));
                return Task.FromResult($"{ids.Count} jobs created, the first is {JobLink(ids[0])}");
            }),

        new("result", "2. Creating jobs", "A job that returns a value",
            "Whatever the method returns is kept with the Succeeded state.",
            "Open the job: the Succeeded state shows Result: 6.",
            "jobs.Enqueue<BasicJobs>(x => x.CountWords(\"night shift runs your jobs while you sleep\"));",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<BasicJobs>(x => x.CountWords("night shift runs your jobs while you sleep"))))),

        new("create-state", "2. Creating jobs", "A state of your choice, with parameters",
            "Create puts a job in any initial state, with a reason, and job parameters any filter or the job itself can read.",
            "Open the job: the history shows the reason \"VIP sign-up\", and Parameters lists TenantId.",
            "client.Create(Job.FromExpression<BasicJobs>(x => x.Hello(\"VIP\")),\n    new EnqueuedState(\"critical\") { Reason = \"VIP sign-up\" },\n    new Dictionary<string, object> { [\"TenantId\"] = \"contoso\" });",
            static c =>
            {
                IBackgroundJobClientV2 client = (IBackgroundJobClientV2)c.Jobs;
                string? id = client.Create(Job.FromExpression<BasicJobs>(x => x.Hello("VIP")), new EnqueuedState("critical") { Reason = "VIP sign-up" },
                    new Dictionary<string, object> { ["TenantId"] = "contoso" });
                return Task.FromResult(JobLink(id));
            }),

        // ------------------------------------------------------------------ 3. Progress
        new("progress", "3. Progress and console", "Progress bar and console",
            "A job takes an IJobConsole parameter (pass null when you create it; the server fills it in) to report progress and write lines.",
            $"Open the job while it runs (15 s): the bar and the console update live. {D("/jobs/processing", "Processing")} shows the bar too.",
            "jobs.Enqueue<ProgressJobs>(x => x.ImportAsync(1000, null!, CancellationToken.None));",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<ProgressJobs>(x => x.ImportAsync(1000, null!, CancellationToken.None))))),

        new("resume", "3. Progress and console", "Checkpoints: resume after a crash",
            "console.Resume(\"step\", ...) runs a step once: a retry skips the steps that already finished.",
            "Open the job: the first attempt crashes in \"render\" and is retried after 5 s. The second attempt doesn't print \"Collecting data\" again. The Checkpoints list shows collect, render, upload.",
            "console.Resume(\"collect\", () => Collect());\nconsole.Resume(\"render\", () => Render());   // throws the first time\nconsole.Resume(\"upload\", () => Upload());",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<ProgressJobs>(x => x.ResumableExport(null!, null!))))),

        new("ilogger", "3. Progress and console", "ILogger lines in the job's console",
            "With AddNightShiftJobLogs, whatever the job writes to ILogger also lands in its console, with [nightshift] lines for start and end.",
            "Open the job: three lines from BasicJobs, the warning in yellow. The app's console shows the same lines with a JobId scope.",
            "builder.Services.AddNightShiftJobLogs(o => o.LifecycleLines = true);",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<BasicJobs>(x => x.Log("A-1001"))))),

        // ------------------------------------------------------------------ 4. Failures
        new("retry", "4. Failures and retries", "Automatic retries",
            "A failed job is retried: 10 times by default with growing delays. Here [AutomaticRetry(Attempts = 2, DelaysInSeconds = [5, 10])].",
            $"The job waits on {D("/retries", "Retries")}, then fails for good after about 15 s and lands on {D("/jobs/failed", "Failed")}. Its history shows each attempt. The event log below shows the exception handler (every attempt) and the alert and webhook (only the final failure).",
            "[AutomaticRetry(Attempts = 2, DelaysInSeconds = [5, 10])]\npublic void FlakyApi(string endpoint) => throw new HttpRequestException(...);",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<FailureJobs>(x => x.FlakyApi("https://partner.example/api"))))),

        new("fail", "4. Failures and retries", "Fail at once",
            "With Attempts = 0 a failure is final.",
            $"The job is on {D("/jobs/failed", "Failed")} at once. Open it: the Diagnosis explains what happened, and you can add a note and acknowledge it. The event log shows the alert and the webhook payload.",
            "[AutomaticRetry(Attempts = 0)]",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<FailureJobs>(x => x.Broken("The input file is empty"))))),

        new("retry-later", "4. Failures and retries", "Retry later, without using a retry",
            "Throw RetryLaterException when the job can't run yet (a rate limit, a lock): it is scheduled again and its retries are untouched.",
            "Open the job: Scheduled twice with \"Retry later (1 of 100)\", then Succeeded on the third call.",
            "throw new RetryLaterException(TimeSpan.FromSeconds(5), \"The API's rate limit is reached\");",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<FailureJobs>(x => x.RateLimitedApi(Guid.NewGuid().ToString()))))),

        new("terminate", "4. Failures and retries", "End a job now: skipped, canceled or failed",
            "TerminateJobException ends the job without retries, as Skipped, Canceled (both Deleted) or Failed.",
            $"Two jobs on {D("/jobs/deleted", "Deleted")} with \"Skipped: ...\" and \"Canceled: ...\", one on {D("/jobs/failed", "Failed")}. None went through Retries.",
            "throw new TerminateJobException(\"Invoice 42 is already paid\");\nthrow new TerminateJobException(\"...\", TerminateJobOutcome.Canceled);\nthrow new TerminateJobException(\"...\", TerminateJobOutcome.Failed);",
            static c => Task.FromResult(
                JobLink(c.Jobs.Enqueue<FailureJobs>(x => x.AlreadyPaid(42)), "skipped") + " · " +
                JobLink(c.Jobs.Enqueue<FailureJobs>(x => x.UserStopped()), "canceled") + " · " +
                JobLink(c.Jobs.Enqueue<FailureJobs>(x => x.InvalidInput("31/02")), "failed"))),

        new("retry-in-process", "4. Failures and retries", "Retry inside the run",
            "[RetryInProcess(3)] retries a quick transient error in the same run, without writing a Failed state.",
            "Open the job: Processing then Succeeded, nothing in between. The app's console shows the two retried attempts as warnings.",
            "[RetryInProcess(3, DelayMilliseconds = 500)]",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<FailureJobs>(x => x.Transient(Guid.NewGuid().ToString()))))),

        new("timeout", "4. Failures and retries", "Timeout",
            "[JobTimeout(5, OnTimeout = Cancel)] cancels the job's token after 5 seconds. The method must take a CancellationToken.",
            $"After 5 s the job is on {D("/jobs/deleted", "Deleted")} with \"Canceled: Timed out after 5 seconds\". The event log shows the exception handler's OnJobCanceled.",
            "[JobTimeout(5, OnTimeout = JobTimeoutAction.Cancel)]\npublic Task HangsAsync(CancellationToken token) => Task.Delay(Timeout.Infinite, token);",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<FailureJobs>(x => x.HangsAsync(CancellationToken.None))))),

        new("long", "4. Failures and retries", "Cancel a running job (step 1: start it)",
            "A two-minute job that checks its CancellationToken. Start it, then cancel it with the next step or with Delete on its page.",
            "Open the job: the bar moves every second.",
            "jobs.Enqueue<ProgressJobs>(x => x.LongTaskAsync(null!, CancellationToken.None));",
            static c =>
            {
                PlaygroundState.LongJobId = c.Jobs.Enqueue<ProgressJobs>(x => x.LongTaskAsync(null!, CancellationToken.None));
                return Task.FromResult(JobLink(PlaygroundState.LongJobId));
            }),

        new("cancel", "4. Failures and retries", "Cancel a running job (step 2: delete it)",
            "Deleting a running job cancels its token within CancellationCheckInterval (2 s here).",
            $"The job stops and is on {D("/jobs/deleted", "Deleted")}; the event log shows OnJobCanceled.",
            "jobs.Delete(jobId);",
            static c => Task.FromResult(PlaygroundState.LongJobId == null
                ? "Start the long job first."
                : (c.Jobs.Delete(PlaygroundState.LongJobId) ? "Deleted " : "Couldn't delete ") + JobLink(PlaygroundState.LongJobId)),
            "Delete it"),

        // ------------------------------------------------------------------ 5. Workflows
        new("continuations", "5. Workflows", "Continuations",
            "A continuation waits for its parent and runs on the condition you choose: on success (the default), on failure, on deletion, on any outcome, or as soon as the parent starts.",
            $"Order 1 (charged): the receipt waits on {D("/jobs/awaiting", "Awaiting")} and runs after; \"watch\" starts while the parent runs. Order 2 (declined): the support alert runs, and the receipt stays Awaiting, because a failed parent blocks success continuations. Open a parent: the Lineage diagram shows the links.",
            "string? charge = jobs.Enqueue<OrderJobs>(x => x.Charge(1, null!));\njobs.ContinueJobWith<OrderJobs>(charge, x => x.SendReceipt(1, null!));\njobs.ContinueJobWith<OrderJobs>(charge, x => x.WatchProgress(1, null!), JobContinuationOptions.OnlyOnProcessingState);\njobs.ContinueJobWith<OrderJobs>(declined, x => x.AlertSupport(2, null!), JobContinuationOptions.OnlyOnFailedState);",
            static c =>
            {
                string charge = c.Jobs.Enqueue<OrderJobs>(x => x.Charge(1, null!))!;
                c.Jobs.ContinueJobWith<OrderJobs>(charge, x => x.SendReceipt(1, null!));
                c.Jobs.ContinueJobWith<OrderJobs>(charge, x => x.WatchProgress(1, null!), JobContinuationOptions.OnlyOnProcessingState);
                string declined = c.Jobs.Enqueue<OrderJobs>(x => x.ChargeDeclined(2))!;
                c.Jobs.ContinueJobWith<OrderJobs>(declined, x => x.SendReceipt(2, null!));
                c.Jobs.ContinueJobWith<OrderJobs>(declined, x => x.AlertSupport(2, null!), JobContinuationOptions.OnlyOnFailedState);
                return Task.FromResult(JobLink(charge, "order 1") + " · " + JobLink(declined, "order 2"));
            }),

        new("from-result", "5. Workflows", "Pass a result to the next job",
            "With UseResultsInContinuations, a parameter marked [FromResult] receives the parent's return value.",
            "Open the continuation (from the parent's Lineage): its console says how many rows the parent counted.",
            "string? count = jobs.Enqueue<OrderJobs>(x => x.CountRows(\"customers.csv\"));\njobs.ContinueJobWith<OrderJobs>(count, x => x.Report(\"customers.csv\", default, null!));\n// public void Report(string file, [FromResult] int rows, IJobConsole console)",
            static c =>
            {
                string count = c.Jobs.Enqueue<OrderJobs>(x => x.CountRows("customers.csv"))!;
                string? report = c.Jobs.ContinueJobWith<OrderJobs>(count, x => x.Report("customers.csv", default, null!));
                return Task.FromResult(JobLink(count, "parent") + " → " + JobLink(report, "report"));
            }),

        new("fan-in", "5. Workflows", "Fan-in: wait for several jobs",
            "ContinueJobWithAll waits for every parent, ContinueJobWithAny for the first, and ContinueJobWithJoin for a condition such as \"any failed\".",
            $"Merge runs when the three exports finished; \"first ready\" right after the fastest. In the second group one export fails, so the AnyFailed alert runs. Look at {D("/jobs/awaiting", "Awaiting")} before they finish.",
            "string?[] exports = [..regions.Select(r => jobs.Enqueue<OrderJobs>(x => x.ExportRegion(r, null!)))];\njobs.ContinueJobWithAll<OrderJobs>(exports, x => x.Merge(null!));\njobs.ContinueJobWithAny<OrderJobs>(exports, x => x.NotifyFirst(null!));\njobs.ContinueJobWithJoin<OrderJobs>(others, FanInMode.AnyFailed, x => x.AlertExportFailed(null!));",
            static c =>
            {
                string?[] exports = [.. new[] { "eu", "us", "asia" }.Select(r => c.Jobs.Enqueue<OrderJobs>(x => x.ExportRegion(r, null!)))];
                string? merge = c.Jobs.ContinueJobWithAll<OrderJobs>(exports, x => x.Merge(null!));
                string? first = c.Jobs.ContinueJobWithAny<OrderJobs>(exports, x => x.NotifyFirst(null!));
                string?[] others = [c.Jobs.Enqueue<OrderJobs>(x => x.ExportRegion("africa", null!)), c.Jobs.Enqueue<OrderJobs>(x => x.ExportRegionFailing("oceania"))];
                string? alert = c.Jobs.ContinueJobWithJoin<OrderJobs>(others, FanInMode.AnyFailed, x => x.AlertExportFailed(null!));
                return Task.FromResult(JobLink(merge, "merge") + " · " + JobLink(first, "first ready") + " · " + JobLink(alert, "any-failed alert"));
            }),

        new("batch", "5. Workflows", "Batch",
            "A batch groups jobs and tracks them together; a continuation runs when the whole batch has finished. Invoice 7 fails.",
            $"{D("/batches", "Batches")}: the batch's bar fills, with 9 succeeded and 1 failed. Open it and press Requeue failed (invoice 7 fails again: it's on purpose).",
            "string batchId = BatchJob.StartNew(jobs, storage, b =>\n{\n    for (int i = 1; i <= 10; i++) b.Enqueue<OrderJobs>(x => x.RenderInvoiceNumber(i));\n}, \"November invoices\");\nBatchJob.ContinueWith(jobs, storage, batchId, Job.FromExpression<OrderJobs>(x => x.BatchReport(batchId, null!)));",
            static c =>
            {
                string batchId = BatchJob.StartNew(c.Jobs, c.Storage, b =>
                {
                    for (int i = 1; i <= 10; i++)
                    {
                        int number = i;
                        b.Enqueue<OrderJobs>(x => x.RenderInvoiceNumber(number));
                    }
                }, "November invoices");
                BatchJob.ContinueWith(c.Jobs, c.Storage, batchId, Job.FromExpression<OrderJobs>(x => x.BatchReport(batchId, null!)));
                return Task.FromResult(Dashboard($"/batches/{batchId}", "open the batch"));
            }),

        new("chain", "5. Workflows", "Chain: a pipeline of steps",
            "A JobChain is a whole workflow created at once: steps one after another, parallel branches (ThenMany), and steps that react to how a group ended.",
            "The chain's flowchart: every step succeeds, the group box says Done.",
            "JobChain.StartWith<OrderJobs>(x => x.Process(1001, null!))\n    .Then<OrderJobs>(x => x.RenderInvoice(1001))\n    .ThenMany(g => g.Branch<OrderJobs>(x => x.SendConfirmation(1001, null!))\n                    .Branch<OrderJobs>(x => x.PostToErp(1001)))\n    .ThenAfterAll<OrderJobs>(x => x.NotifyWarehouse(1001))\n    .Enqueue(jobs);",
            static c =>
            {
                JobChainResult? result = JobChain.StartWith<OrderJobs>(x => x.Process(1001, null!))
                    .Then<OrderJobs>(x => x.RenderInvoice(1001))
                    .ThenMany(g => g.Branch<OrderJobs>(x => x.SendConfirmation(1001, null!)).Branch<OrderJobs>(x => x.PostToErp(1001)))
                    .ThenAfterAll<OrderJobs>(x => x.NotifyWarehouse(1001))
                    .Enqueue(c.Jobs);
                return Task.FromResult(Chain(result));
            }),

        new("chain-failure", "5. Workflows", "Chain with a failure, then retry it",
            "A branch fails for good. \"After all succeeded\" won't run; \"after any failed\" does.",
            "In the flowchart the ERP card is red with Retry from here, Notify warehouse is dimmed as Not run, and the red link leads to the alert. Press Retry from here: the ERP step runs again (and fails again, on purpose), and the generation goes up.",
            ".ThenMany(g => g.Branch(...SendConfirmation...).Branch(...PostToErpFailing...))\n.ThenAfterAll<OrderJobs>(x => x.NotifyWarehouse(1002))\n.ThenAfterAnyFailed<OrderJobs>(x => x.AlertOperations(1002, null!))\n// later: storage.RetryChainFrom(failedStepJobId);",
            static c =>
            {
                JobChainResult? result = JobChain.StartWith<OrderJobs>(x => x.Process(1002, null!))
                    .ThenMany(g => g.Branch<OrderJobs>(x => x.SendConfirmation(1002, null!)).Branch<OrderJobs>(x => x.PostToErpFailing(1002)))
                    .ThenAfterAll<OrderJobs>(x => x.NotifyWarehouse(1002))
                    .ThenAfterAnyFailed<OrderJobs>(x => x.AlertOperations(1002, null!))
                    .Enqueue(c.Jobs);
                return Task.FromResult(Chain(result));
            }),

        new("chain-scheduled", "5. Workflows", "Scheduled chain",
            "A chain can start later. Until then it is Waiting; with OnDelete(Restrict) it can't be deleted while a step runs.",
            "The flowchart: the first step is Scheduled, the rest wait. The header says Deletion: Restrict.",
            "JobChain.StartWith<OrderJobs>(x => x.CloseLedger(\"2026-11\"))\n    .ThenMany(g => g.Branch(...).Branch(...).Branch(...))\n    .ThenAfterAllCompleted<OrderJobs>(x => x.Publish(\"2026-11\"))\n    .OnDelete(ChainDeleteBehavior.Restrict)\n    .Schedule(jobs, TimeSpan.FromHours(6));",
            static c =>
            {
                JobChainResult? result = JobChain.StartWith<OrderJobs>(x => x.CloseLedger("2026-11"))
                    .ThenMany(g => g.Branch<OrderJobs>(x => x.BuildReport("profit-and-loss")).Branch<OrderJobs>(x => x.BuildReport("balance-sheet")).Branch<OrderJobs>(x => x.BuildReport("cash-flow")))
                    .ThenAfterAllCompleted<OrderJobs>(x => x.Publish("2026-11"))
                    .OnDelete(ChainDeleteBehavior.Restrict)
                    .Schedule(c.Jobs, TimeSpan.FromHours(6));
                return Task.FromResult(Chain(result));
            }),

        new("chain-recurring", "5. Workflows", "Recurring chain",
            "A chain on a CRON schedule: each occurrence creates a new chain. skipWhileRunning skips an occurrence while the previous chain hasn't finished.",
            $"{D("/recurring/details/playground:etl", "The recurring job")} runs every minute (press Trigger now to not wait); each run adds a chain on {D("/chains", "Chains")}.",
            "recurring.AddOrUpdateChain(\"playground:etl\",\n    JobChain.StartWith<RecurringJobs>(x => x.Extract()).Then<RecurringJobs>(x => x.Transform()).Then<RecurringJobs>(x => x.Load()),\n    Cron.Minutely(), new RecurringJobOptions(), skipWhileRunning: true);",
            static c =>
            {
                c.Recurring.AddOrUpdateChain("playground:etl",
                    JobChain.StartWith<RecurringJobs>(x => x.Extract()).Then<RecurringJobs>(x => x.Transform()).Then<RecurringJobs>(x => x.Load()),
                    Cron.Minutely(), new RecurringJobOptions(), skipWhileRunning: true);
                return Task.FromResult(Dashboard("/recurring/details/playground:etl", "playground:etl"));
            }),

        // ------------------------------------------------------------------ 6. Typed jobs
        new("ijob", "6. Job classes and functions", "A job class (IJob)",
            "Instead of a lambda, the class is the job: it implements IJob and gets its dependencies from DI.",
            "Open the job: NightlyCleanup.RunAsync, and its console has the logger line.",
            "public sealed class NightlyCleanup(ILogger<NightlyCleanup> logger) : IJob { ... }\njobs.Enqueue<NightlyCleanup>();",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<NightlyCleanup>()))),

        new("handler", "6. Job classes and functions", "A typed handler (IJobHandler<TArgs>)",
            "The job's arguments are a record; the handler registered for that record type runs it.",
            "Open the job: SendInvoiceHandler handled the SendInvoice record (its values are the job's argument).",
            "builder.Services.AddNightShiftJobHandler<SendInvoiceHandler>();\njobs.Enqueue(new SendInvoice(7, \"ana@example.com\"));",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue(new SendInvoice(7, "ana@example.com"))))),

        new("function", "6. Job classes and functions", "Job functions, by name",
            "A method marked [JobFunction(\"name\")], or a delegate, is created by name, with a payload: the caller needs no reference to the class.",
            $"Two jobs: maintenance.cleanup with 30, and the cache.warm delegate (its line is in the event log). {D("/about", "About")} lists the registered functions.",
            "[JobFunction(\"maintenance.cleanup\")] public Task CleanupAsync(int days, CancellationToken token)\nbuilder.Services.AddNightShiftFunction(\"cache.warm\", (provider, token) => ...);\njobs.EnqueueFunction(\"maintenance.cleanup\", 30);\njobs.EnqueueFunction(\"cache.warm\");",
            static c => Task.FromResult(
                JobLink(c.Jobs.EnqueueFunction("maintenance.cleanup", 30), "maintenance.cleanup") + " · " +
                JobLink(c.Jobs.EnqueueFunction("cache.warm"), "cache.warm"))),

        new("catalog", "6. Job classes and functions", "Run any job from the Catalog",
            "JobCatalog.Add<MaintenanceJobs>() puts methods on the Catalog page, where operators run them with arguments: now, later, or as a recurring job.",
            $"{D("/catalog", "Catalog")}: press Run... on MaintenanceJobs.ArchiveOrders, fill in the arguments, choose Later or Recurring. Then open {D("/audit", "Audit")}: the run is recorded with who did it. On a finished job's page, Run again with changes... does the same from an existing job.",
            "JobCatalog.Add<MaintenanceJobs>();"),

        // ------------------------------------------------------------------ 7. Rules
        new("policy", "7. Scheduling rules", "Schedule policies: business calendars",
            "A schedule policy says when jobs may run (weekdays, hours, excluded dates). A job created outside it can move to the next allowed time, be skipped, or be rejected. This creates a policy that opens in 5 minutes.",
            $"MoveForward: on {D("/jobs/scheduled", "Scheduled")}, due when the policy opens, reason \"Moved by policy\". Skip: on {D("/jobs/deleted", "Deleted")}. Reject: no job at all. {D("/policies", "Policies")}: edit the policy and preview a CRON against it.",
            "storage.SaveSchedulePolicy(new SchedulePolicy(\"opens-soon\").AllowBetween(from, to));\njobs.Enqueue<BasicJobs>(x => x.Hello(\"...\"), new ScheduleOptions { Policy = \"opens-soon\", WhenBlocked = WhenBlocked.MoveForward });",
            static c =>
            {
                TimeSpan now = DateTime.UtcNow.TimeOfDay;
                TimeSpan from = TimeSpan.FromMinutes(Math.Ceiling(now.TotalMinutes) + 5);
                static string Format(TimeSpan t) => $"{(int)t.TotalHours % 24:00}:{t.Minutes:00}";
                c.Storage.SaveSchedulePolicy(new SchedulePolicy("opens-soon")
                    .AllowBetween(Format(from), Format(from + TimeSpan.FromHours(1)))
                    .WithDescription("Opens five minutes after you pressed the button (UTC)"));
                string? moved = c.Jobs.Enqueue<BasicJobs>(x => x.Hello("moved"), new ScheduleOptions { Policy = "opens-soon", WhenBlocked = WhenBlocked.MoveForward });
                string? skipped = c.Jobs.Enqueue<BasicJobs>(x => x.Hello("skipped"), new ScheduleOptions { Policy = "opens-soon", WhenBlocked = WhenBlocked.Skip });
                string? rejected = c.Jobs.Enqueue<BasicJobs>(x => x.Hello("rejected"), new ScheduleOptions { Policy = "opens-soon", WhenBlocked = WhenBlocked.Reject });
                return Task.FromResult($"Policy opens at {Format(from)} UTC. MoveForward: {JobLink(moved)} · Skip: {JobLink(skipped)} · Reject: {JobLink(rejected)}");
            }),

        new("late", "7. Scheduling rules", "Skip a job that starts too late",
            "[SkipIfLate(60)]: if the job starts more than a minute after it was due (the app was down, the queue was full), it is skipped instead of running late. This one was due 5 minutes ago.",
            $"On {D("/jobs/deleted", "Deleted")}: \"Skipped: started 5 min late\".",
            "[SkipIfLate(60)]\npublic void TimeSensitive(string what)",
            static c => Task.FromResult(JobLink(((IBackgroundJobClientV2)c.Jobs).Create(Job.FromExpression<RuleJobs>(x => x.TimeSensitive("flash sale e-mail")),
                new ScheduledState(DateTime.UtcNow.AddMinutes(-5)), null)))),

        new("run-window", "7. Scheduling rules", "Run window",
            "[RunWindow(\"03:00-03:30\")] lets a job run only in a time window (here UTC); outside it, the job waits for the window. [Blackout] is the opposite.",
            $"On {D("/jobs/scheduled", "Scheduled")}: \"Outside the run window 03:00-03:30 (UTC); runs at ...\" (unless it is 03:00 UTC now).",
            "[RunWindow(\"03:00-03:30\")]",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<RuleJobs>(x => x.NightlyOnly("reindex"))))),

        new("throttle", "7. Scheduling rules", "Throttle: at most 2 at a time",
            "[Throttle(\"thumbnails\", MaxConcurrency = 2)] lets two jobs run together; the others wait without holding a worker.",
            $"{D("/jobs/processing", "Processing")} never shows more than two thumbnails. The others go through Scheduled with \"Throttled: 2 of 2 slot(s) in use\" and run as slots free up.",
            "[Throttle(\"thumbnails\", MaxConcurrency = 2)]",
            static c =>
            {
                IReadOnlyList<string?> ids = c.Jobs.EnqueueMany<RuleJobs, int>(Enumerable.Range(1, 6), (x, n) => x.Thumbnail(n));
                return Task.FromResult($"6 thumbnails, the first is {JobLink(ids[0])}");
            }),

        new("rate-limit", "7. Scheduling rules", "Rate limit: 3 per 30 seconds",
            "[RateLimit(\"sms\", 3, 30)]: at most 3 jobs start in any 30 seconds.",
            "Three SMS succeed at once; the five others are rescheduled with \"Rate limited: 3 job(s) per 30s\" and trickle through over a minute or two.",
            "[RateLimit(\"sms\", 3, 30)]",
            static c =>
            {
                IReadOnlyList<string?> ids = c.Jobs.EnqueueMany<RuleJobs, int>(Enumerable.Range(1, 8), (x, n) => x.SendSms(n));
                return Task.FromResult($"8 SMS, the last is {JobLink(ids[^1])}");
            }),

        new("duplicates", "7. Scheduling rules", "Prevent duplicates",
            "[PreventDuplicates] refuses a job identical to one still pending or running: Enqueue returns null. With ReplaceExisting the newer one wins instead.",
            "Customer 42: the second Enqueue returned no job. Price of SKU-1: the first job is Deleted (\"Replaced by a newer duplicate\") and the second runs with 9.99.",
            "[PreventDuplicates]\n[PreventDuplicates(\"price:{0}\", OnDuplicate = DuplicateHandling.ReplaceExisting)]",
            static c => Task.FromResult(
                "Sync customer 42: " + JobLink(c.Jobs.Enqueue<RuleJobs>(x => x.SyncCustomer(42))) + ", again: " + JobLink(c.Jobs.Enqueue<RuleJobs>(x => x.SyncCustomer(42))) +
                " · Price: " + JobLink(c.Jobs.Schedule<RuleJobs>(x => x.RecalculatePrice("SKU-1", 10.99m), TimeSpan.FromSeconds(10))) +
                ", then " + JobLink(c.Jobs.Schedule<RuleJobs>(x => x.RecalculatePrice("SKU-1", 9.99m), TimeSpan.FromSeconds(10))))),

        new("circuit", "7. Scheduling rules", "Circuit breaker",
            "[CircuitBreaker(\"payments-api\", failureThreshold: 3, ...)]: after 3 failures in a minute, the circuit opens and further jobs wait instead of hammering a broken service.",
            $"Three calls fail; the circuit opens (alert in the event log) and the other calls are rescheduled with \"Circuit 'payments-api' is open\". {D("/insights", "Insights")} lists the open circuit with a Reset button.",
            "[CircuitBreaker(\"payments-api\", failureThreshold: 3, windowSeconds: 60, openSeconds: 60)]",
            static c =>
            {
                IReadOnlyList<string?> ids = c.Jobs.EnqueueMany<RuleJobs, int>(Enumerable.Range(1, 6), (x, n) => x.CallPayments(n));
                return Task.FromResult($"6 calls, the last is {JobLink(ids[^1])}");
            }),

        new("deadline", "7. Scheduling rules", "Deadline",
            "[Deadline(\"00:00:03\")] doesn't stop the job; it flags it when it finishes later than 3 seconds after it was created, and raises an alert.",
            "Open the job while it runs: the deadline counts down; once it finished: \"deadline missed by ...\". The event log shows a DeadlineMissed alert.",
            "[Deadline(\"00:00:03\")]",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<RuleJobs>(x => x.SlowReport())))),

        new("long-running", "7. Scheduling rules", "Long-running jobs get their own worker",
            "[LongRunning] sends a job to the \"long-running\" queue, which a separate pool of workers takes (LongRunningWorkerCount = 1 here), so long jobs never take all the normal workers.",
            $"{D("/servers", "Servers")}: the server has 8 workers plus 1 for long-running jobs. Run this twice: the second waits on {D("/jobs/enqueued", "Enqueued")} in queue long-running until the first is done.",
            "[LongRunning]\nbuilder.Services.AddNightShiftServer(o => o.LongRunningWorkerCount = 1);",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<RuleJobs>(x => x.ReindexAsync(null!, CancellationToken.None))))),

        new("behavior", "7. Scheduling rules", "Display name, secrets, queue and invocation log",
            "[JobDisplayName] names the job from its arguments, [Redacted] hides an argument, [Queue] picks the queue whatever the caller says, and [LogInvocation] writes start and end to the console.",
            "Open the job: titled \"Send a password reset to ana@example.com\", the token shows as ••••••, the queue is critical, and the console has Starting/Finished lines.",
            "[Queue(\"critical\")]\n[JobDisplayName(\"Send a password reset to {0}\")]\n[LogInvocation]\npublic void PasswordReset(string email, [Redacted] string resetToken)",
            static c => Task.FromResult(JobLink(c.Jobs.Enqueue<RuleJobs>(x => x.PasswordReset("ana@example.com", "s3cr3t-t0ken"))))),

        new("tags", "7. Scheduling rules", "Tags and notes",
            "[Tag(\"customer:{0}\")] tags each job from its arguments; tags can also be added later. Notes record what people found out about a job.",
            $"{D("/tags", "Tags")}: invoices and customer:contoso. Open the job: its tags, and the note added by \"playground\". Try <kbd>Ctrl</kbd>+<kbd>K</kbd> then tag:contoso.",
            "[Tag(\"invoices\")] [Tag(\"customer:{0}\")]\nJobTags.Add(connection, jobId, [\"priority:high\"]);\nJobNotes.Add(connection, jobId, \"Customer asked for a copy\", user: \"playground\");",
            static c =>
            {
                string id = c.Jobs.Enqueue<RuleJobs>(x => x.InvoiceCustomer("contoso", 120))!;
                using IStorageConnection connection = c.Storage.GetConnection();
                JobTags.Add(connection, id, ["priority:high"]);
                JobNotes.Add(connection, id, "Customer asked for a copy", user: "playground");
                return Task.FromResult(JobLink(id));
            }),

        // ------------------------------------------------------------------ 8. Recurring
        new("recurring", "8. Recurring jobs", "A recurring job",
            "A job on a CRON schedule, with a time zone, a queue and what to do with missed runs.",
            $"{D("/recurring", "Recurring jobs")}: playground:digest runs every minute. Open its {D("/recurring/details/playground:digest", "details")}: next runs, the last runs, Trigger now, Pause, Edit (accepts plain English: \"every 5 minutes\").",
            "recurring.AddOrUpdate<RecurringJobs>(\"playground:digest\", x => x.Digest(null!), Cron.Minutely(),\n    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc, MisfireHandling = MisfireHandlingMode.Relaxed });",
            static c =>
            {
                c.Recurring.AddOrUpdate<RecurringJobs>("playground:digest", x => x.Digest(null!), Cron.Minutely(),
                    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc, MisfireHandling = MisfireHandlingMode.Relaxed });
                return Task.FromResult(Dashboard("/recurring/details/playground:digest", "playground:digest"));
            }),

        new("recurring-control", "8. Recurring jobs", "Trigger, pause, resume",
            "The same actions the dashboard has, from code. A paused job keeps its schedule but doesn't run; Resume doesn't catch up.",
            $"Press it once: the digest is triggered and paused (badge on {D("/recurring", "Recurring jobs")}). Press again: resumed.",
            "recurring.Trigger(\"playground:digest\");\nrecurring.Pause(\"playground:digest\");\nrecurring.Resume(\"playground:digest\");",
            static c =>
            {
                using IStorageConnection connection = c.Storage.GetConnection();
                RecurringJobDto? digest = connection.GetRecurringJobs().FirstOrDefault(x => x.Id == "playground:digest");
                if (digest == null)
                {
                    return Task.FromResult("Add the recurring job first (the step above).");
                }

                if (digest.Paused)
                {
                    c.Recurring.Resume("playground:digest");
                    return Task.FromResult("Resumed playground:digest");
                }

                c.Recurring.Trigger("playground:digest");
                c.Recurring.Pause("playground:digest");
                return Task.FromResult("Triggered, then paused playground:digest");
            }, "Toggle"),

        new("from-code", "8. Recurring jobs", "Recurring jobs declared in code",
            "AddNightShiftRecurringJobs declares recurring jobs and policies at startup: they are created or updated each time the app starts, and marked as coming from code.",
            $"{D("/recurring/details/code:heartbeat", "code:heartbeat")} has the From code badge. {D("/recurring/details/code:business-report", "code:business-report")} uses the business-hours policy: its next runs skip nights and weekends.",
            "builder.Services.AddNightShiftRecurringJobs(jobs => jobs\n    .AddPolicy(new SchedulePolicy(\"business-hours\").AllowWeekdays(...).AllowBetween(\"09:00\", \"17:00\"))\n    .Add<RecurringJobs>(\"code:heartbeat\", x => x.Heartbeat(), Cron.Every(TimeSpan.FromMinutes(2)))\n    .Add<RecurringJobs>(\"code:business-report\", x => x.BusinessReport(), Cron.Hourly(), new RecurringJobOptions { Policy = \"business-hours\" }));"),

        new("cron", "8. Recurring jobs", "CRON in plain English",
            "Cron helpers build expressions, read plain English, describe expressions and list the next occurrences.",
            "The result below shows what each call returns.",
            "Cron.FromText(\"weekdays at 9:30\");\nCron.Describe(\"*/15 9-17 * * 1-5\");\nCron.GetNextOccurrences(\"0 9 * * 1-5\", TimeZoneInfo.Utc, DateTime.UtcNow, 3);",
            static c =>
            {
                string fromText = Cron.FromText("weekdays at 9:30");
                string builder = Cron.Builder().EveryMinutes(15).Between(9, 17).OnWeekdays();
                IReadOnlyList<DateTime> next = Cron.GetNextOccurrences("0 9 * * 1-5", TimeZoneInfo.Utc, DateTime.UtcNow, 3);
                return Task.FromResult(Encode(
                    $"FromText(\"weekdays at 9:30\") = {fromText}\n" +
                    $"Builder().EveryMinutes(15).Between(9, 17).OnWeekdays() = {builder}\n" +
                    $"Describe(\"{builder}\") = {Cron.Describe(builder)}\n" +
                    $"Next 3 of \"0 9 * * 1-5\": {string.Join(", ", next.Select(x => x.ToString("ddd yyyy-MM-dd HH:mm")))} UTC"));
            }, "Show"),

        new("forecast", "8. Recurring jobs", "Forecast, export and import",
            "The dashboard can forecast how many runs are coming, and export or import all recurring jobs as JSON.",
            $"{D("/recurring/forecast", "Forecast")}: runs per hour for the next day. {D("/recurring", "Recurring jobs")}: Export downloads the JSON; Import takes it back (try changing a cron in it).",
            "string json = RecurringJobManifest.Export();\nRecurringJobManifest.Apply(json, prune: false);"),

        // ------------------------------------------------------------------ 9. Managing
        new("reschedule", "9. Managing jobs", "Reschedule and requeue from code",
            "Every dashboard action has an API: Reschedule, Requeue, Delete, ChangeState.",
            "A job due in 10 minutes is rescheduled to 10 seconds: its history shows both. The failed job from \"Fail at once\" (if any) is requeued.",
            "jobs.Reschedule(id, TimeSpan.FromSeconds(10));\njobs.Requeue(failedId);",
            static c =>
            {
                string id = c.Jobs.Schedule<BasicJobs>(x => x.Remind("rescheduled"), TimeSpan.FromMinutes(10))!;
                c.Jobs.Reschedule(id, TimeSpan.FromSeconds(10));
                return Task.FromResult("Rescheduled " + JobLink(id));
            }),

        new("wait", "9. Managing jobs", "Wait for a job and read its result",
            "WaitForCompletionAsync waits until a job reaches a final state; TryGetResult reads what it returned.",
            "The result below arrives when the job finished, with its return value.",
            "JobStateInfo? state = await storage.WaitForCompletionAsync(id, TimeSpan.FromSeconds(30));\nstorage.TryGetResult<int>(id, out int words);",
            static async c =>
            {
                string id = c.Jobs.Enqueue<BasicJobs>(x => x.CountWords("wait for me and read what I returned"))!;
                JobStateInfo? state = await c.Storage.WaitForCompletionAsync(id, TimeSpan.FromSeconds(30));
                c.Storage.TryGetResult(id, out int words);
                return $"{JobLink(id)} ended {Encode(state?.Name ?? "?")}, it returned {words}";
            }),

        new("delete-pending", "9. Managing jobs", "Delete pending jobs of a method",
            "DeletePendingJobs deletes every enqueued, scheduled or awaiting job of a method, for example when a feature is switched off.",
            $"Five newsletters are scheduled, then all deleted: {D("/jobs/deleted", "Deleted")} has five more.",
            "storage.DeletePendingJobs<NewsletterJobs>(nameof(NewsletterJobs.Send));",
            static c =>
            {
                for (int i = 1; i <= 5; i++)
                {
                    int edition = i;
                    c.Jobs.Schedule<NewsletterJobs>(x => x.Send($"edition {edition}"), TimeSpan.FromHours(edition));
                }

                int deleted = c.Storage.DeletePendingJobs<NewsletterJobs>(nameof(NewsletterJobs.Send));
                return Task.FromResult($"Scheduled 5 newsletters, deleted {deleted}");
            }),

        new("requeue-failed", "9. Managing jobs", "Requeue every failed job",
            "RequeueFailedJobs requeues failed jobs, all or those a filter picks (by exception type, method...). The Failed page has the same as Requeue all.",
            $"{D("/jobs/failed", "Failed")} empties; the jobs fail again (they're meant to).",
            "BulkJobResult result = storage.RequeueFailedJobs(job => true, TimeSpan.FromSeconds(10));",
            static c =>
            {
                BulkJobResult result = c.Storage.RequeueFailedJobs(static _ => true, TimeSpan.FromSeconds(10));
                return Task.FromResult($"Requeued {result.Processed} failed job(s)");
            }),

        // ------------------------------------------------------------------ 10. Monitoring
        new("monitors", "10. Monitoring", "Job monitors",
            $"Three monitors watch jobs: slow-report (a run longer than 10 s), flaky-sync (2 final failures within an hour) and daily-close (must succeed by {DailyCloseDeadline.Value:hh\\:mm} {DailyCloseDeadline.TimeZone.Id}, a few minutes after the app started).",
            $"This runs a 15 s SlowReport and two FlakySync failures. The event log shows a MonitorIncident for flaky-sync at once, one for slow-report after 10 to 15 s, then MonitorRecovered when the report ends (flaky-sync stays in trouble: it never succeeds). {D("/about", "About")} lists the monitors and their state. If you didn't run DailyClose before its deadline, it needs attention too: run it with the next step and watch it recover.",
            "config.UseJobMonitor(new LongRunningMonitor(\"slow-report\", \"MonitoredJobs.SlowReport\", TimeSpan.FromSeconds(10)))\n      .UseJobMonitor(new RepeatedFailuresMonitor(\"flaky-sync\", \"MonitoredJobs.FlakySync\", failures: 2, window: TimeSpan.FromHours(1)))\n      .UseJobMonitor(new DailyDeadlineMonitor(\"daily-close\", \"MonitoredJobs.DailyClose\", by, timeZone));",
            static c =>
            {
                c.Jobs.Enqueue<MonitoredJobs>(x => x.SlowReport());
                c.Jobs.Enqueue<MonitoredJobs>(x => x.FlakySync());
                c.Jobs.Enqueue<MonitoredJobs>(x => x.FlakySync());
                return Task.FromResult("Started a slow report and two failing syncs. " + MonitorStatus(c));
            }),

        new("monitor-status", "10. Monitoring", "Run the daily close, read the monitors",
            "Runs DailyClose (the job the daily-close monitor waits for), then reads every monitor's state from code.",
            "The result lists each monitor with its state (Healthy, Waiting, NeedsAttention) and the reason.",
            "jobs.Enqueue<MonitoredJobs>(x => x.DailyClose());\nJobMonitors.GetStatus(storage);",
            static c =>
            {
                c.Jobs.Enqueue<MonitoredJobs>(x => x.DailyClose());
                return Task.FromResult("DailyClose enqueued. " + MonitorStatus(c));
            }),

        new("telemetry", "10. Monitoring", "OpenTelemetry",
            "UseTelemetry emits a span when a job is created and one when it runs, plus metrics (duration, active, completed, retried, lateness). This turns on a listener for 60 seconds, as an OpenTelemetry exporter would listen, and runs three jobs.",
            "The event log shows \"create\" (Producer) and \"process\" (Consumer) spans with the same trace id, and each outcome: succeeded, failed, skipped.",
            "config.UseTelemetry();\nbuilder.Services.AddOpenTelemetry()\n    .WithTracing(t => t.AddSource(NightShiftTelemetry.ActivitySourceName))\n    .WithMetrics(m => m.AddMeter(NightShiftTelemetry.MeterName));",
            static c =>
            {
                TelemetryWatcher.WatchUntil = DateTime.UtcNow.AddSeconds(60);
                c.Jobs.Enqueue<BasicJobs>(x => x.Hello("telemetry"));
                c.Jobs.Enqueue<FailureJobs>(x => x.Broken("telemetry"));
                c.Jobs.Enqueue<FailureJobs>(x => x.AlreadyPaid(7));
                return Task.FromResult("Watching telemetry for 60 seconds");
            }),

        new("insights", "10. Monitoring", "Insights",
            "UseJobInsights records how long each method takes and how often it fails. This runs 40 jobs of varied length, some failing.",
            $"{D("/insights", "Insights")}: median, p95 and max per method, the failure rate, a sparkline, and the hour-by-day heatmap. {D("/wallboard", "Wallboard")} is the same for a TV screen.",
            "config.UseJobInsights();",
            static c =>
            {
                for (int i = 0; i < 40; i++)
                {
                    if (i % 8 == 0)
                    {
                        c.Jobs.Enqueue<FailureJobs>(x => x.Broken("insights sample"));
                    }
                    else
                    {
                        c.Jobs.Enqueue<OrderJobs>(x => x.ExportRegion($"region {i}", null!));
                    }
                }

                return Task.FromResult("40 jobs started: give them half a minute, then open Insights");
            }),

        new("activity", "10. Monitoring", "Activity, timeline and audit",
            "UseActivityFeed records every state change for the live views; the audit log records every dashboard action, with who did it.",
            $"{D("/", "Home")}: the live activity list. {D("/timeline", "Timeline")}: one lane per worker, each job a bar. Do something in the dashboard (delete a job), then open {D("/audit", "Audit")}.",
            "config.UseActivityFeed();"),

        new("health", "10. Monitoring", "Health checks and metrics",
            "The dashboard serves /health, /metrics (Prometheus) and /ping; ASP.NET Core health checks report the server in this process and the cluster.",
            $"{D("/health", "/nightshift/health")} (JSON, degraded while a queue has nobody listening), {D("/metrics", "/nightshift/metrics")}, <a href=\"/health/live\" target=\"_blank\">/health/live</a>, <a href=\"/health/ready\" target=\"_blank\">/health/ready</a>.",
            "builder.Services.AddHealthChecks()\n    .AddNightShift(o => o.Scope = NightShiftHealthCheckScope.Process, name: \"nightshift-server\", tags: [\"live\"]);"),

        // ------------------------------------------------------------------ 11. Operations
        new("drain", "11. Operations", "Pause all processing (drain mode)",
            "Before a deployment, pause processing: running jobs finish, new ones wait. Queues and single servers can be paused too.",
            $"A banner shows on every dashboard page; jobs you start now stay Scheduled with \"Held: processing is paused\". {D("/health", "/health")} says degraded. Press again to resume. {D("/servers", "Servers")} has the same buttons.",
            "DrainMode.Enable(connection, \"deploying 2.1\");\nDrainMode.Disable(connection);",
            static c =>
            {
                using IStorageConnection connection = c.Storage.GetConnection();
                if (DrainMode.Read(connection).Active)
                {
                    DrainMode.Disable(connection);
                    return Task.FromResult("Processing resumed");
                }

                DrainMode.Enable(connection, "Paused from the Playground");
                return Task.FromResult("Processing paused: try a step, its job will wait");
            }, "Toggle"),

        new("server", "11. Operations", "Stop and start the server",
            "IBackgroundJobServerControl stops and starts this process's server (for a maintenance window, or with StartMode = Manual).",
            $"Stop: the server leaves {D("/servers", "Servers")}, jobs pile up on Enqueued, <a href=\"/health/live\" target=\"_blank\">/health/live</a> turns unhealthy. Start: a new server id, and the backlog runs.",
            "await control.StopAsync();\nawait control.StartAsync();",
            static async c =>
            {
                IBackgroundJobServerControl control = c.Services.GetRequiredService<IBackgroundJobServerControl>();
                if (control.IsRunning)
                {
                    await control.StopAsync();
                    return "Server stopped";
                }

                await control.StartAsync();
                return "Server started";
            }, "Toggle"),

        new("read-only", "11. Operations", "A read-only dashboard",
            "A second dashboard for people who may look but not act: IsReadOnlyFunc hides every button and refuses every command.",
            "<a href=\"/nightshift-view\" target=\"_blank\">/nightshift-view</a>: the same pages, no Delete, Requeue or Run.",
            "app.MapNightShiftDashboard(\"/nightshift-view\", new DashboardOptions { IsReadOnlyFunc = _ => true });"),

        new("about", "11. Operations", "About, Servers and the command palette",
            "The dashboard explains its own configuration.",
            $"{D("/about", "About")}: compatibility level, serializer, global filters, retention, argument compression, the deployment (playground-1), functions and monitors. {D("/servers", "Servers")}: workers, queues, this process. Press <kbd>Ctrl</kbd>+<kbd>K</kbd> and type <code>&gt; pause</code> to run a command, or a job number to jump to it.",
            "config.UseDeploymentFencing(\"playground-1\").UseArgumentCompression(1024).UseStaleRestartLimit(3);"),
    ];

    private static string JobLink(string? id, string? label = null) => id == null
        ? $"<em>{Encode(label ?? "job")}: none created (a filter refused it)</em>"
        : $"<a href=\"/nightshift/jobs/details/{Encode(id)}\" target=\"_blank\">{Encode(label ?? "job")} #{Encode(id)}</a>";

    private static string Chain(JobChainResult? result) => result == null
        ? "<em>no chain created</em>"
        : Dashboard($"/chains/{result.ChainId}", $"open the chain ({result.Steps.Count} steps)");

    private static string Dashboard(string path, string text) => $"<a href=\"/nightshift{Encode(path)}\" target=\"_blank\">{Encode(text)}</a>";

    private static string D(string path, string text) => Dashboard(path, text);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string MonitorStatus(PlaygroundContext c) =>
        "Monitors: " + string.Join(" · ", JobMonitors.GetStatus(c.Storage).Select(x => Encode($"{x.Name}: {x.State}" + (x.Reason != null ? $" ({x.Reason})" : string.Empty))));
}
