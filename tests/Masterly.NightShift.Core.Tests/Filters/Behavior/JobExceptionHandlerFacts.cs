using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public class JobExceptionHandlerFacts
{
    private readonly PerformContextMock _context = new();
    private readonly RecordingHandler _handler = new();

    public JobExceptionHandlerFacts()
    {
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenHandlerIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => new JobExceptionHandlerFilter(null));

        Assert.Equal("handler", exception.ParamName);
    }

    [Fact]
    public void OnPerformed_CallsOnJobException_WithTheExceptionTheJobThrew()
    {
        InvalidOperationException thrown = new("boom");
        _context.Connection.Setup(x => x.GetJobParameter("JobId", "RetryCount")).Returns("2");

        Filter().OnPerformed(_context.GetPerformedContext(exception: Wrap(thrown)));

        JobExceptionContext context = Assert.Single(_handler.Exceptions);
        Assert.Same(thrown, context.Exception);
        Assert.Same(_context.Object.Items, context.PerformContext.Items);
        Assert.Same(_context.Object.BackgroundJob, context.BackgroundJob);
        Assert.Equal(2, context.RetryCount);
        Assert.Empty(_handler.Cancellations);
    }

    [Fact]
    public void OnPerformed_CallsOnJobException_WhenAFilterGaveUpBeforeTheMethodRan()
    {
        Filter().OnPerformed(_context.GetPerformedContext(exception: Wrap(new ConcurrencyLimitException("resource", "No slot"))));

        Assert.IsType<ConcurrencyLimitException>(Assert.Single(_handler.Exceptions).Exception);
    }

    [Fact]
    public void OnPerformed_CallsOnJobException_ForAJobTerminatedAsFailed()
    {
        Filter().OnPerformed(_context.GetPerformedContext(exception: Wrap(new TerminateJobException("bad input", TerminateJobOutcome.Failed))));

        Assert.IsType<TerminateJobException>(Assert.Single(_handler.Exceptions).Exception);
    }

    [Fact]
    public void OnPerformed_CallsOnJobCanceled_WhenAStateChangeAbortedTheJob()
    {
        Filter().OnPerformed(_context.GetPerformedContext(exception: new JobAbortedException()));

        Assert.IsType<JobAbortedException>(Assert.Single(_handler.Cancellations).Exception);
        Assert.Empty(_handler.Exceptions);
    }

    [Fact]
    public void OnPerformed_CallsOnJobCanceled_WhenTheServerShutDown()
    {
        _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(canceled: true));

        Filter().OnPerformed(_context.GetPerformedContext(exception: new OperationCanceledException()));

        Assert.Single(_handler.Cancellations);
    }

    [Fact]
    public void OnPerformed_CallsOnJobCanceled_ForAJobTerminatedAsCanceled()
    {
        Filter().OnPerformed(_context.GetPerformedContext(exception: Wrap(new TerminateJobException("timed out", TerminateJobOutcome.Canceled))));

        Assert.IsType<TerminateJobException>(Assert.Single(_handler.Cancellations).Exception);
    }

    [Fact]
    public void OnPerformed_CallsNothing_WhenTheJobSucceeded()
    {
        Filter().OnPerformed(_context.GetPerformedContext(result: "done"));

        Assert.Empty(_handler.Exceptions);
        Assert.Empty(_handler.Cancellations);
    }

    [Fact]
    public void OnPerformed_CallsNothing_WhenAFilterHandledTheException()
    {
        PerformedContext performed = _context.GetPerformedContext(exception: Wrap(new InvalidOperationException()));
        performed.ExceptionHandled = true;

        Filter().OnPerformed(performed);

        Assert.Empty(_handler.Exceptions);
    }

    [Fact]
    public void OnPerformed_CallsNothing_WhenAFilterCanceledTheRun_WithoutAnException()
    {
        Filter().OnPerformed(_context.GetPerformedContext(canceled: true));

        Assert.Empty(_handler.Cancellations);
    }

    [Theory]
    [MemberData(nameof(NeitherFailedNorCanceled))]
    public void OnPerformed_CallsNothing_ForRetryLaterOrASkippedJob(Exception exception)
    {
        Filter().OnPerformed(_context.GetPerformedContext(exception: Wrap(exception)));

        Assert.Empty(_handler.Exceptions);
        Assert.Empty(_handler.Cancellations);
    }

    public static IEnumerable<object[]> NeitherFailedNorCanceled =>
    [
        [new RetryLaterException(TimeSpan.FromMinutes(1))],
        [new TerminateJobException("nothing to do")],
    ];

    [Fact]
    public void OnPerformed_IgnoresAHandlerThatThrows()
    {
        JobExceptionHandlerFilter filter = new(new ThrowingHandler());

        filter.OnPerformed(_context.GetPerformedContext(exception: Wrap(new InvalidOperationException())));
        filter.OnPerformed(_context.GetPerformedContext(exception: new JobAbortedException()));
    }

    [Fact]
    public void OnPerforming_DoesNothing()
    {
        Filter().OnPerforming(_context.GetPerformingContext());

        Assert.Empty(_handler.Exceptions);
    }

    [Fact]
    public void OnPerformed_ThrowsAnException_WhenContextIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => Filter().OnPerformed(null));
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void UseJobExceptionHandler_AddsAGlobalFilter()
    {
        int before = GlobalJobFilters.Filters.Count;

        GlobalConfiguration.Configuration.UseJobExceptionHandler(_handler);

        try
        {
            JobFilter added = Assert.Single(GlobalJobFilters.Filters, x => x.Instance is JobExceptionHandlerFilter { Handler: var handler } && handler == _handler);
            Assert.Equal(before + 1, GlobalJobFilters.Filters.Count);
            Assert.NotNull(added);
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(GlobalJobFilters.Filters.Single(x => x.Instance is JobExceptionHandlerFilter { Handler: var handler } && handler == _handler).Instance);
        }
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void UseJobExceptionHandler_WithDelegates_CallsThem()
    {
        List<Exception> failures = [];
        List<Exception> cancellations = [];

        GlobalConfiguration.Configuration.UseJobExceptionHandler(x => failures.Add(x.Exception), x => cancellations.Add(x.Exception));

        JobExceptionHandlerFilter filter = (JobExceptionHandlerFilter)GlobalJobFilters.Filters.Last(x => x.Instance is JobExceptionHandlerFilter).Instance;

        try
        {
            filter.OnPerformed(_context.GetPerformedContext(exception: Wrap(new InvalidOperationException())));
            filter.OnPerformed(_context.GetPerformedContext(exception: new JobAbortedException()));

            Assert.IsType<InvalidOperationException>(Assert.Single(failures));
            Assert.IsType<JobAbortedException>(Assert.Single(cancellations));
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(filter);
        }
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void UseJobExceptionHandler_AddsTheSameHandlerOnce()
    {
        RecordingHandler other = new();

        try
        {
            GlobalConfiguration.Configuration.UseJobExceptionHandler(_handler);
            GlobalConfiguration.Configuration.UseJobExceptionHandler(_handler);
            GlobalConfiguration.Configuration.UseJobExceptionHandler(other);

            Assert.Single(GlobalJobFilters.Filters, x => x.Instance is JobExceptionHandlerFilter { Handler: var handler } && handler == _handler);
            Assert.Single(GlobalJobFilters.Filters, x => x.Instance is JobExceptionHandlerFilter { Handler: var handler } && handler == other);
        }
        finally
        {
            RemoveHandlerFilters();
        }
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void UseJobExceptionHandler_WithTheSameDelegates_AddsThemOnce()
    {
        try
        {
            GlobalConfiguration.Configuration.UseJobExceptionHandler(IgnoreException, IgnoreException);
            GlobalConfiguration.Configuration.UseJobExceptionHandler(IgnoreException, IgnoreException);
            GlobalConfiguration.Configuration.UseJobExceptionHandler(IgnoreException);

            Assert.Equal(2, GlobalJobFilters.Filters.Count(static x => x.Instance is JobExceptionHandlerFilter));
        }
        finally
        {
            RemoveHandlerFilters();
        }
    }

    [Fact]
    public void UseJobExceptionHandler_WithDelegates_AllowsNoCancellationHandler()
    {
        List<Exception> failures = [];
        DelegateJobExceptionHandler handler = new(x => failures.Add(x.Exception), null);
        JobExceptionHandlerFilter filter = new(handler);

        filter.OnPerformed(_context.GetPerformedContext(exception: new JobAbortedException()));

        Assert.Empty(failures);
    }

    [Fact]
    public void UseJobExceptionHandler_ThrowsAnException_WhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>(() => GlobalConfiguration.Configuration.UseJobExceptionHandler((IJobExceptionHandler)null));
        Assert.Throws<ArgumentNullException>(() => GlobalConfiguration.Configuration.UseJobExceptionHandler((Action<JobExceptionContext>)null));
    }

    private JobExceptionHandlerFilter Filter() => new(_handler);

    private static void IgnoreException(JobExceptionContext context)
    {
    }

    private static void RemoveHandlerFilters()
    {
        foreach (JobFilter filter in GlobalJobFilters.Filters.Where(static x => x.Instance is JobExceptionHandlerFilter).ToList())
        {
            GlobalJobFilters.Filters.Remove(filter.Instance);
        }
    }

    private static JobPerformanceException Wrap(Exception exception) => new("An exception occurred during performance of the job.", exception);

    private sealed class RecordingHandler : IJobExceptionHandler
    {
        public List<JobExceptionContext> Exceptions { get; } = [];

        public List<JobExceptionContext> Cancellations { get; } = [];

        public void OnJobException(JobExceptionContext context) => Exceptions.Add(context);

        public void OnJobCanceled(JobExceptionContext context) => Cancellations.Add(context);
    }

    private sealed class ThrowingHandler : IJobExceptionHandler
    {
        public void OnJobException(JobExceptionContext context) => throw new InvalidOperationException("handler bug");

        public void OnJobCanceled(JobExceptionContext context) => throw new InvalidOperationException("handler bug");
    }
}
