using System.Text.Json;
using System.Text.Json.Serialization;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public class GloabalConfigurationExtensionsFacts
{
    [Fact, CleanSerializerSettings]
    public void UseSerializationSettings_AffectSerializationWithUserSettings()
    {
        GlobalConfiguration.Configuration.UseSerializerSettings(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        string result = SerializationHelper.Serialize(new CustomClass { StringProperty = "Value" }, SerializationOption.User);
        Assert.Equal(@"{""stringProperty"":""Value""}", result);
    }

    [Fact, CleanSerializerSettings]
    public void UseSerializationSettingsWithCallback_AffectSerializationWithUserSettings()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings(settings =>
        {
            settings.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        string result = SerializationHelper.Serialize(new CustomClass { StringProperty = "Value" }, SerializationOption.User);
        Assert.Equal(@"{""stringProperty"":""Value""}", result);
    }

    [Fact, CleanSerializerSettings]
    public void UseRecommendedSerializerSettingsWithCallback_LetsTheConvertersItAdds_WinOverTheBuiltInOnes()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings(settings =>
        {
            settings.Converters.Add(new JsonStringEnumConverter());
        });

        string result = SerializationHelper.Serialize(DayOfWeek.Tuesday, SerializationOption.User);

        Assert.Equal("\"Tuesday\"", result);
        Assert.Equal(DayOfWeek.Tuesday, SerializationHelper.Deserialize<DayOfWeek>(result, SerializationOption.User));
    }

    [Fact, CleanSerializerSettings]
    public void UseRecommendedSerializerSettingsWithCallback_KeepsTheTypeOfObjectValues()
    {
        GlobalConfiguration.Configuration.UseRecommendedSerializerSettings(settings =>
        {
            settings.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        string result = SerializationHelper.Serialize(new CustomClass { StringProperty = "Value" }, typeof(object), SerializationOption.User);

        Assert.Equal(
            $@"{{""$type"":""{typeof(CustomClass).FullName}, Masterly.NightShift.Core.Tests"",""stringProperty"":""Value""}}",
            result);
    }

    [Fact, CleanSerializerSettings]
    public void UseSerializerSettings_ReadsDataWrittenWithAnotherNamingPolicy()
    {
        GlobalConfiguration.Configuration.UseSerializerSettings(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        CustomClass? result = SerializationHelper.Deserialize<CustomClass>(@"{""StringProperty"":""Value""}", SerializationOption.User);

        Assert.Equal("Value", result.StringProperty);
    }

    [Fact, GlobalLock(Reason = "Changes GlobalJobInterceptors")]
    public void UseJobInterceptor_AddsTheInterceptorGlobally_AndReturnsTheSameConfiguration()
    {
        IJobInterceptor interceptor = new Mock<IJobInterceptor>().Object;
        IGlobalConfiguration configuration = GlobalConfiguration.Configuration;

        try
        {
            IGlobalConfiguration result = configuration.UseJobInterceptor(interceptor);

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
        IGlobalConfiguration configuration = GlobalConfiguration.Configuration;
        List<object> before = [.. GlobalJobFilters.Filters.Select(x => x.Instance)];
        object[] added = [];

        try
        {
            IGlobalConfiguration result = configuration.UseJobInsights();
            added = [.. GlobalJobFilters.Filters.Select(x => x.Instance).Except(before)];

            Assert.Collection(
                added,
                x => Assert.IsType<JobInsightsAttribute>(x),
                x => Assert.IsType<QueueInsightsFilter>(x));
            Assert.Same(added[1], Assert.IsAssignableFrom<IGlobalConfiguration<QueueInsightsFilter>>(result).Entry);
        }
        finally
        {
            foreach (object filter in added) GlobalJobFilters.Filters.Remove(filter);
        }
    }

    [Fact, GlobalLock(Reason = "Changes GlobalJobFilters")]
    public void UseJobInsights_AddsItsFiltersOnce_WhenCalledAgain()
    {
        IGlobalConfiguration configuration = GlobalConfiguration.Configuration;
        List<object> before = [.. GlobalJobFilters.Filters.Select(x => x.Instance)];
        object[] added = [];

        try
        {
            IGlobalConfiguration first = configuration.UseJobInsights();
            IGlobalConfiguration second = configuration.UseJobInsights();
            added = [.. GlobalJobFilters.Filters.Select(x => x.Instance).Except(before)];

            Assert.Equal(2, added.Length);
            Assert.Single(GlobalJobFilters.Filters, x => x.Instance is JobInsightsAttribute);
            Assert.Single(GlobalJobFilters.Filters, x => x.Instance is QueueInsightsFilter);
            Assert.Same(
                Assert.IsAssignableFrom<IGlobalConfiguration<QueueInsightsFilter>>(first).Entry,
                Assert.IsAssignableFrom<IGlobalConfiguration<QueueInsightsFilter>>(second).Entry);
        }
        finally
        {
            foreach (object filter in added) GlobalJobFilters.Filters.Remove(filter);
        }
    }

    [Fact, GlobalLock(Reason = "Changes GlobalJobFilters")]
    public void UseResultsInContinuations_AddsItsFiltersOnce_WhenCalledAgain()
    {
        IGlobalConfiguration configuration = GlobalConfiguration.Configuration;
        List<object> before = [.. GlobalJobFilters.Filters.Select(x => x.Instance)];
        object[] added = [];

        try
        {
            configuration.UseResultsInContinuations();
            configuration.UseResultsInContinuations();
            added = [.. GlobalJobFilters.Filters.Select(x => x.Instance).Except(before)];

            Assert.Collection(
                added,
                x => Assert.IsType<JobParameterInjectionFilter>(x),
                x => Assert.True(Assert.IsType<ContinuationsSupportAttribute>(x).PushResults));
        }
        finally
        {
            foreach (object filter in added) GlobalJobFilters.Filters.Remove(filter);
        }
    }

    [Fact, GlobalLock(Reason = "Changes GlobalJobFilters")]
    public void UseFailedJobRetention_AddsTheFilterOnce_AndCallingAgainChangesThePeriod()
    {
        IGlobalConfiguration configuration = GlobalConfiguration.Configuration;
        List<object> before = [.. GlobalJobFilters.Filters.Select(x => x.Instance)];
        object[] added = [];

        try
        {
            IGlobalConfiguration result = configuration.UseFailedJobRetention(TimeSpan.FromDays(30));
            configuration.UseFailedJobRetention(TimeSpan.FromDays(7));
            added = [.. GlobalJobFilters.Filters.Select(x => x.Instance).Except(before)];

            Assert.Same(configuration, result);
            FailedJobRetentionFilter filter = Assert.IsType<FailedJobRetentionFilter>(Assert.Single(added));
            Assert.Equal(TimeSpan.FromDays(7), filter.Retention);
        }
        finally
        {
            foreach (object filter in added) GlobalJobFilters.Filters.Remove(filter);
        }
    }

    [Fact, GlobalLock(Reason = "Reads GlobalJobFilters")]
    public void UseFailedJobRetention_RejectsInvalidArguments_WithoutAddingAFilter()
    {
        int before = GlobalJobFilters.Filters.Count;

        Assert.Throws<ArgumentNullException>("configuration", () => GlobalConfigurationExtensions.UseFailedJobRetention(null, TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>("retention", () => GlobalConfiguration.Configuration.UseFailedJobRetention(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>("retention", () => GlobalConfiguration.Configuration.UseFailedJobRetention(TimeSpan.FromHours(-1)));
        Assert.Throws<ArgumentOutOfRangeException>("retention", () => GlobalConfiguration.Configuration.UseFailedJobRetention(TimeSpan.FromDays(ExpireAfterAttribute.MaxDays + 1)));

        Assert.Equal(before, GlobalJobFilters.Filters.Count);
    }

    [Fact]
    public void UseJobInsights_ThrowsArgumentNullException_WhenConfigurationIsNull()
    {
        Assert.Throws<ArgumentNullException>("configuration", () => GlobalConfigurationExtensions.UseJobInsights(null));
    }

    [Fact, GlobalLock(Reason = "Changes ActivityFeed and GlobalJobFilters")]
    public void UseActivityFeed_TurnsTheFeedOn_WithTheFilterRegisteredOnce()
    {
        List<object> removed = [.. GlobalJobFilters.Filters.Where(x => x.Instance is ActivityFeedFilter).Select(x => x.Instance)];
        foreach (object filter in removed) GlobalJobFilters.Filters.Remove(filter);
        ActivityFeed.Enabled = false;

        try
        {
            IGlobalConfiguration result = GlobalConfiguration.Configuration.UseActivityFeed().UseActivityFeed(300);

            Assert.NotNull(result);
            Assert.True(ActivityFeed.Enabled);
            Assert.Equal(300, ActivityFeed.MaxEntries);
            Assert.Single(GlobalJobFilters.Filters, x => x.Instance is ActivityFeedFilter);
        }
        finally
        {
            ActivityFeed.Enabled = false;
            ActivityFeed.MaxEntries = 1000;
            foreach (object? filter in GlobalJobFilters.Filters.Where(x => x.Instance is ActivityFeedFilter).Select(x => x.Instance).ToList()) GlobalJobFilters.Filters.Remove(filter);
            foreach (object filter in removed) GlobalJobFilters.Filters.Add(filter);
        }
    }

    [Fact]
    public void UseActivityFeed_ThrowsArgumentNullException_WhenConfigurationIsNull_OrMaxEntriesIsNotPositive()
    {
        Assert.Throws<ArgumentNullException>("configuration", () => GlobalConfigurationExtensions.UseActivityFeed(null));
        Assert.Throws<ArgumentOutOfRangeException>("maxEntries", () => GlobalConfiguration.Configuration.UseActivityFeed(0));
    }

    [Fact, GlobalLock(Reason = "Reads ActivityFeed and GlobalJobFilters")]
    public void ActivityFeedFilter_IsRegisteredByDefault_ButTheFeedIsOffUntilTurnedOn()
    {
        // The feed writes to one storage list on every state change, so it is opt-in. Every test that turns it
        // on turns it off again.
        Assert.Contains(GlobalJobFilters.Filters, x => x.Instance is ActivityFeedFilter);
        Assert.False(ActivityFeed.Enabled);
    }

    [Theory, GlobalLock(Reason = "Changes JobInvokerRegistry.UseCompiledInvokers")]
    [InlineData(false)]
    [InlineData(true)]
    public void UseCompiledJobInvokers_SetsTheRegistrySwitch(bool enabled)
    {
        bool previous = JobInvokerRegistry.UseCompiledInvokers;
        IGlobalConfiguration configuration = GlobalConfiguration.Configuration;

        try
        {
            JobInvokerRegistry.UseCompiledInvokers = !enabled;

            IGlobalConfiguration result = configuration.UseCompiledJobInvokers(enabled);

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
