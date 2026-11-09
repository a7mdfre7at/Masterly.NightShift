// This file is part of Hangfire. Copyright © 2017 Hangfire OÜ.
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

using Masterly.NightShift.Logging;
using Masterly.NightShift.Processing;

namespace Masterly.NightShift.Server;

/// <summary>
/// Turns background processes into dispatcher builders for <see cref="BackgroundProcessingServer"/>, running them
/// on dedicated threads.
/// </summary>
#pragma warning disable CA1708 // False positive: the analyzer compares the types the compiler generates for extension blocks
public static class BackgroundProcessExtensions
#pragma warning restore CA1708
{
    extension([NotNull] IBackgroundProcess process)
    {
        public IBackgroundProcessDispatcherBuilder UseBackgroundPool(
    )
        {
            return UseBackgroundPool(process, Environment.ProcessorCount);
        }

        public IBackgroundProcessDispatcherBuilder UseBackgroundPool(
            int threadCount)
        {
            return UseBackgroundPool(process, threadCount, null);
        }

        public IBackgroundProcessDispatcherBuilder UseBackgroundPool(
            int threadCount,
            [CanBeNull] Action<Thread> threadConfig)
        {
            ArgumentNullException.ThrowIfNull(process);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadCount);

            return UseBackgroundPool(
                process,
                (threadName, threadStart) => DefaultThreadFactory(threadCount, threadName, threadStart, threadConfig));
        }

        public IBackgroundProcessDispatcherBuilder UseBackgroundPool(
            [NotNull] Func<string, ThreadStart, IEnumerable<Thread>> threadFactory)
        {
            ArgumentNullException.ThrowIfNull(process);
            ArgumentNullException.ThrowIfNull(threadFactory);

            return new BackgroundProcessDispatcherBuilder(
                process,
                threadStart => threadFactory(process.GetType().Name, threadStart));
        }
    }

    extension([NotNull] IBackgroundProcessAsync process)
    {
        public IBackgroundProcessDispatcherBuilder UseBackgroundPool(
    )
        {
            return UseBackgroundPool(process, Environment.ProcessorCount);
        }

        public IBackgroundProcessDispatcherBuilder UseBackgroundPool(
            int maxConcurrency)
        {
            return UseBackgroundPool(
                process,
                maxConcurrency,
                maxConcurrency < Environment.ProcessorCount ? maxConcurrency : Environment.ProcessorCount);
        }

        public IBackgroundProcessDispatcherBuilder UseBackgroundPool(
            int maxConcurrency,
            int threadCount)
        {
            ArgumentNullException.ThrowIfNull(process);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadCount);

            return UseBackgroundPool(
                process,
                maxConcurrency,
                (threadName, threadStart) => DefaultThreadFactory(threadCount, threadName, threadStart, null));
        }

        public IBackgroundProcessDispatcherBuilder UseBackgroundPool(
            int maxConcurrency,
            [NotNull] Func<string, ThreadStart, IEnumerable<Thread>> threadFactory)
        {
            ArgumentNullException.ThrowIfNull(process);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
            ArgumentNullException.ThrowIfNull(threadFactory);

            Func<TaskScheduler> createScheduler = () => new BackgroundTaskScheduler(
                threadStart => threadFactory(process.GetType().Name, threadStart),
                static exception =>
                {
                    LogProvider.GetLogger(typeof(BackgroundTaskScheduler)).FatalException(
                        "Unhandled exception occurred in scheduler. Please report it to Masterly.NightShift developers",
                        exception);
                });

            return new BackgroundProcessDispatcherBuilderAsync(process, createScheduler, maxConcurrency, true);
        }

        public IBackgroundProcessDispatcherBuilder UseThreadPool(
    )
        {
            return UseThreadPool(process, Environment.ProcessorCount);
        }

        public IBackgroundProcessDispatcherBuilder UseThreadPool(
            int maxConcurrency)
        {
            ArgumentNullException.ThrowIfNull(process);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);

            return new BackgroundProcessDispatcherBuilderAsync(process, static () => TaskScheduler.Default, maxConcurrency, false);
        }
    }

    internal static IEnumerable<Thread> DefaultThreadFactory(
        int threadCount,
        [NotNull] string threadName,
        [NotNull] ThreadStart threadStart,
        [CanBeNull] Action<Thread> threadConfig = null)
    {
        ArgumentNullException.ThrowIfNull(threadName);
        ArgumentNullException.ThrowIfNull(threadStart);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadCount);

        for (var i = 0; i < threadCount; i++)
        {
            var thread = new Thread(threadStart)
            {
                IsBackground = true,
                Name = $"{threadName} #{i + 1}"
            };

            threadConfig?.Invoke(thread);
            yield return thread;
        }
    }
}
