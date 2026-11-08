using System;
using System.Linq;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class ActivityHeatmapFacts
    {
        private readonly DateTime _now = new DateTime(2026, 11, 5, 14, 20, 0, DateTimeKind.Utc);
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();

        [Fact]
        public void Read_ReturnsSevenDaysOfHours_OldestFirst_WithFutureHoursEmpty()
        {
            _connection.Setup(x => x.GetCounter("stats:succeeded:2026-11-05-14")).Returns(7);
            _connection.Setup(x => x.GetCounter("stats:failed:2026-11-05-14")).Returns(9);
            _connection.Setup(x => x.GetCounter("stats:succeeded:2026-10-30-00")).Returns(3);

            var rows = ActivityHeatmap.Read(_connection.Object, _now);

            Assert.Equal(7, rows.Count);
            Assert.All(rows, r => Assert.Equal(24, r.Count));
            Assert.Equal(new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc), rows[0][0].HourUtc);
            Assert.Equal(3, rows[0][0].Succeeded);
            var current = rows[6][14];
            Assert.Equal(7, current.Succeeded);
            Assert.Equal(9, current.Failed);
            Assert.Equal(16, current.Total);
            Assert.Equal(0, rows[6][15].Total);
            Assert.Equal(16, ActivityHeatmap.Max(rows));
            Assert.False(ActivityHeatmap.IsEmpty(rows));
            _connection.Verify(x => x.GetCounter("stats:succeeded:2026-11-05-15"), Times.Never);
        }

        [Fact]
        public void Read_FromAStorage_ReusesTheGrid_ForAMinute_WithinTheSameHour()
        {
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
            _connection.Setup(x => x.GetCounter("stats:succeeded:2026-11-05-14")).Returns(7);

            var first = ActivityHeatmap.Read(storage.Object, _now);
            var second = ActivityHeatmap.Read(storage.Object, _now.AddSeconds(30));
            var nextHour = ActivityHeatmap.Read(storage.Object, new DateTime(2026, 11, 5, 15, 0, 5, DateTimeKind.Utc));

            Assert.Same(first, second);
            Assert.NotSame(second, nextHour);
            Assert.Equal(7, first[6][14].Succeeded);
            storage.Verify(x => x.GetReadOnlyConnection(), Times.Exactly(2));
        }

        [Fact]
        public void Read_FromAStorage_ReadsAgain_AfterTheCacheDuration()
        {
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);

            ActivityHeatmap.Read(storage.Object, _now);
            ActivityHeatmap.Read(storage.Object, _now.Add(ActivityHeatmap.CacheDuration));

            storage.Verify(x => x.GetReadOnlyConnection(), Times.Exactly(2));
            _connection.Verify(x => x.Dispose(), Times.Exactly(2));
        }

        [Fact]
        public void Read_FromAStorageWithoutTheExtendedApi_IsEmpty_AndReadsNoCounter()
        {
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.GetCounter(It.IsAny<string>())).Throws(new NotSupportedException());

            var rows = ActivityHeatmap.Read(storage.Object, _now);

            Assert.True(ActivityHeatmap.IsEmpty(rows));
            _connection.Verify(x => x.GetCounter(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Read_IsEmpty_WhenTheConnectionCanNotReadCounters()
        {
            _connection.Setup(x => x.GetCounter(It.IsAny<string>())).Throws(new NotSupportedException());

            var rows = ActivityHeatmap.Read(_connection.Object, _now);

            Assert.True(ActivityHeatmap.IsEmpty(rows));
            Assert.Equal(7 * 24, rows.Sum(r => r.Count));
        }

        [Fact]
        public void Read_IsEmpty_WithoutAStorageConnection()
        {
            var rows = ActivityHeatmap.Read(new Mock<IStorageConnection>().Object, _now);

            Assert.True(ActivityHeatmap.IsEmpty(rows));
            Assert.Equal(1, ActivityHeatmap.Max(rows));
            Assert.Equal(7 * 24, rows.Sum(r => r.Count));
        }
    }
}
