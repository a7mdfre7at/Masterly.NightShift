using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public sealed class WorkerGateFacts : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IStorageConnection> _connection = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly Dictionary<string, string> _hash = [];
    private readonly TimeSpan _closedCheckInterval = WorkerGate.ClosedCheckInterval;

    public WorkerGateFacts()
    {
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(() => new Dictionary<string, string>(_hash));
        _transaction.Setup(x => x.SetRangeInHash(DrainMode.HashKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) =>
            {
                foreach (KeyValuePair<string, string> pair in pairs)
                {
                    _hash[pair.Key] = pair.Value;
                }
            });
        _transaction.Setup(x => x.RemoveHash(DrainMode.HashKey)).Callback(_hash.Clear);
        DrainMode.InvalidateCache();
    }

    public void Dispose()
    {
        WorkerGate.ClosedCheckInterval = _closedCheckInterval;
        DrainMode.InvalidateCache();
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void WaitToFetch_LetsTheWorkersOfAServerFetch_ThatIsNotPaused()
    {
        WorkerGate gate = new();

        Assert.True(gate.WaitToFetch(Context("web-1", Stopping)));
        Assert.True(gate.IsDecided("web-1"));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void WaitToFetch_KeepsTheWorkersOfAServerPausedByHandFromFetching_AndOnlyThoseOfThatServer()
    {
        WorkerGate gate = new();
        DrainMode.PauseServer(_connection.Object, "web-1", "maintenance");

        // The server is stopping, so the wait ends at once: a worker that didn't wait would have gone on
        Assert.ThrowsAny<OperationCanceledException>(() => gate.WaitToFetch(Context("web-1", Stopping)));
        Assert.True(gate.WaitToFetch(Context("web-2", Stopping)));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void WaitToFetch_LetsTheWorkersFetchAgain_OnceTheServerIsResumed()
    {
        WorkerGate gate = new();
        DrainMode.PauseServer(_connection.Object, "web-1");
        Assert.ThrowsAny<OperationCanceledException>(() => gate.WaitToFetch(Context("web-1", Stopping)));

        DrainMode.ResumeServer(_connection.Object, "web-1");

        Assert.True(gate.WaitToFetch(Context("web-1", Stopping)));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void WaitToFetch_ReturnsFalse_AfterItHasWaited_WhileTheServerStaysPaused()
    {
        WorkerGate.ClosedCheckInterval = TimeSpan.FromMilliseconds(20);
        WorkerGate gate = new();
        DrainMode.PauseServer(_connection.Object, "web-1");

        bool mayFetch = gate.WaitToFetch(Context("web-1", CancellationToken.None));

        Assert.False(mayFetch);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void WaitToFetch_StaysOpen_ForTheClusterWideSwitch_AndAPausedQueue()
    {
        // Nobody competes for the jobs then: the workers fetch, and DrainModeFilter holds what they get, as before
        WorkerGate gate = new();
        DrainMode.Enable(_connection.Object, "deploying");
        DrainMode.PauseQueue(_connection.Object, "default");

        Assert.True(gate.WaitToFetch(Context("web-1", Stopping)));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void WaitToFetch_AsksTheStorageOnce_HoweverManyJobsAreFetchedWhileTheAnswerIsCached()
    {
        WorkerGate gate = new();

        bool[] mayFetch = [.. Enumerable.Range(0, 10).Select(_ => gate.WaitToFetch(Context("web-1", Stopping)))];

        Assert.All(mayFetch, static x => Assert.True(x));
        _storage.Verify(x => x.GetConnection(), Times.Once);
        _connection.Verify(x => x.GetAllEntriesFromHash(DrainMode.HashKey), Times.Once);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void WaitToFetch_LetsTheWorkersFetch_WhenThePauseCanNotBeRead()
    {
        // The fetch that follows says what is wrong with the storage; a job of a paused server is still held
        _storage.Setup(x => x.GetConnection()).Throws(new InvalidOperationException("storage down"));
        WorkerGate gate = new();

        Assert.True(gate.WaitToFetch(Context("web-1", Stopping)));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void WaitToFetch_WithADeployment_IsClosedForAServerIdNoCheckHasDecidedFor_AndForAFencedOne()
    {
        WorkerGate gate = new("v1");

        Assert.False(gate.IsDecided("web-1"));
        Assert.ThrowsAny<OperationCanceledException>(() => gate.WaitToFetch(Context("web-1", Stopping)));

        gate.Decide("web-1", fenced: false);
        Assert.True(gate.WaitToFetch(Context("web-1", Stopping)));
        Assert.ThrowsAny<OperationCanceledException>(() => gate.WaitToFetch(Context("web-1-restarted", Stopping)));

        gate.Decide("web-1-restarted", fenced: true);
        Assert.ThrowsAny<OperationCanceledException>(() => gate.WaitToFetch(Context("web-1-restarted", Stopping)));
        Assert.ThrowsAny<OperationCanceledException>(() => gate.WaitToFetch(Context("web-1", Stopping)));

        // No storage is asked while the deployment check keeps the gate closed
        _connection.Verify(x => x.GetAllEntriesFromHash(DrainMode.HashKey), Times.Once);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public async Task WaitToFetch_EndsItsWait_AsSoonAsTheDeploymentCheckDecides()
    {
        // Longer than the test waits, so only the decision can end the wait in time
        WorkerGate.ClosedCheckInterval = TimeSpan.FromMinutes(10);
        TimeSpan holdInterval = DrainMode.HoldInterval;
        DrainMode.HoldInterval = TimeSpan.FromMinutes(10);

        try
        {
            WorkerGate gate = new("v1");
            Task<bool> waiting = Task.Run(() => gate.WaitToFetch(Context("web-1", CancellationToken.None)), TestContext.Current.CancellationToken);

            gate.Decide("web-1", fenced: false);
            await waiting.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);

            Assert.True(gate.WaitToFetch(Context("web-1", CancellationToken.None)));
        }
        finally
        {
            DrainMode.HoldInterval = holdInterval;
        }
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void ClosedCheckInterval_IsNeverLongerThanTheHoldInterval()
    {
        TimeSpan holdInterval = DrainMode.HoldInterval;

        try
        {
            DrainMode.HoldInterval = TimeSpan.FromSeconds(2);
            Assert.Equal(TimeSpan.FromSeconds(2), WorkerGate.ClosedCheckInterval);

            DrainMode.HoldInterval = TimeSpan.FromMinutes(1);
            Assert.Equal(TimeSpan.FromSeconds(5), WorkerGate.ClosedCheckInterval);
        }
        finally
        {
            DrainMode.HoldInterval = holdInterval;
        }
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void GatedWorker_RunsItsWorker_OnlyWhileTheGateIsOpen()
    {
        Mock<IBackgroundProcess> worker = new();
        GatedWorker gated = new(worker.Object, new WorkerGate());
        DrainMode.PauseServer(_connection.Object, "web-1");

        Assert.ThrowsAny<OperationCanceledException>(() => gated.Execute(Context("web-1", Stopping)));
        worker.Verify(x => x.Execute(It.IsAny<BackgroundProcessContext>()), Times.Never);

        DrainMode.ResumeServer(_connection.Object, "web-1");
        BackgroundProcessContext context = Context("web-1", Stopping);
        gated.Execute(context);

        worker.Verify(x => x.Execute(context), Times.Once);
        Assert.Throws<ArgumentNullException>(() => gated.Execute(null));
    }

    // The stopping token of a server that is stopping: a wait for it ends at once
    private static CancellationToken Stopping => new(canceled: true);

    private BackgroundProcessContext Context(string serverId, CancellationToken stoppingToken)
    {
        return new BackgroundProcessContext(
            serverId,
            _storage.Object,
            new Dictionary<string, object>(),
            Guid.NewGuid(),
            stoppingToken,
            CancellationToken.None,
            CancellationToken.None);
    }
}
