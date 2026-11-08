using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class BatchCommandDispatcherFacts
    {
        [Fact]
        public void Dispatch_Sets401StatusCode_WhenNotPermitted()
        {
            var options = new DashboardOptions
            {
                IsReadOnlyFunc = _ => true
            };
            var context = new DashboardContextStub(options);
            var dispatcher = new BatchCommandDispatcher((DashboardContext ctx, string str) => { });
            dispatcher.Dispatch(context);
            Assert.Equal(401, context.Response.StatusCode);
        }

        [Fact]
        public async Task Dispatch_Sets405_AndRunsNothing_ForNonPostRequests()
        {
            var storage = new Mock<JobStorage>();
            var request = new DashboardRequestStub("GET", "/jobs/failed/requeue");
            request.Form["jobs[]"] = new List<string> { "1" };
            var context = new DashboardContextStub(new DashboardOptions(), request, storage.Object);
            var called = new List<string>();

            await new BatchCommandDispatcher((_, jobId) => called.Add(jobId)).Dispatch(context);

            Assert.Equal(405, context.Response.StatusCode);
            Assert.Empty(called);
            storage.Verify(x => x.GetConnection(), Times.Never);
        }

        [Fact]
        public async Task Dispatch_RunsTheCommandForEveryJob_AndAnswers204_ForPostRequests()
        {
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetConnection()).Returns(new Mock<JobStorageConnection> { DefaultValue = DefaultValue.Mock }.Object);
            var request = new DashboardRequestStub("POST", "/jobs/failed/requeue");
            request.Form["jobs[]"] = new List<string> { "1", "2" };
            var context = new DashboardContextStub(new DashboardOptions(), request, storage.Object);
            var called = new List<string>();

            await new BatchCommandDispatcher((_, jobId) => called.Add(jobId)).Dispatch(context);

            Assert.Equal(204, context.Response.StatusCode);
            Assert.Equal(new[] { "1", "2" }, called);
        }

        [Fact, StaticLock]
        public async Task Dispatch_GoesOnAfterAJobWhoseTypeCanNotBeLoaded_AndAnswers422_NamingIt()
        {
            // "Trigger now" in a dashboard-only app: RecurringJobManager wraps the JobLoadException. It answered
            // 500, and the jobs triggered before it weren't audited.
            var audit = new List<string>();
            var transaction = new Mock<IWriteOnlyTransaction>();
            transaction.Setup(x => x.InsertToList(AuditLog.ListKey, It.IsAny<string>())).Callback<string, string>((_, value) => audit.Add(value));
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetConnection()).Returns(connection.Object);
            var request = new DashboardRequestStub("POST", "/recurring/trigger");
            request.Form["jobs[]"] = new List<string> { "a", "shop", "b" };
            var context = new DashboardContextStub(new DashboardOptions(), request, storage.Object);
            var called = new List<string>();

            await new BatchCommandDispatcher((_, id) =>
            {
                if (id == "shop") throw new AggregateException(new JobLoadException("Could not load the job.", new InvalidOperationException("Could not resolve assembly 'Shop.Web'.")));
                called.Add(id);
            }).Dispatch(context);

            Assert.Equal(422, context.Response.StatusCode);
            Assert.Contains("shop (Could not resolve assembly 'Shop.Web'.)", context.Response.Text);
            Assert.Equal(new[] { "a", "b" }, called);
            var entry = Assert.Single(audit);
            Assert.Contains("\"s\":422", entry);
            Assert.Contains("2 of 3 done", entry);
        }

        [Fact, StaticLock]
        public async Task Dispatch_RecordsWhatWasDone_WhenAnotherErrorStopsIt()
        {
            var audit = new List<string>();
            var transaction = new Mock<IWriteOnlyTransaction>();
            transaction.Setup(x => x.InsertToList(AuditLog.ListKey, It.IsAny<string>())).Callback<string, string>((_, value) => audit.Add(value));
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetConnection()).Returns(connection.Object);
            var request = new DashboardRequestStub("POST", "/recurring/trigger");
            request.Form["jobs[]"] = new List<string> { "a", "b" };
            var context = new DashboardContextStub(new DashboardOptions(), request, storage.Object);

            await Assert.ThrowsAsync<InvalidOperationException>(() => new BatchCommandDispatcher((_, id) =>
            {
                if (id == "b") throw new InvalidOperationException("storage down");
            }).Dispatch(context));

            var entry = Assert.Single(audit);
            Assert.Contains("\"s\":500", entry);
            Assert.Contains("1 of 2 done", entry);
        }
    }
}
