using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace NightShiftBenchmarks;

/// <summary>Runs one job through the whole performer with the default filters. No database is involved.</summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class InvocationBenchmarks
{
    private readonly HangfireSide _hangfire = new();
    private readonly NightShiftSide _nightShift = new();

    [GlobalSetup]
    public void Setup()
    {
        HangfireSide.Configure();
        NightShiftSide.Configure();
        _nightShift.PrepareTiers();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Sync job")]
    public object? Hangfire_Sync() => _hangfire.PerformSync();

    [Benchmark, BenchmarkCategory("Sync job")]
    public object? NightShift_Sync_Generated() => _nightShift.PerformSync();

    [Benchmark, BenchmarkCategory("Sync job")]
    public object? NightShift_Sync_Compiled() => _nightShift.PerformSyncCompiled();

    [Benchmark, BenchmarkCategory("Sync job")]
    public object? NightShift_Sync_Reflection() => _nightShift.PerformSyncReflection();

    [Benchmark(Baseline = true), BenchmarkCategory("Async job")]
    public object? Hangfire_Async() => _hangfire.PerformAsync();

    [Benchmark, BenchmarkCategory("Async job")]
    public object? NightShift_Async_Generated() => _nightShift.PerformAsync();

    [Benchmark, BenchmarkCategory("Async job")]
    public object? NightShift_Async_Compiled() => _nightShift.PerformAsyncCompiled();

    [Benchmark, BenchmarkCategory("Async job")]
    public object? NightShift_Async_Reflection() => _nightShift.PerformAsyncReflection();
}

/// <summary>Turns a lambda into a job, and a job into its stored JSON and back.</summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class SerializationBenchmarks
{
    private readonly HangfireSide _hangfire = new();
    private readonly NightShiftSide _nightShift = new();
    private string _hangfirePayload = "";
    private string _nightShiftPayload = "";

    [GlobalSetup]
    public void Setup()
    {
        HangfireSide.Configure();
        NightShiftSide.Configure();
        _hangfirePayload = _hangfire.Serialize();
        _nightShiftPayload = _nightShift.Serialize();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Create job from lambda")]
    public object Hangfire_Create() => _hangfire.CreateJob();

    [Benchmark, BenchmarkCategory("Create job from lambda")]
    public object NightShift_Create() => _nightShift.CreateJob();

    [Benchmark(Baseline = true), BenchmarkCategory("Serialize job")]
    public string Hangfire_Serialize() => _hangfire.Serialize();

    [Benchmark, BenchmarkCategory("Serialize job")]
    public string NightShift_Serialize() => _nightShift.Serialize();

    [Benchmark(Baseline = true), BenchmarkCategory("Deserialize job")]
    public object Hangfire_Deserialize() => _hangfire.Deserialize(_hangfirePayload);

    [Benchmark, BenchmarkCategory("Deserialize job")]
    public object NightShift_Deserialize() => _nightShift.Deserialize(_nightShiftPayload);
}

/// <summary>Writes and reads jobs in SQL Server.</summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SimpleJob(RuntimeMoniker.Net80, invocationCount: 256, iterationCount: 15, warmupCount: 3)]
[SimpleJob(RuntimeMoniker.Net10_0, invocationCount: 256, iterationCount: 15, warmupCount: 3)]
public class StorageBenchmarks
{
    private readonly HangfireSide _hangfire = new();
    private readonly NightShiftSide _nightShift = new();
    private string _hangfireId = "";
    private string _nightShiftId = "";

    [GlobalSetup]
    public void Setup()
    {
        HangfireSide.Configure();
        NightShiftSide.Configure();
        _hangfire.OpenDatabase();
        _nightShift.OpenDatabase();
        _hangfire.Reset();
        _nightShift.Reset();
        _hangfireId = _hangfire.Enqueue()!; // Null only when a filter cancels the job, and none is registered
        _nightShiftId = _nightShift.Enqueue()!; // Null only when a filter cancels the job, and none is registered
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Enqueue")]
    public string? Hangfire_Enqueue() => _hangfire.Enqueue();

    [Benchmark, BenchmarkCategory("Enqueue")]
    public string? NightShift_Enqueue() => _nightShift.Enqueue();

    [Benchmark(Baseline = true), BenchmarkCategory("Read job")]
    public object? Hangfire_ReadJob() => _hangfire.ReadJob(_hangfireId);

    [Benchmark, BenchmarkCategory("Read job")]
    public object? NightShift_ReadJob() => _nightShift.ReadJob(_nightShiftId);
}
