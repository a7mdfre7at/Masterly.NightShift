using System.Linq.Expressions;

namespace Masterly.NightShift;

/// <summary>
/// Fan-in continuations: a job that waits for a whole set of parent jobs (<c>ContinueJobWithAll</c>) or
/// for the first of them (<c>ContinueJobWithAny</c>). Parent identifiers that are <c>null</c> (jobs whose
/// creation was canceled by a filter) are skipped; at least one real parent is required.
/// </summary>
public static class BackgroundJobClientFanInExtensions
{
    extension([NotNull] IBackgroundJobClient client)
    {
        /// <summary>
        /// Creates a job that waits for a set of parents and starts as the join <paramref name="mode"/> says: once all
        /// of them succeeded (<see cref="FanInMode.All"/>), at the first success (<see cref="FanInMode.Any"/>), at the
        /// first failure for good (<see cref="FanInMode.AnyFailed"/>), at the first failure or deletion
        /// (<see cref="FanInMode.AnyFailedOrCanceled"/>), or once all of them finished whatever the outcome
        /// (<see cref="FanInMode.AllCompleted"/>). Parent identifiers that are <c>null</c> are skipped.
        /// </summary>
        /// <param name="parentIds">The parent jobs, at least one.</param>
        /// <param name="mode">When the job starts.</param>
        /// <param name="methodCall">The method call to run.</param>
        /// <returns>The job's id, or <c>null</c> when a client filter canceled its creation.</returns>
        public string? ContinueJobWithJoin(
            [NotNull] IEnumerable<string?> parentIds,
            FanInMode mode,
            [NotNull, InstantHandle] Expression<Action> methodCall)
        {
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a FanInMode value.");
            }

            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, mode);
        }

        /// <inheritdoc cref="ContinueJobWithJoin(IBackgroundJobClient, IEnumerable{string}, FanInMode, Expression{Action})"/>
        public string? ContinueJobWithJoin(
            [NotNull] IEnumerable<string?> parentIds,
            FanInMode mode,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall)
        {
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a FanInMode value.");
            }

            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, mode);
        }

        /// <inheritdoc cref="ContinueJobWithJoin(IBackgroundJobClient, IEnumerable{string}, FanInMode, Expression{Action})"/>
        public string? ContinueJobWithJoin<T>(
            [NotNull] IEnumerable<string?> parentIds,
            FanInMode mode,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall)
        {
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a FanInMode value.");
            }

            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, mode);
        }

        /// <inheritdoc cref="ContinueJobWithJoin(IBackgroundJobClient, IEnumerable{string}, FanInMode, Expression{Action})"/>
        public string? ContinueJobWithJoin<T>(
            [NotNull] IEnumerable<string?> parentIds,
            FanInMode mode,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall)
        {
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a FanInMode value.");
            }

            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, mode);
        }

        /// <summary>Creates a job that starts once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAll(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ContinueJobWithAll(client, parentIds, methodCall, new EnqueuedState(), options);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAll(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            [NotNull] IState nextState,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), nextState, options, FanInMode.All);
        }

        /// <summary>
        /// Creates a job for the specified queue that starts, in <paramref name="nextState"/> (<see cref="EnqueuedState"/>
        /// when <c>null</c>), once <em>every</em> parent has finished as <paramref name="options"/> require.
        /// </summary>
        public string? ContinueJobWithAll(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            ArgumentNullException.ThrowIfNull(queue);
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAll(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAll(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            [NotNull] IState nextState,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), nextState, options, FanInMode.All);
        }

        /// <summary>
        /// Creates a job for the specified queue that starts, in <paramref name="nextState"/> (<see cref="EnqueuedState"/>
        /// when <c>null</c>), once <em>every</em> parent has finished as <paramref name="options"/> require.
        /// </summary>
        public string? ContinueJobWithAll(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            ArgumentNullException.ThrowIfNull(queue);
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAll<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAll<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            [NotNull] IState nextState,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), nextState, options, FanInMode.All);
        }

        /// <summary>
        /// Creates a job for the specified queue that starts, in <paramref name="nextState"/> (<see cref="EnqueuedState"/>
        /// when <c>null</c>), once <em>every</em> parent has finished as <paramref name="options"/> require.
        /// </summary>
        public string? ContinueJobWithAll<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            ArgumentNullException.ThrowIfNull(queue);
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAll<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAll<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            [NotNull] IState nextState,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), nextState, options, FanInMode.All);
        }

        /// <summary>
        /// Creates a job for the specified queue that starts, in <paramref name="nextState"/> (<see cref="EnqueuedState"/>
        /// when <c>null</c>), once <em>every</em> parent has finished as <paramref name="options"/> require.
        /// </summary>
        public string? ContinueJobWithAll<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            ArgumentNullException.ThrowIfNull(queue);
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAny(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAny(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            [NotNull] IState nextState,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), nextState, options, FanInMode.Any);
        }

        /// <summary>
        /// Creates a job for the specified queue that starts, in <paramref name="nextState"/> (<see cref="EnqueuedState"/>
        /// when <c>null</c>), as soon as <em>one</em> parent has finished as <paramref name="options"/> require.
        /// </summary>
        public string? ContinueJobWithAny(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            ArgumentNullException.ThrowIfNull(queue);
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAny(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAny(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            [NotNull] IState nextState,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), nextState, options, FanInMode.Any);
        }

        /// <summary>
        /// Creates a job for the specified queue that starts, in <paramref name="nextState"/> (<see cref="EnqueuedState"/>
        /// when <c>null</c>), as soon as <em>one</em> parent has finished as <paramref name="options"/> require.
        /// </summary>
        public string? ContinueJobWithAny(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            ArgumentNullException.ThrowIfNull(queue);
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAny<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAny<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            [NotNull] IState nextState,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), nextState, options, FanInMode.Any);
        }

        /// <summary>
        /// Creates a job for the specified queue that starts, in <paramref name="nextState"/> (<see cref="EnqueuedState"/>
        /// when <c>null</c>), as soon as <em>one</em> parent has finished as <paramref name="options"/> require.
        /// </summary>
        public string? ContinueJobWithAny<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            ArgumentNullException.ThrowIfNull(queue);
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAny<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public string? ContinueJobWithAny<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            [NotNull] IState nextState,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), nextState, options, FanInMode.Any);
        }

        /// <summary>
        /// Creates a job for the specified queue that starts, in <paramref name="nextState"/> (<see cref="EnqueuedState"/>
        /// when <c>null</c>), as soon as <em>one</em> parent has finished as <paramref name="options"/> require.
        /// </summary>
        public string? ContinueJobWithAny<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            ArgumentNullException.ThrowIfNull(queue);
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.Any);
        }
    }

    private static string? Create(
        IBackgroundJobClient client,
        IEnumerable<string?> parentIds,
        Job job,
        IState nextState,
        JobContinuationOptions options,
        FanInMode mode)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(parentIds);
        ArgumentNullException.ThrowIfNull(nextState);

        List<string> parents = [];
        foreach (string? parentId in parentIds)
        {
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                parents.Add(parentId);
            }
        }

        if (parents.Count == 0)
        {
            throw new ArgumentException("At least one parent job identifier is required; all the given ones were null or empty.", nameof(parentIds));
        }

        return client.Create(job, new AwaitingJobsState(parents, nextState, options, mode));
    }
}
