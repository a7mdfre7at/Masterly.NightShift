using BenchmarkDotNet.Running;
using NightShiftBenchmarks;

if (args is ["throughput", .. string[] rest])
{
    return Throughput.Run(rest);
}

BenchmarkSwitcher.FromAssembly(typeof(InvocationBenchmarks).Assembly).Run(args);
return 0;
