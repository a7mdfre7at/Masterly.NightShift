using System.Net;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class DeletedJobsPageFacts
{
    private readonly PageRenderer _renderer = new();

    [Fact]
    public void Render_WithoutDeletedJobs_ShowsTheEmptyMessage()
    {
        string html = _renderer.Render(new DeletedJobsPage());

        Assert.Contains(Encode(Strings.DeletedJobsPage_NoJobs), html);
        Assert.DoesNotContain("/jobs/deleted/export", html);
    }

    [Fact]
    public void Render_WithDeletedJobs_ShowsTheDownloadAndRequeue()
    {
        SetupJobs();

        string html = _renderer.Render(new DeletedJobsPage());

        Assert.Contains("data-url=\"/jobs/deleted/export\" data-filename=\"nightshift-deleted-jobs.csv\"", html);
        Assert.Contains(Encode(Strings.Common_DownloadCsv), html);
        Assert.Contains("data-url=\"/jobs/deleted/requeue\"", html);
    }

    [Fact]
    public void Render_WhenReadOnly_KeepsTheDownloadButHidesTheRequeue()
    {
        SetupJobs();
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

        string html = _renderer.Render(new DeletedJobsPage());

        Assert.Contains("data-url=\"/jobs/deleted/export\"", html);
        Assert.DoesNotContain("/jobs/deleted/requeue", html);
    }

    private void SetupJobs()
    {
        _renderer.Monitoring.Setup(x => x.DeletedListCount()).Returns(1);
        _renderer.Monitoring.Setup(x => x.DeletedJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<DeletedJobDto>(new Dictionary<string, DeletedJobDto>
            {
                { "1", new DeletedJobDto { Job = Job.FromExpression(() => DeletedPageJobs.Work()), InDeletedState = true, DeletedAt = DateTime.UtcNow } }
            }));
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

public static class DeletedPageJobs
{
    public static void Work()
    {
    }
}
