using System.Text.RegularExpressions;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

// Checks of a rendered page's structure that every redesigned page must pass
internal static class PageStructure
{
    // h1 first, and no heading more than one level below the one before it
    public static void AssertHeadingsInOrder(string html)
    {
        int[] levels = [.. Regex.Matches(html, "<h([1-6])[ >]").Select(static m => m.Groups[1].Value[0] - '0')];
        Assert.NotEmpty(levels);
        Assert.Equal(1, levels[0]);
        for (int i = 1; i < levels.Length; i++)
        {
            Assert.True(levels[i] <= levels[i - 1] + 1, $"h{levels[i]} after h{levels[i - 1]}");
        }
    }

    // Every header cell has a name, for screen readers at least
    public static void AssertNoEmptyHeaderCells(string html)
    {
        Assert.DoesNotMatch("<th[^>]*>\\s*</th>", html);
    }
}
