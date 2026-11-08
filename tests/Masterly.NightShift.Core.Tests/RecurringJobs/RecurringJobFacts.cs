using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class RecurringJobFacts : IDisposable
    {
        private const string RecurringJobId = "recurring-job-id";
        private const string HashKey = "recurring-job:" + RecurringJobId;

        private readonly DateTime _now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Mock<IBackgroundJobFactory> _factory = new Mock<IBackgroundJobFactory>();
        private readonly Dictionary<string, string> _hash;
        private readonly List<Dictionary<string, string>> _writes = new List<Dictionary<string, string>>();

        public RecurringJobFacts()
        {
            _hash = new Dictionary<string, string>
            {
                { "Cron", "0 9 * * *" },
                { "TimeZoneId", "UTC" },
                { "Job", InvocationData.SerializeJob(Job.FromExpression(() => Sample())).SerializePayload() },
                { "NextExecution", JobHelper.SerializeDateTime(_now.AddHours(21)) },
                { "LastExecution", JobHelper.SerializeDateTime(_now.AddHours(-3)) },
                { "CreatedAt", JobHelper.SerializeDateTime(_now.AddDays(-10)) },
                { "V", "2" },
            };

            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
            _connection.Setup(x => x.GetAllEntriesFromHash(HashKey)).Returns(() => _hash);
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _transaction
                .Setup(x => x.SetRangeInHash(HashKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) => _writes.Add(pairs.ToDictionary(p => p.Key, p => p.Value)));

            RecurringJob.ManagerFactory = () => new RecurringJobManager(_storage.Object, _factory.Object, new DefaultTimeZoneResolver(), () => _now);
        }

        public void Dispose()
        {
            RecurringJob.ManagerFactory = null;
        }

        [Fact, GlobalLock(Reason = "Access RecurringJob.ManagerFactory member")]
        public void Pause_PausesTheRecurringJob_ThroughTheManager()
        {
            var result = RecurringJob.Pause(RecurringJobId);

            Assert.True(result);
            Assert.Equal("1", _writes.Single()["Paused"]);
            _transaction.Verify(x => x.Commit());
        }

        [Fact, GlobalLock(Reason = "Access RecurringJob.ManagerFactory member")]
        public void Pause_ReturnsFalse_WhenTheRecurringJobDoesNotExist()
        {
            _connection.Setup(x => x.GetAllEntriesFromHash(HashKey)).Returns((Dictionary<string, string>)null);

            var result = RecurringJob.Pause(RecurringJobId);

            Assert.False(result);
            _transaction.Verify(x => x.Commit(), Times.Never);
        }

        [Fact, GlobalLock(Reason = "Access RecurringJob.ManagerFactory member")]
        public void Pause_ThrowsAnException_WhenIdIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => RecurringJob.Pause(null));

            Assert.Equal("recurringJobId", exception.ParamName);
        }

        [Fact, GlobalLock(Reason = "Access RecurringJob.ManagerFactory member")]
        public void Resume_ResumesAPausedRecurringJob_ThroughTheManager()
        {
            _hash["Paused"] = "1";
            _hash["NextExecution"] = String.Empty;

            var result = RecurringJob.Resume(RecurringJobId);

            Assert.True(result);
            Assert.Equal("0", _writes.Single()["Paused"]);
        }

        [Fact, GlobalLock(Reason = "Access RecurringJob.ManagerFactory member")]
        public void Resume_ReturnsFalse_WhenTheRecurringJobDoesNotExist()
        {
            _connection.Setup(x => x.GetAllEntriesFromHash(HashKey)).Returns((Dictionary<string, string>)null);

            var result = RecurringJob.Resume(RecurringJobId);

            Assert.False(result);
            Assert.Empty(_writes);
        }

        [Fact, GlobalLock(Reason = "Access RecurringJob.ManagerFactory member")]
        public void UpdateSchedule_ChangesTheCronExpression_ThroughTheManager()
        {
            var result = RecurringJob.UpdateSchedule(RecurringJobId, "0 18 * * *");

            Assert.True(result);
            var written = _writes.Single();
            Assert.Equal("0 18 * * *", written["Cron"]);
            Assert.Equal(JobHelper.SerializeDateTime(_now.AddHours(6)), written["NextExecution"]);
        }

        [Fact, GlobalLock(Reason = "Access RecurringJob.ManagerFactory member")]
        public void UpdateSchedule_ForwardsTheQueueAndMisfireHandling()
        {
            var result = RecurringJob.UpdateSchedule(RecurringJobId, null, queue: "critical", misfireHandling: MisfireHandlingMode.Strict);

            Assert.True(result);
            var written = _writes.Single();
            Assert.Equal("critical", written["Queue"]);
            Assert.Equal(((int)MisfireHandlingMode.Strict).ToString(), written["Misfire"]);
            Assert.False(written.ContainsKey("Cron"));
        }

        [Fact, GlobalLock(Reason = "Access RecurringJob.ManagerFactory member")]
        public void UpdateSchedule_ThrowsAnException_WhenCronExpressionIsInvalid()
        {
            var exception = Assert.Throws<ArgumentException>(() => RecurringJob.UpdateSchedule(RecurringJobId, "not a cron"));

            Assert.Equal("cronExpression", exception.ParamName);
            Assert.Empty(_writes);
        }

        [Fact, GlobalLock(Reason = "Access RecurringJob.ManagerFactory member")]
        public void UpdateSchedule_ReturnsFalse_WhenTheRecurringJobDoesNotExist()
        {
            _connection.Setup(x => x.GetAllEntriesFromHash(HashKey)).Returns((Dictionary<string, string>)null);

            var result = RecurringJob.UpdateSchedule(RecurringJobId, "0 18 * * *");

            Assert.False(result);
            Assert.Empty(_writes);
        }

        [Fact, GlobalLock(Reason = "Sets JobStorage.Current and the global time zone resolver")]
        public void AddOrUpdate_UsesTheGlobalTimeZoneResolver_WithTheDefaultManager()
        {
            var plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus-two", TimeSpan.FromHours(2), "plus two", "plus two");
            var resolver = new Mock<ITimeZoneResolver>();
            resolver.Setup(x => x.GetTimeZoneById("plus-two")).Returns(plusTwo);
            RecurringJob.ManagerFactory = null;
            JobStorage.Current = _storage.Object;
            GlobalConfiguration.Configuration.UseTimeZoneResolver(resolver.Object);

            try
            {
                RecurringJob.AddOrUpdate(RecurringJobId, () => Sample(), "0 9 * * *", new RecurringJobOptions { TimeZone = plusTwo });

                Assert.Equal("plus-two", _writes.Single()["TimeZoneId"]);
                resolver.Verify(x => x.GetTimeZoneById("plus-two"));
            }
            finally
            {
                GlobalConfiguration.Configuration.UseTimeZoneResolver(null);
            }
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample()
        {
        }
    }
}
