// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
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
        private int _position;

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
        /// for an async method that is the task, not awaited. May be called more than once.
        /// </summary>
        [CanBeNull]
        public object? Proceed()
        {
            var position = _position;
            _position = position + 1;

            try
            {
                return position < _interceptors.Count
                    ? _interceptors[position].Intercept(this)
                    : Invoker.Invoke(Instance, Arguments);
            }
            finally
            {
                _position = position;
            }
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
