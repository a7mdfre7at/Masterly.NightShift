using System;
using Masterly.NightShift.Dashboard;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
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
            var helper = CreateHelper();
            var result = helper.ToHumanDuration(TimeSpan.FromSeconds(1.087));
            Assert.Equal("+1.087s", result);
        }

        [Fact]
        public void StackTrace_ShowsTheText_WhenItHasNoStackFrames()
        {
            var helper = CreateHelper();

            var result = helper.StackTrace("Cron expression '<x>' is invalid").ToString();

            Assert.Equal("Cron expression &#39;&lt;x&gt;&#39; is invalid", result);
        }

        [Fact]
        public void StackTrace_FormatsTheFrames_OfAStackTrace()
        {
            var helper = CreateHelper();

            var result = helper.StackTrace("System.Exception: Boom\r\n   at Masterly.NightShift.Jobs.Work() in C:\\Jobs.cs:line 12").ToString();

            Assert.StartsWith("System.Exception: Boom", result);
            Assert.Contains("<span class='st-frame'>", result);
            Assert.Contains("Jobs.cs", result);
        }

        private HtmlHelper CreateHelper()
        {
            return new HtmlHelper(_page.Object);
        }
    }
}
