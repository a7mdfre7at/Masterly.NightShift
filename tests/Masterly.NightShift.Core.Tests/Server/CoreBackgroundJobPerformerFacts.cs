using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Core.Tests.Common;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests.Server;

public class CoreBackgroundJobPerformerFacts : IDisposable
{
    private readonly Mock<JobActivator> _activator = new() { CallBase = true };
    private readonly PerformContextMock _context = new();

    private static readonly DateTime SomeDateTime = new(2014, 5, 30, 12, 0, 0);
    private static bool _methodInvoked;
    private static bool _disposed;

    [Fact]
    public void Ctor_ThrowsAnException_WhenActivatorIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            // ReSharper disable once AssignNullToNotNullAttribute
            () => new CoreBackgroundJobPerformer(null, null));

        Assert.Equal("activator", exception.ParamName);
    }

    [Fact]
    public void Ctor_DoesNotThrowAnException_WhenTaskSchedulerIsNull()
    {
        CoreBackgroundJobPerformer performer = new(_activator.Object, null);
        Assert.NotNull(performer);
    }

    [Fact, StaticLock]
    public void Perform_CanInvokeStaticMethods()
    {
        _methodInvoked = false;
        _context.BackgroundJob.Job = Job.FromExpression(() => StaticMethod());
        CoreBackgroundJobPerformer performer = CreatePerformer();

        performer.Perform(_context.Object);

        Assert.True(_methodInvoked);
    }

    [Fact, StaticLock]
    public void Perform_CanInvokeInstanceMethods()
    {
        _methodInvoked = false;
        _context.BackgroundJob.Job = Job.FromExpression<CoreBackgroundJobPerformerFacts>(x => x.InstanceMethod());
        CoreBackgroundJobPerformer performer = CreatePerformer();

        performer.Perform(_context.Object);

        Assert.True(_methodInvoked);
    }

    [Fact, StaticLock]
    public void Perform_ActivatesJob_WithinAScope()
    {
        CoreBackgroundJobPerformer performer = CreatePerformer();
        _context.BackgroundJob.Job = Job.FromExpression<CoreBackgroundJobPerformerFacts>(x => x.InstanceMethod());

        performer.Perform(_context.Object);

        _activator.Verify(x => x.BeginScope(It.IsNotNull<JobActivatorContext>()), Times.Once);
    }

    [Fact, StaticLock]
    public void Perform_DisposesDisposableInstance_AfterPerformance()
    {
        _disposed = false;
        _context.BackgroundJob.Job = Job.FromExpression<Disposable>(x => x.Method());
        CoreBackgroundJobPerformer performer = CreatePerformer();

        performer.Perform(_context.Object);

        Assert.True(_disposed);
    }

    [Fact, StaticLock]
    public void Perform_PassesArguments_ToACallingMethod()
    {
        // Arrange
        _methodInvoked = false;
        _context.BackgroundJob.Job = Job.FromExpression(() => MethodWithArguments("hello", 5));
        CoreBackgroundJobPerformer performer = CreatePerformer();

        // Act
        performer.Perform(_context.Object);

        // Assert - see the `MethodWithArguments` method.
        Assert.True(_methodInvoked);
    }

    [Fact, StaticLock]
    public void Perform_PassesCorrectDateTime_ReadFromArgumentsStoredByATypeConverter()
    {
        // Arrange
        _methodInvoked = false;
        TypeConverter typeConverter = TypeDescriptor.GetConverter(typeof(DateTime));
        string? convertedDate = typeConverter.ConvertToInvariantString(SomeDateTime);

        Type type = typeof(CoreBackgroundJobPerformerFacts);
        MethodInfo? method = type.GetMethod("MethodWithDateTimeArgument");

        // Arguments stored by older versions, read as a server reads them
        _context.BackgroundJob.Job = new Job(type, method, InvocationData.DeserializeArguments(method, [convertedDate]));
        CoreBackgroundJobPerformer performer = CreatePerformer();

        // Act
        performer.Perform(_context.Object);

        // Assert - see also the `MethodWithDateTimeArgument` method.
        Assert.True(_methodInvoked);
    }

    [Fact, StaticLock]
    public void Perform_PassesCorrectDateTime_ReadFromArgumentsStoredInTheOldFormat()
    {
        // Arrange
        _methodInvoked = false;
        string convertedDate = SomeDateTime.ToString("MM/dd/yyyy HH:mm:ss.ffff");

        Type type = typeof(CoreBackgroundJobPerformerFacts);
        MethodInfo? method = type.GetMethod("MethodWithDateTimeArgument");

        // Arguments stored by older versions, read as a server reads them
        _context.BackgroundJob.Job = new Job(type, method, InvocationData.DeserializeArguments(method, [convertedDate]));
        CoreBackgroundJobPerformer performer = CreatePerformer();

        // Act
        performer.Perform(_context.Object);

        // Assert - see also the `MethodWithDateTimeArgument` method.
        Assert.True(_methodInvoked);
    }

    [Fact, StaticLock]
    public void Perform_PassesCorrectDateTimeArguments()
    {
        // Arrange
        _methodInvoked = false;
        _context.BackgroundJob.Job = Job.FromExpression(() => MethodWithDateTimeArgument(SomeDateTime));
        CoreBackgroundJobPerformer performer = CreatePerformer();

        // Act
        performer.Perform(_context.Object);

        // Assert - see also the `MethodWithDateTimeArgument` method.
        Assert.True(_methodInvoked);
    }

    [Fact, StaticLock]
    public void Perform_WorksCorrectly_WithNullValues()
    {
        // Arrange
        _methodInvoked = false;
        _context.BackgroundJob.Job = Job.FromExpression(() => NullArgumentMethod(null));

        CoreBackgroundJobPerformer performer = CreatePerformer();
        // Act
        performer.Perform(_context.Object);

        // Assert - see also `NullArgumentMethod` method.
        Assert.True(_methodInvoked);
    }

    [Fact]
    public void Perform_ThrowsException_WhenActivatorThrowsAnException()
    {
        // Arrange
        InvalidOperationException exception = new();
        _activator.Setup(x => x.ActivateJob(It.IsAny<Type>())).Throws(exception);

        _context.BackgroundJob.Job = Job.FromExpression(() => InstanceMethod());
        CoreBackgroundJobPerformer performer = CreatePerformer();

        // Act
        Assert.Throws<InvalidOperationException>(
            () => performer.Perform(_context.Object));
    }

    [Fact]
    public void Perform_ThrowsPerformanceException_WhenActivatorReturnsNull()
    {
        _activator.Setup(x => x.ActivateJob(It.IsNotNull<Type>())).Returns(null);
        _context.BackgroundJob.Job = Job.FromExpression(() => InstanceMethod());
        CoreBackgroundJobPerformer performer = CreatePerformer();

        Assert.Throws<InvalidOperationException>(
            () => performer.Perform(_context.Object));
    }

    [Fact]
    public void Perform_ThrowsPerformanceException_OnArgumentsDeserializationFailure()
    {
        Type type = typeof(JobFacts);
        MethodInfo? method = type.GetMethod("MethodWithDateTimeArgument");
        _context.BackgroundJob.Job = new Job(type, method, "sdfa");
        CoreBackgroundJobPerformer performer = CreatePerformer();

        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(
            () => performer.Perform(_context.Object));

        Assert.NotNull(exception.InnerException);
    }

    [Fact, StaticLock]
    public void Perform_ThrowsPerformanceException_OnDisposalFailure()
    {
        _methodInvoked = false;
        _context.BackgroundJob.Job = Job.FromExpression<BrokenDispose>(x => x.Method());
        CoreBackgroundJobPerformer performer = CreatePerformer();

        Assert.Throws<InvalidOperationException>(
            () => performer.Perform(_context.Object));

        Assert.True(_methodInvoked);
    }

    [Fact]
    public void Perform_ThrowsPerformanceException_WithUnwrappedInnerException()
    {
        _context.BackgroundJob.Job = Job.FromExpression(() => ExceptionMethod());
        CoreBackgroundJobPerformer performer = CreatePerformer();

        JobPerformanceException thrownException = Assert.Throws<JobPerformanceException>(
            () => performer.Perform(_context.Object));

        Assert.IsType<InvalidOperationException>(thrownException.InnerException);
        Assert.Equal("exception", thrownException.InnerException.Message);
    }

    [Fact]
    public void Run_ThrowsPerformanceException_WithUnwrappedInnerException_ForTasks()
    {
        _context.BackgroundJob.Job = Job.FromExpression(() => TaskExceptionMethod());
        CoreBackgroundJobPerformer performer = CreatePerformer();

        JobPerformanceException thrownException = Assert.Throws<JobPerformanceException>(
            () => performer.Perform(_context.Object));

        Assert.IsType<InvalidOperationException>(thrownException.InnerException);
        Assert.Equal("exception", thrownException.InnerException.Message);
    }

    [Fact]
    public void Perform_ThrowsPerformanceException_WhenMethodThrownTaskCanceledException()
    {
        _context.BackgroundJob.Job = Job.FromExpression(() => TaskCanceledExceptionMethod());
        CoreBackgroundJobPerformer performer = CreatePerformer();

        JobPerformanceException thrownException = Assert.Throws<JobPerformanceException>(
            () => performer.Perform(_context.Object));

        Assert.IsType<TaskCanceledException>(thrownException.InnerException);
    }

    [Fact]
    public void Perform_RethrowsOperationCanceledException_WhenShutdownTokenIsCanceled()
    {
        // Arrange
        _context.BackgroundJob.Job = Job.FromExpression(() => CancelableJob(JobCancellationToken.Null));
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(true));
        _context.CancellationToken.Setup(x => x.ThrowIfCancellationRequested()).Throws<OperationCanceledException>();

        CoreBackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert
        Assert.Throws<OperationCanceledException>(() => performer.Perform(_context.Object));
    }

    [Fact]
    public void Run_RethrowsTaskCanceledException_WhenShutdownTokenIsCanceled()
    {
        // Arrange
        _context.BackgroundJob.Job = Job.FromExpression(() => CancelableJob(JobCancellationToken.Null));
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(true));
        _context.CancellationToken.Setup(x => x.ThrowIfCancellationRequested()).Throws<TaskCanceledException>();

        CoreBackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert
        Assert.Throws<TaskCanceledException>(() => performer.Perform(_context.Object));
    }

    [Fact]
    public void Run_RethrowsJobAbortedException()
    {
        // Arrange
        _context.BackgroundJob.Job = Job.FromExpression(() => CancelableJob(JobCancellationToken.Null));
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(true));
        _context.CancellationToken.Setup(x => x.ThrowIfCancellationRequested()).Throws<JobAbortedException>();

        CoreBackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert
        Assert.Throws<JobAbortedException>(() => performer.Perform(_context.Object));
    }

    [Fact]
    public void ThrowsJobPerformanceException_DoesInclude_JobId()
    {
        // Arrange
        _context.BackgroundJob.Job = Job.FromExpression(() => CancelableJob(JobCancellationToken.Null));
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
        _context.CancellationToken.Setup(x => x.ThrowIfCancellationRequested()).Throws<OperationCanceledException>();

        CoreBackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert
        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() => performer.Perform(_context.Object));
        Assert.Equal(_context.BackgroundJob.Id, exception.JobId);
    }

    [Fact]
    public void Run_ThrowsJobPerformanceException_InsteadOfOperationCanceled_WhenShutdownWasNOTInitiated()
    {
        // Arrange
        _context.BackgroundJob.Job = Job.FromExpression(() => CancelableJob(JobCancellationToken.Null));
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
        _context.CancellationToken.Setup(x => x.ThrowIfCancellationRequested()).Throws<OperationCanceledException>();

        CoreBackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert
        Assert.Throws<JobPerformanceException>(() => performer.Perform(_context.Object));
    }

    [Fact]
    public void Run_PassesStandardCancellationToken_IfThereIsCancellationTokenParameter()
    {
        // Arrange
        _context.BackgroundJob.Job = Job.FromExpression(() => CancelableJob(default(CancellationToken)));
        CancellationTokenSource source = new();
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(source.Token);
        CoreBackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert
        source.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => performer.Perform(_context.Object));
    }

    [Fact]
    public void Perform_PassesThePerformContext_IfThereIsAPerformContextParameter()
    {
        _context.BackgroundJob.Job = Job.FromExpression(() => PerformContextJob(null));
        CoreBackgroundJobPerformer performer = CreatePerformer();

        object? result = performer.Perform(_context.Object);

        Assert.Same(_context.Object, result);
    }

    [Fact]
    public void Perform_ReturnsValue_WhenCallingFunctionReturningValue()
    {
        _context.BackgroundJob.Job = Job.FromExpression<JobFacts.Instance>(x => x.FunctionReturningValue());
        CoreBackgroundJobPerformer performer = CreatePerformer();

        object? result = performer.Perform(_context.Object);

        Assert.Equal("Return value", result);
    }

    [Fact]
    public void Run_DoesNotReturnValue_WhenCallingFunctionReturningPlainTask()
    {
        _context.BackgroundJob.Job = Job.FromExpression<JobFacts.Instance>(x => x.FunctionReturningTask());
        CoreBackgroundJobPerformer performer = CreatePerformer();

        object? result = performer.Perform(_context.Object);

        Assert.Null(result);
    }

    [Fact]
    public void Run_DoesNotReturnValue_WhenCallingFunctionReturningValueTask()
    {
        _context.BackgroundJob.Job = Job.FromExpression<JobFacts.Instance>(x => x.FunctionReturningValueTask());
        CoreBackgroundJobPerformer performer = CreatePerformer();

        object? result = performer.Perform(_context.Object);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Run_ReturnsTaskResult_WhenCallingFunctionReturningGenericTask(bool continueOnCapturedContext)
    {
        _context.BackgroundJob.Job = Job.FromExpression<JobFacts.Instance>(x => x.FunctionReturningTaskResultingInString(continueOnCapturedContext));
        CoreBackgroundJobPerformer performer = CreatePerformer();

        object? result = performer.Perform(_context.Object);

        Assert.Equal("Return value", result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Run_ReturnsTaskResult_WhenCallingFunctionReturningValueTask(bool continueOnCapturedContext)
    {
        _context.BackgroundJob.Job = Job.FromExpression<JobFacts.Instance>(x => x.FunctionReturningValueTaskResultingInString(continueOnCapturedContext));
        CoreBackgroundJobPerformer performer = CreatePerformer();

        object? result = performer.Perform(_context.Object);

        Assert.Equal("Return value", result);
    }

    [Fact]
    public void Perform_ExecutesAsyncMethod_AlwaysWithinTheSameThread()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        _context.BackgroundJob.Job = Job.FromExpression(() => AsyncMethod(Thread.CurrentThread.ManagedThreadId));
        CoreBackgroundJobPerformer performer = CreatePerformer();

        object? result = performer.Perform(_context.Object);

        Assert.True((bool)result);
    }

    [Fact]
    public void Perform_ExecutesAsyncMethod_OnCustomScheduler_WhenItIsSet()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        MyCustomTaskScheduler scheduler = new();

        _context.BackgroundJob.Job = Job.FromExpression(() => TaskExceptionMethod());
        CoreBackgroundJobPerformer performer = CreatePerformer(scheduler);

        Assert.Throws<JobPerformanceException>(() => performer.Perform(_context.Object));

        Assert.True(scheduler.TasksPerformed > 1);
    }

    private static readonly AsyncLocal<string> AsyncLocal = new();

    [Fact]
    public void Perform_FlowsExistingAsyncLocal_WhenInvokingSynchronousMethod()
    {
        InvokeJobMethodWithFlowOfAsyncLocal(Job.FromExpression(() => CheckSynchronousAsyncLocal()));
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void CheckSynchronousAsyncLocal()
    {
        Assert.Equal("world", AsyncLocal.Value);
    }

    [Theory]
    [MemberData(nameof(GetSchedulers))]
    public void Perform_FlowsExistingAsyncLocal_WhenInvokingSimpleAsyncMethod(TaskScheduler scheduler)
    {
        InvokeJobMethodWithFlowOfAsyncLocal(Job.FromExpression(() => CheckSimpleAsyncLocal()), scheduler);
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static Task CheckSimpleAsyncLocal()
    {
        Assert.Equal("world", AsyncLocal.Value);
        return Task.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(GetSchedulers))]
    public void Perform_FlowsExistingAsyncLocal_WhenInvokingAsyncMethodWithAwait(TaskScheduler scheduler)
    {
        InvokeJobMethodWithFlowOfAsyncLocal(Job.FromExpression(() => CheckAsyncAwaitAsyncLocal()), scheduler);
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
    public static async Task CheckAsyncAwaitAsyncLocal()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
    {
        Assert.Equal("world", AsyncLocal.Value);
    }

    [Theory]
    [MemberData(nameof(GetSchedulers))]
    public void Perform_FlowsExistingAsyncLocal_WhenInvokingAsyncMethodAndSettingAsyncLocalAfterAwait(TaskScheduler scheduler)
    {
        InvokeJobMethodWithFlowOfAsyncLocal(Job.FromExpression(() => CheckAsyncLocalAfterAwait()), scheduler);
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static async Task CheckAsyncLocalAfterAwait()
    {
        await Task.Yield();

        Assert.Equal("world", AsyncLocal.Value);
    }

    [Theory]
    [MemberData(nameof(GetSchedulers))]
    public void Perform_FlowsExistingAsyncLocal_WhenInvokingAsyncMethodAndSettingAsyncLocalInTaskContinuation(TaskScheduler scheduler)
    {
        InvokeJobMethodWithFlowOfAsyncLocal(Job.FromExpression(() => CheckAsyncLocalInExplicitTaskContinuation()), scheduler);
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static Task CheckAsyncLocalInExplicitTaskContinuation()
    {
        return Task.FromResult(true).ContinueWith(_ =>
        {
            Assert.Equal("world", AsyncLocal.Value);
        });
    }

    private void InvokeJobMethodWithFlowOfAsyncLocal(Job job, TaskScheduler scheduler = null)
    {
        AsyncLocal.Value = "world";

        int parallelism = Math.Max(10, Environment.ProcessorCount);
        Parallel.For(0, parallelism, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, _ =>
        {
            _context.BackgroundJob.Job = job;
            CoreBackgroundJobPerformer performer = CreatePerformer(scheduler);

            performer.Perform(_context.Object);

            Assert.Equal("world", AsyncLocal.Value);
        });

        Assert.Equal("world", AsyncLocal.Value);
    }

    [Fact]
    public void Perform_DoesNotLeakAsyncLocal_WhenInvokingSynchronousMethod()
    {
        InvokeJobMethodAndAssertAsyncLocalIsNull(Job.FromExpression(() => SynchronousAsyncLocal()));
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void SynchronousAsyncLocal()
    {
        Assert.Null(AsyncLocal.Value);
        AsyncLocal.Value = "hello";
    }

    [Theory]
    [MemberData(nameof(GetSchedulers))]
    public void Perform_DoesNotLeakAsyncLocal_WhenInvokingSimpleAsyncMethod(TaskScheduler scheduler)
    {
        InvokeJobMethodAndAssertAsyncLocalIsNull(Job.FromExpression(() => SimpleAsyncLocal()), scheduler);
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static Task SimpleAsyncLocal()
    {
        Assert.Null(AsyncLocal.Value);
        AsyncLocal.Value = "hello";
        return Task.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(GetSchedulers))]
    public void Perform_DoesNotLeakAsyncLocal_WhenInvokingAsyncMethodWithAwait(TaskScheduler scheduler)
    {
        InvokeJobMethodAndAssertAsyncLocalIsNull(Job.FromExpression(() => AsyncAwaitAsyncLocal()), scheduler);
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
    public static async Task AsyncAwaitAsyncLocal()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
    {
        Assert.Null(AsyncLocal.Value);
        AsyncLocal.Value = "hello";
    }

    [Theory]
    [MemberData(nameof(GetSchedulers))]
    public void Perform_DoesNotLeakAsyncLocal_WhenInvokingAsyncMethodAndSettingAsyncLocalAfterAwait(TaskScheduler scheduler)
    {
        InvokeJobMethodAndAssertAsyncLocalIsNull(Job.FromExpression(() => AsyncLocalSetAfterAwait()), scheduler);
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static async Task AsyncLocalSetAfterAwait()
    {
        await Task.Yield();

        Assert.Null(AsyncLocal.Value);
        AsyncLocal.Value = "hello";
    }

    [Theory]
    [MemberData(nameof(GetSchedulers))]
    public void Perform_DoesNotLeakAsyncLocal_WhenInvokingAsyncMethodAndSettingAsyncLocalInTaskContinuation(TaskScheduler scheduler)
    {
        InvokeJobMethodAndAssertAsyncLocalIsNull(Job.FromExpression(() => AsyncLocalSetInExplicitTaskContinuation()), scheduler);
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static Task AsyncLocalSetInExplicitTaskContinuation()
    {
        return Task.FromResult(true).ContinueWith(_ =>
        {
            Assert.Null(AsyncLocal.Value);
            AsyncLocal.Value = "hello";
        });
    }

    private void InvokeJobMethodAndAssertAsyncLocalIsNull(Job job, TaskScheduler scheduler = null)
    {
        int parallelism = Math.Max(10, Environment.ProcessorCount);
        Parallel.For(0, parallelism, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, _ =>
        {
            _context.BackgroundJob.Job = job;
            CoreBackgroundJobPerformer performer = CreatePerformer(scheduler);

            performer.Perform(_context.Object);

            Assert.Null(AsyncLocal.Value);
        });

        Assert.Null(AsyncLocal.Value);
    }

    public static IEnumerable<object[]> GetSchedulers()
    {
        yield return new object[] { null };
        yield return new object[] { TaskScheduler.Default };
        yield return new object[] { new Masterly.NightShift.Processing.BackgroundTaskScheduler(threadCount: 1) };
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public void InstanceMethod()
    {
        _methodInvoked = true;
    }

    [UsedImplicitly]
    public class Disposable : IDisposable
    {
        [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
        [SuppressMessage("Performance", "CA1822:Mark members as static")]
        public void Method()
        {
            _methodInvoked = true;
        }

        public void Dispose()
        {
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    [UsedImplicitly]
    public class BrokenDispose : IDisposable
    {
        [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
        [SuppressMessage("Performance", "CA1822:Mark members as static")]
        public void Method()
        {
            _methodInvoked = true;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            throw new InvalidOperationException();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void NullArgumentMethod(string[] argument)
    {
        _methodInvoked = true;
        Assert.Null(argument);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static PerformContext PerformContextJob(PerformContext context)
    {
        return context;
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void CancelableJob(IJobCancellationToken token)
    {
        token.ThrowIfCancellationRequested();
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void CancelableJob(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public void MethodWithDateTimeArgument(DateTime argument)
    {
        _methodInvoked = true;

        Assert.Equal(SomeDateTime, argument);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void StaticMethod()
    {
        _methodInvoked = true;
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public void MethodWithArguments(string stringArg, int intArg)
    {
        _methodInvoked = true;

        Assert.Equal("hello", stringArg);
        Assert.Equal(5, intArg);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void ExceptionMethod()
    {
        throw new InvalidOperationException("exception");
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static async Task TaskExceptionMethod()
    {
        await Task.Yield();

        throw new InvalidOperationException("exception");
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void TaskCanceledExceptionMethod()
    {
        throw new TaskCanceledException();
    }

    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static async Task<bool> AsyncMethod(int threadId)
    {
        if (threadId != Thread.CurrentThread.ManagedThreadId)
        {
            throw new InvalidOperationException("Start");
        }

        await Task.Yield();

        if (threadId != Thread.CurrentThread.ManagedThreadId)
        {
            throw new InvalidOperationException("After Yield");
        }

        await Task.Delay(1).ConfigureAwait(true);

        if (threadId != Thread.CurrentThread.ManagedThreadId)
        {
            throw new InvalidOperationException("After Delay");
        }

        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4, TaskScheduler = TaskScheduler.Current },
            i =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            });

        if (threadId != Thread.CurrentThread.ManagedThreadId)
        {
            throw new InvalidOperationException("After Parallel.For");
        }

        await Task.Delay(1).ConfigureAwait(false);

        if (!Thread.CurrentThread.IsThreadPoolThread)
        {
            throw new InvalidOperationException("Not running on ThreadPool after ConfigureAwait(false)");
        }

        return true;
    }

    private CoreBackgroundJobPerformer CreatePerformer(TaskScheduler taskScheduler = null)
    {
        return new CoreBackgroundJobPerformer(_activator.Object, taskScheduler);
    }

    private sealed class MyCustomTaskScheduler : TaskScheduler
    {
        public int TasksPerformed { get; private set; }

        protected override void QueueTask(Task task)
        {
            TryExecuteTask(task);
            TasksPerformed++;
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            return false;
        }

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            return [];
        }
    }
}
