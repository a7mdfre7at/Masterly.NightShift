using Masterly.NightShift.Dashboard.Pages;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard.Pages
{
    public class PageRendererFacts
    {
        [Fact]
        public void Render_ProducesTheLayoutAroundThePage_WhenStorageIsEmpty()
        {
            var renderer = new PageRenderer();

            var html = renderer.Render(new TagsPage());

            Assert.Contains("<html", html);
            Assert.Contains("page-title", html);
        }
    }
}
