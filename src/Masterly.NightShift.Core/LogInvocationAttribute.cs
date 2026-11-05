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
using System.Diagnostics;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;

namespace Masterly.NightShift
{
    /// <summary>
    /// Writes the start, the end with the elapsed time, and any exception of the job method to the job's
    /// console (<see cref="IJobConsole"/>), so the dashboard shows them live without code in the job.
    /// </summary>
    public sealed class LogInvocationAttribute : AsyncJobInterceptorAttribute
    {
        /// <inheritdoc />
        protected override async Task<object?> InterceptAsync(JobInvocation invocation)
        {
            if (invocation == null) throw new ArgumentNullException(nameof(invocation));

            var console = invocation.Context.Console;
            var invoker = invocation.Invoker;
            var stopwatch = Stopwatch.StartNew();

            console.WriteLine(JobConsoleLevel.Debug, $"Starting {invocation.Job.Type.ToGenericTypeString()}.{invoker.MethodName} ({invoker.Kind} invoker)");

            try
            {
                var result = await invocation.ProceedAsync().ConfigureAwait(false);
                console.WriteLine(JobConsoleLevel.Debug, $"Finished in {stopwatch.Elapsed.TotalMilliseconds:N0} ms");
                return result;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                console.WriteLine(JobConsoleLevel.Error, $"Failed after {stopwatch.Elapsed.TotalMilliseconds:N0} ms: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
    }
}
