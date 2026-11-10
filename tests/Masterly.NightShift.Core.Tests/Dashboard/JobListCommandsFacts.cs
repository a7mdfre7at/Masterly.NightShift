using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class JobListCommandsFacts
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IMonitoringApi> _monitor = new();
    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly Mock<IBackgroundJobClient> _client = new();
    private readonly List<string> _changed = [];

    public JobListCommandsFacts()
    {
        _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        _client
            .Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()))
            .Callback<string, IState, string>((id, _, _) => _changed.Add(id))
            .Returns(true);
    }

    [Fact]
    public void DeleteAll_DeletesEveryJob_PageByPage_UntilTheListIsEmpty()
    {
        Queue<List<string>> remaining = new([
            ["1", "2"],
            ["3"],
            [],
        ]);
        DashboardContextStub context = Create();

        BulkJobResult result = JobListCommands.DeleteAll(context, (_, from, count) => Items(remaining.Dequeue()), null, ScheduledState.StateName);

        Assert.Equal(3, result.Processed);
        Assert.True(result.Complete);
        Assert.Equal(0, result.Remaining);
        Assert.Equal(new[] { "1", "2", "3" }, _changed);
        _client.Verify(x => x.ChangeState("1", It.Is<IState>(s => s is DeletedState), ScheduledState.StateName));
    }

    [Fact]
    public void DeleteAll_Stops_WhenAPageReturnsOnlyJobsAlreadySeen()
    {
        DashboardContextStub context = Create();

        // A job that refuses to leave the list (its state changed meanwhile) must not loop forever
        BulkJobResult result = JobListCommands.DeleteAll(context, (_, from, count) => from == 0 ? Items("stuck") : Items(), null, FailedState.StateName);

        Assert.Equal(1, result.Processed);
        Assert.True(result.Complete);
        Assert.Equal(new[] { "stuck" }, _changed);
    }

    [Fact]
    public void DeleteAll_MovesPastJobsThatStayInTheList_AfterTheyWereDeleted()
    {
        // SQL Server keeps the queue row of a deleted job until a worker fetches it, so the enqueued list
        // still starts with the jobs deleted a moment ago
        List<string> queue = [.. Enumerable.Range(1, 1200).Select(i => i.ToString())];
        DashboardContextStub context = Create();

        BulkJobResult result = JobListCommands.DeleteAll(context, (_, from, count) => Items(queue.Skip(from).Take(count).ToArray()), null, EnqueuedState.StateName);

        Assert.Equal(1200, result.Processed);
        Assert.True(result.Complete);
        Assert.Equal(queue, _changed);
    }

    [Fact]
    public void DeleteAll_SkipsJobsNoLongerInTheListsState_WithoutChangingThem()
    {
        // A second click: the head of the queue holds rows of jobs deleted by the first one
        List<KeyValuePair<string, bool>> page =
        [
            .. Enumerable.Range(1, 600).Select(i => new KeyValuePair<string, bool>("old-" + i, false)),
            new KeyValuePair<string, bool>("new-1", true),
        ];
        DashboardContextStub context = Create();

        BulkJobResult result = JobListCommands.DeleteAll(context, (_, from, count) => page.Skip(from).Take(count), null, EnqueuedState.StateName);

        Assert.Equal(1, result.Processed);
        Assert.True(result.Complete);
        Assert.Equal(new[] { "new-1" }, _changed);
    }

    [Fact]
    public void DeleteAll_StopsAtTheWorkLimit_AndReportsWhatRemains()
    {
        // A list that never shrinks and never ends: every read finds jobs it hasn't seen
        int next = 0;
        _monitor.Setup(x => x.FailedCount()).Returns(123456);
        DashboardContextStub context = Create();

        BulkJobResult result = JobListCommands.DeleteAll(
            context,
            (_, from, count) => [.. Enumerable.Range(0, count).Select(_ => new KeyValuePair<string, bool>((next++).ToString(), true))],
            monitor => monitor.FailedCount(),
            FailedState.StateName);

        Assert.False(result.Complete);
        Assert.Equal(JobListCommands.BatchSize * JobListCommands.MaxReads, result.Processed);
        Assert.Equal(123456, result.Remaining);
    }

    [Fact, StaticLock]
    public void DeleteAll_StopsWhenItsTimeIsUp_AndReportsWhatRemains()
    {
        TimeSpan budget = JobListCommands.TimeBudget;
        JobListCommands.TimeBudget = TimeSpan.FromMilliseconds(100);

        try
        {
            // Each change takes 30 ms, like a slow storage: 500 jobs would take 15 s
            _client
                .Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()))
                .Callback<string, IState, string>((id, _, _) => { _changed.Add(id); System.Threading.Thread.Sleep(30); })
                .Returns(true);
            _monitor.Setup(x => x.FailedCount()).Returns(497);
            DashboardContextStub context = Create();

            BulkJobResult result = JobListCommands.DeleteAll(
                context,
                (_, from, count) => [.. Enumerable.Range(0, 500).Select(i => new KeyValuePair<string, bool>(i.ToString(), true))],
                monitor => monitor.FailedCount(),
                FailedState.StateName);

            Assert.False(result.Complete);
            Assert.InRange(result.Processed, 1, 10);
            Assert.Equal(_changed.Count, result.Processed);
            Assert.Equal(497, result.Remaining);
        }
        finally
        {
            JobListCommands.TimeBudget = budget;
        }
    }

    [Fact]
    public void RequeueAll_RequeuesEveryJob_ExpectingTheListsState()
    {
        Queue<List<string>> remaining = new([["1", "2"], []]);
        DashboardContextStub context = Create();

        BulkJobResult result = JobListCommands.RequeueAll(context, (_, from, count) => Items(remaining.Dequeue()), null, FailedState.StateName);

        Assert.Equal(2, result.Processed);
        _client.Verify(x => x.ChangeState("1", It.Is<EnqueuedState>(s => s.Reason == "Requeued via Dashboard UI (requeue all)"), FailedState.StateName));
        _client.Verify(x => x.ChangeState("2", It.Is<EnqueuedState>(s => s.Queue == EnqueuedState.DefaultQueue), FailedState.StateName));
    }

    [Fact]
    public void DeleteAll_DoesNotCountJobsTheClientRefusedToChange()
    {
        _client.Setup(x => x.ChangeState("taken", It.IsAny<IState>(), It.IsAny<string>())).Returns(false);
        DashboardContextStub context = Create();

        BulkJobResult result = JobListCommands.DeleteAll(context, (_, from, count) => from == 0 ? Items("taken", "free") : Items(), null, FailedState.StateName);

        Assert.Equal(1, result.Processed);
        Assert.True(result.Complete);
    }

    [Fact]
    public void DeleteAllFromSet_UsesTheSetRange_AndTheExpectedState()
    {
        int calls = 0;
        _connection.Setup(x => x.GetRangeFromSet("retries", 0, 499)).Returns(() => calls++ == 0 ? ["a", "b"] : []);
        DashboardContextStub context = Create();

        BulkJobResult? result = JobListCommands.DeleteAllFromSet(context, "retries", ScheduledState.StateName);

        Assert.Equal(2, result.Processed);
        Assert.Equal(new[] { "a", "b" }, _changed);
        _client.Verify(x => x.ChangeState("a", It.IsAny<DeletedState>(), ScheduledState.StateName));
    }

    [Fact]
    public void DeleteAllFromSet_ReturnsNull_AndDeletesNothing_WhenTheConnectionCanNotPageASet()
    {
        Mock<IStorageConnection> connection = new();
        _storage.Setup(x => x.GetConnection()).Returns(connection.Object);
        DashboardContextStub context = Create();

        BulkJobResult? result = JobListCommands.DeleteAllFromSet(context, "retries", ScheduledState.StateName);

        Assert.Null(result);
        Assert.Empty(_changed);
        connection.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public void DeleteAllFromSet_ReturnsNull_WithoutTheExtendedApi_EvenWithAJobStorageConnection()
    {
        // The base GetRangeFromSet throws NotSupportedException: delete-all answered 500 instead of 422
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);
        _connection.Setup(x => x.GetRangeFromSet(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>())).Throws(new NotSupportedException());
        DashboardContextStub context = Create();

        BulkJobResult? result = JobListCommands.DeleteAllFromSet(context, "retries", ScheduledState.StateName);

        Assert.Null(result);
        Assert.Empty(_changed);
    }

    [Fact]
    public void RemoveAllRecurringJobs_RemovesEveryRegisteredJob()
    {
        _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(["nightly", "weekly"]);
        Mock<IRecurringJobManager> manager = new();
        DashboardContextStub context = Create();
        context.RecurringJobManager = manager.Object;

        BulkJobResult result = JobListCommands.RemoveAllRecurringJobs(context);

        Assert.Equal(2, result.Processed);
        Assert.True(result.Complete);
        manager.Verify(x => x.RemoveIfExists("nightly"));
        manager.Verify(x => x.RemoveIfExists("weekly"));
    }

    [Fact, StaticLock]
    public void RemoveAllRecurringJobs_StopsWhenItsTimeIsUp_AndSaysHowManyAreLeft()
    {
        _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(["a", "b", "c"]);
        Mock<IRecurringJobManager> manager = new();
        DashboardContextStub context = Create();
        context.RecurringJobManager = manager.Object;
        TimeSpan budget = JobListCommands.TimeBudget;
        JobListCommands.TimeBudget = TimeSpan.Zero;
        try
        {
            BulkJobResult result = JobListCommands.RemoveAllRecurringJobs(context);

            Assert.Equal(1, result.Processed);
            Assert.False(result.Complete);
            Assert.Equal(2, result.Remaining);
            manager.Verify(x => x.RemoveIfExists("a"));
            manager.Verify(x => x.RemoveIfExists(It.IsAny<string>()), Times.Once);
        }
        finally
        {
            JobListCommands.TimeBudget = budget;
        }
    }

    [Fact, StaticLock]
    public async Task Dispatcher_RecordsTheAttempt_WhenTheCommandFailsMidway()
    {
        List<string> list = [];
        Mock<IWriteOnlyTransaction> transaction = new();
        transaction.Setup(x => x.InsertToList(AuditLog.ListKey, It.IsAny<string>())).Callback<string, string>((_, value) => list.Add(value));
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
        DashboardContextStub context = Create();
        JobListCommandDispatcher dispatcher = new(_ => throw new InvalidOperationException("storage down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.Dispatch(context));

        Assert.Contains("\"s\":500", Assert.Single(list));
    }

    [Fact]
    public async Task Dispatcher_AnswersWithWhatWasDone_AsJson()
    {
        DashboardContextStub context = Create();
        JobListCommandDispatcher dispatcher = new(_ => new BulkJobResult(7, 0, true));

        await dispatcher.Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal("{\"processed\":7,\"remaining\":0,\"complete\":true}", context.Response.Text);
    }

    [Fact]
    public async Task Dispatcher_Answers422_WhenTheStorageCanNotRunTheCommand()
    {
        DashboardContextStub context = Create();
        JobListCommandDispatcher dispatcher = new(_ => null);

        await dispatcher.Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
    }

    [Fact]
    public async Task Dispatcher_Answers405_ForGet_And401_ForReadOnlyUsers()
    {
        bool called = false;
        JobListCommandDispatcher dispatcher = new(_ => { called = true; return new BulkJobResult(0, 0, true); });
        DashboardContextStub get = new(new DashboardOptions(), new DashboardRequestStub("GET"), _storage.Object);
        DashboardContextStub readOnly = new(new DashboardOptions { IsReadOnlyFunc = static _ => true }, new DashboardRequestStub("POST"), _storage.Object);

        await dispatcher.Dispatch(get);
        await dispatcher.Dispatch(readOnly);

        Assert.Equal(405, get.Response.StatusCode);
        Assert.Equal(401, readOnly.Response.StatusCode);
        Assert.False(called);
    }

    private static List<KeyValuePair<string, bool>> Items(params string[] ids)
    {
        return [.. ids.Select(static id => new KeyValuePair<string, bool>(id, true))];
    }

    private static List<KeyValuePair<string, bool>> Items(List<string> ids)
    {
        return Items(ids.ToArray());
    }

    private DashboardContextStub Create()
    {
        return new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("POST"), _storage.Object)
        {
            BackgroundJobClient = _client.Object,
        };
    }
}
