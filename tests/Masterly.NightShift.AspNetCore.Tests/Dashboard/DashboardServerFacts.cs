// Hosts the dashboard with WebApplication and endpoint routing, which ASP.NET Core 2.x on .NET Framework doesn't have
#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Masterly.NightShift.AspNetCore.Tests.Dashboard
{
    /// <summary>
    /// The dashboard served by Kestrel, as an app configures it, and requested over HTTP like a browser or an
    /// attacker's page would.
    /// </summary>
    public class DashboardServerFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<IMonitoringApi> _monitor = new Mock<IMonitoringApi> { DefaultValue = DefaultValue.Empty };
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection> { DefaultValue = DefaultValue.Empty };
        private readonly Mock<IBackgroundJobClient> _client = new Mock<IBackgroundJobClient>();
        private readonly Mock<IBackgroundJobClientFactory> _clientFactory = new Mock<IBackgroundJobClientFactory>();

        public DashboardServerFacts()
        {
            _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            _monitor.Setup(x => x.GetStatistics()).Returns(new StatisticsDto());
            _monitor.Setup(x => x.ScheduledJobs(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new JobList<ScheduledJobDto>(new List<KeyValuePair<string, ScheduledJobDto>>()));
            _connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>())).Returns((Dictionary<string, string>)null);
            _clientFactory.Setup(x => x.GetClient(It.IsAny<JobStorage>())).Returns(_client.Object);
            _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>())).Returns(true);
        }

        [Fact]
        public async Task WithoutAntiforgery_ACrossSiteFormPost_IsRejected()
        {
            // What a page on another site does: a form posted by the victim's browser, no custom header possible
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
            {
                request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("AddAntiforgery", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task WithoutAntiforgery_APostWithoutTheScriptHeader_IsRejected_EvenWithoutOrigin()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var response = await app.PostAsync("/nightshift/jobs/actions/delete/1");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task WithoutAntiforgery_ASameOriginScriptPost_IsAccepted()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
            {
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
                request.Headers.TryAddWithoutValidation("Origin", app.BaseAddress);
            });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            _client.Verify(x => x.ChangeState("1", It.IsAny<DeletedState>(), null), Times.Once);
        }

        [Theory]
        [InlineData("cross-site", null)]
        [InlineData("same-site", null)]
        [InlineData(null, "https://evil.example")]
        [InlineData(null, "null")]
        public async Task WithoutAntiforgery_AScriptPostFromAnotherSite_IsRejected(string fetchSite, string origin)
        {
            // A script on another origin can add the header only after a CORS preflight, which an app may allow
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
            {
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                if (fetchSite != null) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
                if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
            });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task WithoutAntiforgery_AScriptPostWithTheRequestsOwnOrigin_IsAccepted_FromBrowsersWithoutFetchMetadata()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
            {
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                request.Headers.TryAddWithoutValidation("Origin", app.BaseAddress);
            });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        [Fact]
        public async Task WithoutAntiforgery_IgnoreAntiforgeryToken_TurnsTheCheckOff()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters(options: new DashboardOptions { IgnoreAntiforgeryToken = true }));

            var response = await app.PostAsync("/nightshift/jobs/actions/delete/1");

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        [Fact]
        public async Task WithAntiforgery_ATokenlessPost_IsRejected_AndThePagesTokenIsAccepted()
        {
            await using var app = await StartAsync(services => services.AddAntiforgery(), static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            // The custom header alone doesn't replace the token when antiforgery is registered
            var tokenless = await app.PostAsync("/nightshift/jobs/actions/delete/1", request => request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest"));

            var page = await app.GetStringAsync("/nightshift/jobs/scheduled");
            var header = Regex.Match(page, "<meta name=\"csrf-header\" content=\"([^\"]+)\">").Groups[1].Value;
            var token = Regex.Match(page, "<meta name=\"csrf-token\" content=\"([^\"]+)\">").Groups[1].Value;
            var withToken = await app.PostAsync("/nightshift/jobs/actions/delete/1", request => request.Headers.TryAddWithoutValidation(header, WebUtility.HtmlDecode(token)));

            Assert.Equal(HttpStatusCode.Forbidden, tokenless.StatusCode);
            Assert.False(String.IsNullOrEmpty(token));
            Assert.Equal(HttpStatusCode.NoContent, withToken.StatusCode);
            _client.Verify(x => x.ChangeState("1", It.IsAny<DeletedState>(), null), Times.Once);
        }

        [Fact]
        public async Task WithAntiforgery_OnlyPagesGetTokens_SoStaticBundlesStayCacheable()
        {
            await using var app = await StartAsync(services => services.AddAntiforgery(), static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var bundle = await app.GetAsync("/nightshift/js1");
            var page = await app.GetAsync("/nightshift/jobs/scheduled");

            Assert.Equal(HttpStatusCode.OK, bundle.StatusCode);
            Assert.False(bundle.Headers.Contains("Set-Cookie"));
            Assert.False(bundle.Headers.CacheControl?.NoStore == true);
            Assert.NotNull(bundle.Content.Headers.Expires);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.True(page.Headers.CacheControl?.NoStore == true);
        }

        [Fact]
        public async Task TheScriptBundle_AddsTheTokenAndTheScriptHeader_OnlyToSameOriginRequests()
        {
            // $.ajaxSetup({ headers }) sent them to every origin an extension called, leaking the token
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var bundle = await app.GetStringAsync("/nightshift/js1");

            Assert.Contains("$.ajaxPrefilter(function (options, originalOptions, xhr) {", bundle);
            Assert.Contains("if (options.crossDomain) return;", bundle);
            Assert.DoesNotContain("$.ajaxSetup({ headers", bundle);
        }

        [Fact]
        public async Task EveryPage_ForbidsFramingByOtherSites()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var page = await app.GetAsync("/nightshift/jobs/scheduled");
            var again = await app.GetAsync("/nightshift/jobs/scheduled");

            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("SAMEORIGIN", Assert.Single(page.Headers.GetValues("X-Frame-Options")));
            Assert.Equal("frame-ancestors 'self'", Assert.Single(page.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("SAMEORIGIN", Assert.Single(again.Headers.GetValues("X-Frame-Options")));
        }

        [Fact]
        public async Task WithAntiforgery_ThePage_SendsTheFramingHeaderOnce()
        {
            await using var app = await StartAsync(services => services.AddAntiforgery(), static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var page = await app.GetAsync("/nightshift/jobs/scheduled");

            Assert.Equal("SAMEORIGIN", Assert.Single(page.Headers.GetValues("X-Frame-Options")));
        }

        [Fact]
        public async Task TheDefaultFilter_TurnsAwayRequestsForwardedByAProxyOnTheSameMachine()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboard());

            var direct = await app.GetAsync("/nightshift/ping");
            var viaProxy = await app.GetAsync("/nightshift/ping", request => request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9"));

            Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, viaProxy.StatusCode);
        }

        [Fact]
        public async Task AFormWithMoreValuesThanAspNetCoreAccepts_Answers413_WithTheReason()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());
            var form = Enumerable.Range(0, 1100).Select(static i => new KeyValuePair<string, string>("jobs[]", i.ToString()));

            var response = await app.PostAsync("/nightshift/jobs/scheduled/delete", request =>
            {
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                request.Content = new FormUrlEncodedContent(form);
            });

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.ToString());
            Assert.Contains("Form value count limit", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task WithAntiforgery_ATokenlessFormOverTheLimits_Answers413_InsteadOfFailing()
        {
            // Without the token in a header, antiforgery reads the form to find it, and this one can't be read
            await using var app = await StartAsync(services => services.AddAntiforgery(), static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());
            var form = Enumerable.Range(0, 1100).Select(static i => new KeyValuePair<string, string>("jobs[]", i.ToString()));

            var response = await app.PostAsync("/nightshift/jobs/scheduled/delete", request => request.Content = new FormUrlEncodedContent(form));

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task WithoutAntiforgery_AScriptPost_DoesNotTrustAForwardedHost_ToMatchItsOrigin()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
            {
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
                request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "evil.example");
            });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        }

        [Theory]
        [InlineData("http://dashboard.example:8080")]
        [InlineData("https://dashboard.example")]
        [InlineData("https://dashboard.example:80")]
        public async Task WithoutAntiforgery_AScriptPost_FromAnotherPortOrSchemeOfTheSameHost_IsRejected(string origin)
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
            {
                // A Host without a port, as browsers send it for the default port
                request.Headers.Host = "dashboard.example";
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                request.Headers.TryAddWithoutValidation("Origin", origin);
            });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task WithoutAntiforgery_AScriptPost_FromTheDefaultPortOfTheSameHost_IsAccepted()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
            {
                request.Headers.Host = "dashboard.example";
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                request.Headers.TryAddWithoutValidation("Origin", "http://dashboard.example");
            });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        [Theory]
        [InlineData("/nightshift/queues/pause/default", HttpStatusCode.NoContent)]
        [InlineData("/nightshift/servers/pause/some-server", HttpStatusCode.NoContent)]
        [InlineData("/nightshift/maintenance/pause", HttpStatusCode.OK)]
        public async Task AScriptPostWithoutABody_IsAccepted_WithItsOptionalFieldsMissing(string path, HttpStatusCode expected)
        {
            // jQuery's $.post(url) without data sends no body and no Content-Type, as the Pause buttons and the
            // palette's pause commands do. Reading the optional reason made ASP.NET Core throw, so they answered 500.
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(new Mock<IWriteOnlyTransaction>().Object);
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var response = await app.PostAsync(path, request =>
            {
                request.Content = null;
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            });

            Assert.Equal(expected, response.StatusCode);
        }

        [Fact]
        public async Task WithAntiforgery_AScriptPostWithoutABody_IsAccepted_WithThePagesToken()
        {
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(new Mock<IWriteOnlyTransaction>().Object);
            await using var app = await StartAsync(services => services.AddAntiforgery(), static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());
            var page = await app.GetStringAsync("/nightshift/jobs/scheduled");
            var header = Regex.Match(page, "<meta name=\"csrf-header\" content=\"([^\"]+)\">").Groups[1].Value;
            var token = WebUtility.HtmlDecode(Regex.Match(page, "<meta name=\"csrf-token\" content=\"([^\"]+)\">").Groups[1].Value);

            var response = await app.PostAsync("/nightshift/queues/pause/default", request =>
            {
                request.Content = null;
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                request.Headers.TryAddWithoutValidation(header, token);
            });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        [Fact]
        public async Task TheStatsEndpoint_AnswersAGetWith405()
        {
            // The pages post the metric names; a GET has no form, and answered 500 before
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var response = await app.GetAsync("/nightshift/stats");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        [Fact]
        public async Task TheScriptBundle_OpensOnlyTheClickedPaletteEntry()
        {
            // The group headers are list items too, so the position among them isn't the entry's index: a click
            // ran the entry after the one clicked, and a click on the "Commands" header paused processing
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var bundle = await app.GetStringAsync("/nightshift/js1");

            Assert.Contains("this._list.on('click', 'li.hf-palette-item', function () {", bundle);
            Assert.Contains("var index = $(this).data('index');", bundle);
            Assert.DoesNotContain("self._selected = $(this).index();", bundle);
        }

        [Fact]
        public async Task TheWallboardsDateScript_IsServed()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            // The wallboard page links it with a version number, like the bundle
            var script = await app.GetAsync("/nightshift/js-intl1");

            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Contains("global.NightShiftIntl = NightShiftIntl;", await script.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task TheScriptBundle_KeepsTheLiveToggle_PerPage_AndSkipsHiddenTabs()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var bundle = await app.GetStringAsync("/nightshift/js1");

            Assert.Contains("this._storageKey = STORAGE_PREFIX + window.location.pathname;", bundle);
            Assert.Contains("if (!manual && (document.hidden ||", bundle);
            Assert.Contains("var LIST_REFRESH_QUERY = 'list-refresh=1';", bundle);
            // The activity feed forgets the entries it can't receive again
            Assert.Contains("ActivityFeed.prototype._forget = function () {", bundle);
        }

        [Fact]
        public async Task TheScriptBundle_PreFillsTheRunFormsDatesAndQueue()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var bundle = await app.GetStringAsync("/nightshift/js1");

            Assert.Contains("this._modal.find('[name=\"queue\"]').val(button.data('queue') || '');", bundle);
            Assert.Contains("input = $('<input type=\"datetime-local\" step=\"1\" class=\"form-control\">').attr({ id: id, name: name }).val(value);", bundle);
        }

        [Fact]
        public async Task TheScriptBundle_SendsExplicitValues_ForCheckboxesNullableChoicesAndFlags()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var bundle = await app.GetStringAsync("/nightshift/js1");

            // An unchecked box sent nothing, which the server read as the default
            Assert.Contains(".append($('<input type=\"hidden\" value=\"false\">').attr('name', name))", bundle);
            Assert.Contains("input.append($('<option>').val('null').text('(null)'));", bundle);
            Assert.Contains("input.attr({ multiple: 'multiple', size: Math.min(8, input.children().length) });", bundle);
            Assert.Contains("group.append($('<input type=\"hidden\" value=\"0\">').attr('name', name));", bundle);
        }

        [Fact]
        public async Task TheScriptBundle_PollsTheProcessingRows_WithoutConsoles_AndNotInHiddenTabs()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var bundle = await app.GetStringAsync("/nightshift/js1");

            Assert.Contains("$.post(self._url, { 'jobs[]': ids, console: 0 })", bundle);
            Assert.Matches(@"if \(document\.hidden\) \{\s+self\._timer = setTimeout\(poll, self\._pollInterval\);\s+return;", bundle);
        }

        [Fact]
        public async Task TheScriptBundle_KeepsAZoneMissingFromTheList_AndSendsAnEmptyQueue()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var bundle = await app.GetStringAsync("/nightshift/js1");

            Assert.Contains("this._timezone.append($('<option>').val(timezone).text(timezone));", bundle);
            Assert.Contains("queue: $.trim(this._queue.val()),", bundle);
        }

        [Fact]
        public async Task TheScriptBundle_GivesThePaletteRolesAndNames_AndLetsUsersTurnOffSingleKeyShortcuts()
        {
            await using var app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

            var bundle = await app.GetStringAsync("/nightshift/js1");

            Assert.Contains("role=\"dialog\" aria-modal=\"true\" aria-label=\"Command palette\"", bundle);
            Assert.Contains("role=\"combobox\" aria-autocomplete=\"list\" aria-expanded=\"true\" aria-controls=\"hfPaletteResults\"", bundle);
            Assert.Contains("id=\"hfPaletteResults\" role=\"listbox\"", bundle);
            Assert.Contains("$('<li class=\"hf-palette-item\" role=\"option\">')", bundle);
            Assert.Contains("this._input.attr('aria-activedescendant', 'hfPaletteItem' + this._selected);", bundle);
            Assert.Contains(".attr('aria-pressed', this._enabled ? 'true' : 'false')", bundle);
            Assert.Contains("if (!nightshift.singleKeyShortcuts.enabled()) return;", bundle);
            Assert.Contains("if (!button.data('confirm') && !confirm(", bundle);
        }

        [Fact]
        public async Task NoAuthorizationFilters_UsesTheOptionsRegisteredInDI()
        {
            var registered = new DashboardOptions { IsReadOnlyFunc = static _ => true, DashboardTitle = "From DI" };
            await using var app = await StartAsync(services => services.AddSingleton(registered), static app => app.MapNightShiftDashboardWithNoAuthorizationFilters("/nofilters"));

            var page = await app.GetStringAsync("/nofilters/jobs/scheduled");
            var command = await app.PostAsync("/nofilters/jobs/actions/delete/1", request => request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest"));

            Assert.Contains("From DI", page);
            Assert.Equal(HttpStatusCode.Unauthorized, command.StatusCode);
            _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);

            // The registered instance keeps its filters for the other dashboards that use it
            Assert.IsType<LocalRequestsOnlyAuthorizationFilter>(Assert.Single(registered.Authorization));
        }

        [Fact]
        public async Task NoAuthorizationFilters_DoesNotClearTheFiltersOfTheOptionsPassed()
        {
            var filter = new Mock<IDashboardAuthorizationFilter>().Object;
            var options = new DashboardOptions { Authorization = new[] { filter } };
            await using var app = await StartAsync(map: app => app.MapNightShiftDashboardWithNoAuthorizationFilters("/nofilters", options));

            var response = await app.GetAsync("/nofilters/ping");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Same(filter, Assert.Single(options.Authorization));
        }

        private async Task<RunningApp> StartAsync(Action<IServiceCollection> services = null, Action<WebApplication> map = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(_storage.Object);
            builder.Services.AddSingleton(_clientFactory.Object);
            builder.Services.AddNightShift(_ => { });
            services?.Invoke(builder.Services);

            var app = builder.Build();
            map?.Invoke(app);
            await app.StartAsync(TestContext.Current.CancellationToken);

            return new RunningApp(app);
        }

        private sealed class RunningApp : IAsyncDisposable
        {
            private readonly WebApplication _app;

            public RunningApp(WebApplication app)
            {
                _app = app;
                BaseAddress = app.Urls.First().TrimEnd('/');
                Client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true });
            }

            public string BaseAddress { get; }
            public HttpClient Client { get; }

            public Task<HttpResponseMessage> PostAsync(string path, Action<HttpRequestMessage> configure = null)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, BaseAddress + path)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>()),
                };
                configure?.Invoke(request);
                return Client.SendAsync(request, TestContext.Current.CancellationToken);
            }

            public Task<HttpResponseMessage> GetAsync(string path, Action<HttpRequestMessage> configure = null)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, BaseAddress + path);
                configure?.Invoke(request);
                return Client.SendAsync(request, TestContext.Current.CancellationToken);
            }

            public Task<string> GetStringAsync(string path)
            {
                return Client.GetStringAsync(BaseAddress + path, TestContext.Current.CancellationToken);
            }

            public async ValueTask DisposeAsync()
            {
                Client.Dispose();
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
        }
    }
}
#endif
