using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public class RateLimitAttributeFacts : IDisposable
{
    private const string Key = "ratelimit:resource:60";

    private readonly PerformContextMock _context = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly Mock<IBackgroundJobStateChanger> _stateChanger = new();
    private readonly HashSet<string> _members = [];
    private readonly DateTime _now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly long _timestamp;

    public RateLimitAttributeFacts()
    {
        _timestamp = JobHelper.ToTimestamp(_now);

        _context.Connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
        _context.Connection.Setup(x => x.GetAllItemsFromSet(Key)).Returns(_members);
        _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
        _context.BackgroundJob.Job = Job.FromExpression(() => Sample());

        ConcurrencyControl.StateChanger = _stateChanger.Object;
        ConcurrencyControl.Clock = () => _now;
    }

    public void Dispose()
    {
        ConcurrencyControl.StateChanger = new BackgroundJobStateChanger();
        ConcurrencyControl.Clock = () => DateTime.UtcNow;
    }

    [Fact]
    public void Ctor_ValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitAttribute("x", 0, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitAttribute("x", 10, 0));
    }

    [Fact]
    public void WaitTimeoutSeconds_RejectsNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitAttribute("x", 10, 60) { WaitTimeoutSeconds = -1 });
        Assert.Equal(0, new RateLimitAttribute("x", 10, 60) { WaitTimeoutSeconds = 0 }.WaitTimeoutSeconds);
    }

    [Fact, StaticLock]
    public void OnPerforming_WaitsNoLongerThanWaitTimeoutSeconds_WhenTheNextSlotIsFurtherAway()
    {
        // The only start leaves the window in an hour, but the job may hold the worker for a second at most
        RateLimitAttribute attribute = new("resource", 1, 3600) { Action = ConcurrencyAction.Wait, WaitTimeoutSeconds = 1 };
        _context.Connection.Setup(x => x.GetAllItemsFromSet("ratelimit:resource:3600")).Returns(_members);
        _members.Add($"{_timestamp}:a");
        Stopwatch elapsed = Stopwatch.StartNew();
        ConcurrencyControl.Clock = () => _now + elapsed.Elapsed;

        using (CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(10)))
        {
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(shutdown.Token);

            Assert.Throws<ConcurrencyLimitException>(() => attribute.OnPerforming(_context.GetPerformingContext()));
        }

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Waited {elapsed.Elapsed}");
    }

    [Fact, StaticLock]
    public void OnPerforming_ExpiresTheSet_AWindowAfterTheLastStart_WhenTheStorageSupportsIt()
    {
        Mock<JobStorageTransaction> storageTransaction = new();
        _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
        _context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        RateLimitAttribute attribute = new("resource", 2, 60);

        attribute.OnPerforming(_context.GetPerformingContext());

        storageTransaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        storageTransaction.Verify(x => x.ExpireSet(Key, TimeSpan.FromSeconds(60) + TimeSpan.FromMinutes(1)));
    }

    [Fact, StaticLock]
    public void OnPerforming_DoesNotExpireTheSet_WhenTheStorageDoesNotSupportIt()
    {
        Mock<JobStorageTransaction> storageTransaction = new();
        _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
        RateLimitAttribute attribute = new("resource", 2, 60);

        attribute.OnPerforming(_context.GetPerformingContext());

        storageTransaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        storageTransaction.Verify(x => x.ExpireSet(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerformed_GivesTheStartBack_WhenALaterFilterCanceledTheRun()
    {
        RateLimitAttribute attribute = new("resource", 2, 60);
        attribute.OnPerforming(_context.GetPerformingContext());

        attribute.OnPerformed(_context.GetPerformedContext(canceled: true));

        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"));
    }

    [Fact, StaticLock]
    public void OnPerformed_KeepsTheStart_WhenTheJobMethodWasCalled_AndThrew()
    {
        RateLimitAttribute attribute = new("resource", 2, 60);
        attribute.OnPerforming(_context.GetPerformingContext());
        _context.Object.BackgroundJob.MethodInvokedTimestamp = 1;

        attribute.OnPerformed(_context.GetPerformedContext(exception: new InvalidOperationException()));

        _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerformed_KeepsTheStart_WhenTheJobSucceeded()
    {
        RateLimitAttribute attribute = new("resource", 2, 60);
        attribute.OnPerforming(_context.GetPerformingContext());

        attribute.OnPerformed(_context.GetPerformedContext());

        _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerformed_GivesTheStartBack_WhenALaterFilterThrew_BeforeTheJobMethodWasCalled()
    {
        // DisableConcurrentExecution's lock timed out, for example: the job never started
        RateLimitAttribute attribute = new("resource", 2, 60);
        attribute.OnPerforming(_context.GetPerformingContext());

        attribute.OnPerformed(_context.GetPerformedContext(exception: new DistributedLockTimeoutException("lock")));

        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"));
    }

    [Fact, StaticLock]
    public void Perform_KeepsTheStart_WhenALaterFilterThrows_AfterTheJobMethodRan()
    {
        BackgroundJobPerformer performer = CreatePerformer(new ThrowingFilter(onPerforming: false));

        Assert.Throws<JobPerformanceException>(() => performer.Perform(_context.Object));

        _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, StaticLock]
    public void Perform_GivesTheStartBack_WhenALaterFilterThrows_BeforeTheJobMethod()
    {
        BackgroundJobPerformer performer = CreatePerformer(new ThrowingFilter(onPerforming: true));

        Assert.Throws<JobPerformanceException>(() => performer.Perform(_context.Object));

        _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"));
    }

    [Fact, StaticLock]
    public void OnPerforming_CountsLimitsWithDifferentWindows_InSeparateSets()
    {
        // A start that an hour-long limit still needs is stale for a one-second limit on the same resource
        HashSet<string> hourly = [$"{_timestamp - 100}:a"];
        _context.Connection.Setup(x => x.GetAllItemsFromSet("ratelimit:api:3600")).Returns(hourly);
        _context.Connection.Setup(x => x.GetAllItemsFromSet("ratelimit:api:1")).Returns([]);
        PerformingContext hourlyPerforming = _context.GetPerformingContext();

        new RateLimitAttribute("api", 10, 1).OnPerforming(_context.GetPerformingContext());
        new RateLimitAttribute("api", 1, 3600).OnPerforming(hourlyPerforming);

        _transaction.Verify(x => x.AddToSet("ratelimit:api:1", $"{_timestamp}:JobId", _timestamp));
        _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.True(hourlyPerforming.Canceled);
    }

    [Fact, StaticLock]
    public void OnPerforming_CountsByScore_WithoutReadingTheWholeSet_WhenTheStorageSupportsIt()
    {
        Mock<JobStorageConnection> connection = UseScoredSetConnection();
        string expired = $"{_timestamp - 61}:old";
        connection.Setup(x => x.GetFirstByLowestScoreFromSet(Key, 0, _timestamp - 61, 100)).Returns([expired]);
        connection.Setup(x => x.GetSetCount(Key)).Returns(2);
        connection.Setup(x => x.GetFirstByLowestScoreFromSet(Key, _timestamp - 60, It.IsAny<double>())).Returns($"{_timestamp - 10}:a");
        RateLimitAttribute attribute = new("resource", 2, 60);
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.False(performing.Canceled);
        connection.Verify(x => x.GetAllItemsFromSet(It.IsAny<string>()), Times.Never);
        _transaction.Verify(x => x.RemoveFromSet(Key, expired));
        _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
    }

    [Fact, StaticLock]
    public void OnPerforming_ReschedulesFromTheOldestStartByScore_WhenTheStorageSupportsIt()
    {
        Mock<JobStorageConnection> connection = UseScoredSetConnection();
        connection.Setup(x => x.GetFirstByLowestScoreFromSet(Key, 0, _timestamp - 61, 100)).Returns([]);
        connection.Setup(x => x.GetSetCount(Key)).Returns(2);
        connection.Setup(x => x.GetFirstByLowestScoreFromSet(Key, _timestamp - 60, It.IsAny<double>())).Returns($"{_timestamp - 50}:a");
        RateLimitAttribute attribute = new("resource", 2, 60);
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.True(performing.Canceled);
        connection.Verify(x => x.GetAllItemsFromSet(It.IsAny<string>()), Times.Never);
        _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
            c.NewState is ScheduledState
            && ((ScheduledState)c.NewState).EnqueueAt >= _now.AddSeconds(11)
            && ((ScheduledState)c.NewState).EnqueueAt <= _now.AddSeconds(12))));
    }

    [Fact, StaticLock]
    public void OnPerforming_ReadsTheWholeSet_WhenTooManyStartsLeftTheWindow()
    {
        Mock<JobStorageConnection> connection = UseScoredSetConnection();
        List<string> expired = [];
        for (int i = 0; i < 100; i++)
        {
            expired.Add($"{_timestamp - 100 - i}:old{i}");
        }

        connection.Setup(x => x.GetFirstByLowestScoreFromSet(Key, 0, _timestamp - 61, 100)).Returns(expired);
        connection.Setup(x => x.GetAllItemsFromSet(Key)).Returns([.. expired, $"{_timestamp - 300}:older"]);
        RateLimitAttribute attribute = new("resource", 1, 60);

        attribute.OnPerforming(_context.GetPerformingContext());

        connection.Verify(x => x.GetSetCount(It.IsAny<string>()), Times.Never);
        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp - 300}:older"));
        _transaction.Verify(x => x.RemoveFromSet(Key, It.IsAny<string>()), Times.Exactly(101));
        _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
    }

    [Fact, StaticLock]
    public void OnPerforming_ShortensTheKey_OfALongResource()
    {
        string tenant = new('t', 95);
        _context.BackgroundJob.Job = Job.FromExpression(() => ForTenant(tenant));
        _context.Connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns([]);
        RateLimitAttribute attribute = new("tenant-{0}", 1, 60);

        attribute.OnPerforming(_context.GetPerformingContext());

        _transaction.Verify(x => x.AddToSet(
            It.Is<string>(key => key.StartsWith("ratelimit:tenant-ttt", StringComparison.Ordinal) && key.Contains("~") && key.Length <= 100),
            $"{_timestamp}:JobId",
            _timestamp));
    }

    [Fact, StaticLock]
    public void OnPerforming_RecordsTheStart_WhenUnderTheLimit()
    {
        RateLimitAttribute attribute = new("resource", 2, 60);
        _members.Add($"{_timestamp - 10}:a");
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.False(performing.Canceled);
        _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        _transaction.Verify(x => x.Commit());
    }

    [Fact, StaticLock]
    public void OnPerforming_Reschedules_ToWhenTheOldestStartLeavesTheWindow()
    {
        RateLimitAttribute attribute = new("resource", 2, 60);
        _members.Add($"{_timestamp - 50}:a");
        _members.Add($"{_timestamp - 10}:b");
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.True(performing.Canceled);
        _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
            c.NewState is ScheduledState
            && ((ScheduledState)c.NewState).EnqueueAt >= _now.AddSeconds(11)
            && ((ScheduledState)c.NewState).EnqueueAt <= _now.AddSeconds(12))));
    }

    [Fact, StaticLock]
    public void OnPerforming_TrimsStartsOutsideTheWindow()
    {
        RateLimitAttribute attribute = new("resource", 1, 60);
        string expired = $"{_timestamp - 61}:old";
        _members.Add(expired);
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.False(performing.Canceled);
        _transaction.Verify(x => x.RemoveFromSet(Key, expired));
        _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
    }

    [Fact, StaticLock]
    public void OnPerforming_CancelsTheJob_WhenActionIsSkip()
    {
        RateLimitAttribute attribute = new("resource", 1, 60) { Action = ConcurrencyAction.Skip };
        _members.Add($"{_timestamp}:a");
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.True(performing.Canceled);
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_ThrowsAnException_WhenActionIsWait_AndTheTimeoutExpires()
    {
        RateLimitAttribute attribute = new("resource", 1, 60) { Action = ConcurrencyAction.Wait, WaitTimeoutSeconds = 0 };
        _members.Add($"{_timestamp}:a");

        Assert.Throws<ConcurrencyLimitException>(() => attribute.OnPerforming(_context.GetPerformingContext()));
    }

    [Fact, StaticLock]
    public void OnPerforming_WaitsUntilTheOldestStartLeavesTheWindow_ThenRecordsTheStart_WhenActionIsWait()
    {
        RateLimitAttribute attribute = new("resource", 1, 60) { Action = ConcurrencyAction.Wait };
        HashSet<string> full = [$"{_timestamp - 60}:a"];
        int reads = 0;
        _context.Connection.Setup(x => x.GetAllItemsFromSet(Key)).Returns(() => reads++ == 0 ? full : []);
        // One millisecond before the oldest start leaves the window, so the wait is that millisecond
        ConcurrencyControl.Clock = () => _now.AddMilliseconds(999);
        PerformingContext performing = _context.GetPerformingContext();

        attribute.OnPerforming(performing);

        Assert.False(performing.Canceled);
        Assert.Equal(2, reads);
        _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerforming_ThrowsOperationCanceled_WhenShutdownIsRequestedWhileWaiting()
    {
        RateLimitAttribute attribute = new("resource", 1, 60) { Action = ConcurrencyAction.Wait };
        _members.Add($"{_timestamp}:a");
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(true));

        Assert.Throws<OperationCanceledException>(() => attribute.OnPerforming(_context.GetPerformingContext()));

        _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerformed_DoesNotTouchTheStorage()
    {
        RateLimitAttribute attribute = new("resource", 1, 60);

        attribute.OnPerformed(_context.GetPerformedContext());

        _context.Connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
        _context.Connection.Verify(x => x.GetAllItemsFromSet(It.IsAny<string>()), Times.Never);
    }

    [Fact, StaticLock]
    public void OnPerformed_DoesNotThrow_WhenTheStartCannotBeGivenBack()
    {
        RateLimitAttribute attribute = new("resource", 2, 60);
        attribute.OnPerforming(_context.GetPerformingContext());
        _transaction.Setup(x => x.Commit()).Throws(new InvalidOperationException("storage is down"));

        attribute.OnPerformed(_context.GetPerformedContext(canceled: true));

        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"));
    }

    [Fact]
    public void AJob_MayCarrySeveralLimits()
    {
        Assert.True(new RateLimitAttribute("api", 10, 1).AllowMultiple);

        RateLimitAttribute[] filters = [.. JobFilterProviders.Providers.GetFilters(Job.FromExpression(() => Tiered()))
            .Select(static x => x.Instance)
            .OfType<RateLimitAttribute>()];

        Assert.Equal([1, 3600], filters.Select(static x => x.PerSeconds).OrderBy(static x => x));
    }

    [Fact]
    public void AGlobalLimit_AndAMethodLimit_BothApply()
    {
        JobFilterCollection global = new()
        {
            new RateLimitAttribute("tenant", 5, 60)
        };
        JobFilterProviderCollection providers = new(global, new JobFilterAttributeFilterProvider());

        RateLimitAttribute[] filters = [.. providers.GetFilters(Job.FromExpression(() => Tiered()))
            .Select(static x => x.Instance)
            .OfType<RateLimitAttribute>()];

        Assert.Equal(3, filters.Length);
        Assert.Contains(filters, static x => x.Resource == "tenant");
    }

    [Fact, StaticLock]
    public void Perform_RunsTheJob_WhenEveryLimitAllowsIt()
    {
        UseTieredSets([], []);
        BackgroundJobPerformer performer = CreateTieredPerformer(ConcurrencyAction.Reschedule);

        performer.Perform(_context.Object);

        _transaction.Verify(x => x.AddToSet("ratelimit:api:1", $"{_timestamp}:JobId", _timestamp));
        _transaction.Verify(x => x.AddToSet("ratelimit:api:3600", $"{_timestamp}:JobId", _timestamp));
        _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact, StaticLock]
    public void Perform_GivesEveryLimitsStartBack_WhenALaterFilterCancelsTheRun()
    {
        UseTieredSets([], []);
        JobFilterCollection filters = new()
        {
            { new RateLimitAttribute("api", 10, 1), 0 },
            { new RateLimitAttribute("api", 1, 3600), 1 },
            { new CancelingFilter(), 2 }
        };
        BackgroundJobPerformer performer = new(filters, new JobActivator());

        performer.Perform(_context.Object);

        _transaction.Verify(x => x.RemoveFromSet("ratelimit:api:1", $"{_timestamp}:JobId"));
        _transaction.Verify(x => x.RemoveFromSet("ratelimit:api:3600", $"{_timestamp}:JobId"));
    }

    [Fact, StaticLock]
    public void Perform_GivesTheOtherLimitsStartBack_WhenOneLimitReschedules()
    {
        UseTieredSets([], [$"{_timestamp - 100}:a"]);
        BackgroundJobPerformer performer = CreateTieredPerformer(ConcurrencyAction.Reschedule);

        performer.Perform(_context.Object);

        _transaction.Verify(x => x.AddToSet("ratelimit:api:1", $"{_timestamp}:JobId", _timestamp));
        _transaction.Verify(x => x.RemoveFromSet("ratelimit:api:1", $"{_timestamp}:JobId"));
        _transaction.Verify(x => x.AddToSet("ratelimit:api:3600", It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.NewState is ScheduledState)));
    }

    [Fact, StaticLock]
    public void Perform_ALaterLimitWithWait_Reschedules_AndGivesTheOtherLimitsStartBack_InsteadOfWaiting()
    {
        // Waiting would let the per-second start age: the job could start after it left its one-second window
        UseTieredSets([], [$"{_timestamp - 100}:a"]);
        BackgroundJobPerformer performer = CreateTieredPerformer(ConcurrencyAction.Wait, waitTimeoutSeconds: 5);
        Stopwatch elapsed = Stopwatch.StartNew();
        ConcurrencyControl.Clock = () => _now + elapsed.Elapsed;

        performer.Perform(_context.Object);

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(4), $"Waited {elapsed.Elapsed}");
        _transaction.Verify(x => x.AddToSet("ratelimit:api:1", $"{_timestamp}:JobId", _timestamp));
        _transaction.Verify(x => x.RemoveFromSet("ratelimit:api:1", $"{_timestamp}:JobId"));
        _transaction.Verify(x => x.AddToSet("ratelimit:api:3600", It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
            c.NewState is ScheduledState
            && ((ScheduledState)c.NewState).EnqueueAt >= _now.AddSeconds(3501)
            && ((ScheduledState)c.NewState).EnqueueAt <= _now.AddSeconds(3502))));
    }

    [Fact, StaticLock]
    public void Perform_TheFirstLimitWithWait_StillFails_WhenItsWaitTimesOut()
    {
        UseTieredSets([$"{_timestamp}:a"], []);
        JobFilterCollection filters = new()
        {
            { new RateLimitAttribute("api", 1, 1) { Action = ConcurrencyAction.Wait, WaitTimeoutSeconds = 0 }, 0 },
            { new RateLimitAttribute("api", 1, 3600) { Action = ConcurrencyAction.Wait, WaitTimeoutSeconds = 0 }, 1 }
        };
        BackgroundJobPerformer performer = new(filters, new JobActivator());

        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => performer.Perform(_context.Object));

        Assert.IsType<ConcurrencyLimitException>(exception.InnerException);
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact, StaticLock]
    public void TwoLimits_WithTheSameResourceAndWindow_KeepTheRunsOwnStart_WhenAnotherFilterHeldTheRunPastTheWindow()
    {
        // The first limit records the run's start; another filter holds the run for 61 seconds, while another
        // job starts. The run's own start is then older than the window, but it is still the run's start: the
        // second limit (1 per minute) must count it and refuse, not remove it and let a second start in
        JobFilterCollection filters = new()
        {
            { new RateLimitAttribute("resource", 1, 60), 0 },
            { new ClockAdvancingFilter(TimeSpan.FromSeconds(61), () => _members.Add($"{_timestamp + 31}:b")), 1 },
            { new RateLimitAttribute("resource", 1, 60), 2 }
        };
        BackgroundJobPerformer performer = new(filters, new JobActivator());
        _transaction.Setup(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp)).Callback(() => _members.Add($"{_timestamp}:JobId"));

        performer.Perform(_context.Object);

        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.NewState is ScheduledState)));
        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"), Times.Once);
    }

    [Fact, StaticLock]
    public void TwoLimits_WithTheSameResourceAndWindow_CountTheRunsStartOnce()
    {
        // One start in the window, and a limit of two: the run's own start is the second, for both limits
        _members.Add($"{_timestamp - 10}:a");
        JobFilterCollection filters = new()
        {
            { new RateLimitAttribute("resource", 2, 60), 0 },
            { new RateLimitAttribute("resource", 2, 60), 1 }
        };
        BackgroundJobPerformer performer = new(filters, new JobActivator());
        _transaction.Setup(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp)).Callback(() => _members.Add($"{_timestamp}:JobId"));

        performer.Perform(_context.Object);

        _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp), Times.Once);
        _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
    }

    [Fact, StaticLock]
    public void TwoLimits_WithTheSameResourceAndWindow_TheStricterOneStillRefuses()
    {
        _members.Add($"{_timestamp - 10}:a");
        JobFilterCollection filters = new()
        {
            { new RateLimitAttribute("resource", 2, 60), 0 },
            { new RateLimitAttribute("resource", 1, 60), 1 }
        };
        BackgroundJobPerformer performer = new(filters, new JobActivator());
        _transaction.Setup(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp)).Callback(() => _members.Add($"{_timestamp}:JobId"));

        performer.Perform(_context.Object);

        _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"));
        _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.NewState is ScheduledState)));
    }

    private void UseTieredSets(HashSet<string> perSecond, HashSet<string> perHour)
    {
        _context.Connection.Setup(x => x.GetAllItemsFromSet("ratelimit:api:1")).Returns(perSecond);
        _context.Connection.Setup(x => x.GetAllItemsFromSet("ratelimit:api:3600")).Returns(perHour);
    }

    // 10 starts per second and 1 per hour on one resource, the per-second limit first
    private static BackgroundJobPerformer CreateTieredPerformer(ConcurrencyAction action, int waitTimeoutSeconds = 0)
    {
        JobFilterCollection filters = new()
        {
            { new RateLimitAttribute("api", 10, 1) { Action = action, WaitTimeoutSeconds = waitTimeoutSeconds }, 0 },
            { new RateLimitAttribute("api", 1, 3600) { Action = action, WaitTimeoutSeconds = waitTimeoutSeconds }, 1 }
        };

        return new BackgroundJobPerformer(filters, new JobActivator());
    }

    [RateLimit("api", 10, 1)]
    [RateLimit("api", 1000, 3600)]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void Tiered()
    {
    }

    // A storage that reads sets by score and counts them (JobStorageFeatures.ExtendedApi and batched reads)
    private Mock<JobStorageConnection> UseScoredSetConnection()
    {
        Mock<JobStorageConnection> connection = new();
        connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
        connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _context.Connection = connection.As<IStorageConnection>();
        _context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        _context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest)).Returns(true);
        return connection;
    }

    // The real performer: the rate limit first, then a filter that throws before or after the job method
    private static BackgroundJobPerformer CreatePerformer(IServerFilter laterFilter)
    {
        JobFilterCollection filters = new()
        {
            { new RateLimitAttribute("resource", 2, 60), 0 },
            { laterFilter, 1 }
        };

        return new BackgroundJobPerformer(filters, new JobActivator());
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void Sample()
    {
    }

    private sealed class CancelingFilter : IServerFilter
    {
        public void OnPerforming(PerformingContext context) => context.Canceled = true;

        public void OnPerformed(PerformedContext context)
        {
        }
    }

    // Holds the run: moves the clock on, as a filter that waited would, and runs what happens meanwhile
    private sealed class ClockAdvancingFilter(TimeSpan by, Action meanwhile) : IServerFilter
    {
        private readonly TimeSpan _by = by;
        private readonly Action _meanwhile = meanwhile;

        public void OnPerforming(PerformingContext context)
        {
            DateTime later = ConcurrencyControl.Clock() + _by;
            ConcurrencyControl.Clock = () => later;
            _meanwhile();
        }

        public void OnPerformed(PerformedContext context)
        {
        }
    }

    private sealed class ThrowingFilter(bool onPerforming) : IServerFilter
    {
        private readonly bool _onPerforming = onPerforming;

        public void OnPerforming(PerformingContext context)
        {
            if (_onPerforming)
            {
                throw new DistributedLockTimeoutException("lock");
            }
        }

        public void OnPerformed(PerformedContext context)
        {
            if (!_onPerforming)
            {
                throw new InvalidOperationException("after the method");
            }
        }
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void ForTenant(string tenant)
    {
    }
}
