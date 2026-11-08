using System;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class ExpireAfterAttributeFacts
    {
        [Fact]
        public void Ctor_ParsesTimeSpansAndDays_AndRejectsNonsense()
        {
            Assert.Equal(TimeSpan.FromMinutes(10), new ExpireAfterAttribute("00:10:00").Duration);
            Assert.Equal(TimeSpan.FromDays(30), new ExpireAfterAttribute(days: 30).Duration);
            Assert.Throws<ArgumentException>(() => new ExpireAfterAttribute("soon"));
            Assert.Throws<ArgumentException>(() => new ExpireAfterAttribute("-00:01:00"));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ExpireAfterAttribute(0));
        }

        [Fact]
        public void Ctor_RejectsDurationsThatWouldOverflowTheExpirationDate()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ExpireAfterAttribute(ExpireAfterAttribute.MaxDays + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ExpireAfterAttribute(Int32.MaxValue));
            Assert.Throws<ArgumentException>(() => new ExpireAfterAttribute("10675199.02:48:05"));
            Assert.Throws<ArgumentException>(() => new ExpireAfterAttribute("36501.00:00:00"));

            Assert.Equal(TimeSpan.FromDays(ExpireAfterAttribute.MaxDays), new ExpireAfterAttribute(ExpireAfterAttribute.MaxDays).Duration);
            Assert.Equal(TimeSpan.FromDays(36500), new ExpireAfterAttribute("36500.00:00:00").Duration);
        }

        [Fact]
        public void OnStateApplied_SetsTheExpiration_ForFinalStatesOnly()
        {
            var attribute = new ExpireAfterAttribute(days: 7);
            var transaction = new Mock<IWriteOnlyTransaction>();

            var succeeded = new ApplyStateContextMock { NewStateObject = new SucceededState(null, 1, 1), JobExpirationTimeout = TimeSpan.FromDays(1) };
            attribute.OnStateApplied(succeeded.Object, transaction.Object);
            Assert.Equal(TimeSpan.FromDays(7), succeeded.Object.JobExpirationTimeout);

            var processing = new ApplyStateContextMock { NewStateObject = new ProcessingState("srv", "w"), JobExpirationTimeout = TimeSpan.FromDays(1) };
            attribute.OnStateApplied(processing.Object, transaction.Object);
            Assert.Equal(TimeSpan.FromDays(1), processing.Object.JobExpirationTimeout);
        }

        [Fact]
        public void OnStateUnapplied_AlreadySetsTheExpiration_ForFinalStatesOnly()
        {
            var attribute = new ExpireAfterAttribute(days: 7);
            var transaction = new Mock<IWriteOnlyTransaction>();

            var succeeded = new ApplyStateContextMock { NewStateObject = new SucceededState(null, 1, 1), JobExpirationTimeout = TimeSpan.FromDays(1) };
            attribute.OnStateUnapplied(succeeded.Object, transaction.Object);
            Assert.Equal(TimeSpan.FromDays(7), succeeded.Object.JobExpirationTimeout);

            var processing = new ApplyStateContextMock { NewStateObject = new ProcessingState("srv", "w"), JobExpirationTimeout = TimeSpan.FromDays(1) };
            attribute.OnStateUnapplied(processing.Object, transaction.Object);
            Assert.Equal(TimeSpan.FromDays(1), processing.Object.JobExpirationTimeout);
        }

        [Fact]
        public void OnStateUnapplied_LeavesFailedJobsAlone_ByDefault()
        {
            var attribute = new ExpireAfterAttribute(days: 7);
            var failed = new ApplyStateContextMock { NewStateObject = new FailedState(new InvalidOperationException()), JobExpirationTimeout = TimeSpan.FromDays(1) };

            attribute.OnStateUnapplied(failed.Object, new Mock<IWriteOnlyTransaction>().Object);

            Assert.False(attribute.IncludeFailed);
            Assert.Equal(TimeSpan.FromDays(1), failed.Object.JobExpirationTimeout);
            Assert.False(failed.Object.ExpireInNonFinalState);
        }

        [Fact]
        public void OnStateUnapplied_ExpiresFailedJobs_WithIncludeFailed()
        {
            var attribute = new ExpireAfterAttribute(days: 7) { IncludeFailed = true };
            var transaction = new Mock<IWriteOnlyTransaction>();

            var failed = new ApplyStateContextMock { NewStateObject = new FailedState(new InvalidOperationException()), JobExpirationTimeout = TimeSpan.FromDays(1) };
            attribute.OnStateUnapplied(failed.Object, transaction.Object);
            Assert.Equal(TimeSpan.FromDays(7), failed.Object.JobExpirationTimeout);
            Assert.True(failed.Object.ExpireInNonFinalState);

            // A retry is not the end of the job
            var scheduled = new ApplyStateContextMock { NewStateObject = new ScheduledState(TimeSpan.FromMinutes(1)), JobExpirationTimeout = TimeSpan.FromDays(1) };
            attribute.OnStateUnapplied(scheduled.Object, transaction.Object);
            Assert.Equal(TimeSpan.FromDays(1), scheduled.Object.JobExpirationTimeout);
            Assert.False(scheduled.Object.ExpireInNonFinalState);
        }

        [Fact]
        public void StateMachine_ExpiresConsoleLines_WithThePerMethodExpiration()
        {
            // The console filter is global with order -1, so it runs before ExpireAfter (order 50).
            var filterProvider = new Mock<IJobFilterProvider>();
            filterProvider.Setup(x => x.GetFilters(It.IsAny<Job>())).Returns(new[]
            {
                new JobFilter(new JobConsoleStateFilter(), JobFilterScope.Global, null),
                new JobFilter(new ExpireAfterAttribute(days: 7), JobFilterScope.Method, null),
            });

            var innerStateMachine = new Mock<IStateMachine>();
            var storageTransaction = new Mock<JobStorageTransaction>();
            var context = new ApplyStateContextMock { NewStateObject = new SucceededState(null, 1, 1), JobExpirationTimeout = TimeSpan.FromDays(1) };
            context.Transaction = storageTransaction.As<IWriteOnlyTransaction>();
            context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
            context.BackgroundJob.Job = Job.FromExpression(() => Sample());

            var stateMachine = new StateMachine(new JobFilterProviderCollection(filterProvider.Object), innerStateMachine.Object);

            stateMachine.ApplyState(context.Object);

            storageTransaction.Verify(x => x.ExpireList(JobConsole.GetConsoleKey("JobId"), TimeSpan.FromDays(7)));
            innerStateMachine.Verify(x => x.ApplyState(It.Is<ApplyStateContext>(c => c.JobExpirationTimeout == TimeSpan.FromDays(7))));
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample()
        {
        }
    }
}
