using Masterly.NightShift.Redis;
using StackExchange.Redis;

// ReSharper disable once CheckNamespace
namespace Masterly.NightShift;

/// <summary>
/// Configures Masterly.NightShift to use Redis: <c>configuration.UseRedisStorage("localhost:6379")</c> in the
/// <c>AddNightShift</c> callback or on <see cref="GlobalConfiguration.Configuration"/>.
/// </summary>
#pragma warning disable CA1708 // False positive: the analyzer compares the types the compiler generates for extension blocks
public static class RedisStorageExtensions
#pragma warning restore CA1708
{
    /// <param name="configuration">The global configuration.</param>
    extension([NotNull] IGlobalConfiguration configuration)
    {
        /// <summary>
        /// Uses a <see cref="RedisStorage"/> on a new connection as the current storage. The storage owns the
        /// connection.
        /// </summary>
        /// <param name="redisConfiguration">A StackExchange.Redis configuration string, such as <c>localhost:6379</c>.</param>
        /// <param name="options">The options, or <c>null</c> for the defaults.</param>
        /// <returns>The configuration, to chain further calls.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> or <paramref name="redisConfiguration"/> is <c>null</c>.</exception>
        public IGlobalConfiguration<RedisStorage> UseRedisStorage([NotNull] string redisConfiguration, [CanBeNull] RedisStorageOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(redisConfiguration);
            return configuration.UseStorage(new RedisStorage(redisConfiguration, options));
        }

        /// <summary>
        /// Uses a <see cref="RedisStorage"/> on a connection the application owns, for example one registered in
        /// its service container, as the current storage.
        /// </summary>
        /// <param name="connection">The connection.</param>
        /// <param name="options">The options, or <c>null</c> for the defaults.</param>
        /// <returns>The configuration, to chain further calls.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> or <paramref name="connection"/> is <c>null</c>.</exception>
        public IGlobalConfiguration<RedisStorage> UseRedisStorage([NotNull] IConnectionMultiplexer connection, [CanBeNull] RedisStorageOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(connection);
            return configuration.UseStorage(new RedisStorage(connection, options));
        }
    }
}
