using Masterly.NightShift.Dashboard.Pages;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages;

public class PageRendererFacts
{
    [Fact]
    public void Render_ProducesTheLayoutAroundThePage_WhenStorageIsEmpty()
    {
        PageRenderer renderer = new();

        string html = renderer.Render(new TagsPage());

        Assert.Contains("<html", html);
        Assert.Contains("page-title", html);
    }
}
