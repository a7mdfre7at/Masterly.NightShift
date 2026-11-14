using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class UrlHelperFacts
{
    private readonly UrlHelper _url = new(new DashboardContextStub(new DashboardOptions()));

    [Fact]
    public void JobDetails_WritesTheId_AsOnePathSegment()
    {
        Assert.Equal("/jobs/details/a~2Fb~252Fc~7Ed", _url.JobDetails("a/b%2Fc~d"));
    }

    [Fact]
    public void JobDetails_KeepsTheLinkOfANumericId()
    {
        Assert.Equal("/jobs/details/42", _url.JobDetails("42"));
    }

    [Fact]
    public void Queue_WritesTheQueue_AsOnePathSegment()
    {
        Assert.Equal("/jobs/enqueued/a~2Fb", _url.Queue("a/b"));
        Assert.Equal("/jobs/enqueued/critical", _url.Queue("critical"));
    }
}
