using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class DashboardContextFacts
    {
        [Fact, GlobalLock(Reason = "Sets the global time zone resolver")]
        public void GetRecurringJobManager_UsesTheGlobalTimeZoneResolver_WhenTheOptionsHaveNone()
        {
            var resolver = new Mock<ITimeZoneResolver>().Object;
            GlobalConfiguration.Configuration.UseTimeZoneResolver(resolver);

            try
            {
                var context = new DashboardContextStub(new DashboardOptions());

                var manager = Assert.IsType<RecurringJobManager>(context.GetRecurringJobManager());

                Assert.Same(resolver, manager.TimeZoneResolver);
            }
            finally
            {
                GlobalConfiguration.Configuration.UseTimeZoneResolver(null);
            }
        }

        [Fact, GlobalLock(Reason = "Sets the global time zone resolver")]
        public void GetRecurringJobManager_PrefersTheResolverFromTheOptions()
        {
            var global = new Mock<ITimeZoneResolver>().Object;
            var fromOptions = new Mock<ITimeZoneResolver>().Object;
            GlobalConfiguration.Configuration.UseTimeZoneResolver(global);

            try
            {
                var context = new DashboardContextStub(new DashboardOptions { TimeZoneResolver = fromOptions });

                var manager = Assert.IsType<RecurringJobManager>(context.GetRecurringJobManager());

                Assert.Same(fromOptions, manager.TimeZoneResolver);
            }
            finally
            {
                GlobalConfiguration.Configuration.UseTimeZoneResolver(null);
            }
        }

        [Fact]
        public void AntiforgeryTokens_AreGenerated_OnlyWhenFirstRead_AndOnlyOnce()
        {
            var context = new TokenContext();

            Assert.Equal(0, context.Generated);

            Assert.Equal("X-CSRF", context.AntiforgeryHeader);
            Assert.Equal("token-1", context.AntiforgeryToken);
            Assert.Equal("X-CSRF", context.AntiforgeryHeader);
            Assert.Equal(1, context.Generated);
        }

        [Fact]
        public void AntiforgeryTokens_AreNotGenerated_WhenAValueWasSet()
        {
            var context = new TokenContext { AntiforgeryToken = "given" };

            Assert.Equal("given", context.AntiforgeryToken);
            Assert.Null(context.AntiforgeryHeader);
            Assert.Equal(0, context.Generated);
        }

        [Fact]
        public void AntiforgeryTokens_AreNull_WhenTheHostGeneratesNone()
        {
            var context = new DashboardContextStub(new DashboardOptions());

            Assert.Null(context.AntiforgeryHeader);
            Assert.Null(context.AntiforgeryToken);
        }

        private sealed class TokenContext : DashboardContext
        {
            public TokenContext() : base(new JobStorageStub(), new DashboardOptions())
            {
            }

            public int Generated { get; private set; }

            protected override void GenerateAntiforgeryTokens()
            {
                Generated++;
                AntiforgeryHeader = "X-CSRF";
                AntiforgeryToken = "token-" + Generated;
            }
        }
    }
}
