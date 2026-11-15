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
    public void EveryBuiltInStateSuffix_GivesTheLabels_ASoftColorAndATextColor()
    {
        string css = Read("nightshift-ui.css");

        Assert.All(BuiltInSuffixes(), suffix => Assert.Matches(
            $@"\.hf-app \.label-state-{suffix} {{ background-color: var\(--hf-[a-z-]+-soft\); color: var\(--hf-[a-z0-9-]+\); }}",
            css));
    }

    [Fact]
    public void EveryBuiltInStateSuffix_GivesTheCurrentStateCard_ItsColor()
    {
        string css = Read("nightshift-ui.css");

        Assert.All(BuiltInSuffixes(), suffix =>
        {
            Assert.Matches($@"\.hf-app \.state-card-state-{suffix} {{ border-left-color: var\(--hf-[a-z-]+\); }}", css);
            Assert.Matches($@"\.hf-app \.state-card-state-{suffix} \.state-card-title {{ color: var\(--hf-[a-z-]+\); }}", css);
        });
    }

    [Fact]
    public void TheBuiltInStateSuffixes_HaveColorsOfTheirOwn_NotAllTheNeutralOne()
    {
        string css = Read("nightshift-ui.css");

        string[] colors = [.. BuiltInSuffixes().Select(suffix =>
            Regex.Match(css, $@"\.hf-app \.label-state-{suffix} {{ background-color: var\((--hf-[a-z-]+)\)").Groups[1].Value)];

        Assert.Equal(6, colors.Distinct(StringComparer.Ordinal).Count());
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
        return reader.ReadToEnd();
    }
}
