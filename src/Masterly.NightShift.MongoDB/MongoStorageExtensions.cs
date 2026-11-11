using Masterly.NightShift.MongoDB;
using MongoDB.Driver;

// ReSharper disable once CheckNamespace
namespace Masterly.NightShift;

/// <summary>
/// Configures Masterly.NightShift to use MongoDB: <c>configuration.UseMongoStorage(connectionString, "jobs")</c>
/// in the <c>AddNightShift</c> callback or on <see cref="GlobalConfiguration.Configuration"/>.
/// </summary>
#pragma warning disable CA1708 // False positive: the analyzer compares the types the compiler generates for extension blocks
public static class MongoStorageExtensions
#pragma warning restore CA1708
{
    /// <param name="configuration">The global configuration.</param>
    extension([NotNull] IGlobalConfiguration configuration)
    {
        /// <summary>Uses a <see cref="MongoStorage"/> on a new client as the current storage.</summary>
        /// <param name="connectionString">A MongoDB connection string to a replica set or a sharded cluster.</param>
        /// <param name="databaseName">The database that holds the collections.</param>
        /// <param name="options">The options, or <c>null</c> for the defaults.</param>
        /// <returns>The configuration, to chain further calls.</returns>
        /// <exception cref="ArgumentNullException">An argument other than <paramref name="options"/> is <c>null</c>.</exception>
        public IGlobalConfiguration<MongoStorage> UseMongoStorage([NotNull] string connectionString, [NotNull] string databaseName, [CanBeNull] MongoStorageOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(connectionString);
            ArgumentNullException.ThrowIfNull(databaseName);
            return configuration.UseStorage(new MongoStorage(connectionString, databaseName, options));
        }

        /// <summary>
        /// Uses a <see cref="MongoStorage"/> on a database of a client the application owns, for example one
        /// registered in its service container, as the current storage.
        /// </summary>
        /// <param name="database">The database that holds the collections.</param>
        /// <param name="options">The options, or <c>null</c> for the defaults.</param>
        /// <returns>The configuration, to chain further calls.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> or <paramref name="database"/> is <c>null</c>.</exception>
        public IGlobalConfiguration<MongoStorage> UseMongoStorage([NotNull] IMongoDatabase database, [CanBeNull] MongoStorageOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(database);
            return configuration.UseStorage(new MongoStorage(database, options));
        }
    }
}
