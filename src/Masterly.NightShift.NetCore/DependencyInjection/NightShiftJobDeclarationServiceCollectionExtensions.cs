using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Masterly.NightShift;

/// <summary>
/// Registers job classes and recurring jobs declared in code in a <see cref="IServiceCollection"/>.
/// </summary>
public static class NightShiftJobDeclarationServiceCollectionExtensions
{
    /// <param name="services">The service collection.</param>
    extension([NotNull] IServiceCollection services)
    {
        /// <summary>
        /// Adds a job class: checks it, registers it with <see cref="JobHandlers"/> for its arguments types, and
        /// in the container as a scoped service, unless it is already registered there.
        /// </summary>
        /// <typeparam name="THandler">A class implementing <see cref="IJob"/> or <see cref="IJobHandler{TArgs}"/>.</typeparam>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddNightShiftJobHandler<THandler>()
            where THandler : class
        {
            ArgumentNullException.ThrowIfNull(services);

            JobHandlers.Add<THandler>();
            services.TryAddScoped<THandler>();
            return services;
        }

        /// <summary>Adds every job class the given assemblies contain, as <c>AddNightShiftJobHandler</c> does.</summary>
        /// <param name="assemblies">The assemblies to search.</param>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddNightShiftJobHandlers([NotNull] params Assembly[] assemblies)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(assemblies);

            foreach (Type handlerType in assemblies.SelectMany(JobHandlers.FindHandlers))
            {
                JobHandlers.Add(handlerType);
                services.TryAddScoped(handlerType);
            }

            return services;
        }

        /// <summary>
        /// Declares recurring jobs, applied when the host starts. An invalid declaration, or a storage that
        /// can't be reached, stops the host from starting. Can be called several times; all the declarations
        /// form one set.
        /// </summary>
        /// <param name="declare">Adds the jobs to the set.</param>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddNightShiftRecurringJobs([NotNull] Action<RecurringJobSet> declare)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(declare);

            services.AddSingleton<IRecurringJobProvider>(new DelegateRecurringJobProvider(declare));
            AddApplyingService(services);
            return services;
        }

        /// <summary>
        /// Adds a provider of recurring jobs, created from the container and applied when the host starts.
        /// </summary>
        /// <typeparam name="TProvider">The provider.</typeparam>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddNightShiftRecurringJobs<TProvider>()
            where TProvider : class, IRecurringJobProvider
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IRecurringJobProvider, TProvider>());
            AddApplyingService(services);
            return services;
        }
    }

    private static void AddApplyingService(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RecurringJobSetHostedService>());
    }

    private sealed class DelegateRecurringJobProvider(Action<RecurringJobSet> declare) : IRecurringJobProvider
    {
        public void DeclareJobs(RecurringJobSet jobs)
        {
            declare(jobs);
        }
    }
}
