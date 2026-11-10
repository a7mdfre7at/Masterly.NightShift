using System.Net;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests.Server;

public class KeepAliveProcessFacts
{
    [Fact]
    public void Ctor_ThrowsAnException_WhenOptionsAreNull()
    {
        Assert.Throws<ArgumentNullException>(() => new KeepAliveProcess((KeepAliveOptions)null));
        Assert.Throws<ArgumentNullException>(() => new KeepAliveProcess((string)null));
    }

    [Fact]
    public void Ctor_ThrowsAnException_WithoutUrlOrFactory()
    {
        Assert.Throws<ArgumentException>(() => new KeepAliveProcess(new KeepAliveOptions()));
    }

    [Fact]
    public void Ctor_ValidatesInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeepAliveProcess(new KeepAliveOptions { Url = "http://localhost", Interval = TimeSpan.Zero }));
    }

    [Fact, StaticLock]
    public void Ping_RequestsTheUrl_AndRecordsSuccess()
    {
        StubHandler handler = new(HttpStatusCode.OK);
        KeepAliveProcess process = new(new KeepAliveOptions { Url = "http://keep.alive/nightshift/stats" }, new HttpClient(handler));

        bool result = process.Ping(CancellationToken.None);

        Assert.True(result);
        Assert.Equal("http://keep.alive/nightshift/stats", handler.LastRequest.RequestUri.ToString());
        Assert.True(KeepAliveProcess.Status.LastSucceeded);
        Assert.NotNull(KeepAliveProcess.Status.LastAttemptAt);
        Assert.Null(KeepAliveProcess.Status.LastError);
    }

    [Fact, StaticLock]
    public void Ping_CountsAnyResponseAsReachingTheHost_ButNotesNonSuccessCodes()
    {
        KeepAliveProcess process = new(new KeepAliveOptions { Url = "http://keep.alive/", LogFailures = false }, new HttpClient(new StubHandler(HttpStatusCode.ServiceUnavailable)));

        bool result = process.Ping(CancellationToken.None);

        Assert.True(result);
        Assert.True(KeepAliveProcess.Status.LastSucceeded);
        Assert.Contains("503", KeepAliveProcess.Status.LastError);
    }

    [Fact, StaticLock]
    public void Ping_RecordsAFailure_WhenTheRequestThrows()
    {
        KeepAliveProcess process = new(new KeepAliveOptions { Url = "http://keep.alive/", LogFailures = false }, new HttpClient(new StubHandler(new HttpRequestException("no route"))));

        Assert.False(process.Ping(CancellationToken.None));
        Assert.Equal("no route", KeepAliveProcess.Status.LastError);
    }

    [Fact, StaticLock]
    public void Ping_UsesTheUrlFactory_OnceItReturnsAUrl()
    {
        int calls = 0;
        StubHandler handler = new(HttpStatusCode.OK);
        KeepAliveProcess process = new(new KeepAliveOptions { UrlFactory = () => ++calls < 2 ? null : "http://late.host/" }, new HttpClient(handler));

        Assert.False(process.Ping(CancellationToken.None));
        Assert.True(process.Ping(CancellationToken.None));
        Assert.True(process.Ping(CancellationToken.None));

        Assert.Equal(2, calls);
        Assert.Equal("http://late.host/", handler.LastRequest.RequestUri.ToString());
    }

    [Fact]
    public void Ctor_ValidatesTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeepAliveProcess(new KeepAliveOptions { Url = "http://localhost", Timeout = TimeSpan.Zero }));
    }

    [Fact, StaticLock]
    public void Ctor_WithUrl_RequestsItEveryFiveMinutes()
    {
        using (new KeepAliveProcess("http://keep.alive/nightshift/ping"))
        {
            Assert.Equal("http://keep.alive/nightshift/ping", KeepAliveProcess.Status.Url);
            Assert.Equal(TimeSpan.FromMinutes(5), KeepAliveProcess.Status.Interval);
            Assert.Null(KeepAliveProcess.Status.LastAttemptAt);
            Assert.Null(KeepAliveProcess.Status.LastSucceeded);
        }
    }

    [Fact, StaticLock]
    public void Ctor_WithOptions_PublishesTheConfiguredInterval()
    {
        using (new KeepAliveProcess(new KeepAliveOptions { Url = "http://keep.alive/", Interval = TimeSpan.FromMinutes(2) }))
        {
            Assert.Equal(TimeSpan.FromMinutes(2), KeepAliveProcess.Status.Interval);
        }
    }

    [Fact, StaticLock]
    public void ToString_ReturnsTheTypeName()
    {
        using KeepAliveProcess process = new("http://keep.alive/");
        Assert.Equal("KeepAliveProcess", process.ToString());
    }

    [Fact, StaticLock]
    public void Dispose_DisposesTheHttpClient()
    {
        StubHandler handler = new(HttpStatusCode.OK);
        KeepAliveProcess process = new(new KeepAliveOptions { Url = "http://keep.alive/" }, new HttpClient(handler));

        process.Dispose();

        Assert.True(handler.Disposed);
    }

    [Fact, StaticLock]
    public void Execute_ThrowsAnException_WhenContextIsNull()
    {
        KeepAliveProcess process = new(new KeepAliveOptions { Url = "http://keep.alive/" }, new HttpClient(new StubHandler(HttpStatusCode.OK)));

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => process.Execute(null));

        Assert.Equal("context", exception.ParamName);
    }

    [Fact, StaticLock]
    public void Execute_WaitsForTheInterval_ThenPings()
    {
        StubHandler handler = new(HttpStatusCode.OK);
        KeepAliveProcess process = new(new KeepAliveOptions { Url = "http://keep.alive/", Interval = TimeSpan.FromMilliseconds(1) }, new HttpClient(handler));

        process.Execute(new BackgroundProcessContextMock().Object);

        Assert.Equal("http://keep.alive/", handler.LastRequest.RequestUri.ToString());
        Assert.True(KeepAliveProcess.Status.LastSucceeded);
    }

    [Fact, StaticLock]
    public void Execute_ThrowsOperationCanceled_WithoutPinging_WhenStopIsRequested()
    {
        StubHandler handler = new(HttpStatusCode.OK);
        KeepAliveProcess process = new(new KeepAliveOptions { Url = "http://keep.alive/", Interval = TimeSpan.FromSeconds(1) }, new HttpClient(handler));
        BackgroundProcessContextMock context = new();
        context.StoppingTokenSource.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => process.Execute(context.Object));

        Assert.Null(handler.LastRequest);
        Assert.Null(KeepAliveProcess.Status.LastAttemptAt);
    }

    [Fact, StaticLock]
    public async Task Execute_StopsWaiting_AsSoonAsStopIsRequested_WithoutWaitingForTheShutdownTimeout()
    {
        KeepAliveProcess process = new(new KeepAliveOptions { Url = "http://keep.alive/", Interval = TimeSpan.FromMinutes(5) }, new HttpClient(new StubHandler(HttpStatusCode.OK)));
        BackgroundProcessContextMock context = new();
        Task execution = Task.Run(() => process.Execute(context.Object), TestContext.Current.CancellationToken);

        try
        {
            context.StoppingTokenSource.Cancel();
            Task finished = await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            Assert.Same(execution, finished);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        }
        finally
        {
            // Ends the wait in any case, so a failure doesn't leave the process waiting for five minutes
            context.ShutdownTokenSource.Cancel();
        }
    }

    [Fact, StaticLock]
    public void Ping_RethrowsOperationCanceled_AndKeepsTheStatus_WhenTheTokenIsCanceled()
    {
        KeepAliveProcess process = new(new KeepAliveOptions { Url = "http://keep.alive/" }, new HttpClient(new StubHandler(HttpStatusCode.OK)));

        Assert.ThrowsAny<OperationCanceledException>(() => process.Ping(new CancellationToken(true)));

        Assert.Null(KeepAliveProcess.Status.LastAttemptAt);
        Assert.Null(KeepAliveProcess.Status.LastSucceeded);
    }

    [Fact, StaticLock]
    public void Ping_StillReportsTheHostAsReached_WhenNonSuccessCodesAreLogged()
    {
        KeepAliveProcess process = new(new KeepAliveOptions { Url = "http://keep.alive/" }, new HttpClient(new StubHandler(HttpStatusCode.Unauthorized)));

        bool result = process.Ping(CancellationToken.None);

        Assert.True(result);
        Assert.True(KeepAliveProcess.Status.LastSucceeded);
        Assert.Equal("HTTP 401 Unauthorized", KeepAliveProcess.Status.LastError);
    }

    [Fact, StaticLock]
    public void Ping_RecordsAFailure_WhenFailuresAreLogged()
    {
        KeepAliveProcess process = new(new KeepAliveOptions { Url = "http://keep.alive/" }, new HttpClient(new StubHandler(new HttpRequestException("refused"))));

        bool result = process.Ping(CancellationToken.None);

        Assert.False(result);
        Assert.False(KeepAliveProcess.Status.LastSucceeded);
        Assert.Equal("refused", KeepAliveProcess.Status.LastError);
        Assert.Equal("http://keep.alive/", KeepAliveProcess.Status.Url);
    }

    [Fact, StaticLock]
    public void Ping_RecordsAFailure_WhenTheRequestTimesOut()
    {
        KeepAliveProcess process = new(
            new KeepAliveOptions { Url = "http://keep.alive/", LogFailures = false },
            new HttpClient(new StubHandler(new TaskCanceledException("timed out"))));

        bool result = process.Ping(CancellationToken.None);

        Assert.False(result);
        Assert.False(KeepAliveProcess.Status.LastSucceeded);
        Assert.Equal("timed out", KeepAliveProcess.Status.LastError);
    }

    [Fact, StaticLock]
    public void Ping_RetriesTheUrlFactory_AfterItThrows()
    {
        int calls = 0;
        StubHandler handler = new(HttpStatusCode.OK);
        KeepAliveProcess process = new(
            new KeepAliveOptions { UrlFactory = () => ++calls == 1 ? throw new InvalidOperationException("not started") : "http://late.host/" },
            new HttpClient(handler));

        bool first = process.Ping(CancellationToken.None);
        KeepAliveStatus? firstStatus = KeepAliveProcess.Status;
        bool second = process.Ping(CancellationToken.None);

        Assert.False(first);
        Assert.False(firstStatus.LastSucceeded);
        Assert.Equal("The keep-alive URL could not be resolved.", firstStatus.LastError);
        Assert.True(second);
        Assert.Equal(2, calls);
        Assert.Equal("http://late.host/", handler.LastRequest.RequestUri.ToString());
    }

    [Fact]
    public void IsHostedByIis_IsFalse_InTheTestRunner()
    {
        Assert.False(KeepAliveProcess.IsHostedByIis);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly Exception _exception;

        public StubHandler(HttpStatusCode statusCode) { _statusCode = statusCode; }
        public StubHandler(Exception exception) { _exception = exception; }

        public HttpRequestMessage LastRequest { get; private set; }
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            cancellationToken.ThrowIfCancellationRequested();
            if (_exception != null) throw _exception;
            return Task.FromResult(new HttpResponseMessage(_statusCode) { ReasonPhrase = _statusCode.ToString() });
        }
    }
}
