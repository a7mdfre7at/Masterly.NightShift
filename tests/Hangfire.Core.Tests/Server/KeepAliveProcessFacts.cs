using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Server;
using Xunit;

namespace Hangfire.Core.Tests.Server
{
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
            var handler = new StubHandler(HttpStatusCode.OK);
            var process = new KeepAliveProcess(new KeepAliveOptions { Url = "http://keep.alive/hangfire/stats" }, new HttpClient(handler));

            var result = process.Ping(CancellationToken.None);

            Assert.True(result);
            Assert.Equal("http://keep.alive/hangfire/stats", handler.LastRequest.RequestUri.ToString());
            Assert.True(KeepAliveProcess.Status.LastSucceeded);
            Assert.NotNull(KeepAliveProcess.Status.LastAttemptAt);
            Assert.Null(KeepAliveProcess.Status.LastError);
        }

        [Fact, StaticLock]
        public void Ping_CountsAnyResponseAsReachingTheHost_ButNotesNonSuccessCodes()
        {
            var process = new KeepAliveProcess(new KeepAliveOptions { Url = "http://keep.alive/", LogFailures = false }, new HttpClient(new StubHandler(HttpStatusCode.ServiceUnavailable)));

            var result = process.Ping(CancellationToken.None);

            Assert.True(result);
            Assert.True(KeepAliveProcess.Status.LastSucceeded);
            Assert.Contains("503", KeepAliveProcess.Status.LastError);
        }

        [Fact, StaticLock]
        public void Ping_RecordsAFailure_WhenTheRequestThrows()
        {
            var process = new KeepAliveProcess(new KeepAliveOptions { Url = "http://keep.alive/", LogFailures = false }, new HttpClient(new StubHandler(new HttpRequestException("no route"))));

            Assert.False(process.Ping(CancellationToken.None));
            Assert.Equal("no route", KeepAliveProcess.Status.LastError);
        }

        [Fact, StaticLock]
        public void Ping_UsesTheUrlFactory_OnceItReturnsAUrl()
        {
            var calls = 0;
            var handler = new StubHandler(HttpStatusCode.OK);
            var process = new KeepAliveProcess(new KeepAliveOptions { UrlFactory = () => ++calls < 2 ? null : "http://late.host/" }, new HttpClient(handler));

            Assert.False(process.Ping(CancellationToken.None));
            Assert.True(process.Ping(CancellationToken.None));
            Assert.True(process.Ping(CancellationToken.None));

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

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                if (_exception != null) throw _exception;
                return Task.FromResult(new HttpResponseMessage(_statusCode) { ReasonPhrase = _statusCode.ToString() });
            }
        }
    }
}
