#if NETFRAMEWORK
// Compiled into every test project's net48 target (see tests/Directory.Build.props). The namespace makes the
// extensions visible wherever Task is.
namespace System.Threading.Tasks
{
    /// <summary>
    /// Task.WaitAsync exists on .NET 6 and later only. These extensions behave the same way on .NET Framework:
    /// they throw <see cref="TimeoutException"/> after the timeout, <see cref="OperationCanceledException"/> when the
    /// token is canceled, and otherwise return the task's own result or exception.
    /// </summary>
    internal static class TaskWaitAsyncPolyfill
    {
        public static Task WaitAsync(this Task task, CancellationToken cancellationToken)
        {
            return task.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public static async Task WaitAsync(this Task task, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using (var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var delay = Task.Delay(timeout, delayCancellation.Token);

                if (await Task.WhenAny(task, delay).ConfigureAwait(false) != task)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException();
                }

                delayCancellation.Cancel();
            }

            await task.ConfigureAwait(false);
        }

        public static Task<T> WaitAsync<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            return task.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await ((Task)task).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }
    }
}
#endif
