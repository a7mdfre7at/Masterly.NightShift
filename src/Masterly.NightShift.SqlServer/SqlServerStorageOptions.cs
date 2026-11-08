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

#nullable enable

using System;
using System.Data;
using System.Data.Common;
using System.Reflection;

namespace Masterly.NightShift.SqlServer
{
    /// <summary>
    /// Options of the <see cref="SqlServerStorage"/>: how it fetches jobs, polls queues, batches commands, takes
    /// locks, cleans up old data and manages its schema. Pass them to <c>UseSqlServerStorage</c> or a
    /// <see cref="SqlServerStorage"/> constructor, and don't change them afterwards.
    /// </summary>
    public class SqlServerStorageOptions
    {
        private TimeSpan _queuePollInterval;
        private string _schemaName;
        private TimeSpan _jobExpirationCheckInterval;
        private TimeSpan _countersAggregateInterval;
        private TimeSpan? _slidingInvisibilityTimeout;
        private TimeSpan? _commandTimeout;
        private TimeSpan? _commandBatchMaxTimeout;
        private DbProviderFactory? _sqlClientFactory;

        // null until the user sets them, so auto-detection only decides the values nobody chose
        private bool? _disableGlobalLocks;
        private bool? _useIgnoreDupKeyOption;

        /// <summary>
        /// Initializes the options with their defaults.
        /// </summary>
        public SqlServerStorageOptions()
        {
            QueuePollInterval = TimeSpan.Zero;
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5);
#pragma warning disable 618
            InvisibilityTimeout = TimeSpan.FromMinutes(30);
#pragma warning restore 618
            JobExpirationCheckInterval = TimeSpan.FromMinutes(30);
            CountersAggregateInterval = TimeSpan.FromMinutes(5);
            PrepareSchemaIfNecessary = true;
            DashboardJobListLimit = 10000;
            _schemaName = Constants.DefaultSchema;
#pragma warning disable 618
            TransactionTimeout = TimeSpan.FromMinutes(1);
#pragma warning restore 618
            DeleteExpiredBatchSize = -1;
            UseTransactionalAcknowledge = false;
            UseRecommendedIsolationLevel = true;
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5);
            TryAutoDetectSchemaDependentOptions = true;
            _sqlClientFactory = GetDefaultSqlClientFactory();
            InactiveStateExpirationTimeout = TimeSpan.Zero;
        }

        private static DbProviderFactory? GetDefaultSqlClientFactory()
        {
            var dbProviderFactoryTypes = new[]
            {
                "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient",
                // Available in the .NET Framework GAC, requires Version + Culture + PublicKeyToken to be explicitly specified
                "System.Data.SqlClient.SqlClientFactory, System.Data, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
                "System.Data.SqlClient.SqlClientFactory, System.Data.SqlClient",
            };

            foreach (var dbProviderFactoryType in dbProviderFactoryTypes)
            {
                var providerFactoryType = Type.GetType(dbProviderFactoryType, throwOnError: false);
                if (providerFactoryType != null)
                {
                    var instanceField = providerFactoryType.GetField("Instance");
                    if (instanceField == null)
                    {
                        continue;
                    }
                    var instance = (DbProviderFactory?)instanceField.GetValue(null);
                    if (instance != null)
                    {
                        return instance;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Gets or sets the isolation level of storage transactions, used only when
        /// <see cref="UseRecommendedIsolationLevel"/> is <c>false</c>. <c>null</c> means <c>READ COMMITTED</c>.
        /// </summary>
        [Obsolete("Set UseRecommendedIsolationLevel instead. Kept for code ported from Hangfire.")]
        public IsolationLevel? TransactionIsolationLevel { get; set; }

        /// <summary>
        /// Gets or sets how long an idle worker waits before asking for a job again. Values below 1 second are
        /// clamped to 100 ms to 1 second, and <see cref="TimeSpan.Zero"/> (the default) means 200 ms with sliding
        /// invisibility and 1 second with the transactional fetch.
        /// </summary>
        /// <exception cref="ArgumentException">The value is negative.</exception>
        public TimeSpan QueuePollInterval
        {
            get { return _queuePollInterval; }
            set
            {
                var message = $"The QueuePollInterval property value should be positive. Given: {value}.";

                if (value != value.Duration())
                {
                    throw new ArgumentException(message, nameof(value));
                }

                _queuePollInterval = value;
            }
        }

        /// <summary>
        /// Gets or sets how long ago a queue row must have been marked as fetched for the legacy transactional
        /// fetch to take it anyway. No effect with sliding invisibility. Default is 30 minutes.
        /// </summary>
        [Obsolete("Used only by the legacy transactional fetch (SlidingInvisibilityTimeout = null), to also take queue rows that were marked as fetched longer ago than this. It has no effect with sliding invisibility, the default. Kept for code ported from Hangfire.")]
        public TimeSpan InvisibilityTimeout { get; set; }

        /// <summary>
        /// Gets or sets how long a fetched job stays invisible to other workers after its last keep-alive.
        /// Default is 5 minutes; <c>null</c> switches to the legacy transactional fetch. SQL Server compares
        /// it in whole seconds, so it must be at least 1 second.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is shorter than 1 second.</exception>
        public TimeSpan? SlidingInvisibilityTimeout
        {
            get { return _slidingInvisibilityTimeout; }
            set
            {
                EnsureAtLeastOneSecond(value, nameof(SlidingInvisibilityTimeout));
                _slidingInvisibilityTimeout = value;
            }
        }

        /// <summary>
        /// Gets or sets whether the install script runs when the storage is created, creating or upgrading the
        /// tables. Default is <c>true</c>. Turn it off when the application's account can't change the schema, or
        /// when you deploy schema changes yourself.
        /// </summary>
        public bool PrepareSchemaIfNecessary { get; set; }

        /// <summary>
        /// Gets or sets how often the expiration manager deletes expired jobs, lists, sets, hashes and aggregated
        /// counters. Default is 30 minutes. Must be greater than zero (checked when a server starts).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is greater than <see cref="int.MaxValue"/> milliseconds.</exception>
        public TimeSpan JobExpirationCheckInterval 
        {
            get { return _jobExpirationCheckInterval; } 
            set {
                if (value.TotalMilliseconds > int.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "Job expiration check interval cannot be greater than int.MaxValue");
                }
                _jobExpirationCheckInterval = value;
            }
        }

        /// <summary>
        /// Gets or sets how often the counters aggregator merges the <c>[Counter]</c> rows into
        /// <c>[AggregatedCounter]</c>. Default is 5 minutes. Must be greater than zero.
        /// </summary>
        /// <exception cref="ArgumentException">The value is zero or negative.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The value is greater than <see cref="int.MaxValue"/> milliseconds.</exception>
        public TimeSpan CountersAggregateInterval
        {
            get { return _countersAggregateInterval; }
            set
            {
                if (value <= TimeSpan.Zero)
                {
                    throw new ArgumentException($"The CountersAggregateInterval property value should be greater than zero. Given: {value}.", nameof(value));
                }

                if (value.TotalMilliseconds > int.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "Counters aggregate interval cannot be greater than int.MaxValue");
                }

                _countersAggregateInterval = value;
            }
        }

        /// <summary>
        /// Gets or sets the upper limit of the job counts used to page the dashboard's job lists. Counting stops
        /// at this number, which keeps the query cheap on large tables. Default is 10000; <c>null</c> counts every job.
        /// </summary>
        public int? DashboardJobListLimit { get; set; }

        /// <summary>
        /// Has no effect: explicit transactions are always used.
        /// </summary>
        [Obsolete("This option has no effect, because TransactionScope is no longer used and explicit transactions are always used instead. You can safely remove it. Kept for code ported from Hangfire.")]
        public TimeSpan TransactionTimeout { get; set; }

        /// <summary>
        /// Gets or sets the timeout of each SQL command. Default is <c>null</c>, the SQL client's default
        /// (30 seconds). Commands take whole seconds, so a value must be at least 1 second.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is shorter than 1 second.</exception>
        public TimeSpan? CommandTimeout
        {
            get { return _commandTimeout; }
            set
            {
                EnsureAtLeastOneSecond(value, nameof(CommandTimeout));
                _commandTimeout = value;
            }
        }

        /// <summary>
        /// Gets or sets the upper limit of the timeout of a batch of commands. Default is 5 minutes; <c>null</c>
        /// sends the commands of a transaction one by one. Commands take whole seconds, so a value must be at
        /// least 1 second.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is shorter than 1 second.</exception>
        public TimeSpan? CommandBatchMaxTimeout
        {
            get { return _commandBatchMaxTimeout; }
            set
            {
                EnsureAtLeastOneSecond(value, nameof(CommandBatchMaxTimeout));
                _commandBatchMaxTimeout = value;
            }
        }

        /// <summary>
        /// Gets or sets the age after which the expiration manager deletes rows of a job's state history that
        /// aren't the job's current state. <see cref="TimeSpan.Zero"/> (the default) keeps the whole history.
        /// Needs schema version 9. Negative values throw when a server starts.
        /// </summary>
        public TimeSpan InactiveStateExpirationTimeout { get; set; }

        /// <summary>
        /// Gets or sets the database schema that holds the tables. Default is <c>HangFire</c>. The name is
        /// used inside both <c>[...]</c> and <c>'...'</c> in SQL, so it can't contain <c>]</c>, <c>'</c> or
        /// control characters, and it is at most 128 characters long.
        /// </summary>
        /// <exception cref="ArgumentException">The value is <c>null</c>, empty, white space, longer than 128
        /// characters, or contains <c>]</c>, <c>'</c> or a control character.</exception>
        public string SchemaName
        {
            get { return _schemaName; }
            set
            {
                ValidateSchemaName(value, nameof(value));
                _schemaName = value;
            }
        }

        internal const int MaxSchemaNameLength = 128;

        internal static void ValidateSchemaName(string schemaName, string paramName)
        {
            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new ArgumentException($"The SchemaName property value should be a non-empty schema name. Given: '{schemaName}'.", paramName);
            }

            if (schemaName.Length > MaxSchemaNameLength)
            {
                throw new ArgumentException($"The schema name can be at most {MaxSchemaNameLength} characters long, but {schemaName.Length} were given.", paramName);
            }

            foreach (var ch in schemaName)
            {
                if (ch == ']' || ch == '\'' || char.IsControl(ch))
                {
                    throw new ArgumentException($"The schema name can't contain ']', ''' or control characters, because the install script uses it inside [...] and '...'. Given: '{schemaName}'.", paramName);
                }
            }
        }

        private static void EnsureAtLeastOneSecond(TimeSpan? value, string propertyName)
        {
            if (value.HasValue && value.Value < TimeSpan.FromSeconds(1))
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"The {propertyName} property value should be at least 1 second, because SQL Server works with whole seconds here. Given: {value}.");
            }
        }

        /// <summary>
        /// Gets or sets a function called before each connection is opened, whose result is disposed right after,
        /// for example to open a connection under another Windows identity on .NET Framework. Default is <c>null</c>.
        /// </summary>
        public Func<IDisposable>? ImpersonationFunc { get; set; }

        /// <summary>
        /// Gets or sets whether to skip the application lock that otherwise guards every write to the
        /// <c>[Set]</c>, <c>[Hash]</c> and <c>[List]</c> tables as a whole. Default is <c>false</c>. When it
        /// isn't set, <see cref="TryAutoDetectSchemaDependentOptions"/> turns it on for schema version 6 or
        /// later; a value you set is always kept.
        /// </summary>
        public bool DisableGlobalLocks
        {
            get { return _disableGlobalLocks ?? false; }
            set { _disableGlobalLocks = value; }
        }

        /// <summary>
        /// Has no effect.
        /// </summary>
        [Obsolete("This option doesn't change anything. You can safely remove it. Kept for code ported from Hangfire.")]
        public bool UsePageLocksOnDequeue { get; set; }

        /// <summary>
        /// Gets or sets whether storage transactions run as <c>READ COMMITTED</c>. Default is <c>true</c>.
        /// When <c>false</c>, the obsolete <c>TransactionIsolationLevel</c> is used instead.
        /// </summary>
        public bool UseRecommendedIsolationLevel { get; set; }

        /// <summary>
        /// Gets or sets whether the startup install may apply the upgrades from schema version 5 to 6 and from 7
        /// to 8, which rebuild large tables. Default is <c>false</c>, which stops an upgrade at version 5 or 7.
        /// New installs always go to the latest version.
        /// </summary>
        public bool EnableHeavyMigrations { get; set; }

        /// <summary>
        /// Gets or sets whether, with <see cref="DisableGlobalLocks"/> on, writes to the <c>[Set]</c>,
        /// <c>[Hash]</c> and <c>[List]</c> tables take an application lock per key instead of none.
        /// Default is <c>false</c>.
        /// </summary>
        public bool UseFineGrainedLocks { get; set; }

        /// <summary>
        /// Gets or sets whether IGNORE_DUP_KEY was applied to [Hash] and [Set] tables and so MERGE
        /// statements can be replaced by much more efficient INSERT/UPDATE pair. This option allows
        /// to avoid deadlocks related to SERIALIZABLE-level range locks without introducing transient
        /// errors due to concurrency. Default is <c>false</c>. When it isn't set,
        /// <see cref="TryAutoDetectSchemaDependentOptions"/> turns it on for schema version 8 or later;
        /// a value you set is always kept.
        /// </summary>
        public bool UseIgnoreDupKeyOption
        {
            get { return _useIgnoreDupKeyOption ?? false; }
            set { _useIgnoreDupKeyOption = value; }
        }

        /// <summary>
        /// Gets or sets the number of records deleted in a single batch by the expiration manager and by
        /// <see cref="SqlServerStorage.Purge"/>. Default
        /// value is -1, which means 1000, or 10000 when <see cref="TryAutoDetectSchemaDependentOptions"/>
        /// finds schema version 6 or later. Values outside 1 to 100000 also mean 1000. It can be configured
        /// to a higher one when processing throughput is high enough, so expiration manager becomes the
        /// bottleneck.
        /// </summary>
        public int DeleteExpiredBatchSize { get; set; }

        /// <summary>The value set for <see cref="DisableGlobalLocks"/>, or <c>null</c> when it was never set.</summary>
        internal bool? DisableGlobalLocksIfSet => _disableGlobalLocks;

        /// <summary>The value set for <see cref="UseIgnoreDupKeyOption"/>, or <c>null</c> when it was never set.</summary>
        internal bool? UseIgnoreDupKeyOptionIfSet => _useIgnoreDupKeyOption;

        /// <summary>
        /// Gets or sets whether to enable experimental feature of transactional acknowledge of completed
        /// background jobs. In this case there will be less requests sent to SQL Server and better handling
        /// of data loss when asynchronous replication is used. But additional blocking on the JobQueue table
        /// is expected, since transaction commit requires an explicit Commit request to be sent. 
        /// </summary>
        public bool UseTransactionalAcknowledge { get; set; }

        /// <summary>
        /// Gets or sets the <see cref="DbProviderFactory"/> for creating <c>SqlConnection</c> instances.
        /// Defaults to either <c>System.Data.SqlClient.SqlClientFactory.Instance</c> or
        /// <c>Microsoft.Data.SqlClient.SqlClientFactory</c> depending on which package reference exists
        /// on the consuming project.
        /// </summary>
        public DbProviderFactory SqlClientFactory
        {
            get => _sqlClientFactory ?? throw new InvalidOperationException("Please add a NuGet package reference to either 'Microsoft.Data.SqlClient' or 'System.Data.SqlClient' in your application project. " +
                                                                            "Masterly.NightShift.SqlServer supports both providers but let the consumer decide which one should be used.");
            set => _sqlClientFactory = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// Gets or sets whether to try automatically query for the current schema on application start
        /// and enable <see cref="UseIgnoreDupKeyOption"/> (schema 8+), <see cref="DisableGlobalLocks"/>
        /// (schema 6+) and the bigger <see cref="DeleteExpiredBatchSize"/> (schema 6+) depending on the
        /// current schema version. Only the options you didn't set are decided this way: a value you set
        /// is always kept. The detected values are used by the storage and aren't written back to this
        /// object. When storage is inaccessible on startup, default values are used for those options until the
        /// version can be read, which is tried again when the storage first needs them, then at most every
        /// 30 seconds.
        /// </summary>
        public bool TryAutoDetectSchemaDependentOptions { get; set; }

        /// <summary>
        /// Gets or sets a default queue provider that will be used when no special provider was
        /// registered for a particular queue.
        /// </summary>
        public IPersistentJobQueueProvider? DefaultQueueProvider { get; set; }

        /// <summary>
        /// This option has no effect and is kept only for compatibility. <see cref="SqlServerStorage"/>
        /// no longer uses the System.Transactions namespace, and always uses regular explicit transactions.
        /// </summary>
        [Obsolete("This option has no effect, because TransactionScope is no longer used and explicit transactions are always used instead. You can safely remove it. Kept for code ported from Hangfire.")]
        public bool DisableTransactionScope { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to disable semaphores used to limit the number
        /// of queries when listening on empty queues. Using this option may increase the overall
        /// throughput for bursty workloads, at the cost of increasing the number of queries.
        /// </summary>
        public bool DisableFetchSemaphores { get; set; }
    }
}
