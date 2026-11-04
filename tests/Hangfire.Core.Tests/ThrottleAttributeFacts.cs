using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Moq;
using Xunit;

namespace Hangfire.Core.Tests
{
    public class ThrottleAttributeFacts : IDisposable
    {
        private const string Key = "throttle:resource";

        private readonly PerformContextMock _context = new PerformContextMock();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Mock<IBackgroundJobStateChanger> _stateChanger = new Mock<IBackgroundJobStateChanger>();
        private readonly HashSet<string> _members = new HashSet<string>();
        private readonly DateTime _now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        private readonly long _timestamp;

        public ThrottleAttributeFacts()
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
        public void Defaults_AreAMutexThatReschedules()
        {
            var attribute = new ThrottleAttribute("resource");

            Assert.Equal(1, attribute.MaxConcurrency);
            Assert.Equal(ConcurrencyAction.Reschedule, attribute.Action);
            Assert.Equal(15, attribute.RetryDelaySeconds);
        }

        [Fact, StaticLock]
        public void OnPerforming_AcquiresASlot_WhenBelowTheLimit()
        {
            var attribute = new ThrottleAttribute("resource") { MaxConcurrency = 2 };
            _members.Add($"{_timestamp}:other");
            var performing = _context.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
            _transaction.Verify(x => x.Commit());
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnPerforming_ReschedulesTheJob_WhenAllSlotsAreTaken()
        {
            var attribute = new ThrottleAttribute("resource") { RetryDelaySeconds = 20 };
            _members.Add($"{_timestamp}:other");
            var performing = _context.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.True(performing.Canceled);
            _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
            _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
                c.BackgroundJobId == "JobId"
                && c.NewState is ScheduledState
                && ((ScheduledState)c.NewState).EnqueueAt >= _now.AddSeconds(20)
                && ((ScheduledState)c.NewState).EnqueueAt <= _now.AddSeconds(21)
                && c.ExpectedStates.Contains(ProcessingState.StateName))));
        }

        [Fact, StaticLock]
        public void OnPerforming_CancelsTheJob_WhenActionIsSkip()
        {
            var attribute = new ThrottleAttribute("resource") { Action = ConcurrencyAction.Skip };
            _members.Add($"{_timestamp}:other");
            var performing = _context.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.True(performing.Canceled);
            Assert.True(performing.Items.ContainsKey(BackgroundJobPerformer.ContextCanceledKey));
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnPerforming_ThrowsAnException_WhenActionIsWait_AndTheTimeoutExpires()
        {
            var attribute = new ThrottleAttribute("resource") { Action = ConcurrencyAction.Wait, WaitTimeoutSeconds = 0 };
            _members.Add($"{_timestamp}:other");

            var exception = Assert.Throws<ConcurrencyLimitException>(() => attribute.OnPerforming(_context.GetPerformingContext()));

            Assert.Equal("resource", exception.Resource);
        }

        [Fact, StaticLock]
        public void OnPerforming_ReclaimsStaleSlots()
        {
            var attribute = new ThrottleAttribute("resource") { StaleTimeoutMinutes = 30 };
            var stale = $"{_timestamp - 31 * 60}:crashed";
            _members.Add(stale);
            var performing = _context.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            _transaction.Verify(x => x.RemoveFromSet(Key, stale));
            _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        }

        [Fact, StaticLock]
        public void OnPerforming_ReclaimsTheSlotOfItsOwnPreviousAttempt()
        {
            var attribute = new ThrottleAttribute("resource");
            var previous = $"{_timestamp - 5}:JobId";
            _members.Add(previous);
            var performing = _context.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            _transaction.Verify(x => x.RemoveFromSet(Key, previous));
            _transaction.Verify(x => x.AddToSet(Key, $"{_timestamp}:JobId", _timestamp));
        }

        [Fact, StaticLock]
        public void OnPerforming_TreatsUnparsableMembersAsStale()
        {
            var attribute = new ThrottleAttribute("resource");
            _members.Add("garbage");
            var performing = _context.GetPerformingContext();

            attribute.OnPerforming(performing);

            Assert.False(performing.Canceled);
            _transaction.Verify(x => x.RemoveFromSet(Key, "garbage"));
        }

        [Fact, StaticLock]
        public void OnPerformed_ReleasesTheAcquiredSlot()
        {
            var attribute = new ThrottleAttribute("resource");
            var performing = _context.GetPerformingContext();
            attribute.OnPerforming(performing);
            var performed = new PerformedContext(performing, null, false, null);

            attribute.OnPerformed(performed);

            _transaction.Verify(x => x.RemoveFromSet(Key, $"{_timestamp}:JobId"));
            _transaction.Verify(x => x.Commit(), Times.Exactly(2));
        }

        [Fact, StaticLock]
        public void OnPerformed_DoesNothing_WhenNoSlotWasAcquired()
        {
            var attribute = new ThrottleAttribute("resource");

            attribute.OnPerformed(_context.GetPerformedContext());

            _transaction.Verify(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void Resource_IsFormattedWithJobArguments_AndLowercased()
        {
            var attribute = new ThrottleAttribute("tenant-{0}");
            _context.BackgroundJob.Job = Job.FromExpression(() => SampleWithArgument("Contoso"));
            _context.Connection.Setup(x => x.GetAllItemsFromSet("throttle:tenant-contoso")).Returns(new HashSet<string>());

            attribute.OnPerforming(_context.GetPerformingContext());

            _transaction.Verify(x => x.AddToSet("throttle:tenant-contoso", $"{_timestamp}:JobId", _timestamp));
        }

        [Fact, StaticLock]
        public void Resource_DefaultsToTypeAndMethod()
        {
            var attribute = new ThrottleAttribute();
            var expectedKey = "throttle:throttleattributefacts.sample";
            _context.Connection.Setup(x => x.GetAllItemsFromSet(expectedKey)).Returns(new HashSet<string>());

            attribute.OnPerforming(_context.GetPerformingContext());

            _transaction.Verify(x => x.AddToSet(expectedKey, $"{_timestamp}:JobId", _timestamp));
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample()
        {
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void SampleWithArgument(string tenant)
        {
        }
    }
}
