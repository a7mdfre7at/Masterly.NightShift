using System.Text.RegularExpressions;
using Masterly.NightShift.Dashboard;
using Match = System.Text.RegularExpressions.Match;

namespace Masterly.NightShift.Core.Tests.Dashboard;

/// <summary>
/// Keeps the dashboard's stylesheets and what <c>docs/dashboard</c> tells extension authors in step, from the source
/// files: every class the customization guide documents has a rule, except the markup hooks it lists under one heading,
/// which have none and are written by their pages, and every rule of the dashboard's own stylesheets styles something
/// a page, a script or the guide uses.
/// </summary>
public class StylesheetDocumentationFacts
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string Dashboard = Path.Combine(Root, "src", "Masterly.NightShift.Core", "Dashboard");
    private static readonly string Stylesheets = Path.Combine(Dashboard, "Content", "css");

    // Vendored unmodified: their selectors count for the documented Bootstrap classes, but nobody keeps them in use
    private static readonly string[] VendoredStylesheets = ["bootstrap.min.css"];

    // The guide's heading of the classes documented without a rule of their own, for extensions to target
    private const string MarkupHooksHeading = "### Markup hooks (no styles of their own)";

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
        string markdown = File.ReadAllText(Path.Combine(Root, guide));
        Dictionary<string, string> hooks = MarkupHooks(markdown);

        // Outside the hooks' heading, a class without a rule is either a mistake or a hook not listed there
        string[] documented = [.. DocumentedClasses(markdown.Replace(MarkupHooksSection(markdown), string.Empty, StringComparison.Ordinal))];
        // The guide names well over a hundred: far fewer means its format changed and this test reads it wrong
        Assert.True(documented.Length > 100, $"{guide} seems to document only {documented.Length} classes");

        List<string> missing = [];
        foreach (string name in documented)
        {
            bool found = name.Contains('{', StringComparison.Ordinal) || name.Contains('*', StringComparison.Ordinal)
                ? selectors.Any(new Regex("^" + Regex.Replace(Regex.Escape(name), @"\\\{[^}]*}|\\\*", "[-a-z0-9]+") + "$").IsMatch)
                : selectors.Contains(name) || hooks.ContainsKey(name);
            if (!found)
            {
                missing.Add($"{guide} documents the class '{name}', which no stylesheet in Content/css has as a selector (a class without a rule of its own belongs under '{MarkupHooksHeading}')");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EveryMarkupHookTheGuideLists_HasNoRule_AndIsWrittenByItsPage()
    {
        string markdown = File.ReadAllText(Path.Combine(Root, "docs", "dashboard", "customization.md"));
        HashSet<string> selectors = [.. Directory.GetFiles(Stylesheets, "*.css").SelectMany(static file => SelectorClasses(File.ReadAllText(file)))];

        Dictionary<string, string> hooks = MarkupHooks(markdown);
        // Fewer means the heading's table changed and this test reads it wrong
        Assert.True(hooks.Count >= 9, $"'{MarkupHooksHeading}' seems to list only {hooks.Count} classes");

        Assert.All(hooks, hook =>
        {
            Assert.False(selectors.Contains(hook.Key), $"'{hook.Key}' has a rule now: move it out of '{MarkupHooksHeading}'");
            Assert.True(File.Exists(hook.Value), $"'{hook.Key}' is written by {hook.Value}, which doesn't exist");
            Assert.True(Mentions(File.ReadAllText(hook.Value), hook.Key), $"{Path.GetFileName(hook.Value)} no longer writes '{hook.Key}'");
        });
    }

    [Fact]
    public void MarkupHooks_ReadsTheClassAndThePageOfEachRow_OfTheHooksHeadingOnly()
    {
        string markdown = string.Join(
            "\r\n",
            "## Before",
            "`ns-before` is documented elsewhere",
            MarkupHooksHeading,
            "",
            "| Class | Marks | Written by |",
            "|---|---|---|",
            "| `ns-one` | A card, an `hf-card`. | [AboutPage.cshtml](../../src/Masterly.NightShift.Core/Dashboard/Pages/AboutPage.cshtml) |",
            "",
            "### After",
            "| `ns-after` | Not a hook. | [AuditPage.cshtml](../../src/Masterly.NightShift.Core/Dashboard/Pages/AuditPage.cshtml) |");

        Dictionary<string, string> hooks = MarkupHooks(markdown);

        Assert.Equal(["ns-one"], hooks.Keys);
        Assert.Equal(Path.Combine(Dashboard, "Pages", "AboutPage.cshtml"), hooks["ns-one"]);
        Assert.DoesNotContain("ns-before", MarkupHooksSection(markdown), StringComparison.Ordinal);
        Assert.DoesNotContain("ns-after", MarkupHooksSection(markdown), StringComparison.Ordinal);
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

    // The text under the hooks' heading, up to the next heading
    private static string MarkupHooksSection(string markdown)
    {
        Match section = Regex.Match(markdown, "^" + Regex.Escape(MarkupHooksHeading) + @"\r?\n.*?(?=^#{1,6} |\z)", RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(section.Success, $"The guide has no heading '{MarkupHooksHeading}'");
        return section.Value;
    }

    // The rows of the hooks' table: the class in the first cell, and the page that writes it, linked by its path
    // from docs/dashboard
    private static Dictionary<string, string> MarkupHooks(string markdown)
    {
        return Regex.Matches(MarkupHooksSection(markdown), @"^\| `((?:ns|hf)-[-a-z0-9]+)` \|.*\]\(([^)]+)\) \|\s*$", RegexOptions.Multiline)
            .ToDictionary(
                static row => row.Groups[1].Value,
                static row => Path.GetFullPath(Path.Combine(Root, "docs", "dashboard", row.Groups[2].Value.Replace('/', Path.DirectorySeparatorChar))),
                StringComparer.Ordinal);
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
