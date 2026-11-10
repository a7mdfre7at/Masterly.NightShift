namespace Masterly.NightShift;

/// <summary>
/// Creates background jobs from an arguments object, run by the <see cref="IJobHandler{TArgs}"/> registered for
/// its type. See <see cref="JobHandlers"/>.
/// </summary>
public static class BackgroundJobClientHandlerExtensions
{
    /// <param name="client">A job client instance.</param>
    extension([NotNull] IBackgroundJobClient client)
    {
        /// <summary>Creates a job for the handler of <typeparamref name="TArgs"/> and places it into its queue.</summary>
        /// <typeparam name="TArgs">The arguments type.</typeparam>
        /// <param name="args">The job's arguments.</param>
        /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
        /// <exception cref="InvalidOperationException">No handler is registered for <typeparamref name="TArgs"/>.</exception>
        public string? Enqueue<TArgs>([NotNull] TArgs args)
            where TArgs : notnull
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.Create(JobHandlers.CreateJob(args), new EnqueuedState());
        }

        /// <summary>Creates a job for the handler of <typeparamref name="TArgs"/> that runs after a delay.</summary>
        /// <typeparam name="TArgs">The arguments type.</typeparam>
        /// <param name="args">The job's arguments.</param>
        /// <param name="delay">How long to wait before the job is enqueued.</param>
        /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
        /// <exception cref="InvalidOperationException">No handler is registered for <typeparamref name="TArgs"/>.</exception>
        public string? Schedule<TArgs>([NotNull] TArgs args, TimeSpan delay)
            where TArgs : notnull
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.Create(JobHandlers.CreateJob(args), new ScheduledState(delay));
        }

        /// <summary>Creates a job for the handler of <typeparamref name="TArgs"/> that runs at a given moment.</summary>
        /// <typeparam name="TArgs">The arguments type.</typeparam>
        /// <param name="args">The job's arguments.</param>
        /// <param name="enqueueAt">The moment the job is enqueued.</param>
        /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
        /// <exception cref="InvalidOperationException">No handler is registered for <typeparamref name="TArgs"/>.</exception>
        public string? Schedule<TArgs>([NotNull] TArgs args, DateTimeOffset enqueueAt)
            where TArgs : notnull
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.Create(JobHandlers.CreateJob(args), new ScheduledState(enqueueAt.UtcDateTime));
        }

        /// <summary>
        /// Creates a job for the handler of <typeparamref name="TArgs"/> that runs after its parent succeeds.
        /// </summary>
        /// <typeparam name="TArgs">The arguments type.</typeparam>
        /// <param name="parentId">The identifier of the parent job.</param>
        /// <param name="args">The job's arguments.</param>
        /// <param name="options">When the continuation runs.</param>
        /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
        /// <exception cref="InvalidOperationException">No handler is registered for <typeparamref name="TArgs"/>.</exception>
        public string? ContinueJobWith<TArgs>(
            [NotNull] string parentId,
            [NotNull] TArgs args,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
            where TArgs : notnull
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.Create(JobHandlers.CreateJob(args), new AwaitingState(parentId, new EnqueuedState(), options));
        }

        /// <summary>Creates a job for <typeparamref name="THandler"/> and places it into its queue.</summary>
        /// <typeparam name="THandler">The handler to run. It doesn't need to be registered.</typeparam>
        /// <typeparam name="TArgs">The arguments type.</typeparam>
        /// <param name="args">The job's arguments.</param>
        /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
        /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
        public string? Enqueue<THandler, TArgs>([NotNull] TArgs args)
            where THandler : class, IJobHandler<TArgs>
            where TArgs : notnull
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.Create(JobHandlers.CreateJob<THandler, TArgs>(args), new EnqueuedState());
        }

        /// <summary>Creates a job for <typeparamref name="THandler"/> that runs after a delay.</summary>
        /// <typeparam name="THandler">The handler to run. It doesn't need to be registered.</typeparam>
        /// <typeparam name="TArgs">The arguments type.</typeparam>
        /// <param name="args">The job's arguments.</param>
        /// <param name="delay">How long to wait before the job is enqueued.</param>
        /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
        /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
        public string? Schedule<THandler, TArgs>([NotNull] TArgs args, TimeSpan delay)
            where THandler : class, IJobHandler<TArgs>
            where TArgs : notnull
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.Create(JobHandlers.CreateJob<THandler, TArgs>(args), new ScheduledState(delay));
        }

        /// <summary>Creates a job for <typeparamref name="THandler"/> that runs at a given moment.</summary>
        /// <typeparam name="THandler">The handler to run. It doesn't need to be registered.</typeparam>
        /// <typeparam name="TArgs">The arguments type.</typeparam>
        /// <param name="args">The job's arguments.</param>
        /// <param name="enqueueAt">The moment the job is enqueued.</param>
        /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
        /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
        public string? Schedule<THandler, TArgs>([NotNull] TArgs args, DateTimeOffset enqueueAt)
            where THandler : class, IJobHandler<TArgs>
            where TArgs : notnull
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.Create(JobHandlers.CreateJob<THandler, TArgs>(args), new ScheduledState(enqueueAt.UtcDateTime));
        }

        /// <summary>Creates a job for <typeparamref name="THandler"/> that runs after its parent succeeds.</summary>
        /// <typeparam name="THandler">The handler to run. It doesn't need to be registered.</typeparam>
        /// <typeparam name="TArgs">The arguments type.</typeparam>
        /// <param name="parentId">The identifier of the parent job.</param>
        /// <param name="args">The job's arguments.</param>
        /// <param name="options">When the continuation runs.</param>
        /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
        /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
        public string? ContinueJobWith<THandler, TArgs>([NotNull] string parentId, [NotNull] TArgs args, JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
            where THandler : class, IJobHandler<TArgs>
            where TArgs : notnull
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.Create(JobHandlers.CreateJob<THandler, TArgs>(args), new AwaitingState(parentId, new EnqueuedState(), options));
        }

        /// <summary>Creates a job for <typeparamref name="TJob"/> and places it into its queue.</summary>
        /// <typeparam name="TJob">The job class. It doesn't need to be registered.</typeparam>
        /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
        /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
        public string? Enqueue<TJob>()
            where TJob : class, IJob
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.Create(JobHandlers.CreateJob<TJob>(), new EnqueuedState());
        }

        /// <summary>Creates a job for <typeparamref name="TJob"/> that runs after a delay.</summary>
        /// <typeparam name="TJob">The job class. It doesn't need to be registered.</typeparam>
        /// <param name="delay">How long to wait before the job is enqueued.</param>
        /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
        /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
        public string? Schedule<TJob>(TimeSpan delay)
            where TJob : class, IJob
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.Create(JobHandlers.CreateJob<TJob>(), new ScheduledState(delay));
        }

        /// <summary>Creates a job for <typeparamref name="TJob"/> that runs at a given moment.</summary>
        /// <typeparam name="TJob">The job class. It doesn't need to be registered.</typeparam>
        /// <param name="enqueueAt">The moment the job is enqueued.</param>
        /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
        /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
        public string? Schedule<TJob>(DateTimeOffset enqueueAt)
            where TJob : class, IJob
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.Create(JobHandlers.CreateJob<TJob>(), new ScheduledState(enqueueAt.UtcDateTime));
        }

        /// <summary>Creates a job for <typeparamref name="TJob"/> that runs after its parent succeeds.</summary>
        /// <typeparam name="TJob">The job class. It doesn't need to be registered.</typeparam>
        /// <param name="parentId">The identifier of the parent job.</param>
        /// <param name="options">When the continuation runs.</param>
        /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
        /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
        public string? ContinueJobWith<TJob>([NotNull] string parentId, JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
            where TJob : class, IJob
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.Create(JobHandlers.CreateJob<TJob>(), new AwaitingState(parentId, new EnqueuedState(), options));
        }
    }
}
