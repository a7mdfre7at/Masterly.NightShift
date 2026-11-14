#if NET10_0_OR_GREATER
using Microsoft.Extensions.DependencyInjection;
using TickerQ.DependencyInjection;
using TickerQ.Utilities;
using TickerQ.Utilities.Base;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Enums;
using TickerQ.Utilities.Interfaces;
using TickerQ.Utilities.Models;

namespace NightShiftBenchmarks;

/// <summary>
/// The in-process operations through TickerQ 10.4.0. A job is a function its source generator registers, its
/// argument is a request stored as JSON bytes, and "creating a job" is building the time ticker that runs it.
/// </summary>
internal sealed class TickerQInProcess
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly TickerFunctionContext _context = new();
    private ServiceProvider _services = null!; // Set by Prepare
    private ITickerExecutionTaskHandler _handler = null!; // Set by Prepare
    private TickerFunctionDelegate _function = null!; // Set by Prepare

    /// <summary>
    /// Registers TickerQ without background services, on its in-memory store, and takes the handler a worker
    /// calls for one job: from the service scope to the status update when the job is done.
    /// </summary>
    public void Prepare()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddTickerQ(static options => options.DisableBackgroundServices());
        _services = services.BuildServiceProvider();
        TickerFunctionProvider.Build();
        _handler = _services.GetRequiredService<ITickerExecutionTaskHandler>();
        _function = TickerFunctionProvider.TickerFunctions[ProcessTickerJob.FunctionName].Delegate;
    }

    public Task Call() => _function(default, _services, _context);

    public TimeTickerEntity CreateJob()
    {
        int id = Samples.Order.Id;
        return new TimeTickerEntity
        {
            Function = ProcessTickerJob.FunctionName,
            ExecutionTime = DateTime.UtcNow,
            Request = TickerHelper.CreateTickerRequest(new ProcessRequest(id, "report")),
        };
    }

    public byte[] Serialize() => TickerHelper.CreateTickerRequest(Samples.Order);

    public Order Deserialize(byte[] payload) => TickerHelper.ReadTickerRequest<Order>(payload);

    public void Run()
    {
        InternalFunctionContext context = new()
        {
            FunctionName = ProcessTickerJob.FunctionName,
            TickerId = Id,
            Type = TickerType.TimeTicker,
            ExecutionTime = DateTime.UtcNow,
            CachedDelegate = _function,
        };
        _handler.ExecuteTaskAsync(context, false).GetAwaiter().GetResult();
    }
}

/// <summary>The two arguments the other libraries' job takes, as TickerQ's single request object.</summary>
public sealed record ProcessRequest(int Id, string Name);

/// <summary>The same job that does almost nothing, for TickerQ.</summary>
public sealed class ProcessTickerJob
{
    public const string FunctionName = "Process";

    [TickerFunction(FunctionName)]
    public Task Process(TickerFunctionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
#endif
