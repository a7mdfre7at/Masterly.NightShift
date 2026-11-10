using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Masterly.NightShift.Common;

namespace NightShiftBenchmarks;

/// <summary>Only the call to the job method, which is the part the source generator replaces.</summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class InvokerBenchmarks
{
    private readonly object[] _arguments = [42, "report"];
    private readonly GeneratedJobs _generatedInstance = new();
    private readonly CompiledJobs _compiledInstance = new();
    private readonly HangfireJobs _hangfireInstance = new();
    private MethodInfo _generatedMethod = null!; // Set by Setup
    private MethodInfo _compiledMethod = null!; // Set by Setup
    private MethodInfo _hangfireMethod = null!; // Set by Setup
    private JobInvoker _generated = null!; // Set by Setup
    private JobInvoker _compiled = null!; // Set by Setup

    [GlobalSetup]
    public void Setup()
    {
        _generatedMethod = typeof(GeneratedJobs).GetMethod(nameof(GeneratedJobs.Process))!; // Declared above
        _compiledMethod = typeof(CompiledJobs).GetMethod(nameof(CompiledJobs.Process))!; // Declared above
        _hangfireMethod = typeof(HangfireJobs).GetMethod(nameof(HangfireJobs.Process))!; // Declared above
        _generated = JobInvokerRegistry.GetInvoker(_generatedMethod);
        _compiled = JobInvokerRegistry.GetInvoker(_compiledMethod);

        if (_generated.Kind != JobInvokerKind.Generated || _compiled.Kind != JobInvokerKind.Compiled)
        {
            throw new InvalidOperationException($"Wrong invokers: {_generated.Kind} and {_compiled.Kind}.");
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Call the method")]
    public object? Hangfire_MethodInfoInvoke() => _hangfireMethod.Invoke(_hangfireInstance, _arguments);

    [Benchmark, BenchmarkCategory("Call the method")]
    public object? NightShift_Generated() => _generated.Invoke(_generatedInstance, _arguments);

    [Benchmark, BenchmarkCategory("Call the method")]
    public object? NightShift_Compiled() => _compiled.Invoke(_compiledInstance, _arguments);

    [Benchmark(Baseline = true), BenchmarkCategory("First call of a method")]
    public object? FirstCall_Compiled()
    {
        JobInvokerRegistry.ClearResolved();
        return JobInvokerRegistry.GetInvoker(_compiledMethod).Invoke(_compiledInstance, _arguments);
    }

    [Benchmark, BenchmarkCategory("First call of a method")]
    public object? FirstCall_Generated()
    {
        JobInvokerRegistry.ClearResolved();
        return JobInvokerRegistry.GetInvoker(_generatedMethod).Invoke(_generatedInstance, _arguments);
    }
}
