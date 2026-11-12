// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
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

using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Options of a <see cref="BackgroundJobServer"/>: its name, how many workers it runs and which queues they
/// fetch from, how often its background processes run, how it stops, and the services it uses. Pass them to
/// the server's constructor or set them in <c>AddNightShiftServer</c>; they are read when the server starts.
/// </summary>
public class BackgroundJobServerOptions
{
    // https://github.com/HangfireIO/Hangfire/issues/246
    private const int MaxDefaultWorkerCount = 20;

    /// <summary>
    /// Initializes the options with their defaults.
    /// </summary>
    public BackgroundJobServerOptions()
    {
        WorkerCount = Math.Min(Environment.ProcessorCount * 5, MaxDefaultWorkerCount);
        Queues = [EnqueuedState.DefaultQueue];
        StopTimeout = BackgroundProcessingServerOptions.DefaultStopTimeout;
        ShutdownTimeout = BackgroundProcessingServer.DefaultShutdownTimeout;
        SchedulePollingInterval = DelayedJobScheduler.DefaultPollingDelay;
        HeartbeatInterval = BackgroundProcessingServerOptions.DefaultHeartbeatInterval;
        ServerTimeout = ServerWatchdog.DefaultServerTimeout;
        ServerCheckInterval = ServerWatchdog.DefaultCheckInterval;
        CancellationCheckInterval = ServerJobCancellationWatcher.DefaultCheckInterval;

        FilterProvider = null;
        Activator = null;
        TimeZoneResolver = null;
        TaskScheduler = TaskScheduler.Default;
    }

    /// <summary>
    /// Gets or sets the first part of the server ID, shown on the dashboard. <c>null</c> (the default) uses the
    /// <c>COMPUTERNAME</c> environment variable, then <c>HOSTNAME</c>, then <see cref="Environment.MachineName"/>.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Gets or sets whether the server runs only the <see cref="Worker"/> pool and the processes every server
    /// needs: its heartbeat, the <see cref="ServerWatchdog"/>, the <see cref="ServerJobCancellationWatcher"/>,
    /// the storage's required processes (<see cref="JobStorage.GetServerRequiredProcesses"/>) and the
    /// additional processes it was given. The <see cref="DelayedJobScheduler"/>, the
    /// <see cref="RecurringJobScheduler"/> and the storage-wide processes
    /// (<see cref="JobStorage.GetStorageWideProcesses"/>, such as SQL Server's expiration manager and
    /// counters aggregator) are left out, so keep at least one server that isn't lightweight per storage.
    /// </summary>
    public bool IsLightweightServer { get; set; }

    /// <summary>
    /// Gets or sets how many jobs the server runs at the same time, each on a dedicated thread. Default is
    /// <c>Math.Min(Environment.ProcessorCount * 5, 20)</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int WorkerCount
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the queues the workers fetch jobs from. Default is <c>{ "default" }</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The value is empty.</exception>
    public string[] Queues
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0)
            {
                throw new ArgumentException("You should specify at least one queue to listen.", nameof(value));
            }

            field = value;
        }
    } = [EnqueuedState.DefaultQueue];

    /// <summary>
    /// Gets or sets the time between the stop signal and the moment the cancellation token of every running
    /// job is canceled. Default is 500 milliseconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (other than
    /// <see cref="Timeout.InfiniteTimeSpan"/>) or greater than <see cref="int.MaxValue"/> milliseconds.</exception>
    public TimeSpan StopTimeout
    {
        get;
        set
        {
            if ((value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan) || value.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"StopTimeout must be either equal to or less than {int.MaxValue} milliseconds and non-negative or infinite");
            }
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the time between the stop signal and the moment the server stops waiting for its
    /// processes and removes itself from the storage. Jobs still running then are abandoned and picked up
    /// again later. Default is 15 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative (other than
    /// <see cref="Timeout.InfiniteTimeSpan"/>) or greater than <see cref="int.MaxValue"/> milliseconds.</exception>
    public TimeSpan ShutdownTimeout
    {
        get;
        set
        {
            if ((value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan) || value.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"ShutdownTimeout must be either equal to or less than {int.MaxValue} milliseconds and non-negative or infinite");
            }
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets how long the delayed and recurring job schedulers wait between runs, so a delayed job runs
    /// up to this much later than its time. Default is 15 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or greater than
    /// <see cref="int.MaxValue"/> milliseconds.</exception>
    public TimeSpan SchedulePollingInterval
    {
        get;
        set
        {
            if (value < TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"SchedulePollingInterval must be non-negative and either equal to or less than {int.MaxValue} milliseconds");
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets how often the server reports to the storage that it is alive. Default is 30 seconds.
    /// Keep it well below <see cref="ServerTimeout"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or longer than 24 hours.</exception>
    // Zero makes the heartbeat process write to the storage without pause
    public TimeSpan HeartbeatInterval
    {
        get;
        set
        {
            if (value <= TimeSpan.Zero || value > ServerWatchdog.MaxHeartbeatInterval)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, $"HeartbeatInterval must be greater than zero and equal to or less than {ServerWatchdog.MaxHeartbeatInterval.TotalHours} hours.");
            }
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets how often the server's watchdog looks for servers that stopped sending heartbeats.
    /// Default is 5 minutes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or longer than 24 hours.</exception>
    // Zero makes the watchdog query the storage without pause
    public TimeSpan ServerCheckInterval
    {
        get;
        set
        {
            if (value <= TimeSpan.Zero || value > ServerWatchdog.MaxServerCheckInterval)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, $"ServerCheckInterval must be greater than zero and equal to or less than {ServerWatchdog.MaxServerCheckInterval.TotalHours} hours.");
            }
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets how long a server may go without a heartbeat before the watchdog of any server removes it
    /// as dead. A server that can't send heartbeats for this long itself restarts its processes. Default is
    /// 5 minutes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or longer than 24 hours.</exception>
    // With zero every server, this one included, is taken for dead and removed at each check
    public TimeSpan ServerTimeout
    {
        get;
        set
        {
            if (value <= TimeSpan.Zero || value > ServerWatchdog.MaxServerTimeout)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, $"ServerTimeout must be greater than zero and equal to or less than {ServerWatchdog.MaxServerTimeout.TotalHours} hours.");
            }

            field = value;

        }
    }

    /// <summary>
    /// Gets or sets how often the server checks whether any of its running jobs was deleted or moved to
    /// another state, to cancel the job's cancellation token. Default is 5 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative or greater than
    /// <see cref="int.MaxValue"/> milliseconds.</exception>
    // The cancellation watcher waits this long between checks: a negative value makes every wait throw, so
    // deleted jobs are never told to stop, and zero makes it query the storage without pause
    public TimeSpan CancellationCheckInterval
    {
        get;
        set
        {
            if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, $"CancellationCheckInterval must be greater than zero and equal to or less than {int.MaxValue} milliseconds.");
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets where the server gets job filters. <c>null</c> (the default) uses
    /// <see cref="JobFilterProviders.Providers"/>: the global filters and the filter attributes.
    /// </summary>
    [CanBeNull]
    public IJobFilterProvider? FilterProvider { get; set; }

    /// <summary>
    /// Gets or sets what creates the instances job methods are called on. <c>null</c> (the default) uses
    /// <see cref="JobActivator.Current"/>.
    /// </summary>
    [CanBeNull]
    public JobActivator? Activator { get; set; }

    /// <summary>
    /// Gets or sets what turns the time zone IDs stored with recurring jobs into <see cref="TimeZoneInfo"/>.
    /// <c>null</c> (the default) uses the global resolver set with <c>UseTimeZoneResolver</c>.
    /// </summary>
    [CanBeNull]
    public ITimeZoneResolver? TimeZoneResolver { get; set; }

    /// <summary>
    /// Gets or sets where asynchronous job methods run while the worker waits for their result. Default is
    /// <see cref="System.Threading.Tasks.TaskScheduler.Default"/>, the thread pool; <c>null</c> runs them on the
    /// worker thread itself. Also used by the schedulers when <see cref="MaxDegreeOfParallelismForSchedulers"/>
    /// is greater than 1.
    /// </summary>
    [CanBeNull]
    public TaskScheduler? TaskScheduler { get; set; }

    /// <summary>
    /// Gets or sets an action called for each worker thread before it starts, for example to set its culture
    /// or priority. Default is <c>null</c>.
    /// </summary>
    [CanBeNull]
    public Action<Thread>? WorkerThreadConfigurationAction { get; set; }

    /// <summary>
    /// Experimental option for schedulers, but not for workers. Gets or sets the
    /// maximum degree of parallelism for <see cref="RecurringJobScheduler"/>
    /// and <see cref="DelayedJobScheduler"/> processes, allowing them to enable
    /// parallel scheduling of recurring and delayed jobs when the specified value
    /// is greater than <c>1</c>. Parallel work items are executed on the task scheduler
    /// specified in the <see cref="TaskScheduler"/> property.
    /// </summary>
    public int MaxDegreeOfParallelismForSchedulers { get; set; } = 1;

    /// <summary>
    /// Gets or sets the number of workers that serve the <c>long-running</c> queue (<see cref="LongRunningAttribute"/>),
    /// in addition to <see cref="WorkerCount"/>, so long jobs never take every worker from the short ones. 0 (the
    /// default) adds none: the queue is then served only when it is in <see cref="Queues"/>, and without a server that
    /// serves it, in this process or another, <c>[LongRunning]</c> jobs wait in the queue (the first one logs a warning
    /// when this process runs servers but none serves the queue).
    /// </summary>
    public int LongRunningWorkerCount
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets whether a server added with <c>AddNightShiftServer</c> starts with the host
    /// (<see cref="ServerStartMode.Automatic"/>, the default) or waits for <c>IBackgroundJobServerControl.StartAsync</c>
    /// (<see cref="ServerStartMode.Manual"/>). A <see cref="BackgroundJobServer"/> created directly always starts at once.
    /// </summary>
    public ServerStartMode StartMode
    {
        get;
        set => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a ServerStartMode value.");
    }
}

/// <summary>When a hosted server starts, see <see cref="BackgroundJobServerOptions.StartMode"/>.</summary>
public enum ServerStartMode
{
    /// <summary>With the host.</summary>
    Automatic = 0,

    /// <summary>When the application asks, with <c>IBackgroundJobServerControl.StartAsync</c>.</summary>
    Manual = 1,
}