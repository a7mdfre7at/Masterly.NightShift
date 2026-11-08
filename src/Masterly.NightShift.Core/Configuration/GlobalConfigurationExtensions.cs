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
using System.Text.Json;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Logging.LogProviders;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift
{
    /// <summary>
    /// The <c>Use*</c> and <c>Set*</c> methods that configure Masterly.NightShift through
    /// <see cref="IGlobalConfiguration"/>: storage, serializer settings, filters, logging and the optional features.
    /// </summary>
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

        /// <summary>
        /// Expires jobs that end up failed (their retries are used up) after <paramref name="retention"/>, so they
        /// don't pile up in storage and on the dashboard. The expiration is set in the same transaction as the Failed
        /// state; a job retried, rescheduled or deleted before then follows its new state. An
        /// <see cref="ExpireAfterAttribute"/> with <see cref="ExpireAfterAttribute.IncludeFailed"/> on a job's method
        /// or type wins over this. Without it failed jobs are kept until someone retries or deletes them. Calling it
        /// again changes the period; the filter is added only once.
        /// </summary>
        /// <param name="configuration">The global configuration.</param>
        /// <param name="retention">How long a failed job is kept: positive, and at most <see cref="ExpireAfterAttribute.MaxDays"/> days.</param>
        /// <returns>The same configuration, for chaining.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="retention"/> is not positive, or it is too long.</exception>
        public static IGlobalConfiguration UseFailedJobRetention([NotNull] this IGlobalConfiguration configuration, TimeSpan retention)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            FailedJobRetentionFilter.Validate(retention, nameof(retention));

            var filter = (FailedJobRetentionFilter)AddGlobalFilterOnce(
                static registered => registered is FailedJobRetentionFilter,
                () => new FailedJobRetentionFilter(retention));

            filter.Retention = retention;
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

        /// <summary>
        /// Sends Masterly.NightShift's log messages to <paramref name="provider"/>, including those of loggers
        /// created before this call. On the netstandard2.0 build, loggers created before the first call keep the
        /// provider they started with.
        /// </summary>
        public static IGlobalConfiguration<TLogProvider> UseLogProvider<TLogProvider>(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] TLogProvider provider)
            where TLogProvider : ILogProvider
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            
            return configuration.Use(provider, static x => LateBoundLogProvider.Use(x));
        }

        /// <summary>
        /// Explicitly disables all the logging in Masterly.NightShift. Not recommended to use in a production
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
        /// Sets the time zone resolver used where none is passed explicitly: by the static
        /// <see cref="RecurringJob"/> class, by <see cref="RecurringJobManager"/> and
        /// <see cref="RecurringJobScheduler"/> instances created without one, by
        /// <see cref="RecurringJobManifest.Apply(string, bool)"/>, and by <see cref="BackgroundJobServer"/>
        /// when <see cref="BackgroundJobServerOptions.TimeZoneResolver"/> is <c>null</c>.
        /// </summary>
        /// <remarks>
        /// With <c>AddNightShift</c>, the server, the injected managers and the dashboard use the container's
        /// <see cref="ITimeZoneResolver"/>, and <c>AddNightShift</c> makes that resolver the global one before its
        /// configuration callback runs. Register a custom resolver in the container: calling this method in the
        /// callback changes only the parts that don't come from the container.
        /// </remarks>
        /// <param name="configuration">The global configuration.</param>
        /// <param name="timeZoneResolver">The resolver, or <c>null</c> to restore <see cref="DefaultTimeZoneResolver"/>.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static IGlobalConfiguration UseTimeZoneResolver(
            [NotNull] this IGlobalConfiguration configuration,
            [CanBeNull] ITimeZoneResolver timeZoneResolver)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            GlobalTimeZoneResolver.Set(timeZoneResolver);
            return configuration;
        }

        /// <summary>
        /// Sets the System.Text.Json options used to serialize user data like job arguments and parameters, or
        /// restores the defaults when <paramref name="settings"/> is <c>null</c>.
        /// <see cref="SerializationHelper.Serialize{T}(T, SerializationOption)"/> with <see cref="SerializationOption.User"/>
        /// uses them too.
        /// </summary>
        /// <remarks>
        /// A copy of <paramref name="settings"/> is used, so changing them afterwards has no effect. Data is written
        /// exactly as they say, with the library's compatibility converters added after yours. Data is read leniently
        /// whatever they say (property names in any case, quoted numbers, public fields), so jobs written before
        /// with other options, for example another naming policy, still bind their arguments.
        /// </remarks>
        public static IGlobalConfiguration UseSerializerSettings(
            [NotNull] this IGlobalConfiguration configuration,
            [CanBeNull] JsonSerializerOptions settings)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            SerializationHelper.SetUserSerializerSettings(settings);
            return configuration;
        }

        /// <summary>
        /// Serializes user data with the same System.Text.Json options as internal data.
        /// </summary>
        public static IGlobalConfiguration UseRecommendedSerializerSettings(
            [NotNull] this IGlobalConfiguration configuration)
        {
            return UseRecommendedSerializerSettings(configuration, null);
        }

        /// <summary>
        /// Serializes user data with a copy of the options used for internal data, changed by
        /// <paramref name="settingsConfiguration"/>. Converters it adds win over the built-in ones for the same type.
        /// </summary>
        public static IGlobalConfiguration UseRecommendedSerializerSettings(
            [NotNull] this IGlobalConfiguration configuration,
            [CanBeNull] Action<JsonSerializerOptions> settingsConfiguration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            SerializationHelper.SetRecommendedSerializerSettings(settingsConfiguration);
            return configuration;
        }

        public static IGlobalConfiguration UseResultsInContinuations(this IGlobalConfiguration configuration)
        {
            AddGlobalFilterOnce(static filter => filter is JobParameterInjectionFilter, static () => new JobParameterInjectionFilter());
            var continuations = (ContinuationsSupportAttribute)AddGlobalFilterOnce(
                static filter => filter is ContinuationsSupportAttribute registered && registered.PushResults,
                static () => new ContinuationsSupportAttribute(pushResults: true));

            return configuration.Use(continuations, static _ => { });
        }

        private static readonly object GlobalFiltersLock = new object();

        // Adds a filter that Use* methods add only once: a process that configures NightShift again (several hosts
        // in one test run, for example) doesn't pile up copies in the global filters. Returns the registered filter.
        private static object AddGlobalFilterOnce(Func<object, bool> isRegistered, Func<object> create)
        {
            lock (GlobalFiltersLock)
            {
                foreach (var filter in GlobalJobFilters.Filters)
                {
                    if (isRegistered(filter.Instance)) return filter.Instance;
                }

                var created = create();
                GlobalJobFilters.Filters.Add(created);
                return created;
            }
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
        /// <see cref="NightShiftTelemetry.ActivitySourceName"/> activity source and the
        /// <see cref="NightShiftTelemetry.MeterName"/> meter. The trace context of the code that
        /// creates a job is stored in the <c>TraceParent</c> job parameter, so the span of the
        /// job's execution joins the same trace. Nothing is recorded unless a listener is attached.
        /// </summary>
        /// <param name="configuration">The global configuration.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static IGlobalConfiguration UseTelemetry([NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            var telemetry = (Diagnostics.TelemetryFilter)AddGlobalFilterOnce(static filter => filter is Diagnostics.TelemetryFilter, static () => new Diagnostics.TelemetryFilter());
            return configuration.Use(telemetry, static _ => { });
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
        /// See <see cref="JobInsightsAttribute"/>. Calling it again adds nothing.
        /// </summary>
        /// <param name="configuration">The global configuration.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static IGlobalConfiguration UseJobInsights([NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            AddGlobalFilterOnce(static filter => filter is JobInsightsAttribute, static () => new JobInsightsAttribute());
            var queueInsights = (QueueInsightsFilter)AddGlobalFilterOnce(static filter => filter is QueueInsightsFilter, static () => new QueueInsightsFilter());
            return configuration.Use(queueInsights, static _ => { });
        }

        /// <summary>
        /// Turns on the <see cref="ActivityFeed"/>: every state transition is recorded in the storage list
        /// <c>activity</c>, which feeds the dashboard's live activity and Timeline. Call it in every process that
        /// changes job states (servers, and apps that create jobs). Entries are written in the background after each
        /// state change commits, so the feed can't slow down or fail a state change, but it still costs a storage
        /// write per batch of transitions.
        /// </summary>
        /// <param name="configuration">The global configuration.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static IGlobalConfiguration UseActivityFeed([NotNull] this IGlobalConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            AddGlobalFilterOnce(static filter => filter is ActivityFeedFilter, static () => new ActivityFeedFilter());

            ActivityFeed.Enabled = true;
            return configuration;
        }

        /// <summary>
        /// Turns on the <see cref="ActivityFeed"/> and sets how many entries it keeps. See
        /// <see cref="UseActivityFeed(IGlobalConfiguration)"/>.
        /// </summary>
        /// <param name="configuration">The global configuration.</param>
        /// <param name="maxEntries">How many entries the list keeps, see <see cref="ActivityFeed.MaxEntries"/>.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static IGlobalConfiguration UseActivityFeed([NotNull] this IGlobalConfiguration configuration, int maxEntries)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries), "Value must be positive.");

            ActivityFeed.MaxEntries = maxEntries;
            return configuration.UseActivityFeed();
        }

        /// <summary>
        /// Posts an alert to <paramref name="url"/> when a job finally fails, a deadline is missed or a circuit opens.
        /// See <see cref="WebhookAlertSink"/>; <paramref name="configure"/> picks the events, the Slack format,
        /// headers and timeouts.
        /// </summary>
        /// <param name="configuration">The global configuration.</param>
        /// <param name="url">The webhook URL.</param>
        /// <param name="configure">Optional changes to the defaults.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static IGlobalConfiguration UseWebhookAlerts(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] string url,
            [CanBeNull] Action<WebhookAlertOptions> configure = null)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (url == null) throw new ArgumentNullException(nameof(url));

            var options = new WebhookAlertOptions { Url = url };
            configure?.Invoke(options);

            return configuration.UseAlerts(new WebhookAlertSink(options));
        }

        /// <summary>
        /// Registers an alert sink and makes sure <see cref="AlertsFilter"/> is in the global filters, so
        /// <see cref="AlertKind.JobFailed"/> alerts are raised. See <see cref="Alerts"/>.
        /// </summary>
        /// <param name="configuration">The global configuration.</param>
        /// <param name="sink">The sink.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public static IGlobalConfiguration UseAlerts(
            [NotNull] this IGlobalConfiguration configuration,
            [NotNull] IAlertSink sink)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (sink == null) throw new ArgumentNullException(nameof(sink));

            Alerts.AddSink(sink);
            AddGlobalFilterOnce(static filter => filter is AlertsFilter, static () => new AlertsFilter());

            return configuration;
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