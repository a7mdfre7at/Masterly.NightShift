using System.Text;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Stubs;

internal sealed class DashboardResponseStub : DashboardResponse
{
    private readonly StringBuilder _text = new();

    public override string ContentType { get; set; }
    public override int StatusCode { get; set; }
    public override Stream Body { get; }

    public string Text => _text.ToString();

    // Each WriteAsync call, to check what is sent as it's produced
    public List<string> Writes { get; } = [];

    public override void SetExpire(DateTimeOffset? value)
    {
        throw new NotImplementedException();
    }

    public override Task WriteAsync(string text)
    {
        _text.Append(text);
        Writes.Add(text);
        return Task.CompletedTask;
    }
}
