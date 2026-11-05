using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Stubs
{
    class DashboardContextStub : DashboardContext
    {
        public DashboardContextStub(DashboardOptions options) : this(options, new DashboardRequestStub())
        {
        }

        public DashboardContextStub(DashboardOptions options, DashboardRequestStub request) : this(options, request, new JobStorageStub())
        {
        }

        public DashboardContextStub(DashboardOptions options, DashboardRequestStub request, JobStorage storage) : base(storage, options)
        {
            Request = request;
            base.Response = new DashboardResponseStub();
        }

        public new DashboardResponseStub Response => (DashboardResponseStub)base.Response;

        public IRecurringJobManager RecurringJobManager { get; set; }

        public IBackgroundJobClient BackgroundJobClient { get; set; }

        public override IRecurringJobManager GetRecurringJobManager()
        {
            return RecurringJobManager ?? base.GetRecurringJobManager();
        }

        public override IBackgroundJobClient GetBackgroundJobClient()
        {
            return BackgroundJobClient ?? base.GetBackgroundJobClient();
        }
    }
}
