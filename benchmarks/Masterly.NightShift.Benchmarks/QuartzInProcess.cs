#if NET10_0_OR_GREATER
using System.Linq.Expressions;
using System.Reflection;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl;

namespace NightShiftBenchmarks;

/// <summary>
/// The in-process operations through Quartz.NET. A job is an <see cref="IJob"/> class, its argument is a typed
/// input stored as JSON, and "creating a job" is building the one-shot trigger that runs it.
/// </summary>
internal sealed class QuartzInProcess
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static readonly JobKey Key = new("process");
    private readonly SystemTextJsonJobInputSerializer _serializer = new();
    private readonly IJob _job = new ProcessQuartzJob();
    private readonly IJobExecutionContext _context = DispatchProxy.Create<IJobExecutionContext, NoOpProxy>();
    private Func<CancellationToken, ValueTask> _run = null!; // Set by Prepare

    /// <summary>
    /// Builds a scheduler on Quartz's in-memory store, never started, and compiles a call to the internal
    /// <c>JobRunShell</c>: the code a worker thread runs for one firing, from creating the job instance to
    /// telling the store the job is done.
    /// </summary>
    public void Prepare()
    {
        IScheduler scheduler = Wait(QuartzSchedulerBuilder.Create(static _ => { }).BuildScheduler());
        IJobDetail detail = JobBuilder.Create<ProcessQuartzJob>().WithIdentity(Key).StoreDurably().Build();
        Wait(scheduler.AddJob(detail, default));

        IOperableTrigger trigger = (IOperableTrigger)TriggerBuilder.Create().ForJob(Key).StartNow().Build();
        // The store gives a firing its ID when it hands the trigger to the scheduler
        trigger.FireInstanceId = "1";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TriggerFiredBundle bundle = new()
        {
            JobDetail = detail,
            Trigger = trigger,
            Recovering = false,
            FireTimeUtc = now,
            ScheduledFireTimeUtc = now,
            PreviousFireTimeUtc = null,
            NextFireTimeUtc = null,
        };

        object core = Member(scheduler, "scheduler");
        object factory = Member(Member(core, "resources"), "JobRunShellFactory");
        MethodInfo create = Method(factory.GetType(), "CreateJobRunShell");
        MethodInfo initialize = Method(create.ReturnType, "Initialize");
        MethodInfo run = Method(create.ReturnType, "Run");

        ParameterExpression token = Expression.Parameter(typeof(CancellationToken));
        ParameterExpression shell = Expression.Variable(create.ReturnType);
        BlockExpression body = Expression.Block(
            [shell],
            Expression.Assign(shell, Expression.Call(Expression.Constant(factory), create, Expression.Constant(bundle))),
            Expression.Call(shell, initialize, Expression.Constant(core), token),
            Expression.Call(shell, run, token));
        _run = Expression.Lambda<Func<CancellationToken, ValueTask>>(body, token).Compile();
    }

    public ValueTask Call() => _job.Execute(_context, default);

    public ITrigger CreateJob()
    {
        int id = Samples.Order.Id;
        return TriggerBuilder.Create().ForJob(Key).UsingJobData("id", id).UsingJobData("name", "report").StartNow().Build();
    }

    public string Serialize() => _serializer.Serialize(Samples.Order);

    public Order Deserialize(string payload) =>
        _serializer.Deserialize<Order>(payload) ?? throw new InvalidOperationException("Quartz read the input as null.");

    public void Run() => _run(default).AsTask().GetAwaiter().GetResult();

    private static object Member(object owner, string name)
    {
        Type type = owner.GetType();
        object? value = type.GetField(name, Hidden)?.GetValue(owner) ?? type.GetProperty(name, Hidden)?.GetValue(owner);
        return value ?? throw new InvalidOperationException($"{type.Name}.{name} was not found: Quartz's internals have changed.");
    }

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, Hidden) ?? throw new InvalidOperationException($"{type.Name}.{name} was not found: Quartz's internals have changed.");

    private static void Wait(ValueTask task) => task.AsTask().GetAwaiter().GetResult();

    private static T Wait<T>(ValueTask<T> task) => task.AsTask().GetAwaiter().GetResult();
}

/// <summary>The same job that does almost nothing, for Quartz.NET.</summary>
public sealed class ProcessQuartzJob : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
#endif
