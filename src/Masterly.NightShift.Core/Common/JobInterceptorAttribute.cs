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
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Common
{
    /// <summary>
    /// Base class for attributes that intercept the call of a background job method. Apply it to a job's
    /// class, interface or method. Interceptors run ordered by <see cref="Order"/>, type-level ones before
    /// method-level ones with the same order, and after the <see cref="GlobalJobInterceptors"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public abstract class JobInterceptorAttribute : Attribute, IJobInterceptor
    {
        /// <summary>
        /// Gets or sets the position in the chain. Lower values run first, that is, outermost.
        /// </summary>
        public int Order { get; set; }

        /// <inheritdoc />
        public abstract object? Intercept(JobInvocation invocation);
    }

    /// <summary>
    /// Base class for asynchronous interceptors. <see cref="InterceptAsync"/> awaits
    /// <see cref="JobInvocation.ProceedAsync"/> and returns the unwrapped value; this class re-shapes that
    /// into the job method's own return type (<see cref="Task"/>, <see cref="Task{TResult}"/>, <c>ValueTask</c>
    /// or a plain value).
    /// </summary>
    /// <remarks>
    /// For a synchronous job method the worker thread waits for <see cref="InterceptAsync"/> to finish, so
    /// keep asynchronous work there short, or make the job method asynchronous.
    /// </remarks>
    public abstract class AsyncJobInterceptorAttribute : JobInterceptorAttribute
    {
        /// <inheritdoc />
        public sealed override object? Intercept(JobInvocation invocation)
        {
            if (invocation == null) throw new ArgumentNullException(nameof(invocation));

            var task = InterceptAsync(invocation);
            if (task == null) throw new InvalidOperationException($"{GetType().Name}.InterceptAsync returned null instead of a task.");

            return TaskShapeAdapter.Adapt(task, invocation.ReturnType);
        }

        /// <summary>
        /// Runs around the job method.
        /// </summary>
        /// <param name="invocation">The invocation. Await <see cref="JobInvocation.ProceedAsync"/> to continue.</param>
        /// <returns>The unwrapped result to use for the job: the method's value, or <c>null</c> for
        /// <c>void</c> and non-generic task methods.</returns>
        [NotNull]
        protected abstract Task<object?> InterceptAsync([NotNull] JobInvocation invocation);
    }
}
