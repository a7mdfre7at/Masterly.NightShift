using System.Text;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class PingDispatcherFacts
{
    private readonly Mock<JobStorage> _storage = new();

    [Fact]
    public async Task Dispatch_AnswersPong_AsPlainText()
    {
        var context = new PingContext(_storage.Object, new DashboardOptions());

        await new PingDispatcher().Dispatch(context);

        Assert.Equal(200, context.PingResponse.StatusCode);
        Assert.Equal("text/plain", context.PingResponse.ContentType);
        Assert.Equal("pong", context.PingResponse.Text);
    }

    [Fact]
    public async Task Dispatch_SetsNoExpiry_SoTheAnswerIsNeverCachedUntilALaterTime()
    {
        var context = new PingContext(_storage.Object, new DashboardOptions());

        await new PingDispatcher().Dispatch(context);

        Assert.Equal(new DateTimeOffset?[] { null }, context.PingResponse.Expirations);
    }

    [Fact]
    public async Task Dispatch_NeverTouchesTheStorage()
    {
        var context = new PingContext(_storage.Object, new DashboardOptions());

        await new PingDispatcher().Dispatch(context);

        _storage.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Dispatch_AnswersPong_EvenForReadOnlyDashboards()
    {
        var context = new PingContext(_storage.Object, new DashboardOptions { IsReadOnlyFunc = _ => true });

        await new PingDispatcher().Dispatch(context);

        Assert.Equal(200, context.PingResponse.StatusCode);
        Assert.Equal("pong", context.PingResponse.Text);
    }

    private sealed class PingContext : DashboardContext
    {
        public PingContext(JobStorage storage, DashboardOptions options) : base(storage, options)
        {
            Request = new DashboardRequestStub("GET", "/ping");
            PingResponse = new RecordingResponse();
            Response = PingResponse;
        }

        public RecordingResponse PingResponse { get; }
    }

    private sealed class RecordingResponse : DashboardResponse
    {
        private readonly StringBuilder _text = new();

        public override string ContentType { get; set; }
        public override int StatusCode { get; set; }
        public override Stream Body { get; } = new MemoryStream();

        public string Text => _text.ToString();
        public List<DateTimeOffset?> Expirations { get; } = [];

        public override void SetExpire(DateTimeOffset? value)
        {
            Expirations.Add(value);
        }

        public override Task WriteAsync(string text)
        {
            _text.Append(text);
            return Task.CompletedTask;
        }
    }
}
