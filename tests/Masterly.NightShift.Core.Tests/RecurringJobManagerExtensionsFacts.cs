using System;
using Moq;
using Xunit;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests
{
    public class RecurringJobManagerExtensionsFacts
    {
        private const string RecurringJobId = "recurring-job-id";

        private readonly Mock<IRecurringJobManagerV3> _managerV3 = new Mock<IRecurringJobManagerV3>();
        private readonly Mock<IRecurringJobManager> _legacyManager = new Mock<IRecurringJobManager>();

        private IRecurringJobManager Manager => _managerV3.Object;

        [Fact]
        public void Pause_ThrowsAnException_WhenManagerIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => RecurringJobManagerExtensions.Pause(null, RecurringJobId));

            Assert.Equal("manager", exception.ParamName);
        }

        [Fact]
        public void Pause_ThrowsNotSupportedException_WhenManagerIsNotV3()
        {
            Assert.Throws<NotSupportedException>(() => _legacyManager.Object.Pause(RecurringJobId));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Pause_DelegatesToTheV3Manager_AndReturnsItsResult(bool expected)
        {
            _managerV3.Setup(x => x.Pause(RecurringJobId)).Returns(expected);

            var result = Manager.Pause(RecurringJobId);

            Assert.Equal(expected, result);
            _managerV3.Verify(x => x.Pause(RecurringJobId), Times.Once);
        }

        [Fact]
        public void Resume_ThrowsAnException_WhenManagerIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => RecurringJobManagerExtensions.Resume(null, RecurringJobId));

            Assert.Equal("manager", exception.ParamName);
        }

        [Fact]
        public void Resume_ThrowsNotSupportedException_WhenManagerIsNotV3()
        {
            Assert.Throws<NotSupportedException>(() => _legacyManager.Object.Resume(RecurringJobId));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Resume_DelegatesToTheV3Manager_AndReturnsItsResult(bool expected)
        {
            _managerV3.Setup(x => x.Resume(RecurringJobId)).Returns(expected);

            var result = Manager.Resume(RecurringJobId);

            Assert.Equal(expected, result);
            _managerV3.Verify(x => x.Resume(RecurringJobId), Times.Once);
        }

        [Fact]
        public void UpdateSchedule_ThrowsAnException_WhenManagerIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => RecurringJobManagerExtensions.UpdateSchedule(null, RecurringJobId, "0 9 * * *"));

            Assert.Equal("manager", exception.ParamName);
        }

        [Fact]
        public void UpdateSchedule_ThrowsNotSupportedException_WhenManagerIsNotV3()
        {
            Assert.Throws<NotSupportedException>(() => _legacyManager.Object.UpdateSchedule(RecurringJobId, "0 9 * * *"));
        }

        [Fact]
        public void UpdateSchedule_DelegatesEveryArgument_ToTheV3Manager()
        {
            _managerV3
                .Setup(x => x.UpdateSchedule(RecurringJobId, "0 9 * * *", TimeZoneInfo.Utc, "critical", MisfireHandlingMode.Ignorable))
                .Returns(true);

            var result = Manager.UpdateSchedule(RecurringJobId, "0 9 * * *", TimeZoneInfo.Utc, "critical", MisfireHandlingMode.Ignorable);

            Assert.True(result);
            _managerV3.Verify(
                x => x.UpdateSchedule(RecurringJobId, "0 9 * * *", TimeZoneInfo.Utc, "critical", MisfireHandlingMode.Ignorable),
                Times.Once);
        }

        [Fact]
        public void UpdateSchedule_PassesNulls_ForTheOmittedArguments()
        {
            Manager.UpdateSchedule(RecurringJobId, null);

            _managerV3.Verify(x => x.UpdateSchedule(RecurringJobId, null, null, null, null), Times.Once);
        }
    }
}
