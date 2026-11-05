using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
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
        public async Task Dispatch_PassesNull_ForEmptyFields_SoTheyKeepTheirValue()
        {
            _request.Form["cron"] = new List<string> { "0 9 * * *" };
            _request.Form["queue"] = new List<string> { "" };
            _manager.Setup(x => x.UpdateSchedule("my-job", "0 9 * * *", null, null, null)).Returns(true);

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
