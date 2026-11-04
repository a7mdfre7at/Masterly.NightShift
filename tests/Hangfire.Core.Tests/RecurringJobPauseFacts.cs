using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Moq;
using Xunit;

namespace Hangfire.Core.Tests
{
    /// <summary>
    /// Pause, resume and schedule changes: the entity rules and the manager operations built on them.
    /// </summary>
    public class RecurringJobPauseFacts
    {
        private const string RecurringJobId = "recurring-job-id";

        private readonly DateTime _now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IStorageConnection> _connection = new Mock<IStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Mock<IBackgroundJobFactory> _factory = new Mock<IBackgroundJobFactory>();
        private readonly Dictionary<string, string> _hash;
        private readonly List<Dictionary<string, string>> _writes = new List<Dictionary<string, string>>();
        private readonly List<double> _scores = new List<double>();

        public RecurringJobPauseFacts()
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
            _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{RecurringJobId}")).Returns(() => _hash);
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _transaction
                .Setup(x => x.SetRangeInHash($"recurring-job:{RecurringJobId}", It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) => _writes.Add(pairs.ToDictionary(p => p.Key, p => p.Value)));
            _transaction
                .Setup(x => x.AddToSet("recurring-jobs", RecurringJobId, It.IsAny<double>()))
                .Callback<string, string, double>((_, _, score) => _scores.Add(score));
        }

        [Fact]
        public void Entity_ReadsThePausedFlag()
        {
            _hash["Paused"] = "1";

            var entity = new RecurringJobEntity(RecurringJobId, _hash);

            Assert.True(entity.Paused);
        }

        [Fact]
        public void Entity_IsNotPaused_ByDefault()
        {
            var entity = new RecurringJobEntity(RecurringJobId, _hash);

            Assert.False(entity.Paused);
        }

        [Fact]
        public void Entity_ScheduleNext_YieldsNothing_WhilePaused()
        {
            var entity = new RecurringJobEntity(RecurringJobId, _hash);
            entity.Pause();

            var executions = entity.ScheduleNext(new DefaultTimeZoneResolver(), _now.AddDays(-2), _now, TimeSpan.Zero);

            Assert.Empty(executions);
            Assert.Null(entity.NextExecution);
        }

        [Fact]
        public void Entity_Pause_IsPersisted_AndRemovesTheJobFromTheSchedule()
        {
            var entity = new RecurringJobEntity(RecurringJobId, _hash);
            entity.Pause();

            Assert.True(entity.IsChanged(_now, out var changed));
            Assert.Equal("1", changed["Paused"]);
            Assert.Equal(String.Empty, changed["NextExecution"]);
        }

        [Fact]
        public void Entity_Resume_SchedulesFromNow_WithoutCatchingUp()
        {
            _hash["Paused"] = "1";
            _hash["NextExecution"] = String.Empty;
            _hash["Misfire"] = ((int)MisfireHandlingMode.Strict).ToString();
            var entity = new RecurringJobEntity(RecurringJobId, _hash);

            entity.Resume(new DefaultTimeZoneResolver(), _now);

            Assert.False(entity.Paused);
            Assert.Equal(_now.AddHours(21), entity.NextExecution);
            Assert.Equal(_now, entity.ResumedAt);
            Assert.Equal(_now.AddHours(-3), entity.LastExecution);
            Assert.True(entity.IsChanged(_now, out var changed));
            Assert.Equal("0", changed["Paused"]);
            Assert.Equal(JobHelper.SerializeDateTime(_now), changed["ResumedAt"]);

            // The next scheduler pass computes from the resume time, so the 09:00 runs missed while paused aren't replayed
            Assert.Equal(_now, entity.ScheduleFrom);
            var executions = entity.ScheduleNext(new DefaultTimeZoneResolver(), entity.ScheduleFrom.Value, _now.AddMinutes(1), TimeSpan.Zero);
            Assert.Empty(executions);
        }

        [Fact]
        public void Entity_ScheduleFrom_PrefersTheLaterOfLastExecutionAndResumedAt()
        {
            _hash["ResumedAt"] = JobHelper.SerializeDateTime(_now.AddHours(-1));
            var entity = new RecurringJobEntity(RecurringJobId, _hash);
            Assert.Equal(_now.AddHours(-1), entity.ScheduleFrom);

            entity.LastExecution = _now;
            Assert.Equal(_now, entity.ScheduleFrom);
        }

        [Fact]
        public void Manager_Pause_WritesThePausedFlag_AndScoresTheJobOutOfTheSchedule()
        {
            var manager = CreateManager();

            var result = manager.Pause(RecurringJobId);

            Assert.True(result);
            Assert.Equal("1", _writes.Single()["Paused"]);
            Assert.Equal(-1.0D, _scores.Single());
            _transaction.Verify(x => x.Commit());
        }

        [Fact]
        public void Manager_Pause_ReturnsFalse_ForUnknownJobs()
        {
            _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{RecurringJobId}")).Returns((Dictionary<string, string>)null);
            var manager = CreateManager();

            Assert.False(manager.Pause(RecurringJobId));
            _transaction.Verify(x => x.Commit(), Times.Never);
        }

        [Fact]
        public void Manager_Resume_PutsTheJobBackOnTheSchedule()
        {
            _hash["Paused"] = "1";
            _hash["NextExecution"] = String.Empty;
            var manager = CreateManager();

            var result = manager.Resume(RecurringJobId);

            Assert.True(result);
            Assert.Equal("0", _writes.Single()["Paused"]);
            Assert.Equal(JobHelper.ToTimestamp(_now.AddHours(21)), _scores.Single());
        }

        [Fact]
        public void Manager_Resume_DoesNothing_ForRunningJobs()
        {
            var manager = CreateManager();

            Assert.True(manager.Resume(RecurringJobId));
            Assert.Empty(_writes);
        }

        [Fact]
        public void Manager_AddOrUpdate_KeepsAPausedJobPaused()
        {
            _hash["Paused"] = "1";
            _hash["NextExecution"] = String.Empty;
            var manager = CreateManager();

            manager.AddOrUpdate(RecurringJobId, Job.FromExpression(() => Sample()), "0 10 * * *", new RecurringJobOptions());

            var written = _writes.Single();
            Assert.Equal("0 10 * * *", written["Cron"]);
            Assert.False(written.ContainsKey("Paused"));
            Assert.True(!written.ContainsKey("NextExecution") || written["NextExecution"] == String.Empty);
            Assert.Equal(-1.0D, _scores.Single());
        }

        [Fact]
        public void Manager_UpdateSchedule_ChangesCronAndReschedules()
        {
            var manager = CreateManager();

            var result = manager.UpdateSchedule(RecurringJobId, "0 18 * * *");

            Assert.True(result);
            var written = _writes.Single();
            Assert.Equal("0 18 * * *", written["Cron"]);
            Assert.Equal(JobHelper.SerializeDateTime(_now.AddHours(6)), written["NextExecution"]);
            Assert.False(written.ContainsKey("Job"));
        }

        [Fact]
        public void Manager_UpdateSchedule_ChangesTimeZoneQueueAndMisfire()
        {
            var plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus-two", TimeSpan.FromHours(2), "plus two", "plus two");
            var resolver = new Mock<ITimeZoneResolver>();
            resolver.Setup(x => x.GetTimeZoneById("plus-two")).Returns(plusTwo);
            resolver.Setup(x => x.GetTimeZoneById("UTC")).Returns(TimeZoneInfo.Utc);
            var manager = new RecurringJobManager(_storage.Object, _factory.Object, resolver.Object, () => _now);

            manager.UpdateSchedule(RecurringJobId, null, plusTwo, "critical", MisfireHandlingMode.Strict);

            var written = _writes.Single();
            Assert.Equal("plus-two", written["TimeZoneId"]);
            Assert.Equal("critical", written["Queue"]);
            Assert.Equal(((int)MisfireHandlingMode.Strict).ToString(), written["Misfire"]);
            Assert.Equal(JobHelper.SerializeDateTime(_now.AddHours(19)), written["NextExecution"]);
        }

        [Fact]
        public void Manager_UpdateSchedule_ThrowsAnException_ForInvalidCron()
        {
            var manager = CreateManager();

            Assert.Throws<ArgumentException>(() => manager.UpdateSchedule(RecurringJobId, "not a cron"));
            Assert.Empty(_writes);
        }

        [Fact]
        public void Manager_UpdateSchedule_ThrowsAnException_ForInvalidQueue()
        {
            var manager = CreateManager();

            Assert.Throws<ArgumentException>(() => manager.UpdateSchedule(RecurringJobId, null, queue: "Not Valid"));
        }

        [Fact]
        public void Manager_UpdateSchedule_ReturnsFalse_ForUnknownJobs()
        {
            _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{RecurringJobId}")).Returns((Dictionary<string, string>)null);
            var manager = CreateManager();

            Assert.False(manager.UpdateSchedule(RecurringJobId, "0 1 * * *"));
        }

        [Fact]
        public void Manager_UpdateSchedule_KeepsAPausedJobOutOfTheSchedule()
        {
            _hash["Paused"] = "1";
            _hash["NextExecution"] = String.Empty;
            var manager = CreateManager();

            manager.UpdateSchedule(RecurringJobId, "0 18 * * *");

            Assert.Equal(-1.0D, _scores.Single());
        }

        [Fact]
        public void Extensions_ThrowNotSupported_ForManagersWithoutV3()
        {
            var manager = new Mock<IRecurringJobManager>().Object;

            Assert.Throws<NotSupportedException>(() => manager.Pause(RecurringJobId));
            Assert.Throws<NotSupportedException>(() => manager.Resume(RecurringJobId));
            Assert.Throws<NotSupportedException>(() => manager.UpdateSchedule(RecurringJobId, "* * * * *"));
        }

        [Fact]
        public void Extensions_ForwardToV3Managers()
        {
            var v3 = new Mock<IRecurringJobManagerV3>();
            v3.Setup(x => x.Pause(RecurringJobId)).Returns(true);
            IRecurringJobManager manager = v3.Object;

            Assert.True(manager.Pause(RecurringJobId));
            v3.Verify(x => x.Pause(RecurringJobId));
        }

        [Fact]
        public void Dto_ExposesThePausedFlag()
        {
            _hash["Paused"] = "1";
            _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(new HashSet<string> { RecurringJobId });

            var dtos = _connection.Object.GetRecurringJobs();

            Assert.True(dtos.Single().Paused);
        }

        private RecurringJobManager CreateManager()
        {
            return new RecurringJobManager(_storage.Object, _factory.Object, new DefaultTimeZoneResolver(), () => _now);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample()
        {
        }
    }
}
