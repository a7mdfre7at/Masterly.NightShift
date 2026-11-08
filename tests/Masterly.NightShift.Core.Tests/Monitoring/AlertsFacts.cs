using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
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
        public void AlertsFilter_WaitsForTheCommit_WhenTheStateChangeRunsActionsAfterIt()
        {
            Alerts.AddSink(_sink);
            var context = new ApplyStateContextMock { NewStateObject = new FailedState(new TimeoutException("boom"), "srv") };
            var afterCommit = new PostCommitActions();
            context.Object.AfterCommit = afterCommit;

            new AlertsFilter().OnStateApplied(context.Object, context.Transaction.Object);
            Assert.Empty(_sink.Alerts);

            afterCommit.Run();
            Assert.Single(_sink.Alerts);
        }

        [Fact, StaticLock]
        public void AlertsFilter_AlertsOnce_WhenTheCommitFailsAndTheStateChangeIsRetried()
        {
            Alerts.AddSink(_sink);
            var filterProvider = new Mock<IJobFilterProvider>();
            filterProvider.Setup(x => x.GetFilters(It.IsAny<Job>()))
                .Returns(new[] { new JobFilter(new AlertsFilter(), JobFilterScope.Global, null) });
            var innerStateMachine = new Mock<IStateMachine>();
            innerStateMachine.Setup(x => x.ApplyState(It.IsAny<ApplyStateContext>())).Returns<ApplyStateContext>(x => x.NewState);
            var stateChanger = new BackgroundJobStateChanger(filterProvider.Object, innerStateMachine.Object);

            var connection = new Mock<IStorageConnection>();
            var transaction = new Mock<IWriteOnlyTransaction>();
            connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
            connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
            connection.Setup(x => x.GetJobData("7")).Returns(new JobData { State = ProcessingState.StateName, Job = Job.FromExpression(() => Console.WriteLine()) });
            transaction.SetupSequence(x => x.Commit()).Throws(new TimeoutException("commit failed")).Pass();

            var context = new StateChangeContext(new Mock<JobStorage>().Object, connection.Object, "7", new FailedState(new InvalidOperationException("boom"), "srv"), ProcessingState.StateName);

            Assert.Throws<TimeoutException>(() => stateChanger.ChangeState(context));
            Assert.Empty(_sink.Alerts);

            stateChanger.ChangeState(context);

            var alert = Assert.Single(_sink.Alerts);
            Assert.Equal("7", alert.JobId);
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
        private const string SlackUrl = "https://hooks.slack.com/services/T0001/B0002/s3cr3tT0k3n";

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
        public void CreateBody_EscapesSlackControlCharacters_SoExceptionTextCanNotPingOrHideLinks()
        {
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Slack }, new HttpClient(new StubHandler(HttpStatusCode.OK)));

            var body = JsonNode.Parse(sink.CreateBody(new Alert(AlertKind.JobFailed, "A & B <failed>", "<!channel> see <https://evil.example|the docs> & fix") { Resource = "<@U123>" }));

            Assert.Equal(
                "*A &amp; B &lt;failed&gt;*\n&lt;!channel&gt; see &lt;https://evil.example|the docs&gt; &amp; fix\nResource: &lt;@U123&gt;",
                body["text"].GetValue<string>());
        }

        [Fact]
        public void CreateBody_UsesTheDiscordShape_WithMentionsTurnedOff()
        {
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Discord }, new HttpClient(new StubHandler(HttpStatusCode.OK)));

            var body = JsonNode.Parse(sink.CreateBody(new Alert(AlertKind.JobFailed, "Job 1 failed", "@everyone Boom") { JobId = "1" }));

            Assert.Equal("**Job 1 failed**\n@everyone Boom\nJob: 1", body["content"].GetValue<string>());
            Assert.Empty(body["allowed_mentions"]["parse"].AsArray());
            Assert.Null(body["text"]);
        }

        [Fact]
        public void CreateBody_UsesAnAdaptiveCardMessage_ForTeams()
        {
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Teams }, new HttpClient(new StubHandler(HttpStatusCode.OK)));
            var alert = new Alert(AlertKind.JobFailed, "Job 42 failed: EmailService.SendWelcome", "SmtpException: Mailbox unavailable")
            {
                JobId = "42",
                Method = "EmailService.SendWelcome",
                OccurredAt = new DateTime(2026, 11, 6, 9, 41, 27, DateTimeKind.Utc),
            };

            var body = JsonNode.Parse(sink.CreateBody(alert));

            Assert.Equal("message", body["type"].GetValue<string>());
            var attachment = Assert.Single(body["attachments"].AsArray());
            Assert.Equal("application/vnd.microsoft.card.adaptive", attachment["contentType"].GetValue<string>());
            var card = attachment["content"];
            Assert.Equal("AdaptiveCard", card["type"].GetValue<string>());
            Assert.Equal("1.4", card["version"].GetValue<string>());
            Assert.Equal("http://adaptivecards.io/schemas/adaptive-card.json", card["$schema"].GetValue<string>());
            var blocks = card["body"].AsArray();
            Assert.Equal("Job 42 failed: EmailService.SendWelcome", blocks[0]["text"].GetValue<string>());
            Assert.Equal("Bolder", blocks[0]["weight"].GetValue<string>());
            Assert.Equal("SmtpException: Mailbox unavailable", blocks[1]["text"].GetValue<string>());
            var facts = blocks[2]["facts"].AsArray();
            Assert.Equal(new[] { "Kind", "Job", "Method", "When" }, facts.Select(static f => f["title"].GetValue<string>()).ToArray());
            Assert.Equal("42", facts[1]["value"].GetValue<string>());
            Assert.Equal("2026-11-06 09:41:27Z", facts[3]["value"].GetValue<string>());
        }

        [Fact]
        public void CreateBody_EscapesMarkdown_InTeamsCards_SoExceptionTextCanNotAddLinks()
        {
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Teams }, new HttpClient(new StubHandler(HttpStatusCode.OK)));

            var body = JsonNode.Parse(sink.CreateBody(new Alert(AlertKind.JobFailed, "*Job* failed", "see [the docs](https://evil.example) <at>all</at> \\x") { Resource = "a_b" }));

            var blocks = body["attachments"][0]["content"]["body"].AsArray();
            Assert.Equal("\\*Job\\* failed", blocks[0]["text"].GetValue<string>());
            Assert.Equal("see \\[the docs\\](https://evil.example) \\<at\\>all\\</at\\> \\\\x", blocks[1]["text"].GetValue<string>());
            Assert.Equal("a\\_b", blocks[2]["facts"].AsArray().Single(static f => f["title"].GetValue<string>() == "Resource")["value"].GetValue<string>());
        }

        [Fact]
        public void CreateBody_EscapesMarkdown_InDiscordMessages_SoExceptionTextCanNotAddLinksOrFormatting()
        {
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Discord }, new HttpClient(new StubHandler(HttpStatusCode.OK)));
            var alert = new Alert(AlertKind.JobFailed, "*Job* ~~1~~ failed", "# Big\n> quote see [the docs](https://evil.example) ||spoiler|| `code` \\x")
            {
                JobId = "a_b",
                Resource = "res(1)",
                ServerId = "web#1",
            };

            var body = JsonNode.Parse(sink.CreateBody(alert));

            Assert.Equal(
                "**\\*Job\\* \\~\\~1\\~\\~ failed**\n" +
                "\\# Big\n\\> quote see \\[the docs\\]\\(https://evil.example\\) \\|\\|spoiler\\|\\| \\`code\\` \\\\x\n" +
                "Job: a\\_b\nResource: res\\(1\\)\nServer: web\\#1",
                body["content"].GetValue<string>());
        }

        [Fact]
        public void CreateBody_CutsDiscordMessages_ToTwoThousandCharacters()
        {
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Discord }, new HttpClient(new StubHandler(HttpStatusCode.OK)));

            var body = JsonNode.Parse(sink.CreateBody(new Alert(AlertKind.JobFailed, "Job 1 failed", new string('x', 5000))));

            var content = body["content"].GetValue<string>();
            Assert.Equal(2000, content.Length);
            Assert.EndsWith("\u2026", content);
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
        public async Task SendAsync_DoesNotFreeASlotThatPublishHolds()
        {
            // Only the published request waits; the direct one answers at once
            var handler = new StubHandler(HttpStatusCode.OK) { Gate = new TaskCompletionSource<bool>(), GatedRequests = 1 };
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", MaxConcurrentRequests = 1 }, new HttpClient(handler));

            sink.Publish(new Alert(AlertKind.JobFailed, "published", "m"));
            await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(await sink.SendAsync(new Alert(AlertKind.JobFailed, "direct", "m")));
            sink.Publish(new Alert(AlertKind.JobFailed, "second", "m"));
            handler.Gate.SetResult(true);

            Assert.Equal(1, sink.Dropped);
            Assert.Equal(2, handler.Requests);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenTheTimeoutIsNotPositive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", Timeout = TimeSpan.Zero }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", Timeout = TimeSpan.FromSeconds(-5) }));
        }

        [Fact]
        public async Task Ctor_LeavesTheTimeoutOfTheCallersClientAlone_EvenWhenTheClientAlreadySentRequests()
        {
            var client = new HttpClient(new StubHandler(HttpStatusCode.OK)) { Timeout = TimeSpan.FromSeconds(30) };
            (await client.GetAsync("http://hooks.local/warm-up", TestContext.Current.CancellationToken)).Dispose();

            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://hooks.local/x", Timeout = TimeSpan.FromSeconds(5) }, client);

            Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
            Assert.True(await sink.SendAsync(new Alert(AlertKind.JobFailed, "t", "m")));
        }

        [Fact]
        public async Task SendAsync_GivesUpAfterTheTimeout_EvenWhenTheClientHasNone()
        {
            var handler = new StubHandler(HttpStatusCode.OK) { Gate = new TaskCompletionSource<bool>() };
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://hooks.local/x", Timeout = TimeSpan.FromMilliseconds(200) }, client);
            var recording = new RecordingLogProvider();

            bool sent;
            using (recording.Use())
            {
                sent = await sink.SendAsync(new Alert(AlertKind.JobFailed, "t", "m")).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }

            Assert.False(sent);
            Assert.Equal(1, sink.Failed);
            var entry = Assert.Single(recording.Entries, x => x.Logger == typeof(WebhookAlertSink).FullName);
            Assert.Contains("no answer within 00:00:00.2000000", entry.Message);
        }

        [Theory]
        [InlineData(SlackUrl, "https://hooks.slack.com/... (hash ")]
        [InlineData("https://ops.example.com:8443/api/alerts?token=s3cr3tT0k3n", "https://ops.example.com:8443/... (hash ")]
        [InlineData("https://user:s3cr3tT0k3n@ops.example.com/", "https://ops.example.com/... (hash ")]
        public void GetLoggableUrl_KeepsOnlyTheSchemeHostAndPort_AndAShortHash(string url, string expectedStart)
        {
            var loggable = WebhookAlertSink.GetLoggableUrl(url);

            Assert.StartsWith(expectedStart, loggable);
            Assert.DoesNotContain("s3cr3tT0k3n", loggable);
            Assert.Matches(@"\(hash [0-9a-f]{6}\)$", loggable);
        }

        [Fact]
        public void GetLoggableUrl_TellsWebhooksOnTheSameHostApart()
        {
            var first = WebhookAlertSink.GetLoggableUrl("https://hooks.slack.com/services/T1/B1/aaaaaaaaaaaaaaaaaaaaaaaa");
            var second = WebhookAlertSink.GetLoggableUrl("https://hooks.slack.com/services/T1/B2/bbbbbbbbbbbbbbbbbbbbbbbb");

            Assert.NotEqual(first, second);
            Assert.Equal(first, WebhookAlertSink.GetLoggableUrl("https://hooks.slack.com/services/T1/B1/aaaaaaaaaaaaaaaaaaaaaaaa"));
        }

        [Fact]
        public void GetLoggableUrl_ShowsAUrlWithoutPathAsItIs_AndHidesAnInvalidOne()
        {
            Assert.Equal("https://alerts.example.com", WebhookAlertSink.GetLoggableUrl("https://alerts.example.com"));
            Assert.Equal("(invalid URL)", WebhookAlertSink.GetLoggableUrl("not a url s3cr3tT0k3n"));
        }

        [Fact]
        public async Task SendAsync_LogsARejection_WithoutTheSecretPartOfTheUrl()
        {
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = SlackUrl }, new HttpClient(new StubHandler(HttpStatusCode.Forbidden)));
            var recording = new RecordingLogProvider();

            using (recording.Use())
            {
                await sink.SendAsync(new Alert(AlertKind.JobFailed, "t", "m"));
            }

            var entry = Assert.Single(recording.Entries, x => x.Logger == typeof(WebhookAlertSink).FullName);
            Assert.Equal(LogLevel.Warn, entry.Level);
            Assert.Contains("https://hooks.slack.com/... (hash ", entry.Message);
            Assert.Contains("HTTP 403", entry.Message);
            Assert.DoesNotContain("s3cr3tT0k3n", entry.Message);
            Assert.DoesNotContain("/services/", entry.Message);
        }

        [Fact]
        public async Task SendAsync_LogsAFailedRequest_WithoutTheSecretPartOfTheUrl()
        {
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = SlackUrl }, new HttpClient(new StubHandler(new HttpRequestException("down"))));
            var recording = new RecordingLogProvider();

            using (recording.Use())
            {
                await sink.SendAsync(new Alert(AlertKind.JobFailed, "t", "m"));
            }

            var entry = Assert.Single(recording.Entries, x => x.Logger == typeof(WebhookAlertSink).FullName);
            Assert.Contains("https://hooks.slack.com/... (hash ", entry.Message);
            Assert.DoesNotContain("s3cr3tT0k3n", entry.Message);
            Assert.IsType<HttpRequestException>(entry.Exception);
        }

        [Fact]
        public void Dispose_LeavesAClientPassedIn_ToItsOwner()
        {
            var handler = new StubHandler(HttpStatusCode.OK);
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x" }, new HttpClient(handler));

            sink.Dispose();

            Assert.False(handler.Disposed);
        }

        [Fact]
        public async Task Dispose_WaitsForARequestInFlight_ThenReturns()
        {
            var handler = new StubHandler(HttpStatusCode.OK) { Gate = new TaskCompletionSource<bool>() };
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", MaxConcurrentRequests = 2 }, new HttpClient(handler));

            sink.Publish(new Alert(AlertKind.JobFailed, "t", "m"));
            await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var disposing = Task.Run(sink.Dispose, TestContext.Current.CancellationToken);

            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(disposing.IsCompleted);

            handler.Gate.SetResult(true);
            await disposing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // The request finished before Dispose returned, and was counted as sent
            Assert.Equal(1, sink.Sent);
        }

        [Fact]
        public async Task Dispose_StopsWaiting_AfterTheRequestTimeout()
        {
            var handler = new StubHandler(HttpStatusCode.OK) { Gate = new TaskCompletionSource<bool>() };
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", Timeout = TimeSpan.FromMilliseconds(300) }, new HttpClient(handler));

            sink.Publish(new Alert(AlertKind.JobFailed, "t", "m"));
            await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            await Task.Run(sink.Dispose, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(0, sink.Sent);
        }

        [Fact]
        public async Task Dispose_CancelsARequestStillRunning_WithAnInfiniteTimeout_AndAClientPassedIn()
        {
            var handler = new StubHandler(HttpStatusCode.OK) { Gate = new TaskCompletionSource<bool>() };
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x", Timeout = Timeout.InfiniteTimeSpan }, new HttpClient(handler));

            // Not counted against MaxConcurrentRequests, so Dispose doesn't wait for it
            var sending = sink.SendAsync(new Alert(AlertKind.JobFailed, "t", "m"));
            await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            sink.Dispose();

            Assert.False(await sending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(1, sink.Failed);
            Assert.False(handler.Disposed);
        }

        [Fact]
        public async Task Publish_SendsNothing_AfterDispose_AndCountsNoDrop()
        {
            var handler = new StubHandler(HttpStatusCode.OK);
            var sink = new WebhookAlertSink(new WebhookAlertOptions { Url = "http://x" }, new HttpClient(handler));

            sink.Dispose();
            sink.Publish(new Alert(AlertKind.JobFailed, "t", "m"));
            await Task.Delay(100, TestContext.Current.CancellationToken);

            Assert.Equal(0, handler.Requests);
            Assert.Equal(0, sink.Dropped);
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
            public int GatedRequests { get; set; } = Int32.MaxValue;
            public int Requests => _requests;
            public bool Disposed { get; private set; }
            private int _requests;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var number = Interlocked.Increment(ref _requests);
                LastRequest = request;
                LastBody = request.Content != null ? await request.Content.ReadAsStringAsync() : null;
                Received.TrySetResult(true);
                if (Gate != null && number <= GatedRequests) await Gate.Task.WaitAsync(cancellationToken);
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
