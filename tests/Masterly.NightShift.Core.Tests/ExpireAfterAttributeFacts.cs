using System;
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
        public void OnStateUnapplied_LeavesTheExpirationAlone()
        {
            var attribute = new ExpireAfterAttribute(days: 7);
            var transaction = new Mock<IWriteOnlyTransaction>();
            var context = new ApplyStateContextMock { NewStateObject = new SucceededState(null, 1, 1), JobExpirationTimeout = TimeSpan.FromDays(1) };

            attribute.OnStateUnapplied(context.Object, transaction.Object);

            Assert.Equal(TimeSpan.FromDays(1), context.Object.JobExpirationTimeout);
        }
    }
}
