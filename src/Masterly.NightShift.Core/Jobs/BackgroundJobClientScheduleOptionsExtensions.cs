using System.Linq.Expressions;

namespace Masterly.NightShift;

/// <summary>
/// Creates fire-and-forget and delayed jobs with <see cref="ScheduleOptions"/>: a schedule policy, a late-start
/// limit and a queue.
/// </summary>
public static class BackgroundJobClientScheduleOptionsExtensions
{
    /// <param name="client">The client.</param>
    extension([NotNull] IBackgroundJobClient client)
    {
        /// <summary>
        /// Creates a fire-and-forget job with options: the schedule policy it follows, how late it may start and its
        /// queue. A policy that blocks the current time moves the job forward, skips it or rejects it, as
        /// <see cref="ScheduleOptions.WhenBlocked"/> says.
        /// </summary>
        /// <param name="methodCall">The method call to run.</param>
        /// <param name="options">The job's options.</param>
        /// <returns>The job's id, or <c>null</c> when a filter or the policy didn't let it be created.</returns>
        /// <exception cref="NotSupportedException">
        /// The client doesn't implement <see cref="IBackgroundJobClientV2"/>, or <see cref="ScheduleOptions.Queue"/>
        /// is set and the storage can't keep a job's queue.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// There is no policy of the given name, or it allows no time in the next ten years.
        /// </exception>
        public string? Enqueue(
            [NotNull, InstantHandle] Expression<Action> methodCall,
            [NotNull] ScheduleOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(client, Job.FromExpression(methodCall, options.Queue), new EnqueuedState(), options);
        }

        /// <inheritdoc cref="Enqueue(IBackgroundJobClient, Expression{Action}, ScheduleOptions)"/>
        public string? Enqueue(
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            [NotNull] ScheduleOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(client, Job.FromExpression(methodCall, options.Queue), new EnqueuedState(), options);
        }

        /// <inheritdoc cref="Enqueue(IBackgroundJobClient, Expression{Action}, ScheduleOptions)"/>
        public string? Enqueue<T>(
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            [NotNull] ScheduleOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(client, Job.FromExpression(methodCall, options.Queue), new EnqueuedState(), options);
        }

        /// <inheritdoc cref="Enqueue(IBackgroundJobClient, Expression{Action}, ScheduleOptions)"/>
        public string? Enqueue<T>(
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            [NotNull] ScheduleOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(client, Job.FromExpression(methodCall, options.Queue), new EnqueuedState(), options);
        }

        /// <summary>
        /// Creates a job that runs after a delay, with options: the schedule policy it follows, how late it may start
        /// and its queue. A policy that blocks the moment it is due moves the job forward, skips it or rejects it, as
        /// <see cref="ScheduleOptions.WhenBlocked"/> says.
        /// </summary>
        /// <param name="methodCall">The method call to run.</param>
        /// <param name="delay">How long after now the job is due.</param>
        /// <param name="options">The job's options.</param>
        /// <returns>The job's id, or <c>null</c> when a filter or the policy didn't let it be created.</returns>
        /// <exception cref="NotSupportedException">
        /// The client doesn't implement <see cref="IBackgroundJobClientV2"/>, or <see cref="ScheduleOptions.Queue"/>
        /// is set and the storage can't keep a job's queue.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// There is no policy of the given name, or it allows no time in the next ten years.
        /// </exception>
        public string? Schedule(
            [NotNull, InstantHandle] Expression<Action> methodCall,
            TimeSpan delay,
            [NotNull] ScheduleOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(client, Job.FromExpression(methodCall, options.Queue), new ScheduledState(delay), options);
        }

        /// <inheritdoc cref="Schedule(IBackgroundJobClient, Expression{Action}, TimeSpan, ScheduleOptions)"/>
        public string? Schedule(
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            TimeSpan delay,
            [NotNull] ScheduleOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(client, Job.FromExpression(methodCall, options.Queue), new ScheduledState(delay), options);
        }

        /// <inheritdoc cref="Schedule(IBackgroundJobClient, Expression{Action}, TimeSpan, ScheduleOptions)"/>
        public string? Schedule<T>(
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            TimeSpan delay,
            [NotNull] ScheduleOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(client, Job.FromExpression(methodCall, options.Queue), new ScheduledState(delay), options);
        }

        /// <inheritdoc cref="Schedule(IBackgroundJobClient, Expression{Action}, TimeSpan, ScheduleOptions)"/>
        public string? Schedule<T>(
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            TimeSpan delay,
            [NotNull] ScheduleOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(client, Job.FromExpression(methodCall, options.Queue), new ScheduledState(delay), options);
        }

        /// <summary>
        /// Creates a job due at a given moment, with options: the schedule policy it follows, how late it may start
        /// and its queue. A policy that blocks that moment moves the job forward, skips it or rejects it, as
        /// <see cref="ScheduleOptions.WhenBlocked"/> says.
        /// </summary>
        /// <param name="methodCall">The method call to run.</param>
        /// <param name="enqueueAt">When the job is due.</param>
        /// <param name="options">The job's options.</param>
        /// <returns>The job's id, or <c>null</c> when a filter or the policy didn't let it be created.</returns>
        /// <exception cref="NotSupportedException">
        /// The client doesn't implement <see cref="IBackgroundJobClientV2"/>, or <see cref="ScheduleOptions.Queue"/>
        /// is set and the storage can't keep a job's queue.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// There is no policy of the given name, or it allows no time in the next ten years.
        /// </exception>
        public string? Schedule(
            [NotNull, InstantHandle] Expression<Action> methodCall,
            DateTimeOffset enqueueAt,
            [NotNull] ScheduleOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(client, Job.FromExpression(methodCall, options.Queue), new ScheduledState(enqueueAt.UtcDateTime), options);
        }

        /// <inheritdoc cref="Schedule(IBackgroundJobClient, Expression{Action}, DateTimeOffset, ScheduleOptions)"/>
        public string? Schedule(
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            DateTimeOffset enqueueAt,
            [NotNull] ScheduleOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(client, Job.FromExpression(methodCall, options.Queue), new ScheduledState(enqueueAt.UtcDateTime), options);
        }

        /// <inheritdoc cref="Schedule(IBackgroundJobClient, Expression{Action}, DateTimeOffset, ScheduleOptions)"/>
        public string? Schedule<T>(
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            DateTimeOffset enqueueAt,
            [NotNull] ScheduleOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(client, Job.FromExpression(methodCall, options.Queue), new ScheduledState(enqueueAt.UtcDateTime), options);
        }

        /// <inheritdoc cref="Schedule(IBackgroundJobClient, Expression{Action}, DateTimeOffset, ScheduleOptions)"/>
        public string? Schedule<T>(
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            DateTimeOffset enqueueAt,
            [NotNull] ScheduleOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Create(client, Job.FromExpression(methodCall, options.Queue), new ScheduledState(enqueueAt.UtcDateTime), options);
        }
    }

    private static string? Create(IBackgroundJobClient client, Job job, IState state, ScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (client is not IBackgroundJobClientV2 clientV2)
        {
            throw new NotSupportedException("ScheduleOptions need a client that implements IBackgroundJobClientV2, as BackgroundJobClient does, to store the job parameters.");
        }

        if (options.Queue != null && !clientV2.Storage.HasFeature(JobStorageFeatures.JobQueueProperty))
        {
            throw new NotSupportedException("The current storage can't keep the queue of a job. Use the QueueAttribute instead of ScheduleOptions.Queue.");
        }

        return clientV2.Create(job, state, options.ToParameters());
    }
}
