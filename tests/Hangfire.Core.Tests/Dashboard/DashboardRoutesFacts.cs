using System;
using System.Linq;
using Hangfire.Dashboard;
using Xunit;

namespace Hangfire.Core.Tests.Dashboard
{
    public class DashboardRoutesFacts
    {
        private static readonly Guid Build = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        private static readonly Guid AnotherBuild = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        private const string JavaScript = "Hangfire.Dashboard.Content.js.hangfire.js";
        private const string Stylesheet = "Hangfire.Dashboard.Content.css.hangfire.css";

        [Fact]
        public void CombineResourceHash_IsTheSameInEveryProcessAndOnEveryPlatform()
        {
            // Expected values come from an independent FNV-1a implementation, so a result that depended
            // on per-process randomization (like String.GetHashCode) or on the platform would fail here
            var single = DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, Build, JavaScript);
            var two = DashboardRoutes.CombineResourceHash(single, Build, Stylesheet);

            Assert.Equal(18652613, DashboardRoutes.InitialResourceHash);
            Assert.Equal(1170108145, single);
            Assert.Equal(1179987587, two);
        }

        [Fact]
        public void CombineResourceHash_Changes_WhenTheResourceAssemblyIsRebuilt()
        {
            var original = DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, Build, JavaScript);
            var rebuilt = DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, AnotherBuild, JavaScript);

            Assert.Equal(66424875, rebuilt);
            Assert.NotEqual(original, rebuilt);
        }

        [Fact]
        public void CombineResourceHash_DependsOnTheOrderOfResources_AsTheBundleDoes()
        {
            var jsFirst = DashboardRoutes.CombineResourceHash(
                DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, Build, JavaScript), Build, Stylesheet);
            var cssFirst = DashboardRoutes.CombineResourceHash(
                DashboardRoutes.CombineResourceHash(DashboardRoutes.InitialResourceHash, Build, Stylesheet), Build, JavaScript);

            Assert.Equal(461273807, cssFirst);
            Assert.NotEqual(jsFirst, cssFirst);
        }

        [Fact]
        public void CombineResourceHash_IsNeverNegative_SoMathAbsInTheLayoutCanNotOverflow()
        {
            var hash = DashboardRoutes.InitialResourceHash;

            foreach (var i in Enumerable.Range(0, 10000))
            {
                hash = DashboardRoutes.CombineResourceHash(hash, Guid.NewGuid(), $"resource-{i}");
                Assert.True(hash >= 0);
            }
        }

        [Fact]
        public void BundleHashes_IncludeHangfiresOwnResources()
        {
            // The static constructor registers the built-in scripts and stylesheets
            Assert.NotEqual(DashboardRoutes.InitialResourceHash, DashboardRoutes.JavaScriptsHashCode);
            Assert.NotEqual(DashboardRoutes.InitialResourceHash, DashboardRoutes.StylesheetsHashCode);
            Assert.NotEqual(DashboardRoutes.InitialResourceHash, DashboardRoutes.StylesheetsDarkModeHashCode);
        }
    }
}
