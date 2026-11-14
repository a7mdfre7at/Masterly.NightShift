#if NET10_0_OR_GREATER
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Masterly.NightShift.Common;
using Quartz;
using TickerQ.Utilities.Entities;

namespace NightShiftBenchmarks;

/// <summary>
/// The in-process operations of the other benchmark classes, for all four libraries in one run. No database
/// is involved. Quartz.NET 4 and TickerQ 10 target .NET 10 only, so this class runs on .NET 10 only.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class LibraryBenchmarks
{
    private readonly object[] _arguments = [42, "report"];
    private readonly HangfireJobs _hangfireInstance = new();
    private readonly GeneratedJobs _nightShiftInstance = new();
    private readonly HangfireSide _hangfire = new();
    private readonly NightShiftSide _nightShift = new();
    private readonly QuartzInProcess _quartz = new();
    private readonly TickerQInProcess _tickerQ = new();
    private MethodInfo _hangfireMethod = null!; // Set by Setup
    private JobInvoker _nightShiftInvoker = null!; // Set by Setup
    private string _hangfirePayload = "";
    private string _nightShiftPayload = "";
    private string _quartzPayload = "";
    private byte[] _tickerQPayload = [];

    [GlobalSetup]
    public void Setup()
    {
        HangfireSide.Configure();
        NightShiftSide.Configure();
        _nightShift.PrepareTiers();
        _quartz.Prepare();
        _tickerQ.Prepare();

        _hangfireMethod = typeof(HangfireJobs).GetMethod(nameof(HangfireJobs.Process))!; // Declared in Jobs.cs
        _nightShiftInvoker = JobInvokerRegistry.GetInvoker(typeof(GeneratedJobs).GetMethod(nameof(GeneratedJobs.Process))!); // Declared in Jobs.cs
        _hangfirePayload = _hangfire.Serialize();
        _nightShiftPayload = _nightShift.Serialize();
        _quartzPayload = _quartz.Serialize();
        _tickerQPayload = _tickerQ.Serialize();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Call the method")]
    public object? Hangfire_Call() => _hangfireMethod.Invoke(_hangfireInstance, _arguments);

    [Benchmark, BenchmarkCategory("Call the method")]
    public object? NightShift_Call() => _nightShiftInvoker.Invoke(_nightShiftInstance, _arguments);

    [Benchmark, BenchmarkCategory("Call the method")]
    public ValueTask Quartz_Call() => _quartz.Call();

    [Benchmark, BenchmarkCategory("Call the method")]
    public Task TickerQ_Call() => _tickerQ.Call();

    [Benchmark(Baseline = true), BenchmarkCategory("Serialize job")]
    public string Hangfire_Serialize() => _hangfire.Serialize();

    [Benchmark, BenchmarkCategory("Serialize job")]
    public string NightShift_Serialize() => _nightShift.Serialize();

    [Benchmark, BenchmarkCategory("Serialize job")]
    public string Quartz_Serialize() => _quartz.Serialize();

    [Benchmark, BenchmarkCategory("Serialize job")]
    public byte[] TickerQ_Serialize() => _tickerQ.Serialize();

    [Benchmark(Baseline = true), BenchmarkCategory("Deserialize job")]
    public object Hangfire_Deserialize() => _hangfire.Deserialize(_hangfirePayload);

    [Benchmark, BenchmarkCategory("Deserialize job")]
    public object NightShift_Deserialize() => _nightShift.Deserialize(_nightShiftPayload);

    [Benchmark, BenchmarkCategory("Deserialize job")]
    public Order Quartz_Deserialize() => _quartz.Deserialize(_quartzPayload);

    [Benchmark, BenchmarkCategory("Deserialize job")]
    public Order TickerQ_Deserialize() => _tickerQ.Deserialize(_tickerQPayload);

    [Benchmark(Baseline = true), BenchmarkCategory("Create job")]
    public object Hangfire_Create() => _hangfire.CreateJob();

    [Benchmark, BenchmarkCategory("Create job")]
    public object NightShift_Create() => _nightShift.CreateJob();

    [Benchmark, BenchmarkCategory("Create job")]
    public ITrigger Quartz_Create() => _quartz.CreateJob();

    [Benchmark, BenchmarkCategory("Create job")]
    public TimeTickerEntity TickerQ_Create() => _tickerQ.CreateJob();

    [Benchmark(Baseline = true), BenchmarkCategory("Run a job")]
    public object? Hangfire_RunSync() => _hangfire.PerformSync();

    [Benchmark, BenchmarkCategory("Run a job")]
    public object? Hangfire_RunAsync() => _hangfire.PerformAsync();

    [Benchmark, BenchmarkCategory("Run a job")]
    public object? NightShift_RunSync() => _nightShift.PerformSync();

    [Benchmark, BenchmarkCategory("Run a job")]
    public object? NightShift_RunAsync() => _nightShift.PerformAsync();

    // Quartz.NET and TickerQ have one path: every job returns a task
    [Benchmark, BenchmarkCategory("Run a job")]
    public void Quartz_Run() => _quartz.Run();

    [Benchmark, BenchmarkCategory("Run a job")]
    public void TickerQ_Run() => _tickerQ.Run();
}
#endif
