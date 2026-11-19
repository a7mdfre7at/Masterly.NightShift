using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class JobDiagnosticsFacts : IDisposable
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IMonitoringApi> _monitor = new();
    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly List<ServerDto> _servers = [];
    private readonly List<QueueWithTopEnqueuedJobsDto> _queues = [];
    private readonly Dictionary<string, JobData> _jobs = [];
    private readonly Dictionary<string, string> _control = [];

    public JobDiagnosticsFacts()
    {
        _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _monitor.Setup(x => x.Servers()).Returns(_servers);
        _monitor.Setup(x => x.Queues()).Returns(_queues);
        _connection.Setup(x => x.GetJobData(It.IsAny<string>())).Returns<string>(id => _jobs.TryGetValue(id, out JobData? data) ? data : null);
        _connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(_control);
        _connection.Setup(x => x.GetAllEntriesFromHash(It.Is<string>(k => k.StartsWith("insights:")))).Returns(default(Dictionary<string, string>));
        DrainMode.InvalidateCache();
    }

    public void Dispose()
    {
        DrainMode.InvalidateCache();
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void Work() { }

    private RazorPage CreatePage()
    {
        JobDetailsPage page = new("1");
        page.Assign(new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), _storage.Object));
        return page;
    }

    private static JobDetailsDto Details(string state, Dictionary<string, string> data = null, Dictionary<string, string> properties = null, DateTime? createdAt = null, string reason = null)
    {
        return new JobDetailsDto
        {
            Job = Job.FromExpression(() => Work()),
            CreatedAt = createdAt ?? DateTime.UtcNow,
            Properties = properties ?? [],
            History =
            [
                new() { StateName = state, CreatedAt = createdAt ?? DateTime.UtcNow, Data = data ?? [], Reason = reason },
            ],
        };
    }

    private void AddServer(string name, DateTime? heartbeat, params string[] queues)
    {
        _servers.Add(new ServerDto { Name = name, Heartbeat = heartbeat, StartedAt = DateTime.UtcNow.AddHours(-1), Queues = queues, WorkersCount = 5 });
    }

    private static string[] Codes(IEnumerable<JobDiagnostic> findings) => [.. findings.Select(x => x.Code)];

    [Fact, StaticLock]
    public void Enqueued_WithoutServers_IsAProblem()
    {
        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("no-servers", finding.Code);
        Assert.Equal(JobDiagnosticSeverity.Problem, finding.Severity);
        Assert.EndsWith("/servers", finding.Link);
    }

    [Fact, StaticLock]
    public void Enqueued_WhenNoServerListensToTheQueue_NamesTheServedQueues()
    {
        AddServer("web-1", DateTime.UtcNow, "default", "critical");

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "reports" } }));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("queue-unserved", finding.Code);
        Assert.Contains("reports", finding.Message.ToString());
        Assert.Contains("critical", finding.Message.ToString());
    }

    [Fact, StaticLock]
    public void Enqueued_WithStaleServersOnly_IsAProblem()
    {
        AddServer("web-1", DateTime.UtcNow.AddHours(-1), "default");

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }));

        Assert.Equal(new[] { "no-servers" }, Codes(findings));
    }

    [Fact, StaticLock]
    public void Enqueued_WhileDraining_SaysProcessingIsPaused()
    {
        AddServer("web-1", DateTime.UtcNow, "default");
        _control["Drain"] = "1";
        _control["DrainReason"] = "deploying";

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("drain", finding.Code);
        Assert.Contains("deploying", finding.Message.ToString());
    }

    [Fact, StaticLock]
    public void Enqueued_ServedAndFresh_IsHealthy()
    {
        AddServer("web-1", DateTime.UtcNow, "default");

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("healthy", finding.Code);
        Assert.Equal(JobDiagnosticSeverity.Info, finding.Severity);
    }

    [Fact, StaticLock]
    public void Enqueued_ForLong_ReportsTheWait_WithTheQueueLength()
    {
        AddServer("web-1", DateTime.UtcNow, "default");
        _queues.Add(new QueueWithTopEnqueuedJobsDto { Name = "default", Length = 120, FirstJobs = new JobList<EnqueuedJobDto>([]) });

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }, createdAt: DateTime.UtcNow.AddMinutes(-5)));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("waiting", finding.Code);
        Assert.Contains("120", finding.Message.ToString());
    }

    [Fact, StaticLock]
    public void Scheduled_InTheFuture_ReportsWhenItIsDue()
    {
        AddServer("web-1", DateTime.UtcNow, "default");
        Dictionary<string, string> data = new() { { "EnqueueAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(10)) } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(ScheduledState.StateName, data, reason: "Retry attempt 2 of 10: boom"));

        Assert.Equal(new[] { "retry", "due" }, Codes(findings));
    }

    [Fact, StaticLock]
    public void Scheduled_Overdue_IsAProblem_AndStillChecksTheQueue()
    {
        AddServer("web-1", DateTime.UtcNow, "other");
        Dictionary<string, string> data = new() { { "EnqueueAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-10)) } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(ScheduledState.StateName, data));

        Assert.Equal(new[] { "overdue", "queue-unserved" }, Codes(findings));
        Assert.Equal(JobDiagnosticSeverity.Problem, findings[0].Severity);
    }

    [Fact, StaticLock]
    public void Awaiting_WithAMissingParent_IsAProblem()
    {
        Dictionary<string, string> data = new() { { "ParentId", "77" }, { "Options", "OnlyOnSucceededState" } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(AwaitingState.StateName, data));

        Assert.Equal(new[] { "parent-missing" }, Codes(findings));
    }

    [Fact, StaticLock]
    public void Awaiting_WhoseParentFailedTheCondition_ExplainsIt()
    {
        _jobs["77"] = new JobData { State = DeletedState.StateName, Job = Job.FromExpression(() => Work()) };
        Dictionary<string, string> data = new() { { "ParentId", "77" }, { "Options", "OnlyOnSucceededState" } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(AwaitingState.StateName, data));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("condition-failed", finding.Code);
        Assert.Equal(JobDiagnosticSeverity.Problem, finding.Severity);
    }

    [Fact, StaticLock]
    public void Awaiting_WithARunningParent_IsInformational()
    {
        _jobs["77"] = new JobData { State = ProcessingState.StateName, Job = Job.FromExpression(() => Work()) };
        Dictionary<string, string> data = new() { { "ParentId", "77" } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(AwaitingState.StateName, data));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("parent-waiting", finding.Code);
        Assert.Equal(JobDiagnosticSeverity.Info, finding.Severity);
    }

    [Fact, StaticLock]
    public void Awaiting_FanIn_SummarizesTheParents()
    {
        _jobs["a"] = new JobData { State = SucceededState.StateName, Job = Job.FromExpression(() => Work()) };
        _jobs["b"] = new JobData { State = ProcessingState.StateName, Job = Job.FromExpression(() => Work()) };
        AwaitingJobsState state = new(["a", "b"], new EnqueuedState(), JobContinuationOptions.OnAnyFinishedState, FanInMode.All);

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(AwaitingState.StateName, state.SerializeData()));

        Assert.Equal("fan-in", findings[0].Code);
        Assert.Contains("1", findings[0].Message.ToString());
        Assert.Contains("parent-waiting", Codes(findings));
    }

    [Fact, StaticLock]
    public void Awaiting_FanInOverManyParents_ReadsOnlyTheFirstOnes_AndListsAFewLines()
    {
        // A continuation of a batch of 5,000 jobs read every parent and wrote a line for each: 10 s, 6 MB
        string[] ids = [.. Enumerable.Range(1, 5000).Select(static i => i.ToString(CultureInfo.InvariantCulture))];
        foreach (string? id in ids)
        {
            _jobs[id] = new JobData { State = ScheduledState.StateName, Job = Job.FromExpression(() => Work()) };
        }

        _jobs["3"] = new JobData { State = FailedState.StateName, Job = Job.FromExpression(() => Work()) };
        AwaitingJobsState state = new(ids, new EnqueuedState(), JobContinuationOptions.OnAnyFinishedState, FanInMode.All);

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(AwaitingState.StateName, state.SerializeData()));

        _connection.Verify(x => x.GetJobData(It.IsAny<string>()), Times.Exactly(JobDiagnostics.MaxParentsRead));
        Assert.Equal("fan-in", findings[0].Code);
        Assert.Contains(JobDiagnostics.MaxParentsRead.ToString(CultureInfo.CurrentCulture), findings[0].Message.ToString());
        Assert.Contains("5000", findings[0].Message.ToString());
        // The failed parent comes first, before the waiting ones
        Assert.Equal("parent-failed", findings[1].Code);
        Assert.Equal(JobDiagnostics.MaxParentLines, findings.Count(static x => x.Code.StartsWith("parent-", StringComparison.Ordinal)));
        JobDiagnostic more = Assert.Single(findings, static x => x.Code == "parents-not-listed");
        Assert.Contains((5000 - JobDiagnostics.MaxParentLines).ToString(CultureInfo.CurrentCulture), more.Message.ToString());
    }

    [Fact, StaticLock]
    public void Awaiting_BatchContinuation_CountsTheBatch_WithItsCounters()
    {
        string[] ids = [.. Enumerable.Range(1, 5000).Select(static i => i.ToString(CultureInfo.InvariantCulture))];
        AwaitingJobsState state = new(ids, new EnqueuedState(), JobContinuationOptions.OnAnyFinishedState, FanInMode.All);
        JobDetailsDto details = Details(AwaitingState.StateName, state.SerializeData(), new Dictionary<string, string> { { BatchJob.ContinuationParameterName, "\"b1\"" } });
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        _connection.Setup(x => x.GetAllEntriesFromHash(BatchJob.GetHashKey("b1"))).Returns(new Dictionary<string, string> { { "Description", "import" } });
        _connection.Setup(x => x.GetCounter(It.IsAny<string>())).Returns(0L);
        _connection.Setup(x => x.GetCounter(BatchJob.GetTotalKey("b1"))).Returns(5000L);
        _connection.Setup(x => x.GetCounter(BatchJob.GetStateKey("b1", SucceededState.StateName))).Returns(4990L);

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", details);

        Assert.Equal("fan-in", findings[0].Code);
        Assert.Contains("4990", findings[0].Message.ToString());
        Assert.Contains("5000", findings[0].Message.ToString());
        JobDiagnostic more = Assert.Single(findings, static x => x.Code == "parents-not-listed");
        Assert.EndsWith("/batches/b1", more.Link);
        _connection.Verify(x => x.GetJobData(It.IsAny<string>()), Times.Exactly(JobDiagnostics.MaxParentsRead));
    }

    [Fact, StaticLock]
    public void Processing_OnAnUnknownServer_IsAProblem()
    {
        Dictionary<string, string> data = new() { { "ServerId", "gone-1" }, { "StartedAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-1)) } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(ProcessingState.StateName, data));

        Assert.Equal(new[] { "server-gone" }, Codes(findings));
    }

    [Fact, StaticLock]
    public void Processing_OnALiveServer_ReportsTheRun()
    {
        AddServer("web-1", DateTime.UtcNow, "default");
        Dictionary<string, string> data = new() { { "ServerId", "web-1" }, { "StartedAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-1)) } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(ProcessingState.StateName, data));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("running", finding.Code);
        Assert.Contains("web-1", finding.Message.ToString());
    }

    [Fact, StaticLock]
    public void Failed_AfterRetries_SaysTheyAreExhausted()
    {
        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(FailedState.StateName, properties: new Dictionary<string, string> { { "RetryCount", "10" }, { AutomaticRetryAttribute.RetryLaterCountParameter, "3" } }));

        Assert.Equal(new[] { "retries-exhausted", "retry-later-exhausted" }, Codes(findings));
        Assert.Contains("10", findings[0].Message.ToString());
    }

    [Fact, StaticLock]
    public void Failed_WithoutRetries_SaysWhy()
    {
        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(FailedState.StateName));

        Assert.Equal(new[] { "not-retried" }, Codes(findings));
    }

    [Fact, StaticLock]
    public void Diagnose_WhenTheStorageThrows_ReportsAnEncodedError()
    {
        _monitor.Setup(x => x.Servers()).Throws(new InvalidOperationException("storage <down>"));

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(EnqueuedState.StateName));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("error", finding.Code);
        Assert.Equal(JobDiagnosticSeverity.Warning, finding.Severity);
        Assert.Contains("storage &lt;down&gt;", finding.Message.ToString());
        Assert.Null(finding.Link);
    }

    [Fact, StaticLock]
    public void Diagnose_WithoutHistory_HasNoFindings()
    {
        JobDetailsDto details = Details(EnqueuedState.StateName);
        details.History = [];

        Assert.Empty(JobDiagnostics.Diagnose(CreatePage(), "1", details));
    }

    [Fact, StaticLock]
    public void Diagnose_ThrowsOnNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => JobDiagnostics.Diagnose(null, "1", Details(EnqueuedState.StateName)));
        Assert.Throws<ArgumentNullException>(() => JobDiagnostics.Diagnose(CreatePage(), "1", null));
    }

    [Fact, StaticLock]
    public void Enqueued_InAPausedQueue_SaysTheQueueIsPaused_WithTheReason()
    {
        AddServer("web-1", DateTime.UtcNow, "default");
        _control[DrainMode.QueueFieldPrefix + "default"] = "maintenance";

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } }));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("queue-paused", finding.Code);
        Assert.Equal(JobDiagnosticSeverity.Warning, finding.Severity);
        Assert.Contains(": maintenance", finding.Message.ToString());
        Assert.EndsWith("/jobs/enqueued", finding.Link);
    }

    [Fact, StaticLock]
    public void Enqueued_WithItsOwnQueue_IsDiagnosedForThatQueue_NotDefault()
    {
        // Enqueue("reports", ...) records "default" in the state; the job waits in "reports"
        AddServer("web-1", DateTime.UtcNow, "default", "emails");
        JobDetailsDto details = Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } });
        details.Job = new Job(typeof(JobDiagnosticsFacts), typeof(JobDiagnosticsFacts).GetMethod(nameof(Work)), [], "reports");

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", details);

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("queue-unserved", finding.Code);
        Assert.Contains("reports", finding.Message.ToString());
    }

    [Fact, StaticLock]
    public void Enqueued_WithItsOwnQueue_ReportsThePauseOfThatQueue()
    {
        AddServer("web-1", DateTime.UtcNow, "reports");
        _control[DrainMode.QueueFieldPrefix + "reports"] = "month end";
        JobDetailsDto details = Details(EnqueuedState.StateName, new Dictionary<string, string> { { "Queue", "default" } });
        details.Job = new Job(typeof(JobDiagnosticsFacts), typeof(JobDiagnosticsFacts).GetMethod(nameof(Work)), [], "reports");

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", details);

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("queue-paused", finding.Code);
        Assert.Contains("month end", finding.Message.ToString());
    }

    [Fact, StaticLock]
    public void Enqueued_InAQueuePausedWithoutAReason_OmitsTheReason()
    {
        AddServer("web-1", DateTime.UtcNow, "default");
        _control[DrainMode.QueueFieldPrefix + "default"] = string.Empty;

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(EnqueuedState.StateName));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("queue-paused", finding.Code);
        Assert.DoesNotContain(": ", finding.Message.ToString());
    }

    [Fact, StaticLock]
    public void Enqueued_WhenAnotherQueueIsPaused_IsHealthy()
    {
        AddServer("web-1", DateTime.UtcNow, "default");
        _control[DrainMode.QueueFieldPrefix + "reports"] = "maintenance";

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(EnqueuedState.StateName));

        Assert.Equal(new[] { "healthy" }, Codes(findings));
    }

    [Fact, StaticLock]
    public void Scheduled_ByRetryLater_SaysTheJobChoseTheTime()
    {
        AddServer("web-1", DateTime.UtcNow, "default");
        Dictionary<string, string> data = new() { { "EnqueueAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(10)) } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(ScheduledState.StateName, data, reason: "Retry later (1 of 5): rate limited"));

        Assert.Equal(new[] { "retry-later", "due" }, Codes(findings));
        Assert.Equal(JobDiagnosticSeverity.Info, findings[0].Severity);
        Assert.Null(findings[0].Link);
    }

    [Fact, StaticLock]
    public void Scheduled_OverdueWithoutLiveServers_IsAProblem_AndReportsTheMissingServers()
    {
        Dictionary<string, string> data = new() { { "EnqueueAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-10)) } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(ScheduledState.StateName, data, reason: "Some other reason"));

        Assert.Equal(new[] { "overdue", "no-servers" }, Codes(findings));
    }

    [Fact, StaticLock]
    public void Awaiting_WhoseParentFailed_IsAProblem()
    {
        _jobs["77"] = new JobData { State = FailedState.StateName, Job = Job.FromExpression(() => Work()) };
        Dictionary<string, string> data = new() { { "ParentId", "77" } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(AwaitingState.StateName, data));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("parent-failed", finding.Code);
        Assert.Equal(JobDiagnosticSeverity.Problem, finding.Severity);
        Assert.Contains("77", finding.Message.ToString());
    }

    [Fact, StaticLock]
    public void Awaiting_WhoseParentIsScheduledForARetry_SaysItIsRetrying()
    {
        _jobs["77"] = new JobData { State = ScheduledState.StateName, Job = Job.FromExpression(() => Work()) };
        _connection.Setup(x => x.GetStateData("77")).Returns(new StateData { Name = ScheduledState.StateName, Reason = "Retry attempt 1 of 10: boom" });
        Dictionary<string, string> data = new() { { "ParentId", "77" } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(AwaitingState.StateName, data));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("parent-retrying", finding.Code);
        Assert.Equal(JobDiagnosticSeverity.Info, finding.Severity);
    }

    [Fact, StaticLock]
    public void Awaiting_WhoseParentIsScheduledForOtherReasons_SaysItIsWaiting()
    {
        _jobs["77"] = new JobData { State = ScheduledState.StateName, Job = Job.FromExpression(() => Work()) };
        _connection.Setup(x => x.GetStateData("77")).Returns(new StateData { Name = ScheduledState.StateName, Reason = "Scheduled by the user" });
        Dictionary<string, string> data = new() { { "ParentId", "77" } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(AwaitingState.StateName, data));

        Assert.Equal(new[] { "parent-waiting" }, Codes(findings));
    }

    [Fact, StaticLock]
    public void Awaiting_WhoseScheduledParentHasNoStateData_SaysItIsWaiting()
    {
        _jobs["77"] = new JobData { State = ScheduledState.StateName, Job = Job.FromExpression(() => Work()) };
        _connection.Setup(x => x.GetStateData("77")).Returns(default(StateData));
        Dictionary<string, string> data = new() { { "ParentId", "77" } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(AwaitingState.StateName, data));

        Assert.Equal(new[] { "parent-waiting" }, Codes(findings));
    }

    [Theory, StaticLock]
    [InlineData("OnAnyFinishedState", "Succeeded")]
    [InlineData("OnAnyFinishedState", "Deleted")]
    [InlineData("OnlyOnDeletedState", "Deleted")]
    [InlineData("OnlyOnSucceededState", "Succeeded")]
    [InlineData("NotAnOption", "Succeeded")]
    [InlineData("", "Deleted")]
    public void Awaiting_WhoseParentFinishedAndMetTheCondition_SaysItShouldHaveStarted(string options, string parentState)
    {
        _jobs["77"] = new JobData { State = parentState, Job = Job.FromExpression(() => Work()) };
        Dictionary<string, string> data = new() { { "ParentId", "77" }, { "Options", options } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(AwaitingState.StateName, data));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("parent-finished", finding.Code);
        Assert.Equal(JobDiagnosticSeverity.Warning, finding.Severity);
    }

    [Fact, StaticLock]
    public void Awaiting_OnlyOnDeleted_WhoseParentSucceeded_FailedTheCondition()
    {
        _jobs["77"] = new JobData { State = SucceededState.StateName, Job = Job.FromExpression(() => Work()) };
        Dictionary<string, string> data = new() { { "ParentId", "77" }, { "Options", "OnlyOnDeletedState" } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(AwaitingState.StateName, data));

        Assert.Equal(new[] { "condition-failed" }, Codes(findings));
    }

    [Fact, StaticLock]
    public void Awaiting_WithoutAParent_HasNoFindings()
    {
        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(AwaitingState.StateName, new Dictionary<string, string> { { "ParentId", "" } }));

        Assert.Empty(findings);
    }

    [Fact, StaticLock]
    public void Awaiting_FanInAny_WithEveryParentDone_CountsThem_AndReportsNoPerParentFinding()
    {
        _jobs["a"] = new JobData { State = SucceededState.StateName, Job = Job.FromExpression(() => Work()) };
        _jobs["b"] = new JobData { State = DeletedState.StateName, Job = Job.FromExpression(() => Work()) };
        AwaitingJobsState state = new(["a", "b"], new EnqueuedState(), JobContinuationOptions.OnAnyFinishedState, FanInMode.Any);

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(AwaitingState.StateName, state.SerializeData()));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("fan-in", finding.Code);
        Assert.Contains("2", finding.Message.ToString());
    }

    [Fact, StaticLock]
    public void Processing_OnAServerThatMissedItsHeartbeat_IsAProblem()
    {
        AddServer("web-1", DateTime.UtcNow.AddHours(-1), "default");
        Dictionary<string, string> data = new() { { "ServerId", "web-1" }, { "StartedAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-1)) } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(ProcessingState.StateName, data));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("server-stale", finding.Code);
        Assert.Equal(JobDiagnosticSeverity.Problem, finding.Severity);
        Assert.Contains("web-1", finding.Message.ToString());
        Assert.EndsWith("/servers", finding.Link);
    }

    [Fact, StaticLock]
    public void Processing_MuchLongerThanUsual_IsCalledOut()
    {
        AddServer("web-1", DateTime.UtcNow, "default");
        RecordDurations("1000", "1000", "1000");
        Dictionary<string, string> data = new() { { "ServerId", "web-1" }, { "StartedAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-10)) } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(ProcessingState.StateName, data));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("slow", finding.Code);
        Assert.Equal(JobDiagnosticSeverity.Warning, finding.Severity);
        Assert.EndsWith("/insights", finding.Link);
    }

    [Fact, StaticLock]
    public void Processing_WithinTheUsualDuration_ReportsTheRunAndTheUsualDuration()
    {
        AddServer("web-1", DateTime.UtcNow, "default");
        RecordDurations("60000", "60000", "60000");
        Dictionary<string, string> data = new() { { "ServerId", "web-1" }, { "StartedAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddSeconds(-10)) } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(ProcessingState.StateName, data));

        JobDiagnostic finding = Assert.Single(findings);
        Assert.Equal("running", finding.Code);
        Assert.Contains("web-1", finding.Message.ToString());
    }

    [Fact, StaticLock]
    public void Processing_WithTooFewRecordedRuns_IgnoresTheHistory()
    {
        AddServer("web-1", DateTime.UtcNow, "default");
        RecordDurations("10", "10");
        Dictionary<string, string> data = new() { { "ServerId", "web-1" }, { "StartedAt", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-10)) } };

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(ProcessingState.StateName, data));

        Assert.Equal(new[] { "running" }, Codes(findings));
    }

    [Fact, StaticLock]
    public void Processing_WithoutAServerId_HasNoFindings()
    {
        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(ProcessingState.StateName));

        Assert.Empty(findings);
    }

    [Fact, StaticLock]
    public void Failed_WithRetriesDisabled_SaysSo()
    {
        JobDetailsDto details = Details(FailedState.StateName);
        details.Job = Job.FromExpression(() => NoRetries());

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", details);

        Assert.Equal(new[] { "retries-disabled" }, Codes(findings));
    }

    [Fact, StaticLock]
    public void Failed_WithMalformedRetryCounters_TreatsThemAsZero()
    {
        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", Details(FailedState.StateName, properties: new Dictionary<string, string> { { "RetryCount", "many" }, { AutomaticRetryAttribute.RetryLaterCountParameter, "x" } }));

        Assert.Equal(new[] { "not-retried" }, Codes(findings));
    }

    [Fact, StaticLock]
    public void AnyState_WithAnOpenCircuit_ReportsIt_AndSkipsUnformattableResources()
    {
        AddServer("web-1", DateTime.UtcNow, "default");
        SetCircuit("payments-api", CircuitBreakerAttribute.OpenState);
        JobDetailsDto details = Details(EnqueuedState.StateName);
        details.Job = Job.FromExpression(() => CircuitJobs.Pay());

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", details);

        Assert.Equal(new[] { "healthy", "circuit-open" }, Codes(findings));
        Assert.Equal(JobDiagnosticSeverity.Warning, findings[1].Severity);
        Assert.Contains("payments-api", findings[1].Message.ToString());
        Assert.EndsWith("/insights", findings[1].Link);
    }

    [Fact, StaticLock]
    public void AnyState_WithAClosedCircuit_ReportsNothingAboutIt()
    {
        AddServer("web-1", DateTime.UtcNow, "default");
        SetCircuit("payments-api", CircuitBreakerAttribute.ClosedState);
        JobDetailsDto details = Details(EnqueuedState.StateName);
        details.Job = Job.FromExpression(() => CircuitJobs.Pay());

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", details);

        Assert.Equal(new[] { "healthy" }, Codes(findings));
    }

    [Fact, StaticLock]
    public void Failed_WithAnOpenCircuit_DoesNotReportTheCircuit()
    {
        SetCircuit("payments-api", CircuitBreakerAttribute.OpenState);
        JobDetailsDto details = Details(FailedState.StateName);
        details.Job = Job.FromExpression(() => CircuitJobs.Pay());

        IReadOnlyList<JobDiagnostic> findings = JobDiagnostics.Diagnose(CreatePage(), "1", details);

        Assert.Equal(new[] { "not-retried" }, Codes(findings));
    }

    private void RecordDurations(params string[] durations)
    {
        string key = JobInsightsAttribute.GetKey(Job.FromExpression(() => Work()));
        _connection.Setup(x => x.GetAllEntriesFromHash("insights:" + key)).Returns(new Dictionary<string, string> { { "LastDurationMs", durations[0] } });
        _connection.Setup(x => x.GetAllItemsFromList("insights:" + key + ":durations")).Returns([.. durations]);
    }

    private void SetCircuit(string resource, string state)
    {
        _connection.Setup(x => x.GetAllEntriesFromHash("circuit:" + resource)).Returns(new Dictionary<string, string>
        {
            { "State", state },
            { "OpenedAt", JobHelper.ToTimestamp(DateTime.UtcNow.AddMinutes(-2)).ToString(CultureInfo.InvariantCulture) },
        });
        _connection.Setup(x => x.GetAllItemsFromSet("circuit:" + resource + ":failures")).Returns([]);
    }

    [AutomaticRetry(Attempts = 0)]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void NoRetries() { }

    [CircuitBreaker("payments-api", 3, 60, 60)]
    public static class CircuitJobs
    {
        // The template refers to an argument the method doesn't have, so the resource can't be formatted
        [CircuitBreaker("{3}", 3, 60, 60)]
        public static void Pay() { }
    }

    [Fact, StaticLock]
    public void FinalStates_HaveNoFindings()
    {
        Assert.Empty(JobDiagnostics.Diagnose(CreatePage(), "1", Details(SucceededState.StateName)));
        Assert.Empty(JobDiagnostics.Diagnose(CreatePage(), "1", Details(DeletedState.StateName)));
    }
}
