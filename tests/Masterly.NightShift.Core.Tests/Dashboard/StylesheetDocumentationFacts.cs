using System.Text.RegularExpressions;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

/// <summary>
/// Keeps the dashboard's stylesheets and what <c>docs/dashboard</c> tells extension authors in step, from the source
/// files: every class the customization guide documents has a rule, and every rule of the dashboard's own
/// stylesheets styles something a page, a script or the guide uses.
/// </summary>
public class StylesheetDocumentationFacts
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string Dashboard = Path.Combine(Root, "src", "Masterly.NightShift.Core", "Dashboard");
    private static readonly string Stylesheets = Path.Combine(Dashboard, "Content", "css");

    // Vendored unmodified: their selectors count for the documented Bootstrap classes, but nobody keeps them in use
    private static readonly string[] VendoredStylesheets = ["bootstrap.min.css"];

    /// <summary>
    /// Documented as the markup of a page, for extensions to target, without a rule of their own (the look comes
    /// from the classes beside them). Each is still written where the comment says.
    /// </summary>
    private static readonly Dictionary<string, string> MarkupHooks = new(StringComparer.Ordinal)
    {
        ["ns-about-list"] = "Pages/AboutPage.cshtml",           // the lists after the cards, beside ns-list
        ["ns-audit-failed"] = "Pages/AuditPage.cshtml",         // an entry that answered 400 or more (ns-run)
        ["ns-audit-user"] = "Pages/AuditPage.cshtml",           // the user of an entry
        ["ns-catalog-filter"] = "Pages/JobCatalogPage.cs",      // a filter shown by name, an ns-chip-status chip
        ["ns-forecast-card"] = "Pages/ForecastPage.cshtml",     // the lanes' hf-card
        ["ns-job-head"] = "Pages/JobDetailsPage.cshtml",        // the job page's ns-page-head
        ["ns-recurring-disabled"] = "Pages/RecurringJobsPage.cshtml", // a schedule that never fires, an ns-chip
        ["ns-recurring-ended"] = "Pages/RecurringJobsPage.cshtml",    // past its end date, an ns-chip
        ["ns-server-paused"] = "Pages/ServersPage.cshtml",      // a paused server's card; its chip says so
    };

    /// <summary>Classes a page builds by concatenation, so no source holds them whole.</summary>
    private static readonly Regex[] BuiltClasses =
    [
        // "ns-state-" + the state's colour group (JobHistoryRenderer.GetStateColorGroup): HtmlHelper.StateLabel and
        // StateDot, HomePage.cs's graph legend, JobDetailsPage.cshtml's time-in-state bar and history steps, and the
        // live activity in nightshift.js
        new("^ns-state-[a-z]+$"),
        // "hf-diagnosis-" + the finding's JobDiagnosticSeverity in lower case, in JobDetailsPage.cshtml
        new("^hf-diagnosis-(info|warning|problem)$"),
        // "hf-flow-box-" + "group" or "outcomes" by the level's kind, in ChainFlowchart.cs
        new("^hf-flow-box-(group|outcomes)$"),
    ];

    [Fact]
    public void EveryClassTheCustomizationGuideDocuments_IsASelectorOfTheStylesheets()
    {
        HashSet<string> selectors = [.. Directory.GetFiles(Stylesheets, "*.css").SelectMany(static file => SelectorClasses(File.ReadAllText(file)))];
        string guide = Path.Combine("docs", "dashboard", "customization.md");

        string[] documented = [.. DocumentedClasses(File.ReadAllText(Path.Combine(Root, guide)))];
        // The guide names well over a hundred: far fewer means its format changed and this test reads it wrong
        Assert.True(documented.Length > 100, $"{guide} seems to document only {documented.Length} classes");

        List<string> missing = [];
        foreach (string name in documented)
        {
            bool found = name.Contains('{', StringComparison.Ordinal) || name.Contains('*', StringComparison.Ordinal)
                ? selectors.Any(new Regex("^" + Regex.Replace(Regex.Escape(name), @"\\\{[^}]*}|\\\*", "[-a-z0-9]+") + "$").IsMatch)
                : selectors.Contains(name) || MarkupHooks.ContainsKey(name);
            if (!found)
            {
                missing.Add($"{guide} documents the class '{name}', which no stylesheet in Content/css has as a selector");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EveryMarkupHook_IsStillDocumented_AndWrittenByItsPage_WithoutARule()
    {
        HashSet<string> documented = [.. DocumentedClasses(File.ReadAllText(Path.Combine(Root, "docs", "dashboard", "customization.md")))];
        HashSet<string> selectors = [.. Directory.GetFiles(Stylesheets, "*.css").SelectMany(static file => SelectorClasses(File.ReadAllText(file)))];

        Assert.All(MarkupHooks, hook =>
        {
            Assert.True(documented.Contains(hook.Key), $"'{hook.Key}' is no longer documented: remove it from MarkupHooks");
            Assert.False(selectors.Contains(hook.Key), $"'{hook.Key}' has a rule now: remove it from MarkupHooks");
            Assert.True(Mentions(File.ReadAllText(Path.Combine(Dashboard, hook.Value)), hook.Key), $"{hook.Value} no longer writes '{hook.Key}'");
        });
    }

    [Fact]
    public void EverySelectorOfTheDashboardsOwnStylesheets_IsUsedByAPageAScriptOrTheDocs()
    {
        string used = string.Join('\n', UsingFiles().Select(File.ReadAllText));

        List<string> unused = [];
        foreach (string file in Directory.GetFiles(Stylesheets, "*.css").Where(static x => !VendoredStylesheets.Contains(Path.GetFileName(x))).Order(StringComparer.Ordinal))
        {
            foreach (string name in SelectorClasses(File.ReadAllText(file)).Where(static x => x.StartsWith("ns-", StringComparison.Ordinal) || x.StartsWith("hf-", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                if (!Mentions(used, name) && !BuiltClasses.Any(x => x.IsMatch(name)))
                {
                    unused.Add($"{Path.GetFileName(file)} has a rule for '.{name}', which no page, first-party script or page of docs/dashboard uses");
                }
            }
        }

        Assert.True(unused.Count == 0, string.Join(Environment.NewLine, unused));
    }

    [Fact]
    public void EveryClassBuiltByConcatenation_IsOneTheBuildersWrite()
    {
        // The allow-list stays narrow: the colour groups and severities it lets through are the ones that exist
        string[] states = [EnqueuedState.StateName, ScheduledState.StateName, ProcessingState.StateName, SucceededState.StateName, FailedState.StateName, DeletedState.StateName, AwaitingState.StateName];
        string[] groups = [.. states.Select(static state => "ns-state-" + JobHistoryRenderer.GetStateColorGroup(state))];
        string[] severities = [.. Enum.GetNames<JobDiagnosticSeverity>().Select(static x => "hf-diagnosis-" + x.ToLowerInvariant())];

        Assert.All(groups, x => Assert.Contains(BuiltClasses, regex => regex.IsMatch(x)));
        Assert.All(severities, x => Assert.Contains(BuiltClasses, regex => regex.IsMatch(x)));
        Assert.Equal(3, severities.Length);
    }

    // The classes of the selectors: what comes before each "{", without comments
    private static IEnumerable<string> SelectorClasses(string css)
    {
        string stripped = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Matches(stripped, "([^{}]*){")
            .SelectMany(static selector => Regex.Matches(selector.Groups[1].Value, @"\.(-?[_a-zA-Z][-_a-zA-Z0-9]*)").Select(static x => x.Groups[1].Value))
            .Distinct(StringComparer.Ordinal);
    }

    // The ns-/hf- classes of the guide's code spans and code blocks: in class="..." attributes, selectors such as
    // td.ns-time-cell and bare names; ns-state-{group} stands for every group. Script hooks (js-*) have no rules.
    private static IEnumerable<string> DocumentedClasses(string markdown)
    {
        return Regex.Matches(markdown, "```.*?```|`[^`\n]+`", RegexOptions.Singleline)
            .SelectMany(static code => Regex.Matches(code.Value, @"(?<![-\w$.{])((?:ns|hf)-[-a-z0-9{}*]*[a-z0-9}*])").Select(static x => x.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
    }

    private static IEnumerable<string> UsingFiles()
    {
        return Directory.GetFiles(Dashboard, "*.cshtml", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Dashboard, "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(Path.Combine(Dashboard, "Content", "js"), "*.js").Where(static x => !x.EndsWith(".min.js", StringComparison.Ordinal)))
            .Concat(Directory.GetFiles(Path.Combine(Root, "docs", "dashboard"), "*.md"));
    }

    private static bool Mentions(string text, string name)
    {
        return Regex.IsMatch(text, "(?<![-_a-zA-Z0-9])" + Regex.Escape(name) + "(?![-_a-zA-Z0-9])");
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Masterly.NightShift.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No Masterly.NightShift.slnx above {AppContext.BaseDirectory}");
    }
}
