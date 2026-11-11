using System.Net;
using System.Text.Json.Nodes;

namespace Masterly.NightShift.Core.Tests;

public class AlertRuleFacts
{
    [Fact]
    public void Allows_LetsThroughOnlyTheKinds()
    {
        AlertRule rule = new AlertRule().ForKinds(AlertKind.JobFailed);

        Assert.True(rule.Allows(Alert(AlertKind.JobFailed, "Billing.Charge")));
        Assert.False(rule.Allows(Alert(AlertKind.CircuitOpened, null)));
    }

    [Theory]
    [InlineData("Billing.Charge", true)]
    [InlineData("BillingService.Refund", true)]
    [InlineData("Billing.Ping", false)]
    [InlineData("Reports.Build", false)]
    [InlineData("billing.charge", false)]
    public void Allows_MatchesTheMethods_WithWildcards(string method, bool expected)
    {
        AlertRule rule = new AlertRule().ForMethods("Billing*.*").ExceptMethods("*.Ping");

        Assert.Equal(expected, rule.Allows(Alert(AlertKind.JobFailed, method)));
    }

    [Fact]
    public void Allows_LetsThroughAlertsAboutNoJob_WhateverTheMethods()
    {
        AlertRule rule = new AlertRule().ForMethods("Billing.?harge");

        Assert.True(rule.Allows(Alert(AlertKind.CircuitOpened, null)));
        Assert.True(rule.Allows(Alert(AlertKind.JobFailed, "Billing.Charge")));
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void Allows_HoldsBackTheSameAlert_DuringTheCooldown()
    {
        DateTime now = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        try
        {
            JobClock.TimeProvider = new FixedClock(now);
            AlertRule rule = new AlertRule().WithCooldown(TimeSpan.FromMinutes(10));

            Assert.True(rule.Allows(Alert(AlertKind.JobFailed, "A.B")));
            Assert.False(rule.Allows(Alert(AlertKind.JobFailed, "A.B")));
            Assert.True(rule.Allows(Alert(AlertKind.JobFailed, "A.C")));

            JobClock.TimeProvider = new FixedClock(now.AddMinutes(11));
            Assert.True(rule.Allows(Alert(AlertKind.JobFailed, "A.B")));
            Assert.Equal(1, rule.Suppressed);
        }
        finally
        {
            JobClock.TimeProvider = TimeProvider.System;
        }
    }

    [Fact]
    public void Builder_RefusesBadArguments()
    {
        AlertRule rule = new();

        Assert.Throws<ArgumentNullException>("patterns", () => rule.ForMethods(null));
        Assert.Throws<ArgumentException>("patterns", () => rule.ExceptMethods(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.WithCooldown(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentNullException>("alert", () => rule.Allows(null));
    }

    [Fact]
    public void AlertRuleSink_PassesOnOnlyTheAlertsTheRuleLetsThrough()
    {
        Recording inner = new();
        AlertRuleSink sink = new(inner, new AlertRule().ForKinds(AlertKind.MonitorIncident));

        sink.Publish(Alert(AlertKind.JobFailed, "A.B"));
        sink.Publish(Alert(AlertKind.MonitorIncident, "A.B"));

        Assert.Equal(AlertKind.MonitorIncident, Assert.Single(inner.Alerts).Kind);
        Assert.Throws<ArgumentNullException>("inner", () => new AlertRuleSink(null, new AlertRule()));
        Assert.Throws<ArgumentNullException>("rule", () => new AlertRuleSink(inner, null));
    }

    [Fact, GlobalLock(Reason = "Changes the global sinks")]
    public void UseAlerts_WithARule_WrapsTheSink()
    {
        try
        {
            Recording inner = new();
            new Mock<IGlobalConfiguration>().Object.UseAlerts(inner, rule => rule.ForKinds(AlertKind.CircuitOpened));

            Alerts.Raise(Alert(AlertKind.JobFailed, "A.B"));
            Alerts.Raise(Alert(AlertKind.CircuitOpened, null));

            Assert.Equal(AlertKind.CircuitOpened, Assert.Single(inner.Alerts).Kind);
        }
        finally
        {
            Alerts.Clear();
            GlobalJobFilters.Filters.Remove(typeof(AlertsFilter));
        }
    }

    [Fact]
    public void Alert_HasAUniqueId()
    {
        Assert.NotEqual(Alert(AlertKind.JobFailed, null).Id, Alert(AlertKind.JobFailed, null).Id);
        Assert.Equal(32, Alert(AlertKind.JobFailed, null).Id.Length);
    }

    [Fact]
    public async Task Webhook_SendsTheAlertsId_AndItsMonitor()
    {
        Stub handler = new(HttpStatusCode.OK);
        using WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://hooks.local" }, new HttpClient(handler));
        Alert alert = new(AlertKind.MonitorIncident, "t", "m") { Monitor = "daily-report" };

        await sink.SendAsync(alert);

        JsonNode body = JsonNode.Parse(handler.Bodies.Single());
        Assert.Equal(alert.Id, body!["id"]!.GetValue<string>());
        Assert.Equal("daily-report", body["monitor"]!.GetValue<string>());
        Assert.Equal("monitorIncident", body["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Webhook_RendersAJsonTemplate_EscapingTheValues()
    {
        using WebhookAlertSink sink = new(new WebhookAlertOptions
        {
            Url = "http://hooks.local",
            Template = "{\"summary\":\"{{title}}\",\"details\":\"{{message}}\",\"job\":\"{{jobId}}\"}",
        });

        string body = sink.CreateBody(new Alert(AlertKind.JobFailed, "Job \"7\" failed", "line1\nline2") { JobId = "7" });

        JsonNode node = JsonNode.Parse(body);
        Assert.Equal("Job \"7\" failed", node!["summary"]!.GetValue<string>());
        Assert.Equal("line1\nline2", node["details"]!.GetValue<string>());
        Assert.Equal("7", node["job"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(WebhookFormat.Slack, "text", ":rotating_light: &lt;b&gt; on 7")]
    [InlineData(WebhookFormat.Discord, "content", ":rotating_light: <b\\> on 7")]
    public void Webhook_RendersATextTemplate_EscapingTheValuesForTheFormat(WebhookFormat format, string property, string expected)
    {
        using WebhookAlertSink sink = new(new WebhookAlertOptions
        {
            Url = "http://hooks.local",
            Format = format,
            Template = ":rotating_light: {{title}} on {{jobId}}",
        });

        string body = sink.CreateBody(new Alert(AlertKind.JobFailed, "<b>", "m") { JobId = "7" });

        Assert.Equal(expected, JsonNode.Parse(body)![property]!.GetValue<string>());
    }

    [Fact]
    public void Webhook_RendersATeamsTemplate_IntoTheCardsText()
    {
        using WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://hooks.local", Format = WebhookFormat.Teams, Template = "Job {{jobId}}: {{message}}" });

        string body = sink.CreateBody(new Alert(AlertKind.JobFailed, "t", "boom") { JobId = "7" });

        Assert.Contains("Job 7: boom", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_RetriesAFailedDelivery_AndRecordsEachAttempt()
    {
        Stub handler = new(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        WebhookAlertOptions options = new() { Url = "http://hooks.local" };
        options.RetryDelays.Clear();
        options.RetryDelays.Add(TimeSpan.FromMilliseconds(10));
        using WebhookAlertSink sink = new(options, new HttpClient(handler));
        Alert alert = new(AlertKind.JobFailed, "t", "m");

        sink.Publish(alert);
        await handler.WaitForAsync(2);
        await WaitUntilAsync(() => sink.Deliveries.Count == 2);

        IReadOnlyList<WebhookDelivery> deliveries = sink.Deliveries;
        Assert.Equal([2, 1], deliveries.Select(static x => x.Attempt));
        Assert.True(deliveries[0].Succeeded);
        Assert.False(deliveries[1].Succeeded);
        Assert.Equal(500, deliveries[1].StatusCode);
        Assert.Equal("HTTP 500", deliveries[1].Error);
        Assert.All(deliveries, x => Assert.Equal(alert.Id, x.AlertId));
        Assert.Equal(1, sink.Sent);
        Assert.Equal(1, sink.Failed);
    }

    [Fact]
    public void Webhook_SkipsAlertsItsRuleHoldsBack()
    {
        Stub handler = new(HttpStatusCode.OK);
        using WebhookAlertSink sink = new(new WebhookAlertOptions { Url = "http://hooks.local", Rule = new AlertRule().ForMethods("Billing.*") }, new HttpClient(handler));

        sink.Publish(Alert(AlertKind.JobFailed, "Reports.Build"));

        Assert.Equal(0, handler.Requests);
    }

    private static Alert Alert(AlertKind kind, string method) => new(kind, "title", "message") { Method = method };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
    }

    private sealed class Recording : IAlertSink
    {
        public List<Alert> Alerts { get; } = [];

        public void Publish(Alert alert) => Alerts.Add(alert);
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class Stub(params HttpStatusCode[] statusCodes) : HttpMessageHandler
    {
        private int _requests;

        public List<string> Bodies { get; } = [];

        public int Requests => Volatile.Read(ref _requests);

        public async Task WaitForAsync(int requests)
        {
            for (int i = 0; i < 300 && Requests < requests; i++) await Task.Delay(10);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            int number;
            lock (Bodies)
            {
                Bodies.Add(body);
                number = Bodies.Count;
            }

            Interlocked.Increment(ref _requests);
            return new HttpResponseMessage(statusCodes[Math.Min(number, statusCodes.Length) - 1]);
        }
    }
}
