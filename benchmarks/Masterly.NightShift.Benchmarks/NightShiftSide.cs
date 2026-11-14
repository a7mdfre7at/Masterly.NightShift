using System.Linq.Expressions;
using System.Reflection;
using Masterly.NightShift;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.SqlServer;
using Masterly.NightShift.Storage;

namespace NightShiftBenchmarks;

/// <summary>Everything the benchmarks do, through the NightShift API.</summary>
internal sealed class NightShiftSide(bool bulk = false) : ILibrary
{
    public const string DatabaseName = "NightShiftBench_NightShift";

    private static readonly Order Sample = Samples.Order;
    private readonly BackgroundJobPerformer _performer = new();
    private readonly StubStorage _stub = new();
    private readonly Job _sync = Job.FromExpression<GeneratedJobs>(x => x.Process(42, "report"));
    private readonly Job _async = Job.FromExpression<GeneratedJobs>(x => x.ProcessAsync(42, "report"));
    private readonly Job _withObject = Job.FromExpression<GeneratedJobs>(x => x.ProcessOrder(Sample));
    private readonly Job _compiledSync = FromVariable<CompiledJobs>(static x => x.Process(42, "report"));
    private readonly Job _compiledAsync = FromVariable<CompiledJobs>(static x => x.ProcessAsync(42, "report"));
    private readonly Job _reflectionSync = FromVariable<ReflectionJobs>(static x => x.Process(42, "report"));
    private readonly Job _reflectionAsync = FromVariable<ReflectionJobs>(static x => x.ProcessAsync(42, "report"));
    private SqlServerStorage? _storage;
    private BackgroundJobClient? _client;

    public string Name => bulk ? "NightShift EnqueueMany" : "NightShift";

    public string Driver => new SqlServerStorageOptions().SqlClientFactory?.GetType().FullName ?? "default";

    public static void Configure()
    {
        GlobalConfiguration.Configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings();
    }

    /// <summary>Resolves each job to its invoker tier, and fails if a benchmark would measure the wrong one.</summary>
    public void PrepareTiers()
    {
        JobInvokerRegistry.UseCompiledInvokers = false;
        PerformSyncReflection();
        PerformAsyncReflection();
        JobInvokerRegistry.UseCompiledInvokers = true;

        Expect(_sync, JobInvokerKind.Generated);
        Expect(_async, JobInvokerKind.Generated);
        Expect(_compiledSync, JobInvokerKind.Compiled);
        Expect(_compiledAsync, JobInvokerKind.Compiled);
        Expect(_reflectionSync, JobInvokerKind.Reflection);
        Expect(_reflectionAsync, JobInvokerKind.Reflection);
    }

    public object? PerformSync() => _performer.Perform(NewContext(_sync));

    public object? PerformAsync() => _performer.Perform(NewContext(_async));

    public object? PerformSyncCompiled() => _performer.Perform(NewContext(_compiledSync));

    public object? PerformAsyncCompiled() => _performer.Perform(NewContext(_compiledAsync));

    public object? PerformSyncReflection() => _performer.Perform(NewContext(_reflectionSync));

    public object? PerformAsyncReflection() => _performer.Perform(NewContext(_reflectionAsync));

    public Job CreateJob()
    {
        int id = Sample.Id;
        return Job.FromExpression<GeneratedJobs>(x => x.Process(id, "report"));
    }

    public string Serialize() => InvocationData.SerializeJob(_withObject).SerializePayload()!; // Null only for a null job

    public Job Deserialize(string payload) => InvocationData.DeserializePayload(payload).DeserializeJob();

    public void OpenDatabase()
    {
        Database.EnsureCreated(DatabaseName);
        _storage = new SqlServerStorage(Database.ConnectionString(DatabaseName), new SqlServerStorageOptions
        {
            // NS_DD=1: let the commits that are safe to lose skip the wait for the disk
            AllowDelayedDurability = Environment.GetEnvironmentVariable("NS_DD") == "1",
            // NS_COALESCE=0: every caller writes on its own
            MaxConcurrentWriters = Environment.GetEnvironmentVariable("NS_COALESCE") == "0" ? 0 : 8,
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            QueuePollInterval = TimeSpan.Zero,
            DisableGlobalLocks = true,
        });
        _client = new BackgroundJobClient(_storage);
    }

    public void Reset() => Database.Reset(DatabaseName);

    public string? Enqueue() => _client!.Enqueue<GeneratedJobs>(x => x.Process(42, "report")); // Set by OpenDatabase

    public string? EnqueueCounted() => _client!.Enqueue<GeneratedJobs>(x => x.Counted()); // Set by OpenDatabase

    public void EnqueueAllCounted(int count)
    {
        if (bulk)
        {
            _client!.EnqueueMany<GeneratedJobs, int>(Enumerable.Range(0, count), static (x, _) => x.Counted()); // Set by OpenDatabase
            return;
        }

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

    public bool HasFinished(int jobCount) => _storage!.GetMonitoringApi().SucceededListCount() >= jobCount; // Set by OpenDatabase

    // The lambda reaches Job.FromExpression through a variable, so the source generator doesn't see it
    private static Job FromVariable<T>(Expression<Action<T>> call) => Job.FromExpression(call);

    private static void Expect(Job job, JobInvokerKind kind)
    {
        JobInvokerKind actual = JobInvokerRegistry.GetInvoker(job.Method).Kind;
        if (actual != kind)
        {
            throw new InvalidOperationException($"{job.Type.Name}.{job.Method.Name} uses {actual}, expected {kind}.");
        }
    }

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
