using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class RateLimitAttributeFacts : IDisposable
    {
        private const string Key = "ratelimit:resource:60";

        private readonly PerformContextMock _context = new PerformContextMock();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Mock<IBackgroundJobStateChanger> _stateChanger = new Mock<IBackgroundJobStateChanger>();
        private readonly HashSet<string> _members = new HashSet<string>();
        private readonly DateTime _now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
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
            var attribute = new RateLimitAttribute("resource", 1, 3600) { Action = ConcurrencyAction.Wait, WaitTimeoutSeconds = 1 };
            _context.Connection.Setup(x => x.GetAllItemsFromSet("ratelimit:resource:3600")).Returns(_members);
            _members.Add($"{_timestamp}:a");
            var elapsed = Stopwatch.StartNew();
            ConcurrencyControl.Clock = () => _now + elapsed.Elapsed;

            using (var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(shutdown.Token);

                Assert.Throws<ConcurrencyLimitException>(() => attribute.OnPerforming(_context.GetPerformingContext()));
            }

            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Waited {elapsed.Elapsed}");
        }

        [Fact, StaticLock]
        public void OnPerforming_ExpiresTheSet_AWindowAfterTheLastStart_WhenTheStorageSupportsIt()
        {
            var storageTransaction = new Mock<JobStorageTransaction>();
            _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
            _context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
            var attribute = new RateLimitAttribute("resource", 2, 60);

            attribute.OnPerforming(_context.GetPerformingContext());

            storageTransaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
            storageTransaction.Verify(x => x.ExpireSet(Key, TimeSpan.FromSeconds(60) + TimeSpan.FromMinutes(1)));
        }

        [Fact, StaticLock]
        public void OnPerforming_DoesNotExpireTheSet_WhenTheStorageDoesNotSupportIt()
        {
            var storageTransaction = new Mock<JobStorageTransaction>();
            _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
            var attribute = new RateLimitAttribute("resource", 2, 60);

            attribute.OnPerforming(_context.GetPerformingContext());

            storageTransaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
            storageTransaction.Verify(x => x.ExpireSet(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnPerformed_GivesTheStartBack_WhenALaterFilterCanceledTheRun()
        {
            var attribute = new RateLimitAttribute("resource", 2, 60);
            attribute.OnPerforming(_context.GetPerformingContext());

            attribute.OnPerformed(_context.GetPerformedContext(canceled: true));

            _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"));
        }

        [Fact, StaticLock]
        public void OnPerformed_KeepsTheStart_WhenTheJobMethodWasCalled_AndThrew()
        {
            var attribute = new RateLimitAttribute("resource", 2, 60);
            attribute.OnPerforming(_context.GetPerformingContext());
            _context.Object.Items[ConcurrencyControl.MethodInvokedItem] = true;

            attribute.OnPerformed(_context.GetPerformedContext(exception: new InvalidOperationException()));

            _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnPerformed_KeepsTheStart_WhenTheJobSucceeded()
        {
            var attribute = new RateLimitAttribute("resource", 2, 60);
            attribute.OnPerforming(_context.GetPerformingContext());

            attribute.OnPerformed(_context.GetPerformedContext());

            _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnPerformed_GivesTheStartBack_WhenALaterFilterThrew_BeforeTheJobMethodWasCalled()
        {
            // DisableConcurrentExecution's lock timed out, for example: the job never started
            var attribute = new RateLimitAttribute("resource", 2, 60);
            attribute.OnPerforming(_context.GetPerformingContext());

            attribute.OnPerformed(_context.GetPerformedContext(exception: new DistributedLockTimeoutException("lock")));

            _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"));
        }

        [Fact, StaticLock]
        public void Perform_KeepsTheStart_WhenALaterFilterThrows_AfterTheJobMethodRan()
        {
            var performer = CreatePerformer(new ThrowingFilter(onPerforming: false));

            Assert.Throws<JobPerformanceException>(() => performer.Perform(_context.Object));

            _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
            _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void Perform_GivesTheStartBack_WhenALaterFilterThrows_BeforeTheJobMethod()
        {
            var performer = CreatePerformer(new ThrowingFilter(onPerforming: true));

            Assert.Throws<JobPerformanceException>(() => performer.Perform(_context.Object));

            _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
            _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"));
        }

        [Fact, StaticLock]
        public void OnPerforming_CountsLimitsWithDifferentWindows_InSeparateSets()
        {
            // A start that an hour-long limit still needs is stale for a one-second limit on the same resource
            var hourly = new HashSet<string> { $"{_timestamp - 100}:a" };
            _context.Connection.Setup(x => x.GetAllItemsFromSet("ratelimit:api:3600")).Returns(hourly);
            _context.Connection.Setup(x => x.GetAllItemsFromSet("ratelimit:api:1")).Returns(new HashSet<string>());
            var hourlyPerforming = _context.GetPerformingContext();

            new RateLimitAttribute("api", 10, 1).OnPerforming(_context.GetPerformingContext());
            new RateLimitAttribute("api", 1, 3600).OnPerforming(hourlyPerforming);

            _transaction.Verify(x => x.AddToSet("ratelimit:api:1", $"{_timestamp}:JobId", _timestamp));
            _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.True(hourlyPerforming.Canceled);
        }

        [Fact, StaticLock]
        public void OnPerforming_CountsByScore_WithoutReadingTheWholeSet_WhenTheStorageSupportsIt()
        {
            var connection = UseScoredSetConnection();
            var expired = $"{_timestamp - 61}:old";
            connection.Setup(x => x.GetFirstByLowestScoreFromSet(Key, 0, _timestamp - 61, 100)).Returns(new List<string> { expired });
            connection.Setup(x => x.GetSetCount(Key)).Returns(2);
            connection.Setup(x => x.GetFirstByLowestScoreFromSet(Key, _timestamp - 60, It.IsAny<double>())).Returns($"{_timestamp - 10}:a");
            var attribute = new RateLimitAttribute("resource", 2, 60);
            var performing = _context.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            connection.Verify(x => x.GetAllItemsFromSet(It.IsAny<string>()), Times.Never);
            _transaction.Verify(x => x.RemoveFromSet(Key, expired));
            _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        }

        [Fact, StaticLock]
        public void OnPerforming_ReschedulesFromTheOldestStartByScore_WhenTheStorageSupportsIt()
        {
            var connection = UseScoredSetConnection();
            connection.Setup(x => x.GetFirstByLowestScoreFromSet(Key, 0, _timestamp - 61, 100)).Returns(new List<string>());
            connection.Setup(x => x.GetSetCount(Key)).Returns(2);
            connection.Setup(x => x.GetFirstByLowestScoreFromSet(Key, _timestamp - 60, It.IsAny<double>())).Returns($"{_timestamp - 50}:a");
            var attribute = new RateLimitAttribute("resource", 2, 60);
            var performing = _context.GetPerformingContext();

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
            var connection = UseScoredSetConnection();
            var expired = new List<string>();
            for (var i = 0; i < 100; i++) expired.Add($"{_timestamp - 100 - i}:old{i}");
            connection.Setup(x => x.GetFirstByLowestScoreFromSet(Key, 0, _timestamp - 61, 100)).Returns(expired);
            connection.Setup(x => x.GetAllItemsFromSet(Key)).Returns(new HashSet<string>(expired) { $"{_timestamp - 300}:older" });
            var attribute = new RateLimitAttribute("resource", 1, 60);

            attribute.OnPerforming(_context.GetPerformingContext());

            connection.Verify(x => x.GetSetCount(It.IsAny<string>()), Times.Never);
            _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp - 300}:older"));
            _transaction.Verify(x => x.RemoveFromSet(Key, It.IsAny<string>()), Times.Exactly(101));
            _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        }

        [Fact, StaticLock]
        public void OnPerforming_ShortensTheKey_OfALongResource()
        {
            var tenant = new string('t', 95);
            _context.BackgroundJob.Job = Job.FromExpression(() => ForTenant(tenant));
            _context.Connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns(new HashSet<string>());
            var attribute = new RateLimitAttribute("tenant-{0}", 1, 60);

            attribute.OnPerforming(_context.GetPerformingContext());

            _transaction.Verify(x => x.AddToSet(
                It.Is<string>(key => key.StartsWith("ratelimit:tenant-ttt", StringComparison.Ordinal) && key.Contains("~") && key.Length <= 100),
                $"{_timestamp}:JobId",
                _timestamp));
        }

        [Fact, StaticLock]
        public void OnPerforming_RecordsTheStart_WhenUnderTheLimit()
        {
            var attribute = new RateLimitAttribute("resource", 2, 60);
            _members.Add($"{_timestamp - 10}:a");
            var performing = _context.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
            _transaction.Verify(x => x.Commit());
        }

        [Fact, StaticLock]
        public void OnPerforming_Reschedules_ToWhenTheOldestStartLeavesTheWindow()
        {
            var attribute = new RateLimitAttribute("resource", 2, 60);
            _members.Add($"{_timestamp - 50}:a");
            _members.Add($"{_timestamp - 10}:b");
            var performing = _context.GetPerformingContext();

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
            var attribute = new RateLimitAttribute("resource", 1, 60);
            var expired = $"{_timestamp - 61}:old";
            _members.Add(expired);
            var performing = _context.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            _transaction.Verify(x => x.RemoveFromSet(Key, expired));
            _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        }

        [Fact, StaticLock]
        public void OnPerforming_CancelsTheJob_WhenActionIsSkip()
        {
            var attribute = new RateLimitAttribute("resource", 1, 60) { Action = ConcurrencyAction.Skip };
            _members.Add($"{_timestamp}:a");
            var performing = _context.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.True(performing.Canceled);
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnPerforming_ThrowsAnException_WhenActionIsWait_AndTheTimeoutExpires()
        {
            var attribute = new RateLimitAttribute("resource", 1, 60) { Action = ConcurrencyAction.Wait, WaitTimeoutSeconds = 0 };
            _members.Add($"{_timestamp}:a");

            Assert.Throws<ConcurrencyLimitException>(() => attribute.OnPerforming(_context.GetPerformingContext()));
        }

        [Fact, StaticLock]
        public void OnPerforming_WaitsUntilTheOldestStartLeavesTheWindow_ThenRecordsTheStart_WhenActionIsWait()
        {
            var attribute = new RateLimitAttribute("resource", 1, 60) { Action = ConcurrencyAction.Wait };
            var full = new HashSet<string> { $"{_timestamp - 60}:a" };
            var reads = 0;
            _context.Connection.Setup(x => x.GetAllItemsFromSet(Key)).Returns(() => reads++ == 0 ? full : new HashSet<string>());
            // One millisecond before the oldest start leaves the window, so the wait is that millisecond
            ConcurrencyControl.Clock = () => _now.AddMilliseconds(999);
            var performing = _context.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            Assert.Equal(2, reads);
            _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnPerforming_ThrowsOperationCanceled_WhenShutdownIsRequestedWhileWaiting()
        {
            var attribute = new RateLimitAttribute("resource", 1, 60) { Action = ConcurrencyAction.Wait };
            _members.Add($"{_timestamp}:a");
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(true));

            Assert.Throws<OperationCanceledException>(() => attribute.OnPerforming(_context.GetPerformingContext()));

            _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnPerformed_DoesNotTouchTheStorage()
        {
            var attribute = new RateLimitAttribute("resource", 1, 60);

            attribute.OnPerformed(_context.GetPerformedContext());

            _context.Connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
            _context.Connection.Verify(x => x.GetAllItemsFromSet(It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnPerformed_DoesNotThrow_WhenTheStartCannotBeGivenBack()
        {
            var attribute = new RateLimitAttribute("resource", 2, 60);
            attribute.OnPerforming(_context.GetPerformingContext());
            _transaction.Setup(x => x.Commit()).Throws(new InvalidOperationException("storage is down"));

            attribute.OnPerformed(_context.GetPerformedContext(canceled: true));

            _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"));
        }

        [Fact]
        public void AJob_MayCarrySeveralLimits()
        {
            Assert.True(new RateLimitAttribute("api", 10, 1).AllowMultiple);

            var filters = JobFilterProviders.Providers.GetFilters(Job.FromExpression(() => Tiered()))
                .Select(static x => x.Instance)
                .OfType<RateLimitAttribute>()
                .ToArray();

            Assert.Equal(new[] { 1, 3600 }, filters.Select(static x => x.PerSeconds).OrderBy(static x => x));
        }

        [Fact]
        public void AGlobalLimit_AndAMethodLimit_BothApply()
        {
            var global = new JobFilterCollection();
            global.Add(new RateLimitAttribute("tenant", 5, 60));
            var providers = new JobFilterProviderCollection(global, new JobFilterAttributeFilterProvider());

            var filters = providers.GetFilters(Job.FromExpression(() => Tiered()))
                .Select(static x => x.Instance)
                .OfType<RateLimitAttribute>()
                .ToArray();

            Assert.Equal(3, filters.Length);
            Assert.Contains(filters, static x => x.Resource == "tenant");
        }

        [Fact, StaticLock]
        public void Perform_RunsTheJob_WhenEveryLimitAllowsIt()
        {
            UseTieredSets(new HashSet<string>(), new HashSet<string>());
            var performer = CreateTieredPerformer(ConcurrencyAction.Reschedule);

            performer.Perform(_context.Object);

            _transaction.Verify(x => x.AddToSet("ratelimit:api:1", $"{_timestamp}:JobId", _timestamp));
            _transaction.Verify(x => x.AddToSet("ratelimit:api:3600", $"{_timestamp}:JobId", _timestamp));
            _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        }

        [Fact, StaticLock]
        public void Perform_GivesEveryLimitsStartBack_WhenALaterFilterCancelsTheRun()
        {
            UseTieredSets(new HashSet<string>(), new HashSet<string>());
            var filters = new JobFilterCollection();
            filters.Add(new RateLimitAttribute("api", 10, 1), 0);
            filters.Add(new RateLimitAttribute("api", 1, 3600), 1);
            filters.Add(new CancelingFilter(), 2);
            var performer = new BackgroundJobPerformer(filters, new JobActivator());

            performer.Perform(_context.Object);

            _transaction.Verify(x => x.RemoveFromSet("ratelimit:api:1", $"{_timestamp}:JobId"));
            _transaction.Verify(x => x.RemoveFromSet("ratelimit:api:3600", $"{_timestamp}:JobId"));
        }

        [Fact, StaticLock]
        public void Perform_GivesTheOtherLimitsStartBack_WhenOneLimitReschedules()
        {
            UseTieredSets(new HashSet<string>(), new HashSet<string> { $"{_timestamp - 100}:a" });
            var performer = CreateTieredPerformer(ConcurrencyAction.Reschedule);

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
            UseTieredSets(new HashSet<string>(), new HashSet<string> { $"{_timestamp - 100}:a" });
            var performer = CreateTieredPerformer(ConcurrencyAction.Wait, waitTimeoutSeconds: 5);
            var elapsed = Stopwatch.StartNew();
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
            UseTieredSets(new HashSet<string> { $"{_timestamp}:a" }, new HashSet<string>());
            var filters = new JobFilterCollection();
            filters.Add(new RateLimitAttribute("api", 1, 1) { Action = ConcurrencyAction.Wait, WaitTimeoutSeconds = 0 }, 0);
            filters.Add(new RateLimitAttribute("api", 1, 3600) { Action = ConcurrencyAction.Wait, WaitTimeoutSeconds = 0 }, 1);
            var performer = new BackgroundJobPerformer(filters, new JobActivator());

            var exception = Assert.Throws<JobPerformanceException>(() => performer.Perform(_context.Object));

            Assert.IsType<ConcurrencyLimitException>(exception.InnerException);
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        }

        [Fact, StaticLock]
        public void TwoLimits_WithTheSameResourceAndWindow_KeepTheRunsOwnStart_WhenAnotherFilterHeldTheRunPastTheWindow()
        {
            // The first limit records the run's start; another filter holds the run for 61 seconds, while another
            // job starts. The run's own start is then older than the window, but it is still the run's start: the
            // second limit (1 per minute) must count it and refuse, not remove it and let a second start in
            var filters = new JobFilterCollection();
            filters.Add(new RateLimitAttribute("resource", 1, 60), 0);
            filters.Add(new ClockAdvancingFilter(TimeSpan.FromSeconds(61), () => _members.Add($"{_timestamp + 31}:b")), 1);
            filters.Add(new RateLimitAttribute("resource", 1, 60), 2);
            var performer = new BackgroundJobPerformer(filters, new JobActivator());
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
            var filters = new JobFilterCollection();
            filters.Add(new RateLimitAttribute("resource", 2, 60), 0);
            filters.Add(new RateLimitAttribute("resource", 2, 60), 1);
            var performer = new BackgroundJobPerformer(filters, new JobActivator());
            _transaction.Setup(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp)).Callback(() => _members.Add($"{_timestamp}:JobId"));

            performer.Perform(_context.Object);

            _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp), Times.Once);
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        }

        [Fact, StaticLock]
        public void TwoLimits_WithTheSameResourceAndWindow_TheStricterOneStillRefuses()
        {
            _members.Add($"{_timestamp - 10}:a");
            var filters = new JobFilterCollection();
            filters.Add(new RateLimitAttribute("resource", 2, 60), 0);
            filters.Add(new RateLimitAttribute("resource", 1, 60), 1);
            var performer = new BackgroundJobPerformer(filters, new JobActivator());
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
            var filters = new JobFilterCollection();
            filters.Add(new RateLimitAttribute("api", 10, 1) { Action = action, WaitTimeoutSeconds = waitTimeoutSeconds }, 0);
            filters.Add(new RateLimitAttribute("api", 1, 3600) { Action = action, WaitTimeoutSeconds = waitTimeoutSeconds }, 1);

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
            var connection = new Mock<JobStorageConnection>();
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
            var filters = new JobFilterCollection();
            filters.Add(new RateLimitAttribute("resource", 2, 60), 0);
            filters.Add(laterFilter, 1);

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
        private sealed class ClockAdvancingFilter : IServerFilter
        {
            private readonly TimeSpan _by;
            private readonly Action _meanwhile;

            public ClockAdvancingFilter(TimeSpan by, Action meanwhile)
            {
                _by = by;
                _meanwhile = meanwhile;
            }

            public void OnPerforming(PerformingContext context)
            {
                var later = ConcurrencyControl.Clock() + _by;
                ConcurrencyControl.Clock = () => later;
                _meanwhile();
            }

            public void OnPerformed(PerformedContext context)
            {
            }
        }

        private sealed class ThrowingFilter : IServerFilter
        {
            private readonly bool _onPerforming;

            public ThrowingFilter(bool onPerforming)
            {
                _onPerforming = onPerforming;
            }

            public void OnPerforming(PerformingContext context)
            {
                if (_onPerforming) throw new DistributedLockTimeoutException("lock");
            }

            public void OnPerformed(PerformedContext context)
            {
                if (!_onPerforming) throw new InvalidOperationException("after the method");
            }
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void ForTenant(string tenant)
        {
        }
    }
}
