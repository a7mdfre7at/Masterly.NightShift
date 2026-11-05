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

using Masterly.NightShift.Annotations;

namespace Masterly.NightShift.Server
{
    /// <summary>
    /// Wraps the call of a background job method: an interceptor runs code before and after the method,
    /// can change its arguments, call it several times, or not call it at all. Unlike an
    /// <see cref="IServerFilter"/>, it runs inside the performer, on the job's synchronization context, with
    /// the activated instance at hand.
    /// </summary>
    /// <remarks>
    /// Interceptors come from <see cref="Common.JobInterceptorAttribute"/>s on the job's type and method, and
    /// from <see cref="GlobalJobInterceptors"/>. They form a chain: each one calls
    /// <see cref="JobInvocation.Proceed"/> (or <see cref="JobInvocation.ProceedAsync"/>) to run the next one,
    /// and the last call performs the method itself.
    /// </remarks>
    public interface IJobInterceptor
    {
        /// <summary>
        /// Runs around the job method.
        /// </summary>
        /// <param name="invocation">The invocation. Call <see cref="JobInvocation.Proceed"/> to continue.</param>
        /// <returns>The value to use as the method's result. It must have the method's return type: for an
        /// async method, a task of the same type. <see cref="Common.AsyncJobInterceptorAttribute"/> takes care
        /// of that for asynchronous interceptors.</returns>
        [CanBeNull]
        object? Intercept([NotNull] JobInvocation invocation);
    }
}
