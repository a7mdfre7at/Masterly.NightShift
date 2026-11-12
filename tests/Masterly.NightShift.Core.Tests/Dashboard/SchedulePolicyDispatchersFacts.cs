using System.Text.RegularExpressions;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class SchedulePolicyDispatchersFacts
{
    private const string DeletePattern = "/policies/delete/(?<Name>[^/]+)/(?<Revision>[0-9]+)";

    private readonly PolicyStorage _storage = new();

    [Fact]
    public async Task SaveAsync_NamesThePolicyInTheAuditDetail_WhenItSaves()
    {
        CommandResult result = await SchedulePolicyDispatchers.SaveAsync(Save("business-hours", revision: "0"));

        Assert.Equal(204, result.StatusCode);
        Assert.Equal("policy 'business-hours', revision 1", result.Detail);
    }

    [Fact]
    public async Task SaveAsync_NamesThePolicyInTheAuditDetail_WhenItIsRefused()
    {
        _storage.Add(new SchedulePolicy("p"), revision: 4);
        DashboardContextStub bad = Save("p", revision: "4");
        ((DashboardRequestStub)bad.Request).Form["windows"] = ["nine to five"];

        CommandResult stale = await SchedulePolicyDispatchers.SaveAsync(Save("p", revision: "3"));
        CommandResult invalid = await SchedulePolicyDispatchers.SaveAsync(bad);

        Assert.Equal(409, stale.StatusCode);
        Assert.Equal("policy 'p'", stale.Detail);
        Assert.Equal(422, invalid.StatusCode);
        Assert.Equal("policy 'p'", invalid.Detail);
        Assert.Contains("nine to five", invalid.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("99999999999")]
    [InlineData("7")]
    [InlineData("Someday")]
    public async Task SaveAsync_Answers422_ForADayThatIsNotADayOfTheWeek(string day)
    {
        DashboardContextStub context = Save("p", revision: "0");
        ((DashboardRequestStub)context.Request).Form["days"] = ["Monday", day];

        CommandResult result = await SchedulePolicyDispatchers.SaveAsync(context);

        Assert.Equal(422, result.StatusCode);
        Assert.Equal($"'{day}' isn't a day of the week.", result.Error);
        Assert.Null(_storage.Object.GetSchedulePolicy("p"));
    }

    [Theory]
    [InlineData("99999999999999999999")]
    [InlineData("-1")]
    [InlineData("three")]
    public async Task SaveAsync_Answers422_ForARevisionThatIsNotOne(string revision)
    {
        CommandResult result = await SchedulePolicyDispatchers.SaveAsync(Save("p", revision));

        Assert.Equal(422, result.StatusCode);
        Assert.Equal($"'{revision}' isn't a revision.", result.Error);
        Assert.Null(_storage.Object.GetSchedulePolicy("p"));
    }

    [Fact]
    public async Task SaveAsync_AcceptsDayNamesInAnyCase()
    {
        DashboardContextStub context = Save("p", revision: "0");
        ((DashboardRequestStub)context.Request).Form["days"] = ["saturday", "SUNDAY"];

        CommandResult result = await SchedulePolicyDispatchers.SaveAsync(context);

        Assert.Equal(204, result.StatusCode);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Saturday], _storage.Object.GetSchedulePolicy("p")?.Days);
    }

    [Fact]
    public async Task Delete_Answers422_ForARevisionTooLargeToBeOne()
    {
        _storage.Add(new SchedulePolicy("p"), revision: 2);
        DashboardContextStub context = Post("/policies/delete/p/99999999999999999999", DeletePattern);

        await new JsonCommandDispatcher(SchedulePolicyDispatchers.Delete).Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
        Assert.Contains("isn't a revision", context.Response.Text, StringComparison.Ordinal);
        Assert.NotNull(_storage.Object.GetSchedulePolicy("p"));
    }

    private DashboardContextStub Save(string name, string revision)
    {
        DashboardContextStub context = Post("/policies/save", "/policies/save");
        DashboardRequestStub request = (DashboardRequestStub)context.Request;
        request.Form["name"] = [name];
        request.Form["revision"] = [revision];
        return context;
    }

    private DashboardContextStub Post(string path, string pattern)
    {
        DashboardRequestStub request = new("POST", path);
        return new DashboardContextStub(new DashboardOptions(), request, _storage.Object)
        {
            UriMatch = Regex.Match(path, pattern),
        };
    }
}
