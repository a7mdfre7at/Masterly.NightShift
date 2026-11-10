using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Masterly.NightShift.Client;

namespace Masterly.NightShift.Core.Tests;

public class BackgroundJobClientBulkExtensionsFacts
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IStorageConnection> _connection = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly JobFilterCollection _filters = [];
    private readonly List<string> _calls = [];
    private readonly BackgroundJobClient _client;
    private int _nextId;

    public BackgroundJobClientBulkExtensionsFacts()
    {
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _connection
            .Setup(x => x.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>()))
            .Returns(() => (++_nextId).ToString());

        _client = new BackgroundJobClient(_storage.Object, new JobFilterProviderCollection(_filters));
    }

    [Fact]
    public void EnqueueMany_ThrowsAnException_WhenClientIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => ((IBackgroundJobClient)null).EnqueueMany([Job.FromExpression(() => Method(1))]));

        Assert.Equal("client", exception.ParamName);
    }

    [Fact]
    public void EnqueueMany_ThrowsAnException_WhenJobsIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => _client.EnqueueMany((IEnumerable<Job>)null));

        Assert.Equal("jobs", exception.ParamName);
    }

    [Fact]
    public void EnqueueMany_ReturnsAnEmptyList_WithoutTouchingTheStorage_WhenThereAreNoJobs()
    {
        IReadOnlyList<string> ids = _client.EnqueueMany((IEnumerable<Job>)[]);

        Assert.Empty(ids);
        _storage.Verify(x => x.GetConnection(), Times.Never);
    }

    [Fact]
    public void EnqueueMany_ReturnsTheIdOfEveryJob_InTheOrderGiven()
    {
        IReadOnlyList<string> ids = _client.EnqueueMany([Job.FromExpression(() => Method(1)), Job.FromExpression(() => Method(2))]);

        Assert.Equal(["1", "2"], ids);
    }

    [Fact]
    public void EnqueueMany_PutsEveryJobIntoTheEnqueuedState_InOneTransaction()
    {
        _client.EnqueueMany([Job.FromExpression(() => Method(1)), Job.FromExpression(() => Method(2))]);

        _transaction.Verify(x => x.SetJobState("1", It.Is<IState>(s => s.Name == EnqueuedState.StateName)));
        _transaction.Verify(x => x.SetJobState("2", It.Is<IState>(s => s.Name == EnqueuedState.StateName)));
        _transaction.Verify(x => x.Commit(), Times.Once);
    }

    [Fact]
    public void EnqueueMany_GivesEveryJobAStateOfItsOwn()
    {
        List<IState> states = [];
        _transaction.Setup(x => x.SetJobState(It.IsAny<string>(), It.IsAny<IState>())).Callback<string, IState>((_, state) => states.Add(state));

        _client.EnqueueMany([Job.FromExpression(() => Method(1)), Job.FromExpression(() => Method(2))]);

        Assert.Equal(2, states.Count);
        Assert.NotSame(states[0], states[1]);
    }

    [Fact]
    public void EnqueueMany_CallsOnCreatingForEveryJob_ThenCreatesThem_ThenCallsOnCreated()
    {
        _filters.Add(new RecordingFilter(_calls));
        _transaction.Setup(x => x.Commit()).Callback(() => _calls.Add("commit"));

        _client.EnqueueMany([Job.FromExpression(() => Method(1)), Job.FromExpression(() => Method(2))]);

        Assert.Equal(["creating 1", "creating 2", "commit", "created 1 as 1", "created 2 as 2"], _calls);
    }

    [Fact]
    public void EnqueueMany_CallsOnCreated_FromTheLastFilterToTheFirst()
    {
        _filters.Add(new RecordingFilter(_calls, "a"), 1);
        _filters.Add(new SecondRecordingFilter(_calls, "b"), 2);

        _client.EnqueueMany([Job.FromExpression(() => Method(1))]);

        Assert.Equal(["a creating 1", "b creating 1", "b created 1 as 1", "a created 1 as 1"], _calls);
    }

    [Fact]
    public void EnqueueMany_ReturnsNull_AndDoesNotCreateTheJob_WhenAFilterCancelsIt()
    {
        _filters.Add(new RecordingFilter(_calls) { CancelArgument = 2 });

        IReadOnlyList<string> ids = _client.EnqueueMany(
            [Job.FromExpression(() => Method(1)), Job.FromExpression(() => Method(2)), Job.FromExpression(() => Method(3))]);

        Assert.Equal(["1", null, "2"], ids);
        _connection.Verify(
            x => x.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>()),
            Times.Exactly(2));
    }

    [Fact]
    public void EnqueueMany_TellsTheFiltersBeforeTheCancelingOne_ThatTheJobWasCanceled()
    {
        _filters.Add(new RecordingFilter(_calls, "a"), 1);
        _filters.Add(new SecondRecordingFilter(_calls, "b") { CancelArgument = 1 }, 2);

        _client.EnqueueMany([Job.FromExpression(() => Method(1))]);

        Assert.Equal(["a creating 1", "b creating 1", "a canceled 1"], _calls);
    }

    [Fact]
    public void EnqueueMany_CreatesNothing_AndThrows_WhenOnCreatingThrows()
    {
        _filters.Add(new RecordingFilter(_calls) { ThrowOnCreatingArgument = 2 });

        BackgroundJobClientException exception = Assert.Throws<BackgroundJobClientException>(
            () => _client.EnqueueMany([Job.FromExpression(() => Method(1)), Job.FromExpression(() => Method(2))]));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        _connection.Verify(
            x => x.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>()),
            Times.Never);
        Assert.Equal(["creating 1", "creating 2", "failed 1"], _calls);
    }

    [Fact]
    public void EnqueueMany_TellsEveryFilter_AndThrows_WhenTheStorageFails()
    {
        _filters.Add(new RecordingFilter(_calls));
        _transaction.Setup(x => x.Commit()).Throws<TimeoutException>();

        BackgroundJobClientException exception = Assert.Throws<BackgroundJobClientException>(
            () => _client.EnqueueMany([Job.FromExpression(() => Method(1)), Job.FromExpression(() => Method(2))]));

        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(["creating 1", "creating 2", "failed 1", "failed 2"], _calls);
    }

    [Fact]
    public void EnqueueMany_ReturnsNulls_WhenAFilterHandlesTheStorageFailure()
    {
        _filters.Add(new RecordingFilter(_calls) { HandleExceptions = true });
        _transaction.Setup(x => x.Commit()).Throws<TimeoutException>();

        IReadOnlyList<string> ids = _client.EnqueueMany([Job.FromExpression(() => Method(1)), Job.FromExpression(() => Method(2))]);

        Assert.Equal([null, null], ids);
    }

    [Fact]
    public void EnqueueMany_PassesTheParametersFiltersSet_ToTheStorage()
    {
        _filters.Add(new RecordingFilter(_calls) { ParameterName = "Tenant" });
        List<IDictionary<string, string>> parameters = [];
        _connection
            .Setup(x => x.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>()))
            .Callback<Job, IDictionary<string, string>, DateTime, TimeSpan>((_, p, _, _) => parameters.Add(p))
            .Returns(() => (++_nextId).ToString());

        _client.EnqueueMany([Job.FromExpression(() => Method(1)), Job.FromExpression(() => Method(2))]);

        Assert.Equal("1", parameters[0]["Tenant"]);
        Assert.Equal("2", parameters[1]["Tenant"]);
    }

    [Fact, StaticLock]
    public void EnqueueMany_CreatesTheJobsInGroupsOfBulkSize()
    {
        int previous = BackgroundJobClient.BulkSize;
        BackgroundJobClient.BulkSize = 2;

        try
        {
            IReadOnlyList<string> ids = _client.EnqueueMany(
                [Job.FromExpression(() => Method(1)), Job.FromExpression(() => Method(2)), Job.FromExpression(() => Method(3))]);

            Assert.Equal(["1", "2", "3"], ids);
            _transaction.Verify(x => x.Commit(), Times.Exactly(2));
        }
        finally
        {
            BackgroundJobClient.BulkSize = previous;
        }
    }

    [Fact]
    public void BulkSize_ThrowsAnException_WhenValueIsNotPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BackgroundJobClient.BulkSize = 0);
    }

    [Fact]
    public void EnqueueMany_UsesTheStoragesBulkCreation_WhenItHasOne()
    {
        Mock<JobStorageConnection> connection = new() { CallBase = true };
        connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        connection
            .Setup(x => x.CreateExpiredJobs(It.IsAny<IReadOnlyList<Job>>(), It.IsAny<IReadOnlyList<IDictionary<string, string>>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>()))
            .Returns(["10", "11"]);
        _storage.Setup(x => x.GetConnection()).Returns(connection.Object);

        IReadOnlyList<string> ids = _client.EnqueueMany([Job.FromExpression(() => Method(1)), Job.FromExpression(() => Method(2))]);

        Assert.Equal(["10", "11"], ids);
        connection.Verify(
            x => x.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>()),
            Times.Never);
    }

    [Fact]
    public void CreateExpiredJobs_CreatesTheJobsOneByOne_ByDefault()
    {
        Mock<JobStorageConnection> connection = new() { CallBase = true };
        connection
            .Setup(x => x.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>()))
            .Returns(() => (++_nextId).ToString());
        Job[] jobs = [Job.FromExpression(() => Method(1)), Job.FromExpression(() => Method(2))];
        Dictionary<string, string>[] parameters = [[], []];

        IReadOnlyList<string> ids = connection.Object.CreateExpiredJobs(jobs, parameters, DateTime.UtcNow, TimeSpan.FromHours(1));

        Assert.Equal(["1", "2"], ids);
    }

    [Fact]
    public void EnqueueMany_CreatesTheJobsOneByOne_ForAnotherClientImplementation()
    {
        Mock<IBackgroundJobClient> client = new();
        client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("id");

        IReadOnlyList<string> ids = client.Object.EnqueueMany([Job.FromExpression(() => Method(1)), Job.FromExpression(() => Method(2))]);

        Assert.Equal(["id", "id"], ids);
        client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()), Times.Exactly(2));
    }

    [Fact]
    public void EnqueueMany_WithMethodCalls_CreatesAJobPerCall()
    {
        List<Job> jobs = CaptureJobs();
        Expression<Action<BulkService>>[] calls = [x => x.Run(1), x => x.Run(2)];

        _client.EnqueueMany(calls);

        Assert.Equal([1, 2], jobs.Select(static x => x.Args[0]));
    }

    [Fact]
    public void EnqueueMany_WithAsyncMethodCalls_CreatesAJobPerCall()
    {
        List<Job> jobs = CaptureJobs();
        Expression<Func<BulkService, Task>>[] calls = [x => x.RunAsync(1), x => x.RunAsync(2)];

        _client.EnqueueMany(calls);

        Assert.Equal([1, 2], jobs.Select(static x => x.Args[0]));
        Assert.All(jobs, static x => Assert.Equal(nameof(BulkService.RunAsync), x.Method.Name));
    }

    [Fact]
    public void EnqueueMany_WithItems_CreatesAJobPerItem_WithTheItemAsTheArgument()
    {
        List<Job> jobs = CaptureJobs();

        _client.EnqueueMany<BulkService, int>([7, 8, 9], static (x, id) => x.Run(id));

        Assert.Equal([7, 8, 9], jobs.Select(static x => x.Args[0]));
        Assert.All(jobs, static x => Assert.Equal(typeof(BulkService), x.Type));
    }

    [Fact]
    public void EnqueueMany_WithItems_AndAnAsyncCall_CreatesAJobPerItem()
    {
        List<Job> jobs = CaptureJobs();

        _client.EnqueueMany<BulkService, int>([7, 8], static (x, id) => x.RunAsync(id));

        Assert.Equal([7, 8], jobs.Select(static x => x.Args[0]));
    }

    [Fact]
    public void EnqueueMany_WithItems_KeepsTheOtherArgumentsOfTheCall()
    {
        List<Job> jobs = CaptureJobs();
        string prefix = "report";

        _client.EnqueueMany<BulkService, int>([7], (x, id) => x.Name(prefix, id));

        Assert.Equal(["report", 7], jobs[0].Args);
    }

    private List<Job> CaptureJobs()
    {
        List<Job> jobs = [];
        _connection
            .Setup(x => x.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>()))
            .Callback<Job, IDictionary<string, string>, DateTime, TimeSpan>((job, _, _, _) => jobs.Add(job))
            .Returns(() => (++_nextId).ToString());
        return jobs;
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Method(int argument)
    {
    }

    public class BulkService
    {
        public void Run(int id)
        {
        }

        public Task RunAsync(int id) => Task.CompletedTask;

        public void Name(string prefix, int id)
        {
        }
    }

    private class RecordingFilter(List<string> calls, string name = null) : IClientFilter
    {
        private readonly string _prefix = name == null ? "" : name + " ";

        public int? CancelArgument { get; init; }
        public int? ThrowOnCreatingArgument { get; init; }
        public bool HandleExceptions { get; init; }
        public string ParameterName { get; init; }

        public void OnCreating(CreatingContext context)
        {
            int argument = (int)context.Job.Args[0];
            calls.Add($"{_prefix}creating {argument}");

            if (ParameterName != null) context.SetJobParameter(ParameterName, argument);
            if (argument == CancelArgument) context.Canceled = true;
            if (argument == ThrowOnCreatingArgument) throw new InvalidOperationException("OnCreating failed");
        }

        public void OnCreated(CreatedContext context)
        {
            int argument = (int)context.Job.Args[0];

            if (context.Exception != null)
            {
                calls.Add($"{_prefix}failed {argument}");
                context.ExceptionHandled = HandleExceptions;
            }
            else if (context.Canceled)
            {
                calls.Add($"{_prefix}canceled {argument}");
            }
            else
            {
                calls.Add($"{_prefix}created {argument} as {context.BackgroundJob?.Id}");
            }
        }
    }

    // A second type: the provider collection keeps one filter per type unless it allows several
    private sealed class SecondRecordingFilter(List<string> calls, string name) : RecordingFilter(calls, name);
}
