#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Processing;

namespace Masterly.NightShift.Server
{
    /// <summary>
    /// One call of a background job method as seen by <see cref="IJobInterceptor"/>s: the instance, the
    /// arguments (which interceptors may replace) and the way to continue the chain.
    /// </summary>
    public sealed class JobInvocation
    {
        private readonly IReadOnlyList<IJobInterceptor> _interceptors;
        private readonly Func<object, Task>? _getTask;

        // Each step of the chain gets its own invocation, so an interceptor that proceeds after an await (when
        // the outer Proceed call has long returned) still continues with the next step, never restarts the chain
        private readonly int _position;

        internal JobInvocation(
            [NotNull] PerformContext context,
            [NotNull] JobInvoker invoker,
            [CanBeNull] object? instance,
            [NotNull] object?[] arguments,
            [NotNull] IReadOnlyList<IJobInterceptor> interceptors)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            Invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
            Instance = instance;
            Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
            _interceptors = interceptors ?? throw new ArgumentNullException(nameof(interceptors));

            ReturnsTaskLike = invoker.ReturnType.IsTaskLike(out _getTask);
        }

        private JobInvocation(JobInvocation previous, int position)
        {
            Context = previous.Context;
            Invoker = previous.Invoker;
            Instance = previous.Instance;
            Arguments = previous.Arguments;
            ReturnsTaskLike = previous.ReturnsTaskLike;
            _interceptors = previous._interceptors;
            _getTask = previous._getTask;
            _position = position;
        }

        /// <summary>Gets the context the job is performed in.</summary>
        [NotNull]
        public PerformContext Context { get; }

        /// <summary>Gets the job being performed.</summary>
        [NotNull]
        public Job Job => Context.BackgroundJob.Job!;

        /// <summary>Gets the invoker that will call the method, including how (<see cref="JobInvoker.Kind"/>).</summary>
        [NotNull]
        public JobInvoker Invoker { get; }

        /// <summary>Gets the activated instance the method is called on, or <c>null</c> for static methods.</summary>
        [CanBeNull]
        public object? Instance { get; }

        /// <summary>
        /// Gets the arguments, already converted to the parameter types and with Masterly.NightShift's substitutions
        /// (cancellation tokens, <see cref="PerformContext"/>) applied. Elements may be replaced before
        /// calling <see cref="Proceed"/>.
        /// </summary>
        [NotNull]
        public object?[] Arguments { get; }

        /// <summary>Gets the method's return type.</summary>
        [NotNull]
        public Type ReturnType => Invoker.ReturnType;

        /// <summary>Gets whether the method returns a <see cref="Task"/>, <c>ValueTask</c> or another awaitable.</summary>
        public bool ReturnsTaskLike { get; }

        /// <summary>
        /// Runs the next interceptor, or the job method when this is the last one, and returns its raw result:
        /// for an async method that is the task, not awaited. May be called more than once, and at any time,
        /// for example after an <c>await</c>: it always continues with the step after the current one.
        /// </summary>
        [CanBeNull]
        public object? Proceed()
        {
            return _position < _interceptors.Count
                ? _interceptors[_position].Intercept(new JobInvocation(this, _position + 1))
                : Invoker.Invoke(Instance, Arguments);
        }

        /// <summary>
        /// Like <see cref="Proceed"/>, but awaits a task-like result and returns the unwrapped value
        /// (<c>null</c> for <c>void</c> and non-generic tasks).
        /// </summary>
        [NotNull]
        public async Task<object?> ProceedAsync()
        {
            var result = Proceed();

            if (result == null || _getTask == null)
            {
                return result;
            }

            var task = _getTask(result);
            await task.ConfigureAwait(false);

            return task.GetTaskLikeResult(result, ReturnType);
        }
    }
}
