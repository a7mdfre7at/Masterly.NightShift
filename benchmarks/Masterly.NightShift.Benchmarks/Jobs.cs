using Masterly.NightShift;

namespace NightShiftBenchmarks;

/// <summary>A typical job argument: a small object with a few members.</summary>
public sealed class Order
{
    public int Id { get; set; }

    public string Customer { get; set; } = "";

    public decimal Total { get; set; }

    public List<string> Items { get; set; } = [];
}

/// <summary>Does almost nothing, so the benchmarks measure the library and not the job.</summary>
[GenerateJobInvokers]
public class GeneratedJobs
{
    public int Process(int id, string name) => id + name.Length;

    public Task ProcessAsync(int id, string name) => Task.CompletedTask;

    public int ProcessOrder(Order order) => order.Id;
}

/// <summary>NightShift has no generated invoker for this type: it is only used through expression variables.</summary>
public class CompiledJobs
{
    public int Process(int id, string name) => id + name.Length;

    public Task ProcessAsync(int id, string name) => Task.CompletedTask;
}

/// <summary>Forced onto NightShift's last resort, <c>MethodInfo.Invoke</c>.</summary>
public class ReflectionJobs
{
    public int Process(int id, string name) => id + name.Length;

    public Task ProcessAsync(int id, string name) => Task.CompletedTask;
}

/// <summary>The same job for Hangfire.</summary>
public class HangfireJobs
{
    public int Process(int id, string name) => id + name.Length;

    public Task ProcessAsync(int id, string name) => Task.CompletedTask;

    public int ProcessOrder(Order order) => order.Id;
}
