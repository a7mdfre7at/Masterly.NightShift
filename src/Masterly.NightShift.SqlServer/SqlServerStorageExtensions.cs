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

using Masterly.NightShift.SqlServer;

// ReSharper disable once CheckNamespace
namespace Masterly.NightShift;

/// <summary>
/// Configures Masterly.NightShift to use SQL Server: <c>configuration.UseSqlServerStorage(connectionString)</c>
/// in the <c>AddNightShift</c> callback or on <see cref="GlobalConfiguration.Configuration"/>.
/// </summary>
#pragma warning disable CA1708 // False positive: the analyzer compares the types the compiler generates for extension blocks
public static class SqlServerStorageExtensions
#pragma warning restore CA1708
{
    private static int _metricsInitialized;

    /// <param name="configuration">The global configuration.</param>
    extension([NotNull] IGlobalConfiguration configuration)
    {
        /// <summary>
        /// Uses a <see cref="SqlServerStorage"/> created from a connection string, with the default
        /// <see cref="SqlServerStorageOptions"/>, as the current storage, and adds the SQL Server metrics to the
        /// dashboard's home page.
        /// </summary>
        /// <param name="connectionString">A SQL Server connection string.</param>
        /// <returns>The configuration, to chain further calls.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> or <paramref name="connectionString"/> is <c>null</c>.</exception>
        public IGlobalConfiguration<SqlServerStorage> UseSqlServerStorage(
            [NotNull] string connectionString)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(connectionString);

            var storage = new SqlServerStorage(connectionString);
            return configuration.UseStorage(storage).UseSqlServerStorageCommonMetrics();
        }

        /// <summary>
        /// Uses a <see cref="SqlServerStorage"/> created from a connection string and options as the current
        /// storage, and adds the SQL Server metrics to the dashboard's home page.
        /// </summary>
        /// <param name="connectionString">A SQL Server connection string.</param>
        /// <param name="options">The storage options.</param>
        /// <returns>The configuration, to chain further calls.</returns>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        public IGlobalConfiguration<SqlServerStorage> UseSqlServerStorage(
            [NotNull] string connectionString,
            [NotNull] SqlServerStorageOptions options)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(connectionString);
            ArgumentNullException.ThrowIfNull(options);

            var storage = new SqlServerStorage(connectionString, options);
            return configuration.UseStorage(storage).UseSqlServerStorageCommonMetrics();
        }

        /// <summary>
        /// Uses a <see cref="SqlServerStorage"/> that opens its connections with <paramref name="connectionFactory"/>,
        /// with the default <see cref="SqlServerStorageOptions"/>, as the current storage, and adds the SQL Server
        /// metrics to the dashboard's home page.
        /// </summary>
        /// <param name="connectionFactory">Creates a new connection each time it is called; the storage opens it when it is closed, and disposes it.</param>
        /// <returns>The configuration, to chain further calls.</returns>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        public IGlobalConfiguration<SqlServerStorage> UseSqlServerStorage(
            [NotNull] Func<DbConnection> connectionFactory)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(connectionFactory);

            var storage = new SqlServerStorage(connectionFactory);
            return configuration.UseStorage(storage).UseSqlServerStorageCommonMetrics();
        }

        /// <summary>
        /// Uses a <see cref="SqlServerStorage"/> that opens its connections with <paramref name="connectionFactory"/>,
        /// with the given options, as the current storage, and adds the SQL Server metrics to the dashboard's home page.
        /// </summary>
        /// <param name="connectionFactory">Creates a new connection each time it is called; the storage opens it when it is closed, and disposes it.</param>
        /// <param name="options">The storage options.</param>
        /// <returns>The configuration, to chain further calls.</returns>
        /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
        public IGlobalConfiguration<SqlServerStorage> UseSqlServerStorage(
            [NotNull] Func<DbConnection> connectionFactory,
            [NotNull] SqlServerStorageOptions options)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(connectionFactory);
            ArgumentNullException.ThrowIfNull(options);

            var storage = new SqlServerStorage(connectionFactory, options);
            return configuration.UseStorage(storage).UseSqlServerStorageCommonMetrics();
        }
    }

    extension([NotNull] IGlobalConfiguration<SqlServerStorage> configuration)
    {
        private IGlobalConfiguration<SqlServerStorage> UseSqlServerStorageCommonMetrics(
    )
        {
            ArgumentNullException.ThrowIfNull(configuration);

            if (Interlocked.Exchange(ref _metricsInitialized, 1) == 0)
            {
                configuration.UseDashboardMetric(SqlServerStorage.SchemaVersion);
                configuration.UseDashboardMetric(SqlServerStorage.ActiveConnections);
                configuration.UseDashboardMetric(SqlServerStorage.TotalConnections);
                configuration.UseDashboardMetric(SqlServerStorage.ActiveTransactions);
                configuration.UseDashboardMetric(SqlServerStorage.DataFilesSize);
                configuration.UseDashboardMetric(SqlServerStorage.LogFilesSize);
            }

            return configuration;
        }
    }
}
