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
using System.Collections.Generic;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Profiling;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Server
{
    /// <summary>
    /// Provides information about the context in which the job
    /// is performed.
    /// </summary>
    public class PerformContext
    {
        public PerformContext([NotNull] PerformContext context)
            : this(context.Storage, context.Connection, context.BackgroundJob, context.CancellationToken, context.Profiler, context.ServerId, context.Items)
        {
            Performer = context.Performer;
        }

        [Obsolete("Use PerformContext(JobStorage, IStorageConnection, BackgroundJob, IJobCancellationToken) overload instead. Kept for code ported from Hangfire.")]
        public PerformContext(
            [NotNull] IStorageConnection connection,
            [NotNull] BackgroundJob backgroundJob,
            [NotNull] IJobCancellationToken cancellationToken)
            : this(null, connection, backgroundJob, cancellationToken)
        {
        }

        public PerformContext(
            [CanBeNull] JobStorage? storage,
            [NotNull] IStorageConnection connection, 
            [NotNull] BackgroundJob backgroundJob,
            [NotNull] IJobCancellationToken cancellationToken)
            : this(storage, connection, backgroundJob, cancellationToken, EmptyProfiler.Instance, null, null)
        {
        }

        internal PerformContext(
            [CanBeNull] JobStorage? storage,
            [NotNull] IStorageConnection connection, 
            [NotNull] BackgroundJob backgroundJob,
            [NotNull] IJobCancellationToken cancellationToken,
            [NotNull] IProfiler profiler,
            [CanBeNull] string? serverId,
            [CanBeNull] IDictionary<string, object>? items)
        {
            Storage = storage;
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            BackgroundJob = backgroundJob ?? throw new ArgumentNullException(nameof(backgroundJob));
            CancellationToken = cancellationToken ?? throw new ArgumentNullException(nameof(cancellationToken));
            Profiler = profiler ?? throw new ArgumentNullException(nameof(profiler));
            ServerId = serverId;

            Items = items ?? new Dictionary<string, object>();
        }

        [CanBeNull]
        public JobStorage? Storage { get; }

        /// <summary>
        /// Gets an instance of the key-value storage. You can use it
        /// to pass additional information between different client filters
        /// or just between different methods.
        /// </summary>
        [NotNull]
        public IDictionary<string, object> Items { get; }

        [NotNull]
        public BackgroundJob BackgroundJob { get; }

        [Obsolete("Use BackgroundJob property instead. Kept for code ported from Hangfire.")]
        public string JobId => BackgroundJob.Id;

        [Obsolete("Use BackgroundJob property instead. Kept for code ported from Hangfire.")]
        public Job? Job => BackgroundJob.Job;

        [Obsolete("Use BackgroundJob property instead. Kept for code ported from Hangfire.")]
        public DateTime CreatedAt => BackgroundJob.CreatedAt;

        [NotNull]
        public IJobCancellationToken CancellationToken { get; }

        [NotNull]
        public IStorageConnection Connection { get; }
        
        [NotNull]
        internal IProfiler Profiler { get; }
        
        [CanBeNull]
        public IBackgroundJobPerformer? Performer { get; internal set; }

        [CanBeNull]
        public string? ServerId { get; }

        /// <summary>
        /// Gets the console the job reports progress, lines and checkpoints to. Shared by every context created
        /// for this performance, so filters and interceptors write to the same console as the job. Safe to use
        /// from several threads.
        /// </summary>
        [NotNull]
        public IJobConsole Console
        {
            get
            {
                // Items is shared by every context of the performance, and the job may ask from several threads
                lock (Items)
                {
                    if (Items.TryGetValue(ConsoleItemKey, out var existing) && existing is IJobConsole console)
                    {
                        return console;
                    }

                    var created = new JobConsole(this);
                    Items[ConsoleItemKey] = created;
                    return created;
                }
            }
        }

        internal const string ConsoleItemKey = "X_HF_Console";

        public void SetJobParameter([NotNull] string name, object value)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            Connection.SetJobParameter(BackgroundJob.Id, name, SerializationHelper.Serialize(value, SerializationOption.User));
        }

        public T? GetJobParameter<T>([NotNull] string name) => GetJobParameter<T>(name, allowStale: false);

        public T? GetJobParameter<T>([NotNull] string name, bool allowStale)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            try
            {
                string? value;

                if (allowStale && BackgroundJob.ParametersSnapshot != null)
                {
                    BackgroundJob.ParametersSnapshot.TryGetValue(name, out value);
                }
                else
                {
                    value = Connection.GetJobParameter(BackgroundJob.Id, name);                
                }

                return SerializationHelper.Deserialize<T>(value, SerializationOption.User);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                throw new InvalidOperationException(
                    $"Could not get a value of the job parameter `{name}`. See inner exception for details.", ex);
            }
        }
    }
}
