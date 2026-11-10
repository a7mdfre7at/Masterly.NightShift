using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class SparklineFacts
{
    [Fact]
    public void Render_ReturnsAnEmptyString_WhenValuesAreNull()
    {
        NonEscapedString svg = Sparkline.Render(null, 100, 20, false);

        Assert.Equal(string.Empty, svg.ToString());
    }

    [Fact]
    public void Render_ReturnsAnEmptyString_WhenValuesAreEmpty()
    {
        NonEscapedString svg = Sparkline.Render([], 100, 20, false);

        Assert.Equal(string.Empty, svg.ToString());
    }

    [Fact]
    public void Render_ScalesThePoints_ToTheLargestValue()
    {
        string svg = Sparkline.Render([10, 20, 5], 100, 20, false).ToString();

        Assert.Equal(
            "<svg class=\"sparkline\" width=\"100\" height=\"20\" viewBox=\"0 0 100 20\" aria-hidden=\"true\">" +
            "<polyline fill=\"none\" stroke-width=\"1.5\" points=\"1,10 50,1 99,14.5\"/>" +
            "<circle class=\"sparkline-last\" r=\"2.5\" cx=\"99\" cy=\"14.5\"/>" +
            "</svg>",
            svg);
    }

    [Fact]
    public void Render_HighlightsTheLastPoint_WhenAsked()
    {
        string svg = Sparkline.Render([10, 20], 100, 20, true).ToString();

        Assert.Contains("<circle class=\"sparkline-last sparkline-last-failed\" r=\"2.5\" cx=\"99\" cy=\"1\"/>", svg);
    }

    [Fact]
    public void Render_DrawsASinglePoint_AtTheLeftEdge()
    {
        string svg = Sparkline.Render([7], 50, 10, false).ToString();

        Assert.Contains("points=\"1,1\"", svg);
        Assert.Contains("cx=\"1\" cy=\"1\"", svg);
    }

    [Fact]
    public void Render_KeepsZeroValuesOnTheBaseline_WhenEveryValueIsZero()
    {
        string svg = Sparkline.Render([0, 0], 12, 10, false).ToString();

        Assert.Contains("points=\"1,9 11,9\"", svg);
    }
}
