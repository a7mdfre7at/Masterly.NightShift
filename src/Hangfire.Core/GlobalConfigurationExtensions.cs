// This file is part of Hangfire. Copyright © 2015 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Hangfire.Annotations;
using Hangfire.Common;
using Hangfire.Dashboard;
using Hangfire.Dashboard.Pages;
using Hangfire.Logging;
using Hangfire.Logging.LogProviders;
using Hangfire.Server;
using Newtonsoft.Json;

namespace Hangfire
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class GlobalConfigurationExtensions
    {
        public static IGlobalConfiguration<TStorage> UseStorage<TStorage>(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] TStorage storage)
            where TStorage : JobStorage
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            return configuration.Use(storage, static x => JobStorage.Current = x);
        }

        public static IGlobalConfiguration<TStorage> WithJobExpirationTimeout<TStorage>(
            [NotNull] this IGlobalConfiguration<TStorage> configuration,
            TimeSpan timeout)
            where TStorage : JobStorage
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            configuration.Entry.JobExpirationTimeout = timeout;
            return configuration;
        }

        public static IGlobalConfiguration<TActivator> UseActivator<TActivator>(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] TActivator activator)
            where TActivator : JobActivator
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (activator == null) throw new ArgumentNullException(nameof(activator));

            return configuration.Use(activator, static x => JobActivator.Current = x);
        }

        public static IGlobalConfiguration<JobActivator> UseDefaultActivator(
            [NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            return configuration.UseActivator(new JobActivator());
        }

        public static IGlobalConfiguration<TLogProvider> UseLogProvider<TLogProvider>(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] TLogProvider provider)
            where TLogProvider : ILogProvider
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            
            return configuration.Use(provider, static x => LogProvider.SetCurrentLogProvider(x));
        }

        /// <summary>
        /// Explicitly disables all the logging in Hangfire. Not recommended to use in a production
        /// application, because logging provides significant benefits in case of exceptions or other
        /// problems. But it can be useful for testing-related scenarios.
        /// </summary>
        public static IGlobalConfiguration<ILogProvider> UseNoOpLogProvider(
            [NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return configuration.UseLogProvider(LogProvider.NoOpLogProvider.Instance);
        }

        public static IGlobalConfiguration<NLogLogProvider> UseNLogLogProvider(
            [NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            return configuration.UseLogProvider(new NLogLogProvider());
        }

        public static IGlobalConfiguration<ColouredConsoleLogProvider> UseColouredConsoleLogProvider(
            [NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            return configuration.UseLogProvider(new ColouredConsoleLogProvider());
        }

        public static IGlobalConfiguration<ColouredConsoleLogProvider> UseColouredConsoleLogProvider(
            [NotNull] this IGlobalConfiguration configuration,
            LogLevel minLevel)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            return configuration.UseLogProvider(new ColouredConsoleLogProvider(minLevel));
        }

        public static IGlobalConfiguration<Log4NetLogProvider> UseLog4NetLogProvider(
            [NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            return configuration.UseLogProvider(new Log4NetLogProvider());
        }

        public static IGlobalConfiguration<ElmahLogProvider> UseElmahLogProvider(
            [NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            return configuration.UseLogProvider(new ElmahLogProvider());
        }

        public static IGlobalConfiguration<ElmahLogProvider> UseElmahLogProvider(
            [NotNull] this IGlobalConfiguration configuration,
            LogLevel minLevel)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            return configuration.UseLogProvider(new ElmahLogProvider(minLevel));
        }

        public static IGlobalConfiguration<EntLibLogProvider> UseEntLibLogProvider(
            [NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            return configuration.UseLogProvider(new EntLibLogProvider());
        }

        public static IGlobalConfiguration<SerilogLogProvider> UseSerilogLogProvider(
            [NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            return configuration.UseLogProvider(new SerilogLogProvider());
        }

        public static IGlobalConfiguration<LoupeLogProvider> UseLoupeLogProvider(
            [NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            return configuration.UseLogProvider(new LoupeLogProvider());
        }

        public static IGlobalConfiguration<TFilter> UseFilter<TFilter>(
            [NotNull] this IGlobalConfiguration configuration, 
            [NotNull] TFilter filter)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (filter == null) throw new ArgumentNullException(nameof(filter));

            return configuration.Use(filter, static x => GlobalJobFilters.Filters.Add(x));
        }

        public static IGlobalConfiguration<TFilterProvider> UseFilterProvider<TFilterProvider>(
            [NotNull] this IGlobalConfiguration configuration, 
            [NotNull] TFilterProvider filterProvider)
            where TFilterProvider : IJobFilterProvider
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (filterProvider == null) throw new ArgumentNullException(nameof(filterProvider));

            return configuration.Use(filterProvider, static x => JobFilterProviders.Providers.Add(x));
        }

        public static IGlobalConfiguration UseDashboardMetric(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] DashboardMetric metric)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (metric == null) throw new ArgumentNullException(nameof(metric));

            DashboardMetrics.AddMetric(metric);
            HomePage.Metrics.Add(metric);

            return configuration;
        }

        public static IGlobalConfiguration UseDashboardMetrics(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] params DashboardMetric[] metrics)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (metrics == null) throw new ArgumentNullException(nameof(metrics));

            foreach (var metric in metrics)
            {
                DashboardMetrics.AddMetric(metric);
                HomePage.Metrics.Add(metric);
            }

            return configuration;
        }

        public static IGlobalConfiguration UseJobDetailsRenderer(
            [NotNull] this IGlobalConfiguration configuration,
            int order,
            [NotNull] Func<JobDetailsRendererDto, NonEscapedString> renderer)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));

            JobDetailsRenderer.AddRenderer(order, renderer);

            return configuration;
        }

        public static IGlobalConfiguration UseTypeResolver(
            [NotNull] this IGlobalConfiguration configuration,
            [CanBeNull] Func<string, Type> typeResolver)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            TypeHelper.CurrentTypeResolver = typeResolver;
            return configuration;
        }

        public static IGlobalConfiguration UseTypeSerializer(
            [NotNull] this IGlobalConfiguration configuration,
            [CanBeNull] Func<Type, string> typeSerializer)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            TypeHelper.CurrentTypeSerializer = typeSerializer;
            return configuration;
        }

        public static IGlobalConfiguration UseDefaultTypeResolver(
            [NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return configuration.UseTypeResolver(null);
        }

        public static IGlobalConfiguration UseDefaultTypeSerializer(
            [NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return configuration.UseTypeSerializer(null);
        }

        public static IGlobalConfiguration UseSimpleAssemblyNameTypeSerializer(
            [NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return configuration.UseTypeSerializer(TypeHelper.SimpleAssemblyTypeSerializer);
        }

        public static IGlobalConfiguration UseIgnoredAssemblyVersionTypeResolver(
            [NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return configuration.UseTypeResolver(TypeHelper.IgnoredAssemblyVersionTypeResolver);
        }

        /// <summary>
        /// These settings are used to serialize user data like arguments or parameters.
        /// You can use <see cref="SerializationHelper.Serialize{T}(T, SerializationOption)"/> with <see cref="SerializationOption.User"/> option
        /// to serialize with specified settings
        /// </summary>
        public static IGlobalConfiguration UseSerializerSettings(
            [NotNull] this IGlobalConfiguration configuration,
            [CanBeNull] JsonSerializerSettings settings)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            SerializationHelper.SetUserSerializerSettings(settings);
            return configuration;
        }

        public static IGlobalConfiguration UseRecommendedSerializerSettings(
            [NotNull] this IGlobalConfiguration configuration)
        {
            return UseRecommendedSerializerSettings(configuration, null);
        }

        public static IGlobalConfiguration UseRecommendedSerializerSettings(
            [NotNull] this IGlobalConfiguration configuration,
            [CanBeNull] Action<JsonSerializerSettings> settingsConfiguration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            var settings = SerializationHelper.GetInternalSettings();
            settingsConfiguration?.Invoke(settings);

            SerializationHelper.SetUserSerializerSettings(settings);
            return configuration;
        }

        public static IGlobalConfiguration UseResultsInContinuations(this IGlobalConfiguration configuration)
        {
            return configuration
                .UseFilter(new JobParameterInjectionFilter())
                .UseFilter(new ContinuationsSupportAttribute(pushResults: true));
        }

        public static IGlobalConfiguration UseMaxLinesInExceptionDetails(
            [NotNull] this IGlobalConfiguration configuration,
            [CanBeNull] int? numberOfLines)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (numberOfLines.HasValue && numberOfLines.Value <= 1) throw new ArgumentOutOfRangeException(nameof(numberOfLines), "Must be either a positive value larger than 1; or a null value.");

            States.FailedState.MaxLinesInExceptionDetails = numberOfLines;
            return configuration;
        }

        public static IGlobalConfiguration UseMaxArgumentSizeToRender(
            [NotNull] this IGlobalConfiguration configuration,
            int size)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "Must be a positive value");

            JobMethodCallRenderer.MaxArgumentToRenderSize = size;
            return configuration;
        }

        public static IGlobalConfiguration UseDefaultCulture(
            [NotNull] this IGlobalConfiguration configuration,
            [CanBeNull] CultureInfo culture)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return configuration.UseFilter(new CaptureCultureAttribute(culture?.Name));
        }

        public static IGlobalConfiguration UseDefaultCulture(
            [NotNull] this IGlobalConfiguration configuration,
            [CanBeNull] CultureInfo culture,
            bool captureDefault)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return configuration.UseFilter(new CaptureCultureAttribute(culture?.Name, captureDefault));
        }

        public static IGlobalConfiguration UseDefaultCulture(
            [NotNull] this IGlobalConfiguration configuration,
            [CanBeNull] CultureInfo culture,
            [CanBeNull] CultureInfo uiCulture)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return configuration.UseFilter(new CaptureCultureAttribute(culture?.Name, uiCulture?.Name));
        }

        public static IGlobalConfiguration UseDefaultCulture(
            [NotNull] this IGlobalConfiguration configuration,
            [CanBeNull] CultureInfo culture,
            [CanBeNull] CultureInfo uiCulture,
            bool captureDefault)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return configuration.UseFilter(new CaptureCultureAttribute(culture?.Name, uiCulture?.Name, captureDefault));
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Emits OpenTelemetry-compatible traces and metrics for background jobs through the
        /// <see cref="HangfireTelemetry.ActivitySourceName"/> activity source and the
        /// <see cref="HangfireTelemetry.MeterName"/> meter. The trace context of the code that
        /// creates a job is stored in the <c>TraceParent</c> job parameter, so the span of the
        /// job's execution joins the same trace. Nothing is recorded unless a listener is attached.
        /// </summary>
        /// <param name="configuration">The global configuration.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static IGlobalConfiguration UseTelemetry([NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return configuration.UseFilter(new Diagnostics.TelemetryFilter());
        }

#endif

        /// <summary>
        /// Adds an interceptor that runs around every background job method, before the interceptors declared
        /// with attributes on the job's type and method. See <see cref="IJobInterceptor"/>.
        /// </summary>
        /// <param name="configuration">The global configuration.</param>
        /// <param name="interceptor">The interceptor.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static IGlobalConfiguration UseJobInterceptor(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] IJobInterceptor interceptor)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (interceptor == null) throw new ArgumentNullException(nameof(interceptor));

            GlobalJobInterceptors.Add(interceptor);
            return configuration;
        }

        /// <summary>
        /// Records per-method run counts, failure counts and durations for the dashboard's Insights page.
        /// See <see cref="JobInsightsAttribute"/>.
        /// </summary>
        /// <param name="configuration">The global configuration.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static IGlobalConfiguration UseJobInsights([NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return configuration.UseFilter(new JobInsightsAttribute());
        }

        /// <summary>
        /// Chooses whether job methods without a generated invoker are called through a delegate compiled
        /// once per method (the default) or through reflection. See <see cref="JobInvokerRegistry"/>.
        /// </summary>
        /// <param name="configuration">The global configuration.</param>
        /// <param name="enabled"><c>false</c> to use reflection for every method without a generated invoker.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static IGlobalConfiguration UseCompiledJobInvokers(
            [NotNull] this IGlobalConfiguration configuration,
            bool enabled)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            JobInvokerRegistry.UseCompiledInvokers = enabled;
            return configuration;
        }

        public static IGlobalConfiguration UseDashboardStylesheet(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] Assembly assembly,
            [NotNull] string resource)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            if (resource == null) throw new ArgumentNullException(nameof(resource));

            DashboardRoutes.AddStylesheet(assembly, resource);
            return configuration;
        }

        public static IGlobalConfiguration UseDashboardStylesheetDarkMode(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] Assembly assembly,
            [NotNull] string resource)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            if (resource == null) throw new ArgumentNullException(nameof(resource));

            DashboardRoutes.AddStylesheetDarkMode(assembly, resource);
            return configuration;
        }

        public static IGlobalConfiguration UseDashboardJavaScript(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] Assembly assembly,
            [NotNull] string resource)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            if (resource == null) throw new ArgumentNullException(nameof(resource));

            DashboardRoutes.AddJavaScript(assembly, resource);
            return configuration;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static IGlobalConfiguration<T> Use<T>(
            [NotNull] this IGlobalConfiguration configuration, T entry,
            [NotNull] Action<T> entryAction)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            entryAction(entry);

            return new ConfigurationEntry<T>(entry);
        }

        private sealed class ConfigurationEntry<T> : IGlobalConfiguration<T>
        {
            public ConfigurationEntry(T entry)
            {
                Entry = entry;
            }

            public T Entry { get; }
        }
    }
}