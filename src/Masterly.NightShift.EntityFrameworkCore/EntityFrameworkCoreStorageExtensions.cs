using Masterly.NightShift.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

// ReSharper disable once CheckNamespace
namespace Masterly.NightShift;

/// <summary>
/// Configures Masterly.NightShift to use Entity Framework Core:
/// <c>configuration.UseEntityFrameworkCoreStorage(o => o.UseSqlServer(connectionString))</c> in the
/// <c>AddNightShift</c> callback or on <see cref="GlobalConfiguration.Configuration"/>.
/// </summary>
#pragma warning disable CA1708 // False positive: the analyzer compares the types the compiler generates for extension blocks
public static class EntityFrameworkCoreStorageExtensions
#pragma warning restore CA1708
{
    /// <param name="configuration">The global configuration.</param>
    extension([NotNull] IGlobalConfiguration configuration)
    {
        /// <summary>
        /// Uses an <see cref="EntityFrameworkCoreStorage"/> with a context of its own, configured for a provider, as
        /// the current storage.
        /// </summary>
        /// <param name="configure">Configures the provider of the context, for example <c>o => o.UseNpgsql(connectionString)</c>.</param>
        /// <param name="options">The options, or <c>null</c> for the defaults.</param>
        /// <returns>The configuration, to chain further calls.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> or <paramref name="configure"/> is <c>null</c>.</exception>
        public IGlobalConfiguration<EntityFrameworkCoreStorage> UseEntityFrameworkCoreStorage(
            [NotNull] Action<DbContextOptionsBuilder> configure,
            [CanBeNull] EntityFrameworkCoreStorageOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(configure);
            return configuration.UseStorage(new EntityFrameworkCoreStorage(configure, options));
        }

        /// <summary>
        /// Uses an <see cref="EntityFrameworkCoreStorage"/> on the application's contexts as the current storage.
        /// Their model must include <c>ApplyNightShiftModel</c>; jobs can then be staged with
        /// <c>context.NightShift()</c>.
        /// </summary>
        /// <param name="contextFactory">Creates a new context each time it is called, for example <c>IDbContextFactory&lt;T&gt;.CreateDbContext</c>.</param>
        /// <param name="options">The options, or <c>null</c> for the defaults.</param>
        /// <returns>The configuration, to chain further calls.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> or <paramref name="contextFactory"/> is <c>null</c>.</exception>
        public IGlobalConfiguration<EntityFrameworkCoreStorage> UseEntityFrameworkCoreStorage(
            [NotNull] Func<DbContext> contextFactory,
            [CanBeNull] EntityFrameworkCoreStorageOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(contextFactory);
            return configuration.UseStorage(new EntityFrameworkCoreStorage(contextFactory, options));
        }
    }
}
