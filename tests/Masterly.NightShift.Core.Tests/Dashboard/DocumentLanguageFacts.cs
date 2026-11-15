using System.Globalization;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class DocumentLanguageFacts
{
    [Theory]
    [InlineData("de", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-AO", "pt")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("tr-TR", "tr-TR")]
    [InlineData("fa-IR", "fa")]
    public void Of_ACultureWithATranslation_IsTheMostSpecificLanguageTheDashboardHasStringsFor(string culture, string expected)
    {
        Assert.Equal(expected, DocumentLanguage.Of(CultureInfo.GetCultureInfo(culture)));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void Of_ACultureWithoutATranslation_IsEnglish_TheLanguageItsPagesAreShownIn(string culture)
    {
        Assert.Equal("en", DocumentLanguage.Of(CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void Of_TheInvariantCulture_IsEnglish_NotTheInvariantCulturesNameIv()
    {
        Assert.Equal("en", DocumentLanguage.Of(CultureInfo.InvariantCulture));
    }
}
