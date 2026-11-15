using System.Net;
using System.Text.RegularExpressions;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Masterly.NightShift.AspNetCore.Tests.Dashboard;

/// <summary>
/// The dashboard served by Kestrel, as an app configures it, and requested over HTTP like a browser or an
/// attacker's page would.
/// </summary>
public class DashboardServerFacts
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IMonitoringApi> _monitor = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<JobStorageConnection> _connection = new() { DefaultValue = DefaultValue.Empty };
    private readonly Mock<IBackgroundJobClient> _client = new();
    private readonly Mock<IBackgroundJobClientFactory> _clientFactory = new();

    public DashboardServerFacts()
    {
        _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
        _monitor.Setup(x => x.GetStatistics()).Returns(new StatisticsDto());
        _monitor.Setup(x => x.ScheduledJobs(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new JobList<ScheduledJobDto>([]));
        _connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>())).Returns((Dictionary<string, string>)null);
        _clientFactory.Setup(x => x.GetClient(It.IsAny<JobStorage>())).Returns(_client.Object);
        _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>())).Returns(true);
    }

    [Fact]
    public async Task BasicAuth_WithoutCredentials_AnswersUnauthorized_WithTheBrowsersChallenge()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboard(new DashboardOptions
        {
            AsyncAuthorization = [new BasicAuthDashboardAuthorizationFilter("operator", "s3cret")],
        }));

        HttpResponseMessage response = await app.GetAsync("/nightshift/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Basic", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task BasicAuth_WithTheRightCredentials_ServesThePage()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboard(new DashboardOptions
        {
            AsyncAuthorization = [new BasicAuthDashboardAuthorizationFilter("operator", "s3cret")],
        }));

        HttpResponseMessage response = await app.GetAsync("/nightshift/", static request =>
            request.Headers.TryAddWithoutValidation("Authorization", "Basic b3BlcmF0b3I6czNjcmV0"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WithoutAntiforgery_ACrossSiteFormPost_IsRejected()
    {
        // What a page on another site does: a form posted by the victim's browser, no custom header possible
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        HttpResponseMessage response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
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
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        HttpResponseMessage response = await app.PostAsync("/nightshift/jobs/actions/delete/1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WithoutAntiforgery_ASameOriginScriptPost_IsAccepted()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        HttpResponseMessage response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
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
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        HttpResponseMessage response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
        {
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            if (fetchSite != null)
            {
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
            }

            if (origin != null)
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WithoutAntiforgery_AScriptPostWithTheRequestsOwnOrigin_IsAccepted_FromBrowsersWithoutFetchMetadata()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        HttpResponseMessage response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
        {
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.TryAddWithoutValidation("Origin", app.BaseAddress);
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task WithoutAntiforgery_IgnoreAntiforgeryToken_TurnsTheCheckOff()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters(options: new DashboardOptions { IgnoreAntiforgeryToken = true }));

        HttpResponseMessage response = await app.PostAsync("/nightshift/jobs/actions/delete/1");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task WithAntiforgery_ATokenlessPost_IsRejected_AndThePagesTokenIsAccepted()
    {
        await using RunningApp app = await StartAsync(services => services.AddAntiforgery(), static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        // The custom header alone doesn't replace the token when antiforgery is registered
        HttpResponseMessage tokenless = await app.PostAsync("/nightshift/jobs/actions/delete/1", request => request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest"));

        string page = await app.GetStringAsync("/nightshift/jobs/scheduled");
        string header = Regex.Match(page, "<meta name=\"csrf-header\" content=\"([^\"]+)\">").Groups[1].Value;
        string token = Regex.Match(page, "<meta name=\"csrf-token\" content=\"([^\"]+)\">").Groups[1].Value;
        HttpResponseMessage withToken = await app.PostAsync("/nightshift/jobs/actions/delete/1", request => request.Headers.TryAddWithoutValidation(header, WebUtility.HtmlDecode(token)));

        Assert.Equal(HttpStatusCode.Forbidden, tokenless.StatusCode);
        Assert.False(string.IsNullOrEmpty(token));
        Assert.Equal(HttpStatusCode.NoContent, withToken.StatusCode);
        _client.Verify(x => x.ChangeState("1", It.IsAny<DeletedState>(), null), Times.Once);
    }

    [Fact]
    public async Task WithAntiforgery_OnlyPagesGetTokens_SoStaticBundlesStayCacheable()
    {
        await using RunningApp app = await StartAsync(services => services.AddAntiforgery(), static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        HttpResponseMessage bundle = await app.GetAsync("/nightshift/js1");
        HttpResponseMessage page = await app.GetAsync("/nightshift/jobs/scheduled");

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
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string bundle = await app.GetStringAsync("/nightshift/js1");

        Assert.Contains("$.ajaxPrefilter(function (options, originalOptions, xhr) {", bundle);
        Assert.Contains("if (options.crossDomain) return;", bundle);
        Assert.DoesNotContain("$.ajaxSetup({ headers", bundle);
    }

    [Fact]
    public async Task EveryPage_ForbidsFramingByOtherSites()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        HttpResponseMessage page = await app.GetAsync("/nightshift/jobs/scheduled");
        HttpResponseMessage again = await app.GetAsync("/nightshift/jobs/scheduled");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("SAMEORIGIN", Assert.Single(page.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("frame-ancestors 'self'", Assert.Single(page.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("SAMEORIGIN", Assert.Single(again.Headers.GetValues("X-Frame-Options")));
    }

    [Fact]
    public async Task WithAntiforgery_ThePage_SendsTheFramingHeaderOnce()
    {
        await using RunningApp app = await StartAsync(services => services.AddAntiforgery(), static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        HttpResponseMessage page = await app.GetAsync("/nightshift/jobs/scheduled");

        Assert.Equal("SAMEORIGIN", Assert.Single(page.Headers.GetValues("X-Frame-Options")));
    }

    [Fact]
    public async Task TheDefaultFilter_TurnsAwayRequestsForwardedByAProxyOnTheSameMachine()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboard());

        HttpResponseMessage direct = await app.GetAsync("/nightshift/ping");
        HttpResponseMessage viaProxy = await app.GetAsync("/nightshift/ping", request => request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9"));

        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, viaProxy.StatusCode);
    }

    [Fact]
    public async Task AFormWithMoreValuesThanAspNetCoreAccepts_Answers413_WithTheReason()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());
        IEnumerable<KeyValuePair<string, string>> form = Enumerable.Range(0, 1100).Select(static i => new KeyValuePair<string, string>("jobs[]", i.ToString()));

        HttpResponseMessage response = await app.PostAsync("/nightshift/jobs/scheduled/delete", request =>
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
        await using RunningApp app = await StartAsync(services => services.AddAntiforgery(), static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());
        IEnumerable<KeyValuePair<string, string>> form = Enumerable.Range(0, 1100).Select(static i => new KeyValuePair<string, string>("jobs[]", i.ToString()));

        HttpResponseMessage response = await app.PostAsync("/nightshift/jobs/scheduled/delete", request => request.Content = new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WithoutAntiforgery_AScriptPost_DoesNotTrustAForwardedHost_ToMatchItsOrigin()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        HttpResponseMessage response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
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
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        HttpResponseMessage response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
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
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        HttpResponseMessage response = await app.PostAsync("/nightshift/jobs/actions/delete/1", request =>
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
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        HttpResponseMessage response = await app.PostAsync(path, request =>
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
        await using RunningApp app = await StartAsync(services => services.AddAntiforgery(), static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());
        string page = await app.GetStringAsync("/nightshift/jobs/scheduled");
        string header = Regex.Match(page, "<meta name=\"csrf-header\" content=\"([^\"]+)\">").Groups[1].Value;
        string token = WebUtility.HtmlDecode(Regex.Match(page, "<meta name=\"csrf-token\" content=\"([^\"]+)\">").Groups[1].Value);

        HttpResponseMessage response = await app.PostAsync("/nightshift/queues/pause/default", request =>
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
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        HttpResponseMessage response = await app.GetAsync("/nightshift/stats");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task TheBundles_HaveJQuery4AndBootstrap5_EachFileOnItsOwnLines()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string scripts = await app.GetStringAsync("/nightshift/js1");
        string styles = await app.GetStringAsync("/nightshift/css1");

        Assert.Contains("/*! jQuery v4.0.0 |", scripts);
        Assert.Contains("* Bootstrap v5.3.9 (https://getbootstrap.com/)", scripts);
        Assert.Contains("* Bootstrap  v5.3.9 (https://getbootstrap.com/)", styles);
        Assert.DoesNotContain("Bootstrap v3", scripts);
        Assert.DoesNotContain("glyphicons-halflings", styles);
        // bootstrap.bundle.min.js ends with a line comment and no line break: the next file starts on a line of its own
        Assert.Contains("//# sourceMappingURL=bootstrap.bundle.min.js.map\n", scripts);
    }

    [Fact]
    public async Task TheScriptBundle_UsesBootstrap5AndJQuery4Apis_NotTheRemovedOnes()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string bundle = await app.GetStringAsync("/nightshift/js1");
        string script = bundle[bundle.IndexOf("(function (nightshift) {", StringComparison.Ordinal)..];

        Assert.Contains("bootstrap.Modal.getOrCreateInstance(this._modal[0]).show();", script);
        Assert.Contains("bootstrap.Tooltip.getOrCreateInstance(this);", script);
        Assert.Contains("nightshift.setLoading = function (element, loading) {", script);
        Assert.DoesNotContain("$.trim(", script);
        Assert.DoesNotContain(".button('loading')", script);
        Assert.DoesNotContain(".modal('show')", script);
        Assert.DoesNotContain(".tooltip()", script);
        Assert.DoesNotContain(".modal.in", script);
        // Glyphicon names are only resolved (NightShift.icon accepts the old ones), never drawn as Glyphicon classes
        Assert.DoesNotMatch(@"(class=|addClass\()[^)>]*\bglyphicon\b", script);
        // The names NightShift.icon resolves come first
        Assert.StartsWith("window.NightShift = window.NightShift || {};\nNightShift._icons = {\"names\":[", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBundles_HaveChartJs4_InOrder_WithoutTheStreamingPluginOrChartJs2sStylesheet()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string scripts = await app.GetStringAsync("/nightshift/js1");
        string styles = await app.GetStringAsync("/nightshift/css1");

        int bootstrap = scripts.IndexOf("* Bootstrap v5.3.9 (https://getbootstrap.com/)", StringComparison.Ordinal);
        int chart = scripts.IndexOf(" * Chart.js v4.6.1", StringComparison.Ordinal);
        int intl = scripts.IndexOf("global.NightShiftIntl = NightShiftIntl;", StringComparison.Ordinal);
        int nightshift = scripts.IndexOf("(function (nightshift) {", StringComparison.Ordinal);

        // Chart.js first, then the date adapter that overrides its own, then the graphs that use both
        Assert.True(bootstrap >= 0 && bootstrap < chart && chart < intl && intl < nightshift, $"{bootstrap} {chart} {intl} {nightshift}");
        // chart.umd.min.js ends with a line comment: the date adapter starts on a line of its own
        Assert.Matches(@"//# sourceMappingURL=chart\.umd\.min\.js\.map\r?\n\s*// Date formatting for the dashboard", scripts);
        Assert.DoesNotContain("Chart.js v2", scripts);
        Assert.DoesNotContain(" * chartjs-plugin-streaming v", scripts);
        Assert.DoesNotContain("chartjs-render-monitor", styles);
    }

    [Fact]
    public async Task TheScriptBundle_DrawsTheGraphsWithTheChartJs4Api_NotTheChartJs2One()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string bundle = await app.GetStringAsync("/nightshift/js1");
        string script = bundle[bundle.IndexOf("// Date formatting for the dashboard", StringComparison.Ordinal)..];

        Assert.Contains("Chart.defaults.font.family = ", script);
        Assert.Contains("global.Chart._adapters._date.override({", script);
        Assert.Contains("RealtimeScale.id = 'nightshiftRealtime';", script);
        Assert.Contains("Chart.register(RealtimeScale);", script);
        Assert.Contains("this._frameId = window.requestAnimationFrame(this._onFrame);", script);
        Assert.Contains("this._chart.update('none');", script);
        Assert.Contains("plugins: {", script);
        string[] removed = ["xAxes", "yAxes", "gridLines", "scaleLabel", "tooltips:", "Chart.defaults.global", "Chart.helpers", "realtime:", "streaming:", "ticks: { beginAtZero", "fontColor"];
        Assert.All(removed, api => Assert.DoesNotContain(api, script, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheIconSprite_IsServedAtTheUrlThePagesUse_AndTheGlyphiconFontsAreGone()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string page = await app.GetStringAsync("/nightshift/");
        string url = Regex.Match(page, "data-icons-url=\"([^\"]+)\"").Groups[1].Value;
        HttpResponseMessage sprite = await app.GetAsync(url);
        HttpResponseMessage font = await app.GetAsync("/nightshift/fonts/glyphicons-halflings-regular/woff2");

        Assert.Matches("^/nightshift/icons[0-9]+$", url);
        Assert.Contains($"<use href=\"{url}#bi-", page);
        Assert.Equal(HttpStatusCode.OK, sprite.StatusCode);
        Assert.Equal("image/svg+xml", sprite.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<symbol id=\"bi-trash-fill\" viewBox=\"0 0 16 16\">", await sprite.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, font.StatusCode);
    }

    [Fact]
    public async Task TheScriptBundle_OpensOnlyTheClickedPaletteEntry()
    {
        // The group headers are list items too, so the position among them isn't the entry's index: a click
        // ran the entry after the one clicked, and a click on the "Commands" header paused processing
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string bundle = await app.GetStringAsync("/nightshift/js1");

        Assert.Contains("this._list.on('click', 'li.hf-palette-item', function () {", bundle);
        Assert.Contains("var index = $(this).data('index');", bundle);
        Assert.DoesNotContain("self._selected = $(this).index();", bundle);
    }

    [Fact]
    public async Task TheWallboardsDateScript_IsServed()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        // The wallboard page links it with a version number, like the bundle
        HttpResponseMessage script = await app.GetAsync("/nightshift/js-intl1");

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("global.NightShiftIntl = NightShiftIntl;", await script.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheScriptBundle_KeepsTheLiveToggle_PerPage_AndSkipsHiddenTabs()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string bundle = await app.GetStringAsync("/nightshift/js1");

        Assert.Contains("this._storageKey = STORAGE_PREFIX + window.location.pathname;", bundle);
        Assert.Contains("if (!manual && (document.hidden ||", bundle);
        Assert.Contains("var LIST_REFRESH_QUERY = 'list-refresh=1';", bundle);
        // The activity feed forgets the entries it can't receive again
        Assert.Contains("ActivityFeed.prototype._forget = function () {", bundle);
    }

    [Fact]
    public async Task TheScriptBundle_PreFillsTheRunFormsDatesAndQueue()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string bundle = await app.GetStringAsync("/nightshift/js1");

        Assert.Contains("this._modal.find('[name=\"queue\"]').val(button.attr('data-queue') || '');", bundle);
        Assert.Contains("input = $('<input type=\"datetime-local\" step=\"1\" class=\"form-control\">').attr({ id: id, name: name }).val(value);", bundle);
    }

    [Fact]
    public async Task TheScriptBundle_SendsExplicitValues_ForCheckboxesNullableChoicesAndFlags()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string bundle = await app.GetStringAsync("/nightshift/js1");

        // An unchecked box sent nothing, which the server read as the default
        Assert.Contains(".append($('<input type=\"hidden\" value=\"false\">').attr('name', name))", bundle);
        Assert.Contains("input.append($('<option>').val('null').text('(null)'));", bundle);
        Assert.Contains("input.attr({ multiple: 'multiple', size: Math.min(8, input.children().length) });", bundle);
        Assert.Contains("group.append($('<input type=\"hidden\" value=\"0\">').attr('name', name));", bundle);
    }

    [Fact]
    public async Task TheScriptBundle_PollsTheProcessingRows_WithoutConsoles_AndNotInHiddenTabs()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string bundle = await app.GetStringAsync("/nightshift/js1");

        Assert.Contains("$.post(self._url, { 'jobs[]': ids, console: 0 })", bundle);
        Assert.Matches(@"if \(document\.hidden\) \{\s+self\._timer = setTimeout\(poll, self\._pollInterval\);\s+return;", bundle);
    }

    [Fact]
    public async Task TheScriptBundle_KeepsAZoneMissingFromTheList_AndSendsAnEmptyQueue()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string bundle = await app.GetStringAsync("/nightshift/js1");

        Assert.Contains("this._timezone.append($('<option>').val(timezone).text(timezone));", bundle);
        Assert.Contains("queue: trim(this._queue.val()),", bundle);
    }

    [Fact]
    public async Task TheScriptBundle_ReadsIdsAndNamesAsWritten_NotThroughJQuerysDataConversion()
    {
        // .data('id') JSON-parses "[7]" and turns "7" into a number, so the schedule editor of the job "[7]"
        // saved the job "7"
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string bundle = await app.GetStringAsync("/nightshift/js1");

        Assert.Contains("this._jobId = button.attr('data-id') || '';", bundle);
        Assert.Contains("return $(this).attr('data-id') === decodeURIComponent(", bundle);
        Assert.Contains("self.toggle(button.attr('data-id') || '', button.attr('data-name') || '', button.attr('data-state'));", bundle);
        Assert.DoesNotContain(".data('id')", bundle);
        Assert.DoesNotContain(".data('name')", bundle);
        Assert.DoesNotContain(".data('job-id')", bundle);
    }

    [Fact]
    public async Task TheScriptBundle_EncodesPathSegments_AsTheServerReadsThem()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string bundle = await app.GetStringAsync("/nightshift/js1");

        Assert.Contains("return c === '~' ? '~7E' : c === '%' ? '~25' : '~2F';", bundle);
        Assert.Contains("$.post(this._updateUrl + nightshift.routeValue(this._jobId), {", bundle);
    }

    [Fact]
    public async Task TheScriptBundle_GivesThePaletteRolesAndNames_AndLetsUsersTurnOffSingleKeyShortcuts()
    {
        await using RunningApp app = await StartAsync(map: static app => app.MapNightShiftDashboardWithNoAuthorizationFilters());

        string bundle = await app.GetStringAsync("/nightshift/js1");

        Assert.Contains("$('<div class=\"hf-palette\" role=\"dialog\" aria-modal=\"true\" style=\"display: none\">').attr('aria-label', TEXT.paletteLabel)", bundle);
        Assert.Contains("role=\"combobox\" aria-autocomplete=\"list\" aria-expanded=\"true\" aria-controls=\"hfPaletteResults\"", bundle);
        Assert.Contains("id=\"hfPaletteResults\" role=\"listbox\"", bundle);
        Assert.Contains("$('<li class=\"hf-palette-item\" role=\"option\">')", bundle);
        Assert.Contains("this._input.attr('aria-activedescendant', 'hfPaletteItem' + this._selected);", bundle);
        Assert.Contains(".attr('aria-pressed', this._enabled ? 'true' : 'false')", bundle);
        Assert.Contains("if (!nightshift.singleKeyShortcuts.enabled()) return;", bundle);
        // A command without its own confirmation asks in the dashboard's dialog, never window.confirm
        Assert.Contains("nightshift.confirm({ title: format(TEXT.shortcutConfirm, commandName(button), count)", bundle);
        Assert.DoesNotContain("!confirm(", bundle);
    }

    [Fact]
    public async Task NoAuthorizationFilters_UsesTheOptionsRegisteredInDI()
    {
        DashboardOptions registered = new() { IsReadOnlyFunc = static _ => true, DashboardTitle = "From DI" };
        await using RunningApp app = await StartAsync(services => services.AddSingleton(registered), static app => app.MapNightShiftDashboardWithNoAuthorizationFilters("/nofilters"));

        string page = await app.GetStringAsync("/nofilters/jobs/scheduled");
        HttpResponseMessage command = await app.PostAsync("/nofilters/jobs/actions/delete/1", request => request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest"));

        Assert.Contains("From DI", page);
        Assert.Equal(HttpStatusCode.Unauthorized, command.StatusCode);
        _client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);

        // The registered instance keeps its filters for the other dashboards that use it
        Assert.IsType<LocalRequestsOnlyAuthorizationFilter>(Assert.Single(registered.Authorization));
    }

    [Fact]
    public async Task NoAuthorizationFilters_DoesNotClearTheFiltersOfTheOptionsPassed()
    {
        IDashboardAuthorizationFilter filter = new Mock<IDashboardAuthorizationFilter>().Object;
        DashboardOptions options = new() { Authorization = [filter] };
        await using RunningApp app = await StartAsync(map: app => app.MapNightShiftDashboardWithNoAuthorizationFilters("/nofilters", options));

        HttpResponseMessage response = await app.GetAsync("/nofilters/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Same(filter, Assert.Single(options.Authorization));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseOne_OfAnIdWithALiteralPercent2F_PausesThatJob_NotTheOneWithASlash(bool middleware)
    {
        // Kestrel decodes "%25" but keeps "%2F", so encodeURIComponent("a%2Fb") reached the dashboard as the
        // "a%2Fb" that encodeURIComponent("a/b") gives, and the job "a/b" was paused
        Mock<IRecurringJobManagerV3> manager = RecurringJobManager();
        await using RunningApp app = await StartAsync(
            services => services.AddSingleton(Factory(manager.Object)),
            middleware ? static app => app.UseNightShiftDashboard() : static app => app.MapNightShiftDashboard());

        // What the pages send now: "~" escapes "%", "/" and itself inside the segment, which no host decodes
        HttpResponseMessage literal = await app.PostAsync("/nightshift/recurring/pause-one/a~252Fb", ScriptPost);
        HttpResponseMessage slash = await app.PostAsync("/nightshift/recurring/resume-one/a~2Fb", ScriptPost);

        Assert.Equal(HttpStatusCode.NoContent, literal.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, slash.StatusCode);
        manager.Verify(x => x.Pause("a%2Fb"), Times.Once);
        manager.Verify(x => x.Pause("a/b"), Times.Never);
        manager.Verify(x => x.Resume("a/b"), Times.Once);
    }

    [Theory]
    [InlineData("/nightshift/recurring/pause-one/reports%2Fdaily", "reports/daily")]
    [InlineData("/nightshift/recurring/pause-one/nightly%20report", "nightly report")]
    [InlineData("/nightshift/recurring/pause-one/plain-id", "plain-id")]
    public async Task PauseOne_StillUnderstands_LinksEncodedWithEncodeURIComponent(string path, string expected)
    {
        // What the pages sent before, and what an extension that encodes the id itself still sends
        Mock<IRecurringJobManagerV3> manager = RecurringJobManager();
        await using RunningApp app = await StartAsync(services => services.AddSingleton(Factory(manager.Object)), static app => app.MapNightShiftDashboard());

        HttpResponseMessage response = await app.PostAsync(path, ScriptPost);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        manager.Verify(x => x.Pause(expected), Times.Once);
    }

    private static Mock<IRecurringJobManagerV3> RecurringJobManager()
    {
        Mock<IRecurringJobManagerV3> manager = new();
        manager.Setup(x => x.Pause(It.IsAny<string>())).Returns(true);
        manager.Setup(x => x.Resume(It.IsAny<string>())).Returns(true);
        return manager;
    }

    private static IRecurringJobManagerFactory Factory(IRecurringJobManager manager)
    {
        Mock<IRecurringJobManagerFactory> factory = new();
        factory.Setup(x => x.GetManager(It.IsAny<JobStorage>())).Returns(manager);
        return factory.Object;
    }

    private static void ScriptPost(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
    }

    private async Task<RunningApp> StartAsync(Action<IServiceCollection> services = null, Action<WebApplication> map = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_storage.Object);
        builder.Services.AddSingleton(_clientFactory.Object);
        builder.Services.AddNightShift(_ => { });
        services?.Invoke(builder.Services);

        WebApplication app = builder.Build();
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
            HttpRequestMessage request = new(HttpMethod.Post, BaseAddress + path)
            {
                Content = new FormUrlEncodedContent([]),
            };
            configure?.Invoke(request);
            return Client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public Task<HttpResponseMessage> GetAsync(string path, Action<HttpRequestMessage> configure = null)
        {
            HttpRequestMessage request = new(HttpMethod.Get, BaseAddress + path);
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
