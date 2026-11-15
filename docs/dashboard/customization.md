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
| `Html` | Helpers: `HtmlEncode`, `Raw`, `Icon` (see [Icons](#icons)), `JobId` (a short id), `JobIdLink`, `JobNameLink`, `JobName`, `StateLabel` (a state chip, see [Components](#components)), `JobCheckbox` and `SelectAllCheckbox` (the selection boxes of a list, with accessible names), `ConfirmAttributes` (see [Confirmations](#confirmations)), `QueueLabel`, `ServerId`, `StackTrace`, `RelativeTime`, `LocalTime`, `ToHumanDuration`, `Breadcrumbs`, `Paginator`, `PerPageSelector`, `BlockMetric`, `InlineMetric`, `RenderPartial`. |
| `Statistics` | The storage's `StatisticsDto` (counts by state, servers, queues, recurring jobs), read once per request. |
| `Storage`, `Context`, `DashboardOptions`, `IsReadOnly`, `RequestPath`, `Query(key)` | The request's context. |
| `StorageUtcNow`, `ApplicationUtcNow`, `TimeDifference` | The storage's clock (when supported), your app's clock, and the difference. |
| `Pager` | Paging state for a list page: `new Pager(from, perPage, defaultPerPage, total)`. `FromRecord` and `RecordsPerPage` are what to ask the storage for; `Html.Paginator(pager)` draws the pagination footer (a `<nav aria-label="Pagination">` with the rows-per-page menu, "1–20 of N", the page links and the previous/next buttons) and `Html.PerPageSelector(pager)` the compact "1–20 of N" with previous/next for the list's toolbar. A page never has more than `Pager.MaxRecordsPerPage` (1000) rows. See [A page with paging](#a-page-with-paging). |

### Menu items

`NavigationMenu.Items` and `JobsSidebarMenu.Items` are `List<Func<RazorPage, MenuItem>>`. The function runs on every page render; return `null` to hide the item for that request.

| `MenuItem` member | Type | Meaning |
|---|---|---|
| `MenuItem(text, url)` | constructor | Label and link. |
| `Active` | `bool` | Highlights the item. Usually `page.RequestPath.StartsWith(...)`. |
| `Icon` | `string` | The name of an icon of the dashboard's sprite, such as `"clock"` or `"card-list"` (see [Icons](#icons)). A Glyphicon name of earlier versions, such as `"time"`, gives the icon that replaced it. Without an icon, or with a name the sprite doesn't hold, a chevron is shown. Only used in the main menu; the navigation rail (windows from 600 to 1239px wide) shows the icon above a short label. |
| `Group` | `string` | The group of the navigation drawer the item is listed in: `NavigationMenuGroup.Jobs`, `Workflows`, `Observe` or `System`, or any other text, which lists the item in a group of that title after the built-in ones. An item without a group is listed last, under "More". Only used in the main menu. |
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
| `JobHistoryRenderer.AddStateCssSuffix(string stateName, string cssSuffix)` | Gives the state's labels the CSS class `label-state-{cssSuffix}` and its steps in the job's history the class `state-card-state-{cssSuffix}`. The built-in suffixes are `active`, `success`, `danger`, `warning`, `info` and `inactive`; with one of them the state's labels and card get that suffix's colors, in light and dark, and so does the state in the job lineage diagram and the time-in-state bar. For a suffix of your own, style `.hf-app .label-state-{suffix}` and `.hf-app .state-card-state-{suffix} .ns-step-marker` in your stylesheet. Calling it again for the same state replaces the suffix. |
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

The button posts to the command defined in [A command route](#a-command-route). `data-confirm` asks first, in the dashboard's confirmation dialog (see [Confirmations](#confirmations)); leave it out to post straight away. While the request runs, the button is busy: it shows a progress indicator and its `data-loading-text`, when it has one, and keeps the focus. Afterwards the page reloads and a snackbar says the command is done, or, when the request failed, an error snackbar says why and offers Retry.

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

// List "Reports" in the Observe group of the drawer instead of under "More"
NavigationMenu.Items.Add(page => new MenuItem("Reports", page.Url.To("/reports")) { Icon = "card-list", Group = NavigationMenuGroup.Observe });
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
- Tooltips: every element with a `title` when the page loads gets a Bootstrap tooltip. For content you add later, call `NightShift.tooltips(container)`; to change the title of an element that has one, `NightShift.setTitle(element, text)`, since Bootstrap moves the `title` into `data-bs-original-title`. Before you replace or remove content that has tooltips (with `.html()`, `.empty()`, `.remove()` or `.replaceWith()`), call `NightShift.disposeTooltips(container)`: it disposes the tooltips (and popovers) of the container and of everything in it, and takes away a tooltip shown at that moment. Bootstrap keeps its tooltips in a map of its own that jQuery's cleanup doesn't reach, so without it the removed elements stay in memory, and a tooltip that was showing stays on the screen. To replace only what is inside an element, pass its children: `NightShift.disposeTooltips($(list).children())`.
- `NightShift.setLoading(button, true)` makes a button busy while your request runs: a circular progress indicator, its `data-loading-text` when it has one, `aria-busy` and `aria-disabled`. It isn't `disabled`, so it keeps the keyboard focus; the dashboard ignores clicks on any element with `aria-disabled="true"`. `NightShift.setLoading(button, false)` gives its content back.
- jQuery 4 removed `$.trim`, `$.isArray`, `$.isFunction`, `$.parseJSON`, `$.type`, `$.now` and other deprecated helpers: use the standard JavaScript ones (`String.prototype.trim`, `Array.isArray`, `JSON.parse`, `Date.now`). See the [jQuery 4 upgrade guide](https://jquery.com/upgrade-guide/4.0/).
- Graphs: the bundle has [Chart.js 4.6](https://www.chartjs.org/docs/4.6.1/) (its UMD build, the `Chart` global, with every chart type, scale and plugin registered) and the dashboard's date adapter, so a `time` scale works as it is. Write the chart in Chart.js 4's options: `scales: { x: {...}, y: {...} }` instead of `xAxes`/`yAxes`, `grid` instead of `gridLines`, `plugins.legend` and `plugins.tooltip` instead of `legend` and `tooltips`, `Chart.defaults.font` instead of `Chart.defaults.global` (see the [migration guide](https://www.chartjs.org/docs/4.6.1/migration/v3-migration.html)). `chartjs-plugin-streaming` is no longer in the bundle; for a graph that scrolls with the clock, set the time scale's `min` and `max` and call `chart.update('none')` from `requestAnimationFrame`, as the home page's realtime graph does. To follow the theme, listen to the `nightshift:theme` event on `document`, which the dashboard triggers when the user picks a theme.

Pages written for Bootstrap 3 need the usual changes ([Bootstrap's migration guide](https://getbootstrap.com/docs/5.3/migration/)): `label label-*` becomes `badge text-bg-*`, `panel` becomes `card`, `btn-default` becomes `btn-outline-secondary`, `pull-right` becomes `float-end`, `data-toggle` becomes `data-bs-toggle`, and so on. `page-header` and `dl-horizontal` still get their old look, so existing pages keep their titles and lists.

### Components

The dashboard follows [Material Design 3](https://m3.material.io/) on top of Bootstrap: the Bootstrap classes your pages use get the MD3 look, and a few classes of its own add what Bootstrap lacks. All of them read the design tokens (see [Theming and dark mode](#theming-and-dark-mode)), so they follow the theme.

| Component | Markup |
|---|---|
| Buttons | `btn btn-primary` is a filled button, `btn btn-outline-secondary` an outlined one, `btn btn-success` / `btn-info` tonal, `btn-warning` (or `ns-btn-tonal-tertiary`) tonal in the tertiary colour, `btn btn-danger` (or `btn-death`) a filled button in the error colour, `btn btn-link` a text button. The same with MD3 names: `ns-btn-filled`, `ns-btn-tonal`, `ns-btn-outlined`, `ns-btn-text`, `ns-btn-error`. Buttons are 36px high, `btn-sm` and `hf-btn-xs` 32px. A button that shows only an icon is `<button class="ns-icon-button" aria-label="...">` (40×40, `ns-icon-button-sm` 32×32). A group of `btn-outline-secondary` buttons in a `btn-group` is a segmented button; the one with `active` is selected. |
| Text fields | `form-control` and `form-select` are outlined fields (40px; `-sm` 32px), with the error look when `aria-invalid="true"` or `is-invalid`. Group a label, a control and its texts in `<div class="ns-field">` with `<div class="ns-field-supporting">` and `<div class="ns-field-error">`; `NightShift.fields(container)` links them (`for`, `aria-describedby`) and marks a `required` control's label, and `NightShift.fieldError(control, message)` shows or clears the error (`aria-invalid`). `hf-mono` (or `ns-field-mono` on the group) writes the value in JetBrains Mono. |
| Chips | `ns-chip` with `ns-chip-assist`, `ns-chip-filter` (selected with `aria-pressed="true"`, `selected`, or a checked input inside), `ns-chip-input` (with a `<button class="ns-chip-remove" aria-label="...">`) or `ns-chip-status`. |
| Segmented button | `<div class="ns-segmented" role="group">` of buttons (`aria-pressed`) or of labels holding radio buttons; the selected one is `active`, pressed or checked. The focus ring of a hidden radio button is drawn on its label. |
| Checkboxes and switches | Every checkbox and radio button is an 18px MD3 control with a 40px state layer. A switch is `<input type="checkbox" role="switch" class="ns-switch">` (or Bootstrap's `form-switch`), with its label in `<label class="ns-switch-label">`. In a job list, use `Html.JobCheckbox(id)` and `Html.SelectAllCheckbox()`: they write the classes the list's script needs and name the boxes for screen readers ("Select job #114"). |
| State chips | `Html.StateLabel(state)` writes `<span class="ns-state-chip ns-state-{group}" data-state="{state}">` with the state's icon and its name: a built-in state's in the reader's language, as the jobs menu names it, any other state's as it is. `data-state` keeps the state's own name for scripts and styles (the chain flowchart's `hf-flow-state`, the lineage's `hf-lineage-state`, the time-in-state bar's segments and the live activity's chips have it too). The groups are `enqueued`, `scheduled`, `processing`, `succeeded`, `failed`, `deleted` and `awaiting` (`JobHistoryRenderer.GetStateColorGroup(state)`); the class `ns-state-{group}` gives any element the variables `--ns-state-color`, `--ns-state-on-color`, `--ns-state-container` and `--ns-state-on-container`. |
| Page header | `<header class="ns-page-head">` with `<div class="ns-page-heading">` and `<div class="ns-page-actions">`. `Html.PageTitle(title, overline, count)` writes the `h1` (id `page-title`): the kind of item in an overline, the title, and the number of items in a count chip (`ns-count-chip js-list-count`, which a list's live refresh updates). Under the heading, `<div class="ns-page-facts">` holds status chips. Give the page one filled button; put a destructive one first, in `ns-btn-outlined-error`, and set it apart with `<span class="ns-actions-divider" aria-hidden="true"></span>`. Below 600px the actions take the whole width. |
| Summary tiles | `<div class="ns-summary">` of `<section class="ns-summary-tile">` with `<h2 class="ns-summary-label">` (an icon and a name), `<p class="ns-summary-value">` and `<p class="ns-summary-sub">`. A tile with `ns-state-{group}` has the state's colour on its left edge; an `a.ns-summary-tile` rises on hover; `<div class="ns-meter"><span style="width: 40%"></span></div>` is a thin bar in the brand gradient. |
| Job lists | One template: the page header, a toolbar (`btn-toolbar-top ns-list-toolbar`, with the list's `js-jobs-list-command` buttons in `ns-list-commands`; give the main one `data-list-primary="true"`, which the `r` shortcut runs), and the table in `<div class="hf-card ns-table-card">` with `table ns-job-table ns-table-reflow`. A row's job is two lines in `<td class="ns-job-cell">`: `<div class="ns-job-title">` (`Html.StateDot(state)`, the name) and `<div class="ns-job-meta">` (`<span class="ns-job-id">`, `Html.QueueChip(queue)`, the server); `<div class="ns-job-error">` adds an exception (`ns-exception-type`, `ns-job-error-message`); on the Failed list its **More details** is a `js-sheet-open` whose `data-sheet-url` loads `/jobs/failed/details/{id}` into the side sheet, where the stack trace follows `<div class="ns-sheet-trace-head">` (its `h3` and the copy button). The time goes in `td.ns-time-cell`; `data-label` on a cell names its value when the row is two lines below 600px. |
| Row actions | `Html.ActionsColumnHeader()` heads the column (its name is for screen readers only), and each row has `<td class="ns-actions-cell"><div class="ns-row-actions">` with `Html.JobCommandButton(url, jobId, icon, label)` (posts the job as `jobs[]` to a list command and reloads), `Html.JobDeleteButton(url, jobId)` (in the error colour, after a confirmation) and `Html.CopyJobIdButton(jobId)`. The buttons show while the row is hovered or holds the focus, so Tab reaches them, and always on touch screens and below 600px. `Html.CopyButton(text, label)` copies any text and says so in a snackbar. |
| Notes | `<div class="ns-note" role="note">` with an icon and `<div class="ns-note-text">`: a quiet note on a tonal surface. With `js-note` and `data-note="key"` and a `<button class="ns-icon-button js-note-dismiss" aria-label="...">`, the reader can dismiss it, and it stays away in that browser. |
| Danger zone | `<section class="ns-danger-zone">` with a `hf-card-header` (its `h2` in the error colour) and `<div class="ns-danger-row">` per operation: `<div class="ns-danger-text"><strong>Name</strong><p>What it does</p></div>` and an error button. The Servers page puts pausing processing, Clear all jobs and Reset storage there. |
| Home page | The header band `<header class="ns-page-head ns-home-head">` is the one place the brand gradient is a band (a faint tint, with the gradient as a rule at its foot). The state tiles are `<a class="ns-summary-tile ns-home-tile ns-state-{group}">` with a `<span class="ns-summary-label">`, a `<span class="ns-summary-value" data-metric="{metric}">` the statistics poller updates, and an optional `ns-summary-sub`. A graph is `<ul class="ns-graph-legend">` (items with `ns-state-{group}` and a `ns-graph-swatch`), the canvas in `<div class="ns-graph">` (which sets its height; the graphs don't keep an aspect ratio), and `<p class="ns-graph-summary">`, which the canvas names with `aria-describedby`. Activity rows are `li.hf-activity-row`: a state dot, `<div class="hf-activity-main">` with `ns-job-title` and `ns-job-meta`, the state chip and the time. |
| Policy cards | `<div class="ns-policy-grid">` of `<section class="hf-card ns-policy-card" id="policy-{name}">`: the name in `<h2 class="ns-policy-name">`, `ns-policy-description`, `ns-policy-facts` (chips), and a `dl.ns-kv.ns-policy-rules` whose values are `<ul class="ns-policy-chips">`: the days as `li.ns-chip.ns-policy-day` (`ns-policy-day-allowed` for an allowed one, with its state in words in a `visually-hidden` span), windows in `li.ns-policy-window` with `ns-policy-overnight`, and more dates in `<details class="ns-policy-more">`. `ns-policy-actions` holds Delete (`ns-btn-text ns-btn-text-error`) and, in `ns-policy-actions-end`, Preview and Edit, which open `<template id="tpl-policy-preview-{n}">` and `<template id="tpl-policy-edit-{n}">` (a `js-policy-form`; `js-policy-preview-form`) in the side sheet; New policy opens `<template id="tpl-policy-new">`. Only the cards' ids start with `policy-`. |
| Recurring jobs list | `<table class="table ns-job-table ns-table-reflow ns-recurring-table">` with two-line rows: `ns-job-title` holds the id as `a.job-method` (its page) and the method in `ns-recurring-method`; `ns-job-meta` holds `ns-recurring-schedule` (the description, then `code.cron-badge`), the time zone, `Html.QueueChip` and the chips `ns-recurring-declared`, `ns-recurring-paused` and `ns-recurring-retired` (a policy is an `ns-chip-assist` link); a scheduler error is an `ns-job-error` line with **More details**. The next and last executions are `td.ns-time-cell.ns-recurring-when`: a chip (`ns-recurring-late`, `ns-recurring-error`, `ns-recurring-retrying`, `ns-recurring-ended`, `ns-recurring-disabled`, or the last job's state chip in `a.ns-recurring-last`) with the time in `ns-time-exact`. The row actions post `data-ajax` to `/recurring/trigger-one/{id}`, `pause-one` or `resume-one`, `js-recurring-edit` opens the editor, and Delete (`ns-row-danger js-job-command` posting to `/recurring/remove`) comes after an `ns-actions-divider`. The editor is `<template class="js-recurring-edit-template">` in `<div id="recurringJobEditor" hidden>` (which keeps `data-preview-url` and `data-update-url`): a `form.ns-sheet-form` of `<fieldset class="ns-fieldset">`s. |
| Catalog and runner | A `<section class="hf-card ns-table-card hf-catalog-group js-catalog-group">` per type with its `h2` (the namespace in `ns-catalog-namespace`) and `<ul class="ns-list ns-catalog-list">` of `li.ns-list-item.js-catalog-entry`: the method (`hf-catalog-method`), its parameters in `ns-catalog-params` (`hf-param`, `hf-param-injected`, `hf-param-type`), then in `ns-job-meta` the facts read from its filters as `ns-chip-status` chips (`ns-catalog-filter` for a filter shown by name) and `hf-catalog-stats`; Run is `button.ns-icon-button.js-catalog-run` in `ns-row-actions`. The filter is `#catalogFilter.js-catalog-filter` with a `visually-hidden` label; `js-catalog-nomatch` shows when nothing matches. The runner is `<template class="js-catalog-template">` in `<div id="catalogRunner" hidden>` (with its `data-*` addresses and texts): `form.js-catalog-form` with the parameters and the modes in `ns-fieldset`s; a field with `data-required="true"` or `data-json="true"` is checked before the form is sent. |
| Forecast, About and Audit | The forecast's lanes are in `<section class="hf-card ns-forecast-card">` (`hf-forecast-row`; `hf-forecast-row-paused` no longer fades the row, its label has an `ns-recurring-paused` chip in `hf-forecast-facts`), its legend is an `ns-graph-legend`, and its hot spots are `ns-run` rows with an `ns-forecast-count` chip and job chips in `ns-forecast-jobs`. About is a jump nav and cards: `ns-about-grid` (the settings as `dl.ns-kv.ns-about-kv`, the storage with `ns-about-features` items, `ns-about-supported` or `ns-about-unsupported`), then `ns-about-list` lists. The audit log has `ns-audit-toolbar` (`#auditFilter.js-audit-filter`, the `js-audit-failed` filter chip, the `js-audit-count` size chips) and `ol.ns-run-list.ns-audit-list` of `li.ns-run.js-audit-entry` (`ns-audit-failed`, `data-search`, `data-failed`) with `ns-audit-action`, `ns-audit-user`, `ns-audit-targets` and `ns-audit-detail`. |
| Server cards | `<div class="ns-server-grid">` of `<section class="hf-card ns-server-card">` (`ns-server-stale` when its heartbeat is late, `ns-server-paused`): `ns-server-head` with the name in `<h2 class="ns-server-name">`, `<code class="ns-server-id">` and `ns-server-statuses` with the `ns-server-status` chips (Possibly aborted, Paused, both, or Active); `ns-server-workers` with the busy workers on a `<div class="ns-meter" role="meter">`; a `dl.ns-kv.ns-server-facts` (queues as `Html.QueueChip`, relative times with the exact one in `ns-time-exact`); and `ns-server-actions`. |
| Job page | The header (`ns-page-head ns-job-head`) holds the state in `<span class="ns-job-state">` (a large state chip), the id with `Html.CopyJobIdButton` in `ns-job-id-fact`, the queue and the recurring job, chain, batch and tags as chips; Delete (`ns-btn-outlined-error`) is set apart from Requeue, the primary action of a failed, deleted or succeeded job. A failed job's exception follows in `<section class="hf-card ns-exception-card">` (`ns-exception-type`, `ns-exception-message`, the stack trace in `<details class="ns-exception-trace">`), then summary tiles and `<nav class="ns-jump-nav">`, links to the sections, each an `h2` with an id (`job-details-code`, `-live`, `-history`, `-parameters`, `-lineage`, `-notes`). |
| Time in each state | `<div class="hf-state-bar js-state-bar" role="list">` of `<span role="listitem" class="hf-state-seg ns-state-{group}" style="flex-grow: {share}">`: each segment grows with its share of the time from a minimum width; its label is `<em aria-hidden="true">` and its state and time are in a `visually-hidden` span and the tooltip. A segment too narrow for its label gets `hf-state-seg-tight` (from the page, then `NightShift.fitStateBars()` on each resize), which hides the label. |
| History stepper | `<ol class="ns-stepper">` of `<li class="ns-step ns-state-{group}">` (`ns-step-current` for the newest): `<span class="ns-step-marker">` with the state's icon, the state chip in `<h3 class="ns-step-title">`, `ns-step-time`, `ns-step-duration` (the time since the state before), `ns-step-reason`, and what `JobHistoryRenderer` renders in `<div class="ns-step-data">`, where a `dl.hf-dl-horizontal` shows names beside values. |
| Console | `<div class="job-console">` is a grid of two columns: each `<div class="job-console-line">` holds `<span class="job-console-time">` and `<span class="job-console-text">`, so times line up and long lines wrap under their text. Give the time `data-moment-local` (seconds) and `data-moment-local-format="timeWithSeconds"` to show it in the viewer's time zone. `<div class="ns-meter ns-meter-lg">` (with `ns-meter-active` or `ns-meter-failed`) is the larger progress bar. |
| Cards | `hf-card` (or `ns-card`) is an outlined card, with `hf-card-header` and `hf-card-body` (or `ns-card-header` and `ns-card-body`); add `ns-card-elevated` or `ns-card-filled` for the other two MD3 cards. Bootstrap's `card` gets the outlined look too. |
| Banners | `<div class="ns-banner ns-banner-info">` (or `-warning`, `-error`) with an icon, `<span class="ns-banner-text">` and `<span class="ns-banner-actions">`. |
| Empty and error states | `Html.EmptyState(icon, title, text, actionUrl, actionLabel)` writes `<div class="ns-empty-state">` with the icon, the title in `<h2 class="ns-empty-state-title">`, a sentence and a link to the next step ("No failed jobs", then "Succeeded jobs"). `ns-error-state` is the same for an error. |
| Progress | `<div class="ns-progress-linear"><span style="--ns-progress: 40%"></span></div>` (add `ns-progress-indeterminate` without a value), and `<span class="ns-progress-circular" aria-hidden="true"></span>` (`ns-progress-circular-lg` for 40px). |
| Key-value list | `<dl class="ns-kv">` with `dt` and `dd` pairs: two columns, one below 600px. |
| Code | `<div class="ns-code"><pre><code>...</code></pre><button class="ns-icon-button ns-code-copy js-copy-code" data-code="..." data-copied="Copied" aria-label="Copy">...</button></div>`: JetBrains Mono on the inset surface, and the classes `keyword`, `string`, `type`, `number` and `comment` in the theme's syntax colours. |
| Tables | Bootstrap's `table` has dense 40px rows (`ns-table-comfortable` for 48px), a header that stays under the app bar on large windows, a hover state layer, and selected rows (`highlight` or `aria-selected="true"`) in the secondary container. Give header cells `scope="col"`. Add `ns-table-reflow` to make a table a list of two-line items below 600px (`ns-reflow-primary` marks the cell that goes first). A list in `<div class="js-jobs-list">` with `.js-jobs-list-command` buttons gets the selection bar ("N selected", the commands and Clear) when rows are selected. |
| Dialogs | Bootstrap's `modal` is an MD3 dialog (headline `modal-title`, actions in `modal-footer`) that fills the window below 600px. The dashboard gives the focus back to the control that opened a dialog, keeps Tab inside it, and asks "Discard changes?" before a dialog with a form closes over unsaved input. |

The text of the dashboard is .875rem, 14px at the browser's default size: the root font size is the browser's own (16px by default), so a user who sets a larger font gets a larger dashboard. A `rem` in your stylesheet is therefore the browser's font size, 16px by default; dashboards before this one set the root to 14px, and Bootstrap 3's set it to 10px. Size text with the type scale (`font: var(--md-sys-typescale-body-medium)`, `--md-sys-typescale-label-medium` and so on) or in `rem`, and nothing below .6875rem (11px).

### Markup hooks (no styles of their own)

The classes below have no rule in the dashboard's stylesheets: their look comes from the classes beside them, and what they mark is said in words on the same row or card, by a chip or a label. They exist only so that your stylesheets and scripts can target that markup, and they are kept stable on purpose: later versions go on writing them where they are now, even when the dashboard's own styles change.

| Class | Marks | Written by |
|---|---|---|
| `ns-about-list` | The lists after the cards of the About page, each also an `ns-list`. | [AboutPage.cshtml](../../src/Masterly.NightShift.Core/Dashboard/Pages/AboutPage.cshtml) |
| `ns-audit-failed` | An audit entry that was answered with 400 or more, an `li.ns-run`: beside its action, a Failed state chip shows the status code with the error icon, after a Failed state dot. | [AuditPage.cshtml](../../src/Masterly.NightShift.Core/Dashboard/Pages/AuditPage.cshtml) |
| `ns-audit-user` | The chip that names the user of an audit entry, an `ns-chip-status`. | [AuditPage.cshtml](../../src/Masterly.NightShift.Core/Dashboard/Pages/AuditPage.cshtml) |
| `ns-catalog-filter` | A filter of a catalog entry shown by its name, an `ns-chip-status` chip. | [JobCatalogPage.cs](../../src/Masterly.NightShift.Core/Dashboard/Pages/JobCatalogPage.cs) |
| `ns-forecast-card` | The card that holds the forecast's lanes, an `hf-card`. | [ForecastPage.cshtml](../../src/Masterly.NightShift.Core/Dashboard/Pages/ForecastPage.cshtml) |
| `ns-job-head` | The job page's header, an `ns-page-head`. | [JobDetailsPage.cshtml](../../src/Masterly.NightShift.Core/Dashboard/Pages/JobDetailsPage.cshtml) |
| `ns-recurring-disabled` | The Disabled chip in the next execution of a recurring job whose schedule never fires. | [RecurringJobsPage.cshtml](../../src/Masterly.NightShift.Core/Dashboard/Pages/RecurringJobsPage.cshtml) |
| `ns-recurring-ended` | The Ended chip in the next execution of a recurring job past its end date, with that date under it. | [RecurringJobsPage.cshtml](../../src/Masterly.NightShift.Core/Dashboard/Pages/RecurringJobsPage.cshtml) |
| `ns-server-paused` | The card of a paused server, an `ns-server-card`: its Paused chip says so, also beside Possibly aborted when its heartbeat is late. | [ServersPage.cshtml](../../src/Masterly.NightShift.Core/Dashboard/Pages/ServersPage.cshtml) |

### Confirmations

A command button with `data-confirm` asks in the dashboard's confirmation dialog before it posts. Write the attributes with `Html.ConfirmAttributes(title, body, confirmLabel, destructive, typeToConfirm)`, or by hand:

| Attribute | Meaning |
|---|---|
| `data-confirm` | The headline, a question that names the action ("Delete 3 jobs?"). In a list command, `{0}` is replaced by the number of selected rows. |
| `data-confirm-one` | The headline when one row is selected ("Delete 1 job?"). |
| `data-confirm-body` | What happens. |
| `data-confirm-label` | The confirming button's label; without it, the command button's own text. |
| `data-confirm-destructive="true"` | The confirming button is in the error colour, and the focus starts on Cancel. A `btn-death` or `btn-danger` button is destructive without it. |
| `data-confirm-type` | Text the user must type before the confirming button works, for what can't be undone. |

From a script, `NightShift.confirm({ title, body, confirmLabel, cancelLabel, destructive, typeToConfirm })` shows the same dialog and returns a `Promise` that resolves to `true` or `false`:

```js
NightShift.confirm({ title: 'Rebuild the report?', body: 'It takes a few minutes.', confirmLabel: 'Rebuild' })
    .then(function (confirmed) { if (confirmed) rebuild(); });
```

### Snackbars and announcements

`NightShift.snackbar.show({ text, kind, action, duration })` shows a snackbar at the bottom of the window: one at a time, in a polite status region, for 4 to 10 seconds (longer with an action, and while the pointer or the focus is on it). `kind` is `'error'`, `'warning'` or nothing; `action` is `{ label, href }` or `{ label, run }`. `NightShift.snackbar.showAfterReload(options)` shows it on the next page the tab loads, for a command that reloads the page. `NightShift.showRequestError(xhr, actionName, retry)` shows the error snackbar of a failed request, with a Retry button when `retry` is a function, and `NightShift.showToast(text, kind)` remains for scripts written for it. `NightShift.announce(text)` reads a short text to screen readers without showing anything.
### Side sheet

`NightShift.sheet.open({ title, overline, content, url, wide, opener, onClose, guard })` opens an MD3 modal side sheet at the end of the window (full screen below 600px), for the details of an item without leaving the list: the title in its `h2`, then `content` (HTML, a node or a jQuery object) or the HTML that `url` answers with, shown with a skeleton while it loads. The children of an element of the content with the class `js-sheet-actions` go to the sheet's footer. While it is open the rest of the page is inert and Tab stays inside; Esc, the close button and the scrim close it (`NightShift.sheet.close()`), and the focus goes back to `opener` (the element that had it). With `guard: true`, input in a form of the sheet makes Esc, the close button and a button with `js-sheet-close` ask "Discard changes?" first, and a click on the scrim does nothing meanwhile (`NightShift.sheet.requestClose()` closes it the same way). The input stays unsaved until `NightShift.sheet.markClean()` says it was saved, so a submit that the page's checks or the server refuse still asks: a save that reloads the page needs nothing, one that keeps the sheet open calls it. The schedule editor of the recurring jobs, the catalog's runner and the schedule policy editor open this way. `NightShift.sheet.isOpen()` says whether it is open; a list's live refresh and the single-key shortcuts wait while it is.

Without a script, a button with the class `js-sheet-open` opens it with its `data-sheet-title` and `data-sheet-overline`, and with the content of `data-sheet-url`, of the `<template>` its `data-sheet-content` selector names, or else of the `<template class="js-sheet-template">` in its row; `data-sheet-wide="true"` makes it wide and `data-sheet-guard="true"` opens it with `guard: true`:

```html
<button type="button" class="btn btn-sm ns-btn-text js-sheet-open" aria-haspopup="dialog" data-sheet-title="Report #42">Details</button>
<template class="js-sheet-template">
    <p class="ns-sheet-lead">What happened.</p>
    <div class="js-sheet-actions"><a class="btn btn-sm btn-primary" href="...">Open</a></div>
</template>
```

### Icons

The dashboard's icons are [Bootstrap Icons](https://icons.getbootstrap.com/) in one SVG sprite (the route `/icons...`), which holds the icons the dashboard uses. Draw one with `Html.Icon(name)` in a page, or `NightShift.icon(name)` in a script, which resolves the name the same way and returns the same HTML:

```csharp
WriteLiteral("<button class=\"btn btn-sm btn-outline-secondary\" data-ajax=\"...\">");
Write(Html.Icon("arrow-repeat"));
WriteLiteral(" Rebuild</button>");
```

```js
$('<span>').html(NightShift.icon('check-lg', 'text-success'));
```

The icon is an inline `<svg class="bi">` that takes the size and color of the text around it, and is hidden from screen readers (`aria-hidden="true"`): give a button that shows only an icon an `aria-label` or a `title`. `Html.Icon(name, cssClass)` adds classes to the `svg`.

The sprite holds `arrow-clockwise`, `arrow-repeat`, `arrows`, `arrows-fullscreen`, `bar-chart-fill`, `book-fill`, `box-arrow-down`, `box-arrow-left`, `calendar3`, `card-list`, `chat-left-text-fill`, `check-circle`, `check-circle-fill`, `check-lg`, `chevron-left`, `chevron-right`, `circle-fill`, `circle-half`, `clock`, `copy`, `download`, `easel-fill`, `exclamation-circle-fill`, `exclamation-triangle-fill`, `eye-fill`, `fire`, `flag-fill`, `folder2-open`, `grid-fill`, `hdd-fill`, `hourglass-split`, `house-door-fill`, `inbox-fill`, `info-circle-fill`, `lightning-fill`, `link-45deg`, `list`, `list-task`, `lock-fill`, `moon-stars`, `pause-fill`, `pencil-fill`, `play-circle`, `play-circle-fill`, `play-fill`, `plus-lg`, `question-circle-fill`, `search`, `shuffle`, `sun-fill`, `tag-fill`, `terminal-fill`, `text-left`, `trash-fill`, `upload`, `wifi-off`, `x-circle-fill`, `x-lg`, `zoom-in` and `zoom-out`. Both helpers ignore white space around the name and a `bi-` prefix (`bi-trash-fill` is `trash-fill`), and the Glyphicon names the dashboard used before, with or without `glyphicon-` (`trash`, `glyphicon-time`), give the icon that replaced them. Any other name is written as it is, and draws nothing: an empty 1em box. (A menu item's `Icon` is different: a name the sprite doesn't hold gives the chevron.) For another icon, put the Bootstrap Icons SVG in your page.

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
// Reuse the colours and icon of a built-in state: active (teal, like Enqueued), info (blue, like Scheduled),
// warning (ember, like Processing), success (green), danger (rose) or inactive (gray, like Deleted)
JobHistoryRenderer.AddStateCssSuffix("Quarantined", cssSuffix: "warning");
JobHistoryRenderer.Register("Quarantined", (html, data) =>
    new NonEscapedString(
        $"<dl class=\"hf-dl-horizontal\"><dt>Reason</dt><dd>{html.HtmlEncode(data.TryGetValue("Reason", out var reason) ? reason : "-")}</dd></dl>"));
```

With a built-in suffix the state's chips (`Html.StateLabel`) and its card on the job page take the colour group of that state (`JobHistoryRenderer.GetStateColorGroup` returns it). With a suffix of your own, the chip is neutral and has the class `label-state-{suffix}`, for your stylesheet to colour.

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

The stylesheets are built on Material Design 3 tokens (`nightshift-tokens.css`): the colour roles (`--md-sys-color-primary`, `--md-sys-color-surface`, `--md-sys-color-on-surface`, `--md-sys-color-outline` and the rest, each also as `-rgb` where a translucent colour needs it), the job states' colour groups (`--ns-state-succeeded`, `--ns-state-succeeded-container` and so on), the navigation drawer's colours (`--ns-nav-*`), the type scale (`--md-sys-typescale-*`), shape (`--md-sys-shape-corner-*`), elevation (`--md-sys-elevation-level1` to `5`), state layers (`--md-sys-state-*-state-layer-opacity`), motion (`--md-sys-motion-duration-*`, `--md-sys-motion-easing-*`) and the 4px spacing grid (`--md-sys-spacing-*`). The colour roles are set on `:root`, again on `:root[data-theme="dark"]`, and for the system's dark preference when no theme is chosen; with `DarkModeEnabled = false` the page has `data-theme="light"`, so it stays light. The earlier `--hf-*` variables (`--hf-primary`, `--hf-bg`, `--hf-surface`, `--hf-sidebar-bg`...) are still set, read from the roles, so stylesheets written for them keep working. To change colours, override the roles in a stylesheet you add with `AddStylesheet`:

```css
:root {
    --md-sys-color-primary: #6d28d9;
    --md-sys-color-primary-rgb: 109, 40, 217;
}

:root[data-theme="dark"] {
    --md-sys-color-primary: #c4b5fd;
    --md-sys-color-primary-rgb: 196, 181, 253;
}
```

Bootstrap's own variables (`--bs-body-bg`, `--bs-body-color`, `--bs-border-color` and the fonts) are set from these, so Bootstrap components in your pages follow the theme too; the dashboard doesn't use Bootstrap's `data-bs-theme`. These names are taken from the current stylesheet and may change between versions.

### Dates and times

The server writes dates into the page as text with a machine-readable timestamp, and the dashboard's script (`nightshift-intl.js`) rewrites them in the browser with the `Intl` APIs:

- in the viewer's time zone;
- in the language of the page (the two-letter code of `CurrentUICulture`); for a code the browser doesn't know, such as the invariant culture, the browser's own language;
- relative times ("3 minutes ago") use the same thresholds moment.js used, and date arithmetic, including across daylight saving changes, matches moment's.

The same code is the date adapter of Chart.js 4, so the graphs need neither moment.js nor `chartjs-adapter-moment`: its `add`, `diff`, `startOf` and `endOf` give what that adapter gives on moment, and its formats are the named formats below. Hourly statistics, such as the Insights heatmap, are in UTC.

In your pages, use `Html.RelativeTime(dateTime)` ("3 minutes ago", full date on hover) and `Html.LocalTime(dateTime)` (a full local date and time) to get the same behavior. In your scripts, `NightShiftIntl.format(time, name)` formats a Unix time in milliseconds with one of the named formats `time`, `timeWithSeconds`, `shortDate`, `longDate`, `dateTime`, `dateTimeWithSeconds`, `fullDateTime`, `numericDateTime`, `month` or `year`, and `NightShiftIntl.fromNow(time)` gives the relative form.

## Things to know

- Registries are global and static. Two dashboards in one process show the same custom pages and menu items, and tests that register routes affect each other.
- A route added after a built-in route with an overlapping pattern is never reached. Check with `DashboardRoutes.Routes.FindDispatcher("/your/path")`.
- Everything you write with `WriteLiteral`, `Html.Raw` or `NonEscapedString` goes to the browser as HTML. Encode anything that comes from job data or users with `Write(...)` or `Html.HtmlEncode(...)`.
- Custom routes are protected by the dashboard's authorization filters like built-in ones, but a plain `IDashboardDispatcher` must check `IsReadOnly` itself.
- The built-in CSS classes and the `--hf-*` and `--ns-*` variables are not a stable API, except the [markup hooks](#markup-hooks-no-styles-of-their-own); the `--md-sys-*` tokens follow Material Design 3's names.
- Every infinite animation stops, and transitions end at once, when the user asks for reduced motion (`prefers-reduced-motion`); give your own animations the same treatment, or use the `--md-sys-motion-duration-*` tokens, which collapse to 1ms then.

## See also

- [Setting up the dashboard](setup.md)
- [Authorization](authorization.md)
- [Pages](pages.md)
- [Global configuration](../configuration/global-configuration.md)
- [Custom filters](../filters/custom-filters.md)
- [Insights](../monitoring/insights.md)
