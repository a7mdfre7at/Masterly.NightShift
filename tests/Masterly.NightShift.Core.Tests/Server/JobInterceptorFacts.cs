using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests.Server;

public class JobInterceptorFacts : IDisposable
{
    private static readonly List<string> Log = [];
    private readonly PerformContextMock _context = new();

    public JobInterceptorFacts()
    {
        GlobalJobInterceptors.Clear();
        Log.Clear();
    }

    public void Dispose()
    {
        GlobalJobInterceptors.Clear();
    }

    [Fact, StaticLock]
    public void Interceptors_RunInOrder_AroundTheMethod_TypeLevelFirst()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Intercepted>(x => x.Method());

        object result = Perform();

        Assert.Equal("result", result);
        Assert.Equal(
            new[] { "type:before", "method:before", "method", "method:after", "type:after" },
            Log);
    }

    [Fact, StaticLock]
    public void GlobalInterceptors_RunOutermost()
    {
        GlobalJobInterceptors.Add(new LoggingInterceptor("global"));
        _context.BackgroundJob.Job = Job.FromExpression<Intercepted>(x => x.Method());

        Perform();

        Assert.Equal("global:before", Log[0]);
        Assert.Equal("global:after", Log[^1]);
    }

    [Fact, StaticLock]
    public void Order_SortsInterceptors_LowerFirst()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Intercepted>(x => x.Ordered());

        Perform();

        Assert.Equal(new[] { "type:before", "first:before", "second:before", "ordered", "second:after", "first:after", "type:after" }, Log);
    }

    [Fact, StaticLock]
    public void Interceptor_CanReplaceArguments()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Intercepted>(x => x.Echo("original"));

        object result = Perform();

        Assert.Equal("replaced", result);
    }

    [Fact, StaticLock]
    public void Interceptor_CanShortCircuit_WithoutCallingTheMethod()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Intercepted>(x => x.Skipped());

        object result = Perform();

        Assert.Equal("short-circuit", result);
        Assert.DoesNotContain("skipped", Log);
    }

    [Fact, StaticLock]
    public void Interceptor_CanProceedSeveralTimes()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Intercepted>(x => x.Counted());

        object result = Perform();

        Assert.Equal(3, result);
        Assert.Equal(3, Log.FindAll(x => x == "counted").Count);
    }

    [Fact, StaticLock]
    public void AsyncInterceptor_WrapsAsyncMethods_AndKeepsTheReturnType()
    {
        _context.BackgroundJob.Job = Job.FromExpression<AsyncIntercepted>(x => x.AsyncMethod(20));

        object result = Perform();

        Assert.Equal(21, result);
        Assert.Equal(new[] { "async:before", "async-method", "async:after" }, Log);
    }

    [Fact, StaticLock]
    public void AsyncInterceptor_WrapsNonGenericTaskMethods()
    {
        _context.BackgroundJob.Job = Job.FromExpression<AsyncIntercepted>(x => x.AsyncVoid());

        object result = Perform();

        Assert.Null(result);
        Assert.Equal(new[] { "async:before", "async-void", "async:after" }, Log);
    }

    [Fact, StaticLock]
    public void AsyncInterceptor_WrapsSynchronousMethods()
    {
        _context.BackgroundJob.Job = Job.FromExpression<AsyncIntercepted>(x => x.SyncWithAsyncInterceptor());

        object result = Perform();

        Assert.Equal("sync", result);
        Assert.Equal(new[] { "async:before", "sync", "async:after" }, Log);
    }

    [Fact, StaticLock]
    public void AsyncInterceptor_WrapsValueTaskMethods()
    {
        _context.BackgroundJob.Job = Job.FromExpression<AsyncIntercepted>(x => x.ValueTaskMethod());

        object result = Perform();

        Assert.Equal(5, result);
    }

    [Fact, StaticLock]
    public void Exceptions_FromTheMethod_PassThroughInterceptors_AsJobPerformanceException()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Intercepted>(x => x.Throwing());

        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => Perform());

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("type:exception", Log);
    }

    [Fact, StaticLock]
    public void Invocation_ExposesJobAndInvoker()
    {
        _context.BackgroundJob.Job = Job.FromExpression<Intercepted>(x => x.Method());
        JobInvocation captured = null;
        GlobalJobInterceptors.Add(new CapturingInterceptor(x => captured = x));

        Perform();

        Assert.NotNull(captured);
        Assert.Same(_context.Object.BackgroundJob.Job, captured.Job);
        Assert.Equal(typeof(string), captured.ReturnType);
        Assert.False(captured.ReturnsTaskLike);
        Assert.IsType<Intercepted>(captured.Instance);
        Assert.NotNull(captured.Invoker);
    }

    [Fact]
    public void GlobalJobInterceptors_Add_ThrowsAnException_WhenInterceptorIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => GlobalJobInterceptors.Add(null));
    }

    [Fact, StaticLock]
    public void GlobalJobInterceptors_Remove_RemovesRegisteredInterceptor()
    {
        LoggingInterceptor interceptor = new("x");
        GlobalJobInterceptors.Add(interceptor);

        Assert.True(GlobalJobInterceptors.Remove(interceptor));
        Assert.False(GlobalJobInterceptors.Remove(interceptor));
        Assert.Empty(GlobalJobInterceptors.Interceptors);
    }

    private object Perform()
    {
        CoreBackgroundJobPerformer performer = new(new JobActivator(), null);
        return performer.Perform(_context.Object);
    }

    [Logging("type")]
    public class Intercepted
    {
        [Logging("method")]
        public string Method()
        {
            Log.Add("method");
            return "result";
        }

        [Logging("second", Order = 2), Logging("first", Order = 1)]
        public void Ordered()
        {
            Log.Add("ordered");
        }

        [ReplaceFirstArgument("replaced")]
        public string Echo(string value) => value;

        [ShortCircuit]
        public string Skipped()
        {
            Log.Add("skipped");
            return "never";
        }

        [ProceedThreeTimes]
        public int Counted()
        {
            Log.Add("counted");
            return 1;
        }

        public void Throwing() => throw new InvalidOperationException("boom");
    }

    // No type-level interceptor here: a synchronous interceptor around an async method sees the task,
    // not its completion, which would interleave the log lines these tests check.
    public class AsyncIntercepted
    {
        [AsyncLogging("async")]
        public async Task<int> AsyncMethod(int value)
        {
            await Task.Yield();
            Log.Add("async-method");
            return value + 1;
        }

        [AsyncLogging("async")]
        public async Task AsyncVoid()
        {
            await Task.Yield();
            Log.Add("async-void");
        }

        [AsyncLogging("async")]
        public string SyncWithAsyncInterceptor()
        {
            Log.Add("sync");
            return "sync";
        }

        [AsyncLogging("async")]
        public ValueTask<int> ValueTaskMethod() => new(5);
    }

    private sealed class LoggingAttribute(string name) : JobInterceptorAttribute
    {
        private readonly string _name = name;

        public override object Intercept(JobInvocation invocation)
        {
            Log.Add(_name + ":before");
            try
            {
                object? result = invocation.Proceed();
                Log.Add(_name + ":after");
                return result;
            }
            catch
            {
                Log.Add(_name + ":exception");
                throw;
            }
        }
    }

    private sealed class AsyncLoggingAttribute(string name) : AsyncJobInterceptorAttribute
    {
        private readonly string _name = name;

        protected override async Task<object> InterceptAsync(JobInvocation invocation)
        {
            Log.Add(_name + ":before");
            object? result = await invocation.ProceedAsync();
            Log.Add(_name + ":after");
            return result;
        }
    }

    private sealed class ReplaceFirstArgumentAttribute(object value) : JobInterceptorAttribute
    {
        private readonly object _value = value;

        public override object Intercept(JobInvocation invocation)
        {
            invocation.Arguments[0] = _value;
            return invocation.Proceed();
        }
    }

    private sealed class ShortCircuitAttribute : JobInterceptorAttribute
    {
        public override object Intercept(JobInvocation invocation) => "short-circuit";
    }

    private sealed class ProceedThreeTimesAttribute : JobInterceptorAttribute
    {
        public override object Intercept(JobInvocation invocation)
        {
            int sum = 0;
            for (int i = 0; i < 3; i++)
            {
                sum += (int)invocation.Proceed();
            }

            return sum;
        }
    }

    private sealed class LoggingInterceptor(string name) : IJobInterceptor
    {
        private readonly string _name = name;

        public object Intercept(JobInvocation invocation)
        {
            Log.Add(_name + ":before");
            object? result = invocation.Proceed();
            Log.Add(_name + ":after");
            return result;
        }
    }

    private sealed class CapturingInterceptor(Action<JobInvocation> capture) : IJobInterceptor
    {
        private readonly Action<JobInvocation> _capture = capture;

        public object Intercept(JobInvocation invocation)
        {
            _capture(invocation);
            return invocation.Proceed();
        }
    }
}
