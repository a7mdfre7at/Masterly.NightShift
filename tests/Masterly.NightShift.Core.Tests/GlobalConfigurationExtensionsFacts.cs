using System;
using System.Linq;
using System.Text.Json;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class GloabalConfigurationExtensionsFacts
    {
        [Fact, CleanSerializerSettings]
        public void UseSerializationSettings_AffectSerializationWithUserSettings()
        {
            GlobalConfiguration.Configuration.UseSerializerSettings(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var result = SerializationHelper.Serialize(new CustomClass { StringProperty = "Value" }, SerializationOption.User);
            Assert.Equal(@"{""stringProperty"":""Value""}", result);
        }

        [Fact, CleanSerializerSettings]
        public void UseSerializationSettingsWithCallback_AffectSerializationWithUserSettings()
        {
            GlobalConfiguration.Configuration.UseRecommendedSerializerSettings(settings =>
            {
                settings.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            });

            var result = SerializationHelper.Serialize(new CustomClass { StringProperty = "Value" }, SerializationOption.User);
            Assert.Equal(@"{""stringProperty"":""Value""}", result);
        }

        [Fact, GlobalLock(Reason = "Changes GlobalJobInterceptors")]
        public void UseJobInterceptor_AddsTheInterceptorGlobally_AndReturnsTheSameConfiguration()
        {
            var interceptor = new Mock<IJobInterceptor>().Object;
            var configuration = GlobalConfiguration.Configuration;

            try
            {
                var result = configuration.UseJobInterceptor(interceptor);

                Assert.Same(configuration, result);
                Assert.Contains(interceptor, GlobalJobInterceptors.Interceptors);
            }
            finally
            {
                GlobalJobInterceptors.Remove(interceptor);
            }
        }

        [Fact]
        public void UseJobInterceptor_ThrowsArgumentNullException_ForMissingArguments()
        {
            Assert.Throws<ArgumentNullException>("configuration", () => GlobalConfigurationExtensions.UseJobInterceptor(null, new Mock<IJobInterceptor>().Object));
            Assert.Throws<ArgumentNullException>("interceptor", () => GlobalConfiguration.Configuration.UseJobInterceptor(null));
        }

        [Fact, GlobalLock(Reason = "Changes GlobalJobFilters")]
        public void UseJobInsights_RegistersTheInsightsAndQueueInsightsFilters()
        {
            var configuration = GlobalConfiguration.Configuration;
            var before = GlobalJobFilters.Filters.Select(x => x.Instance).ToList();
            object[] added = Array.Empty<object>();

            try
            {
                var result = configuration.UseJobInsights();
                added = GlobalJobFilters.Filters.Select(x => x.Instance).Except(before).ToArray();

                Assert.Collection(
                    added,
                    x => Assert.IsType<JobInsightsAttribute>(x),
                    x => Assert.IsType<QueueInsightsFilter>(x));
                Assert.Same(added[1], Assert.IsAssignableFrom<IGlobalConfiguration<QueueInsightsFilter>>(result).Entry);
            }
            finally
            {
                foreach (var filter in added) GlobalJobFilters.Filters.Remove(filter);
            }
        }

        [Fact]
        public void UseJobInsights_ThrowsArgumentNullException_WhenConfigurationIsNull()
        {
            Assert.Throws<ArgumentNullException>("configuration", () => GlobalConfigurationExtensions.UseJobInsights(null));
        }

        [Theory, GlobalLock(Reason = "Changes JobInvokerRegistry.UseCompiledInvokers")]
        [InlineData(false)]
        [InlineData(true)]
        public void UseCompiledJobInvokers_SetsTheRegistrySwitch(bool enabled)
        {
            var previous = JobInvokerRegistry.UseCompiledInvokers;
            var configuration = GlobalConfiguration.Configuration;

            try
            {
                JobInvokerRegistry.UseCompiledInvokers = !enabled;

                var result = configuration.UseCompiledJobInvokers(enabled);

                Assert.Same(configuration, result);
                Assert.Equal(enabled, JobInvokerRegistry.UseCompiledInvokers);
            }
            finally
            {
                JobInvokerRegistry.UseCompiledInvokers = previous;
            }
        }

        [Fact]
        public void UseCompiledJobInvokers_ThrowsArgumentNullException_WhenConfigurationIsNull()
        {
            Assert.Throws<ArgumentNullException>("configuration", () => GlobalConfigurationExtensions.UseCompiledJobInvokers(null, true));
        }

        public class CustomClass
        {
            public string StringProperty { get; set; }
        }
    }
}
