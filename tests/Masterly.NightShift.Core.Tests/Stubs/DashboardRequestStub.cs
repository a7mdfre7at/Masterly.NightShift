using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Stubs;

internal class DashboardRequestStub(string method = "GET", string path = "/") : DashboardRequest
{
    public override string Method { get; } = method;
    public override string Path { get; } = path;
    public override string PathBase => "";
    public override string LocalIpAddress => "127.0.0.1";
    public override string RemoteIpAddress => "127.0.0.1";

    public Dictionary<string, string> Query { get; } = [];
    public Dictionary<string, List<string>> Form { get; } = [];

    public override string GetQuery(string key)
    {
        return Query.TryGetValue(key, out string? value) ? value : null;
    }

    public override Task<IList<string>> GetFormValuesAsync(string key)
    {
        IList<string> values = Form.TryGetValue(key, out List<string>? list) ? list : [];
        return Task.FromResult(values);
    }
}
