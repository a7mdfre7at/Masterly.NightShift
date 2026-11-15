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

using Masterly.NightShift.Processing;

namespace Masterly.NightShift.Server;

internal sealed class BackgroundProcessDispatcherBuilder : IBackgroundProcessDispatcherBuilder
{
    private readonly IBackgroundProcess _process;
    private readonly Func<ThreadStart, IEnumerable<Thread>> _threadFactory;
    private readonly string _name;

    public BackgroundProcessDispatcherBuilder(
        [NotNull] IBackgroundProcess process,
        [NotNull] Func<ThreadStart, IEnumerable<Thread>> threadFactory)
        : this(process, threadFactory, null)
    {
    }

    /// <summary>
    /// A builder whose dispatcher goes by <paramref name="name"/> in the logs instead of the name of the process's
    /// type: for a process that wraps another one and stands for it.
    /// </summary>
    public BackgroundProcessDispatcherBuilder(
        [NotNull] IBackgroundProcess process,
        [NotNull] Func<ThreadStart, IEnumerable<Thread>> threadFactory,
        [CanBeNull] string? name)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(threadFactory);

        _process = process;
        _threadFactory = threadFactory;
        _name = name ?? process.GetType().Name;
    }

    public IBackgroundDispatcher Create(BackgroundServerContext context, BackgroundProcessingServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        BackgroundExecution execution = new(
            new BackgroundExecutionOptions
            {
                Name = _name,
                RetryDelay = options.RetryDelay
            },
            context.StoppingToken);

        return new BackgroundDispatcher(
            execution,
            ExecuteProcess,
            Tuple.Create(_process, context, execution),
            _threadFactory);
    }

    public override string ToString()
    {
        return _name;
    }

    private static void ExecuteProcess(Guid executionId, object? state)
    {
        Tuple<IBackgroundProcess, BackgroundServerContext, BackgroundExecution> tuple = (Tuple<IBackgroundProcess, BackgroundServerContext, BackgroundExecution>)state!; // The tuple passed when creating the dispatcher
        BackgroundServerContext serverContext = tuple.Item2;

        BackgroundProcessContext context = new(
            serverContext.ServerId,
            serverContext.Storage,
            serverContext.Properties.ToDictionary(static x => x.Key, static x => x.Value),
            executionId,
            serverContext.StoppingToken,
            serverContext.StoppedToken,
            serverContext.ShutdownToken)
        {
            BusyWorkers = serverContext.BusyWorkers
        };

        while (!context.IsStopping)
        {
            tuple.Item1.Execute(context);
            tuple.Item3.NotifySucceeded();
        }
    }
}
