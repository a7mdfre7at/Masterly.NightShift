# Customizing the dashboard

Add your own pages, menu items, home page metrics, JSON endpoints and buttons to the dashboard, ship CSS and JavaScript with them, and control the language and theme. Use this when you want operators to see and do app-specific things next to the built-in pages.

## Quick example

A page at `/nightshift/reports` with its own menu item:

```csharp
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;

public sealed class ReportsPage : RazorPage
{
    public override void Execute()
    {
        Layout = new LayoutPage("Reports");

        WriteLiteral("<h1 class=\"hf-page-header\">Reports</h1>");
        WriteLiteral("<p>Succeeded jobs so far: ");
        Write(Statistics.Succeeded);   // Write HTML-encodes; WriteLiteral doesn't
        WriteLiteral("</p>");
    }
}
```

Register it once at startup, before the app starts serving requests:

```csharp
DashboardRoutes.Routes.AddRazorPage("/reports", _ => new ReportsPage());

NavigationMenu.Items.Add(page => new MenuItem("Reports", page.Url.To("/reports"))
{
    Icon = "card-list",
    Active = page.RequestPath.StartsWith("/reports", StringComparison.OrdinalIgnoreCase),
});
```

## How it works

Every extension point is a static registry in `Masterly.NightShift.Core`:

| Registry | Holds |
|---|---|
| `DashboardRoutes.Routes` | Path patterns and the dispatcher (page, command, JSON, file) for each. |
| `NavigationMenu.Items` | The sidebar's main menu. |
| `JobsSidebarMenu.Items` | The sub-items under "Jobs". |
| `DashboardMetrics` | Metrics that `/stats` can refresh in the browser. |
| Home page metrics (`UseDashboardMetric`) | Tiles at the top of the home page. |
| Job details renderers (`UseJobDetailsRenderer`) | Extra sections on the job page. |
| `JobHistoryRenderer` | How each state is drawn in a job's history. |
| `DashboardRoutes.AddStylesheet` / `AddStylesheetDarkMode` / `AddJavaScript` | Embedded CSS and JS bundled into the dashboard's single stylesheet and script. |

They are shared by every dashboard in the process (all paths, all storages). Register everything at startup: the lists are not meant to be changed while requests are being served. In ASP.NET Core the dashboard methods take the route table from DI: `AddNightShift` registers `DashboardRoutes.Routes` as the `RouteCollection` service unless one is registered already.

**Routes.** A route's path is a regular expression matched against the request path relative to the dashboard (for example `/reports/42` for `/nightshift/reports/42`). The pattern is anchored at both ends, matched case-insensitively with a one-second timeout, and a trailing `/` on the request is ignored. Named groups such as `(?<ReportId>[^/]+)` are available to the dispatcher through `context.UriMatch`. Routes are tried in the order they were added and **the first match wins**; the built-in routes are added first, so give your routes their own prefix (such as `/reports`) rather than one under `/jobs/enqueued/`, `/jobs/details/`, `/batches/` or `/tags/`, whose patterns catch everything below them.

**Pages.** Built-in pages are `.cshtml` files compiled by a source generator that isn't shipped, so in your app you write a page as a class that derives from `RazorPage` and writes HTML in `Execute()`. Setting `Layout = new LayoutPage(title)` wraps your content in the dashboard's sidebar, top bar, styles and scripts; without it, your page is sent as is.

**Commands.** A command route (`AddCommand` or one of the batch commands) only accepts `POST`, answers 401 to read-only users, and is recorded in the audit log. A `GET` for its path goes to a later route that matches too, such as a page, and answers 405 only when there is none. The dashboard's script turns any element with a `data-ajax` attribute into a button that posts to that URL with the antiforgery token and the `X-Requested-With` header, then reloads the page; when the request fails, it shows the status and the server's plain-text or JSON `error` message at the top of the page.

## Options / API

### Routes

| Member | Signature | Behavior |
|---|---|---|
| `DashboardRoutes.Routes` | `RouteCollection` | The route table. |
| `Add` | `Add(string pathTemplate, IDashboardDispatcher dispatcher)` | Any dispatcher. You handle method, read-only and response yourself. |
| `AddRazorPage` | `AddRazorPage(string pathTemplate, Func<Match, RazorPage> pageFunc)` | Renders a page as `text/html`. `pageFunc` gets the regex match, so you can pass route values to the page's constructor. |
| `AddCommand` | `AddCommand(string pathTemplate, Func<DashboardContext, bool> command)` | `POST` only (otherwise 405); 401 when read-only; `true` answers 204, `false` answers 422. Recorded in the audit log, with the route's named group values as targets. |
| `AddBatchCommand` | `AddBatchCommand(string pathTemplate, Action<DashboardContext, string> command)` | `POST` only (otherwise 405); 401 when read-only. Reads the `jobs[]` form values and calls `command` for each (422 when there are none); answers 204. Recorded in the audit log. |
| `AddClientBatchCommand` | `AddClientBatchCommand(string pathTemplate, Action<IBackgroundJobClient, string> command)` | `AddBatchCommand` with the dashboard's `IBackgroundJobClient`. |
| `AddRecurringBatchCommand` | `AddRecurringBatchCommand(string pathTemplate, Action<IRecurringJobManager, string> command)` | `AddBatchCommand` with the dashboard's `IRecurringJobManager`; the values are recurring job ids. |
| `RouteCollection.FindDispatcher` | `FindDispatcher(string path)`, `FindDispatcher(string path, string? method)` | Returns the dispatcher and match for a path, or `null`. With `GET` or `HEAD` as the method, command routes are skipped when a later route matches too, as the dashboard does for real requests. Handy in tests. |

### Dispatchers and context

| Type / member | Meaning |
|---|---|
| `IDashboardDispatcher.Dispatch(DashboardContext context)` | Returns `Task`. Handles one request. |
| `DashboardContext.Storage` / `Options` | The dashboard's `JobStorage` and `DashboardOptions`. |
| `DashboardContext.UriMatch` | The route's regex `Match`. |
| `DashboardContext.IsReadOnly` | Result of `IsReadOnlyFunc` for this request. |
| `DashboardContext.GetBackgroundJobClient()` / `GetRecurringJobManager()` | Client and manager bound to the dashboard's storage (from DI in ASP.NET Core). |
| `DashboardContext.GetUserName()` | The signed-in user's name, or `null`. |
| `DashboardRequest` (`context.Request`) | `Method`, `Path` (relative to the dashboard), `PathBase`, `LocalIpAddress`, `RemoteIpAddress`, `GetQuery(key)`, `GetFormValuesAsync(key)`, `GetHeader(name)` (`null` when the header is missing, or on hosts other than ASP.NET Core). Values captured from the path are already decoded by ASP.NET Core, except `%2F`, which stays as is. The built-in pages write `%`, `/` and `~` inside a value as `~25`, `~2F` and `~7E` before encoding it, so that a literal `%2F` and a `/` stay apart; a link from an extension to a built-in page should do the same with `NightShift.routeValue(value)` in scripts. |
| `DashboardResponse` (`context.Response`) | `ContentType`, `StatusCode`, `Body`, `WriteAsync(text)`, `SetExpire(DateTimeOffset?)`. |
| `context.GetHttpContext()` | The ASP.NET Core `HttpContext` (namespace `Masterly.NightShift.Dashboard`, package `Masterly.NightShift.AspNetCore`). |

### Pages

| `RazorPage` member | Meaning |
|---|---|
| `Execute()` | Override it and write the page. |
| `WriteLiteral(string)` | Writes text as is (HTML). |
| `Write(object)` | Writes a value HTML-encoded, unless it is a `NonEscapedString` (what `Html.*` helpers return). |
| `Layout` | Set to `new LayoutPage(title)` (namespace `Masterly.NightShift.Dashboard.Pages`) to get the dashboard chrome. |
| `Url.To(path)`, `Url.JobDetails(id)`, `Url.Queue(name)`, `Url.LinkToQueues()`, `Url.Home()` | Links that include the dashboard path and `PrefixPath`. `JobDetails` and `Queue` write the id or name as one path segment, as the built-in pages do (`~25`, `~2F`, `~7E`, then URL encoding); `To` takes the path as it is. |
| `Html` | Helpers: `HtmlEncode`, `Raw`, `Icon` (see [Icons](#icons)), `JobId` (a short id), `JobIdLink`, `JobNameLink`, `JobName`, `StateLabel`, `QueueLabel`, `ServerId`, `StackTrace`, `RelativeTime`, `LocalTime`, `ToHumanDuration`, `Breadcrumbs`, `Paginator`, `PerPageSelector`, `BlockMetric`, `InlineMetric`, `RenderPartial`. |
| `Statistics` | The storage's `StatisticsDto` (counts by state, servers, queues, recurring jobs), read once per request. |
| `Storage`, `Context`, `DashboardOptions`, `IsReadOnly`, `RequestPath`, `Query(key)` | The request's context. |
| `StorageUtcNow`, `ApplicationUtcNow`, `TimeDifference` | The storage's clock (when supported), your app's clock, and the difference. |
| `Pager` | Paging state for a list page: `new Pager(from, perPage, defaultPerPage, total)`. `FromRecord` and `RecordsPerPage` are what to ask the storage for; `Html.Paginator(pager)` and `Html.PerPageSelector(pager)` draw the page links and the per-page buttons. A page never has more than `Pager.MaxRecordsPerPage` (1000) rows. See [A page with paging](#a-page-with-paging). |

### Menu items

`NavigationMenu.Items` and `JobsSidebarMenu.Items` are `List<Func<RazorPage, MenuItem>>`. The function runs on every page render; return `null` to hide the item for that request.

| `MenuItem` member | Type | Meaning |
|---|---|---|
| `MenuItem(text, url)` | constructor | Label and link. |
| `Active` | `bool` | Highlights the item. Usually `page.RequestPath.StartsWith(...)`. |
| `Icon` | `string` | The name of an icon of the dashboard's sprite, such as `"clock"` or `"card-list"` (see [Icons](#icons)). A Glyphicon name of earlier versions, such as `"time"`, gives the icon that replaced it. Without an icon, or with a name the sprite doesn't hold, a chevron is shown. Only used in the main menu. |
| `Metric` / `Metrics` | `DashboardMetric` / `DashboardMetric[]` | Counters shown next to the label. |

### Metrics

| Member | Meaning |
|---|---|
| `new DashboardMetric(name, title, func)` | `name` is a unique key (such as `"reports:pending"`), `title` the label, `func` computes a `Metric` from the page (return `null` to show nothing). `new DashboardMetric(name, func)` uses the name as the title. |
| `DashboardMetric.Url` | Makes the home page tile a link. |
| `DashboardMetric.Title` | If it is the name of a dashboard resource string (such as `"Metrics_Servers"`), the translated text is shown; otherwise the text itself. |
| `new Metric(long value)` / `new Metric(string value)` | The value. The `long` form formats with thousands separators and keeps the number in `IntValue`; `Value` is the text shown. |
| `Metric.Style` | `MetricStyle.Default`, `Info`, `Success`, `Warning` or `Danger`. |
| `Metric.Highlighted` | Draws attention to the value. |
| `Metric.Title` | Tooltip. |
| `DashboardMetrics.AddMetric(metric)` / `GetMetrics()` | Lets `/stats` refresh the metric live in the browser; `GetMetrics()` lists the registered ones. |
| `DashboardMetrics.ServerCount`, `RecurringJobCount`, `RetriesCount`, `EnqueuedCountOrNull`, `FailedCountOrNull`, `EnqueuedAndQueueCount`, `ScheduledCount`, `ProcessingCount`, `SucceededCount`, `FailedCount`, `DeletedCount`, `AwaitingCount` | The built-in metrics, ready to reuse. |
| `configuration.UseDashboardMetric(metric)` / `UseDashboardMetrics(params metrics)` | `IGlobalConfiguration` extensions: add to the home page and to `DashboardMetrics`. |

### Job page and states

| Member | Meaning |
|---|---|
| `configuration.UseJobDetailsRenderer(int order, Func<JobDetailsRendererDto, NonEscapedString> renderer)` | Adds a section to every job page, after the built-in sections and before the state history. Lower `order` comes first. The DTO has `Page`, `JobId` and `JobDetails` (with `Job`, `Properties`, `History`). If the renderer throws, the exception is shown in its place. |
| `JobHistoryRenderer.Register(string state, Func<HtmlHelper, IDictionary<string, string>, NonEscapedString> renderer)` | Draws a state's data in the history. Replaces the renderer if one exists. States without one get a key/value list. |
| `JobHistoryRenderer.AddStateCssSuffix(string stateName, string cssSuffix)` | Gives the state's labels the CSS class `label-state-{cssSuffix}` and its current-state card the class `state-card-state-{cssSuffix}`. The built-in suffixes are `active`, `success`, `danger`, `warning`, `info` and `inactive`; with one of them the state also gets that suffix's colors in the job lineage diagram and the time-in-state bar. Calling it again for the same state replaces the suffix. |
| `JobHistoryRenderer.GetStateCssSuffix(string stateName)` | The state's CSS suffix, or `null` when it has none. A state without a suffix gets the neutral label and card look. |
| `JobHistoryRenderer.Exists(string state)` | Whether a renderer is registered for the state. |

### Static resources

| Member | Meaning |
|---|---|
| `DashboardRoutes.AddStylesheet(Assembly assembly, string resource)` | Appends an embedded CSS resource to the dashboard stylesheet. |
| `DashboardRoutes.AddStylesheetDarkMode(Assembly assembly, string resource)` | Appends an embedded CSS resource to the dark stylesheet, used only when `DarkModeEnabled` is `true`. |
| `DashboardRoutes.AddJavaScript(Assembly assembly, string resource)` | Appends an embedded JS resource to the dashboard script. |

## More examples

### A page with a route value and a button

```csharp
public sealed class ReportDetailsPage : RazorPage
{
    private readonly string _reportId;

    public ReportDetailsPage(string reportId)
    {
        _reportId = reportId;
    }

    public override void Execute()
    {
        Layout = new LayoutPage($"Report {_reportId}");

        WriteLiteral("<h1 class=\"hf-page-header\">Report ");
        Write(_reportId);
        WriteLiteral("</h1>");

        if (!IsReadOnly)
        {
            WriteLiteral($"<button class=\"btn btn-sm btn-primary\" data-ajax=\"{Html.HtmlEncode(Url.To("/reports/rebuild/" + _reportId))}\" data-confirm=\"Rebuild this report?\">Rebuild</button>");
        }
    }
}
```

```csharp
DashboardRoutes.Routes.AddRazorPage(
    "/reports/details/(?<ReportId>[^/]+)",
    match => new ReportDetailsPage(match.Groups["ReportId"].Value));
```

The button posts to the command defined in [A command route](#a-command-route). `data-confirm` asks first; leave it out to post straight away. While the request runs, the button is disabled and shows its `data-loading-text`, when it has one.

### A page that uses the dashboard's helpers

```csharp
public sealed class LatestFailuresPage : RazorPage
{
    public override void Execute()
    {
        Layout = new LayoutPage("Latest failures");

        var failed = Storage.GetMonitoringApi().FailedJobs(0, 20);

        WriteLiteral("<h1 class=\"hf-page-header\">Latest failures</h1><table class=\"table\"><tbody>");
        foreach (var job in failed)
        {
            WriteLiteral("<tr><td>");
            Write(Html.JobIdLink(job.Key));
            WriteLiteral("</td><td>");
            Write(Html.JobNameLink(job.Key, job.Value?.Job));
            WriteLiteral("</td><td>");
            if (job.Value?.FailedAt is DateTime failedAt)
            {
                Write(Html.RelativeTime(failedAt));
            }
            WriteLiteral("</td></tr>");
        }
        WriteLiteral("</tbody></table>");
    }
}
```

`Html.RelativeTime` and `Html.LocalTime` output dates the browser reformats, like the built-in pages (see [Dates and times](#dates-and-times)).

### A page with paging

List pages read `from` and `count` from the query string and let `Pager` work out the rest. The paginator's links keep the page's path and set those two values.

```csharp
public sealed class ReportFailuresPage : RazorPage
{
    public override void Execute()
    {
        Layout = new LayoutPage("Report failures");

        int.TryParse(Query("from"), out var from);
        int.TryParse(Query("count"), out var perPage);

        var monitor = Storage.GetMonitoringApi();
        var pager = new Pager(from, perPage, DashboardOptions.DefaultRecordsPerPage, monitor.FailedCount());
        var failed = monitor.FailedJobs(pager.FromRecord, pager.RecordsPerPage);

        WriteLiteral("<h1 class=\"hf-page-header\">Report failures</h1>");
        Write(Html.PerPageSelector(pager));
        WriteLiteral("<table class=\"table\"><tbody>");
        foreach (var job in failed)
        {
            WriteLiteral("<tr><td>");
            Write(Html.JobIdLink(job.Key));
            WriteLiteral("</td><td>");
            Write(job.Value?.ExceptionMessage);
            WriteLiteral("</td></tr>");
        }
        WriteLiteral("</tbody></table>");
        Write(Html.Paginator(pager));
    }
}
```

### Adding a menu item or a Jobs sub-item

```csharp
JobsSidebarMenu.Items.Add(page => new MenuItem("Quarantined", page.Url.To("/jobs/quarantined"))
{
    Active = page.RequestPath.StartsWith("/jobs/quarantined", StringComparison.OrdinalIgnoreCase),
});

// Put "Reports" right after "Jobs" instead of at the end
NavigationMenu.Items.Insert(1, page => new MenuItem("Reports", page.Url.To("/reports")) { Icon = "card-list" });
```

Hide an item for some users by returning `null`:

```csharp
NavigationMenu.Items.Add(page => page.IsReadOnly
    ? null
    : new MenuItem("Maintenance", page.Url.To("/servers")) { Icon = "hdd-fill" });
```

### Adding a metric to the home page

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Dashboard;

public static class Metrics
{
    public static readonly DashboardMetric PendingReports = new DashboardMetric(
        "reports:pending",
        "Pending reports",
        page =>
        {
            var pending = page.Statistics.Enqueued;   // replace with your own query
            return new Metric(pending)
            {
                Style = pending > 100 ? MetricStyle.Warning : MetricStyle.Default,
                Highlighted = pending > 100,
                Title = "Reports waiting to be built",
            };
        });
}
```

```csharp
services.AddNightShift(config => config
    .UseSqlServerStorage(connectionString)
    .UseDashboardMetric(Metrics.PendingReports));
```

`UseDashboardMetric` also registers the metric with `DashboardMetrics`, so the tile updates live while the home page is open. The function runs on every page render and every poll (every `StatsPollingInterval` milliseconds per open tab), so keep it fast; `page.Storage` gives you the storage if you need more than `page.Statistics`.

The built-in metrics can go on the home page too:

```csharp
GlobalConfiguration.Configuration.UseDashboardMetrics(
    DashboardMetrics.ServerCount,
    DashboardMetrics.RecurringJobCount,
    DashboardMetrics.FailedCount);
```

### A metric next to a menu item

```csharp
DashboardMetrics.AddMetric(Metrics.PendingReports);   // so /stats can refresh it

NavigationMenu.Items.Add(page => new MenuItem("Reports", page.Url.To("/reports"))
{
    Icon = "card-list",
    Active = page.RequestPath.StartsWith("/reports", StringComparison.OrdinalIgnoreCase),
    Metric = Metrics.PendingReports,
});
```

### A JSON route

```csharp
using System.Text.Json;
using Masterly.NightShift.Dashboard;

public sealed class ReportStatusDispatcher : IDashboardDispatcher
{
    public async Task Dispatch(DashboardContext context)
    {
        var statistics = context.Storage.GetMonitoringApi().GetStatistics();

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            enqueued = statistics.Enqueued,
            failed = statistics.Failed,
            filter = context.Request.GetQuery("filter"),
        }));
    }
}
```

```csharp
DashboardRoutes.Routes.Add("/reports/status", new ReportStatusDispatcher());
```

A plain dispatcher answers every HTTP method and ignores `IsReadOnly`. If it changes data, check `context.Request.Method` and `context.IsReadOnly` yourself, or use `AddCommand`.

### A command route

```csharp
DashboardRoutes.Routes.AddCommand("/reports/rebuild/(?<ReportId>[^/]+)", context =>
{
    var reportId = context.UriMatch.Groups["ReportId"].Value;
    if (reportId.Length == 0) return false;   // answers 422

    context.GetBackgroundJobClient().Enqueue<ReportService>(x => x.BuildDaily());
    return true;                              // answers 204
});
```

### Batch commands for selected jobs

Built-in job lists post the selected ids as `jobs[]`. Your batch commands receive them one at a time:

```csharp
DashboardRoutes.Routes.AddClientBatchCommand("/reports/requeue", (client, jobId) =>
    client.Requeue(jobId));

DashboardRoutes.Routes.AddRecurringBatchCommand("/reports/trigger", (manager, recurringJobId) =>
    manager.Trigger(recurringJobId));

DashboardRoutes.Routes.AddBatchCommand("/reports/forget", (context, jobId) =>
{
    using var connection = context.Storage.GetConnection();
    connection.SetJobParameter(jobId, "ReportForgotten", "true");
});
```

### Adding CSS and JavaScript

Embed the files in your assembly:

```xml
<ItemGroup>
  <EmbeddedResource Include="Dashboard\reports.css" />
  <EmbeddedResource Include="Dashboard\reports-dark.css" />
  <EmbeddedResource Include="Dashboard\reports.js" />
</ItemGroup>
```

The resource name is the root namespace plus the folder path with dots, for example `MyApp.Dashboard.reports.css`. Check with `typeof(ReportsPage).Assembly.GetManifestResourceNames()` if unsure.

```csharp
var assembly = typeof(ReportsPage).Assembly;

DashboardRoutes.AddStylesheet(assembly, "MyApp.Dashboard.reports.css");
DashboardRoutes.AddStylesheetDarkMode(assembly, "MyApp.Dashboard.reports-dark.css");
DashboardRoutes.AddJavaScript(assembly, "MyApp.Dashboard.reports.js");
```

Your files are appended after the built-in ones, in the order you add them, to one stylesheet (`/css...`), one dark stylesheet (`/css-dark...`) and one script (`/js...`), shared by every dashboard in the process. The browser caches them for a year; the number in their URL changes when the set of resources or the build of an assembly that provides them changes, so a deployment never serves stale files. A missing resource name makes the whole bundle fail, so test it.

### Serving another embedded file

For an image or a file that shouldn't be bundled, write a dispatcher:

```csharp
public sealed class ReportLogoDispatcher : IDashboardDispatcher
{
    public async Task Dispatch(DashboardContext context)
    {
        await using var stream = typeof(ReportLogoDispatcher).Assembly.GetManifestResourceStream("MyApp.Dashboard.logo.svg");
        if (stream == null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        context.Response.ContentType = "image/svg+xml";
        context.Response.SetExpire(DateTimeOffset.UtcNow.AddDays(7));
        await stream.CopyToAsync(context.Response.Body);
    }
}
```

```csharp
DashboardRoutes.Routes.Add("/reports/logo.svg", new ReportLogoDispatcher());
```

Reference it from a page with `Url.To("/reports/logo.svg")`.

### Markup and scripts

The pages are built with [Bootstrap 5.3](https://getbootstrap.com/docs/5.3/) and [jQuery 4](https://jquery.com/), both in the dashboard's script bundle, with Popper inside `bootstrap.bundle`. Your pages and scripts can use them:

- Bootstrap 5 classes: `btn btn-sm btn-primary`, `btn-outline-secondary` for the plain buttons, `badge text-bg-success` for labels, `table table-sm`, `card`, `form-control`, `form-select`, `mb-3`, `float-end`, `text-end`, `d-none d-md-block`, `visually-hidden`. The dashboard restyles them in its own colors, in light and dark. Two classes of its own fill gaps: `hf-btn-xs`, a button smaller than `btn-sm`, and `hf-dl-horizontal`, a definition list with the terms beside the descriptions. Page titles use `hf-page-header`.
- Bootstrap's components through `data-bs-*` attributes (`data-bs-toggle`, `data-bs-dismiss="modal"`) or its JavaScript API, the `bootstrap` global: `bootstrap.Modal.getOrCreateInstance(element).show()`.
- Tooltips: every element with a `title` when the page loads gets a Bootstrap tooltip. For content you add later, call `NightShift.tooltips(container)`; to change the title of an element that has one, `NightShift.setTitle(element, text)`, since Bootstrap moves the `title` into `data-bs-original-title`.
- `NightShift.setLoading(button, true)` disables a button and shows its `data-loading-text` while your request runs; `NightShift.setLoading(button, false)` gives its content back.
- jQuery 4 removed `$.trim`, `$.isArray`, `$.isFunction`, `$.parseJSON`, `$.type`, `$.now` and other deprecated helpers: use the standard JavaScript ones (`String.prototype.trim`, `Array.isArray`, `JSON.parse`, `Date.now`). See the [jQuery 4 upgrade guide](https://jquery.com/upgrade-guide/4.0/).

Pages written for Bootstrap 3 need the usual changes ([Bootstrap's migration guide](https://getbootstrap.com/docs/5.3/migration/)): `label label-*` becomes `badge text-bg-*`, `panel` becomes `card`, `btn-default` becomes `btn-outline-secondary`, `pull-right` becomes `float-end`, `data-toggle` becomes `data-bs-toggle`, and so on. `page-header` and `dl-horizontal` still get their old look, so existing pages keep their titles and lists.

### Icons

The dashboard's icons are [Bootstrap Icons](https://icons.getbootstrap.com/) in one SVG sprite (the route `/icons...`), which holds the icons the dashboard uses. Draw one with `Html.Icon(name)` in a page, or `NightShift.icon(name)` in a script, which returns the same HTML:

```csharp
WriteLiteral("<button class=\"btn btn-sm btn-outline-secondary\" data-ajax=\"...\">");
Write(Html.Icon("arrow-repeat"));
WriteLiteral(" Rebuild</button>");
```

```js
$('<span>').html(NightShift.icon('check-lg', 'text-success'));
```

The icon is an inline `<svg class="bi">` that takes the size and color of the text around it, and is hidden from screen readers (`aria-hidden="true"`): give a button that shows only an icon an `aria-label` or a `title`. `Html.Icon(name, cssClass)` adds classes to the `svg`.

The sprite holds `arrow-clockwise`, `arrow-repeat`, `arrows`, `arrows-fullscreen`, `bar-chart-fill`, `book-fill`, `box-arrow-down`, `box-arrow-left`, `calendar3`, `card-list`, `chat-left-text-fill`, `check-circle`, `check-lg`, `chevron-right`, `circle-half`, `clock`, `copy`, `download`, `easel-fill`, `exclamation-circle-fill`, `exclamation-triangle-fill`, `eye-fill`, `fire`, `flag-fill`, `folder2-open`, `grid-fill`, `hdd-fill`, `house-door-fill`, `info-circle-fill`, `lightning-fill`, `link-45deg`, `list`, `list-task`, `moon-stars`, `pause-fill`, `pencil-fill`, `play-circle`, `play-fill`, `plus-lg`, `question-circle-fill`, `search`, `shuffle`, `sun-fill`, `tag-fill`, `terminal-fill`, `text-left`, `trash-fill`, `upload`, `x-circle-fill`, `x-lg`, `zoom-in` and `zoom-out`. The Glyphicon names the dashboard used before, such as `trash` or `time`, give the icon that replaced them. For another icon, put the Bootstrap Icons SVG in your page.

### Adding a section to the job page

```csharp
GlobalConfiguration.Configuration.UseJobDetailsRenderer(10, dto =>
{
    if (!dto.JobDetails.Properties.TryGetValue("TenantId", out var tenantId))
    {
        return new NonEscapedString(string.Empty);
    }

    return new NonEscapedString(
        $"<h3>Tenant</h3><p><code>{dto.Page.Html.HtmlEncode(tenantId)}</code></p>");
});
```

Parameters set from filters with `SetJobParameter(name, value)` are stored as JSON, so a string value arrives with its quotes.

### Showing a custom state

If you have your own `IState` (see [Custom filters](../filters/custom-filters.md)), tell the dashboard how to draw it:

```csharp
// Reuse the look of a built-in state: active, success, danger, warning, info or inactive
JobHistoryRenderer.AddStateCssSuffix("Quarantined", cssSuffix: "warning");
JobHistoryRenderer.Register("Quarantined", (html, data) =>
    new NonEscapedString(
        $"<dl class=\"hf-dl-horizontal\"><dt>Reason</dt><dd>{html.HtmlEncode(data.TryGetValue("Reason", out var reason) ? reason : "-")}</dd></dl>"));
```

### Localization

The dashboard's text comes from resources in these languages:

| Culture | Language |
|---|---|
| (default) | English |
| `ca` | Catalan |
| `de` | German |
| `es` | Spanish |
| `fa` | Persian |
| `fr` | French |
| `nb` | Norwegian Bokmål |
| `nl` | Dutch |
| `pt` | Portuguese |
| `pt-BR` | Portuguese (Brazil) |
| `pt-PT` | Portuguese (Portugal) |
| `ru` | Russian |
| `sv` | Swedish |
| `tr-TR` | Turkish |
| `zh` | Chinese (Simplified) |
| `zh-TW` | Chinese (Traditional) |

The Catalan, German, Spanish, French, Dutch, Turkish and both Chinese translations don't cover every string yet; missing strings appear in English.

The language is the request's `CultureInfo.CurrentUICulture`. In ASP.NET Core, set it per request with the request localization middleware, before the dashboard:

```csharp
string[] cultures = ["en-US", "de-DE", "fr-FR", "pt-BR"];

app.UseRequestLocalization(options => options
    .SetDefaultCulture(cultures[0])
    .AddSupportedCultures(cultures)
    .AddSupportedUICultures(cultures));

app.MapNightShiftDashboard();
```

By default the middleware picks the culture from the `culture`/`ui-culture` query string, a cookie, or the browser's `Accept-Language` header. Your own pages and menu items aren't translated by the dashboard; menu item functions run per request, so you can look up your own resources there.

### Theming and dark mode

With `DarkModeEnabled = true` (the default) the sidebar has a theme switcher: follow the system, light, or dark. The choice is saved in the browser's local storage. Add `?theme=dark`, `?theme=light` or `?theme=system` to any dashboard URL to set it from a link, which is handy for a screen that shows the dashboard. Set `DarkModeEnabled = false` to always use the light theme; the dark stylesheet and the switcher are then left out.

The [Wallboard](pages.md#wallboard) is always dark, whatever the setting: it is made for a screen on a wall, where a dark page is easier to read from across the room.

The stylesheet is built on CSS custom properties such as `--hf-primary`, `--hf-bg`, `--hf-surface` and `--hf-sidebar-bg`, set on `:root` and again on `:root[data-theme="dark"]`. To change colors, override them in a stylesheet you add with `AddStylesheet`:

```css
:root {
    --hf-primary: #7c3aed;
    --hf-sidebar-bg: #1e1b4b;
}

:root[data-theme="dark"] {
    --hf-primary: #a78bfa;
}
```

Bootstrap's own variables (`--bs-body-bg`, `--bs-body-color`, `--bs-border-color` and the fonts) are set from these, so Bootstrap components in your pages follow the theme too; the dashboard doesn't use Bootstrap's `data-bs-theme`. These names are taken from the current stylesheet and may change between versions.

### Dates and times

The server writes dates into the page as text with a machine-readable timestamp, and the dashboard's script (`nightshift-intl.js`) rewrites them in the browser with the `Intl` APIs:

- in the viewer's time zone;
- in the language of the page (the two-letter code of `CurrentUICulture`); for a code the browser doesn't know, such as the invariant culture, the browser's own language;
- relative times ("3 minutes ago") use the same thresholds moment.js used, and date arithmetic, including across daylight saving changes, matches moment's.

The same code is the date adapter of the Chart.js 2.x graphs, so moment.js isn't loaded. Hourly statistics, such as the Insights heatmap, are in UTC.

In your pages, use `Html.RelativeTime(dateTime)` ("3 minutes ago", full date on hover) and `Html.LocalTime(dateTime)` (a full local date and time) to get the same behavior. In your scripts, `NightShiftIntl.format(time, name)` formats a Unix time in milliseconds with one of the named formats `time`, `timeWithSeconds`, `shortDate`, `longDate`, `dateTime`, `dateTimeWithSeconds`, `fullDateTime`, `numericDateTime`, `month` or `year`, and `NightShiftIntl.fromNow(time)` gives the relative form.

## Things to know

- Registries are global and static. Two dashboards in one process show the same custom pages and menu items, and tests that register routes affect each other.
- A route added after a built-in route with an overlapping pattern is never reached. Check with `DashboardRoutes.Routes.FindDispatcher("/your/path")`.
- Everything you write with `WriteLiteral`, `Html.Raw` or `NonEscapedString` goes to the browser as HTML. Encode anything that comes from job data or users with `Write(...)` or `Html.HtmlEncode(...)`.
- Custom routes are protected by the dashboard's authorization filters like built-in ones, but a plain `IDashboardDispatcher` must check `IsReadOnly` itself.
- The built-in CSS classes and the `--hf-*` variables are not a stable API.

## See also

- [Setting up the dashboard](setup.md)
- [Authorization](authorization.md)
- [Pages](pages.md)
- [Global configuration](../configuration/global-configuration.md)
- [Custom filters](../filters/custom-filters.md)
- [Insights](../monitoring/insights.md)
