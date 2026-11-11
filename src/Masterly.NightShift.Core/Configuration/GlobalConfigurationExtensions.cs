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

using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Diagnostics;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Logging.LogProviders;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// The <c>Use*</c> and <c>Set*</c> methods that configure Masterly.NightShift through
/// <see cref="IGlobalConfiguration"/>: storage, serializer settings, filters, logging and the optional features.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
#pragma warning disable CA1708 // False positive: the analyzer compares the types the compiler generates for extension blocks
public static class GlobalConfigurationExtensions
#pragma warning restore CA1708
{
    extension([NotNull] IGlobalConfiguration configuration)
    {
        public IGlobalConfiguration<TStorage> UseStorage<TStorage>(
            [NotNull] TStorage storage)
            where TStorage : JobStorage
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(storage);

            return configuration.Use(storage, static x => JobStorage.Current = x);
        }
    }

    public static IGlobalConfiguration<TStorage> WithJobExpirationTimeout<TStorage>(
        [NotNull] this IGlobalConfiguration<TStorage> configuration,
        TimeSpan timeout)
        where TStorage : JobStorage
    {
        ArgumentNullException.ThrowIfNull(configuration);

        configuration.Entry.JobExpirationTimeout = timeout;
        return configuration;
    }

    /// <param name="configuration">The global configuration.</param>
    extension([NotNull] IGlobalConfiguration configuration)
    {
        /// <summary>
        /// Expires jobs that end up failed (their retries are used up) after <paramref name="retention"/>, so they
        /// don't pile up in storage and on the dashboard. The expiration is set in the same transaction as the Failed
        /// state; a job retried, rescheduled or deleted before then follows its new state. An
        /// <see cref="ExpireAfterAttribute"/> with <see cref="ExpireAfterAttribute.IncludeFailed"/> on a job's method
        /// or type wins over this. Without it failed jobs are kept until someone retries or deletes them. Calling it
        /// again changes the period; the filter is added only once.
        /// </summary>
        /// <param name="retention">How long a failed job is kept: positive, and at most <see cref="ExpireAfterAttribute.MaxDays"/> days.</param>
        /// <returns>The same configuration, for chaining.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="retention"/> is not positive, or it is too long.</exception>
        public IGlobalConfiguration UseFailedJobRetention(TimeSpan retention)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            FailedJobRetentionFilter.Validate(retention, nameof(retention));

            FailedJobRetentionFilter filter = (FailedJobRetentionFilter)AddGlobalFilterOnce(
                static registered => registered is FailedJobRetentionFilter,
                () => new FailedJobRetentionFilter(retention));

            filter.Retention = retention;
            return configuration;
        }

        public IGlobalConfiguration<TActivator> UseActivator<TActivator>(
            [NotNull] TActivator activator)
            where TActivator : JobActivator
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(activator);

            return configuration.Use(activator, static x => JobActivator.Current = x);
        }

        public IGlobalConfiguration<JobActivator> UseDefaultActivator(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return configuration.UseActivator(new JobActivator());
        }

        /// <summary>
        /// Sends Masterly.NightShift's log messages to <paramref name="provider"/>, including those of loggers
        /// created before this call.
        /// </summary>
        public IGlobalConfiguration<TLogProvider> UseLogProvider<TLogProvider>(
            [NotNull] TLogProvider provider)
            where TLogProvider : ILogProvider
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(provider);

            return configuration.Use(provider, static x => LateBoundLogProvider.Use(x));
        }

        /// <summary>
        /// Explicitly disables all the logging in Masterly.NightShift. Not recommended to use in a production
        /// application, because logging provides significant benefits in case of exceptions or other
        /// problems. But it can be useful for testing-related scenarios.
        /// </summary>
        public IGlobalConfiguration<ILogProvider> UseNoOpLogProvider(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);
            return configuration.UseLogProvider(LogProvider.NoOpLogProvider.Instance);
        }

        public IGlobalConfiguration<NLogLogProvider> UseNLogLogProvider(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return configuration.UseLogProvider(new NLogLogProvider());
        }

        public IGlobalConfiguration<ColouredConsoleLogProvider> UseColouredConsoleLogProvider(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return configuration.UseLogProvider(new ColouredConsoleLogProvider());
        }

        public IGlobalConfiguration<ColouredConsoleLogProvider> UseColouredConsoleLogProvider(
            LogLevel minLevel)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return configuration.UseLogProvider(new ColouredConsoleLogProvider(minLevel));
        }

        public IGlobalConfiguration<Log4NetLogProvider> UseLog4NetLogProvider(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return configuration.UseLogProvider(new Log4NetLogProvider());
        }

        public IGlobalConfiguration<ElmahLogProvider> UseElmahLogProvider(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return configuration.UseLogProvider(new ElmahLogProvider());
        }

        public IGlobalConfiguration<ElmahLogProvider> UseElmahLogProvider(
            LogLevel minLevel)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return configuration.UseLogProvider(new ElmahLogProvider(minLevel));
        }

        public IGlobalConfiguration<EntLibLogProvider> UseEntLibLogProvider(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return configuration.UseLogProvider(new EntLibLogProvider());
        }

        public IGlobalConfiguration<SerilogLogProvider> UseSerilogLogProvider(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return configuration.UseLogProvider(new SerilogLogProvider());
        }

        public IGlobalConfiguration<LoupeLogProvider> UseLoupeLogProvider(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return configuration.UseLogProvider(new LoupeLogProvider());
        }

        public IGlobalConfiguration<TFilter> UseFilter<TFilter>(
            [NotNull] TFilter filter)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(filter);

            return configuration.Use(filter, static x => GlobalJobFilters.Filters.Add(x!)); // Checked above
        }

        public IGlobalConfiguration<TFilterProvider> UseFilterProvider<TFilterProvider>(
            [NotNull] TFilterProvider filterProvider)
            where TFilterProvider : IJobFilterProvider
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(filterProvider);

            return configuration.Use(filterProvider, static x => JobFilterProviders.Providers.Add(x));
        }

        public IGlobalConfiguration UseDashboardMetric(
            [NotNull] DashboardMetric metric)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(metric);

            DashboardMetrics.AddMetric(metric);
            HomePage.Metrics.Add(metric);

            return configuration;
        }

        public IGlobalConfiguration UseDashboardMetrics(
            [NotNull] params DashboardMetric[] metrics)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(metrics);

            foreach (DashboardMetric metric in metrics)
            {
                DashboardMetrics.AddMetric(metric);
                HomePage.Metrics.Add(metric);
            }

            return configuration;
        }

        public IGlobalConfiguration UseJobDetailsRenderer(
            int order,
            [NotNull] Func<JobDetailsRendererDto, NonEscapedString> renderer)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(renderer);

            JobDetailsRenderer.AddRenderer(order, renderer);

            return configuration;
        }

        /// <summary>Sets the clock behind the timestamps the library writes, for example to control time in tests.</summary>
        /// <param name="timeProvider">The time source.</param>
        /// <returns>The configuration, to chain calls.</returns>
        public IGlobalConfiguration<TimeProvider> UseTimeProvider([NotNull] TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(timeProvider);

            return configuration.Use(timeProvider, static x => JobClock.TimeProvider = x);
        }

        public IGlobalConfiguration UseTypeResolver(
            [CanBeNull] Func<string, Type>? typeResolver)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            TypeHelper.CurrentTypeResolver = typeResolver!; // null restores the default
            return configuration;
        }

        public IGlobalConfiguration UseTypeSerializer(
            [CanBeNull] Func<Type, string>? typeSerializer)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            TypeHelper.CurrentTypeSerializer = typeSerializer!; // null restores the default
            return configuration;
        }

        public IGlobalConfiguration UseDefaultTypeResolver(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);
            return configuration.UseTypeResolver(null);
        }

        public IGlobalConfiguration UseDefaultTypeSerializer(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);
            return configuration.UseTypeSerializer(null);
        }

        public IGlobalConfiguration UseSimpleAssemblyNameTypeSerializer(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);
            return configuration.UseTypeSerializer(TypeHelper.SimpleAssemblyTypeSerializer);
        }

        public IGlobalConfiguration UseIgnoredAssemblyVersionTypeResolver(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);
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
        /// <param name="timeZoneResolver">The resolver, or <c>null</c> to restore <see cref="DefaultTimeZoneResolver"/>.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseTimeZoneResolver(
            [CanBeNull] ITimeZoneResolver? timeZoneResolver)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            GlobalTimeZoneResolver.Set(timeZoneResolver);
            return configuration;
        }

        /// <summary>
        /// Sets the time zone of recurring jobs registered without one (<see cref="RecurringJobOptions.DefaultTimeZone"/>),
        /// so CRON expressions written for local business hours don't each need a time zone. Recurring jobs already
        /// stored keep theirs until they are registered again. UTC unless this is called.
        /// </summary>
        /// <param name="timeZone">The time zone.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseDefaultRecurringJobTimeZone([NotNull] TimeZoneInfo timeZone)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(timeZone);

            RecurringJobOptions.DefaultTimeZone = timeZone;
            return configuration;
        }

        /// <summary>
        /// Fails a job for good once it was picked up again more than <paramref name="maxRestarts"/> times because its
        /// server stopped or died while it ran, instead of letting it loop through the cluster. A server stopping
        /// gracefully puts its running jobs back too, so set the limit above the number of deployments a long job may
        /// see. See <see cref="StaleJobFilter"/>.
        /// </summary>
        /// <param name="maxRestarts">The restarts allowed, 0 or more, or <c>null</c> for no limit (the default).</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseStaleRestartLimit(int? maxRestarts)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            StaleJobFilter.MaxRestarts = maxRestarts;
            return configuration;
        }

        /// <summary>
        /// Sets how long finished jobs are kept, per state: <c>UseJobRetention(o => { o.Succeeded = TimeSpan.FromHours(6); o.Deleted = TimeSpan.FromDays(7); })</c>.
        /// A per-method <see cref="ExpireAfterAttribute"/> wins. Calling it again replaces the retention.
        /// </summary>
        /// <param name="configure">Sets the retention of each state.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseJobRetention([NotNull] Action<JobRetentionOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(configure);

            JobRetentionOptions options = new();
            configure(options);

            JobRetentionFilter filter = (JobRetentionFilter)AddGlobalFilterOnce(
                static registered => registered is JobRetentionFilter,
                () => new JobRetentionFilter(options.States));
            filter.Update(options.States);

            if (options.Failed is { } failed)
            {
                configuration.UseFailedJobRetention(failed);
            }

            return configuration;
        }

        /// <summary>
        /// Compresses the stored arguments of new jobs that are at least <paramref name="minLength"/> characters long,
        /// as GZip in base64 behind the marker <c>gz:</c>. Compressed arguments are always read back, so it can be turned
        /// off later; servers of earlier versions can't read them, so turn it on once every server runs this version.
        /// </summary>
        /// <param name="minLength">
        /// The length from which arguments are compressed, 1 or more, 1,024 by default; <c>null</c> turns compression
        /// off (compressed arguments are still read).
        /// </param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseArgumentCompression(int? minLength = 1024)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            ArgumentCompression.MinLength = minLength;
            return configuration;
        }

        /// <summary>
        /// Turns on <see cref="DeploymentFencing"/>: servers created afterwards record their deployment, and servers of
        /// an older deployment stop taking jobs once a newer one runs.
        /// </summary>
        /// <param name="deploymentId">
        /// The deployment, up to 64 characters, different for every release; <c>null</c> for the entry assembly's
        /// informational version (or version).
        /// </param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseDeploymentFencing([CanBeNull] string? deploymentId = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            DeploymentFencing.DeploymentId = deploymentId == null
                ? DeploymentFencing.GetDefaultDeploymentId()
                : DeploymentFencing.NormalizeDeploymentId(deploymentId, nameof(deploymentId));
            return configuration;
        }

        /// <summary>
        /// Registers a provider of dates that schedule policies exclude with <see cref="SchedulePolicy.ExcludeDatesFrom"/>,
        /// such as public holidays, under a name. Register it in every process that schedules or runs jobs following
        /// such a policy: where it is missing, the policy allows no instant. Registering another provider under the
        /// same name replaces it.
        /// </summary>
        /// <param name="name">The provider's name, as policies refer to it.</param>
        /// <param name="provider">The provider.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseScheduleDateProvider([NotNull] string name, [NotNull] IScheduleDateProvider provider)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(provider);

            ScheduleDateProviders.Set(name, provider);
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
        public IGlobalConfiguration UseSerializerSettings(
            [CanBeNull] JsonSerializerOptions? settings)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            SerializationHelper.SetUserSerializerSettings(settings);
            return configuration;
        }

        /// <summary>
        /// Serializes user data with the same System.Text.Json options as internal data.
        /// </summary>
        public IGlobalConfiguration UseRecommendedSerializerSettings(
    )
        {
            return UseRecommendedSerializerSettings(configuration, null);
        }

        /// <summary>
        /// Serializes user data with a copy of the options used for internal data, changed by
        /// <paramref name="settingsConfiguration"/>. Converters it adds win over the built-in ones for the same type.
        /// </summary>
        public IGlobalConfiguration UseRecommendedSerializerSettings(
            [CanBeNull] Action<JsonSerializerOptions>? settingsConfiguration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            SerializationHelper.SetRecommendedSerializerSettings(settingsConfiguration);
            return configuration;
        }
    }

    extension(IGlobalConfiguration configuration)
    {
        public IGlobalConfiguration UseResultsInContinuations()
        {
            AddGlobalFilterOnce(static filter => filter is JobParameterInjectionFilter, static () => new JobParameterInjectionFilter());
            ContinuationsSupportAttribute continuations = (ContinuationsSupportAttribute)AddGlobalFilterOnce(
                static filter => filter is ContinuationsSupportAttribute registered && registered.PushResults,
                static () => new ContinuationsSupportAttribute(pushResults: true));

            return configuration.Use(continuations, static _ => { });
        }
    }

    private static readonly NightShiftLock GlobalFiltersLock = new();

    // Adds a filter that Use* methods add only once: a process that configures NightShift again (several hosts
    // in one test run, for example) doesn't pile up copies in the global filters. Returns the registered filter.
    private static object AddGlobalFilterOnce(Func<object, bool> isRegistered, Func<object> create)
    {
        lock (GlobalFiltersLock)
        {
            foreach (JobFilter filter in GlobalJobFilters.Filters)
            {
                if (isRegistered(filter.Instance)) return filter.Instance;
            }

            object created = create();
            GlobalJobFilters.Filters.Add(created);
            return created;
        }
    }

    /// <param name="configuration">The global configuration.</param>
    extension([NotNull] IGlobalConfiguration configuration)
    {
        public IGlobalConfiguration UseMaxLinesInExceptionDetails(
            [CanBeNull] int? numberOfLines)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            if (numberOfLines.HasValue && numberOfLines.Value <= 1) throw new ArgumentOutOfRangeException(nameof(numberOfLines), "Must be either a positive value larger than 1; or a null value.");

            States.FailedState.MaxLinesInExceptionDetails = numberOfLines;
            return configuration;
        }

        public IGlobalConfiguration UseMaxArgumentSizeToRender(
            int size)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentOutOfRangeException.ThrowIfNegative(size);

            JobMethodCallRenderer.MaxArgumentToRenderSize = size;
            return configuration;
        }

        public IGlobalConfiguration UseDefaultCulture(
            [CanBeNull] CultureInfo? culture)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            return configuration.UseFilter(new CaptureCultureAttribute(culture?.Name));
        }

        public IGlobalConfiguration UseDefaultCulture(
            [CanBeNull] CultureInfo? culture,
            bool captureDefault)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            return configuration.UseFilter(new CaptureCultureAttribute(culture?.Name, captureDefault));
        }

        public IGlobalConfiguration UseDefaultCulture(
            [CanBeNull] CultureInfo? culture,
            [CanBeNull] CultureInfo? uiCulture)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            return configuration.UseFilter(new CaptureCultureAttribute(culture?.Name, uiCulture?.Name));
        }

        public IGlobalConfiguration UseDefaultCulture(
            [CanBeNull] CultureInfo? culture,
            [CanBeNull] CultureInfo? uiCulture,
            bool captureDefault)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            return configuration.UseFilter(new CaptureCultureAttribute(culture?.Name, uiCulture?.Name, captureDefault));
        }

        /// <summary>
        /// Emits OpenTelemetry-compatible traces and metrics for background jobs through the
        /// <see cref="NightShiftTelemetry.ActivitySourceName"/> activity source and the
        /// <see cref="NightShiftTelemetry.MeterName"/> meter. The trace context of the code that
        /// creates a job is stored in the <c>TraceParent</c> job parameter, so the span of the
        /// job's execution joins the same trace. Nothing is recorded unless a listener is attached.
        /// </summary>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseTelemetry()
        {
            ArgumentNullException.ThrowIfNull(configuration);

            TelemetryFilter telemetry = (Diagnostics.TelemetryFilter)AddGlobalFilterOnce(static filter => filter is Diagnostics.TelemetryFilter, static () => new Diagnostics.TelemetryFilter());
            return configuration.Use(telemetry, static _ => { });
        }


        /// <summary>
        /// Adds an interceptor that runs around every background job method, before the interceptors declared
        /// with attributes on the job's type and method. See <see cref="IJobInterceptor"/>.
        /// </summary>
        /// <param name="interceptor">The interceptor.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseJobInterceptor(
            [NotNull] IJobInterceptor interceptor)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(interceptor);

            GlobalJobInterceptors.Add(interceptor);
            return configuration;
        }

        /// <summary>
        /// Records per-method run counts, failure counts and durations for the dashboard's Insights page.
        /// See <see cref="JobInsightsAttribute"/>. Calling it again adds nothing.
        /// </summary>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseJobInsights()
        {
            ArgumentNullException.ThrowIfNull(configuration);

            AddGlobalFilterOnce(static filter => filter is JobInsightsAttribute, static () => new JobInsightsAttribute());
            QueueInsightsFilter queueInsights = (QueueInsightsFilter)AddGlobalFilterOnce(static filter => filter is QueueInsightsFilter, static () => new QueueInsightsFilter());
            return configuration.Use(queueInsights, static _ => { });
        }

        /// <summary>
        /// Turns on the <see cref="ActivityFeed"/>: every state transition is recorded in the storage list
        /// <c>activity</c>, which feeds the dashboard's live activity and Timeline. Call it in every process that
        /// changes job states (servers, and apps that create jobs). Entries are written in the background after each
        /// state change commits, so the feed can't slow down or fail a state change, but it still costs a storage
        /// write per batch of transitions.
        /// </summary>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseActivityFeed()
        {
            ArgumentNullException.ThrowIfNull(configuration);

            AddGlobalFilterOnce(static filter => filter is ActivityFeedFilter, static () => new ActivityFeedFilter());

            ActivityFeed.Enabled = true;
            return configuration;
        }

        /// <summary>
        /// Turns on the <see cref="ActivityFeed"/> and sets how many entries it keeps. See
        /// <see cref="UseActivityFeed(IGlobalConfiguration)"/>.
        /// </summary>
        /// <param name="maxEntries">How many entries the list keeps, see <see cref="ActivityFeed.MaxEntries"/>.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseActivityFeed(int maxEntries)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);

            ActivityFeed.MaxEntries = maxEntries;
            return configuration.UseActivityFeed();
        }

        /// <summary>
        /// Posts an alert to <paramref name="url"/> when a job finally fails, a deadline is missed or a circuit opens.
        /// See <see cref="WebhookAlertSink"/>; <paramref name="configure"/> picks the events, the Slack format,
        /// headers and timeouts.
        /// </summary>
        /// <param name="url">The webhook URL.</param>
        /// <param name="configure">Optional changes to the defaults.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseWebhookAlerts(
            [NotNull] string url,
            [CanBeNull] Action<WebhookAlertOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(url);

            WebhookAlertOptions options = new() { Url = url };
            configure?.Invoke(options);

            return configuration.UseAlerts(new WebhookAlertSink(options));
        }

        /// <summary>
        /// Registers a <see cref="JobMonitor"/>: a daily deadline, a long-running limit or a repeated-failures limit for
        /// the jobs of one method. Servers created afterwards check the monitors every <see cref="JobMonitors.CheckInterval"/>
        /// and raise <see cref="AlertKind.MonitorIncident"/> and <see cref="AlertKind.MonitorRecovered"/> alerts.
        /// Register the same monitors in every process that runs jobs, so their outcomes are recorded.
        /// </summary>
        /// <param name="monitor">The monitor.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseJobMonitor([NotNull] JobMonitor monitor)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(monitor);

            JobMonitors.Add(monitor);
            AddGlobalFilterOnce(static filter => filter is JobMonitorFilter, static () => new JobMonitorFilter());
            return configuration;
        }

        /// <summary>
        /// Registers an alert sink that receives only the alerts <paramref name="rule"/> lets through: their kinds, the
        /// job methods they are about, a cooldown. See <see cref="AlertRule"/>.
        /// </summary>
        /// <param name="sink">The sink.</param>
        /// <param name="rule">Sets the rule.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseAlerts(
            [NotNull] IAlertSink sink,
            [NotNull] Action<AlertRule> rule)
        {
            ArgumentNullException.ThrowIfNull(sink);
            ArgumentNullException.ThrowIfNull(rule);

            AlertRule alertRule = new();
            rule(alertRule);
            return configuration.UseAlerts(new AlertRuleSink(sink, alertRule));
        }

        /// <summary>
        /// Registers an alert sink and makes sure <see cref="AlertsFilter"/> is in the global filters, so
        /// <see cref="AlertKind.JobFailed"/> alerts are raised. See <see cref="Alerts"/>.
        /// </summary>
        /// <param name="sink">The sink.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseAlerts(
            [NotNull] IAlertSink sink)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(sink);

            Alerts.AddSink(sink);
            AddGlobalFilterOnce(static filter => filter is AlertsFilter, static () => new AlertsFilter());

            return configuration;
        }

        /// <summary>
        /// Calls <paramref name="handler"/> for the exceptions of every background job: each failed attempt, and each
        /// canceled run. See <see cref="IJobExceptionHandler"/>. Each call adds a handler.
        /// </summary>
        /// <param name="handler">The handler.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseJobExceptionHandler([NotNull] IJobExceptionHandler handler)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(handler);

            GlobalJobFilters.Filters.Add(new JobExceptionHandlerFilter(handler));
            return configuration;
        }

        /// <summary>
        /// Calls <paramref name="onException"/> for each failed attempt of every background job, and
        /// <paramref name="onCanceled"/>, when given, for each canceled run. See <see cref="IJobExceptionHandler"/>.
        /// </summary>
        /// <param name="onException">Called for each failed attempt.</param>
        /// <param name="onCanceled">Called for each canceled run, or <c>null</c> to ignore them.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseJobExceptionHandler(
            [NotNull] Action<JobExceptionContext> onException,
            [CanBeNull] Action<JobExceptionContext>? onCanceled = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(onException);

            return configuration.UseJobExceptionHandler(new DelegateJobExceptionHandler(onException, onCanceled));
        }

        /// <summary>
        /// Chooses whether job methods without a generated invoker are called through a delegate compiled
        /// once per method (the default) or through reflection. See <see cref="JobInvokerRegistry"/>.
        /// </summary>
        /// <param name="enabled"><c>false</c> to use reflection for every method without a generated invoker.</param>
        /// <returns>The same configuration, for chaining.</returns>
        public IGlobalConfiguration UseCompiledJobInvokers(
            bool enabled)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            JobInvokerRegistry.UseCompiledInvokers = enabled;
            return configuration;
        }

        public IGlobalConfiguration UseDashboardStylesheet(
            [NotNull] Assembly assembly,
            [NotNull] string resource)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(assembly);
            ArgumentNullException.ThrowIfNull(resource);

            DashboardRoutes.AddStylesheet(assembly, resource);
            return configuration;
        }

        public IGlobalConfiguration UseDashboardStylesheetDarkMode(
            [NotNull] Assembly assembly,
            [NotNull] string resource)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(assembly);
            ArgumentNullException.ThrowIfNull(resource);

            DashboardRoutes.AddStylesheetDarkMode(assembly, resource);
            return configuration;
        }

        public IGlobalConfiguration UseDashboardJavaScript(
            [NotNull] Assembly assembly,
            [NotNull] string resource)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(assembly);
            ArgumentNullException.ThrowIfNull(resource);

            DashboardRoutes.AddJavaScript(assembly, resource);
            return configuration;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public IGlobalConfiguration<T> Use<T>(
            T entry,
            [NotNull] Action<T> entryAction)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            entryAction(entry);

            return new ConfigurationEntry<T>(entry);
        }
    }

    private sealed class ConfigurationEntry<T>(T entry) : IGlobalConfiguration<T>
    {
        public T Entry { get; } = entry;
    }
}
