using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Masterly.NightShift.AspNetCore.Tests
{
    public class NightShiftServiceCollectionExtensionsFacts
    {
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();

        [Fact]
        public void AddNightShift_RegistersIRecurringJobManagerV3()
        {
            using (var provider = BuildProvider())
            {
                var manager = provider.GetRequiredService<IRecurringJobManagerV3>();

                var recurringJobManager = Assert.IsType<RecurringJobManager>(manager);
                Assert.Same(_storage.Object, recurringJobManager.Storage);
                Assert.Same(manager, provider.GetRequiredService<IRecurringJobManagerV3>());
            }
        }

        [Fact]
        public void AddNightShift_RegistersEveryRecurringJobManagerInterface()
        {
            using (var provider = BuildProvider())
            {
                Assert.NotNull(provider.GetRequiredService<IRecurringJobManager>());
                Assert.NotNull(provider.GetRequiredService<IRecurringJobManagerV2>());
                Assert.NotNull(provider.GetRequiredService<IRecurringJobManagerV3>());
            }
        }

        [Fact]
        public void AddNightShift_RegistersIRecurringJobManagerFactoryV3_AsTheSameFactoryAsTheOtherVersions()
        {
            using (var provider = BuildProvider())
            {
                var factory = provider.GetRequiredService<IRecurringJobManagerFactoryV3>();

                Assert.Same(factory, provider.GetRequiredService<IRecurringJobManagerFactoryV2>());
                Assert.Same(factory, provider.GetRequiredService<IRecurringJobManagerFactory>());
                Assert.IsType<RecurringJobManager>(factory.GetManagerV3(_storage.Object));
            }
        }

        [Fact]
        public void AddNightShift_KeepsAnIRecurringJobManagerV3_RegisteredBefore()
        {
            var custom = new Mock<IRecurringJobManagerV3>().Object;
            var services = new ServiceCollection()
                .AddSingleton(custom)
                .AddSingleton(_storage.Object)
                .AddNightShift(_ => { });

            using (var provider = services.BuildServiceProvider())
            {
                Assert.Same(custom, provider.GetRequiredService<IRecurringJobManagerV3>());
            }
        }

        [Fact]
        public void AddNightShift_CalledTwice_RunsOnlyTheLastConfigurationCallback()
        {
            var firstCalls = 0;
            var lastCalls = 0;
            var services = new ServiceCollection()
                .AddSingleton(_storage.Object)
                .AddNightShift(_ => firstCalls++)
                .AddNightShift(_ => lastCalls++);

            using (var provider = services.BuildServiceProvider())
            {
                provider.GetRequiredService<IRecurringJobManagerV3>();
                provider.GetRequiredService<IGlobalConfiguration>();

                Assert.Equal(0, firstCalls);
                Assert.Equal(1, lastCalls);
            }
        }

        [Fact]
        public void AddNightShift_MakesTheRegisteredTimeZoneResolverTheGlobalOne()
        {
            var resolver = new Mock<ITimeZoneResolver>().Object;
            var services = new ServiceCollection()
                .AddSingleton(resolver)
                .AddSingleton(_storage.Object)
                .AddNightShift(_ => { });

            try
            {
                using (var provider = services.BuildServiceProvider())
                {
                    provider.GetRequiredService<IGlobalConfiguration>();

                    Assert.Same(resolver, GetGlobalTimeZoneResolver());
                }
            }
            finally
            {
                GlobalConfiguration.Configuration.UseTimeZoneResolver(null);
            }
        }

        [Fact]
        public void AddNightShift_KeepsAGlobalTimeZoneResolverSetElsewhere_WhenOnlyTheDefaultIsRegistered()
        {
            var setElsewhere = new Mock<ITimeZoneResolver>().Object;
            GlobalConfiguration.Configuration.UseTimeZoneResolver(setElsewhere);
            var services = new ServiceCollection()
                .AddSingleton(_storage.Object)
                .AddNightShift(_ => { });

            try
            {
                using (var provider = services.BuildServiceProvider())
                {
                    provider.GetRequiredService<IGlobalConfiguration>();

                    Assert.Same(setElsewhere, GetGlobalTimeZoneResolver());
                }
            }
            finally
            {
                GlobalConfiguration.Configuration.UseTimeZoneResolver(null);
            }
        }

        [Fact]
        public void AddNightShift_LetsTheConfigurationCallbackReplaceTheGlobalTimeZoneResolver()
        {
            var registered = new Mock<ITimeZoneResolver>().Object;
            var configured = new Mock<ITimeZoneResolver>().Object;
            var services = new ServiceCollection()
                .AddSingleton(registered)
                .AddSingleton(_storage.Object)
                .AddNightShift(config => config.UseTimeZoneResolver(configured));

            try
            {
                using (var provider = services.BuildServiceProvider())
                {
                    provider.GetRequiredService<IGlobalConfiguration>();

                    Assert.Same(configured, GetGlobalTimeZoneResolver());
                }
            }
            finally
            {
                GlobalConfiguration.Configuration.UseTimeZoneResolver(null);
            }
        }

        [Fact]
        public void AddNightShiftServer_WithConfiguration_ReadsTheServerOptions()
        {
            var configuration = BuildConfiguration(new Dictionary<string, string>
            {
                { "NightShift:Server:ServerName", "orders-worker" },
                { "NightShift:Server:WorkerCount", "3" },
                { "NightShift:Server:Queues:0", "critical" },
                { "NightShift:Server:Queues:1", "default" },
                { "NightShift:Server:IsLightweightServer", "true" },
                { "NightShift:Server:SchedulePollingInterval", "00:00:05" },
                { "NightShift:Server:ShutdownTimeout", "00:00:30" },
                { "NightShift:Server:StopTimeout", "00:00:10" },
                { "NightShift:Server:HeartbeatInterval", "00:00:20" },
                { "NightShift:Server:ServerCheckInterval", "00:02:00" },
                { "NightShift:Server:ServerTimeout", "00:10:00" },
                { "NightShift:Server:CancellationCheckInterval", "00:00:03" },
                { "NightShift:Server:MaxDegreeOfParallelismForSchedulers", "2" },
            });

            var options = ResolveServerOptions(configuration.GetSection("NightShift:Server"));

            Assert.Equal("orders-worker", options.ServerName);
            Assert.Equal(3, options.WorkerCount);
            Assert.Equal(new[] { "critical", "default" }, options.Queues);
            Assert.True(options.IsLightweightServer);
            Assert.Equal(TimeSpan.FromSeconds(5), options.SchedulePollingInterval);
            Assert.Equal(TimeSpan.FromSeconds(30), options.ShutdownTimeout);
            Assert.Equal(TimeSpan.FromSeconds(10), options.StopTimeout);
            Assert.Equal(TimeSpan.FromSeconds(20), options.HeartbeatInterval);
            Assert.Equal(TimeSpan.FromMinutes(2), options.ServerCheckInterval);
            Assert.Equal(TimeSpan.FromMinutes(10), options.ServerTimeout);
            Assert.Equal(TimeSpan.FromSeconds(3), options.CancellationCheckInterval);
            Assert.Equal(2, options.MaxDegreeOfParallelismForSchedulers);
        }

        [Fact]
        public void AddNightShiftServer_WithConfiguration_ReadsACommaSeparatedQueueList()
        {
            var configuration = BuildConfiguration(new Dictionary<string, string> { { "Server:Queues", "critical, default ,reports" } });

            var options = ResolveServerOptions(configuration.GetSection("Server"));

            Assert.Equal(new[] { "critical", "default", "reports" }, options.Queues);
        }

        [Fact]
        public void AddNightShiftServer_WithConfiguration_LetsACommaSeparatedValueOverrideAnArray_FromALaterSource()
        {
            // appsettings.json with an array, then an environment variable NightShift__Server__Queues=low,default
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { { "Server:Queues:0", "critical" }, { "Server:Queues:1", "default" } })
                .AddInMemoryCollection(new Dictionary<string, string> { { "Server:Queues", "low,default" } })
                .Build();

            var options = ResolveServerOptions(configuration.GetSection("Server"));

            Assert.Equal(new[] { "low", "default" }, options.Queues);
        }

        [Fact]
        public void AddNightShiftServer_WithConfiguration_PrefersACommaSeparatedValueToAnArray_WhateverTheSourceOrder()
        {
            // Configuration doesn't tell which source set which form, so the comma-separated value always wins
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { { "Server:Queues", "low,default" } })
                .AddInMemoryCollection(new Dictionary<string, string> { { "Server:Queues:0", "critical" }, { "Server:Queues:1", "default" } })
                .Build();

            var options = ResolveServerOptions(configuration.GetSection("Server"));

            Assert.Equal(new[] { "low", "default" }, options.Queues);
        }

        [Fact]
        public void AddNightShiftServer_WithConfiguration_ReadsTheArray_WhenTheValueIsEmpty()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { { "Server:Queues:0", "critical" }, { "Server:Queues:1", "default" } })
                .AddInMemoryCollection(new Dictionary<string, string> { { "Server:Queues", " " } })
                .Build();

            var options = ResolveServerOptions(configuration.GetSection("Server"));

            Assert.Equal(new[] { "critical", "default" }, options.Queues);
        }

        [Fact]
        public void AddNightShiftServer_WithConfiguration_KeepsTheDefaults_ForMissingKeys()
        {
            var defaults = new BackgroundJobServerOptions();

            var options = ResolveServerOptions(BuildConfiguration(new Dictionary<string, string>()).GetSection("Server"));

            Assert.Null(options.ServerName);
            Assert.Equal(defaults.WorkerCount, options.WorkerCount);
            Assert.Equal(new[] { "default" }, options.Queues);
            Assert.Equal(defaults.SchedulePollingInterval, options.SchedulePollingInterval);
            Assert.False(options.IsLightweightServer);
        }

        [Theory]
        [InlineData("WorkerCount", "lots")]
        [InlineData("WorkerCount", "0")]
        [InlineData("SchedulePollingInterval", "soon")]
        [InlineData("IsLightweightServer", "maybe")]
        [InlineData("CancellationCheckInterval", "-00:00:05")]
        [InlineData("CancellationCheckInterval", "00:00:00")]
        [InlineData("HeartbeatInterval", "00:00:00")]
        [InlineData("ServerCheckInterval", "00:00:00")]
        [InlineData("ServerTimeout", "00:00:00")]
        public void AddNightShiftServer_WithConfiguration_NamesTheSetting_WhenAValueIsInvalid(string key, string value)
        {
            var configuration = BuildConfiguration(new Dictionary<string, string> { { "NightShift:Server:" + key, value } });

            var exception = Assert.Throws<InvalidOperationException>(() => ResolveServerOptions(configuration.GetSection("NightShift:Server")));

            Assert.Contains("NightShift:Server:" + key, exception.Message);
            Assert.Contains(value, exception.Message);
        }

        [Fact]
        public void AddNightShiftServer_WithConfiguration_RunsTheOptionsActionAfterReadingTheSection()
        {
            var configuration = BuildConfiguration(new Dictionary<string, string> { { "Server:WorkerCount", "3" } });

            var options = ResolveServerOptions(configuration.GetSection("Server"), action: options => options.WorkerCount++);

            Assert.Equal(4, options.WorkerCount);
        }

        [Fact]
        public void AddNightShiftServer_WithOnlyAConfiguration_RegistersAServerUsingIt()
        {
            var configuration = BuildConfiguration(new Dictionary<string, string> { { "Server:WorkerCount", "7" } });
            var services = new ServiceCollection()
                .AddSingleton(_storage.Object)
                .AddNightShift(_ => { })
                .AddNightShiftServer(configuration.GetSection("Server"));

            using (var provider = services.BuildServiceProvider())
            {
                var server = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundJobServerHostedService>());

                // The options are private to the hosted service
                var options = (BackgroundJobServerOptions)typeof(BackgroundJobServerHostedService)
                    .GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(server);
                Assert.Equal(7, options.WorkerCount);
            }
        }

        [Fact]
        public void AddNightShiftServer_WithConfiguration_ThrowsAnException_WhenAnArgumentIsNull()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration(new Dictionary<string, string>());

            Assert.Equal("services", Assert.Throws<ArgumentNullException>(() => NightShiftServiceCollectionExtensions.AddNightShiftServer(null, configuration)).ParamName);
            Assert.Equal("configuration", Assert.Throws<ArgumentNullException>(() => services.AddNightShiftServer((IConfiguration)null)).ParamName);
            Assert.Equal("optionsAction", Assert.Throws<ArgumentNullException>(() => services.AddNightShiftServer(configuration, null)).ParamName);
        }

        private static IConfigurationRoot BuildConfiguration(Dictionary<string, string> values)
        {
            return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }

        private BackgroundJobServerOptions ResolveServerOptions(IConfiguration section, Action<BackgroundJobServerOptions> action = null)
        {
            BackgroundJobServerOptions captured = null;
            var services = new ServiceCollection()
                .AddSingleton(_storage.Object)
                .AddNightShift(_ => { })
                .AddNightShiftServer(section, (_, options) =>
                {
                    action?.Invoke(options);
                    captured = options;
                });

            using (var provider = services.BuildServiceProvider())
            {
                Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundJobServerHostedService>());
            }

            return captured;
        }

        private static object GetGlobalTimeZoneResolver()
        {
            // The global resolver is internal to Masterly.NightShift.Core
            var type = typeof(RecurringJob).Assembly.GetType("Masterly.NightShift.GlobalTimeZoneResolver", throwOnError: true);
            return type.GetProperty("Current").GetValue(null);
        }

        private ServiceProvider BuildProvider()
        {
            return new ServiceCollection()
                .AddSingleton(_storage.Object)
                .AddNightShift(_ => { })
                .BuildServiceProvider();
        }
    }
}
