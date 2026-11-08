using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class ServersPageFacts
    {
        private readonly PageRenderer _renderer = new PageRenderer();

        [Fact]
        public void Render_ShowsTheNoServersWarning_AndHostingDetails_WhenNoServerIsRegistered()
        {
            var html = _renderer.Render(new ServersPage());

            Assert.Contains(H(Strings.ServersPage_NoServers), html);
            Assert.DoesNotContain("<table class=\"table\"", html);
            Assert.Contains("id=\"servers-hosting\"", html);
            Assert.Contains(H(Strings.ServersPage_Hosting_Uptime), html);
            Assert.Contains("invoker-stats", html);
            Assert.DoesNotContain(H(Strings.ServersPage_Hosting_IisWarning_Html), html);
        }

        [Fact]
        public void Render_MarksEachServer_ByHowLongAgoItsHeartbeatWas()
        {
            var now = DateTime.UtcNow;
            SetupServers(
                Server("fresh-server", now),
                Server("late-server", now.AddSeconds(-90)),
                Server("dead-server", now.AddMinutes(-10)),
                Server("silent-server", null));

            var html = _renderer.Render(new ServersPage());

            Assert.Contains("glyphicon-ok text-success\" title=\"" + H(Strings.ServersPage_Active) + "\"", html);
            // Past ServerPossiblyAbortedThreshold (one minute) is possibly aborted, as on the job pages; it took twice that
            Assert.DoesNotContain("glyphicon margin-right-14p", html);
            Assert.Equal(2, (html.Length - html.Replace("glyphicon-alert text-danger", String.Empty).Length) / "glyphicon-alert text-danger".Length);
            Assert.Contains(H(Strings.ServersPage_Note_Title), html);
            Assert.Contains("/jobs/processing", html);
            Assert.Contains("fresh-server", html);
            Assert.Contains("silent-server", html);
        }

        [Fact]
        public void Render_DoesNotShowTheAbortedNote_WhenEveryServerIsAlive()
        {
            SetupServers(Server("fresh-server", DateTime.UtcNow));

            var html = _renderer.Render(new ServersPage());

            Assert.DoesNotContain(H(Strings.ServersPage_Note_Title), html);
            Assert.DoesNotContain("glyphicon-alert text-danger", html);
        }

        [Fact]
        public void Render_ListsTheQueuesOfAServer()
        {
            SetupServers(Server("fresh-server", DateTime.UtcNow, "critical", "default"));

            var html = _renderer.Render(new ServersPage());

            Assert.Contains("critical", html);
            Assert.Contains("default", html);
        }

        [Fact]
        public void Render_ShowsPauseButtons_ForRunningServers()
        {
            SetupServers(Server("fresh-server", DateTime.UtcNow));

            var html = _renderer.Render(new ServersPage());

            Assert.Contains("data-ajax=\"/servers/pause/fresh-server\"", html);
            Assert.DoesNotContain("/servers/resume/", html);
            Assert.DoesNotContain(H(Strings.ServersPage_Paused) + "</span>", html);
        }

        [Fact]
        public void Render_ShowsThePausedLabel_AndAResumeButton_ForAPausedServer()
        {
            SetupServers(Server("paused server", DateTime.UtcNow), Server("fresh-server", DateTime.UtcNow));
            SetupControlHash(new Dictionary<string, string> { { DrainMode.ServerFieldPrefix + "paused server", "disk swap" } });

            var html = _renderer.Render(new ServersPage());

            Assert.Contains("title=\"disk swap\">" + H(Strings.ServersPage_Paused) + "</span>", html);
            Assert.Contains("data-ajax=\"/servers/resume/paused%20server\"", html);
            Assert.Contains("data-ajax=\"/servers/pause/fresh-server\"", html);
        }

        [Fact]
        public void Render_ListsPausesLeftByServersThatAreGone_WithAClearButton()
        {
            SetupServers(Server("web-1:2:new", DateTime.UtcNow));
            SetupControlHash(new Dictionary<string, string>
            {
                { DrainMode.ServerFieldPrefix + "web-1:1:old", "before deploy" },
                { DrainMode.ServerFieldPrefix + "web-1:2:new", "" },
            });

            var html = _renderer.Render(new ServersPage());

            Assert.Contains("<h4>" + H(Strings.ServersPage_StalePauses_Title) + "</h4>", html);
            Assert.Contains("<code>web-1:1:old</code>", html);
            Assert.Contains("<em>before deploy</em>", html);
            Assert.Contains("data-ajax=\"/servers/resume/web-1%3A1%3Aold\"", html);
            Assert.Contains(H(Strings.ServersPage_StalePauses_Clear), html);
            Assert.DoesNotContain("<code>web-1:2:new</code>", html);
            Assert.Contains("data-ajax=\"/servers/resume/web-1%3A2%3Anew\"", html);
        }

        [Fact]
        public void Render_ListsStalePauses_EvenWithoutAnyServer()
        {
            SetupControlHash(new Dictionary<string, string> { { DrainMode.ServerFieldPrefix + "gone", "" } });

            var html = _renderer.Render(new ServersPage());

            Assert.Contains(H(Strings.ServersPage_NoServers), html);
            Assert.Contains("<code>gone</code>", html);
            Assert.Contains("data-ajax=\"/servers/resume/gone\"", html);
        }

        [Fact]
        public void Render_DoesNotListStalePauses_WhenEveryPausedServerIsRunning()
        {
            SetupServers(Server("paused server", DateTime.UtcNow));
            SetupControlHash(new Dictionary<string, string> { { DrainMode.ServerFieldPrefix + "paused server", "" } });

            var html = _renderer.Render(new ServersPage());

            Assert.DoesNotContain(H(Strings.ServersPage_StalePauses_Title), html);
        }

        [Fact]
        public void Render_ListsStalePauses_WithoutTheClearButton_WhenTheDashboardIsReadOnly()
        {
            _renderer.Options = new Masterly.NightShift.DashboardOptions { IsReadOnlyFunc = _ => true };
            SetupControlHash(new Dictionary<string, string> { { DrainMode.ServerFieldPrefix + "gone", "" } });

            var html = _renderer.Render(new ServersPage());

            Assert.Contains("<code>gone</code>", html);
            Assert.DoesNotContain("/servers/resume/", html);
        }

        [Fact]
        public void Render_HidesEveryCommand_WhenTheDashboardIsReadOnly()
        {
            _renderer.Options = new Masterly.NightShift.DashboardOptions { IsReadOnlyFunc = _ => true };
            _renderer.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Purge)).Returns(true);
            SetupServers(Server("fresh-server", DateTime.UtcNow));

            var html = _renderer.Render(new ServersPage());

            Assert.DoesNotContain("/servers/pause/", html);
            Assert.DoesNotContain("<th class=\"min-width\"></th>", html);
            Assert.DoesNotContain("js-drain-form", html);
            Assert.DoesNotContain("/maintenance/resume", html);
            Assert.DoesNotContain("id=\"servers-maintenance\"", html);
        }

        [Fact]
        public void Render_ShowsProcessingAsRunning_WithAPauseForm_WhenNotDrained()
        {
            var html = _renderer.Render(new ServersPage());

            Assert.Contains(H(Strings.ServersPage_Processing_Running), html);
            Assert.Contains("data-url=\"/maintenance/pause\"", html);
            Assert.DoesNotContain("hf-drain-active", html);
        }

        [Fact]
        public void Render_ShowsProcessingAsPaused_WithSinceReasonAndResume_WhenDrained()
        {
            SetupControlHash(new Dictionary<string, string>
            {
                { "Drain", "1" },
                { "DrainSince", JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(-5)) },
                { "DrainReason", "deploying v2" },
            });

            var html = _renderer.Render(new ServersPage());

            Assert.Contains("hf-drain-active", html);
            Assert.Contains(H(Strings.ServersPage_Processing_Paused), html);
            Assert.Contains("<em>deploying v2</em>", html);
            Assert.Contains("data-ajax=\"/maintenance/resume\"", html);
            Assert.DoesNotContain("js-drain-form", html);
            Assert.Contains(Strings.ServersPage_Paused_Since.Split(new[] { "{0}" }, StringSplitOptions.None)[0] + "<span data-moment=", html);
        }

        [Fact]
        public void Render_ShowsProcessingAsPaused_WithoutSinceOrReason_WhenTheyAreNotStored()
        {
            SetupControlHash(new Dictionary<string, string> { { "Drain", "1" } });

            var html = _renderer.Render(new ServersPage());

            Assert.Contains(H(Strings.ServersPage_Processing_Paused), html);
            Assert.DoesNotContain("<em>", html);
            Assert.DoesNotContain(Strings.ServersPage_Paused_Since.Split(new[] { "{0}" }, StringSplitOptions.None)[0] + "<span data-moment=", html);
            Assert.Contains("data-ajax=\"/maintenance/resume\"", html);
        }

        [Fact]
        public void Render_ShowsTheMaintenanceSection_WhenTheStorageSupportsPurge()
        {
            _renderer.Storage.Setup(x => x.HasFeature(JobStorageFeatures.Purge)).Returns(true);

            var html = _renderer.Render(new ServersPage());

            Assert.Contains("id=\"servers-maintenance\"", html);
            Assert.Contains("data-ajax=\"/maintenance/clear-jobs\"", html);
            Assert.Contains("data-ajax=\"/maintenance/reset\"", html);
        }

        [Fact]
        public void Render_HidesTheMaintenanceSection_WhenTheStorageDoesNotSupportPurge()
        {
            var html = _renderer.Render(new ServersPage());

            Assert.DoesNotContain("id=\"servers-maintenance\"", html);
        }

        [Fact, StaticLock]
        public void Render_ShowsTheKeepAliveAsPending_BeforeTheFirstRequest()
        {
            using (new KeepAliveProcess(new KeepAliveOptions { Url = "http://keep.alive/pending" }))
            {
                var html = _renderer.Render(new ServersPage());

                Assert.Contains("label-info\">" + H(Strings.ServersPage_KeepAlive_Pending), html);
                Assert.DoesNotContain(H(Strings.ServersPage_KeepAlive_NotConfigured), html);
            }
        }

        [Fact, StaticLock]
        public void Render_ShowsTheKeepAliveAsOk_WithItsUrl_AfterASuccessfulRequest()
        {
            using (var process = new KeepAliveProcess(new KeepAliveOptions { Url = "http://keep.alive/ok" }, new HttpClient(new StubHandler(null))))
            {
                process.Ping(CancellationToken.None);

                var html = _renderer.Render(new ServersPage());

                Assert.Contains("label-success\">" + H(Strings.ServersPage_KeepAlive_Ok), html);
                Assert.Contains("<code>http://keep.alive/ok</code>", html);
            }
        }

        [Fact, StaticLock]
        public void Render_ShowsTheKeepAliveAsFailed_WithTheError_AfterAFailedRequest()
        {
            using (var process = new KeepAliveProcess(new KeepAliveOptions { Url = "http://keep.alive/failed", LogFailures = false }, new HttpClient(new StubHandler(new HttpRequestException("no route to host")))))
            {
                process.Ping(CancellationToken.None);

                var html = _renderer.Render(new ServersPage());

                Assert.Contains("label-danger\" title=\"no route to host\">" + H(Strings.ServersPage_KeepAlive_Failed), html);
                Assert.Contains("&middot; no route to host", html);
            }
        }

        private void SetupServers(params ServerDto[] servers)
        {
            _renderer.Monitoring.Setup(x => x.Servers()).Returns(new List<ServerDto>(servers));
        }

        private void SetupControlHash(Dictionary<string, string> hash)
        {
            _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(hash);
        }

        private static ServerDto Server(string name, DateTime? heartbeat, params string[] queues)
        {
            return new ServerDto
            {
                Name = name,
                WorkersCount = 5,
                StartedAt = DateTime.UtcNow.AddHours(-1),
                Heartbeat = heartbeat,
                Queues = queues.Length > 0 ? queues : new[] { "default" },
            };
        }

        private static string H(string text) => WebUtility.HtmlEncode(text);

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Exception _exception;

            public StubHandler(Exception exception)
            {
                _exception = exception;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_exception != null) throw _exception;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
