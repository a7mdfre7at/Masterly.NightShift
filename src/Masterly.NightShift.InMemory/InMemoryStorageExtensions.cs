using Masterly.NightShift.InMemory;

// ReSharper disable once CheckNamespace
namespace Masterly.NightShift;

/// <summary>
/// Configures Masterly.NightShift to keep its jobs in memory: <c>configuration.UseInMemoryStorage()</c> in the
/// <c>AddNightShift</c> callback or on <see cref="GlobalConfiguration.Configuration"/>.
/// </summary>
public static class InMemoryStorageExtensions
{
    /// <param name="configuration">The global configuration.</param>
    extension([NotNull] IGlobalConfiguration configuration)
    {
        /// <summary>Uses a new <see cref="InMemoryStorage"/> with the default options as the current storage.</summary>
        /// <returns>The configuration, to chain further calls.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <c>null</c>.</exception>
        public IGlobalConfiguration<InMemoryStorage> UseInMemoryStorage()
        {
            ArgumentNullException.ThrowIfNull(configuration);
            return configuration.UseStorage(new InMemoryStorage());
        }

        /// <summary>Uses a new <see cref="InMemoryStorage"/> with the given options as the current storage.</summary>
        /// <param name="options">The storage options.</param>
        /// <returns>The configuration, to chain further calls.</returns>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        public IGlobalConfiguration<InMemoryStorage> UseInMemoryStorage([NotNull] InMemoryStorageOptions options)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(options);
            return configuration.UseStorage(new InMemoryStorage(options));
        }
    }
}
