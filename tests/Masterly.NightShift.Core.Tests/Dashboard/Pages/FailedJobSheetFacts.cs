using System.Globalization;
using System.Net;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class FailedJobSheetFacts
{
    private const string Trace = "System.InvalidOperationException: Connection <lost>\r\n   at FailedPageJobs.Work() in C:\\Work.cs:line 1";

    private readonly PageRenderer _renderer = new();
    private readonly DateTime _failedAt = new(2026, 11, 15, 10, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Render_ShowsTheLatestFailure_WithItsTimeServerTraceAndCopyButton_WithoutTheLayout()
    {
        SetupJob("7", Failure("Connection <lost>", _failedAt), Failure("Older", _failedAt.AddHours(-1)));

        string html = _renderer.Render(new FailedJobSheet("7"));

        Assert.DoesNotContain("<html", html);
        Assert.Contains("<p class=\"ns-exception-type\">System.InvalidOperationException</p>", html);
        Assert.Contains("<p class=\"ns-sheet-lead\">Connection &lt;lost&gt;</p>", html);
        Assert.DoesNotContain("Older", html);
        Assert.Contains($"<dt>{Encode(Strings.Common_Reason)}</dt>", html);
        Assert.Contains($"<dt>{Encode(Strings.FailedJobsPage_Table_Failed)}</dt>", html);
        Assert.Contains("data-moment=\"" + JobHelper.ToTimestamp(_failedAt).ToString(CultureInfo.InvariantCulture) + "\"", html);
        Assert.Contains($"<dt>{Encode(Strings.Common_Server)}</dt>", html);
        Assert.Contains("web-1", html);
        Assert.Contains("<pre class=\"stack-trace\"><code>", html);
        Assert.DoesNotContain("Connection <lost>", html);
        Assert.Contains("data-copy=\"" + Encode(Trace) + "\"", html);
        Assert.Contains($"aria-label=\"{Encode(Strings.FailedJobsPage_Sheet_CopyStackTrace)}\"", html);
        Assert.Contains("<div class=\"js-sheet-actions\">", html);
        Assert.Contains("href=\"/jobs/details/7\"", html);
        Assert.Contains("data-url=\"/jobs/failed/requeue\" data-job=\"7\"", html);
        Assert.Contains("data-url=\"/jobs/failed/delete\" data-job=\"7\"", html);
        Assert.DoesNotContain(Encode(Strings.FailedJobsPage_Sheet_NoLongerFailed), html);
        _renderer.Monitoring.Verify(x => x.JobDetails("7"), Times.Once);
        _renderer.Monitoring.Verify(x => x.FailedJobs(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void Render_ForAJobNoLongerFailed_ShowsItsLatestFailure_WithoutRequeueOrDelete()
    {
        SetupJob("7", new StateHistoryDto { StateName = EnqueuedState.StateName, CreatedAt = _failedAt.AddMinutes(1), Data = new Dictionary<string, string>() }, Failure("Boom", _failedAt));

        string html = _renderer.Render(new FailedJobSheet("7"));

        Assert.Contains(Encode(Strings.FailedJobsPage_Sheet_NoLongerFailed), html);
        Assert.Contains("<p class=\"ns-sheet-lead\">Boom</p>", html);
        Assert.Contains("href=\"/jobs/details/7\"", html);
        Assert.DoesNotContain("js-job-command", html);
    }

    [Fact]
    public void Render_WhenReadOnly_KeepsTheLinkButHidesTheCommands()
    {
        SetupJob("7", Failure("Boom", _failedAt));
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = _ => true };

        string html = _renderer.Render(new FailedJobSheet("7"));

        Assert.Contains("href=\"/jobs/details/7\"", html);
        Assert.Contains("<pre class=\"stack-trace\">", html);
        Assert.DoesNotContain("js-job-command", html);
    }

    [Fact]
    public void Render_ForAMissingJob_SaysSo()
    {
        string html = _renderer.Render(new FailedJobSheet("<42>"));

        Assert.Contains(Encode(string.Format(Strings.JobDetailsPage_JobExpired, "<42>")), html);
        Assert.Contains("role=\"status\"", html);
        Assert.DoesNotContain("<42>", html);
        Assert.DoesNotContain("js-sheet-actions", html);
    }

    [Fact]
    public void Render_ForAJobThatNeverFailed_SaysSo_AndLinksToTheJob()
    {
        SetupJob("7", new StateHistoryDto { StateName = SucceededState.StateName, CreatedAt = _failedAt, Data = new Dictionary<string, string>() });

        string html = _renderer.Render(new FailedJobSheet("7"));

        Assert.Contains(Encode(Strings.FailedJobsPage_Sheet_NotFailed), html);
        Assert.Contains("href=\"/jobs/details/7\"", html);
        Assert.DoesNotContain("stack-trace", html);
        Assert.DoesNotContain("js-job-command", html);
    }

    [Fact]
    public void Render_ForAFailureWithoutTrace_LeavesTheTraceAndItsCopyButtonOut()
    {
        StateHistoryDto failure = Failure("Boom", _failedAt);
        failure.Data.Remove("ExceptionDetails");
        failure.Data.Remove("ServerId");
        SetupJob("7", failure);

        string html = _renderer.Render(new FailedJobSheet("7"));

        Assert.Contains("<p class=\"ns-sheet-lead\">Boom</p>", html);
        Assert.DoesNotContain("stack-trace", html);
        Assert.DoesNotContain("js-copy", html);
        Assert.DoesNotContain($"<dt>{Encode(Strings.Common_Server)}</dt>", html);
    }

    private void SetupJob(string jobId, params StateHistoryDto[] history)
    {
        _renderer.Monitoring.Setup(x => x.JobDetails(jobId)).Returns(new JobDetailsDto
        {
            Job = Job.FromExpression(() => FailedPageJobs.Work()),
            CreatedAt = _failedAt.AddHours(-2),
            Properties = new Dictionary<string, string>(),
            History = [.. history]
        });
    }

    private static StateHistoryDto Failure(string message, DateTime failedAt)
    {
        return new StateHistoryDto
        {
            StateName = FailedState.StateName,
            Reason = "An exception occurred",
            CreatedAt = failedAt,
            Data = new Dictionary<string, string>
            {
                { "FailedAt", JobHelper.SerializeDateTime(failedAt) },
                { "ExceptionType", "System.InvalidOperationException" },
                { "ExceptionMessage", message },
                { "ExceptionDetails", Trace },
                { "ServerId", "web-1:42:abc" }
            }
        };
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
