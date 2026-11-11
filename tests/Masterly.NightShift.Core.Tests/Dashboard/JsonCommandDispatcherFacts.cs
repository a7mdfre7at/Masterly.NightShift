using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class JsonCommandDispatcherFacts
{
    private readonly PolicyStorage _storage = new();

    [Fact]
    public async Task Dispatch_Refuses_ReadOnlyUsers_AndOtherMethodsThanPost()
    {
        JsonCommandDispatcher dispatcher = new(static _ => CommandResult.Done());
        DashboardContextStub readOnly = new(new DashboardOptions { IsReadOnlyFunc = static _ => true }, new DashboardRequestStub("POST"));
        DashboardContextStub get = new(new DashboardOptions(), new DashboardRequestStub("GET"));

        await dispatcher.Dispatch(readOnly);
        await dispatcher.Dispatch(get);

        Assert.Equal(401, readOnly.Response.StatusCode);
        Assert.Equal(405, get.Response.StatusCode);
    }

    [Fact]
    public async Task Dispatch_Answers204_WhenTheCommandIsDone()
    {
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub("POST"));

        await new JsonCommandDispatcher(static _ => CommandResult.Done()).Dispatch(context);

        Assert.Equal(204, context.Response.StatusCode);
    }

    [Fact]
    public async Task Dispatch_Answers422_WithTheMessage_WhenTheCommandIsRefused()
    {
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub("POST"));

        await new JsonCommandDispatcher((Func<DashboardContext, CommandResult>)(static _ => throw new InvalidOperationException("Chain 'x' hasn't finished."))).Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
        Assert.Equal("Chain 'x' hasn't finished.", JsonNode.Parse(context.Response.Text)!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChainRetry_Answers422_WhenTheJobIsNotAChainStep()
    {
        DashboardContextStub context = Post("/chains/retry/42", "/chains/retry/(?<JobId>.+)");

        await DashboardRoutes.Routes.FindDispatcher("/chains/retry/42")!.Item1.Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
        Assert.Contains("isn't a step of a chain", context.Response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolicySave_CreatesThePolicy_FromTheForm()
    {
        DashboardContextStub context = Post("/policies/save", "/policies/save", out DashboardRequestStub request);
        request.Form["name"] = ["business-hours"];
        request.Form["revision"] = ["0"];
        request.Form["days"] = ["Monday", "Tuesday"];
        request.Form["windows"] = ["09:00-12:00, 13:00-17:00"];
        request.Form["dates"] = ["2026-12-24, 12-25"];
        request.Form["description"] = ["Office hours"];

        await new JsonCommandDispatcher(SchedulePolicyDispatchers.SaveAsync).Dispatch(context);

        Assert.Equal(204, context.Response.StatusCode);
        SchedulePolicy saved = _storage.Object.GetSchedulePolicy("business-hours");
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday], saved?.Days);
        Assert.Equal(2, saved?.Windows.Count);
        Assert.Equal([new DateOnly(2026, 12, 24)], saved?.ExcludedDates);
        Assert.Equal([new AnnualDate(12, 25)], saved?.ExcludedAnnualDates);
        Assert.Equal("Office hours", saved?.Description);
    }

    [Fact]
    public async Task PolicySave_Answers409_WhenTheRevisionChanged_And422_ForABadValue()
    {
        _storage.Add(new SchedulePolicy("p"), revision: 4);

        DashboardContextStub stale = Post("/policies/save", "/policies/save", out DashboardRequestStub staleRequest);
        staleRequest.Form["name"] = ["p"];
        staleRequest.Form["revision"] = ["3"];
        await new JsonCommandDispatcher(SchedulePolicyDispatchers.SaveAsync).Dispatch(stale);

        DashboardContextStub bad = Post("/policies/save", "/policies/save", out DashboardRequestStub badRequest);
        badRequest.Form["name"] = ["p"];
        badRequest.Form["revision"] = ["4"];
        badRequest.Form["windows"] = ["nine to five"];
        await new JsonCommandDispatcher(SchedulePolicyDispatchers.SaveAsync).Dispatch(bad);

        Assert.Equal(409, stale.Response.StatusCode);
        Assert.Contains("Someone changed this policy", stale.Response.Text, StringComparison.Ordinal);
        Assert.Equal(422, bad.Response.StatusCode);
    }

    [Fact]
    public async Task PolicyDelete_DeletesWithTheRevision_OrAnswers409()
    {
        _storage.Add(new SchedulePolicy("p"), revision: 2);

        DashboardContextStub stale = Post("/policies/delete/p/1", "/policies/delete/(?<Name>[^/]+)/(?<Revision>[0-9]+)");
        await new JsonCommandDispatcher(SchedulePolicyDispatchers.Delete).Dispatch(stale);
        DashboardContextStub current = Post("/policies/delete/p/2", "/policies/delete/(?<Name>[^/]+)/(?<Revision>[0-9]+)");
        await new JsonCommandDispatcher(SchedulePolicyDispatchers.Delete).Dispatch(current);

        Assert.Equal(409, stale.Response.StatusCode);
        Assert.Equal(204, current.Response.StatusCode);
        Assert.Null(_storage.Object.GetSchedulePolicy("p"));
    }

    private DashboardContextStub Post(string path, string pattern) => Post(path, pattern, out _);

    private DashboardContextStub Post(string path, string pattern, out DashboardRequestStub request)
    {
        request = new DashboardRequestStub("POST", path);
        return new DashboardContextStub(new DashboardOptions(), request, _storage.Object)
        {
            UriMatch = Regex.Match(path, pattern),
        };
    }
}
