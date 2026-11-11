namespace Masterly.NightShift;

/// <summary>
/// Creates jobs of a <see cref="JobFunction"/> by name, with a payload: <c>client.EnqueueFunction("maintenance.cleanup", new { Days = 30 })</c>.
/// The payload is checked against the function when the job is created.
/// </summary>
public static class BackgroundJobClientFunctionExtensions
{
    /// <param name="client">The client.</param>
    extension([NotNull] IBackgroundJobClient client)
    {
        /// <summary>Creates a fire-and-forget job of a function.</summary>
        /// <param name="name">The function's name.</param>
        /// <param name="payload">The payload, see <see cref="JobFunction.CreateJob"/>.</param>
        /// <param name="options">The schedule policy, late-start limit and queue, or <c>null</c>.</param>
        /// <returns>The job's id, or <c>null</c> when a filter or the policy didn't let it be created.</returns>
        /// <exception cref="ArgumentException">There is no function of this name, or the payload doesn't fit it.</exception>
        public string? EnqueueFunction([NotNull] string name, [CanBeNull] object? payload = null, [CanBeNull] ScheduleOptions? options = null)
        {
            return Create(client, name, payload, new EnqueuedState(), options);
        }

        /// <summary>Creates a job of a function that runs after a delay.</summary>
        /// <inheritdoc cref="EnqueueFunction(IBackgroundJobClient, string, object, ScheduleOptions)"/>
        public string? ScheduleFunction([NotNull] string name, [CanBeNull] object? payload, TimeSpan delay, [CanBeNull] ScheduleOptions? options = null)
        {
            return Create(client, name, payload, new ScheduledState(delay), options);
        }

        /// <summary>Creates a job of a function due at a moment.</summary>
        /// <inheritdoc cref="EnqueueFunction(IBackgroundJobClient, string, object, ScheduleOptions)"/>
        public string? ScheduleFunction([NotNull] string name, [CanBeNull] object? payload, DateTimeOffset enqueueAt, [CanBeNull] ScheduleOptions? options = null)
        {
            return Create(client, name, payload, new ScheduledState(enqueueAt.UtcDateTime), options);
        }
    }

    private static string? Create(IBackgroundJobClient client, string name, object? payload, IState state, ScheduleOptions? options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(name);

        Job job = JobFunctions.CreateJob(name, payload, options?.Queue);
        if (options == null) return client.Create(job, state);

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

/// <summary>Recurring jobs that run a <see cref="JobFunction"/> by name.</summary>
public static class RecurringJobFunctions
{
    /// <param name="manager">The manager.</param>
    extension([NotNull] IRecurringJobManager manager)
    {
        /// <summary>Registers or updates a recurring job that runs a function with a payload.</summary>
        /// <param name="recurringJobId">The recurring job's id.</param>
        /// <param name="name">The function's name.</param>
        /// <param name="payload">The payload, checked now.</param>
        /// <param name="cronExpression">The schedule.</param>
        /// <param name="options">The recurring job's options, or <c>null</c>.</param>
        /// <exception cref="ArgumentException">There is no function of this name, or the payload doesn't fit it.</exception>
        public void AddOrUpdateFunction(
            [NotNull] string recurringJobId,
            [NotNull] string name,
            [CanBeNull] object? payload,
            [NotNull] string cronExpression,
            [CanBeNull] RecurringJobOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(manager);
            manager.AddOrUpdate(recurringJobId, JobFunctions.CreateJob(name, payload, null), cronExpression, options ?? new RecurringJobOptions());
        }
    }
}

public partial class BackgroundJob
{
    /// <summary>Creates a fire-and-forget job of a function. See <see cref="BackgroundJobClientFunctionExtensions"/>.</summary>
    public static string? EnqueueFunction([NotNull] string name, [CanBeNull] object? payload = null, [CanBeNull] ScheduleOptions? options = null)
    {
        return ClientFactory().EnqueueFunction(name, payload, options);
    }

    /// <summary>Creates a job of a function that runs after a delay. See <see cref="BackgroundJobClientFunctionExtensions"/>.</summary>
    public static string? ScheduleFunction([NotNull] string name, [CanBeNull] object? payload, TimeSpan delay, [CanBeNull] ScheduleOptions? options = null)
    {
        return ClientFactory().ScheduleFunction(name, payload, delay, options);
    }

    /// <summary>Creates a job of a function due at a moment. See <see cref="BackgroundJobClientFunctionExtensions"/>.</summary>
    public static string? ScheduleFunction([NotNull] string name, [CanBeNull] object? payload, DateTimeOffset enqueueAt, [CanBeNull] ScheduleOptions? options = null)
    {
        return ClientFactory().ScheduleFunction(name, payload, enqueueAt, options);
    }
}
