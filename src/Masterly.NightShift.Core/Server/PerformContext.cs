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

using Masterly.NightShift.Profiling;

namespace Masterly.NightShift.Server;

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
        StartedAt = context.StartedAt;
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
        StartedAt = JobClock.UtcNow;

        Items = items ?? new Dictionary<string, object>();
    }

    /// <summary>
    /// The job parameter with the moment a delayed job was scheduled for, as a Unix time in milliseconds. Written
    /// when the job is created in the <see cref="ScheduledState"/>.
    /// </summary>
    public const string ScheduledForParameter = "ScheduledFor";

    /// <summary>
    /// The job parameter with the moment a continuation became due, as a Unix time in milliseconds: when it left the
    /// Awaiting state, or the time its next state scheduled it for. Written by <see cref="ContinuationsSupportAttribute"/>.
    /// </summary>
    internal const string DueAtParameter = "DueAt";

    /// <summary>The job parameter with the occurrence of the recurring job that created the job, as a Unix time in seconds.</summary>
    internal const string RecurringTimeParameter = "Time";

    /// <summary>The job parameter with the identifier of the recurring job that created the job.</summary>
    internal const string RecurringJobIdParameter = "RecurringJobId";

    /// <summary>The job parameter with the parent of a continuation, written when it is created.</summary>
    public const string ParentIdParameter = "ParentId";

    /// <summary>The job parameter with the parents of a fan-in continuation, written when it is created.</summary>
    public const string ParentIdsParameter = "ParentIds";

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
    /// Gets how many times the job has been retried by <see cref="AutomaticRetryAttribute"/>: 0 on its first run.
    /// </summary>
    public int RetryCount => GetJobParameter<int>(AutomaticRetryAttribute.RetryCountParameter, allowStale: true);

    /// <summary>
    /// Gets when this run of the job started, in UTC: when the server created the context, just before the
    /// server filters and the job method run.
    /// </summary>
    public DateTime StartedAt { get; }

    /// <summary>
    /// Gets the moment the job was meant to run, in UTC: the time a delayed job was scheduled for, or the
    /// occurrence of the recurring job that created it. <c>null</c> for any other job, such as a fire-and-forget
    /// job or a continuation. Retries don't change it.
    /// </summary>
    /// <remarks>Read from the parameters the worker loaded with the job, so it costs no storage round trip.</remarks>
    public DateTime? ScheduledFor
    {
        get
        {
            long? scheduledFor = GetJobParameter<long?>(ScheduledForParameter, allowStale: true);
            if (scheduledFor.HasValue)
            {
                return JobHelper.FromMillisecondTimestamp(scheduledFor.Value);
            }

            long? occurrence = GetJobParameter<long?>(RecurringTimeParameter, allowStale: true);
            return occurrence.HasValue ? JobHelper.FromTimestamp(occurrence.Value) : null;
        }
    }

    /// <summary>
    /// Gets how late this run started: the time from <see cref="ScheduledFor"/> to <see cref="StartedAt"/>. A
    /// continuation without <see cref="ScheduledFor"/> is measured from the moment it became due, when its parents let
    /// it leave the Awaiting state, so the time its parents waited and ran doesn't count; any other job without
    /// <see cref="ScheduledFor"/> from its creation. Never negative. A retry is measured from the same moment, so its
    /// lateness includes the earlier attempts. The time the job was held, after that moment, because the server that
    /// fetched it was paused on its own or fenced is left out (<see cref="DrainModeFilter.ServerHoldParameter"/>):
    /// the servers that may work could have run it meanwhile. A hold before that moment isn't lateness to begin
    /// with, also when the job was moved to a later time after it. The time it was held by the cluster-wide switch
    /// or a paused queue counts.
    /// </summary>
    public TimeSpan Lateness => GetLateness(ScheduledFor);

    /// <summary>
    /// The lateness measured from <paramref name="scheduledFor"/>, the value of <see cref="ScheduledFor"/> the caller
    /// has already read, or, when it is <c>null</c>, from the moment the job became due or was created.
    /// </summary>
    internal TimeSpan GetLateness(DateTime? scheduledFor)
    {
        DateTime anchor = scheduledFor ?? DueAt ?? BackgroundJob.CreatedAt;
        TimeSpan lateness = StartedAt - anchor - ServerHold.GetHeldTime(BackgroundJob, anchor, StartedAt);
        return lateness > TimeSpan.Zero ? lateness : TimeSpan.Zero;
    }

    // When a continuation left the Awaiting state, see DueAtParameter
    private DateTime? DueAt
    {
        get
        {
            long? dueAt = GetJobParameter<long?>(DueAtParameter, allowStale: true);
            return dueAt.HasValue ? JobHelper.FromMillisecondTimestamp(dueAt.Value) : null;
        }
    }

    /// <summary>Gets the identifier of the recurring job that created the job, or <c>null</c>.</summary>
    [CanBeNull]
    public string? RecurringJobId => GetJobParameter<string>(RecurringJobIdParameter, allowStale: true);

    /// <summary>
    /// Gets the parent of a continuation (<c>ContinueJobWith</c>, a chain step after a single step), or <c>null</c>
    /// for a job that isn't one, a fan-in continuation (see <see cref="ParentIds"/>) or a continuation created by an
    /// earlier version.
    /// </summary>
    [CanBeNull]
    public string? ParentId => GetJobParameter<string>(ParentIdParameter, allowStale: true);

    /// <summary>
    /// Gets the parents of a continuation: the parents of a fan-in continuation (<c>ContinueJobWithAll</c>,
    /// <c>ContinueJobWithAny</c>, a chain step after a group), the single parent of another continuation, or an
    /// empty list.
    /// </summary>
    [NotNull]
    public IReadOnlyList<string> ParentIds
    {
        get
        {
            string[]? parents = GetJobParameter<string[]>(ParentIdsParameter, allowStale: true);
            if (parents is { Length: > 0 })
            {
                return parents;
            }

            return ParentId is { } parentId ? [parentId] : [];
        }
    }

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
                if (Items.TryGetValue(ConsoleItemKey, out object? existing) && existing is IJobConsole console)
                {
                    return console;
                }

                JobConsole created = new(this);
                Items[ConsoleItemKey] = created;
                return created;
            }
        }
    }

    internal const string ConsoleItemKey = "X_HF_Console";

    public void SetJobParameter([NotNull] string name, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Connection.SetJobParameter(BackgroundJob.Id, name, SerializationHelper.Serialize(value, SerializationOption.User));
    }

    public T? GetJobParameter<T>([NotNull] string name) => GetJobParameter<T>(name, allowStale: false);

    public T? GetJobParameter<T>([NotNull] string name, bool allowStale)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

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
