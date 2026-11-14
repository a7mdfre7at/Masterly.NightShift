using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class RouteValuesFacts
{
    [Theory]
    [InlineData("a/b")]
    [InlineData("a%2Fb")]
    [InlineData("a%252Fb")]
    [InlineData("a~2Fb")]
    [InlineData("~25~7E~")]
    [InlineData("100%")]
    [InlineData("%41")]
    [InlineData("nightly report: é ü 日本")]
    [InlineData("?#&+=;,")]
    [InlineData("plain-id")]
    public void Decode_ReadsBack_WhatEncodeWrote_AfterTheHostDecodedThePath(string value)
    {
        string path = DecodeAsKestrelDoes(RouteValues.Encode(value));

        Assert.DoesNotContain('/', path);
        Assert.Equal(value, RouteValues.Decode(path));
    }

    [Fact]
    public void Encode_GivesDifferentPaths_ForASlashAndALiteralPercent2F()
    {
        Assert.NotEqual(DecodeAsKestrelDoes(RouteValues.Encode("a/b")), DecodeAsKestrelDoes(RouteValues.Encode("a%2Fb")));
    }

    [Theory]
    [InlineData("plain-id", "plain-id")]
    [InlineData("reports%2Fdaily", "reports/daily")]
    [InlineData("reports%2fdaily", "reports/daily")]
    [InlineData("100%", "100%")]
    [InlineData("%41", "%41")]
    [InlineData("a~b", "a~b")]
    [InlineData("a~2", "a~2")]
    [InlineData("~", "~")]
    public void Decode_ReadsPathsWrittenWithEncodeURIComponentAlone(string path, string expected)
    {
        // Links of earlier versions and of extensions: Kestrel decoded everything but "%2F"
        Assert.Equal(expected, RouteValues.Decode(path));
    }

    [Fact]
    public void Encode_LeavesOrdinaryIdsAsTheyAre()
    {
        Assert.Equal("nightly-report_1.v2", RouteValues.Encode("nightly-report_1.v2"));
        Assert.Equal("nightly%20report", RouteValues.Encode("nightly report"));
    }

    // Kestrel and the other ASP.NET Core servers decode every escape of the path except "%2F"
    private static string DecodeAsKestrelDoes(string path)
    {
        const string Slash = "\u0000";
        return Uri.UnescapeDataString(path.Replace("%2F", Slash, StringComparison.OrdinalIgnoreCase))
            .Replace(Slash, "%2F", StringComparison.Ordinal);
    }
}
