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

            object? result;

            try
            {
                result = await invocation.ProceedAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                console.WriteLine(JobConsoleLevel.Error, $"Failed after {stopwatch.Elapsed.TotalMilliseconds:N0} ms: {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            // Outside the try: the method has returned, so nothing written about it may report it as failed
            console.WriteLine(JobConsoleLevel.Debug, $"Finished in {stopwatch.Elapsed.TotalMilliseconds:N0} ms");
            return result;
        }
    }
}
