using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Masterly.NightShift;

/// <summary>
/// Registers <see cref="IScheduleDateProvider"/> implementations, the dates schedule policies exclude with
/// <see cref="SchedulePolicy.ExcludeDatesFrom"/>.
/// </summary>
public static class NightShiftScheduleDateProviderServiceCollectionExtensions
{
    /// <param name="services">The service collection.</param>
    extension([NotNull] IServiceCollection services)
    {
        /// <summary>
        /// Adds a date provider under a name, created once from the container (a singleton) when the Masterly.NightShift
        /// configuration is, and registered with <c>UseScheduleDateProvider</c>. Register it in every application that
        /// schedules or runs jobs following a policy that names it.
        /// </summary>
        /// <typeparam name="TProvider">The provider.</typeparam>
        /// <param name="name">The provider's name, as policies refer to it.</param>
        /// <returns>The service collection.</returns>
        /// <exception cref="ArgumentException">The name isn't a valid policy or provider name.</exception>
        public IServiceCollection AddNightShiftScheduleDateProvider<TProvider>([NotNull] string name)
            where TProvider : class, IScheduleDateProvider
        {
            ArgumentNullException.ThrowIfNull(services);

            ScheduleDateProviderRegistration registration = new(SchedulePolicy.NormalizeName(name), typeof(TProvider));

            services.TryAddSingleton<TProvider>();
            services.AddSingleton(registration);
            return services;
        }
    }
}

/// <summary>A date provider registered in the container, applied when the global configuration is created.</summary>
internal sealed record ScheduleDateProviderRegistration(string Name, Type ProviderType)
{
    /// <summary>Registers every date provider of the container with the global configuration.</summary>
    public static void Apply([NotNull] IServiceProvider services, [NotNull] IGlobalConfiguration configuration)
    {
        foreach (ScheduleDateProviderRegistration registration in services.GetServices<ScheduleDateProviderRegistration>())
        {
            configuration.UseScheduleDateProvider(
                registration.Name,
                (IScheduleDateProvider)services.GetRequiredService(registration.ProviderType));
        }
    }
}
