#if NET10_0_OR_GREATER
using Microsoft.Data.SqlClient;
using Quartz;

namespace NightShiftBenchmarks;

/// <summary>
/// The end-to-end run through Quartz.NET with its SQL Server job store. A fire-and-forget job is a one-shot
/// trigger of one durable job, the cheapest way Quartz has to say "run this once, now".
/// </summary>
internal sealed class QuartzSide(int workerCount, bool bulk = false) : ILibrary
{
    public const string DatabaseName = "NightShiftBench_Quartz";

    private static readonly JobKey Key = new("counted");
    private IScheduler? _scheduler;

    public string Name => bulk ? "Quartz.NET 4.7.3 ScheduleJob(triggers)" : "Quartz.NET 4.7.3";

    public string Driver => SqlClientFactory.Instance.GetType().FullName ?? "default";

    private IScheduler Scheduler => _scheduler!; // Set by OpenDatabase

    public void OpenDatabase()
    {
        Database.EnsureCreated(DatabaseName);
        _scheduler = Wait(QuartzSchedulerBuilder.Create(quartz =>
        {
            // One trigger per free worker and round trip, where the default is one trigger at a time
            quartz.ConfigureScheduler(options => options.MaxBatchSize = workerCount);
            quartz.UseDefaultThreadPool(workerCount);
            quartz.UsePersistentStore(store =>
            {
                store.UseSqlServer(SqlClientFactory.Instance, Database.ConnectionString(DatabaseName));
                store.ProvisionSchema();
                store.ConfigureStore(options =>
                {
                    // The jobs wait for the server longer than the default minute, and must not count as misfired
                    options.MisfireThreshold = TimeSpan.FromMinutes(30);
                    options.AcquireTriggersWithinLock = true;
                    options.UseBackgroundThreads = true;
                });
            });
        }).BuildScheduler());
    }

    public void Reset()
    {
        Wait(Scheduler.Clear());
        Wait(Scheduler.AddJob(NewJob(), default));
    }

    public void EnqueueAllCounted(int count)
    {
        if (bulk)
        {
            List<ITrigger> triggers = new(count);
            for (int i = 0; i < count; i++)
            {
                triggers.Add(TriggerBuilder.Create().ForJob(Key).StartNow().Build());
            }

            Wait(Scheduler.ScheduleJob(NewJob(), triggers, ScheduleJobOptions.Replacing));
            return;
        }

        for (int i = 0; i < count; i++)
        {
            EnqueueCounted();
        }
    }

    public void EnqueueCountedInParallel(int count, int threads) => Callers.Run(count, threads, EnqueueCounted);

    public IDisposable StartServer(int workers)
    {
        Wait(Scheduler.Start());
        return new Stop(Scheduler);
    }

    // Quartz deletes a one-shot trigger when its job has run, so the jobs that are done are the ones without a trigger
    public bool HasFinished(int jobCount) => RemainingCount() == 0;

    private static long RemainingCount()
    {
        using SqlConnection connection = new(Database.ConnectionString(DatabaseName));
        using SqlCommand command = new("SELECT COUNT(*) FROM QRTZ_TRIGGERS", connection);
        connection.Open();
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IJobDetail NewJob() => JobBuilder.Create<CountedQuartzJob>().WithIdentity(Key).StoreDurably().Build();

    private void EnqueueCounted() => Wait(Scheduler.ScheduleJob(TriggerBuilder.Create().ForJob(Key).StartNow().Build(), default));

    private static void Wait(ValueTask task) => task.AsTask().GetAwaiter().GetResult();

    private static T Wait<T>(ValueTask<T> task) => task.AsTask().GetAwaiter().GetResult();

    private sealed class Stop(IScheduler scheduler) : IDisposable
    {
        public void Dispose() => Wait(scheduler.Standby());
    }
}

/// <summary>The same empty job for Quartz.NET.</summary>
public sealed class CountedQuartzJob : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        Completed.Add();
        return ValueTask.CompletedTask;
    }
}
#endif
