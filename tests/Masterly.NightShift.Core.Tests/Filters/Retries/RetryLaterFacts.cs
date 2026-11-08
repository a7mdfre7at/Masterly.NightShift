using System;
using System.Collections.Generic;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class RetryLaterFacts : IDisposable
    {
        private readonly DateTime _now = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Dictionary<string, string> _parameters = new Dictionary<string, string>();
        private readonly ElectStateContextMock _context = new ElectStateContextMock();

        public RetryLaterFacts()
        {
            AutomaticRetryAttribute.Clock = () => _now;

            _connection.Setup(x => x.GetJobParameter("id", It.IsAny<string>())).Returns<string, string>((_, name) => _parameters.TryGetValue(name, out var value) ? value : null);
            _connection.Setup(x => x.SetJobParameter("id", It.IsAny<string>(), It.IsAny<string>())).Callback<string, string, string>((_, name, value) => _parameters[name] = value);

            _context.ApplyContext.BackgroundJob.Id = "id";
            _context.ApplyContext.Connection = _connection;
            _context.ApplyContext.Transaction = _transaction;
        }

        public void Dispose()
        {
            AutomaticRetryAttribute.Clock = () => DateTime.UtcNow;
        }

        [Fact]
        public void Ctor_WithDelay_RejectsNegativeAndHugeDelays()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryLaterException(TimeSpan.FromSeconds(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryLaterException(TimeSpan.FromDays(400)));
        }

        [Fact]
        public void GetRetryAt_UsesTheDelay_OrTheMoment()
        {
            Assert.Equal(_now.AddMinutes(5), new RetryLaterException(TimeSpan.FromMinutes(5)).GetRetryAt(_now));
            Assert.Equal(_now.AddHours(1), new RetryLaterException(_now.AddHours(1)).GetRetryAt(_now));
            Assert.Equal(_now, new RetryLaterException(_now.AddHours(-1), "in the past").GetRetryAt(_now).AddHours(1));
        }

        [Fact]
        public void Message_IsTheReason_WhenGiven()
        {
            Assert.Equal("Rate limited", new RetryLaterException(TimeSpan.FromMinutes(1), "Rate limited").Message);
            Assert.Contains("00:01:00", new RetryLaterException(TimeSpan.FromMinutes(1)).Message);
        }

        [Fact]
        public void OnStateElection_SchedulesForTheRequestedMoment_WithoutUsingAnAttempt()
        {
            var filter = new AutomaticRetryAttribute { Attempts = 3 };
            _context.ApplyContext.NewStateObject = new FailedState(new RetryLaterException(TimeSpan.FromMinutes(10), "Partner API is busy"));

            filter.OnStateElection(_context.Object);

            var scheduled = Assert.IsType<ScheduledState>(_context.Object.CandidateState);
            Assert.Equal(_now.AddMinutes(10), scheduled.EnqueueAt);
            Assert.StartsWith("Retry later (1 of 100): Partner API is busy", scheduled.Reason);
            Assert.Equal("1", _parameters[AutomaticRetryAttribute.RetryLaterCountParameter]);
            Assert.False(_parameters.ContainsKey("RetryCount"));
        }

        [Fact]
        public void OnStateElection_EnqueuesImmediately_WhenTheMomentHasPassed()
        {
            var filter = new AutomaticRetryAttribute();
            _context.ApplyContext.NewStateObject = new FailedState(new RetryLaterException(_now.AddMinutes(-1)));

            filter.OnStateElection(_context.Object);

            Assert.IsType<EnqueuedState>(_context.Object.CandidateState);
        }

        [Fact]
        public void OnStateElection_CountsTheRequests()
        {
            var filter = new AutomaticRetryAttribute();
            _parameters[AutomaticRetryAttribute.RetryLaterCountParameter] = "41";
            _context.ApplyContext.NewStateObject = new FailedState(new RetryLaterException(TimeSpan.FromSeconds(30)));

            filter.OnStateElection(_context.Object);

            Assert.Equal("42", _parameters[AutomaticRetryAttribute.RetryLaterCountParameter]);
            Assert.Contains("(42 of 100)", _context.Object.CandidateState.Reason);
        }

        [Fact]
        public void OnStateElection_FallsBackToTheUsualRetry_OnceTheRequestsAreUsedUp()
        {
            var filter = new AutomaticRetryAttribute { MaxRetryLaterRequests = 2, Attempts = 5, DelaysInSeconds = new[] { 7 } };
            _parameters[AutomaticRetryAttribute.RetryLaterCountParameter] = "2";
            _context.ApplyContext.NewStateObject = new FailedState(new RetryLaterException(TimeSpan.FromHours(1)));

            filter.OnStateElection(_context.Object);

            var scheduled = Assert.IsType<ScheduledState>(_context.Object.CandidateState);
            Assert.StartsWith("Retry attempt 1 of 5", scheduled.Reason);
            Assert.Equal("1", _parameters["RetryCount"]);
            Assert.Equal("2", _parameters[AutomaticRetryAttribute.RetryLaterCountParameter]);
        }

        [Fact]
        public void OnStateElection_TreatsTheExceptionAsAnyOther_WhenDisabled()
        {
            var filter = new AutomaticRetryAttribute { MaxRetryLaterRequests = 0, Attempts = 0 };
            _context.ApplyContext.NewStateObject = new FailedState(new RetryLaterException(TimeSpan.FromHours(1)));

            filter.OnStateElection(_context.Object);

            Assert.IsType<FailedState>(_context.Object.CandidateState);
            Assert.Empty(_parameters);
        }

        [Fact]
        public void MaxRetryLaterRequests_RejectsNegativeValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AutomaticRetryAttribute { MaxRetryLaterRequests = -1 });
            Assert.Equal(100, new AutomaticRetryAttribute().MaxRetryLaterRequests);
        }

        [Fact]
        public void OnStateApplied_AddsRetryLaterJobs_ToTheRetriesSet()
        {
            var filter = new AutomaticRetryAttribute();
            var applyContext = new ApplyStateContextMock { NewStateObject = new ScheduledState(_now.AddMinutes(1)) { Reason = "Retry later (1 of 100): busy" } };
            applyContext.BackgroundJob.Id = "id";

            filter.OnStateApplied(applyContext.Object, _transaction.Object);

            _transaction.Verify(x => x.AddToSet("retries", "id"));
        }
    }
}
