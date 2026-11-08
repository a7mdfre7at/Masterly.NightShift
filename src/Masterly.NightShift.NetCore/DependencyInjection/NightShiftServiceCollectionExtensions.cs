// This file is part of Hangfire. Copyright © 2016 Hangfire OÜ.
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

#nullable enable

using System;
using System.Collections.Generic;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.AspNetCore;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Masterly.NightShift
{
    /// <summary>
    /// Registers Masterly.NightShift in a <see cref="IServiceCollection"/>: <c>AddNightShift</c> adds the
    /// configuration, the storage, the job client and the recurring job manager, and <c>AddNightShiftServer</c>
    /// adds a background job server that starts and stops with the host.
    /// </summary>
    public static class NightShiftServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the Masterly.NightShift services: <see cref="IGlobalConfiguration"/>, <see cref="JobStorage"/>,
        /// <see cref="JobActivator"/>, <see cref="IBackgroundJobClient"/>, <see cref="IRecurringJobManager"/>
        /// and the factories behind them. <paramref name="configuration"/> runs once, the first time
        /// <see cref="IGlobalConfiguration"/> is resolved (resolving the storage, the client, the recurring
        /// job manager or a server does that), after the container's logger factory and service scope factory
        /// were installed as the log provider and the job activator, so it can replace them.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">Configures Masterly.NightShift, for example with <c>UseSqlServerStorage</c>.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddNightShift(
            [NotNull] this IServiceCollection services,
            [NotNull] Action<IGlobalConfiguration> configuration)
        {
            return AddNightShift(services, (provider, config) => configuration(config));
        }

        /// <summary>
        /// Adds the Masterly.NightShift services, like <see cref="AddNightShift(IServiceCollection, Action{IGlobalConfiguration})"/>,
        /// with a callback that also receives the <see cref="IServiceProvider"/>.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">Configures Masterly.NightShift, with access to the registered services.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddNightShift(
            [NotNull] this IServiceCollection services,
            [NotNull] Action<IServiceProvider, IGlobalConfiguration> configuration)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            services.TryAddSingletonChecked(static _ => JobStorage.Current);
            services.TryAddSingletonChecked(static _ => JobActivator.Current);

            services.TryAddSingleton(static _ => DashboardRoutes.Routes);
            services.TryAddSingleton<IJobFilterProvider>(static _ => JobFilterProviders.Providers);
            services.TryAddSingleton<ITimeZoneResolver>(static _ => new DefaultTimeZoneResolver());
            
            services.TryAddSingleton(static x => new DefaultClientManagerFactory(x));
            services.TryAddSingletonChecked<IBackgroundJobClientFactory>(static x => x.GetRequiredService<DefaultClientManagerFactory>());
            services.TryAddSingletonChecked<IBackgroundJobClientFactoryV2>(static x => x.GetRequiredService<DefaultClientManagerFactory>());
            services.TryAddSingletonChecked<IRecurringJobManagerFactory>(static x => x.GetRequiredService<DefaultClientManagerFactory>());
            services.TryAddSingletonChecked<IRecurringJobManagerFactoryV2>(static x => x.GetRequiredService<DefaultClientManagerFactory>());
            services.TryAddSingletonChecked<IRecurringJobManagerFactoryV3>(static x => x.GetRequiredService<DefaultClientManagerFactory>());

            services.TryAddSingletonChecked(static x => x
                .GetRequiredService<IBackgroundJobClientFactory>().GetClient(x.GetRequiredService<JobStorage>()));
            services.TryAddSingletonChecked(static x => x
                .GetRequiredService<IBackgroundJobClientFactoryV2>().GetClientV2(x.GetRequiredService<JobStorage>()));

            services.TryAddSingletonChecked(static x => x
                .GetRequiredService<IRecurringJobManagerFactory>().GetManager(x.GetRequiredService<JobStorage>()));
            services.TryAddSingletonChecked(static x => x
                .GetRequiredService<IRecurringJobManagerFactoryV2>().GetManagerV2(x.GetRequiredService<JobStorage>()));
            services.TryAddSingletonChecked(static x => x
                .GetRequiredService<IRecurringJobManagerFactoryV3>().GetManagerV3(x.GetRequiredService<JobStorage>()));

            // IGlobalConfiguration serves as a marker indicating that Masterly.NightShift's services
            // were added to the service container (checked by IApplicationBuilder extensions).
            //
            // Being a singleton, it also guarantees that the configuration callback will be
            // executed just once upon initialization, so there's no need to double-check that.
            //
            // It should never be replaced by another implementation !!!
            // AddSingleton() doesn't throw when it is already registered: a second AddNightShift call adds
            // another registration, the last one is resolved, and only its configuration callback runs.

            services.AddSingleton<IGlobalConfiguration>(serviceProvider =>
            {
                var configurationInstance = GlobalConfiguration.Configuration;

                // init defaults for log provider and job activator
                // they may be overwritten by the configuration callback later

                var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
                if (loggerFactory != null)
                {
                    configurationInstance.UseLogProvider(new AspNetCoreLogProvider(loggerFactory));
                }

                var scopeFactory = serviceProvider.GetService<IServiceScopeFactory>();
                if (scopeFactory != null)
                {
                    configurationInstance.UseActivator(new AspNetCoreJobActivator(scopeFactory));
                }

                // a resolver registered in the container also serves the static RecurringJob class and anything
                // else created without one; the default registered above leaves the global resolver as it is
                try
                {
                    var timeZoneResolver = serviceProvider.GetService<ITimeZoneResolver>();
                    if (timeZoneResolver != null && timeZoneResolver.GetType() != typeof(DefaultTimeZoneResolver))
                    {
                        configurationInstance.UseTimeZoneResolver(timeZoneResolver);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    // for example a resolver that depends on the storage or the job client, or a scoped one
                    LogProvider.GetLogger(typeof(NightShiftServiceCollectionExtensions)).WarnException(
                        "The registered ITimeZoneResolver couldn't be created while configuring Masterly.NightShift, so the static RecurringJob class keeps the global resolver. Call UseTimeZoneResolver in the AddNightShift callback to set it.",
                        ex);
                }

                // do configuration inside callback

                configuration(serviceProvider, configurationInstance);
                
                return configurationInstance;
            });
            
            return services;
        }

        /// <summary>
        /// Adds a background job server, run as an <see cref="IHostedService"/>, whose options are set by
        /// <paramref name="optionsAction"/> when the server is created. The storage, the activator, the filter
        /// provider and the time zone resolver the options leave unset are taken from the container.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="optionsAction">Sets the server options.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddNightShiftServer(
            [NotNull] this IServiceCollection services,
            [NotNull] Action<BackgroundJobServerOptions> optionsAction)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (optionsAction == null) throw new ArgumentNullException(nameof(optionsAction));

            return AddNightShiftServerInner(services, null, null, (provider, options) => optionsAction(options));
        }

        /// <summary>
        /// Adds a background job server, run as an <see cref="IHostedService"/>, whose options are set by
        /// <paramref name="optionsAction"/>, which also receives the <see cref="IServiceProvider"/>.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="optionsAction">Sets the server options.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddNightShiftServer(
            [NotNull] this IServiceCollection services,
            [NotNull] Action<IServiceProvider, BackgroundJobServerOptions> optionsAction)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (optionsAction == null) throw new ArgumentNullException(nameof(optionsAction));

            return AddNightShiftServerInner(services, null, null, optionsAction);
        }

        /// <summary>
        /// Adds a background job server for the given storage, run as an <see cref="IHostedService"/>, whose
        /// options are set by <paramref name="optionsAction"/>.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="optionsAction">Sets the server options.</param>
        /// <param name="storage">The storage the server processes jobs from.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddNightShiftServer(
            [NotNull] this IServiceCollection services,
            [NotNull] Action<IServiceProvider, BackgroundJobServerOptions> optionsAction,
            [NotNull] JobStorage storage)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (optionsAction == null) throw new ArgumentNullException(nameof(optionsAction));
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            return AddNightShiftServerInner(services, storage, null, optionsAction);
        }

        /// <summary>
        /// Adds a background job server for the given storage, with extra background processes, run as an
        /// <see cref="IHostedService"/>, whose options are set by <paramref name="optionsAction"/>.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="optionsAction">Sets the server options.</param>
        /// <param name="storage">The storage the server processes jobs from.</param>
        /// <param name="additionalProcesses">Processes the server runs besides its own.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddNightShiftServer(
            [NotNull] this IServiceCollection services,
            [NotNull] Action<IServiceProvider, BackgroundJobServerOptions> optionsAction,
            [NotNull] JobStorage storage,
            [NotNull] IEnumerable<IBackgroundProcess> additionalProcesses)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (optionsAction == null) throw new ArgumentNullException(nameof(optionsAction));
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (additionalProcesses == null) throw new ArgumentNullException(nameof(additionalProcesses));

            return AddNightShiftServerInner(services, storage, additionalProcesses, optionsAction);
        }

        /// <summary>
        /// Adds a background job server whose <see cref="BackgroundJobServerOptions"/> are read from a configuration
        /// section, for example <c>builder.Configuration.GetSection("NightShift:Server")</c>. The keys are the option
        /// names: <c>ServerName</c>, <c>WorkerCount</c>, <c>Queues</c> (an array, or a comma-separated list),
        /// <c>IsLightweightServer</c>, <c>MaxDegreeOfParallelismForSchedulers</c>, and the time spans
        /// <c>StopTimeout</c>, <c>ShutdownTimeout</c>, <c>SchedulePollingInterval</c>, <c>HeartbeatInterval</c>,
        /// <c>ServerCheckInterval</c>, <c>ServerTimeout</c> and <c>CancellationCheckInterval</c>
        /// (<c>"00:00:15"</c>). A missing key keeps the default. The section is read when the server starts.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The configuration section holding the options.</param>
        public static IServiceCollection AddNightShiftServer(
            [NotNull] this IServiceCollection services,
            [NotNull] IConfiguration configuration)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            return AddNightShiftServerInner(services, null, null, (provider, options) => BackgroundJobServerOptionsBinder.Bind(configuration, options));
        }

        /// <summary>
        /// Adds a background job server whose <see cref="BackgroundJobServerOptions"/> are read from a configuration
        /// section, as <see cref="AddNightShiftServer(IServiceCollection, IConfiguration)"/> does, then passed to
        /// <paramref name="optionsAction"/> for the settings configuration can't hold (an activator, a filter
        /// provider, a task scheduler).
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The configuration section holding the options.</param>
        /// <param name="optionsAction">Changes the options after they are read from <paramref name="configuration"/>.</param>
        public static IServiceCollection AddNightShiftServer(
            [NotNull] this IServiceCollection services,
            [NotNull] IConfiguration configuration,
            [NotNull] Action<IServiceProvider, BackgroundJobServerOptions> optionsAction)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (optionsAction == null) throw new ArgumentNullException(nameof(optionsAction));

            return AddNightShiftServerInner(services, null, null, (provider, options) =>
            {
                BackgroundJobServerOptionsBinder.Bind(configuration, options);
                optionsAction(provider, options);
            });
        }

        /// <summary>
        /// Adds a background job server, run as an <see cref="IHostedService"/>. It uses the
        /// <see cref="BackgroundJobServerOptions"/> registered in the container, or the defaults, and the storage,
        /// background processes, activator, filter provider and time zone resolver registered there.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddNightShiftServer([NotNull] this IServiceCollection services)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            return AddNightShiftServerInner(services, null, null);
        }

        /// <summary>
        /// Adds a background job server for the given storage, run as an <see cref="IHostedService"/>, with the
        /// <see cref="BackgroundJobServerOptions"/> registered in the container, or the defaults.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="storage">The storage the server processes jobs from.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddNightShiftServer(
            [NotNull] this IServiceCollection services,
            [NotNull] JobStorage storage)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            return AddNightShiftServerInner(services, storage, null);
        }

        /// <summary>
        /// Adds a background job server for the given storage, with extra background processes, run as an
        /// <see cref="IHostedService"/>, with the <see cref="BackgroundJobServerOptions"/> registered in the
        /// container, or the defaults.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="storage">The storage the server processes jobs from.</param>
        /// <param name="additionalProcesses">Processes the server runs besides its own.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddNightShiftServer(
            [NotNull] this IServiceCollection services,
            [NotNull] JobStorage storage,
            [NotNull] IEnumerable<IBackgroundProcess> additionalProcesses)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (additionalProcesses == null) throw new ArgumentNullException(nameof(additionalProcesses));

            return AddNightShiftServerInner(services, storage, additionalProcesses);
        }

        private static IServiceCollection AddNightShiftServerInner(
            [NotNull] IServiceCollection services,
            [CanBeNull] JobStorage? storage,
            [CanBeNull] IEnumerable<IBackgroundProcess>? additionalProcesses)
        {
            services.AddTransient<IHostedService, BackgroundJobServerHostedService>(provider =>
            {
                var options = provider.GetService<BackgroundJobServerOptions>() ?? new BackgroundJobServerOptions();
                return CreateBackgroundJobServerHostedService(provider, storage, additionalProcesses, options);
            });

            return services;
        }

        private static IServiceCollection AddNightShiftServerInner(
            [NotNull] IServiceCollection services,
            [CanBeNull] JobStorage? storage,
            [CanBeNull] IEnumerable<IBackgroundProcess>? additionalProcesses,
            [NotNull] Action<IServiceProvider, BackgroundJobServerOptions> optionsAction)
        {
            services.AddTransient<IHostedService, BackgroundJobServerHostedService>(provider =>
            {
                var options = new BackgroundJobServerOptions();
                optionsAction(provider, options);

                return CreateBackgroundJobServerHostedService(provider, storage, additionalProcesses, options);
            });

            return services;
        }

        /// <summary>
        /// Adds a background processing server created by <paramref name="implementationFactory"/>, run as an
        /// <see cref="IHostedService"/> that stops it when the host stops.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="implementationFactory">Creates the server.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddNightShiftServer(
            [NotNull] this IServiceCollection services,
            [NotNull] Func<IServiceProvider, IBackgroundProcessingServer> implementationFactory)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (implementationFactory == null) throw new ArgumentNullException(nameof(implementationFactory));

            services.AddTransient<IHostedService, BackgroundProcessingServerHostedService>(
                provider => new BackgroundProcessingServerHostedService(
                    implementationFactory(provider),
                    provider.GetService<IHostApplicationLifetime>()));

            return services;
        }

        private static BackgroundJobServerHostedService CreateBackgroundJobServerHostedService(
            IServiceProvider provider,
            JobStorage? storage,
            IEnumerable<IBackgroundProcess>? additionalProcesses,
            BackgroundJobServerOptions options)
        {
            ThrowIfNotConfigured(provider);

            storage = storage ?? provider.GetService<JobStorage>() ?? JobStorage.Current;
            additionalProcesses = additionalProcesses ?? provider.GetServices<IBackgroundProcess>();

            options.Activator = options.Activator ?? provider.GetService<JobActivator>();
            options.FilterProvider = options.FilterProvider ?? provider.GetService<IJobFilterProvider>();
            options.FilterProvider = JobLoggingScope.AddTo(options.FilterProvider, provider);
            options.TimeZoneResolver = options.TimeZoneResolver ?? provider.GetService<ITimeZoneResolver>();

            GetInternalServices(provider, out var factory, out var stateChanger, out var performer);

            // Hosts that don't register it (the ASP.NET Core 2.x web host) start the server at once, as before
            var lifetime = provider.GetService<IHostApplicationLifetime>();

#pragma warning disable 618
            return new BackgroundJobServerHostedService(
#pragma warning restore 618
                storage, options, additionalProcesses, factory, performer, stateChanger, lifetime);
        }

        /// <summary>
        /// Gets the <see cref="IBackgroundJobFactory"/>, <see cref="IBackgroundJobStateChanger"/> and
        /// <see cref="IBackgroundJobPerformer"/> registered in the container. Used by the server and client
        /// registrations of this package and <c>Masterly.NightShift.AspNetCore</c>.
        /// It is public, rather than internal, because <c>Masterly.NightShift.AspNetCore</c> is a separate package
        /// that calls it; application code doesn't need it.
        /// </summary>
        /// <param name="provider">The service provider.</param>
        /// <param name="factory">The registered job factory, or <c>null</c>.</param>
        /// <param name="stateChanger">The registered state changer, or <c>null</c>.</param>
        /// <param name="performer">The registered job performer, or <c>null</c>.</param>
        /// <returns><c>true</c> when all three are registered; otherwise <c>false</c>, and all three are <c>null</c>.</returns>
        public static bool GetInternalServices(
            [NotNull] IServiceProvider provider,
            out IBackgroundJobFactory? factory,
            out IBackgroundJobStateChanger? stateChanger,
            out IBackgroundJobPerformer? performer)
        {
            factory = provider.GetService<IBackgroundJobFactory>();
            performer = provider.GetService<IBackgroundJobPerformer>();
            stateChanger = provider.GetService<IBackgroundJobStateChanger>();

            if (factory != null && performer != null && stateChanger != null)
            {
                return true;
            }

            factory = null;
            performer = null;
            stateChanger = null;

            return false;
        }

        private static void TryAddSingletonChecked<T>(
            [NotNull] this IServiceCollection serviceCollection, 
            [NotNull] Func<IServiceProvider, T> implementationFactory)
            where T : class
        {
            serviceCollection.TryAddSingleton<T>(serviceProvider =>
            {
                if (serviceProvider == null) throw new ArgumentNullException(nameof(serviceProvider));

                // ensure the configuration was performed
                serviceProvider.GetRequiredService<IGlobalConfiguration>();

                return implementationFactory(serviceProvider);
            });
        }

        /// <summary>
        /// Throws when <c>AddNightShift</c> wasn't called on the service collection of <paramref name="serviceProvider"/>.
        /// It is public, rather than internal, because <c>Masterly.NightShift.AspNetCore</c> is a separate package
        /// that calls it; application code doesn't need it.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <exception cref="InvalidOperationException">The Masterly.NightShift services weren't added.</exception>
        public static void ThrowIfNotConfigured([NotNull] IServiceProvider serviceProvider)
        {
            var configuration = serviceProvider.GetService<IGlobalConfiguration>();
            if (configuration == null)
            {
                throw new InvalidOperationException(
                    "Unable to find the required services. Please add all the required services by calling 'IServiceCollection.AddNightShift' inside the call to 'ConfigureServices(...)' in the application startup code.");
            }
        }
    }
}
