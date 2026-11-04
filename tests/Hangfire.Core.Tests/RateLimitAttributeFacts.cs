using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Moq;
using Xunit;

namespace Hangfire.Core.Tests
{
    public class RateLimitAttributeFacts : IDisposable
    {
        private const string Key = "ratelimit:resource";

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

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample()
        {
        }
    }
}
