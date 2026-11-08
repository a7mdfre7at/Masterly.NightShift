// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Dapper;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.SqlServer
{
    /// <summary>
    /// The SQL Server storage. It keeps jobs, queues, recurring jobs and statistics in tables of one database
    /// schema (<see cref="SqlServerStorageOptions.SchemaName"/>), installs or upgrades that schema when it is
    /// created (<see cref="SqlServerStorageOptions.PrepareSchemaIfNecessary"/>), and adds its heartbeat, expiration
    /// and counter aggregation processes to the servers. Configure it with <c>UseSqlServerStorage</c>.
    /// </summary>
    public class SqlServerStorage : JobStorage
    {
        private static readonly char[] SemicolonSeparator = new[] { ';' };
        private static readonly char[] EqualSignSeparator = new[] { '=' };

        private readonly ConcurrentDictionary<KeyValuePair<Func<string, string>, string>, string> _queryTemplateCache = new(new QueryTemplateKeyEqualityComparer());

        private readonly DbConnection _existingConnection;
        private readonly Func<DbConnection> _connectionFactory;
        private readonly SqlServerStorageOptions _options;
        private readonly string _connectionString;
        private string _escapedSchemaName;
        private SqlServerHeartbeatProcess _heartbeatProcess;

        // Set by TryAutoDetectSchemaDependentOptions. Kept here rather than written to the options,
        // so the values the user set (or left unset) on the options object stay as they are. The version is
        // written before the flag, and read after it.
        private int _detectedSchemaVersion;
        private volatile bool _schemaVersionDetected;

        // When the version couldn't be read at startup (the database wasn't reachable yet, for example), it is
        // read again on first use, at most once per SchemaVersionRetryInterval, until that succeeds
        private static readonly TimeSpan SchemaVersionRetryInterval = TimeSpan.FromSeconds(30);
        private readonly object _schemaVersionRetryLock = new object();
        private volatile bool _schemaVersionRetryPending;
        private long _nextSchemaVersionRetryTicks;

        // Likewise, when the startup install failed, it is tried again when the storage is used: after
        // SchemaVersionRetryInterval at first, then twice as long after each failure, up to MaxSchemaInstallRetryInterval
        private static readonly TimeSpan MaxSchemaInstallRetryInterval = TimeSpan.FromMinutes(30);
        private volatile bool _schemaInstallRetryPending;
        private long _nextSchemaInstallRetryTicks;
        private long _schemaInstallRetryIntervalTicks = SchemaVersionRetryInterval.Ticks;

        // Delays before the second and third attempt of the startup install
        private static readonly TimeSpan[] InstallRetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };

        private readonly Dictionary<string, bool> _features =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { JobStorageFeatures.ExtendedApi, true },
                { JobStorageFeatures.JobQueueProperty, true },
                { JobStorageFeatures.ProcessesInsteadOfComponents, true },
                { JobStorageFeatures.Connection.BatchedGetFirstByLowest, true },
                { JobStorageFeatures.Connection.GetUtcDateTime, true },
                { JobStorageFeatures.Connection.GetSetContains, true },
                { JobStorageFeatures.Connection.LimitedGetSetCount, true },
                { JobStorageFeatures.Transaction.AcquireDistributedLock, true },
                { JobStorageFeatures.Transaction.CreateJob, false },
                { JobStorageFeatures.Transaction.SetJobParameter, false },
                { JobStorageFeatures.Monitoring.DeletedStateGraphs, true },
                { JobStorageFeatures.Purge, true },
                { JobStorageFeatures.Monitoring.AwaitingJobs, true },
                { JobStorageFeatures.Monitoring.QueueCounts, true }
            };

        /// <summary>
        /// Initializes the storage from a connection string, with the default <see cref="SqlServerStorageOptions"/>.
        /// </summary>
        /// <param name="nameOrConnectionString">A SQL Server connection string, or the name of one in the
        /// <c>connectionStrings</c> section of <c>app.config</c>/<c>web.config</c> (.NET Framework, or an app that
        /// has <c>System.Configuration.ConfigurationManager</c>). A text without <c>=</c> is taken for a name.</param>
        /// <exception cref="ArgumentNullException"><paramref name="nameOrConnectionString"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="nameOrConnectionString"/> has no <c>=</c>, and no
        /// connection string with that name is configured.</exception>
        /// <exception cref="InvalidOperationException">Neither <c>Microsoft.Data.SqlClient</c> nor
        /// <c>System.Data.SqlClient</c> is available.</exception>
        public SqlServerStorage(string nameOrConnectionString)
            : this(nameOrConnectionString, new SqlServerStorageOptions())
        {
        }

        /// <summary>
        /// Initializes SqlServerStorage from the provided SqlServerStorageOptions and the provided connection
        /// string.
        /// </summary>
        /// <param name="nameOrConnectionString">A SQL Server connection string, or the name of one in the
        /// <c>connectionStrings</c> section of <c>app.config</c>/<c>web.config</c> (.NET Framework, or an app that
        /// has <c>System.Configuration.ConfigurationManager</c>). A text without <c>=</c> is taken for a name.</param>
        /// <param name="options">The storage options.</param>
        /// <exception cref="ArgumentNullException"><paramref name="nameOrConnectionString"/> argument is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="nameOrConnectionString"/> has no <c>=</c>, and no
        /// connection string with that name is configured.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> argument is null.</exception>
        /// <exception cref="InvalidOperationException">Neither <c>Microsoft.Data.SqlClient</c> nor
        /// <c>System.Data.SqlClient</c> is available, and <see cref="SqlServerStorageOptions.SqlClientFactory"/> wasn't
        /// set.</exception>
        public SqlServerStorage(string nameOrConnectionString, SqlServerStorageOptions options)
        {
            if (nameOrConnectionString == null) throw new ArgumentNullException(nameof(nameOrConnectionString));
            if (options == null) throw new ArgumentNullException(nameof(options));

            // Reading the factory throws when no SQL client is available. Done here, so that this fails at once
            // instead of being taken for a database that isn't reachable yet by the install, which tolerates errors
            _ = options.SqlClientFactory;

            _connectionString = ResolveConnectionString(nameOrConnectionString);
            _connectionFactory = DefaultConnectionFactory;
            _options = options;

            Initialize();
        }

        /// <summary>
        /// Returns a connection string as it is, and looks a name up in the <c>connectionStrings</c> section of
        /// the application's configuration file. Every connection string has a <c>key=value</c> pair, so a text
        /// without <c>=</c> is a name. Throws when it isn't found, so that a name isn't taken for a connection string
        /// and the storage fails at its first use instead.
        /// </summary>
        internal static string ResolveConnectionString(string nameOrConnectionString)
        {
            if (nameOrConnectionString.IndexOf('=') >= 0) return nameOrConnectionString;

            var connectionString = GetConnectionStringFromConfiguration(nameOrConnectionString, out var error);
            if (!String.IsNullOrEmpty(connectionString)) return connectionString;

            throw new ArgumentException(
                $"'{nameOrConnectionString}' looks like a connection string name, as it has no 'key=value' pairs, but no connection string with that name was found in the connectionStrings section of the application's configuration file. Pass a connection string, or add one with that name. Configuration files are read on .NET Framework, or when the app has System.Configuration.ConfigurationManager; on .NET, read the connection string with IConfiguration.GetConnectionString and pass it.",
                nameof(nameOrConnectionString),
                error);
        }

        // Read through reflection so that no package is needed: System.Configuration is part of .NET Framework,
        // and on .NET it forwards to System.Configuration.ConfigurationManager when the app has that package
        private static string GetConnectionStringFromConfiguration(string name, out Exception error)
        {
            error = null;

            try
            {
                var configurationManager = Type.GetType(
                    "System.Configuration.ConfigurationManager, System.Configuration, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
                    throwOnError: false);

                var connectionStrings = configurationManager?.GetProperty("ConnectionStrings")?.GetValue(null);
                var settings = connectionStrings?.GetType().GetProperty("Item", new[] { typeof(string) })?.GetValue(connectionStrings, new object[] { name });

                return settings?.GetType().GetProperty("ConnectionString")?.GetValue(settings) as string;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // The configuration system's assembly is missing, or the configuration file can't be read
                error = ex is TargetInvocationException { InnerException: not null } ? ex.InnerException : ex;
                return null;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SqlServerStorage"/> class with
        /// explicit instance of the <see cref="DbConnection"/> class that will be used
        /// to query the data.
        /// </summary>
        public SqlServerStorage([NotNull] DbConnection existingConnection)
            : this(existingConnection, new SqlServerStorageOptions())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SqlServerStorage"/> class with
        /// explicit instance of the <see cref="DbConnection"/> class that will be used
        /// to query the data, with the given options.
        /// </summary>
        public SqlServerStorage([NotNull] DbConnection existingConnection, [NotNull] SqlServerStorageOptions options)
        {
            if (existingConnection == null) throw new ArgumentNullException(nameof(existingConnection));
            if (options == null) throw new ArgumentNullException(nameof(options));

            _existingConnection = existingConnection;
            _options = options;

            Initialize();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SqlServerStorage"/> class with
        /// a connection factory <see cref="Func{DbConnection}"/> class that will be invoked
        /// to create new database connections for querying the data.
        /// </summary>
        public SqlServerStorage([NotNull] Func<DbConnection> connectionFactory)
            : this(connectionFactory, new SqlServerStorageOptions())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SqlServerStorage"/> class with
        /// a connection factory <see cref="Func{DbConnection}"/> class that will be invoked
        /// to create new database connections for querying the data.
        /// </summary>
        public SqlServerStorage([NotNull] Func<DbConnection> connectionFactory, [NotNull] SqlServerStorageOptions options)
        {
            if (connectionFactory == null) throw new ArgumentNullException(nameof(connectionFactory));
            if (options == null) throw new ArgumentNullException(nameof(options));

            _connectionFactory = connectionFactory;
            _options = options;

            Initialize();
        }

        /// <summary>
        /// Gets the queue implementations: <see cref="SqlServerStorageOptions.DefaultQueueProvider"/> (or the
        /// built-in SQL Server queue) for every queue, plus the providers added for particular queues.
        /// </summary>
        public virtual PersistentJobQueueProviderCollection QueueProviders { get; private set; }

        /// <inheritdoc />
        public override bool LinearizableReads => true;

        internal string SchemaName => _escapedSchemaName;
        internal int? CommandTimeout => _options.CommandTimeout.HasValue ? (int)_options.CommandTimeout.Value.TotalSeconds : (int?)null;
        internal int? CommandBatchMaxTimeout => _options.CommandBatchMaxTimeout.HasValue ? (int)_options.CommandBatchMaxTimeout.Value.TotalSeconds : (int?)null;
        internal TimeSpan? SlidingInvisibilityTimeout => _options.SlidingInvisibilityTimeout;
        internal SqlServerStorageOptions Options => _options;
        internal SqlServerHeartbeatProcess HeartbeatProcess => _heartbeatProcess;

        // The schema-dependent options in effect: the user's value when set, otherwise the detected one
        internal bool DisableGlobalLocks => _options.DisableGlobalLocksIfSet ?? DetectedSchemaVersion >= 6;
        // Off on a schema known to be older than 8 even when set: the writes that rely on it fail there with
        // primary key violations, while running without it only costs an extra round trip
        internal bool UseIgnoreDupKeyOption
        {
            get
            {
                var version = DetectedSchemaVersion;
                return !(version < 8) && (_options.UseIgnoreDupKeyOptionIfSet ?? version >= 8);
            }
        }

        internal int DeleteExpiredBatchSize => _options.DeleteExpiredBatchSize == -1 && DetectedSchemaVersion >= 6
            ? 10000
            : _options.DeleteExpiredBatchSize;

        // The schema version read by TryAutoDetectSchemaDependentOptions, or null while it is unknown
        internal int? DetectedSchemaVersion
        {
            get
            {
                RetryPendingSchemaWork();
                return _schemaVersionDetected ? _detectedSchemaVersion : (int?)null;
            }
        }

        // Whether the startup install failed and is still to be tried again
        internal bool SchemaInstallPending => _schemaInstallRetryPending;

        // Rows deleted per statement by the expiration manager and Purge: values outside 1 to 100000 mean 1000
        internal int EffectiveDeleteBatchSize
        {
            get
            {
                var batchSize = DeleteExpiredBatchSize;
                return batchSize <= 0 || batchSize > 100_000 ? DefaultDeleteBatchSize : batchSize;
            }
        }

        // High enough to keep the number of queries down, low enough to stay below lock escalation (which
        // happens at about 5000 locks, depending on the version). Cascading deletes of State (3-5 rows per job
        // usually) and JobParameter (2-3 rows per job usually) rows take locks too.
        private const int DefaultDeleteBatchSize = 1000;

        /// <inheritdoc />
        public override IMonitoringApi GetMonitoringApi()
        {
            return new SqlServerMonitoringApi(this, _options.DashboardJobListLimit);
        }

        /// <inheritdoc />
        public override IStorageConnection GetConnection()
        {
            RetryPendingSchemaWork();
            return new SqlServerConnection(this);
        }

        /// <inheritdoc />
#pragma warning disable 618
        [Obsolete($"Use the `{nameof(GetStorageWideProcesses)}` and/or `{nameof(GetServerRequiredProcesses)}` methods instead, and enable `{nameof(JobStorageFeatures)}.{nameof(JobStorageFeatures.ProcessesInsteadOfComponents)}`. Kept for code ported from Hangfire.")]
        public override IEnumerable<IServerComponent> GetComponents()
#pragma warning restore 618
        {
            yield return new ExpirationManager(this, _options.InactiveStateExpirationTimeout, _options.JobExpirationCheckInterval);
            yield return new CountersAggregator(this, _options.CountersAggregateInterval);
            yield return _heartbeatProcess;
        }

        /// <summary>
        /// Gets the process every server runs for this storage: the heartbeat that keeps the queue rows of the
        /// server's running jobs fetched.
        /// </summary>
        public override IEnumerable<IBackgroundProcess> GetServerRequiredProcesses()
        {
            yield return _heartbeatProcess;
        }

        /// <summary>
        /// Gets the maintenance processes run by servers that aren't lightweight: the expiration manager and the
        /// counters aggregator.
        /// </summary>
        public override IEnumerable<IBackgroundProcess> GetStorageWideProcesses()
        {
            yield return new ExpirationManager(this, _options.InactiveStateExpirationTimeout, _options.JobExpirationCheckInterval);
            yield return new CountersAggregator(this, _options.CountersAggregateInterval);
        }

        /// <inheritdoc />
        public override void WriteOptionsToLog(ILog logger)
        {
            logger.Info($"Using the following options for SQL Server job storage: Queue poll interval: {_options.QueuePollInterval}.");
        }

        /// <inheritdoc />
        public override bool HasFeature(string featureId)
        {
            if (featureId == null) throw new ArgumentNullException(nameof(featureId));

            return _features.TryGetValue(featureId, out var isSupported)
                ? isSupported
                : base.HasFeature(featureId);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Rows are deleted in batches (<see cref="SqlServerStorageOptions.DeleteExpiredBatchSize"/> rows per
        /// statement, as the expiration manager does), each batch in its own short transaction bound by
        /// <see cref="SqlServerStorageOptions.CommandTimeout"/>. Child tables go before their parents. So a purge
        /// of any size finishes without holding locks or growing the transaction log for its whole duration, but
        /// it isn't all or nothing: when it is interrupted, the rows deleted so far stay deleted, and running it
        /// again finishes the job.
        /// </remarks>
        public override void Purge(StoragePurgeOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var log = LogProvider.GetLogger(typeof(SqlServerStorage));
            log.Info("Purging the SQL Server job storage...");

            var deleted = UseConnection(null, static (storage, connection, ctx) =>
            {
                long total = 0;

                total += storage.PurgeTable(connection, "JobParameter", null);
                total += storage.PurgeTable(connection, "State", null);
                total += storage.PurgeTable(connection, "JobQueue", null);
                total += storage.PurgeTable(connection, "Job", null);
                total += storage.PurgeTable(connection, "Counter", null);
                total += storage.PurgeTable(connection, "AggregatedCounter", null);
                total += storage.PurgeTable(connection, "List", ctx.KeepAuditLog ? "[Key] <> @auditLogKey" : null);
                total += storage.PurgeTable(connection, "Set", ctx.KeepRecurringJobs ? "[Key] <> N'recurring-jobs'" : null);

                var hashConditions = new List<string>();
                if (ctx.KeepRecurringJobs) hashConditions.Add("[Key] not like N'recurring-job:%'");
                if (ctx.KeepDrainMode) hashConditions.Add("[Key] <> @drainModeKey");

                total += storage.PurgeTable(connection, "Hash", hashConditions.Count > 0 ? String.Join(" and ", hashConditions) : null);

                if (!ctx.KeepServers)
                {
                    total += storage.PurgeTable(connection, "Server", null);
                }

                return total;
            }, options);

            log.Info($"The SQL Server job storage was purged: {deleted} rows deleted.");
        }

        private long PurgeTable(DbConnection connection, string table, string condition)
        {
            var batchSize = EffectiveDeleteBatchSize;

            // The row count is selected rather than taken from the command's result, which is -1 when the
            // session has NOCOUNT on (an existing connection keeps the settings of earlier commands)
            var query = FormattableString.Invariant(
                $"delete top (@count) T from [{SchemaName}].[{table}] T{(condition != null ? " where " + condition : String.Empty)}; select @@ROWCOUNT;");
            var parameters = new { count = batchSize, auditLogKey = AuditLog.ListKey, drainModeKey = DrainMode.HashKey };

            long deleted = 0;
            int affected;

            // Every statement commits on its own, so no batch waits for the others, and a failure keeps what
            // the previous batches deleted
            do
            {
                affected = connection.ExecuteScalar<int>(query, parameters, commandTimeout: CommandTimeout);
                deleted += affected;
            } while (affected >= batchSize);

            return deleted;
        }

        /// <summary>
        /// Describes the storage as <c>SQL Server: {server}@{database}</c>, taken from the connection string
        /// without credentials, or <c>SQL Server (custom)</c> when it was created without a connection string.
        /// </summary>
        public override string ToString()
        {
            const string canNotParseMessage = "<Connection string can not be parsed>";

            try
            {
                if (_connectionString == null)
                {
                    return "SQL Server (custom)";
                }

                var parts = _connectionString.Split(SemicolonSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(static x => x.Split(EqualSignSeparator, StringSplitOptions.RemoveEmptyEntries))
                    .Select(static x => new { Key = x[0].Trim(), Value = x[1].Trim() })
                    .GroupBy(static x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(static x => x.Key, static x => x.Last().Value, StringComparer.OrdinalIgnoreCase);

                var builder = new StringBuilder();

                foreach (var alias in new[] { "Data Source", "Server", "Address", "Addr", "Network Address" })
                {
                    if (parts.TryGetValue(alias, out var part))
                    {
                        builder.Append(part);
                        break;
                    }
                }

                if (builder.Length != 0) builder.Append('@');

                foreach (var alias in new[] { "Database", "Initial Catalog" })
                {
                    if (parts.TryGetValue(alias, out var part))
                    {
                        builder.Append(part);
                        break;
                    }
                }

                return builder.Length != 0
                    ? $"SQL Server: {builder}"
                    : canNotParseMessage;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return canNotParseMessage;
            }
        }

        internal string GetQueryFromTemplate(Func<string, string> templateFunc)
        {
            return _queryTemplateCache.GetOrAdd(
                new KeyValuePair<Func<string, string>, string>(templateFunc, SchemaName),
                static pair => pair.Key(pair.Value));
        }

        internal void UseConnection(
            DbConnection dedicatedConnection,
            [InstantHandle] Action<SqlServerStorage, DbConnection> action)
        {
            UseConnection(dedicatedConnection, static (storage, connection, ctx) =>
            {
                ctx(storage, connection);
                return true;
            }, action);
        }

        internal TResult UseConnection<TResult>(
            DbConnection dedicatedConnection,
            [InstantHandle] Func<SqlServerStorage, DbConnection, TResult> action)
        {
            return UseConnection(dedicatedConnection, static (storage, connection, ctx) => ctx(storage, connection), action);
        }

        internal TResult UseConnection<TContext, TResult>(
            DbConnection dedicatedConnection,
            [InstantHandle] Func<SqlServerStorage, DbConnection, TContext, TResult> func,
            TContext context)
        {
            DbConnection connection = null;

            try
            {
                connection = dedicatedConnection ?? CreateAndOpenConnection();
                return func(this, connection, context);
            }
            finally
            {
                if (dedicatedConnection == null)
                {
                    ReleaseConnection(connection);
                }
            }
        }

        internal void UseTransaction<TContext>(
            DbConnection dedicatedConnection,
            [InstantHandle] Action<SqlServerStorage, DbConnection, DbTransaction, TContext> action,
            TContext context)
        {
            UseTransaction(dedicatedConnection, static (storage, connection, transaction, ctx) =>
            {
                ctx.Key(storage, connection, transaction, ctx.Value);
                return true;
            }, new KeyValuePair<Action<SqlServerStorage, DbConnection, DbTransaction, TContext>, TContext>(action, context), null);
        }
        
        internal TResult UseTransaction<TContext, TResult>(
            DbConnection dedicatedConnection,
            [InstantHandle] Func<SqlServerStorage, DbConnection, DbTransaction, TContext, TResult> func,
            TContext context,
            IsolationLevel? isolationLevel)
        {
            isolationLevel = isolationLevel ?? (_options.UseRecommendedIsolationLevel
                ? IsolationLevel.ReadCommitted
#pragma warning disable 618
                : _options.TransactionIsolationLevel);
#pragma warning restore 618

            return UseConnection(dedicatedConnection, static (storage, connection, ctx) =>
            {
                using (var transaction = connection.BeginTransaction(ctx.Value.Value ?? IsolationLevel.ReadCommitted))
                {
                    TResult result;

                    try
                    {
                        result = ctx.Key(storage, connection, transaction, ctx.Value.Key);
                        transaction.Commit();
                    }
                    catch (Exception ex) when (ex.IsCatchableExceptionType())
                    {
                        // It is possible that XACT_ABORT option is set, and in this
                        // case transaction will be aborted automatically on server.
                        // Some older SqlClient implementations throw InvalidOperationException
                        // when trying to rollback such an aborted transaction, so we
                        // try to handle this case.
                        //
                        // It's also possible that our connection is broken, so this
                        // check is useful even when XACT_ABORT option wasn't set.
                        if (transaction.Connection != null)
                        {
                            // Don't rely on implicit rollback when calling the Dispose
                            // method, because some implementations may throw the
                            // NullReferenceException, although it's prohibited to throw
                            // any exception from a Dispose method, according to the
                            // .NET Framework Design Guidelines:
                            // https://github.com/dotnet/efcore/issues/12864
                            // https://github.com/HangfireIO/Hangfire/issues/1494
                            transaction.Rollback();
                        }

                        throw;
                    }

                    return result;
                }
            }, new KeyValuePair<Func<SqlServerStorage, DbConnection, DbTransaction, TContext, TResult>, KeyValuePair<TContext, IsolationLevel?>>(
                func,
                new KeyValuePair<TContext, IsolationLevel?>(context, isolationLevel)));
        }

        internal DbConnection CreateAndOpenConnection()
        {
            return CreateAndOpenConnection(clearPoolOnOpenFailure: false);
        }

        private DbConnection CreateAndOpenConnection(bool clearPoolOnOpenFailure)
        {
            using (_options.ImpersonationFunc?.Invoke())
            {
                DbConnection connection = null;

                try
                {
                    connection = _existingConnection ?? _connectionFactory();

                    if (connection.State == ConnectionState.Closed)
                    {
                        connection.Open();
                    }

                    return connection;
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    // Before the connection is disposed, which makes it forget its pool
                    if (clearPoolOnOpenFailure && connection != null)
                    {
                        ConnectionPools.TryClear(connection);
                    }

                    ReleaseConnection(connection);
                    throw;
                }
            }
        }

        internal bool IsExistingConnection(IDbConnection connection)
        {
            return connection != null && ReferenceEquals(connection, _existingConnection);
        }

        internal void ReleaseConnection(IDbConnection connection)
        {
            if (connection != null && !IsExistingConnection(connection))
            {
                connection.Dispose();
            }
        }
        
        private DbConnection DefaultConnectionFactory()
        {
            var connection = _options.SqlClientFactory.CreateConnection() ?? throw new InvalidOperationException($"The provider factory ({_options.SqlClientFactory}) returned a null DbConnection.");
            connection.ConnectionString = _connectionString;
            return connection;
        }

        private void Initialize()
        {
            _escapedSchemaName = _options.SchemaName.Replace("]", "]]");

            if (_options.PrepareSchemaIfNecessary && !InstallSchema())
            {
                _schemaInstallRetryPending = true;
                Interlocked.Exchange(ref _nextSchemaInstallRetryTicks, DateTime.UtcNow.Ticks + _schemaInstallRetryIntervalTicks);
            }

            if (_options.TryAutoDetectSchemaDependentOptions && !TryDetectSchemaVersion(afterStartup: false))
            {
                _schemaVersionRetryPending = true;
            }

            InitializeQueueProviders();
            _heartbeatProcess = new SqlServerHeartbeatProcess();

            _features.Add(
                JobStorageFeatures.Transaction.RemoveFromQueue(typeof(SqlServerTimeoutJob)),
                _options.UseTransactionalAcknowledge);
        }

        private bool InstallSchema()
        {
            var log = LogProvider.GetLogger(typeof(SqlServerObjectsInstaller));

            log.Info("Start installing Masterly.NightShift SQL objects...");

            Exception lastException = null;

            for (var attempt = 0; attempt <= InstallRetryDelays.Length; attempt++)
            {
                if (attempt > 0)
                {
                    // A database that is still starting (a container started at the same time, for example)
                    // usually refuses connections for a few seconds, so give it time between attempts
                    Thread.Sleep(InstallRetryDelays[attempt - 1]);
                }

                try
                {
                    // After a failed open, the SQL client fails every open of a pooled connection string at once for
                    // 5 seconds or more (its pool blocking period), so the pool is cleared to let the next attempt
                    // reach the server
                    InstallSchemaOnce(clearPoolOnOpenFailure: attempt < InstallRetryDelays.Length);

                    lastException = null;
                    break;
                }
                // Not only DbException: a connection factory can fail in its own way while the database is starting
                // (a token service that isn't ready yet, for example)
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    lastException = ex;
                    log.WarnException(
                        "An exception occurred while trying to perform the migration." +
                        (attempt < InstallRetryDelays.Length ? $" Retrying in {InstallRetryDelays[attempt].TotalSeconds} s..." : ""),
                        ex);
                }
            }

            if (lastException != null)
            {
                log.WarnException($"Was unable to perform the Masterly.NightShift schema migration due to an exception. It is tried again when the storage is used, first in {SchemaVersionRetryInterval.TotalSeconds} s. Ignore this message unless you've just installed or upgraded Masterly.NightShift.", lastException);
                return false;
            }

            log.Info("Masterly.NightShift SQL objects installed.");
            return true;
        }

        private void InstallSchemaOnce(bool clearPoolOnOpenFailure)
        {
            var connection = CreateAndOpenConnection(clearPoolOnOpenFailure);

            try
            {
                // SchemaName can't contain the characters that would need escaping in the script
                SqlServerObjectsInstaller.Install(connection, _options.SchemaName, _options.EnableHeavyMigrations);
            }
            finally
            {
                ReleaseConnection(connection);
            }
        }

        // One more attempt of the install that failed at startup, without waiting between attempts. Returns the error,
        // or null when it succeeded.
        private Exception RetryInstallSchema()
        {
            try
            {
                InstallSchemaOnce(clearPoolOnOpenFailure: false);

                LogProvider.GetLogger(typeof(SqlServerObjectsInstaller)).Info("Masterly.NightShift SQL objects installed, after the install at startup had failed.");
                return null;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return ex;
            }
        }

        private bool TryDetectSchemaVersion(bool afterStartup)
        {
            var log = LogProvider.GetLogger(typeof(SqlServerStorage));

            try
            {
                var version = UseConnection(null, static (storage, connection) =>
                    connection.ExecuteScalar<int>($"select top (1) [Version] from [{storage.SchemaName}].[Schema]", commandTimeout: storage.CommandTimeout));

                _detectedSchemaVersion = version;
                _schemaVersionDetected = true;

                if (afterStartup)
                {
                    log.Info($"Read the schema version ({version}) after startup. The schema-dependent options follow it from now on.");
                }

                // The user's values win, but say so when they ask for more than the schema supports
                if (_options.UseIgnoreDupKeyOptionIfSet == true && version < 8)
                {
                    log.Warn(
                        $"UseIgnoreDupKeyOption is on, but the schema is at version {version}: the IGNORE_DUP_KEY option it relies on comes with version 8, so it stays off until the schema is upgraded.");
                }

                if (_options.DisableGlobalLocksIfSet == true && version < 6)
                {
                    log.Warn(
                        $"DisableGlobalLocks is on, but the schema is at version {version}: running without global locks needs version 6 or later. Upgrade the schema or turn the option off.");
                }

                return true;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                if (afterStartup)
                {
                    log.DebugException($"Still unable to read the schema version for the TryAutoDetectSchemaDependentOptions option. Trying again in {SchemaVersionRetryInterval.TotalSeconds} s.", ex);
                }
                else
                {
                    log.ErrorException("Was unable to use the TryAutoDetectSchemaDependentOptions option due to an exception. The defaults are used until the schema version can be read, which is tried again when the storage is used.", ex);
                }

                return false;
            }
        }

        // Tries again what failed at startup, the install and reading the schema version, each at most once per its
        // retry interval. Cheap when nothing is pending, so it runs whenever the storage is used.
        private void RetryPendingSchemaWork()
        {
            var installDue = _schemaInstallRetryPending;
            var detectionDue = _schemaVersionRetryPending && !_schemaVersionDetected;
            if (!installDue && !detectionDue) return;

            var now = DateTime.UtcNow.Ticks;
            installDue = installDue && now >= Interlocked.Read(ref _nextSchemaInstallRetryTicks);
            detectionDue = detectionDue && now >= Interlocked.Read(ref _nextSchemaVersionRetryTicks);
            if (!installDue && !detectionDue) return;

            // A thread that finds another one trying doesn't wait for it: it goes on with the defaults
            if (!Monitor.TryEnter(_schemaVersionRetryLock)) return;

            try
            {
                if (_schemaInstallRetryPending && now >= Interlocked.Read(ref _nextSchemaInstallRetryTicks))
                {
                    var error = RetryInstallSchema();

                    if (error == null)
                    {
                        _schemaInstallRetryPending = false;

                        // The schema may have changed: read its version again
                        if (_options.TryAutoDetectSchemaDependentOptions && TryDetectSchemaVersion(afterStartup: true))
                        {
                            _schemaVersionRetryPending = false;
                        }
                    }
                    else
                    {
                        // A database that refuses for longer (missing permissions, for example) isn't asked as often
                        var interval = Math.Min(Interlocked.Read(ref _schemaInstallRetryIntervalTicks) * 2, MaxSchemaInstallRetryInterval.Ticks);
                        Interlocked.Exchange(ref _schemaInstallRetryIntervalTicks, interval);
                        Interlocked.Exchange(ref _nextSchemaInstallRetryTicks, DateTime.UtcNow.Ticks + interval);

                        LogProvider.GetLogger(typeof(SqlServerObjectsInstaller)).WarnException(
                            $"Still unable to perform the Masterly.NightShift schema migration that failed at startup. Trying again in {TimeSpan.FromTicks(interval).TotalMinutes:0.#} minute(s).",
                            error);
                    }
                }

                if (_schemaVersionRetryPending && !_schemaVersionDetected && now >= Interlocked.Read(ref _nextSchemaVersionRetryTicks))
                {
                    Interlocked.Exchange(ref _nextSchemaVersionRetryTicks, now + SchemaVersionRetryInterval.Ticks);

                    if (TryDetectSchemaVersion(afterStartup: true))
                    {
                        _schemaVersionRetryPending = false;
                    }
                }
            }
            finally
            {
                Monitor.Exit(_schemaVersionRetryLock);
            }
        }

        private void InitializeQueueProviders()
        {
            var defaultQueueProvider = _options.DefaultQueueProvider ?? new SqlServerJobQueueProvider(this, _options);
            QueueProviders = new PersistentJobQueueProviderCollection(defaultQueueProvider);
        }

        /// <summary>The dashboard metric showing the number of active (not sleeping) connections to the database.</summary>
        public static readonly DashboardMetric ActiveConnections = new DashboardMetric(
            "connections:active",
            "Metrics_ActiveConnections",
            static page =>
            {
                var sqlStorage = page.Storage as SqlServerStorage;
                if (sqlStorage == null) return new Metric("???");

                return sqlStorage.UseConnection(null, static (_, connection) =>
                {
                    var sqlQuery = @"
select count(*) from sys.sysprocesses
where dbid = db_id(@name) and status != 'background' and status != 'sleeping'";

                    var value = connection
                        .QuerySingle<int>(sqlQuery, new { name = connection.Database });

                    return new Metric(value);
                });
            });

        /// <summary>The dashboard metric showing the number of connections to the database.</summary>
        public static readonly DashboardMetric TotalConnections = new DashboardMetric(
            "connections:total",
            "Metrics_TotalConnections",
            static page =>
            {
                var sqlStorage = page.Storage as SqlServerStorage;
                if (sqlStorage == null) return new Metric("???");

                return sqlStorage.UseConnection(null, static (_, connection) =>
                {
                    var sqlQuery = @"
select count(*) from sys.sysprocesses
where dbid = db_id(@name) and status != 'background'";

                    var value = connection
                        .QuerySingle<int>(sqlQuery, new { name = connection.Database });

                    return new Metric(value);
                });
            });

        /// <summary>The dashboard metric showing the number of connections to the database with an open transaction.</summary>
        public static readonly DashboardMetric ActiveTransactions = new DashboardMetric(
            "transactions:active",
            "Metrics_SQLServer_ActiveTransactions",
            static page =>
            {
                var sqlStorage = page.Storage as SqlServerStorage;
                if (sqlStorage == null) return new Metric("???");

                return sqlStorage.UseConnection(null, static (_, connection) =>
                {
                    var sqlQuery = @"
select count(*) from sys.sysprocesses
where dbid = db_id(@name) and status != 'background' and open_tran = 1";

                    var value = connection
                        .QuerySingle<int>(sqlQuery, new { name = connection.Database });

                    return new Metric(value);
                });
            });

        /// <summary>The dashboard metric showing the space used in the database's data files.</summary>
        public static readonly DashboardMetric DataFilesSize = new DashboardMetric(
            "database:files:rows:size",
            "Metrics_SQLServer_DataFilesSize",
            static page =>
            {
                var sqlStorage = page.Storage as SqlServerStorage;
                if (sqlStorage == null) return new Metric("???");

                return sqlStorage.UseConnection(null, static (_, connection) =>
                {
                    var sqlQuery = @"
select SUM(CAST(FILEPROPERTY(name, 'SpaceUsed') AS INT)/128.0) as RowsSizeMB from sys.database_files
where type = 0;";

                    var value = connection.QuerySingle<double>(sqlQuery);

                    return new Metric(value.ToString("F", CultureInfo.CurrentCulture));
                });
            });

        /// <summary>The dashboard metric showing the space used in the database's log files.</summary>
        public static readonly DashboardMetric LogFilesSize = new DashboardMetric(
            "database:files:log:size",
            "Metrics_SQLServer_LogFilesSize",
            static page =>
            {
                var sqlStorage = page.Storage as SqlServerStorage;
                if (sqlStorage == null) return new Metric("???");

                return sqlStorage.UseConnection(null, static (_, connection) =>
                {
                    var sqlQuery = @"
select SUM(CAST(FILEPROPERTY(name, 'SpaceUsed') AS INT)/128.0) as LogSizeMB from sys.database_files
where type = 1;";

                    var value = connection.QuerySingle<double>(sqlQuery);

                    return new Metric(value.ToString("F", CultureInfo.CurrentCulture));
                });
            });

        /// <summary>The dashboard metric showing the schema version, highlighted when it is older than the latest.</summary>
        public static readonly DashboardMetric SchemaVersion = new DashboardMetric(
            "sqlserver:schema",
            "Metrics_SQLServer_SchemaVersion",
            static page =>
            {
                var sqlStorage = page.Storage as SqlServerStorage;
                if (sqlStorage == null) return new Metric("???");

                return sqlStorage.UseConnection(null, static (storage, connection) =>
                {
                    var sqlQuery = $@"select top(1) [Version] from [{storage.SchemaName}].[Schema]";
                    var version = connection.QuerySingleOrDefault<int?>(sqlQuery);

                    if (!version.HasValue)
                    {
                        return new Metric("Unspecified")
                        {
                            Style = MetricStyle.Danger,
                        };
                    }

                    return new Metric(version.Value)
                    {
                        Style = version < SqlServerObjectsInstaller.LatestSchemaVersion
                            ? MetricStyle.Warning
                            : version == SqlServerObjectsInstaller.LatestSchemaVersion
                                ? MetricStyle.Success
                                : MetricStyle.Default
                    };
                });
            });

        /// <summary>
        /// Creates a dashboard metric showing a SQL Server performance counter from
        /// <c>sys.dm_os_performance_counters</c>, given its object name, counter name and instance name.
        /// </summary>
        public static readonly Func<string, string, string, DashboardMetric> PerformanceCounterDatabaseMetric = 
            static (string objectName, string counterName, string instanceName) => new DashboardMetric(
            $"sqlserver:counter:{objectName}:{counterName}:{instanceName ?? "db"}",
            counterName,
            page =>
            {
                if (objectName == null) throw new ArgumentNullException(nameof(objectName));
                if (counterName == null) throw new ArgumentNullException(nameof(counterName));

                var sqlStorage = page.Storage as SqlServerStorage;
                if (sqlStorage == null) return new Metric("???");

                return sqlStorage.UseConnection(null, static (_, connection, ctx) =>
                {
                    var sqlQuery = $@"SELECT cntr_value FROM sys.dm_os_performance_counters where object_name = @objectName and instance_name = @instanceName and counter_name = @counterName";
                    long? value;

                    try
                    {
                        value = connection.QuerySingle<long>(sqlQuery, new
                        {
                            objectName = ctx.Item1,
                            instanceName = ctx.Item2 ?? connection.Database,
                            counterName = ctx.Item3
                        });
                    }
                    catch
                    {
                        value = null;
                    }

                    return value != null ? new Metric(value.Value) : new Metric("???");
                }, Tuple.Create(objectName, instanceName, counterName));
            });

        private sealed class
            QueryTemplateKeyEqualityComparer : IEqualityComparer<KeyValuePair<Func<string, string>, string>>
        {
            public bool Equals(KeyValuePair<Func<string, string>, string> x, KeyValuePair<Func<string, string>, string> y)
            {
                return x.Key.Equals(y.Key) && x.Value == y.Value;
            }

            public int GetHashCode(KeyValuePair<Func<string, string>, string> obj)
            {
                unchecked
                {
                    return (obj.Key.GetHashCode() * 397) ^ obj.Value.GetHashCode();
                }
            }
        }
    }
}