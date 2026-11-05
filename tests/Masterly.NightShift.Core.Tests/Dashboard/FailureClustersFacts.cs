using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class FailureClustersFacts
    {
        private readonly Mock<IMonitoringApi> _monitor = new Mock<IMonitoringApi>();
        private readonly List<KeyValuePair<string, FailedJobDto>> _failed = new List<KeyValuePair<string, FailedJobDto>>();

        public FailureClustersFacts()
        {
            _monitor.Setup(x => x.FailedJobs(It.IsAny<int>(), It.IsAny<int>()))
                .Returns<int, int>((from, count) => new JobList<FailedJobDto>(_failed.Skip(from).Take(count)));
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Charge(int id) { }
        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Notify(int id) { }

        private void AddFailure(string id, string exceptionType, Job job, DateTime failedAt, string message = "boom", bool inFailedState = true)
        {
            _failed.Add(new KeyValuePair<string, FailedJobDto>(id, new FailedJobDto
            {
                Job = job,
                ExceptionType = exceptionType,
                ExceptionMessage = message,
                FailedAt = failedAt,
                InFailedState = inFailedState,
            }));
        }

        [Fact]
        public void Build_GroupsByExceptionAndMethod_LargestFirst()
        {
            var now = DateTime.UtcNow;
            var charge = Job.FromExpression(() => Charge(1));
            var notify = Job.FromExpression(() => Notify(1));

            AddFailure("5", "SqlException", charge, now, "deadlock");
            AddFailure("4", "SqlException", charge, now.AddMinutes(-1));
            AddFailure("3", "TimeoutException", notify, now.AddMinutes(-2));
            AddFailure("2", "SqlException", charge, now.AddMinutes(-3));
            AddFailure("1", "TimeoutException", notify, now.AddMinutes(-4));
            AddFailure("0", "OtherException", notify, now.AddMinutes(-5));

            var clusters = FailureClusters.Build(_monitor.Object);

            Assert.Equal(2, clusters.Count);
            Assert.Equal("SqlException", clusters[0].ExceptionType);
            Assert.Equal("FailureClustersFacts.Charge", clusters[0].Method);
            Assert.Equal(new[] { "5", "4", "2" }, clusters[0].JobIds);
            Assert.Equal("deadlock", clusters[0].SampleMessage);
            Assert.Equal(now.AddMinutes(-3), clusters[0].FirstFailedAt);
            Assert.Equal(now, clusters[0].LastFailedAt);
            Assert.Equal(2, clusters[1].Count);
        }

        [Fact]
        public void Build_SkipsJobsThatLeftTheFailedState_AndEmptyEntries()
        {
            var job = Job.FromExpression(() => Charge(1));
            AddFailure("1", "X", job, DateTime.UtcNow, inFailedState: false);
            AddFailure("2", "X", job, DateTime.UtcNow);
            AddFailure("3", "X", job, DateTime.UtcNow);
            _failed.Add(new KeyValuePair<string, FailedJobDto>("4", null));

            var clusters = FailureClusters.Build(_monitor.Object);

            Assert.Equal(new[] { "2", "3" }, Assert.Single(clusters).JobIds);
        }

        [Fact]
        public void Build_PagesThroughTheSample_AndRespectsTheLimit()
        {
            var job = Job.FromExpression(() => Charge(1));
            for (var i = 0; i < 250; i++) AddFailure(i.ToString(), "E" + (i % 12), job, DateTime.UtcNow);

            var clusters = FailureClusters.Build(_monitor.Object, sampleSize: 230, maxClusters: 3);

            Assert.Equal(3, clusters.Count);
            Assert.Equal(230, _failed.Take(230).Count());
            Assert.True(clusters.Sum(c => c.Count) <= 230);
            _monitor.Verify(x => x.FailedJobs(0, 100));
            _monitor.Verify(x => x.FailedJobs(200, 30));
            _monitor.Verify(x => x.FailedJobs(It.Is<int>(f => f >= 230), It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public void Build_UsesPlaceholders_ForUnloadableJobs()
        {
            AddFailure("1", null, null, DateTime.UtcNow);
            AddFailure("2", null, null, DateTime.UtcNow);

            var cluster = Assert.Single(FailureClusters.Build(_monitor.Object));

            Assert.Equal("?", cluster.ExceptionType);
            Assert.Equal("?", cluster.Method);
            Assert.Equal("?|?", cluster.Key);
        }
    }
}
