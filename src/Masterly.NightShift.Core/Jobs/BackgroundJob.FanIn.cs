#nullable enable

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;

namespace Masterly.NightShift
{
    public partial class BackgroundJob
    {
        /// <summary>Creates a job that starts once <em>every</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAll(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAll(parentIds, methodCall, options);
        }

        /// <summary>Creates a job for the specified queue that starts once <em>every</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAll(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAll(parentIds, queue, methodCall, options: options);
        }

        /// <summary>Creates a job that starts once <em>every</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAll(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAll(parentIds, methodCall, options);
        }

        /// <summary>Creates a job for the specified queue that starts once <em>every</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAll(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAll(parentIds, queue, methodCall, options: options);
        }

        /// <summary>Creates a job that starts once <em>every</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAll<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAll(parentIds, methodCall, options);
        }

        /// <summary>Creates a job for the specified queue that starts once <em>every</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAll<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAll(parentIds, queue, methodCall, options: options);
        }

        /// <summary>Creates a job that starts once <em>every</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAll<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAll(parentIds, methodCall, options);
        }

        /// <summary>Creates a job for the specified queue that starts once <em>every</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAll<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAll(parentIds, queue, methodCall, options: options);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAny(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAny(parentIds, methodCall, options);
        }

        /// <summary>Creates a job for the specified queue that starts as soon as <em>one</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAny(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAny(parentIds, queue, methodCall, options: options);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAny(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAny(parentIds, methodCall, options);
        }

        /// <summary>Creates a job for the specified queue that starts as soon as <em>one</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAny(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAny(parentIds, queue, methodCall, options: options);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAny<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAny(parentIds, methodCall, options);
        }

        /// <summary>Creates a job for the specified queue that starts as soon as <em>one</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAny<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAny(parentIds, queue, methodCall, options: options);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAny<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAny(parentIds, methodCall, options);
        }

        /// <summary>Creates a job for the specified queue that starts as soon as <em>one</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAny<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull] string queue,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAny(parentIds, queue, methodCall, options: options);
        }
    }
}
