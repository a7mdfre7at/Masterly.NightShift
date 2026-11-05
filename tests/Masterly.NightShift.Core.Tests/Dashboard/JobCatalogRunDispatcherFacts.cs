using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using System.Text.Json.Nodes;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class JobCatalogRunDispatcherFacts : IDisposable
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IBackgroundJobClient> _client = new Mock<IBackgroundJobClient>();
        private readonly Mock<IRecurringJobManager> _manager = new Mock<IRecurringJobManager>();
        private readonly string _entryId;

        public JobCatalogRunDispatcherFacts()
        {
            JobCatalog.Clear();
            JobCatalog.Add<RunnerJobs>();
            _entryId = JobCatalog.GetEntries().Single(e => e.Type == typeof(RunnerJobs) && e.Method.Name == "Send").Id;
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("123");
        }

        public void Dispose()
        {
            JobCatalog.Clear();
        }

        private DashboardContextStub Post(params (string, string)[] form)
        {
            return Context("POST", false, form);
        }

        private DashboardContextStub Context(string method = "POST", bool readOnly = false, (string, string)[] form = null)
        {
            var request = new DashboardRequestStub(method);
            foreach (var (key, value) in form ?? new (string, string)[0]) request.Form[key] = new List<string> { value };

            return new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => readOnly }, request, _storage.Object)
            {
                BackgroundJobClient = _client.Object,
                RecurringJobManager = _manager.Object,
            };
        }

        [Fact]
        public async Task Dispatch_Sets401_WhenReadOnly()
        {
            var context = Context(readOnly: true);

            await new JobCatalogRunDispatcher().Dispatch(context);

            Assert.Equal(401, context.Response.StatusCode);
        }

        [Fact]
        public async Task Dispatch_Sets405_ForGet()
        {
            var context = Context("GET");

            await new JobCatalogRunDispatcher().Dispatch(context);

            Assert.Equal(405, context.Response.StatusCode);
        }

        [Fact]
        public async Task Dispatch_Sets422_WithoutAnId_And404_ForAnUnknownOne()
        {
            var missing = Context();
            await new JobCatalogRunDispatcher().Dispatch(missing);
            Assert.Equal(422, missing.Response.StatusCode);

            var unknown = Post(("id", "Nope|Method|"));
            await new JobCatalogRunDispatcher().Dispatch(unknown);
            Assert.Equal(404, unknown.Response.StatusCode);
        }

        [Fact, StaticLock]
        public async Task Dispatch_EnqueuesTheJob_WithConvertedArguments()
        {
            var context = Post(("id", _entryId), ("mode", "enqueue"), ("queue", "critical"), ("arg:to", "bob"), ("arg:count", "3"));

            await new JobCatalogRunDispatcher().Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            Assert.Equal("123", JsonNode.Parse(context.Response.Text)["jobId"].GetValue<string>());
            _client.Verify(x => x.Create(
                It.Is<Job>(j => j.Method.Name == "Send" && (string)j.Args[0] == "bob" && (int)j.Args[1] == 3 && j.Queue == "critical"),
                It.Is<EnqueuedState>(s => s.Queue == "critical")));
        }

        [Fact, StaticLock]
        public async Task Dispatch_SchedulesTheJob_WithADelay()
        {
            var context = Post(("id", _entryId), ("mode", "schedule"), ("delay", "15"), ("arg:to", "bob"), ("arg:count", "1"));

            await new JobCatalogRunDispatcher().Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<ScheduledState>(s => s.EnqueueAt > DateTime.UtcNow.AddMinutes(14) && s.EnqueueAt < DateTime.UtcNow.AddMinutes(16))));
        }

        [Fact, StaticLock]
        public async Task Dispatch_Sets422_ForABadDelay()
        {
            var context = Post(("id", _entryId), ("mode", "schedule"), ("delay", "-1"), ("arg:to", "bob"), ("arg:count", "1"));

            await new JobCatalogRunDispatcher().Dispatch(context);

            Assert.Equal(422, context.Response.StatusCode);
            Assert.Contains("delay", JsonNode.Parse(context.Response.Text)["error"].GetValue<string>());
        }

        [Fact, StaticLock]
        public async Task Dispatch_SchedulesTheJob_WithADelayPhrase()
        {
            var context = Post(("id", _entryId), ("mode", "schedule"), ("delay", "in 2 hours"), ("arg:to", "bob"), ("arg:count", "1"));

            await new JobCatalogRunDispatcher().Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            _client.Verify(x => x.Create(It.IsAny<Job>(), It.Is<ScheduledState>(s => s.EnqueueAt > DateTime.UtcNow.AddMinutes(115) && s.EnqueueAt < DateTime.UtcNow.AddMinutes(125))));
        }

        [Theory, StaticLock]
        [InlineData("whenever you like")]
        [InlineData("")]
        public async Task Dispatch_Sets422_ForADelayThatIsNeitherMinutesNorAPhrase(string delay)
        {
            var context = Post(("id", _entryId), ("mode", "schedule"), ("delay", delay), ("arg:to", "bob"), ("arg:count", "1"));

            await new JobCatalogRunDispatcher().Dispatch(context);

            Assert.Equal(422, context.Response.StatusCode);
            Assert.Contains("phrase", JsonNode.Parse(context.Response.Text)["error"].GetValue<string>());
            _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
        }

        [Fact, StaticLock]
        public async Task Dispatch_Sets422_ForAnUnknownMode()
        {
            var context = Post(("id", _entryId), ("mode", "teleport"), ("arg:to", "bob"), ("arg:count", "1"));

            await new JobCatalogRunDispatcher().Dispatch(context);

            Assert.Equal(422, context.Response.StatusCode);
            Assert.Equal("Unknown mode 'teleport'.", JsonNode.Parse(context.Response.Text)["error"].GetValue<string>());
            _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
            _manager.Verify(x => x.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()), Times.Never);
        }

        [Fact, StaticLock]
        public async Task Dispatch_CreatesARecurringJob_FromPlainEnglish()
        {
            var context = Post(("id", _entryId), ("mode", "recurring"), ("recurringId", " nightly "), ("cron", "every day at 3am"), ("arg:to", "ops"), ("arg:count", "2"));

            await new JobCatalogRunDispatcher().Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            Assert.Equal("nightly", JsonNode.Parse(context.Response.Text)["recurringId"].GetValue<string>());
            _manager.Verify(x => x.AddOrUpdate("nightly", It.Is<Job>(j => (string)j.Args[0] == "ops"), "0 3 * * *", It.IsAny<RecurringJobOptions>()));
        }

        [Fact, StaticLock]
        public async Task Dispatch_Sets422_ForRecurringJobsWithoutIdOrCron()
        {
            var noId = Post(("id", _entryId), ("mode", "recurring"), ("cron", "* * * * *"), ("arg:to", "x"), ("arg:count", "1"));
            await new JobCatalogRunDispatcher().Dispatch(noId);
            Assert.Equal(422, noId.Response.StatusCode);

            var noCron = Post(("id", _entryId), ("mode", "recurring"), ("recurringId", "x"), ("arg:to", "x"), ("arg:count", "1"));
            await new JobCatalogRunDispatcher().Dispatch(noCron);
            Assert.Equal(422, noCron.Response.StatusCode);
        }

        [Fact, StaticLock]
        public async Task Dispatch_Sets422_ForBadArguments()
        {
            var context = Post(("id", _entryId), ("arg:to", "bob"), ("arg:count", "many"));

            await new JobCatalogRunDispatcher().Dispatch(context);

            Assert.Equal(422, context.Response.StatusCode);
            Assert.Contains("count", JsonNode.Parse(context.Response.Text)["error"].GetValue<string>());
            _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
        }

        [Fact, StaticLock]
        public async Task Dispatch_RunsAnExistingJobAgain_WithTheGivenArguments()
        {
            var connection = new Mock<IStorageConnection>();
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(connection.Object);
            connection.Setup(x => x.GetJobData("41")).Returns(new JobData { Job = Job.FromExpression<UncataloguedJobs>(x => x.Replay("old", 1)) });
            var context = Post(("source", "41"), ("arg:name", "new"), ("arg:count", "7"));

            await new JobCatalogRunDispatcher().Dispatch(context);

            Assert.Equal(200, context.Response.StatusCode);
            _client.Verify(x => x.Create(
                It.Is<Job>(j => j.Type == typeof(UncataloguedJobs) && j.Method.Name == "Replay" && (string)j.Args[0] == "new" && (int)j.Args[1] == 7),
                It.IsAny<EnqueuedState>()));
        }

        [Fact, StaticLock]
        public async Task Dispatch_Sets404_WhenTheSourceJobIsGone()
        {
            var connection = new Mock<IStorageConnection>();
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(connection.Object);
            var context = Post(("source", "404"));

            await new JobCatalogRunDispatcher().Dispatch(context);

            Assert.Equal(404, context.Response.StatusCode);
        }

        public class UncataloguedJobs
        {
            public void Replay(string name, int count) { }
        }

        public class RunnerJobs
        {
            public void Send(string to, int count) { }
        }
    }
}
