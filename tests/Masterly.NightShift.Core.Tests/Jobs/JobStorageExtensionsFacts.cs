using System.Text.Json;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests;

public class JobStorageExtensionsFacts
{
    private const string JobId = "42";

    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IStorageConnection> _connection = new();
    private readonly Mock<IMonitoringApi> _monitor = new();
    private readonly Mock<IBackgroundJobClient> _client = new();
    private readonly List<KeyValuePair<string, FailedJobDto>> _failed = [];

    public JobStorageExtensionsFacts()
    {
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
        _monitor.Setup(x => x.FailedJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns<int, int>((from, count) => new JobList<FailedJobDto>(_failed.Skip(from).Take(count)));
        _monitor.Setup(x => x.FailedCount()).Returns(() => _failed.Count);

        // A requeued job leaves the failed list, as it does in a real storage
        _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), FailedState.StateName))
            .Returns<string, IState, string>((id, _, _) => _failed.RemoveAll(x => x.Key == id) > 0);
    }

    [Fact]
    public void RequeueFailedJobs_RequeuesEveryFailedJob_ExpectingTheFailedState()
    {
        AddFailed("1", "2", "3");

        BulkJobResult result = _storage.Object.RequeueFailedJobs(_client.Object);

        Assert.Equal(3, result.Processed);
        Assert.Equal(0, result.Remaining);
        Assert.True(result.Complete);
        Assert.Empty(_failed);
        _client.Verify(x => x.ChangeState("2", It.Is<EnqueuedState>(s => s.Reason == "Requeued with RequeueFailedJobs"), FailedState.StateName), Times.Once);
    }

    [Fact]
    public void RequeueFailedJobs_RequeuesOnlyTheJobsTheFilterSelects()
    {
        AddFailed("1", "2", "3", "4");
        _failed[1].Value.ExceptionType = typeof(TimeoutException).FullName;
        _failed[3].Value.ExceptionType = typeof(TimeoutException).FullName;

        BulkJobResult result = _storage.Object.RequeueFailedJobs(_client.Object, job => job.ExceptionType == typeof(TimeoutException).FullName);

        Assert.Equal(2, result.Processed);
        Assert.True(result.Complete);
        Assert.Equal(["1", "3"], _failed.Select(x => x.Key));
    }

    [Fact]
    public void RequeueFailedJobs_SkipsJobsThatAreNoLongerFailed_OrWhoseDataIsGone()
    {
        AddFailed("1", "2");
        _failed[0].Value.InFailedState = false;
        _failed.Add(new KeyValuePair<string, FailedJobDto>("3", null));

        BulkJobResult result = _storage.Object.RequeueFailedJobs(_client.Object);

        Assert.Equal(1, result.Processed);
        Assert.True(result.Complete);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void RequeueFailedJobs_ReadsEveryPage_WithoutAWorkLimit()
    {
        AddFailed([.. Enumerable.Range(0, 1200).Select(i => i.ToString())]);

        BulkJobResult result = _storage.Object.RequeueFailedJobs(_client.Object);

        Assert.Equal(1200, result.Processed);
        Assert.True(result.Complete);
    }

    [Fact]
    public void RequeueFailedJobs_StopsAtItsTimeBudget_AndCountsTheJobsLeft()
    {
        AddFailed("1", "2", "3");

        BulkJobResult result = _storage.Object.RequeueFailedJobs(_client.Object, timeBudget: TimeSpan.Zero);

        Assert.Equal(1, result.Processed);
        Assert.Equal(2, result.Remaining);
        Assert.False(result.Complete);
    }

    [Fact]
    public void RequeueFailedJobs_ThrowsOnInvalidArguments()
    {
        Assert.Throws<ArgumentNullException>("storage", () => JobStorageExtensions.RequeueFailedJobs(null));
        Assert.Throws<ArgumentNullException>("client", () => _storage.Object.RequeueFailedJobs((IBackgroundJobClient)null));
        Assert.Throws<ArgumentOutOfRangeException>("timeBudget", () => _storage.Object.RequeueFailedJobs(_client.Object, timeBudget: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void GetJobState_ReturnsTheCurrentState_WithTheJobsCreationTime()
    {
        DateTime createdAt = new(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
        _connection.Setup(x => x.GetJobData(JobId)).Returns(new JobData { State = FailedState.StateName, CreatedAt = createdAt });
        _connection.Setup(x => x.GetStateData(JobId)).Returns(new StateData
        {
            Name = FailedState.StateName,
            Reason = "An exception occurred",
            Data = new Dictionary<string, string> { { "ExceptionType", "System.TimeoutException" } }
        });

        JobStateInfo? state = _storage.Object.GetJobState(JobId);

        Assert.NotNull(state);
        Assert.Equal(FailedState.StateName, state.Name);
        Assert.Equal("An exception occurred", state.Reason);
        Assert.Equal(createdAt, state.JobCreatedAt);
        Assert.Equal("System.TimeoutException", state.Data["exceptiontype"]);
    }

    [Fact]
    public void GetJobState_ReturnsNull_WhenTheJobDoesNotExist_OrHasNoState()
    {
        _connection.Setup(x => x.GetJobData("no-state")).Returns(new JobData());

        Assert.Null(_storage.Object.GetJobState("missing"));
        Assert.Null(_storage.Object.GetJobState("no-state"));
    }

    [Fact]
    public void GetJobState_ThrowsOnNullArguments()
    {
        Assert.Throws<ArgumentNullException>("storage", () => JobStorageExtensions.GetJobState(null, JobId));
        Assert.Throws<ArgumentNullException>("jobId", () => _storage.Object.GetJobState(null));
    }

    [Theory]
    [InlineData("Succeeded", true)]
    [InlineData("Failed", true)]
    [InlineData("Deleted", true)]
    [InlineData("deleted", true)]
    [InlineData("Enqueued", false)]
    [InlineData("Scheduled", false)]
    [InlineData("Processing", false)]
    [InlineData("Awaiting", false)]
    [InlineData("MyCustomState", false)]
    public void IsCompleted_IsTrueOnlyForSucceededFailedAndDeleted(string state, bool completed)
    {
        JobStateInfo info = new(state, null, new Dictionary<string, string>(), DateTime.UtcNow);

        Assert.Equal(completed, info.IsCompleted);
    }

    [Fact]
    public async Task WaitForCompletionAsync_ReturnsAtOnce_WhenTheJobIsCompleted()
    {
        SetupStates(SucceededState.StateName);

        JobStateInfo? state = await _storage.Object.WaitForCompletionAsync(JobId, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(SucceededState.StateName, state?.Name);
        _connection.Verify(x => x.GetStateData(JobId), Times.Once);
    }

    [Fact]
    public async Task WaitForCompletionAsync_WaitsUntilTheJobCompletes()
    {
        SetupStates(EnqueuedState.StateName, ProcessingState.StateName, FailedState.StateName);

        JobStateInfo? state = await _storage.Object.WaitForCompletionAsync(JobId, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(FailedState.StateName, state?.Name);
        _connection.Verify(x => x.GetStateData(JobId), Times.Exactly(3));
    }

    [Fact]
    public async Task WaitForCompletionAsync_KeepsWaiting_WhileTheJobHasNoStateYet()
    {
        _connection.Setup(x => x.GetJobData(JobId)).Returns(new JobData { CreatedAt = DateTime.UtcNow });
        _connection.SetupSequence(x => x.GetStateData(JobId))
            .Returns((StateData)null)
            .Returns(new StateData { Name = DeletedState.StateName, Data = new Dictionary<string, string>() });

        JobStateInfo? state = await _storage.Object.WaitForCompletionAsync(JobId, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(DeletedState.StateName, state?.Name);
    }

    [Fact]
    public async Task WaitForCompletionAsync_ReturnsTheCurrentState_WhenTheTimeIsUp()
    {
        SetupStates(ProcessingState.StateName);

        JobStateInfo? state = await _storage.Object.WaitForCompletionAsync(JobId, TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        Assert.Equal(ProcessingState.StateName, state?.Name);
        Assert.False(state.IsCompleted);
    }

    [Fact]
    public async Task WaitForCompletionAsync_ReadsOnce_WithAZeroTimeout()
    {
        SetupStates(ProcessingState.StateName);

        JobStateInfo? state = await _storage.Object.WaitForCompletionAsync(JobId, TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessingState.StateName, state?.Name);
        _connection.Verify(x => x.GetStateData(JobId), Times.Once);
    }

    [Fact]
    public async Task WaitForCompletionAsync_ReturnsNull_WhenTheJobDoesNotExist()
    {
        Assert.Null(await _storage.Object.WaitForCompletionAsync("missing", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitForCompletionAsync_ThrowsOperationCanceled_WhenCanceled()
    {
        SetupStates(ProcessingState.StateName);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _storage.Object.WaitForCompletionAsync(JobId, Timeout.InfiniteTimeSpan, cancellation.Token));
    }

    [Fact]
    public async Task WaitForCompletionAsync_ThrowsOnInvalidArguments()
    {
        await Assert.ThrowsAsync<ArgumentNullException>("storage", () => JobStorageExtensions.WaitForCompletionAsync(null, JobId, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>("jobId", () => _storage.Object.WaitForCompletionAsync(null, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>("timeout", () => _storage.Object.WaitForCompletionAsync(JobId, TimeSpan.FromSeconds(-5), TestContext.Current.CancellationToken));
    }

    // The job's state is each of the given ones in turn, and stays in the last one
    private void SetupStates(params string[] states)
    {
        _connection.Setup(x => x.GetJobData(JobId)).Returns(new JobData { CreatedAt = DateTime.UtcNow });

        int index = 0;
        _connection.Setup(x => x.GetStateData(JobId)).Returns(() => new StateData
        {
            Name = states[Math.Min(index++, states.Length - 1)],
            Data = new Dictionary<string, string>(),
        });
    }

    [Fact]
    public void TryGetResult_ReadsTheResultOfASucceededJob()
    {
        SetupState(SucceededState.StateName, SerializationHelper.Serialize(new Answer { Value = 42, Text = "done" }, SerializationOption.User));

        bool found = _storage.Object.TryGetResult<Answer>(JobId, out Answer? result);

        Assert.True(found);
        Assert.Equal(42, result.Value);
        Assert.Equal("done", result.Text);
    }

    [Fact]
    public void TryGetResult_ReadsAPrimitiveResult()
    {
        SetupState(SucceededState.StateName, SerializationHelper.Serialize(42, SerializationOption.User));

        Assert.True(_storage.Object.TryGetResult<int>(JobId, out int result));
        Assert.Equal(42, result);
    }

    [Fact]
    public void TryGetResult_ReturnsFalse_WhenTheJobHasNotSucceeded()
    {
        SetupState(FailedState.StateName, "42");

        Assert.False(_storage.Object.TryGetResult<int>(JobId, out int result));
        Assert.Equal(0, result);
    }

    [Fact]
    public void TryGetResult_ReturnsFalse_WhenTheJobDoesNotExist()
    {
        Assert.False(_storage.Object.TryGetResult<string>("missing", out string? result));
        Assert.Null(result);
    }

    [Fact]
    public void TryGetResult_ReturnsFalse_WhenTheJobReturnedNothing()
    {
        SetupState(SucceededState.StateName, null);

        Assert.False(_storage.Object.TryGetResult<string>(JobId, out _));
    }

    [Fact]
    public void TryGetResult_ReturnsFalse_WhenTheResultCouldNotBeSerialized()
    {
        SetupState(SucceededState.StateName, "Can not serialize the return value");

        Assert.False(_storage.Object.TryGetResult<string>(JobId, out _));
    }

    [Fact]
    public void TryGetResult_Throws_WhenTheResultCanNotBeReadAsTheType()
    {
        SetupState(SucceededState.StateName, SerializationHelper.Serialize("text", SerializationOption.User));

        Assert.ThrowsAny<JsonException>(() => _storage.Object.TryGetResult<int>(JobId, out _));
    }

    [Fact]
    public void TryGetResult_ThrowsOnNullArguments()
    {
        Assert.Throws<ArgumentNullException>("storage", () => JobStorageExtensions.TryGetResult<int>(null, JobId, out _));
        Assert.Throws<ArgumentNullException>("jobId", () => _storage.Object.TryGetResult<int>(null, out _));
    }

    private void AddFailed(params string[] ids)
    {
        foreach (string id in ids)
        {
            _failed.Add(new KeyValuePair<string, FailedJobDto>(id, new FailedJobDto { InFailedState = true }));
        }
    }

    private void SetupState(string name, string result)
    {
        Dictionary<string, string> data = [];
        if (result != null) data["Result"] = result;

        _connection.Setup(x => x.GetStateData(JobId)).Returns(new StateData { Name = name, Data = data });
    }

    public sealed class Answer
    {
        public int Value { get; set; }
        public string Text { get; set; }
    }
}
