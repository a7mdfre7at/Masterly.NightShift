using System.Reflection;
using System.Text.RegularExpressions;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class StylesheetFacts
{
    private static readonly string[] BuiltInStates =
    [
        EnqueuedState.StateName, ScheduledState.StateName, ProcessingState.StateName, SucceededState.StateName,
        FailedState.StateName, DeletedState.StateName, AwaitingState.StateName,
    ];

    [Fact]
    public void EveryBuiltInState_HasAColourGroup_WhoseClassSetsItsFourColours()
    {
        string css = Read("nightshift-ui.css");

        Assert.All(BuiltInStates, state =>
        {
            string group = JobHistoryRenderer.GetStateColorGroup(state)!;
            Assert.Matches(
                $@"\.ns-state-{group} {{ --ns-state-color: var\(--ns-state-{group}\); --ns-state-on-color: var\(--ns-state-on-{group}\); --ns-state-container: var\(--ns-state-{group}-container\); --ns-state-on-container: var\(--ns-state-on-{group}-container\); }}",
                css);
        });
        Assert.Equal(7, BuiltInStates.Select(static state => JobHistoryRenderer.GetStateColorGroup(state)).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryColourGroup_IsATokenOfBothThemes()
    {
        string tokens = Read("nightshift-tokens.css");

        Assert.All(BuiltInStates, state =>
        {
            string group = JobHistoryRenderer.GetStateColorGroup(state)!;
            // Light, dark chosen, dark from the system
            Assert.Equal(3, Regex.Matches(tokens, $@"--ns-state-{group}-container: #[0-9a-f]{{6}};").Count);
            Assert.Equal(3, Regex.Matches(tokens, $@"--ns-state-on-{group}-container: #[0-9a-f]{{6}};").Count);
        });
    }

    [Fact]
    public void EveryBuiltInStateSuffix_GivesTheLabelsAndTheCurrentStateCard_TheColoursOfAStateGroup()
    {
        string css = Read("nightshift-ui.css");

        Assert.All(BuiltInSuffixes(), suffix =>
        {
            Assert.Matches($@"\.hf-app \.label-state-{suffix} {{ background-color: var\(--ns-state-[a-z]+-container\); color: var\(--ns-state-on-[a-z]+-container\); }}", css);
            Assert.Matches($@"\.hf-app \.state-card-state-{suffix} {{ border-left-color: var\(--ns-state-[a-z]+\); }}", css);
            Assert.Matches($@"\.hf-app \.state-card-state-{suffix} \.state-card-title {{ color: var\(--ns-state-[a-z]+\); }}", css);
        });
    }

    [Theory]
    [InlineData("nightshift.css")]
    [InlineData("nightshift-dark.css")]
    public void TheOlderStylesheets_DontColorStateLabelsOrCards_SoTheyDontOverrideTheTokens(string file)
    {
        string css = Read(file);

        Assert.DoesNotContain(".label-state-", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".state-card-state-", css, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nightshift.css")]
    [InlineData("nightshift-dark.css")]
    public void TheOlderStylesheets_WriteNoColours_TheyReadTheTokens(string file)
    {
        string css = Read(file);

        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b", css);
        Assert.DoesNotMatch(@"rgba?\(\s*\d", css);
    }

    [Fact]
    public void TheStylesheet_HasOneReducedMotionRule_ThatStopsEveryAnimation()
    {
        string css = Read("nightshift-ui.css");

        Assert.Single(Regex.Matches(css, @"@media \(prefers-reduced-motion: reduce\)"));
        System.Text.RegularExpressions.Match rule = Regex.Match(css, @"@media \(prefers-reduced-motion: reduce\) {(?<body>.*?)\n}", RegexOptions.Singleline);
        Assert.Contains("animation-iteration-count: 1 !important;", rule.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("transition-duration: 1ms !important;", rule.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStylesheet_HasOneFocusRing_ForTheKeyboard_AndAMintOneInTheDrawer()
    {
        string css = Read("nightshift-ui.css");

        Assert.Matches(@"\.hf-app :focus-visible,[^{]*{\s*outline: var\(--md-sys-focus-ring-width\) solid var\(--md-sys-focus-ring-color\);\s*outline-offset: var\(--md-sys-focus-ring-offset\);", css);
        Assert.Matches(@"\.hf-app \.hf-sidebar :focus-visible {\s*outline-color: var\(--ns-nav-accent\);", css);
        Assert.Contains("label:has(input:focus-visible)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void LinksInsideTheTextOfANote_AreUnderlined_AsTheirColourAloneDoesntSetThemApart()
    {
        // The Servers page's note about servers whose heartbeat is late links to Processing inside a sentence
        string css = Read("nightshift-ui.css");

        Assert.Matches(@"body\.hf-app \.ns-note-text a {\s*text-decoration: underline;", css);
    }

    [Fact]
    public void TheTokens_PutTheRootFontSizeBackToTheBrowsers_AndSizeTextInRem()
    {
        string tokens = Read("nightshift-tokens.css");
        string css = Read("nightshift-ui.css");

        Assert.Contains("--md-sys-typescale-body-medium: 400 0.875rem/1.25rem", tokens, StringComparison.Ordinal);
        Assert.Matches(@"html {\s*font-size: 100%;\s*}", css);
        Assert.DoesNotMatch(@"html {\s*font-size: 14px;", css);
        // No text below 11px
        Assert.DoesNotMatch(@"font-size: (9|10)(\.\d+)?px", css);
        Assert.DoesNotMatch(@"font-size: 0\.(5|6[0-7])\d*rem", css);
    }

    [Fact]
    public void TheTokensStylesheet_IsLoadedRightAfterBootstrap()
    {
        string first = DashboardRoutes.GetContentResourceName("css", "bootstrap.min.css");
        string tokens = DashboardRoutes.GetContentResourceName("css", "nightshift-tokens.css");

        using Stream? stream = typeof(DashboardRoutes).Assembly.GetManifestResourceStream(tokens);
        Assert.NotNull(stream);
        Assert.NotEqual(first, tokens);
    }

    private static string[] BuiltInSuffixes()
    {
        string[] suffixes = [.. BuiltInStates.Select(static state => JobHistoryRenderer.GetStateCssSuffix(state)!).Distinct(StringComparer.Ordinal)];
        Assert.Equal(["active", "info", "warning", "success", "danger", "inactive"], suffixes);
        return suffixes;
    }

    private static string Read(string file)
    {
        Assembly assembly = typeof(DashboardRoutes).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(DashboardRoutes.GetContentResourceName("css", file))!;
        using StreamReader reader = new(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
