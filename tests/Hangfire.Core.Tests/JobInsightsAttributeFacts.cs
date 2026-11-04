using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Hangfire.Common;
using Hangfire.Storage;
using Moq;
using Xunit;

namespace Hangfire.Core.Tests
{
    public class JobInsightsAttributeFacts
    {
        private readonly PerformContextMock _context = new PerformContextMock();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly string _key;

        public JobInsightsAttributeFacts()
        {
            _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _context.BackgroundJob.Job = Job.FromExpression(() => Sample());
            _key = JobInsightsAttribute.GetKey(_context.BackgroundJob.Job);
        }

        [Fact]
        public void GetKey_IsTypeAndMethod()
        {
            Assert.Equal("JobInsightsAttributeFacts.Sample", _key);
        }

        [Fact, StaticLock]
        public void OnPerformed_RecordsASuccessfulRun()
        {
            var attribute = new JobInsightsAttribute();
            var performing = _context.GetPerformingContext();
            attribute.OnPerforming(performing);

            attribute.OnPerformed(new Hangfire.Server.PerformedContext(performing, "result", false, null));

            _transaction.Verify(x => x.AddToSet(JobInsightsAttribute.IndexKey, _key));
            _transaction.Verify(x => x.IncrementCounter($"insights:{_key}:ok"));
            _transaction.Verify(x => x.IncrementCounter($"insights:{_key}:failed"), Times.Never);
            _transaction.Verify(x => x.InsertToList($"insights:{_key}:durations", It.IsAny<string>()));
            _transaction.Verify(x => x.TrimList($"insights:{_key}:durations", 0, JobInsightsAttribute.DurationSampleSize - 1));
            _transaction.Verify(x => x.SetRangeInHash($"insights:{_key}", It.Is<IEnumerable<KeyValuePair<string, string>>>(pairs =>
                pairs.Any(p => p.Key == "LastJobId" && p.Value == "JobId")
                && pairs.Any(p => p.Key == "LastFailed" && p.Value == "0")
                && pairs.Any(p => p.Key == "Invoker" && (p.Value == "Generated" || p.Value == "Compiled" || p.Value == "Reflection")))));
            _transaction.Verify(x => x.Commit());
        }

        [Fact, StaticLock]
        public void OnPerformed_RecordsAFailedRun()
        {
            var attribute = new JobInsightsAttribute();
            var performing = _context.GetPerformingContext();
            attribute.OnPerforming(performing);

            attribute.OnPerformed(new Hangfire.Server.PerformedContext(performing, null, false, new InvalidOperationException()));

            _transaction.Verify(x => x.IncrementCounter($"insights:{_key}:failed"));
            _transaction.Verify(x => x.IncrementCounter($"insights:{_key}:ok"), Times.Never);
            _transaction.Verify(x => x.SetRangeInHash($"insights:{_key}", It.Is<IEnumerable<KeyValuePair<string, string>>>(pairs =>
                pairs.Any(p => p.Key == "LastFailed" && p.Value == "1"))));
        }

        [Fact, StaticLock]
        public void OnPerformed_RecordsNothing_ForCanceledJobs()
        {
            var attribute = new JobInsightsAttribute();

            attribute.OnPerformed(_context.GetPerformedContext(canceled: true));

            _transaction.Verify(x => x.Commit(), Times.Never);
        }

        [Fact]
        public void ReadAll_ReturnsEmpty_ForConnectionsWithoutListSupport()
        {
            Assert.Empty(JobInsightsAttribute.ReadAll(_context.Connection.Object));
        }

        [Fact]
        public void ReadAll_ComputesStatistics_FromStoredData()
        {
            var connection = new Mock<JobStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet(JobInsightsAttribute.IndexKey)).Returns(new HashSet<string> { "Jobs.Run" });
            connection.Setup(x => x.GetAllEntriesFromHash("insights:Jobs.Run")).Returns(new Dictionary<string, string>
            {
                { "LastRunAt", JobHelper.SerializeDateTime(new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc)) },
                { "LastDurationMs", "300" },
                { "LastJobId", "42" },
                { "LastServerId", "server-a" },
                { "LastFailed", "1" },
                { "Invoker", "Generated" },
            });
            connection.Setup(x => x.GetAllItemsFromList("insights:Jobs.Run:durations")).Returns(new List<string> { "300", "100", "200" });
            connection.Setup(x => x.GetCounter("insights:Jobs.Run:ok")).Returns(7);
            connection.Setup(x => x.GetCounter("insights:Jobs.Run:failed")).Returns(3);

            var insight = Assert.Single(JobInsightsAttribute.ReadAll(connection.Object));

            Assert.Equal("Jobs.Run", insight.Key);
            Assert.Equal(7, insight.Succeeded);
            Assert.Equal(3, insight.Failed);
            Assert.Equal(10, insight.Total);
            Assert.Equal(0.3, insight.FailureRate, 3);
            Assert.Equal(new long[] { 200, 100, 300 }, insight.Durations);
            Assert.Equal(200, insight.AverageMs);
            Assert.Equal(200, insight.MedianMs);
            Assert.Equal(300, insight.P95Ms);
            Assert.Equal(300, insight.MaxMs);
            Assert.Equal(300, insight.LastDurationMs);
            Assert.Equal("42", insight.LastJobId);
            Assert.Equal("server-a", insight.LastServerId);
            Assert.True(insight.LastFailed);
            Assert.Equal("Generated", insight.Invoker);
            Assert.False(insight.LastRunWasSlow);
        }

        [Fact]
        public void Read_FlagsSlowRuns_WithEnoughSamples()
        {
            var connection = new Mock<JobStorageConnection>();
            var durations = Enumerable.Repeat("100", 12).ToList();
            durations.Insert(0, "5000"); // newest first
            connection.Setup(x => x.GetAllEntriesFromHash("insights:Jobs.Slow")).Returns(new Dictionary<string, string> { { "LastDurationMs", "5000" } });
            connection.Setup(x => x.GetAllItemsFromList("insights:Jobs.Slow:durations")).Returns(durations);

            var insight = JobInsightsAttribute.Read(connection.Object, "Jobs.Slow");

            Assert.True(insight.LastRunWasSlow);
        }

        [Fact]
        public void Read_ReturnsNull_WhenNothingWasRecorded()
        {
            var connection = new Mock<JobStorageConnection>();
            connection.Setup(x => x.GetAllEntriesFromHash("insights:Nothing")).Returns((Dictionary<string, string>)null);

            Assert.Null(JobInsightsAttribute.Read(connection.Object, "Nothing"));
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample()
        {
        }
    }
}
