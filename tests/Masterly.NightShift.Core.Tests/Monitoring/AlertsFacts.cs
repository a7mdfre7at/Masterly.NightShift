using System.Net;
using System.Text.Json.Nodes;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Core.Tests;

public class AlertsFacts : IDisposable
{
    private readonly RecordingSink _sink = new();

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
        Mock<IAlertSink> failing = new();
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
        ApplyStateContextMock context = new() { NewStateObject = new FailedState(new TimeoutException("boom"), "srv") };
        context.BackgroundJob.Id = "7";

        new AlertsFilter().OnStateApplied(context.Object, context.Transaction.Object);

        Alert alert = Assert.Single(_sink.Alerts);
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
        ApplyStateContextMock context = new() { NewStateObject = new FailedState(new TimeoutException("boom"), "srv") };
        PostCommitActions afterCommit = new();
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
        Mock<IJobFilterProvider> filterProvider = new();
        filterProvider.Setup(x => x.GetFilters(It.IsAny<Job>()))
            .Returns([new JobFilter(new AlertsFilter(), JobFilterScope.Global, null)]);
        Mock<IStateMachine> innerStateMachine = new();
        innerStateMachine.Setup(x => x.ApplyState(It.IsAny<ApplyStateContext>())).Returns<ApplyStateContext>(x => x.NewState);
        BackgroundJobStateChanger stateChanger = new(filterProvider.Object, innerStateMachine.Object);

        Mock<IStorageConnection> connection = new();
        Mock<IWriteOnlyTransaction> transaction = new();
        connection.Setup(x => x.CreateWriteTransaction()).Returns(transaction.Object);
        connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
        connection.Setup(x => x.GetJobData("7")).Returns(new JobData { State = ProcessingState.StateName, Job = Job.FromExpression(() => Console.WriteLine()) });
        transaction.SetupSequence(x => x.Commit()).Throws(new TimeoutException("commit failed")).Pass();

        StateChangeContext context = new(new Mock<JobStorage>().Object, connection.Object, "7", new FailedState(new InvalidOperationException("boom"), "srv"), ProcessingState.StateName);

        Assert.Throws<TimeoutException>(() => stateChanger.ChangeState(context));
        Assert.Empty(_sink.Alerts);

        stateChanger.ChangeState(context);

        Alert alert = Assert.Single(_sink.Alerts);
        Assert.Equal("7", alert.JobId);
    }

    [Fact, StaticLock]
    public void AlertsFilter_IgnoresOtherStates()
    {
        Alerts.AddSink(_sink);
        ApplyStateContextMock context = new() { NewStateObject = new SucceededState(null, 0, 0) };

        new AlertsFilter().OnStateApplied(context.Object, context.Transaction.Object);

        Assert.Empty(_sink.Alerts);
    }

    [Fact, StaticLock]
    public void AlertsFilter_OnStateUnapplied_RaisesNothing()
    {
        Alerts.AddSink(_sink);
        ApplyStateContextMock context = new() { NewStateObject = new FailedState(new TimeoutException("boom"), "srv") };

        new AlertsFilter().OnStateUnapplied(context.Object, context.Transaction.Object);

        Assert.Empty(_sink.Alerts);
        context.Transaction.VerifyNoOtherCalls();
    }

    [Fact, StaticLock]
    public void UseAlerts_RegistersTheSink_AndTheFilterOnce()
    {
        IGlobalConfiguration configuration = new Mock<IGlobalConfiguration>().Object;

        configuration.UseAlerts(_sink);
        configuration.UseAlerts(new RecordingSink());

        Assert.Equal(2, Alerts.Sinks.Count);
        Assert.Single(GlobalJobFilters.Filters, x => x.Instance is AlertsFilter);
    }

    [Fact, StaticLock]
    public void UseWebhookAlerts_CreatesAWebhookSink()
    {
        new Mock<IGlobalConfiguration>().Object.UseWebhookAlerts("http://hooks.local/x", o => o.Format = WebhookFormat.Slack);

        WebhookAlertSink sink = Assert.IsType<WebhookAlertSink>(Assert.Single(Alerts.Sinks));
        Assert.Equal("http://hooks.local/x", sink.Options.Url);
        Assert.Equal(WebhookFormat.Slack, sink.Options.Format);
    }

    [Fact, StaticLock]
    public void DeadlineAttribute_RaisesDeadlineMissed()
    {
        Alerts.AddSink(_sink);
        ElectStateContextMock context = new();
        context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);
        context.ApplyContext.Connection
            .Setup(x => x.GetJobParameter("JobId", DeadlineAttribute.DeadlineAtParameter))
            .Returns(SerializationHelper.Serialize(DateTime.UtcNow.AddMinutes(-5), SerializationOption.User));

        new DeadlineAttribute(60).OnStateElection(context.Object);

        Alert alert = Assert.Single(_sink.Alerts);
        Assert.Equal(AlertKind.DeadlineMissed, alert.Kind);
        Assert.Equal("JobId", alert.JobId);
    }

    private sealed class RecordingSink : IAlertSink
    {
        public List<Alert> Alerts { get; } = [];
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
        StubHandler handler = new(HttpStatusCode.OK);
        WebhookAlertOptions options = new() { Url = "http://hooks.local/nightshift" };
        options.Headers["Authorization"] = "Bearer token";
        WebhookAlertSink sink = new(options, new HttpClient(handler));
        Alert alert = new(AlertKind.CircuitOpened, "Circuit 'api' is open", "3 failures") { Resource = "api", JobId = "9" };

        bool sent = await sink.SendAsync(alert);

        Assert.True(sent);
        Assert.Equal(1, sink.Sent);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("Bearer token", handler.LastRequest.Headers.GetValues("Authorization").Single());
        JsonNode? body = JsonNode.Parse(handler.LastBody);
        Assert.Equal("nightshift", body["source"].GetValue<string>());
        Assert.Equal("circuitOpened", body["kind"].GetValue<string>());
        Assert.Equal("api", body["resource"].GetValue<string>());
        Assert.Equal("9", body["jobId"].GetValue<string>());
        Assert.Null(body["serverId"]);
    }

    [Fact]
    public void CreateBody_UsesTheSlackShape_WhenConfigured()
    {
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Slack }, new HttpClient(new StubHandler(HttpStatusCode.OK)));

        JsonNode? body = JsonNode.Parse(sink.CreateBody(new Alert(AlertKind.JobFailed, "Job 1 failed", "Boom") { JobId = "1", ServerId = "s" }));

        Assert.Equal("*Job 1 failed*\nBoom\nJob: 1\nServer: s", body["text"].GetValue<string>());
    }

    [Fact]
    public void CreateBody_EscapesSlackControlCharacters_SoExceptionTextCanNotPingOrHideLinks()
    {
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Slack }, new HttpClient(new StubHandler(HttpStatusCode.OK)));

        JsonNode? body = JsonNode.Parse(sink.CreateBody(new Alert(AlertKind.JobFailed, "A & B <failed>", "<!channel> see <https://evil.example|the docs> & fix") { Resource = "<@U123>" }));

        Assert.Equal(
            "*A &amp; B &lt;failed&gt;*\n&lt;!channel&gt; see &lt;https://evil.example|the docs&gt; &amp; fix\nResource: &lt;@U123&gt;",
            body["text"].GetValue<string>());
    }

    [Fact]
    public void CreateBody_UsesTheDiscordShape_WithMentionsTurnedOff()
    {
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Discord }, new HttpClient(new StubHandler(HttpStatusCode.OK)));

        JsonNode? body = JsonNode.Parse(sink.CreateBody(new Alert(AlertKind.JobFailed, "Job 1 failed", "@everyone Boom") { JobId = "1" }));

        Assert.Equal("**Job 1 failed**\n@everyone Boom\nJob: 1", body["content"].GetValue<string>());
        Assert.Empty(body["allowed_mentions"]["parse"].AsArray());
        Assert.Null(body["text"]);
    }

    [Fact]
    public void CreateBody_UsesAnAdaptiveCardMessage_ForTeams()
    {
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Teams }, new HttpClient(new StubHandler(HttpStatusCode.OK)));
        Alert alert = new(AlertKind.JobFailed, "Job 42 failed: EmailService.SendWelcome", "SmtpException: Mailbox unavailable")
        {
            JobId = "42",
            Method = "EmailService.SendWelcome",
            OccurredAt = new DateTime(2026, 11, 6, 9, 41, 27, DateTimeKind.Utc),
        };

        JsonNode? body = JsonNode.Parse(sink.CreateBody(alert));

        Assert.Equal("message", body["type"].GetValue<string>());
        JsonNode? attachment = Assert.Single(body["attachments"].AsArray());
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment["contentType"].GetValue<string>());
        JsonNode? card = attachment["content"];
        Assert.Equal("AdaptiveCard", card["type"].GetValue<string>());
        Assert.Equal("1.4", card["version"].GetValue<string>());
        Assert.Equal("http://adaptivecards.io/schemas/adaptive-card.json", card["$schema"].GetValue<string>());
        JsonArray blocks = card["body"].AsArray();
        Assert.Equal("Job 42 failed: EmailService.SendWelcome", blocks[0]["text"].GetValue<string>());
        Assert.Equal("Bolder", blocks[0]["weight"].GetValue<string>());
        Assert.Equal("SmtpException: Mailbox unavailable", blocks[1]["text"].GetValue<string>());
        JsonArray facts = blocks[2]["facts"].AsArray();
        Assert.Equal(new[] { "Kind", "Job", "Method", "When" }, facts.Select(static f => f["title"].GetValue<string>()).ToArray());
        Assert.Equal("42", facts[1]["value"].GetValue<string>());
        Assert.Equal("2026-11-06 09:41:27Z", facts[3]["value"].GetValue<string>());
    }

    [Fact]
    public void CreateBody_EscapesMarkdown_InTeamsCards_SoExceptionTextCanNotAddLinks()
    {
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Teams }, new HttpClient(new StubHandler(HttpStatusCode.OK)));

        JsonNode? body = JsonNode.Parse(sink.CreateBody(new Alert(AlertKind.JobFailed, "*Job* failed", "see [the docs](https://evil.example) <at>all</at> \\x") { Resource = "a_b" }));

        JsonArray blocks = body["attachments"][0]["content"]["body"].AsArray();
        Assert.Equal("\\*Job\\* failed", blocks[0]["text"].GetValue<string>());
        Assert.Equal("see \\[the docs\\](https://evil.example) \\<at\\>all\\</at\\> \\\\x", blocks[1]["text"].GetValue<string>());
        Assert.Equal("a\\_b", blocks[2]["facts"].AsArray().Single(static f => f["title"].GetValue<string>() == "Resource")["value"].GetValue<string>());
    }

    [Fact]
    public void CreateBody_EscapesMarkdown_InDiscordMessages_SoExceptionTextCanNotAddLinksOrFormatting()
    {
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Discord }, new HttpClient(new StubHandler(HttpStatusCode.OK)));
        Alert alert = new(AlertKind.JobFailed, "*Job* ~~1~~ failed", "# Big\n> quote see [the docs](https://evil.example) ||spoiler|| `code` \\x")
        {
            JobId = "a_b",
            Resource = "res(1)",
            ServerId = "web#1",
        };

        JsonNode? body = JsonNode.Parse(sink.CreateBody(alert));

        Assert.Equal(
            "**\\*Job\\* \\~\\~1\\~\\~ failed**\n" +
            "\\# Big\n\\> quote see \\[the docs\\]\\(https://evil.example\\) \\|\\|spoiler\\|\\| \\`code\\` \\\\x\n" +
            "Job: a\\_b\nResource: res\\(1\\)\nServer: web\\#1",
            body["content"].GetValue<string>());
    }

    [Fact]
    public void CreateBody_CutsDiscordMessages_ToTwoThousandCharacters()
    {
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", Format = WebhookFormat.Discord }, new HttpClient(new StubHandler(HttpStatusCode.OK)));

        JsonNode? body = JsonNode.Parse(sink.CreateBody(new Alert(AlertKind.JobFailed, "Job 1 failed", new string('x', 5000))));

        string content = body["content"].GetValue<string>();
        Assert.Equal(2000, content.Length);
        Assert.EndsWith("\u2026", content);
    }

    [Fact]
    public async Task SendAsync_CountsFailures_ForNonSuccessCodes_AndExceptions()
    {
        WebhookAlertSink rejected = new(new WebhookAlertOptions { Url = "http://x" }, new HttpClient(new StubHandler(HttpStatusCode.BadGateway)));
        WebhookAlertSink throwing = new(new WebhookAlertOptions { Url = "http://x" }, new HttpClient(new StubHandler(new HttpRequestException("down"))));

        Assert.False(await rejected.SendAsync(new Alert(AlertKind.JobFailed, "t", "m")));
        Assert.False(await throwing.SendAsync(new Alert(AlertKind.JobFailed, "t", "m")));
        Assert.Equal(1, rejected.Failed);
        Assert.Equal(1, throwing.Failed);
    }

    [Fact]
    public void Publish_SkipsEventsThatAreNotSubscribed()
    {
        StubHandler handler = new(HttpStatusCode.OK);
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", Events = AlertKind.CircuitOpened }, new HttpClient(handler));

        sink.Publish(new Alert(AlertKind.JobFailed, "t", "m"));

        Assert.Null(handler.LastRequest);
        Assert.Equal(0, sink.Dropped);
    }

    [Fact]
    public async Task Publish_SendsInTheBackground()
    {
        StubHandler handler = new(HttpStatusCode.OK);
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x" }, new HttpClient(handler));

        sink.Publish(new Alert(AlertKind.JobFailed, "t", "m"));

        Assert.True(await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Publish_DropsTheAlert_WhenTooManyRequestsAreInFlight()
    {
        StubHandler handler = new(HttpStatusCode.OK) { Gate = new TaskCompletionSource<bool>() };
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", MaxConcurrentRequests = 1 }, new HttpClient(handler));

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
        StubHandler handler = new(HttpStatusCode.OK) { Gate = new TaskCompletionSource<bool>(), GatedRequests = 1 };
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", MaxConcurrentRequests = 1 }, new HttpClient(handler));

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
        HttpClient client = new(new StubHandler(HttpStatusCode.OK)) { Timeout = TimeSpan.FromSeconds(30) };
        (await client.GetAsync("http://hooks.local/warm-up", TestContext.Current.CancellationToken)).Dispose();

        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://hooks.local/x", Timeout = TimeSpan.FromSeconds(5) }, client);

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        Assert.True(await sink.SendAsync(new Alert(AlertKind.JobFailed, "t", "m")));
    }

    [Fact]
    public async Task SendAsync_GivesUpAfterTheTimeout_EvenWhenTheClientHasNone()
    {
        StubHandler handler = new(HttpStatusCode.OK) { Gate = new TaskCompletionSource<bool>() };
        HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://hooks.local/x", Timeout = TimeSpan.FromMilliseconds(200) }, client);
        RecordingLogProvider recording = new();

        bool sent;
        using (recording.Use())
        {
            sent = await sink.SendAsync(new Alert(AlertKind.JobFailed, "t", "m")).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.False(sent);
        Assert.Equal(1, sink.Failed);
        RecordingLogProvider.LogEntry entry = Assert.Single(recording.Entries, x => x.Logger == typeof(WebhookAlertSink).FullName);
        Assert.Contains("no answer within 00:00:00.2000000", entry.Message);
    }

    [Theory]
    [InlineData(SlackUrl, "https://hooks.slack.com/... (hash ")]
    [InlineData("https://ops.example.com:8443/api/alerts?token=s3cr3tT0k3n", "https://ops.example.com:8443/... (hash ")]
    [InlineData("https://user:s3cr3tT0k3n@ops.example.com/", "https://ops.example.com/... (hash ")]
    public void GetLoggableUrl_KeepsOnlyTheSchemeHostAndPort_AndAShortHash(string url, string expectedStart)
    {
        string loggable = WebhookAlertSink.GetLoggableUrl(url);

        Assert.StartsWith(expectedStart, loggable);
        Assert.DoesNotContain("s3cr3tT0k3n", loggable);
        Assert.Matches(@"\(hash [0-9a-f]{6}\)$", loggable);
    }

    [Fact]
    public void GetLoggableUrl_TellsWebhooksOnTheSameHostApart()
    {
        string first = WebhookAlertSink.GetLoggableUrl("https://hooks.slack.com/services/T1/B1/aaaaaaaaaaaaaaaaaaaaaaaa");
        string second = WebhookAlertSink.GetLoggableUrl("https://hooks.slack.com/services/T1/B2/bbbbbbbbbbbbbbbbbbbbbbbb");

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
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = SlackUrl }, new HttpClient(new StubHandler(HttpStatusCode.Forbidden)));
        RecordingLogProvider recording = new();

        using (recording.Use())
        {
            await sink.SendAsync(new Alert(AlertKind.JobFailed, "t", "m"));
        }

        RecordingLogProvider.LogEntry entry = Assert.Single(recording.Entries, x => x.Logger == typeof(WebhookAlertSink).FullName);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Contains("https://hooks.slack.com/... (hash ", entry.Message);
        Assert.Contains("HTTP 403", entry.Message);
        Assert.DoesNotContain("s3cr3tT0k3n", entry.Message);
        Assert.DoesNotContain("/services/", entry.Message);
    }

    [Fact]
    public async Task SendAsync_LogsAFailedRequest_WithoutTheSecretPartOfTheUrl()
    {
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = SlackUrl }, new HttpClient(new StubHandler(new HttpRequestException("down"))));
        RecordingLogProvider recording = new();

        using (recording.Use())
        {
            await sink.SendAsync(new Alert(AlertKind.JobFailed, "t", "m"));
        }

        RecordingLogProvider.LogEntry entry = Assert.Single(recording.Entries, x => x.Logger == typeof(WebhookAlertSink).FullName);
        Assert.Contains("https://hooks.slack.com/... (hash ", entry.Message);
        Assert.DoesNotContain("s3cr3tT0k3n", entry.Message);
        Assert.IsType<HttpRequestException>(entry.Exception);
    }

    [Fact]
    public void Dispose_LeavesAClientPassedIn_ToItsOwner()
    {
        StubHandler handler = new(HttpStatusCode.OK);
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x" }, new HttpClient(handler));

        sink.Dispose();

        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_WaitsForARequestInFlight_ThenReturns()
    {
        StubHandler handler = new(HttpStatusCode.OK) { Gate = new TaskCompletionSource<bool>() };
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", MaxConcurrentRequests = 2 }, new HttpClient(handler));

        sink.Publish(new Alert(AlertKind.JobFailed, "t", "m"));
        await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Task disposing = sink.DisposeAsync().AsTask();

        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(disposing.IsCompleted);

        handler.Gate.SetResult(true);
        await disposing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The request finished before DisposeAsync returned, and was counted as sent
        Assert.Equal(1, sink.Sent);
    }

    [Fact]
    public async Task DisposeAsync_StopsWaiting_AfterTheRequestTimeout()
    {
        StubHandler handler = new(HttpStatusCode.OK) { Gate = new TaskCompletionSource<bool>() };
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", Timeout = TimeSpan.FromMilliseconds(300) }, new HttpClient(handler));

        sink.Publish(new Alert(AlertKind.JobFailed, "t", "m"));
        await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await sink.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(0, sink.Sent);
    }

    [Fact]
    public async Task Dispose_CancelsARequestInFlight_AtOnce_InsteadOfWaitingForIt()
    {
        StubHandler handler = new(HttpStatusCode.OK) { Gate = new TaskCompletionSource<bool>() };
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", Timeout = TimeSpan.FromMinutes(1) }, new HttpClient(handler));

        sink.Publish(new Alert(AlertKind.JobFailed, "t", "m"));
        await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        sink.Dispose();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {stopwatch.Elapsed}");

        // The canceled request records its failure on its own thread, which may come after Dispose returned
        await WaitUntilAsync(() => sink.Failed == 1);
        Assert.Equal(0, sink.Sent);
        Assert.Equal(1, sink.Failed);
        Assert.Equal("The sink was disposed", Assert.Single(sink.Deliveries).Error);
    }

    [Fact]
    public async Task Dispose_GivesUpAnAlertWaitingForARetry()
    {
        StubHandler handler = new(HttpStatusCode.InternalServerError);
        WebhookAlertOptions options = new() { Url = "http://x" };
        options.RetryDelays.Clear();
        options.RetryDelays.Add(TimeSpan.FromMinutes(10));
        WebhookAlertSink sink = new(options, new HttpClient(handler));

        sink.Publish(new Alert(AlertKind.JobFailed, "t", "m"));
        await WaitUntilAsync(() => sink.PendingRetries == 1);

        sink.Dispose();
        await WaitUntilAsync(() => sink.PendingRetries == 0);

        Assert.Equal(0, sink.PendingRetries);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://hooks.local/x")]
    [InlineData("file:///c:/hooks")]
    public void Ctor_RefusesAUrlThatIsNotAnAbsoluteHttpUrl(string url)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>("options", () => new WebhookAlertSink(new WebhookAlertOptions { Url = url }));

        Assert.DoesNotContain(url, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctor_RefusesANegativeRetryDelay()
    {
        WebhookAlertOptions options = new() { Url = "http://x" };
        options.RetryDelays.Add(TimeSpan.FromSeconds(-1));

        Assert.Throws<ArgumentOutOfRangeException>("options", () => new WebhookAlertSink(options));
    }

    [Fact]
    public void OwnClient_DoesNotFollowRedirects_SoTheHeadersStayWithTheConfiguredHost()
    {
        using SocketsHttpHandler handler = WebhookAlertSink.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task SendAsync_UsesTheOptionsAsTheyWereWhenTheSinkWasCreated()
    {
        StubHandler handler = new(HttpStatusCode.OK);
        WebhookAlertOptions options = new() { Url = "http://hooks.local/a" };
        options.Headers["Authorization"] = "Bearer first";
        WebhookAlertSink sink = new(options, new HttpClient(handler));

        options.Url = "http://elsewhere.local/b";
        options.Headers["Authorization"] = "Bearer second";
        options.Headers["X-Extra"] = "1";
        options.Timeout = TimeSpan.FromMilliseconds(1);
        await sink.SendAsync(new Alert(AlertKind.JobFailed, "t", "m"));

        Assert.Equal(new Uri("http://hooks.local/a"), handler.LastRequest.RequestUri);
        Assert.Equal("Bearer first", handler.LastRequest.Headers.GetValues("Authorization").Single());
        Assert.False(handler.LastRequest.Headers.Contains("X-Extra"));
        Assert.Equal(1, sink.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 1)]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.NotFound, 1)]
    [InlineData(HttpStatusCode.Found, 1)]
    [InlineData(HttpStatusCode.RequestTimeout, 2)]
    [InlineData(HttpStatusCode.TooManyRequests, 2)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 2)]
    public async Task Publish_RetriesOnlyTransientFailures(HttpStatusCode statusCode, int expectedRequests)
    {
        StubHandler handler = new(statusCode);
        WebhookAlertOptions options = new() { Url = "http://x" };
        options.RetryDelays.Clear();
        options.RetryDelays.Add(TimeSpan.FromMilliseconds(10));
        WebhookAlertSink sink = new(options, new HttpClient(handler));

        sink.Publish(new Alert(AlertKind.JobFailed, "t", "m"));
        await WaitUntilAsync(() => sink.Deliveries.Count == expectedRequests);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(expectedRequests, handler.Requests);
        Assert.Equal(0, sink.PendingRetries);
    }

    [Fact]
    public async Task Publish_DoesNotRetry_WhenTooManyAlertsWaitForARetry()
    {
        StubHandler handler = new(HttpStatusCode.InternalServerError);
        WebhookAlertOptions options = new() { Url = "http://x", MaxConcurrentRequests = 1 };
        options.RetryDelays.Clear();
        options.RetryDelays.Add(TimeSpan.FromMinutes(10));
        WebhookAlertSink sink = new(options, new HttpClient(handler));

        sink.Publish(new Alert(AlertKind.JobFailed, "first", "m"));
        await WaitUntilAsync(() => sink.PendingRetries == 1);
        sink.Publish(new Alert(AlertKind.JobFailed, "second", "m"));
        await WaitUntilAsync(() => sink.Dropped == 1);

        Assert.Equal(1, sink.Dropped);
        Assert.Equal(2, handler.Requests);
        Assert.Equal(1, sink.PendingRetries);
        sink.Dispose();
    }

    [Theory]
    [InlineData(60, null, 60)]
    [InlineData(60, 5, 60)]
    [InlineData(60, 120, 120)]
    [InlineData(60, 3600, 600)]
    public void GetRetryDelay_WaitsAtLeastAsLongAsRetryAfterAsks_UpToTenMinutes(int configuredSeconds, int? retryAfterSeconds, int expectedSeconds)
    {
        TimeSpan delay = WebhookAlertSink.GetRetryDelay(
            TimeSpan.FromSeconds(configuredSeconds),
            retryAfterSeconds.HasValue ? TimeSpan.FromSeconds(retryAfterSeconds.Value) : null);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public async Task Publish_WaitsForRetryAfter_BeforeTheRetry()
    {
        StubHandler handler = new(HttpStatusCode.TooManyRequests) { RetryAfter = TimeSpan.FromSeconds(1) };
        WebhookAlertOptions options = new() { Url = "http://x" };
        options.RetryDelays.Clear();
        options.RetryDelays.Add(TimeSpan.FromMilliseconds(10));
        WebhookAlertSink sink = new(options, new HttpClient(handler));

        sink.Publish(new Alert(AlertKind.JobFailed, "t", "m"));
        await WaitUntilAsync(() => handler.Requests == 1);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Requests);
        await WaitUntilAsync(() => handler.Requests == 2);
        Assert.Equal(2, handler.Requests);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Dispose_CancelsARequestStillRunning_WithAnInfiniteTimeout_AndAClientPassedIn()
    {
        StubHandler handler = new(HttpStatusCode.OK) { Gate = new TaskCompletionSource<bool>() };
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x", Timeout = Timeout.InfiniteTimeSpan }, new HttpClient(handler));

        // Not counted against MaxConcurrentRequests, so Dispose doesn't wait for it
        Task<bool> sending = sink.SendAsync(new Alert(AlertKind.JobFailed, "t", "m"));
        await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        sink.Dispose();

        Assert.False(await sending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(1, sink.Failed);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task Publish_SendsNothing_AfterDispose_AndCountsNoDrop()
    {
        StubHandler handler = new(HttpStatusCode.OK);
        WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://x" }, new HttpClient(handler));

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
        public int GatedRequests { get; set; } = int.MaxValue;
        public TimeSpan? RetryAfter { get; set; }
        public int Requests => _requests;
        public bool Disposed { get; private set; }
        private int _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int number = Interlocked.Increment(ref _requests);
            LastRequest = request;
            LastBody = request.Content != null ? await request.Content.ReadAsStringAsync() : null;
            Received.TrySetResult(true);
            if (Gate != null && number <= GatedRequests)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            if (_exception != null)
            {
                throw _exception;
            }

            HttpResponseMessage response = new(_statusCode);
            if (RetryAfter is { } retryAfter)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
            }

            return response;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
