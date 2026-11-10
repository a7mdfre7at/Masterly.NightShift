using System.Reflection;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.SqlServer;
using Hangfire.Storage;

namespace NightShiftBenchmarks;

/// <summary>Everything the benchmarks do, through Hangfire's API.</summary>
internal sealed class HangfireSide : ILibrary
{
    public const string DatabaseName = "NightShiftBench_Hangfire";

    private static readonly Order Sample = Samples.Order;
    private readonly BackgroundJobPerformer _performer = new();
    private readonly StubStorage _stub = new();
    private readonly Job _sync = Job.FromExpression<HangfireJobs>(x => x.Process(42, "report"));
    private readonly Job _async = Job.FromExpression<HangfireJobs>(x => x.ProcessAsync(42, "report"));
    private readonly Job _withObject = Job.FromExpression<HangfireJobs>(x => x.ProcessOrder(Sample));
    private SqlServerStorage? _storage;
    private BackgroundJobClient? _client;

    public string Name => "Hangfire 1.8.25";

    public string Driver => new SqlServerStorageOptions().SqlClientFactory?.GetType().FullName ?? "default";

    public static void Configure()
    {
        GlobalConfiguration.Configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings();
    }

    public object? PerformSync() => _performer.Perform(NewContext(_sync));

    public object? PerformAsync() => _performer.Perform(NewContext(_async));

    public Job CreateJob()
    {
        int id = Sample.Id;
        return Job.FromExpression<HangfireJobs>(x => x.Process(id, "report"));
    }

    public string Serialize() => InvocationData.SerializeJob(_withObject).SerializePayload();

    public Job Deserialize(string payload) => InvocationData.DeserializePayload(payload).DeserializeJob();

    public void OpenDatabase()
    {
        Database.EnsureCreated(DatabaseName);
        _storage = new SqlServerStorage(Database.ConnectionString(DatabaseName), new SqlServerStorageOptions
        {
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            QueuePollInterval = TimeSpan.Zero,
            DisableGlobalLocks = true,
        });
        _client = new BackgroundJobClient(_storage);
    }

    public void Reset() => Database.Reset(DatabaseName);

    public string? Enqueue() => _client!.Enqueue<HangfireJobs>(x => x.Process(42, "report")); // Set by OpenDatabase

    public string? EnqueueCounted() => _client!.Enqueue<HangfireJobs>(x => x.Counted()); // Set by OpenDatabase

    public void EnqueueAllCounted(int count)
    {
        for (int i = 0; i < count; i++)
        {
            EnqueueCounted();
        }
    }

    public void EnqueueCountedInParallel(int count, int threads)
    {
        int next = 0;
        Thread[] workers = new Thread[threads];

        for (int i = 0; i < threads; i++)
        {
            workers[i] = new Thread(() =>
            {
                while (Interlocked.Increment(ref next) <= count)
                {
                    EnqueueCounted();
                }
            });
            workers[i].Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }
    }

    public object? ReadJob(string id)
    {
        using IStorageConnection connection = _storage!.GetConnection(); // Set by OpenDatabase
        return connection.GetJobData(id);
    }

    public IDisposable StartServer(int workerCount)
    {
        return new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = workerCount }, _storage!); // Set by OpenDatabase
    }

    public long SucceededCount() => _storage!.GetMonitoringApi().SucceededListCount(); // Set by OpenDatabase

    private PerformContext NewContext(Job job)
    {
        BackgroundJob backgroundJob = new("1", job, DateTime.UtcNow);
        return new PerformContext(_stub, _stub.Connection, backgroundJob, new JobCancellationToken(false));
    }

    private sealed class StubStorage : JobStorage
    {
        public IStorageConnection Connection { get; } = DispatchProxy.Create<IStorageConnection, NoOpProxy>();

        public override IMonitoringApi GetMonitoringApi() => throw new NotSupportedException();

        public override IStorageConnection GetConnection() => Connection;
    }
}
