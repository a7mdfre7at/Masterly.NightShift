using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class ScheduleForecastFacts
    {
        private readonly DateTime _from = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        private static RecurringJobDto Dto(string id, string cron, bool paused = false, string timeZone = null)
        {
            return new RecurringJobDto { Id = id, Cron = cron, Paused = paused, TimeZoneId = timeZone, Job = Job.FromExpression(() => Sample()) };
        }

        [Fact]
        public void Build_ValidatesArguments()
        {
            Assert.Throws<ArgumentNullException>(() => ScheduleForecast.Build((IEnumerable<RecurringJobDto>)null, _from, TimeSpan.FromHours(1), _ => null));
            Assert.Throws<ArgumentNullException>(() => ScheduleForecast.Build(new RecurringJobDto[0], _from, TimeSpan.FromHours(1), null));
            Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleForecast.Build(new RecurringJobDto[0], _from, TimeSpan.Zero, _ => null));
        }

        [Fact]
        public void Build_ProjectsOccurrences_InsideTheWindow()
        {
            var forecast = ScheduleForecast.Build(new[] { Dto("hourly", "0 * * * *") }, _from, TimeSpan.FromHours(24), _ => null);

            var row = Assert.Single(forecast.Rows);
            Assert.Equal(23, row.Occurrences.Count);
            Assert.Equal(_from.AddHours(1), row.Occurrences[0]);
            Assert.All(row.Occurrences, x => Assert.InRange(x, _from, _from.AddHours(24)));
            Assert.Equal(23, forecast.TotalRuns);
            Assert.Equal(ScheduleForecast.DefaultDuration, row.ExpectedDuration);
            Assert.False(row.DurationKnown);
        }

        [Fact]
        public void Build_LeavesOutOccurrences_BeforeTheStart_ButKeepsTheOneAtTheStart()
        {
            var dto = Dto("hourly", "0 * * * *");
            dto.StartAt = _from.AddHours(5);

            var forecast = ScheduleForecast.Build(new[] { dto }, _from, TimeSpan.FromHours(10), _ => null);

            var row = Assert.Single(forecast.Rows);
            Assert.Equal(_from.AddHours(5), row.Occurrences[0]);
            Assert.Equal(5, row.Occurrences.Count);
        }

        [Fact]
        public void Build_LeavesOutOccurrences_AfterTheEnd_ButKeepsTheOneAtTheEnd()
        {
            var dto = Dto("hourly", "0 * * * *");
            dto.EndAt = _from.AddHours(3);

            var forecast = ScheduleForecast.Build(new[] { dto }, _from, TimeSpan.FromHours(10), _ => null);

            var row = Assert.Single(forecast.Rows);
            Assert.Equal(new[] { _from.AddHours(1), _from.AddHours(2), _from.AddHours(3) }, row.Occurrences);
        }

        [Fact]
        public void Build_ProjectsNothing_ForAJobThatHasEnded_OrStartsAfterTheWindow()
        {
            var ended = Dto("ended", "0 * * * *");
            ended.EndAt = _from.AddDays(-1);
            var later = Dto("later", "0 * * * *");
            later.StartAt = _from.AddDays(2);

            var forecast = ScheduleForecast.Build(new[] { ended, later }, _from, TimeSpan.FromHours(24), _ => null);

            Assert.Equal(2, forecast.Rows.Count);
            Assert.All(forecast.Rows, x => Assert.Empty(x.Occurrences));
            Assert.Equal(0, forecast.TotalRuns);
        }

        [Fact]
        public void Build_UsesTheStartAndEnd_StoredWithTheJob()
        {
            var connection = new Mock<IStorageConnection>();
            SetupRecurringJob(connection, "windowed", "0 * * * *");
            var hash = RecurringJobHash("0 * * * *", null);
            hash["StartAt"] = JobHelper.SerializeDateTime(_from.AddHours(2));
            hash["EndAt"] = JobHelper.SerializeDateTime(_from.AddHours(4));
            connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:windowed")).Returns(hash);

            var forecast = ScheduleForecast.Build(connection.Object, _from, TimeSpan.FromHours(24));

            var row = Assert.Single(forecast.Rows);
            Assert.Equal(new[] { _from.AddHours(2), _from.AddHours(3), _from.AddHours(4) }, row.Occurrences);
        }

        [Fact]
        public void Build_UsesTheExpectedDuration_FromInsights()
        {
            var key = JobInsightsAttribute.GetKey(Job.FromExpression(() => Sample()));

            var forecast = ScheduleForecast.Build(new[] { Dto("job", "0 */6 * * *") }, _from, TimeSpan.FromHours(24), k => k == key ? TimeSpan.FromMinutes(10) : (TimeSpan?)null);

            var row = Assert.Single(forecast.Rows);
            Assert.True(row.DurationKnown);
            Assert.Equal(TimeSpan.FromMinutes(10), row.ExpectedDuration);
            Assert.Equal(3, row.Occurrences.Count);
            Assert.Equal(TimeSpan.FromMinutes(30), forecast.TotalBusyTime);
        }

        [Fact]
        public void Build_SkipsPausedAndRemovedJobs_ButListsPausedOnes()
        {
            var removed = Dto("removed", "* * * * *");
            removed.Removed = true;

            var forecast = ScheduleForecast.Build(new[] { Dto("paused", "* * * * *", paused: true), removed }, _from, TimeSpan.FromHours(1), _ => null);

            var row = Assert.Single(forecast.Rows);
            Assert.Equal("paused", row.Id);
            Assert.Empty(row.Occurrences);
        }

        [Fact]
        public void Build_ReportsBadCronExpressions_InsteadOfThrowing()
        {
            var forecast = ScheduleForecast.Build(new[] { Dto("broken", "not a cron") }, _from, TimeSpan.FromHours(1), _ => null);

            var row = Assert.Single(forecast.Rows);
            Assert.Empty(row.Occurrences);
            Assert.NotNull(row.Error);
        }

        [Fact]
        public void Build_FindsHotSpots_WhereJobsStartTogether()
        {
            var forecast = ScheduleForecast.Build(new[] { Dto("a", "0 * * * *"), Dto("b", "2 * * * *"), Dto("c", "30 * * * *") }, _from, TimeSpan.FromHours(3), _ => null);

            Assert.Equal(2, forecast.HotSpots.Count);
            Assert.All(forecast.HotSpots, h => Assert.Equal(new[] { "a", "b" }, h.JobIds));
            Assert.Equal(_from.AddHours(1), forecast.HotSpots[0].Slot);
        }

        [Fact]
        public void Build_OrdersRowsByRunCount_AndFindsTheBusiestHour()
        {
            var forecast = ScheduleForecast.Build(new[] { Dto("rare", "0 14 * * *"), Dto("often", "*/15 * * * *") }, _from, TimeSpan.FromHours(3), _ => null);

            Assert.Equal(new[] { "often", "rare" }, forecast.Rows.Select(r => r.Id));
            Assert.Equal(_from.AddHours(2), forecast.BusiestHour);
        }

        [Fact]
        public void Build_MarksAJobThatRunsMoreOftenThanTheCap_AsTruncated_AndKeepsItOutOfHotSpots()
        {
            // Every minute over a week: 10,080 runs. The row said "2000x" and its band stopped after about 33 hours,
            // and its first runs collided with every other job of those hours.
            var forecast = ScheduleForecast.Build(new[] { Dto("every-minute", "* * * * *"), Dto("daily", "0 13 * * *") }, _from, TimeSpan.FromDays(7), _ => null);

            var row = forecast.Rows.Single(r => r.Id == "every-minute");
            Assert.True(row.Truncated);
            Assert.True(forecast.Truncated);
            Assert.Equal(ScheduleForecast.MaxOccurrencesPerJob, row.Occurrences.Count);
            Assert.Equal(_from.AddDays(7), row.RunsUntil);
            Assert.False(forecast.Rows.Single(r => r.Id == "daily").Truncated);
            Assert.Empty(forecast.HotSpots);
        }

        [Fact]
        public void Build_TruncatedRuns_StopAtTheJobsEnd()
        {
            var dto = Dto("every-minute", "* * * * *");
            dto.EndAt = _from.AddDays(3);

            var forecast = ScheduleForecast.Build(new[] { dto }, _from, TimeSpan.FromDays(7), _ => null);

            Assert.Equal(_from.AddDays(3), Assert.Single(forecast.Rows).RunsUntil);
        }

        [Fact]
        public void Build_DoesNotMarkAJobAsTruncated_WhenItsRunsEndAtTheCap()
        {
            // 2000 runs, the last one inside the window, nothing after it
            var forecast = ScheduleForecast.Build(new[] { Dto("every-minute", "* * * * *") }, _from, TimeSpan.FromMinutes(2001), _ => null);

            var row = Assert.Single(forecast.Rows);
            Assert.Equal(ScheduleForecast.MaxOccurrencesPerJob, row.Occurrences.Count);
            Assert.False(row.Truncated);
            Assert.Null(row.RunsUntil);
        }

        [Fact]
        public void Build_HonoursTheTimeZone()
        {
            var zone = TimeZoneInfo.Utc;
            var forecast = ScheduleForecast.Build(new[] { Dto("daily", "0 13 * * *", timeZone: zone.Id) }, _from, TimeSpan.FromHours(24), _ => null);

            Assert.Equal(_from.AddHours(1), Assert.Single(Assert.Single(forecast.Rows).Occurrences));
        }

        [Fact]
        public void BuildFromConnection_ThrowsWhenConnectionIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => ScheduleForecast.Build((IStorageConnection)null, _from, TimeSpan.FromHours(1)));
        }

        [Fact]
        public void BuildFromConnection_ReadsRecurringJobs_AndTheMedianDurationFromInsights()
        {
            var connection = new Mock<JobStorageConnection>();
            SetupRecurringJob(connection, "job", "0 */6 * * *");
            SetupDurations(connection, "100", "300", "200");

            var forecast = ScheduleForecast.Build(connection.Object, _from, TimeSpan.FromHours(24));

            var row = Assert.Single(forecast.Rows);
            Assert.Equal("job", row.Id);
            Assert.Equal(3, row.Occurrences.Count);
            Assert.True(row.DurationKnown);
            Assert.Equal(TimeSpan.FromMilliseconds(200), row.ExpectedDuration);
        }

        [Fact]
        public void BuildFromConnection_UsesTheDefaultDuration_WithFewerThanThreeRecordedRuns()
        {
            var connection = new Mock<JobStorageConnection>();
            SetupRecurringJob(connection, "job", "0 */6 * * *");
            SetupDurations(connection, "100", "300");

            var forecast = ScheduleForecast.Build(connection.Object, _from, TimeSpan.FromHours(24));

            var row = Assert.Single(forecast.Rows);
            Assert.False(row.DurationKnown);
            Assert.Equal(ScheduleForecast.DefaultDuration, row.ExpectedDuration);
        }

        [Fact]
        public void BuildFromConnection_UsesTheDefaultDuration_WithoutAnyInsights()
        {
            var connection = new Mock<JobStorageConnection>();
            SetupRecurringJob(connection, "job", "0 */6 * * *");

            var forecast = ScheduleForecast.Build(connection.Object, _from, TimeSpan.FromHours(24));

            Assert.False(Assert.Single(forecast.Rows).DurationKnown);
        }

        [Fact]
        public void BuildFromConnection_WithALegacyConnection_UsesTheDefaultDuration()
        {
            var connection = new Mock<IStorageConnection>();
            SetupRecurringJob(connection, "job", "0 */6 * * *");

            var forecast = ScheduleForecast.Build(connection.Object, _from, TimeSpan.FromHours(24));

            var row = Assert.Single(forecast.Rows);
            Assert.Equal(3, row.Occurrences.Count);
            Assert.False(row.DurationKnown);
        }

        [Fact]
        public void BuildFromConnection_ResolvesTimeZones_WithTheGivenResolver()
        {
            var connection = new Mock<IStorageConnection>();
            SetupRecurringJob(connection, "daily", "0 13 * * *", timeZoneId: "Custom/Plus2");
            var zone = TimeZoneInfo.CreateCustomTimeZone("Custom/Plus2", TimeSpan.FromHours(2), "Custom/Plus2", "Custom/Plus2");
            var resolver = new Mock<ITimeZoneResolver>();
            resolver.Setup(x => x.GetTimeZoneById("Custom/Plus2")).Returns(zone);

            var forecast = ScheduleForecast.Build(connection.Object, _from, TimeSpan.FromHours(24), resolver.Object);

            Assert.Equal(_from.AddHours(23), Assert.Single(Assert.Single(forecast.Rows).Occurrences));
            resolver.Verify(x => x.GetTimeZoneById("Custom/Plus2"), Times.Once);
        }

        [Fact]
        public void BuildFromConnection_WithoutRecurringJobs_IsEmpty()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(new HashSet<string>());

            var forecast = ScheduleForecast.Build(connection.Object, _from, TimeSpan.FromHours(1));

            Assert.Empty(forecast.Rows);
            Assert.Empty(forecast.HotSpots);
            Assert.Equal(0, forecast.TotalRuns);
            Assert.Null(forecast.BusiestHour);
            Assert.Equal(_from.AddHours(1), forecast.To);
        }

        private static void SetupRecurringJob(Mock<IStorageConnection> connection, string id, string cron, string timeZoneId = null)
        {
            connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(new HashSet<string> { id });
            connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:" + id)).Returns(RecurringJobHash(cron, timeZoneId));
        }

        private static void SetupRecurringJob(Mock<JobStorageConnection> connection, string id, string cron)
        {
            connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(new HashSet<string> { id });
            connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:" + id)).Returns(RecurringJobHash(cron, null));
        }

        private static Dictionary<string, string> RecurringJobHash(string cron, string timeZoneId)
        {
            var hash = new Dictionary<string, string>
            {
                { "Cron", cron },
                { "Job", InvocationData.SerializeJob(Job.FromExpression(() => Sample())).SerializePayload() },
            };
            if (timeZoneId != null) hash["TimeZoneId"] = timeZoneId;
            return hash;
        }

        private static void SetupDurations(Mock<JobStorageConnection> connection, params string[] newestFirst)
        {
            var key = JobInsightsAttribute.GetKey(Job.FromExpression(() => Sample()));
            connection.Setup(x => x.GetAllEntriesFromHash("insights:" + key)).Returns(new Dictionary<string, string> { { "LastDurationMs", newestFirst[0] } });
            connection.Setup(x => x.GetAllItemsFromList("insights:" + key + ":durations")).Returns(newestFirst.ToList());
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample() { }
    }
}
