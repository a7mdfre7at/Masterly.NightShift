using System.Text.RegularExpressions;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class ServerPauseDispatcherFacts : IDisposable
{
    private const string PauseRoute = "/servers/pause/(?<Id>.+)";
    private const string ResumeRoute = "/servers/resume/(?<Id>.+)";

    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IStorageConnection> _connection = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly Dictionary<string, string> _hash = [];

    public ServerPauseDispatcherFacts()
    {
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(() => _hash);
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _transaction.Setup(x => x.SetRangeInHash(DrainMode.HashKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) =>
            {
                foreach (KeyValuePair<string, string> pair in pairs)
                {
                    _hash[pair.Key] = pair.Value;
                }
            });
        DrainMode.InvalidateCache();
    }

    public void Dispose()
    {
        DrainMode.InvalidateCache();
    }

    [Fact]
    public async Task Dispatch_Sets401_WhenReadOnly()
    {
        DashboardContextStub context = Create("/servers/pause/web-1", PauseRoute, readOnly: true);

        await new ServerPauseDispatcher(pause: true).Dispatch(context);

        Assert.Equal(401, context.Response.StatusCode);
        _storage.Verify(x => x.GetConnection(), Times.Never);
    }

    [Fact]
    public async Task Dispatch_Sets405_ForNonPostRequests()
    {
        DashboardContextStub context = Create("/servers/pause/web-1", PauseRoute, method: "GET");

        await new ServerPauseDispatcher(pause: true).Dispatch(context);

        Assert.Equal(405, context.Response.StatusCode);
        _storage.Verify(x => x.GetConnection(), Times.Never);
    }

    [Fact]
    public async Task Dispatch_Sets422_WhenTheServerIdIsBlank()
    {
        DashboardContextStub context = Create("/servers/pause/  ", PauseRoute);

        await new ServerPauseDispatcher(pause: true).Dispatch(context);

        Assert.Equal(422, context.Response.StatusCode);
        _storage.Verify(x => x.GetConnection(), Times.Never);
    }

    [Fact, StaticLock]
    public async Task Dispatch_PausesTheUnescapedServer_WithTheTrimmedReason()
    {
        // ASP.NET Core decodes the path except %2F, which the dashboard decodes itself
        DashboardContextStub context = Create("/servers/pause/web-1:42%2Fa", PauseRoute, reason: "  deploy  ");

        await new ServerPauseDispatcher(pause: true).Dispatch(context);

        Assert.Equal(204, context.Response.StatusCode);
        DrainModeStatus status = DrainMode.Read(_connection.Object);
        Assert.True(status.IsServerPaused("web-1:42/a"));
        Assert.Equal("deploy", status.PausedServers["web-1:42/a"]);
        Assert.False(status.Active);
        _transaction.Verify(x => x.Commit(), Times.AtLeastOnce);
    }

    [Fact, StaticLock]
    public async Task Dispatch_PausesTheServer_WithoutAReason_WhenNoneIsGiven()
    {
        DashboardContextStub context = Create("/servers/pause/web-1", PauseRoute, reason: "   ");

        await new ServerPauseDispatcher(pause: true).Dispatch(context);

        Assert.Equal(204, context.Response.StatusCode);
        Assert.Equal(string.Empty, _hash[DrainMode.ServerFieldPrefix + "web-1"]);
        Assert.Null(DrainMode.Read(_connection.Object).PausedServers["web-1"]);
    }

    [Fact, StaticLock]
    public async Task Dispatch_TruncatesTheReason_To200Characters()
    {
        DashboardContextStub context = Create("/servers/pause/web-1", PauseRoute, reason: new string('r', 250));

        await new ServerPauseDispatcher(pause: true).Dispatch(context);

        Assert.Equal(new string('r', 200), _hash[DrainMode.ServerFieldPrefix + "web-1"]);
    }

    [Fact, StaticLock]
    public async Task Dispatch_ResumesAPausedServer_IgnoringAnyReason()
    {
        _hash[DrainMode.ServerFieldPrefix + "web-1"] = "deploy";
        DashboardContextStub context = Create("/servers/resume/web-1", ResumeRoute, reason: "ignored");

        await new ServerPauseDispatcher(pause: false).Dispatch(context);

        Assert.Equal(204, context.Response.StatusCode);
        Assert.False(DrainMode.Read(_connection.Object).IsServerPaused("web-1"));
        Assert.NotEqual("ignored", _hash[DrainMode.ServerFieldPrefix + "web-1"]);
    }

    [Fact, StaticLock]
    public async Task Dispatch_RecordsTheServerAndTheReason_InTheAuditLog()
    {
        DashboardContextStub context = Create("/servers/pause/web-7", PauseRoute, reason: "debugging");

        await new ServerPauseDispatcher(pause: true).Dispatch(context);

        _transaction.Verify(x => x.InsertToList(
            AuditLog.ListKey,
            It.Is<string>(s => s.Contains("\"web-7\"") && s.Contains("\"debugging\"") && s.Contains("/servers/pause/web-7"))));
    }

    private DashboardContextStub Create(string path, string route, string method = "POST", bool readOnly = false, string reason = null)
    {
        DashboardRequestStub request = new(method, path);
        if (reason != null)
        {
            request.Form["reason"] = [reason];
        }

        return new DashboardContextStub(new DashboardOptions { IsReadOnlyFunc = _ => readOnly }, request, _storage.Object)
        {
            UriMatch = Regex.Match(path, route),
        };
    }
}
