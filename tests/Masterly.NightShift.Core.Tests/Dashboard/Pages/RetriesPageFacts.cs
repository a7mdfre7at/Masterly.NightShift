using System.Net;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class RetriesPageFacts
{
    private readonly PageRenderer _renderer = new();

    [Fact]
    public void Render_WithoutRetries_ShowsTheEmptyMessage()
    {
        string html = _renderer.Render(new RetriesPage());

        Assert.Contains(Encode(Strings.RetriesPage_NoJobs), html);
        Assert.DoesNotContain("/retries/delete-all", html);
    }

    [Fact]
    public void Render_WithRetries_ShowsTheCommands()
    {
        SetupRetries();

        string html = _renderer.Render(new RetriesPage());

        Assert.Contains("data-ajax=\"/retries/delete-all\"", html);
        Assert.Contains($"data-confirm=\"{Encode(Strings.Common_DeleteAllConfirm_Title)}\" data-confirm-body=\"{Encode(Strings.Common_DeleteAllConfirm)}\"", html);
        Assert.Contains("data-url=\"/jobs/scheduled/enqueue\"", html);
        Assert.Contains("data-url=\"/jobs/scheduled/delete\"", html);
        Assert.Contains("value=\"1\"", html);
    }

    [Fact]
    public void Render_WhenReadOnly_HidesTheCommands()
    {
        SetupRetries();
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

        string html = _renderer.Render(new RetriesPage());

        Assert.Contains("<span class=\"ns-job-id\">#1</span>", html);
        Assert.Contains("data-copy=\"1\"", html);
        Assert.DoesNotContain("js-job-command", html);
        Assert.DoesNotContain("/retries/delete-all", html);
        Assert.DoesNotContain("/jobs/scheduled/enqueue", html);
    }

    [Fact]
    public void Render_ARetryWithAnUnreadableEnqueueAt_IsShownWithoutIt()
    {
        SetupRetries();
        _renderer.Connection.Setup(x => x.GetStateData("1")).Returns(new StateData
        {
            Name = "Scheduled",
            Reason = "Retry attempt 1 of 10",
            Data = new Dictionary<string, string> { { "EnqueueAt", "nonsense" } },
        });

        string html = _renderer.Render(new RetriesPage());

        Assert.Contains("Retry attempt 1 of 10", html);
        Assert.DoesNotContain("nonsense", html);
    }

    private void SetupRetries()
    {
        _renderer.Connection.Setup(x => x.GetSetCount("retries")).Returns(1);
        _renderer.Connection.Setup(x => x.GetRangeFromSet("retries", It.IsAny<int>(), It.IsAny<int>()))
            .Returns(() => ["1"]);
        _renderer.Connection.Setup(x => x.GetJobData("1")).Returns(new JobData
        {
            Job = Job.FromExpression(() => RetriesPageJobs.Work()),
            State = "Scheduled",
            CreatedAt = DateTime.UtcNow
        });
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

public static class RetriesPageJobs
{
    public static void Work()
    {
    }
}
