using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Moq;
using System.Text.Json.Nodes;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class RecurringJobUpdateDispatcherFacts
    {
        private readonly Mock<IRecurringJobManagerV3> _manager = new Mock<IRecurringJobManagerV3>();
        private readonly DashboardRequestStub _request = new DashboardRequestStub("POST", "/recurring/update/my-job");
        private readonly DashboardContextStub _context;

        public RecurringJobUpdateDispatcherFacts()
        {
            _context = new DashboardContextStub(new DashboardOptions(), _request)
            {
                RecurringJobManager = _manager.Object,
                UriMatch = Regex.Match("/recurring/update/my-job", "/recurring/update/(?<JobId>.+)")
            };
        }

        [Fact]
        public async Task Dispatch_Sets401_WhenReadOnly()
        {
            var context = new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => true }, _request);

            await new RecurringJobUpdateDispatcher().Dispatch(context);

            Assert.Equal(401, context.Response.StatusCode);
        }

        [Fact]
        public async Task Dispatch_DecodesAnEncodedSlashInTheRecurringJobId()
        {
            // The page sends encodeURIComponent("reports/daily"); ASP.NET Core leaves "%2F" encoded
            _request.Form["cron"] = new List<string> { "0 4 * * *" };
            _manager.Setup(x => x.UpdateSchedule("reports/daily", "0 4 * * *", null, null, null)).Returns(true);
            var context = new DashboardContextStub(new DashboardOptions(), _request)
            {
                RecurringJobManager = _manager.Object,
                UriMatch = Regex.Match("/recurring/update/reports%2Fdaily", "/recurring/update/(?<JobId>.+)")
            };

            await new RecurringJobUpdateDispatcher().Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            _manager.Verify(x => x.UpdateSchedule("reports/daily", "0 4 * * *", null, null, null), Times.Once);
        }

        [Fact]
        public async Task Dispatch_Sets405_ForNonPostRequests()
        {
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("GET"));

            await new RecurringJobUpdateDispatcher().Dispatch(context);

            Assert.Equal(405, context.Response.StatusCode);
        }

        [Fact]
        public async Task Dispatch_UpdatesTheSchedule_FromPlainEnglish_AndReturnsTheCron()
        {
            _request.Form["cron"] = new List<string> { "every 15 minutes" };
            _request.Form["timezone"] = new List<string> { "UTC" };
            _request.Form["queue"] = new List<string> { "critical" };
            _request.Form["misfire"] = new List<string> { "Strict" };
            _manager.Setup(x => x.UpdateSchedule("my-job", "*/15 * * * *", It.Is<TimeZoneInfo>(z => z.Id == "UTC"), "critical", MisfireHandlingMode.Strict)).Returns(true);

            await new RecurringJobUpdateDispatcher().Dispatch(_context);

            Assert.Equal(200, _context.Response.StatusCode);
            Assert.Equal("*/15 * * * *", JsonNode.Parse(_context.Response.Text)["cron"].GetValue<string>());
            _manager.VerifyAll();
        }

        [Fact]
        public async Task Dispatch_PassesNull_ForEmptyOrMissingFields_SoTheyKeepTheirValue()
        {
            _request.Form["cron"] = new List<string> { "0 9 * * *" };
            _request.Form["timezone"] = new List<string> { "" };
            _manager.Setup(x => x.UpdateSchedule("my-job", "0 9 * * *", null, null, null)).Returns(true);

            await new RecurringJobUpdateDispatcher().Dispatch(_context);

            Assert.Equal(200, _context.Response.StatusCode);
            _manager.VerifyAll();
        }

        [Fact]
        public async Task Dispatch_PutsTheJobBackInTheDefaultQueue_WhenTheQueueIsSentEmpty()
        {
            // Clearing the queue in the editor kept the old queue, so a job could never go back to "default"
            _request.Form["cron"] = new List<string> { "0 9 * * *" };
            _request.Form["queue"] = new List<string> { " " };
            _manager.Setup(x => x.UpdateSchedule("my-job", "0 9 * * *", null, "default", null)).Returns(true);

            await new RecurringJobUpdateDispatcher().Dispatch(_context);

            Assert.Equal(200, _context.Response.StatusCode);
            _manager.VerifyAll();
        }

        [Fact]
        public async Task Dispatch_Sets404_WhenTheJobDoesNotExist()
        {
            _request.Form["cron"] = new List<string> { "0 9 * * *" };
            _manager.Setup(x => x.UpdateSchedule("my-job", "0 9 * * *", null, null, null)).Returns(false);

            await new RecurringJobUpdateDispatcher().Dispatch(_context);

            Assert.Equal(404, _context.Response.StatusCode);
        }

        [Fact]
        public async Task Dispatch_RecordsTheUpdate_WithStatus200_InTheAuditLog()
        {
            var audit = new List<string>();
            var context = AuditedContext(audit);
            _request.Form["cron"] = new List<string> { "0 9 * * *" };
            _manager.Setup(x => x.UpdateSchedule("my-job", "0 9 * * *", null, null, null)).Returns(true);

            await new RecurringJobUpdateDispatcher().Dispatch(context);

            var entry = JsonNode.Parse(Assert.Single(audit));
            Assert.Equal(200, entry["s"].GetValue<int>());
            Assert.Equal("my-job", entry["g"][0].GetValue<string>());
        }

        [Fact]
        public async Task Dispatch_RecordsAnUnknownJob_WithStatus404_NotAsASuccess()
        {
            var audit = new List<string>();
            var context = AuditedContext(audit);
            _request.Form["cron"] = new List<string> { "0 9 * * *" };
            _manager.Setup(x => x.UpdateSchedule("my-job", "0 9 * * *", null, null, null)).Returns(false);

            await new RecurringJobUpdateDispatcher().Dispatch(context);

            Assert.Equal(404, context.Response.StatusCode);
            Assert.Equal(404, JsonNode.Parse(Assert.Single(audit))["s"].GetValue<int>());
        }

        [Fact]
        public async Task Dispatch_Sets422WithMessage_ForUnparsableSchedules()
        {
            _request.Form["cron"] = new List<string> { "whenever you like" };

            await new RecurringJobUpdateDispatcher().Dispatch(_context);

            Assert.Equal(422, _context.Response.StatusCode);
            Assert.Equal("application/json", _context.Response.ContentType);
            Assert.False(String.IsNullOrEmpty(JsonNode.Parse(_context.Response.Text)["error"].GetValue<string>()));
            _manager.Verify(x => x.UpdateSchedule(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeZoneInfo>(), It.IsAny<string>(), It.IsAny<MisfireHandlingMode?>()), Times.Never);
        }

        [Fact]
        public async Task Dispatch_Sets422_ForUnknownMisfireModes()
        {
            _request.Form["misfire"] = new List<string> { "Whatever" };

            await new RecurringJobUpdateDispatcher().Dispatch(_context);

            Assert.Equal(422, _context.Response.StatusCode);
        }

        [Fact]
        public async Task Dispatch_Sets422_WhenTheManagerRejectsTheInput()
        {
            _request.Form["queue"] = new List<string> { "Bad Queue" };
            _manager.Setup(x => x.UpdateSchedule("my-job", null, null, "Bad Queue", null)).Throws(new ArgumentException("The queue name is invalid."));

            await new RecurringJobUpdateDispatcher().Dispatch(_context);

            Assert.Equal(422, _context.Response.StatusCode);
            Assert.Contains("queue name", JsonNode.Parse(_context.Response.Text)["error"].GetValue<string>());
        }

        [Fact, StaticLock]
        public async Task Dispatch_RecordsA422_InTheAuditLog()
        {
            // Failed edits were never audited, although the docs say every posted action is
            var audit = new List<string>();
            _request.Form["cron"] = new List<string> { "whenever you like" };
            var context = AuditedContext(audit);

            await new RecurringJobUpdateDispatcher().Dispatch(context);

            Assert.Equal(422, context.Response.StatusCode);
            var entry = Assert.Single(audit);
            Assert.Contains("\"s\":422", entry);
            Assert.Contains("my-job", entry);
        }

        private DashboardContextStub AuditedContext(List<string> audit)
        {
            var storage = new Mock<JobStorage>();
            var connection = new Mock<JobStorageConnection>();
            var transaction = new Mock<IWriteOnlyTransaction>();
            storage.Setup(x => x.GetConnection()).Returns(connection.Object);
            connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
            transaction.Setup(x => x.InsertToList(AuditLog.ListKey, It.IsAny<string>())).Callback<string, string>((_, value) => audit.Add(value));

            return new DashboardContextStub(new DashboardOptions(), _request, storage.Object)
            {
                RecurringJobManager = _manager.Object,
                UriMatch = Regex.Match("/recurring/update/my-job", "/recurring/update/(?<JobId>.+)")
            };
        }
    }

    public class CronPreviewDispatcherFacts
    {
        [Fact]
        public async Task Dispatch_ReturnsTheResolvedCron_AndNextOccurrences()
        {
            var request = new DashboardRequestStub("GET", "/recurring/preview");
            request.Query["cron"] = "every 6 hours";
            request.Query["timezone"] = "UTC";
            var context = new DashboardContextStub(new DashboardOptions(), request);

            await new CronPreviewDispatcher().Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            var json = JsonNode.Parse(context.Response.Text);
            Assert.Equal("0 */6 * * *", json["cron"].GetValue<string>());
            Assert.Equal("UTC", json["timeZone"].GetValue<string>());
            Assert.Equal(5, (json["next"].AsArray()).Count);
            Assert.Null(json["error"]);
        }

        [Fact]
        public async Task Dispatch_LeavesOutOccurrences_BeforeTheStart_AndAfterTheEnd()
        {
            var start = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(10), DateTimeKind.Utc);
            var request = new DashboardRequestStub("GET", "/recurring/preview");
            request.Query["cron"] = "0 * * * *";
            request.Query["start"] = new DateTimeOffset(start).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            request.Query["end"] = new DateTimeOffset(start.AddHours(2)).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var context = new DashboardContextStub(new DashboardOptions(), request);

            await new CronPreviewDispatcher().Dispatch(context);

            var next = JsonNode.Parse(context.Response.Text)["next"].AsArray().Select(x => x.GetValue<long>()).ToArray();
            Assert.Equal(new[] { start, start.AddHours(1), start.AddHours(2) }.Select(JobHelper.ToTimestamp).ToArray(), next);
        }

        [Fact]
        public async Task Dispatch_IgnoresAStartInThePast_AndAnUnreadableWindow()
        {
            var request = new DashboardRequestStub("GET", "/recurring/preview");
            request.Query["cron"] = "0 * * * *";
            request.Query["start"] = new DateTimeOffset(DateTime.UtcNow.AddDays(-3)).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            request.Query["end"] = "soon";
            var context = new DashboardContextStub(new DashboardOptions(), request);

            await new CronPreviewDispatcher().Dispatch(context);

            var next = JsonNode.Parse(context.Response.Text)["next"].AsArray();
            Assert.Equal(5, next.Count);
            Assert.True(JobHelper.FromTimestamp(next[0].GetValue<long>()) > DateTime.UtcNow.AddMinutes(-1));
        }

        [Fact]
        public async Task Dispatch_ReportsErrors_InTheBody()
        {
            var request = new DashboardRequestStub("GET", "/recurring/preview");
            request.Query["cron"] = "not a schedule at all";
            var context = new DashboardContextStub(new DashboardOptions(), request);

            await new CronPreviewDispatcher().Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            Assert.False(String.IsNullOrEmpty(JsonNode.Parse(context.Response.Text)["error"].GetValue<string>()));
        }

        [Fact]
        public async Task Dispatch_RequiresASchedule()
        {
            var context = new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub("GET", "/recurring/preview"));

            await new CronPreviewDispatcher().Dispatch(context);

            Assert.NotNull(JsonNode.Parse(context.Response.Text)["error"]);
        }
    }
}
