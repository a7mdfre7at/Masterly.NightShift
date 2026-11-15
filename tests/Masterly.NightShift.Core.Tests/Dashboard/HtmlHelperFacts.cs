using System.ComponentModel;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class HtmlHelperFacts
{
    private readonly Mock<RazorPage> _page;

    public HtmlHelperFacts()
    {
        _page = new Mock<RazorPage>();
    }

    [Fact]
    public void ToHumanDuration_FormatsFractionalSeconds()
    {
        HtmlHelper helper = CreateHelper();
        string? result = helper.ToHumanDuration(TimeSpan.FromSeconds(1.087));
        Assert.Equal("+1.087s", result);
    }

    [Fact]
    public void StackTrace_ShowsTheText_WhenItHasNoStackFrames()
    {
        HtmlHelper helper = CreateHelper();

        string result = helper.StackTrace("Cron expression '<x>' is invalid").ToString();

        Assert.Equal("Cron expression &#39;&lt;x&gt;&#39; is invalid", result);
    }

    [Fact]
    public void StackTrace_FormatsTheFrames_OfAStackTrace()
    {
        HtmlHelper helper = CreateHelper();

        string result = helper.StackTrace("System.Exception: Boom\r\n   at Masterly.NightShift.Jobs.Work() in C:\\Jobs.cs:line 12").ToString();

        Assert.StartsWith("System.Exception: Boom", result);
        Assert.Contains("<span class='st-frame'>", result);
        Assert.Contains("Jobs.cs", result);
    }

    [Fact]
    public void JobName_MasksRedactedArguments_InADisplayNameAttribute()
    {
        HtmlHelper helper = CreateHelper();
        Job job = Job.FromExpression(() => Connect("db.local", "hunter2"));

        string name = helper.JobName(job, includeQueue: false);

        Assert.Equal($"Connect to db.local as {RedactedAttribute.Mask}", name);
    }

    [DisplayName("Connect to {0} as {1}")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void Connect(string host, [Redacted] string password)
    {
    }

    [Fact]
    public void Icon_WritesAnSvgThatUsesTheSpriteSymbol_HiddenFromScreenReaders()
    {
        string html = new Pages.PageRenderer().Render(new IconPage(page => page.Html.Icon("trash-fill")));

        Assert.Equal($"<svg class=\"bi\" aria-hidden=\"true\"><use href=\"{DashboardIcons.Path}#bi-trash-fill\"></use></svg>", html);
        Assert.Matches("^/icons[0-9]+$", DashboardIcons.Path);
    }

    [Fact]
    public void Icon_AddsTheGivenClasses_AndEncodesThem()
    {
        string html = new Pages.PageRenderer().Render(new IconPage(page => page.Html.Icon("check-lg", "text-success \"x\"")));

        Assert.StartsWith("<svg class=\"bi text-success &quot;x&quot;\" aria-hidden=\"true\">", html);
    }

    [Theory]
    [InlineData("trash", "trash-fill")]
    [InlineData("glyphicon-time", "clock")]
    [InlineData("bi-card-list", "card-list")]
    [InlineData("warning-sign", "exclamation-triangle-fill")]
    public void Icon_GivesTheReplacement_OfAGlyphiconName(string name, string symbol)
    {
        string html = new Pages.PageRenderer().Render(new IconPage(page => page.Html.Icon(name)));

        Assert.Contains($"#bi-{symbol}\"", html);
    }

    [Fact]
    public void Icon_Throws_WithoutAName()
    {
        Assert.Throws<ArgumentNullException>(() => new Pages.PageRenderer().Render(new IconPage(page => page.Html.Icon(null!))));
    }

    [Fact]
    public void DashboardIcons_TheSpriteHoldsEveryIconAGlyphiconWasReplacedWith()
    {
        Assert.Contains(DashboardIcons.DefaultMenuIcon, DashboardIcons.Names);
        Assert.All(DashboardIcons.GlyphiconNames.Values, symbol => Assert.Contains(symbol, DashboardIcons.Names));
        Assert.Null(DashboardIcons.Resolve("no-such-icon"));
        Assert.Null(DashboardIcons.Resolve(null));
    }

    private sealed class IconPage(Func<RazorPage, NonEscapedString> icon) : RazorPage
    {
        public override void Execute()
        {
            Write(icon(this));
        }
    }

    private HtmlHelper CreateHelper()
    {
        return new HtmlHelper(_page.Object);
    }
}
