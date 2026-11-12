using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class BatchCommandDispatcherFacts
{
    [Fact]
    public void Dispatch_Sets401StatusCode_WhenNotPermitted()
    {
        DashboardOptions options = new()
        {
            IsReadOnlyFunc = _ => true
        };
        DashboardContextStub context = new(options);
        BatchCommandDispatcher dispatcher = new((ctx, str) => { });
        dispatcher.Dispatch(context);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task Dispatch_Sets405_AndRunsNothing_ForNonPostRequests()
    {
        Mock<JobStorage> storage = new();
        DashboardRequestStub request = new("GET", "/jobs/failed/requeue");
        request.Form["jobs[]"] = ["1"];
        DashboardContextStub context = new(new DashboardOptions(), request, storage.Object);
        List<string> called = [];

        await new BatchCommandDispatcher((_, jobId) => called.Add(jobId)).Dispatch(context);

        Assert.Equal(405, context.Response.StatusCode);
        Assert.Empty(called);
        storage.Verify(x => x.GetConnection(), Times.Never);
    }

    [Fact]
    public async Task Dispatch_RunsTheCommandForEveryJob_AndAnswers204_ForPostRequests()
    {
        Mock<JobStorage> storage = new();
        storage.Setup(x => x.GetConnection()).Returns(new Mock<JobStorageConnection> { DefaultValue = DefaultValue.Mock }.Object);
        DashboardRequestStub request = new("POST", "/jobs/failed/requeue");
        request.Form["jobs[]"] = ["1", "2"];
        DashboardContextStub context = new(new DashboardOptions(), request, storage.Object);
        List<string> called = [];

        await new BatchCommandDispatcher((_, jobId) => called.Add(jobId)).Dispatch(context);

        Assert.Equal(204, context.Response.StatusCode);
        Assert.Equal(new[] { "1", "2" }, called);
    }

    [Fact, StaticLock]
    public async Task Dispatch_GoesOnAfterAJobWhoseTypeCanNotBeLoaded_AndAnswers422_NamingIt()
    {
        // "Trigger now" in a dashboard-only app: RecurringJobManager wraps the JobLoadException. It answered
        // 500, and the jobs triggered before it weren't audited.
        List<string> audit = [];
        Mock<IWriteOnlyTransaction> transaction = new();
        transaction.Setup(x => x.InsertToList(AuditLog.ListKey, It.IsAny<string>())).Callback<string, string>((_, value) => audit.Add(value));
        Mock<IStorageConnection> connection = new();
        connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
        Mock<JobStorage> storage = new();
        storage.Setup(x => x.GetConnection()).Returns(connection.Object);
        DashboardRequestStub request = new("POST", "/recurring/trigger");
        request.Form["jobs[]"] = ["a", "shop", "b"];
        DashboardContextStub context = new(new DashboardOptions(), request, storage.Object);
        List<string> called = [];

        await new BatchCommandDispatcher((_, id) =>
        {
            if (id == "shop")
            {
                throw new AggregateException(new JobLoadException("Could not load the job.", new InvalidOperationException("Could not resolve assembly 'Shop.Web'.")));
            }

            called.Add(id);
        }).Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
        Assert.Contains("shop (Could not resolve assembly 'Shop.Web'.)", context.Response.Text);
        Assert.Equal(new[] { "a", "b" }, called);
        string entry = Assert.Single(audit);
        Assert.Contains("\"s\":422", entry);
        Assert.Contains("2 of 3 done", entry);
    }

    [Fact, StaticLock]
    public async Task Dispatch_RecordsWhatWasDone_WhenAnotherErrorStopsIt()
    {
        List<string> audit = [];
        Mock<IWriteOnlyTransaction> transaction = new();
        transaction.Setup(x => x.InsertToList(AuditLog.ListKey, It.IsAny<string>())).Callback<string, string>((_, value) => audit.Add(value));
        Mock<IStorageConnection> connection = new();
        connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
        Mock<JobStorage> storage = new();
        storage.Setup(x => x.GetConnection()).Returns(connection.Object);
        DashboardRequestStub request = new("POST", "/recurring/trigger");
        request.Form["jobs[]"] = ["a", "b"];
        DashboardContextStub context = new(new DashboardOptions(), request, storage.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new BatchCommandDispatcher((_, id) =>
        {
            if (id == "b")
            {
                throw new InvalidOperationException("storage down");
            }
        }).Dispatch(context));

        string entry = Assert.Single(audit);
        Assert.Contains("\"s\":500", entry);
        Assert.Contains("1 of 2 done", entry);
    }
}
