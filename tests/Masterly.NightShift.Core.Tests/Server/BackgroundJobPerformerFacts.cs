using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Server;
using Moq.Sequences;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests.Server;

public class BackgroundJobPerformerFacts
{
    private readonly PerformContextMock _context;
    private readonly Mock<IBackgroundJobPerformer> _innerPerformer;
    private readonly IList<object> _filters;
    private readonly Mock<IJobFilterProvider> _filterProvider;

    public BackgroundJobPerformerFacts()
    {
        _context = new PerformContextMock();
        _innerPerformer = new Mock<IBackgroundJobPerformer>();

        _filters = [];
        _filterProvider = new Mock<IJobFilterProvider>();
        _filterProvider.Setup(x => x.GetFilters(It.IsNotNull<Job>())).Returns(
            _filters.Select(f => new JobFilter(f, JobFilterScope.Type, null)));
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenFilterProvider_IsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundJobPerformer(null, _innerPerformer.Object));

        Assert.Equal("filterProvider", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenInnerPerformer_IsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundJobPerformer(_filterProvider.Object, (IBackgroundJobPerformer)null));

        Assert.Equal("innerPerformer", exception.ParamName);
    }

    [Fact]
    public void Run_ThrowsAnException_WhenContextIsNull()
    {
        BackgroundJobPerformer performer = CreatePerformer();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => performer.Perform(null));

        Assert.Equal("context", exception.ParamName);
    }

    [Fact]
    public void Run_CallsTheRunMethod_OfInnerProcess()
    {
        BackgroundJobPerformer performer = CreatePerformer();

        performer.Perform(_context.Object);

        _innerPerformer.Verify(x => x.Perform(_context.Object), Times.Once);
    }

    [Fact]
    public void Run_StoresJobReturnValueInPerformedContext()
    {
        // Arrange
        Mock<IServerFilter> filter = CreateFilter<IServerFilter>();
        BackgroundJobPerformer performer = CreatePerformer();

        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Returns("Returned value");

        // Act
        performer.Perform(_context.Object);

        // Assert
        filter.Verify(
            x => x.OnPerformed(It.Is<PerformedContext>(context => (string)context.Result == "Returned value")));
    }

    [Fact]
    public void Run_ReturnsValueReturnedByJob()
    {
        // Arrange
        // ReSharper disable once UnusedVariable
        Mock<IServerFilter> filter = CreateFilter<IServerFilter>();
        BackgroundJobPerformer performer = CreatePerformer();

        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Returns("Returned value");

        // Act
        object? result = performer.Perform(_context.Object);

        // Assert
        Assert.Equal("Returned value", result);
    }

    [Fact]
    public void Run_DoesNotCatchExceptions()
    {
        // Arrange
        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Throws<InvalidOperationException>();

        BackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => performer.Perform(_context.Object));
    }

    [Fact]
    public void Run_CallsExceptionFilter_OnException()
    {
        // Arrange
        Mock<IServerExceptionFilter> filter = CreateFilter<IServerExceptionFilter>();

        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Throws<InvalidOperationException>();

        BackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => performer.Perform(_context.Object));

        filter.Verify(x => x.OnServerException(It.Is<ServerExceptionContext>(context =>
            context.Exception is InvalidOperationException)));
    }

    [Fact, Sequence]
    public void Run_CallsExceptionFilters_InReverseOrder()
    {
        // Arrange
        Mock<IServerExceptionFilter> filter1 = CreateFilter<IServerExceptionFilter>();
        Mock<IServerExceptionFilter> filter2 = CreateFilter<IServerExceptionFilter>();

        filter2.Setup(x => x.OnServerException(It.IsAny<ServerExceptionContext>())).InSequence();
        filter1.Setup(x => x.OnServerException(It.IsAny<ServerExceptionContext>())).InSequence();

        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Throws<InvalidOperationException>();

        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        Assert.Throws<InvalidOperationException>(() => performer.Perform(_context.Object));

        // Assert - see the `SequenceAttribute` class.
    }

    [Fact]
    public void Run_EatsException_WhenItWasHandlerByFilter()
    {
        // Arrange
        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Throws<InvalidOperationException>();

        Mock<IServerExceptionFilter> filter = CreateFilter<IServerExceptionFilter>();
        filter.Setup(x => x.OnServerException(It.IsAny<ServerExceptionContext>()))
            .Callback((ServerExceptionContext x) => x.ExceptionHandled = true);

        BackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert does not throw
        performer.Perform(_context.Object);
    }

    [Fact, Sequence]
    public void Run_CallsServerFilters_BeforeAndAfterTheCreationOfAJob()
    {
        // Arrange
        Mock<IServerFilter> filter = CreateFilter<IServerFilter>();

        filter.Setup(x => x.OnPerforming(It.IsNotNull<PerformingContext>()))
            .InSequence();

        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .InSequence();

        filter.Setup(x => x.OnPerformed(It.IsNotNull<PerformedContext>()))
            .InSequence();

        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        performer.Perform(_context.Object);

        // Assert - see the `SequenceAttribute` class.
    }

    [Fact, Sequence]
    public void Run_WrapsFilterCalls_OneIntoAnother()
    {
        // Arrange
        Mock<IServerFilter> outerFilter = CreateFilter<IServerFilter>();
        Mock<IServerFilter> innerFilter = CreateFilter<IServerFilter>();

        outerFilter.Setup(x => x.OnPerforming(It.IsAny<PerformingContext>())).InSequence();
        innerFilter.Setup(x => x.OnPerforming(It.IsAny<PerformingContext>())).InSequence();
        innerFilter.Setup(x => x.OnPerformed(It.IsAny<PerformedContext>())).InSequence();
        outerFilter.Setup(x => x.OnPerformed(It.IsAny<PerformedContext>())).InSequence();

        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        performer.Perform(_context.Object);

        // Assert - see the `SequenceAttribute` class.
    }

    [Fact]
    public void Run_DoesNotCallBoth_Perform_And_OnPerforming_WhenFilterCancelsThis()
    {
        // Arrange
        Mock<IServerFilter> filter = CreateFilter<IServerFilter>();

        filter.Setup(x => x.OnPerforming(It.IsAny<PerformingContext>()))
            .Callback((PerformingContext x) => x.Canceled = true);

        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        performer.Perform(_context.Object);

        // Assert
        _innerPerformer.Verify(x => x.Perform(_context.Object), Times.Never);

        filter.Verify(x => x.OnPerformed(It.IsAny<PerformedContext>()), Times.Never);
    }

    [Fact]
    public void Run_TellsOuterFilter_AboutTheCancellationOfCreation()
    {
        // Arrange
        Mock<IServerFilter> outerFilter = CreateFilter<IServerFilter>();
        Mock<IServerFilter> innerFilter = CreateFilter<IServerFilter>();

        innerFilter.Setup(x => x.OnPerforming(It.IsAny<PerformingContext>()))
            .Callback((PerformingContext context) => context.Canceled = true);

        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        performer.Perform(_context.Object);

        // Assert
        outerFilter.Verify(x => x.OnPerformed(It.Is<PerformedContext>(context => context.Canceled)));
    }

    [Fact]
    public void Run_DoesNotCall_Perform_And_OnPerformed_WhenExceptionOccured_DuringPerformingPhase()
    {
        // Arrange
        Mock<IServerFilter> filter = CreateFilter<IServerFilter>();

        filter.Setup(x => x.OnPerforming(It.IsAny<PerformingContext>()))
            .Throws<InvalidOperationException>();

        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(
            () => performer.Perform(_context.Object));

        // Assert
        Assert.IsType<InvalidOperationException>(exception.InnerException);

        _innerPerformer.Verify(x => x.Perform(It.IsAny<PerformContext>()), Times.Never);

        filter.Verify(x => x.OnPerformed(It.IsAny<PerformedContext>()), Times.Never);
    }

    [Fact]
    public void Run_TellsFiltersAboutException_WhenItIsOccured_DuringThePerformanceOfAJob()
    {
        // Arrange
        Mock<IServerFilter> filter = CreateFilter<IServerFilter>();

        InvalidOperationException exception = new();
        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Throws(exception);

        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        Assert.Throws<InvalidOperationException>(() => performer.Perform(_context.Object));

        // Assert
        filter.Verify(x => x.OnPerformed(It.Is<PerformedContext>(
            context => context.Exception == exception)));
    }

    [Fact]
    public void Run_TellsOuterFilters_AboutAllExceptions()
    {
        // Arrange
        Mock<IServerFilter> outerFilter = CreateFilter<IServerFilter>();
        // ReSharper disable once UnusedVariable
        Mock<IServerFilter> innerFilter = CreateFilter<IServerFilter>();

        InvalidOperationException exception = new();
        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Throws(exception);

        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        Assert.Throws<InvalidOperationException>(() => performer.Perform(_context.Object));

        outerFilter.Verify(x => x.OnPerformed(It.Is<PerformedContext>(context => context.Exception == exception)));
    }

    [Fact]
    public void Run_DoesNotThrow_HandledExceptions()
    {
        // Arrange
        Mock<IServerFilter> filter = CreateFilter<IServerFilter>();

        InvalidOperationException exception = new();
        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Throws(exception);

        filter.Setup(x => x.OnPerformed(It.Is<PerformedContext>(context => context.Exception == exception)))
            .Callback((PerformedContext x) => x.ExceptionHandled = true);

        BackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert does not throw
        performer.Perform(_context.Object);
    }

    [Fact]
    public void Run_TellsOuterFilter_EvenAboutHandledException()
    {
        // Arrange
        Mock<IServerFilter> outerFilter = CreateFilter<IServerFilter>();
        Mock<IServerFilter> innerFilter = CreateFilter<IServerFilter>();

        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Throws<InvalidOperationException>();

        innerFilter.Setup(x => x.OnPerformed(It.IsAny<PerformedContext>()))
            .Callback((PerformedContext x) => x.ExceptionHandled = true);

        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        performer.Perform(_context.Object);

        // Assert
        outerFilter.Verify(x => x.OnPerformed(It.Is<PerformedContext>(context => context.Exception != null)));
    }

    [Fact]
    public void Run_WrapsOnPerformedException_IntoJobPerformanceException()
    {
        // Arrange
        Mock<IServerFilter> filter = CreateFilter<IServerFilter>();
        filter.Setup(x => x.OnPerformed(It.IsAny<PerformedContext>()))
            .Throws<InvalidOperationException>();

        BackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert
        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() =>
            performer.Perform(_context.Object));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void Run_WrapsOnPerformedException_OccuredAfterAnotherException_IntoJobPerformanceException()
    {
        // Arrange
        Mock<IServerFilter> filter = CreateFilter<IServerFilter>();
        filter.Setup(x => x.OnPerformed(It.IsAny<PerformedContext>()))
            .Throws<InvalidOperationException>();

        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Throws<ArgumentNullException>();

        BackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert
        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(() =>
            performer.Perform(_context.Object));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void Run_ExceptionFiltersAreNOTInvoked_OnJobAbortedException()
    {
        // Arrange
        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Throws<JobAbortedException>();

        Mock<IServerExceptionFilter> filter = CreateFilter<IServerExceptionFilter>();
        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        Assert.Throws<JobAbortedException>(() => performer.Perform(_context.Object));

        // Assert
        filter.Verify(
            x => x.OnServerException(It.IsAny<ServerExceptionContext>()),
            Times.Never);
    }

    [Fact]
    public void Run_ExceptionFiltersAreNOTInvoked_OnOperationCanceledException_WhenShutdownTokenIsCanceled()
    {
        // Arrange
        CancellationTokenSource cts = new();
        cts.Cancel();

        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(cts.Token);
        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Throws<OperationCanceledException>();

        Mock<IServerExceptionFilter> filter = CreateFilter<IServerExceptionFilter>();
        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        Assert.Throws<OperationCanceledException>(
            () => performer.Perform(_context.Object));

        // Assert
        filter.Verify(
            x => x.OnServerException(It.IsAny<ServerExceptionContext>()),
            Times.Never);
    }

    [Fact]
    public void Run_ExceptionFiltersAreInvoked_OnOperationCanceledException_WhenShutdownTokenIsNOTCanceled()
    {
        // Arrange
        _innerPerformer
            .Setup(x => x.Perform(_context.Object))
            .Throws<OperationCanceledException>();

        Mock<IServerExceptionFilter> filter = CreateFilter<IServerExceptionFilter>();
        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        Assert.Throws<OperationCanceledException>(
            () => performer.Perform(_context.Object));

        // Assert
        filter.Verify(
            x => x.OnServerException(It.IsAny<ServerExceptionContext>()),
            Times.Once);
    }

    [Fact]
    public void Run_ThrowsOperationCanceledException_OccurredInPreFilterMethods_WhenShutdownTokenIsCanceled()
    {
        // Arrange
        CancellationTokenSource cts = new();
        cts.Cancel();

        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(cts.Token);
        Mock<IServerFilter> filter = CreateFilter<IServerFilter>();
        filter.Setup(x => x.OnPerforming(It.IsAny<PerformingContext>()))
            .Throws<OperationCanceledException>();

        BackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert
        Assert.Throws<OperationCanceledException>(
            () => performer.Perform(_context.Object));
    }

    [Fact]
    public void Run_ThrowsJobPerformanceException_InsteadOfOperationCanceled_OccurredInPreFilterMethods_WhenShutdownTokenIsNotCanceled()
    {
        // Arrange
        Mock<IServerFilter> filter = CreateFilter<IServerFilter>();
        filter.Setup(x => x.OnPerforming(It.IsAny<PerformingContext>()))
            .Throws<OperationCanceledException>();

        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(
            () => performer.Perform(_context.Object));

        // Assert
        Assert.IsType<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public void Run_ThrowsOperationCanceledException_OccurredInPostFilterMethods_WhenShutdownTokenIsCanceled()
    {
        // Arrange
        CancellationTokenSource cts = new();
        cts.Cancel();

        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(cts.Token);
        Mock<IServerFilter> filter = CreateFilter<IServerFilter>();
        filter.Setup(x => x.OnPerformed(It.IsAny<PerformedContext>()))
            .Throws<OperationCanceledException>();

        BackgroundJobPerformer performer = CreatePerformer();

        // Act & Assert
        Assert.Throws<OperationCanceledException>(() => performer.Perform(_context.Object));
    }

    [Fact]
    public void Run_ThrowsJobPerformanceException_InsteadOfOperationCanceled_OccurredInPostFilterMethods_WhenShutdownTokenIsNOTCanceled()
    {
        // Arrange
        Mock<IServerFilter> filter = CreateFilter<IServerFilter>();
        filter.Setup(x => x.OnPerformed(It.IsAny<PerformedContext>()))
            .Throws<OperationCanceledException>();

        BackgroundJobPerformer performer = CreatePerformer();

        // Act
        JobPerformanceException exception = Assert.Throws<JobPerformanceException>(
            () => performer.Perform(_context.Object));

        // Assert
        Assert.IsType<OperationCanceledException>(exception.InnerException);
    }

    [Theory]
    [MemberData(nameof(GetSchedulers))]
    public void Run_FlowsAsyncLocal_ThroughFilters_AndSynchronousBackgroundJobMethod(TaskScheduler scheduler)
    {
        // Arrange
        Guid id = Guid.NewGuid();
        _filters.Add(new AsyncLocalFilter(id));
        _context.BackgroundJob.Job = Job.FromExpression(() => AsyncLocalSync());

        BackgroundJobPerformer performer = CreatePerformer(CreateInnerPerformer(scheduler));

        // Act
        object? result = performer.Perform(_context.Object);

        // Assert
        Assert.Equal(id, result);
    }

    [Theory]
    [MemberData(nameof(GetSchedulers))]
    public void Run_FlowsAsyncLocal_ThroughFilters_AndSimpleAsynchronousBackgroundJobMethod(TaskScheduler scheduler)
    {
        // Arrange
        Guid id = Guid.NewGuid();
        _filters.Add(new AsyncLocalFilter(id));
        _context.BackgroundJob.Job = Job.FromExpression(() => AsyncLocalSimpleAsync());

        BackgroundJobPerformer performer = CreatePerformer(CreateInnerPerformer(scheduler));

        // Act
        object? result = performer.Perform(_context.Object);

        // Assert
        Assert.Equal(id, result);
    }

    [Theory]
    [MemberData(nameof(GetSchedulers))]
    public void Run_FlowsAsyncLocal_ThroughFilters_AndAsyncAwaitAsynchronousBackgroundJobMethod(TaskScheduler scheduler)
    {
        // Arrange
        Guid id = Guid.NewGuid();
        _filters.Add(new AsyncLocalFilter(id));
        _context.BackgroundJob.Job = Job.FromExpression(() => AsyncLocalAsyncAwait());

        BackgroundJobPerformer performer = CreatePerformer(CreateInnerPerformer(scheduler));

        // Act
        object? result = performer.Perform(_context.Object);

        // Assert
        Assert.Equal(id, result);
    }

    [Theory]
    [MemberData(nameof(GetSchedulers))]
    public void Run_FlowsAsyncLocal_ThroughFilters_AndAsyncAwaitContinuationAsynchronousBackgroundJobMethod(TaskScheduler scheduler)
    {
        // Arrange
        Guid id = Guid.NewGuid();
        _filters.Add(new AsyncLocalFilter(id));
        _context.BackgroundJob.Job = Job.FromExpression(() => AsyncLocalAsyncAwaitContinuation());

        BackgroundJobPerformer performer = CreatePerformer(CreateInnerPerformer(scheduler));

        // Act
        object? result = performer.Perform(_context.Object);

        // Assert
        Assert.Equal(id, result);
    }

    private static CoreBackgroundJobPerformer CreateInnerPerformer(TaskScheduler taskScheduler)
    {
        return new CoreBackgroundJobPerformer(new JobActivator(), taskScheduler);
    }

    public static IEnumerable<object[]> GetSchedulers()
    {
        yield return new object[] { null };
        yield return new object[] { TaskScheduler.Default };
        yield return new object[] { new Masterly.NightShift.Processing.BackgroundTaskScheduler(threadCount: 1) };
    }

    private BackgroundJobPerformer CreatePerformer(IBackgroundJobPerformer inner = null)
    {
        return new BackgroundJobPerformer(_filterProvider.Object, inner ?? _innerPerformer.Object);
    }

    private Mock<T> CreateFilter<T>()
        where T : class
    {
        Mock<T> filter = new();
        _filters.Add(filter.Object);

        return filter;
    }

    private static readonly AsyncLocal<Guid> Identifier = new();

    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    [SuppressMessage("ReSharper", "UnusedMethodReturnValue.Global")]
    public static Guid AsyncLocalSync()
    {
        return Identifier.Value;
    }

    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static Task<Guid> AsyncLocalSimpleAsync()
    {
        return Task.FromResult(Identifier.Value);
    }

    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
    public static async Task<Guid> AsyncLocalAsyncAwait()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
    {
        return Identifier.Value;
    }

    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static async Task<Guid> AsyncLocalAsyncAwaitContinuation()
    {
        await Task.Yield();
        return Identifier.Value;
    }

    private sealed class AsyncLocalFilter(Guid identifier) : IServerFilter
    {
        private readonly Guid _identifier = identifier;

        public void OnPerforming(PerformingContext context)
        {
            Identifier.Value = _identifier;
        }

        public void OnPerformed(PerformedContext context)
        {
            Assert.Equal(_identifier, Identifier.Value);
        }
    }
}
