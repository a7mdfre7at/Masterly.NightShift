using System;
using Hangfire.Common;
using Hangfire.States;
using Moq;
using Xunit;

namespace Hangfire.Core.Tests
{
    public class DeadlineAttributeFacts : IDisposable
    {
        private readonly DateTime _now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        public DeadlineAttributeFacts()
        {
            DeadlineAttribute.Clock = () => _now;
        }

        public void Dispose()
        {
            DeadlineAttribute.Clock = () => DateTime.UtcNow;
        }

        [Fact]
        public void Ctor_ParsesTimeSpans_AndSeconds()
        {
            Assert.Equal(TimeSpan.FromMinutes(10), new DeadlineAttribute("00:10:00").Timeout);
            Assert.Equal(TimeSpan.FromSeconds(90), new DeadlineAttribute(90).Timeout);
            Assert.Throws<ArgumentOutOfRangeException>(() => new DeadlineAttribute(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DeadlineAttribute("-00:01:00"));
        }

        [Fact]
        public void OnCreating_SetsTheDeadlineParameter_FromNow()
        {
            var context = new CreateContextMock();
            var attribute = new DeadlineAttribute("00:10:00");

            attribute.OnCreating(context.GetCreatingContext());

            var value = context.Object.Parameters[DeadlineAttribute.DeadlineAtParameter];
            var deadline = value is string serialized ? SerializationHelper.Deserialize<DateTime>(serialized, SerializationOption.User) : (DateTime)value;
            Assert.Equal(_now.AddMinutes(10), deadline);
        }

        [Fact]
        public void OnStateElection_RecordsTheMiss_WhenSucceedingAfterTheDeadline()
        {
            var context = new ElectStateContextMock();
            context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);
            context.ApplyContext.Connection
                .Setup(x => x.GetJobParameter("JobId", DeadlineAttribute.DeadlineAtParameter))
                .Returns(SerializationHelper.Serialize(_now.AddMinutes(-5), SerializationOption.User));

            new DeadlineAttribute(60).OnStateElection(context.Object);

            context.ApplyContext.Connection.Verify(x => x.SetJobParameter("JobId", DeadlineAttribute.DeadlineMissedParameter,
                It.Is<string>(v => v.Contains("00:05:00"))));
        }

        [Fact]
        public void OnStateElection_DoesNothing_WhenOnTime()
        {
            var context = new ElectStateContextMock();
            context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);
            context.ApplyContext.Connection
                .Setup(x => x.GetJobParameter("JobId", DeadlineAttribute.DeadlineAtParameter))
                .Returns(SerializationHelper.Serialize(_now.AddMinutes(5), SerializationOption.User));

            new DeadlineAttribute(60).OnStateElection(context.Object);

            context.ApplyContext.Connection.Verify(x => x.SetJobParameter("JobId", DeadlineAttribute.DeadlineMissedParameter, It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void OnStateElection_IgnoresOtherStates()
        {
            var context = new ElectStateContextMock();
            context.ApplyContext.NewStateObject = new FailedState(new Exception());

            new DeadlineAttribute(60).OnStateElection(context.Object);

            context.ApplyContext.Connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void ReadDeadline_ReturnsNull_WithoutParameter()
        {
            var context = new PerformContextMock();

            Assert.Null(DeadlineAttribute.ReadDeadline(context.Connection.Object, "JobId"));
        }
    }
}
