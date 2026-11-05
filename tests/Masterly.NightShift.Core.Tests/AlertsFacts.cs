using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using System.Text.Json.Nodes;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class AlertsFacts : IDisposable
    {
        private readonly RecordingSink _sink = new RecordingSink();

        public AlertsFacts()
        {
            Alerts.Clear();
        }

        public void Dispose()
        {
            Alerts.Clear();
            GlobalJobFilters.Filters.Remove(typeof(AlertsFilter));
        }

        [Fact, StaticLock]
        public void Raise_DeliversToEverySink_AndSurvivesAFailingOne()
        {
            var failing = new Mock<IAlertSink>();
            failing.Setup(x => x.Publish(It.IsAny<Alert>())).Throws<InvalidOperationException>();
            Alerts.AddSink(failing.Object);
            Alerts.AddSink(_sink);

            Alerts.Raise(new Alert(AlertKind.JobFailed, "t", "m"));

            Assert.Single(_sink.Alerts);
            Assert.True(Alerts.HasSinks);
        }

        [Fact, StaticLock]
        public void AlertsFilter_RaisesJobFailed_WhenAJobEntersTheFailedState()
        {
            Alerts.AddSink(_sink);
            var context = new ApplyStateContextMock { NewStateObject = new FailedState(new TimeoutException("boom"), "srv") };
            context.BackgroundJob.Id = "7";

            new AlertsFilter().OnStateApplied(context.Object, context.Transaction.Object);

            var alert = Assert.Single(_sink.Alerts);
            Assert.Equal(AlertKind.JobFailed, alert.Kind);
            Assert.Equal("7", alert.JobId);
            Assert.Equal("BackgroundJobMock.SomeMethod", alert.Method);
            Assert.Equal("srv", alert.ServerId);
            Assert.Contains("TimeoutException: boom", alert.Message);
        }

        [Fact, StaticLock]
        public void AlertsFilter_IgnoresOtherStates()
        {
            Alerts.AddSink(_sink);
            var context = new ApplyStateContextMock { NewStateObject = new SucceededState(null, 0, 0) };

            new AlertsFilter().OnStateApplied(context.Object, context.Transaction.Object);

            Assert.Empty(_sink.Alerts);
        }

        [Fact, StaticLock]
        public void AlertsFilter_OnStateUnapplied_RaisesNothing()
        {
            Alerts.AddSink(_sink);
            var context = new ApplyStateContextMock { NewStateObject = new FailedState(new TimeoutException("boom"), "srv") };

            new AlertsFilter().OnStateUnapplied(context.Object, context.Transaction.Object);

            Assert.Empty(_sink.Alerts);
            context.Transaction.VerifyNoOtherCalls();
        }

        [Fact, StaticLock]
        public void UseAlerts_RegistersTheSink_AndTheFilterOnce()
        {
            var configuration = new Mock<IGlobalConfiguration>().Object;

            configuration.UseAlerts(_sink);
            configuration.UseAlerts(new RecordingSink());

            Assert.Equal(2, Alerts.Sinks.Count);
            Assert.Single(GlobalJobFilters.Filters, x => x.Instance is AlertsFilter);
        }

        [Fact, StaticLock]
        public void UseWebhookAlerts_CreatesAWebhookSink()
        {
            new Mock<IGlobalConfiguration>().Object.UseWebhookAlerts("http://hooks.local/x", o => o.Format = WebhookFormat.Slack);

            var sink = Assert.IsType<WebhookAlertSink>(Assert.Single(Alerts.Sinks));
            Assert.Equal("http://hooks.local/x", sink.Options.Url);
            Assert.Equal(WebhookFormat.Slack, sink.Options.Format);
        }

        [Fact, StaticLock]
        public void DeadlineAttribute_RaisesDeadlineMissed()
        {
            Alerts.AddSink(_sink);
            var context = new ElectStateContextMock();
            context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);
            context.ApplyContext.Connection
                .Setup(x => x.GetJobParameter("JobId", DeadlineAttribute.DeadlineAtParameter))
                .Returns(SerializationHelper.Serialize(DateTime.UtcNow.AddMinutes(-5), SerializationOption.User));

            new DeadlineAttribute(60).OnStateElection(context.Object);

            var alert = Assert.Single(_sink.Alerts);
            Assert.Equal(AlertKind.DeadlineMissed, alert.Kind);
            Assert.Equal("JobId", alert.JobId);
        }

        private sealed class RecordingSink : IAlertSink
        {
            public List<Alert> Alerts { get; } = new List<Alert>();
            public void Publish(Alert alert) => Alerts.Add(alert);
        }
    }

    public class WebhookAlertSinkFacts
    {
        [Fact]
        public void Ctor_ValidatesOptions()
        {
            Assert.Throws<ArgumentNullException>(() => new WebhookAlertSink(null));
            Assert.Throws<ArgumentException>(() => new WebhookAlertSink(new WebhookAlertOptions()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", MaxConcurrentRequests = 0 }));
        }

        [Fact]
        public async Task SendAsync_PostsJson_WithHeaders()
        {
            var handler = new StubHandler(HttpStatusCode.OK);
            var options = new WebhookAlertOptions { Url = "http://hooks.local/nightshift" };
            options.Headers["Authorization"] = "Bearer token";
            var sink = new WebhookAlertSink(options, new HttpClient(handler));
            var alert = new Alert(AlertKind.CircuitOpened, "Circuit 'api' is open", "3 failures") { Resource = "api", JobId = "9" };

            var sent = await sink.SendAsync(alert);

            Assert.True(sent);
            Assert.Equal(1, sink.Sent);
            Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
            Assert.Equal("Bearer token", handler.LastRequest.Headers.GetValues("Authorization").Single());
            var body = JsonNode.Parse(handler.LastBody);
            Assert.Equal("nightshift", body["source"].GetValue<string>());
            Assert.Equal("circuitOpened", body["kind"].GetValue<string>());
            Assert.Equal("api", body["resource"].GetValue<string>());
            Assert.Equal("9", body["jobId"].GetValue<string>());
            Assert.Null(body["serverId"]);
        }

        [Fact]
        public void CreateBody_UsesTheSlackShape_WhenConfigured()
        {
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Slack }, new HttpClient(new StubHandler(HttpStatusCode.OK)));

            var body = JsonNode.Parse(sink.CreateBody(new Alert(AlertKind.JobFailed, "Job 1 failed", "Boom") { JobId = "1", ServerId = "s" }));

            Assert.Equal("*Job 1 failed*\nBoom\nJob: 1\nServer: s", body["text"].GetValue<string>());
        }

        [Fact]
        public async Task SendAsync_CountsFailures_ForNonSuccessCodes_AndExceptions()
        {
            var rejected = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x" }, new HttpClient(new StubHandler(HttpStatusCode.BadGateway)));
            var throwing = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x" }, new HttpClient(new StubHandler(new HttpRequestException("down"))));

            Assert.False(await rejected.SendAsync(new Alert(AlertKind.JobFailed, "t", "m")));
            Assert.False(await throwing.SendAsync(new Alert(AlertKind.JobFailed, "t", "m")));
            Assert.Equal(1, rejected.Failed);
            Assert.Equal(1, throwing.Failed);
        }

        [Fact]
        public void Publish_SkipsEventsThatAreNotSubscribed()
        {
            var handler = new StubHandler(HttpStatusCode.OK);
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", Events = AlertKind.CircuitOpened }, new HttpClient(handler));

            sink.Publish(new Alert(AlertKind.JobFailed, "t", "m"));

            Assert.Null(handler.LastRequest);
            Assert.Equal(0, sink.Dropped);
        }

        [Fact]
        public async Task Publish_SendsInTheBackground()
        {
            var handler = new StubHandler(HttpStatusCode.OK);
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x" }, new HttpClient(handler));

            sink.Publish(new Alert(AlertKind.JobFailed, "t", "m"));

            Assert.True(await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task Publish_DropsTheAlert_WhenTooManyRequestsAreInFlight()
        {
            var handler = new StubHandler(HttpStatusCode.OK) { Gate = new TaskCompletionSource<bool>() };
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", MaxConcurrentRequests = 1 }, new HttpClient(handler));

            sink.Publish(new Alert(AlertKind.JobFailed, "first", "m"));
            await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            sink.Publish(new Alert(AlertKind.JobFailed, "second", "m"));
            handler.Gate.SetResult(true);

            Assert.Equal(1, sink.Dropped);
            Assert.Equal(1, handler.Requests);
        }

        [Fact]
        public void Dispose_DisposesTheHttpClient()
        {
            var handler = new StubHandler(HttpStatusCode.OK);
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x" }, new HttpClient(handler));

            sink.Dispose();

            Assert.True(handler.Disposed);
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _statusCode;
            private readonly Exception _exception;

            public StubHandler(HttpStatusCode statusCode) { _statusCode = statusCode; }
            public StubHandler(Exception exception) { _exception = exception; }

            public HttpRequestMessage LastRequest { get; private set; }
            public string LastBody { get; private set; }
            public TaskCompletionSource<bool> Received { get; } = new TaskCompletionSource<bool>();
            public TaskCompletionSource<bool> Gate { get; set; }
            public int Requests => _requests;
            public bool Disposed { get; private set; }
            private int _requests;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requests);
                LastRequest = request;
                LastBody = request.Content != null ? await request.Content.ReadAsStringAsync() : null;
                Received.TrySetResult(true);
                if (Gate != null) await Gate.Task;
                if (_exception != null) throw _exception;
                return new HttpResponseMessage(_statusCode);
            }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }
    }
}
