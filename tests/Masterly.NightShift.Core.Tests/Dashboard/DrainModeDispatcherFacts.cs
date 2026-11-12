using System.Text.Json.Nodes;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class DrainModeDispatcherFacts : IDisposable
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IStorageConnection> _connection = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private Dictionary<string, string> _hash;

    public DrainModeDispatcherFacts()
    {
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
        _connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(() => _hash);
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _transaction.Setup(x => x.SetRangeInHash(DrainMode.HashKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) =>
            {
                _hash ??= [];
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
    public async Task Dispatch_Sets401_WhenReadOnly_ForPost()
    {
        DashboardContextStub context = new(new DashboardOptions { IsReadOnlyFunc = _ => true }, new DashboardRequestStub("POST"), _storage.Object);

        await new DrainModeDispatcher(enable: true).Dispatch(context);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.Null(_hash);
    }

    [Fact]
    public async Task Dispatch_AnswersTheStatus_ForGet_EvenWhenReadOnly()
    {
        _hash = new Dictionary<string, string> { { "Drain", "1" }, { "DrainReason", "deploy" } };
        DashboardContextStub context = new(new DashboardOptions { IsReadOnlyFunc = _ => true }, new DashboardRequestStub("GET"), _storage.Object);

        await new DrainModeDispatcher(enable: true).Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        JsonNode? json = JsonNode.Parse(context.Response.Text);
        Assert.True(json["active"].GetValue<bool>());
        Assert.Equal("deploy", json["reason"].GetValue<string>());
        Assert.Equal(30, json["holdSeconds"].GetValue<int>());
    }

    [Fact]
    public async Task Dispatch_Sets405_ForOtherMethods()
    {
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub("PUT"), _storage.Object);

        await new DrainModeDispatcher(enable: true).Dispatch(context);

        Assert.Equal(405, context.Response.StatusCode);
    }

    [Fact, StaticLock]
    public async Task Dispatch_EnablesTheSwitch_WithTheReason()
    {
        DashboardRequestStub request = new("POST");
        request.Form["reason"] = ["  db maintenance  "];
        DashboardContextStub context = new(new DashboardOptions(), request, _storage.Object);

        await new DrainModeDispatcher(enable: true).Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("1", _hash["Drain"]);
        Assert.Equal("db maintenance", _hash["DrainReason"]);
        Assert.True(JsonNode.Parse(context.Response.Text)["active"].GetValue<bool>());
    }

    [Fact, StaticLock]
    public async Task Dispatch_TruncatesTheReason_To200Characters()
    {
        DashboardRequestStub request = new("POST");
        request.Form["reason"] = [new('d', 201)];
        DashboardContextStub context = new(new DashboardOptions(), request, _storage.Object);

        await new DrainModeDispatcher(enable: true).Dispatch(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(new string('d', 200), _hash["DrainReason"]);
        Assert.Equal(new string('d', 200), JsonNode.Parse(context.Response.Text)["reason"].GetValue<string>());
    }

    [Fact, StaticLock]
    public async Task Dispatch_DisablesTheSwitch()
    {
        _hash = new Dictionary<string, string> { { "Drain", "1" } };
        DashboardContextStub context = new(new DashboardOptions(), new DashboardRequestStub("POST"), _storage.Object);

        await new DrainModeDispatcher(enable: false).Dispatch(context);

        Assert.Equal("0", _hash["Drain"]);
        Assert.False(JsonNode.Parse(context.Response.Text)["active"].GetValue<bool>());
    }
}
