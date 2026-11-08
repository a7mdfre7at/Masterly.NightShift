#nullable enable

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;

namespace Masterly.NightShift
{
    /// <summary>
    /// Fan-in continuations: a job that waits for a whole set of parent jobs (<c>ContinueJobWithAll</c>) or
    /// for the first of them (<c>ContinueJobWithAny</c>). Parent identifiers that are <c>null</c> (jobs whose
    /// creation was canceled by a filter) are skipped; at least one real parent is required.
    /// </summary>
    public static class BackgroundJobClientFanInExtensions
    {
        /// <summary>Creates a job that starts once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAll(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ContinueJobWithAll(client, parentIds, methodCall, new EnqueuedState(), options);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAll(
            [NotNull] this IBackgroundJobClient client,
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
        public static string? ContinueJobWithAll(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAll(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAll(
            [NotNull] this IBackgroundJobClient client,
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
        public static string? ContinueJobWithAll(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAll<T>(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAll<T>(
            [NotNull] this IBackgroundJobClient client,
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
        public static string? ContinueJobWithAll<T>(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAll<T>(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, once <em>every</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAll<T>(
            [NotNull] this IBackgroundJobClient client,
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
        public static string? ContinueJobWithAll<T>(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.All);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAny(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAny(
            [NotNull] this IBackgroundJobClient client,
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
        public static string? ContinueJobWithAny(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAny(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAny(
            [NotNull] this IBackgroundJobClient client,
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
        public static string? ContinueJobWithAny(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAny<T>(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAny<T>(
            [NotNull] this IBackgroundJobClient client,
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
        public static string? ContinueJobWithAny<T>(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAny<T>(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return Create(client, parentIds, Job.FromExpression(methodCall), new EnqueuedState(), options, FanInMode.Any);
        }

        /// <summary>Creates a job that starts, in <paramref name="nextState"/>, as soon as <em>one</em> parent has finished as <paramref name="options"/> require.</summary>
        public static string? ContinueJobWithAny<T>(
            [NotNull] this IBackgroundJobClient client,
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
        public static string? ContinueJobWithAny<T>(
            [NotNull] this IBackgroundJobClient client,
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            [CanBeNull] IState? nextState = null,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            return Create(client, parentIds, Job.FromExpression(methodCall, queue), nextState ?? new EnqueuedState(), options, FanInMode.Any);
        }

        private static string? Create(
            IBackgroundJobClient client,
            IEnumerable<string?> parentIds,
            Job job,
            IState nextState,
            JobContinuationOptions options,
            FanInMode mode)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (parentIds == null) throw new ArgumentNullException(nameof(parentIds));
            if (nextState == null) throw new ArgumentNullException(nameof(nextState));

            var parents = new List<string>();
            foreach (var parentId in parentIds)
            {
                if (!String.IsNullOrWhiteSpace(parentId)) parents.Add(parentId!);
            }

            if (parents.Count == 0)
            {
                throw new ArgumentException("At least one parent job identifier is required; all the given ones were null or empty.", nameof(parentIds));
            }

            return client.Create(job, new AwaitingJobsState(parents, nextState, options, mode));
        }
    }
}
