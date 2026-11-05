using System.Collections.Generic;
using System.Threading.Tasks;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Stubs
{
    class DashboardRequestStub : DashboardRequest
    {
        public DashboardRequestStub(string method = "GET", string path = "/")
        {
            Method = method;
            Path = path;
        }

        public override string Method { get; }
        public override string Path { get; }
        public override string PathBase => "";
        public override string LocalIpAddress => "127.0.0.1";
        public override string RemoteIpAddress => "127.0.0.1";

        public Dictionary<string, string> Query { get; } = new Dictionary<string, string>();
        public Dictionary<string, List<string>> Form { get; } = new Dictionary<string, List<string>>();

        public override string GetQuery(string key)
        {
            return Query.TryGetValue(key, out var value) ? value : null;
        }

        public override Task<IList<string>> GetFormValuesAsync(string key)
        {
            IList<string> values = Form.TryGetValue(key, out var list) ? list : new List<string>();
            return Task.FromResult(values);
        }
    }
}
