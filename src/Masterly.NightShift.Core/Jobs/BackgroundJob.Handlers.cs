namespace Masterly.NightShift;

public partial class BackgroundJob
{
    /// <summary>Creates a job for the handler of <typeparamref name="TArgs"/> and places it into its queue.</summary>
    /// <typeparam name="TArgs">The arguments type.</typeparam>
    /// <param name="args">The job's arguments.</param>
    /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
    /// <exception cref="InvalidOperationException">No handler is registered for <typeparamref name="TArgs"/>.</exception>
    public static string? Enqueue<TArgs>([NotNull] TArgs args)
        where TArgs : notnull
    {
        return ClientFactory().Enqueue(args);
    }

    /// <summary>Creates a job for the handler of <typeparamref name="TArgs"/> that runs after a delay.</summary>
    /// <typeparam name="TArgs">The arguments type.</typeparam>
    /// <param name="args">The job's arguments.</param>
    /// <param name="delay">How long to wait before the job is enqueued.</param>
    /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
    /// <exception cref="InvalidOperationException">No handler is registered for <typeparamref name="TArgs"/>.</exception>
    public static string? Schedule<TArgs>([NotNull] TArgs args, TimeSpan delay)
        where TArgs : notnull
    {
        return ClientFactory().Schedule(args, delay);
    }

    /// <summary>Creates a job for the handler of <typeparamref name="TArgs"/> that runs at a given moment.</summary>
    /// <typeparam name="TArgs">The arguments type.</typeparam>
    /// <param name="args">The job's arguments.</param>
    /// <param name="enqueueAt">The moment the job is enqueued.</param>
    /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
    /// <exception cref="InvalidOperationException">No handler is registered for <typeparamref name="TArgs"/>.</exception>
    public static string? Schedule<TArgs>([NotNull] TArgs args, DateTimeOffset enqueueAt)
        where TArgs : notnull
    {
        return ClientFactory().Schedule(args, enqueueAt);
    }

    /// <summary>Creates a job for the handler of <typeparamref name="TArgs"/> that runs after its parent succeeds.</summary>
    /// <typeparam name="TArgs">The arguments type.</typeparam>
    /// <param name="parentId">The identifier of the parent job.</param>
    /// <param name="args">The job's arguments.</param>
    /// <param name="options">When the continuation runs.</param>
    /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
    /// <exception cref="InvalidOperationException">No handler is registered for <typeparamref name="TArgs"/>.</exception>
    public static string? ContinueJobWith<TArgs>(
        [NotNull] string parentId,
        [NotNull] TArgs args,
        JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        where TArgs : notnull
    {
        return ClientFactory().ContinueJobWith(parentId, args, options);
    }

    /// <summary>Creates a job for <typeparamref name="THandler"/> and places it into its queue.</summary>
    /// <typeparam name="THandler">The handler to run. It doesn't need to be registered.</typeparam>
    /// <typeparam name="TArgs">The arguments type.</typeparam>
    /// <param name="args">The job's arguments.</param>
    /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
    /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
    public static string? Enqueue<THandler, TArgs>([NotNull] TArgs args)
        where THandler : class, IJobHandler<TArgs>
        where TArgs : notnull
    {
        return ClientFactory().Enqueue<THandler, TArgs>(args);
    }

    /// <summary>Creates a job for <typeparamref name="THandler"/> that runs after a delay.</summary>
    /// <typeparam name="THandler">The handler to run. It doesn't need to be registered.</typeparam>
    /// <typeparam name="TArgs">The arguments type.</typeparam>
    /// <param name="args">The job's arguments.</param>
    /// <param name="delay">How long to wait before the job is enqueued.</param>
    /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
    /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
    public static string? Schedule<THandler, TArgs>([NotNull] TArgs args, TimeSpan delay)
        where THandler : class, IJobHandler<TArgs>
        where TArgs : notnull
    {
        return ClientFactory().Schedule<THandler, TArgs>(args, delay);
    }

    /// <summary>Creates a job for <typeparamref name="THandler"/> that runs at a given moment.</summary>
    /// <typeparam name="THandler">The handler to run. It doesn't need to be registered.</typeparam>
    /// <typeparam name="TArgs">The arguments type.</typeparam>
    /// <param name="args">The job's arguments.</param>
    /// <param name="enqueueAt">The moment the job is enqueued.</param>
    /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
    /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
    public static string? Schedule<THandler, TArgs>([NotNull] TArgs args, DateTimeOffset enqueueAt)
        where THandler : class, IJobHandler<TArgs>
        where TArgs : notnull
    {
        return ClientFactory().Schedule<THandler, TArgs>(args, enqueueAt);
    }

    /// <summary>Creates a job for <typeparamref name="THandler"/> that runs after its parent succeeds.</summary>
    /// <typeparam name="THandler">The handler to run. It doesn't need to be registered.</typeparam>
    /// <typeparam name="TArgs">The arguments type.</typeparam>
    /// <param name="parentId">The identifier of the parent job.</param>
    /// <param name="args">The job's arguments.</param>
    /// <param name="options">When the continuation runs.</param>
    /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
    /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
    public static string? ContinueJobWith<THandler, TArgs>([NotNull] string parentId, [NotNull] TArgs args, JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        where THandler : class, IJobHandler<TArgs>
        where TArgs : notnull
    {
        return ClientFactory().ContinueJobWith<THandler, TArgs>(parentId, args, options);
    }

    /// <summary>Creates a job for <typeparamref name="TJob"/> and places it into its queue.</summary>
    /// <typeparam name="TJob">The job class. It doesn't need to be registered.</typeparam>
    /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
    /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
    public static string? Enqueue<TJob>()
        where TJob : class, IJob
    {
        return ClientFactory().Enqueue<TJob>();
    }

    /// <summary>Creates a job for <typeparamref name="TJob"/> that runs after a delay.</summary>
    /// <typeparam name="TJob">The job class. It doesn't need to be registered.</typeparam>
    /// <param name="delay">How long to wait before the job is enqueued.</param>
    /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
    /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
    public static string? Schedule<TJob>(TimeSpan delay)
        where TJob : class, IJob
    {
        return ClientFactory().Schedule<TJob>(delay);
    }

    /// <summary>Creates a job for <typeparamref name="TJob"/> that runs at a given moment.</summary>
    /// <typeparam name="TJob">The job class. It doesn't need to be registered.</typeparam>
    /// <param name="enqueueAt">The moment the job is enqueued.</param>
    /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
    /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
    public static string? Schedule<TJob>(DateTimeOffset enqueueAt)
        where TJob : class, IJob
    {
        return ClientFactory().Schedule<TJob>(enqueueAt);
    }

    /// <summary>Creates a job for <typeparamref name="TJob"/> that runs after its parent succeeds.</summary>
    /// <typeparam name="TJob">The job class. It doesn't need to be registered.</typeparam>
    /// <param name="parentId">The identifier of the parent job.</param>
    /// <param name="options">When the continuation runs.</param>
    /// <returns>The job's identifier, or <c>null</c> when a filter canceled the creation.</returns>
    /// <exception cref="ArgumentException">The type can't be used as a job class.</exception>
    public static string? ContinueJobWith<TJob>([NotNull] string parentId, JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        where TJob : class, IJob
    {
        return ClientFactory().ContinueJobWith<TJob>(parentId, options);
    }
}
