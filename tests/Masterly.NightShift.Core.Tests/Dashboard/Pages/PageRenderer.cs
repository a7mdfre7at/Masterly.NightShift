using System;
using System.Collections.Generic;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    /// <summary>
    /// Renders a dashboard page against mocked storage, with an empty but valid
    /// answer for every monitoring query, so a test only sets up what it checks.
    /// </summary>
    internal sealed class PageRenderer
    {
        public PageRenderer()
        {
            Storage = new Mock<JobStorage>();
            Monitoring = new Mock<IMonitoringApi>();
            Connection = new Mock<JobStorageConnection> { DefaultValue = DefaultValue.Empty };
            Options = new DashboardOptions();
            Request = new DashboardRequestStub();

            Storage.Setup(x => x.GetMonitoringApi()).Returns(Monitoring.Object);
            Storage.Setup(x => x.GetConnection()).Returns(Connection.Object);
            Storage.Setup(x => x.GetReadOnlyConnection()).Returns(Connection.Object);
            // Like SQL Server: the pages may use the optional members of JobStorageConnection
            Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);

            Monitoring.Setup(x => x.GetStatistics()).Returns(new StatisticsDto());
            Monitoring.Setup(x => x.Servers()).Returns(new List<ServerDto>());
            Monitoring.Setup(x => x.Queues()).Returns(new List<QueueWithTopEnqueuedJobsDto>());
            Monitoring.Setup(x => x.EnqueuedJobs(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new JobList<EnqueuedJobDto>(new List<KeyValuePair<string, EnqueuedJobDto>>()));
            Monitoring.Setup(x => x.FetchedJobs(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new JobList<FetchedJobDto>(new List<KeyValuePair<string, FetchedJobDto>>()));
            Monitoring.Setup(x => x.ProcessingJobs(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new JobList<ProcessingJobDto>(new List<KeyValuePair<string, ProcessingJobDto>>()));
            Monitoring.Setup(x => x.ScheduledJobs(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new JobList<ScheduledJobDto>(new List<KeyValuePair<string, ScheduledJobDto>>()));
            Monitoring.Setup(x => x.SucceededJobs(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new JobList<SucceededJobDto>(new List<KeyValuePair<string, SucceededJobDto>>()));
            Monitoring.Setup(x => x.FailedJobs(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new JobList<FailedJobDto>(new List<KeyValuePair<string, FailedJobDto>>()));
            Monitoring.Setup(x => x.DeletedJobs(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new JobList<DeletedJobDto>(new List<KeyValuePair<string, DeletedJobDto>>()));
            Monitoring.Setup(x => x.SucceededByDatesCount()).Returns(new Dictionary<DateTime, long>());
            Monitoring.Setup(x => x.FailedByDatesCount()).Returns(new Dictionary<DateTime, long>());
            Monitoring.Setup(x => x.HourlySucceededJobs()).Returns(new Dictionary<DateTime, long>());
            Monitoring.Setup(x => x.HourlyFailedJobs()).Returns(new Dictionary<DateTime, long>());

            Connection.Setup(x => x.GetAllItemsFromList(It.IsAny<string>())).Returns(new List<string>());
            Connection.Setup(x => x.GetRangeFromList(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new List<string>());
            Connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns(new HashSet<string>());
            Connection.Setup(x => x.GetRangeFromSet(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new List<string>());
            Connection.Setup(x => x.GetFirstByLowestScoreFromSet(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<int>()))
                .Returns(new List<string>());
        }

        public Mock<JobStorage> Storage { get; }
        public Mock<IMonitoringApi> Monitoring { get; }
        public Mock<JobStorageConnection> Connection { get; }
        public DashboardOptions Options { get; set; }
        public DashboardRequestStub Request { get; set; }

        /// <summary>The context of the last render, to inspect the response.</summary>
        public DashboardContextStub Context { get; private set; }

        public string Render(RazorPage page)
        {
            Context = new DashboardContextStub(Options, Request, Storage.Object);
            page.Assign(Context);
            return page.ToString();
        }
    }
}
