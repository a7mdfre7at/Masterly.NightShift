using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class FailedJobRetentionFilterFacts
    {
        private const string JobId = "JobId";

        [Fact]
        public void Ctor_RejectsRetentionsThatAreNotPositiveOrTooLong()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FailedJobRetentionFilter(TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FailedJobRetentionFilter(TimeSpan.FromMinutes(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FailedJobRetentionFilter(TimeSpan.FromDays(ExpireAfterAttribute.MaxDays + 1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FailedJobRetentionFilter(TimeSpan.MaxValue));

            Assert.Equal(TimeSpan.FromDays(ExpireAfterAttribute.MaxDays), new FailedJobRetentionFilter(TimeSpan.FromDays(ExpireAfterAttribute.MaxDays)).Retention);
            Assert.Equal(TimeSpan.FromSeconds(1), new FailedJobRetentionFilter(TimeSpan.FromSeconds(1)).Retention);
        }

        [Fact]
        public void OnStateUnapplied_SetsTheExpiration_ForTheFailedStateOnly()
        {
            var filter = new FailedJobRetentionFilter(TimeSpan.FromDays(30));
            var transaction = new Mock<IWriteOnlyTransaction>();

            var failed = new ApplyStateContextMock { NewStateObject = new FailedState(new InvalidOperationException()), JobExpirationTimeout = TimeSpan.FromDays(1) };
            filter.OnStateUnapplied(failed.Object, transaction.Object);
            Assert.Equal(TimeSpan.FromDays(30), failed.Object.JobExpirationTimeout);
            Assert.True(failed.Object.ExpireInNonFinalState);

            var scheduled = new ApplyStateContextMock { NewStateObject = new ScheduledState(TimeSpan.FromMinutes(1)), JobExpirationTimeout = TimeSpan.FromDays(1) };
            filter.OnStateUnapplied(scheduled.Object, transaction.Object);
            Assert.Equal(TimeSpan.FromDays(1), scheduled.Object.JobExpirationTimeout);
            Assert.False(scheduled.Object.ExpireInNonFinalState);

            var succeeded = new ApplyStateContextMock { NewStateObject = new SucceededState(null, 1, 1), JobExpirationTimeout = TimeSpan.FromDays(1) };
            filter.OnStateUnapplied(succeeded.Object, transaction.Object);
            Assert.Equal(TimeSpan.FromDays(1), succeeded.Object.JobExpirationTimeout);
            Assert.False(succeeded.Object.ExpireInNonFinalState);
        }

        [Fact]
        public void StateMachine_ExpiresAFailedJob_InTheSameTransaction()
        {
            var transaction = ApplyState(new FailedState(new InvalidOperationException()), new FailedJobRetentionFilter(TimeSpan.FromDays(30)));

            transaction.Verify(x => x.ExpireJob(JobId, TimeSpan.FromDays(30)));
            transaction.Verify(x => x.PersistJob(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void StateMachine_PersistsAFailedJob_WithoutRetention()
        {
            var transaction = ApplyState(new FailedState(new InvalidOperationException()));

            transaction.Verify(x => x.PersistJob(JobId));
            transaction.Verify(x => x.ExpireJob(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact]
        public void StateMachine_DoesNotExpireAFailure_ThatAutomaticRetryTurnsIntoARetry()
        {
            var transaction = ApplyState(
                new FailedState(new InvalidOperationException()),
                new AutomaticRetryAttribute { Attempts = 3 },
                new FailedJobRetentionFilter(TimeSpan.FromDays(30)));

            transaction.Verify(x => x.SetJobState(JobId, It.Is<IState>(s => s.Name == ScheduledState.StateName)));
            transaction.Verify(x => x.PersistJob(JobId));
            transaction.Verify(x => x.ExpireJob(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact]
        public void StateMachine_ExpiresTheJob_WhenAutomaticRetryGivesUp()
        {
            var transaction = ApplyState(
                new FailedState(new InvalidOperationException()),
                new AutomaticRetryAttribute { Attempts = 0 },
                new FailedJobRetentionFilter(TimeSpan.FromDays(30)));

            transaction.Verify(x => x.SetJobState(JobId, It.Is<IState>(s => s.Name == FailedState.StateName)));
            transaction.Verify(x => x.ExpireJob(JobId, TimeSpan.FromDays(30)));
            transaction.Verify(x => x.PersistJob(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void StateMachine_PersistsTheJobAgain_WhenItLeavesTheFailedState()
        {
            var transaction = ApplyState(
                new EnqueuedState(),
                FailedState.StateName,
                new FailedJobRetentionFilter(TimeSpan.FromDays(30)));

            transaction.Verify(x => x.PersistJob(JobId));
            transaction.Verify(x => x.ExpireJob(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact]
        public void StateMachine_UsesTheStorageExpiration_WhenAFailedJobIsDeleted()
        {
            var transaction = ApplyState(
                new DeletedState(),
                FailedState.StateName,
                new FailedJobRetentionFilter(TimeSpan.FromDays(30)));

            transaction.Verify(x => x.ExpireJob(JobId, TimeSpan.FromDays(1)));
        }

        [Fact]
        public void StateMachine_PrefersThePerMethodExpireAfter_AndExpiresTheConsoleWithIt()
        {
            var transaction = ApplyState(
                new FailedState(new InvalidOperationException()),
                new JobFilter(new FailedJobRetentionFilter(TimeSpan.FromDays(30)), JobFilterScope.Global, null),
                new JobFilter(new JobConsoleStateFilter(), JobFilterScope.Global, null),
                new JobFilter(new ExpireAfterAttribute(days: 7) { IncludeFailed = true }, JobFilterScope.Method, null));

            transaction.Verify(x => x.ExpireList(JobConsole.GetConsoleKey(JobId), TimeSpan.FromDays(7)));
            transaction.Verify(x => x.ExpireJob(JobId, TimeSpan.FromDays(7)));
            transaction.Verify(x => x.PersistList(It.IsAny<string>()), Times.Never);
            transaction.Verify(x => x.PersistJob(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void StateMachine_KeepsTheGlobalRetention_WhenThePerMethodExpireAfterLeavesFailedJobsOut()
        {
            var transaction = ApplyState(
                new FailedState(new InvalidOperationException()),
                new JobFilter(new FailedJobRetentionFilter(TimeSpan.FromDays(30)), JobFilterScope.Global, null),
                new JobFilter(new ExpireAfterAttribute(days: 7), JobFilterScope.Method, null));

            transaction.Verify(x => x.ExpireJob(JobId, TimeSpan.FromDays(30)));
        }

        private static Mock<JobStorageTransaction> ApplyState(IState newState, params object[] filters)
        {
            return ApplyState(newState, ProcessingState.StateName, filters);
        }

        private static Mock<JobStorageTransaction> ApplyState(IState newState, string oldStateName, params object[] filters)
        {
            return ApplyState(newState, oldStateName, filters.Select(x => new JobFilter(x, JobFilterScope.Global, null)).ToArray());
        }

        private static Mock<JobStorageTransaction> ApplyState(IState newState, params JobFilter[] filters)
        {
            return ApplyState(newState, ProcessingState.StateName, filters);
        }

        private static Mock<JobStorageTransaction> ApplyState(IState newState, string oldStateName, JobFilter[] filters)
        {
            var filterProvider = new Mock<IJobFilterProvider>();
            filterProvider.Setup(x => x.GetFilters(It.IsAny<Job>())).Returns(filters);

            var transaction = new Mock<JobStorageTransaction>();
            var context = new ApplyStateContextMock
            {
                NewStateObject = newState,
                OldStateName = oldStateName,
                JobExpirationTimeout = TimeSpan.FromDays(1),
                BackgroundJob = new BackgroundJobMock { Id = JobId, Job = Job.FromExpression(() => Sample()) },
            };
            context.Transaction = transaction.As<IWriteOnlyTransaction>();
            context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);

            var coreStateMachine = new CoreStateMachine(static (_, stateName) => new CoreStateMachine.StateHandlersCollection(
                new List<IStateHandler>(),
                Enumerable.Empty<IStateHandler>(),
                stateName));
            var stateMachine = new StateMachine(new JobFilterProviderCollection(filterProvider.Object), coreStateMachine);

            stateMachine.ApplyState(context.Object);
            return transaction;
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample()
        {
        }
    }
}
