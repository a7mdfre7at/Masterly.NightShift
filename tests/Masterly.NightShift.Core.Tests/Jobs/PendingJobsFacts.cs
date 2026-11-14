using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests;

public class PendingJobsFacts
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IMonitoringApi> _monitoring = new();

    public PendingJobsFacts()
    {
        _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitoring.Object);
        _monitoring.Setup(x => x.Queues()).Returns([]);
    }

    [Fact]
    public void GetPendingJobs_GivesAScheduledJobWithoutAReadableEnqueueTime_NoEnqueueAt()
    {
        DateTime due = new(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
        _monitoring.Setup(x => x.ScheduledJobs(0, It.IsAny<int>())).Returns(new JobList<ScheduledJobDto?>(
        [
            new("1", new ScheduledJobDto { InScheduledState = true, EnqueueAt = DateTime.MinValue }),
            new("2", new ScheduledJobDto { InScheduledState = true, EnqueueAt = due }),
        ]));

        IReadOnlyList<PendingJob> jobs = _storage.Object.GetPendingJobs();

        Assert.Null(Assert.Single(jobs, static x => x.Id == "1").EnqueueAt);
        Assert.Equal(due, Assert.Single(jobs, static x => x.Id == "2").EnqueueAt);
        Assert.Equal(["2"], jobs.Where(x => x.EnqueueAt < due.AddMinutes(1)).Select(static x => x.Id));
    }
}
