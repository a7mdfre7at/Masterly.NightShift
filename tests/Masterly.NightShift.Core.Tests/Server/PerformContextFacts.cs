using Masterly.NightShift.Server;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests.Server;

public class PerformContextFacts
{
    private readonly Mock<JobStorage> _storage;
    private readonly Mock<IStorageConnection> _connection;
    private readonly Mock<IJobCancellationToken> _cancellationToken;
    private readonly BackgroundJobMock _backgroundJob;

    public PerformContextFacts()
    {
        _storage = new Mock<JobStorage>();
        _connection = new Mock<IStorageConnection>();
        _backgroundJob = new BackgroundJobMock();
        _cancellationToken = new Mock<IJobCancellationToken>();
    }

    [Fact]
    public void Ctor_DoesNotThrowAnException_WhenStorageIsNull()
    {
        PerformContext context = new(null, _connection.Object, _backgroundJob.Object, _cancellationToken.Object);
        Assert.NotNull(context);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenConnectionIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new PerformContext(_storage.Object, null, _backgroundJob.Object, _cancellationToken.Object));

        Assert.Equal("connection", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenBackgroundJobIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new PerformContext(_storage.Object, _connection.Object, null, _cancellationToken.Object));

        Assert.Equal("backgroundJob", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenCancellationTokenIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new PerformContext(_storage.Object, _connection.Object, _backgroundJob.Object, null));

        Assert.Equal("cancellationToken", exception.ParamName);
    }

    [Fact]
    public void Ctor_CorrectlySets_AllInstanceProperties()
    {
        PerformContext context = CreateContext();

        Assert.Same(_storage.Object, context.Storage);
        Assert.Equal(_backgroundJob.Object, context.BackgroundJob);
        Assert.NotNull(context.Items);
        Assert.Same(_connection.Object, context.Connection);
        Assert.Same(_cancellationToken.Object, context.CancellationToken);
    }

    [Fact]
    public void CopyCtor_ThrowsAnException_WhenContextIsNull()
    {
        Assert.Throws<NullReferenceException>(
            () => new PerformContext(null));
    }

    [Fact]
    public void CopyCtor_CopiesAllPropertyValues()
    {
        PerformContext context = CreateContext();
        PerformContext contextCopy = new(context);

        Assert.Same(context.Items, contextCopy.Items);
        Assert.Same(context.Storage, contextCopy.Storage);
        Assert.Same(context.Connection, contextCopy.Connection);
        Assert.Same(context.BackgroundJob, contextCopy.BackgroundJob);
        Assert.Same(context.CancellationToken, contextCopy.CancellationToken);
    }

    [Fact]
    public void SetJobParameter_ThrowsAnException_WhenParameterNameIsNullOrEmpty()
    {
        PerformContext context = CreateContext();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => context.SetJobParameter(null, null));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void SetJobParameter_ConvertsValueToJson_AndSetsItUsingConnection()
    {
        PerformContext context = CreateContext();

        context.SetJobParameter("name", "value");

        _connection.Verify(x => x.SetJobParameter(_backgroundJob.Id, "name", "\"value\""));
    }

    [Fact]
    public void GetJobParameter_ThrowsAnException_WhenNameIsNullOrEmpty()
    {
        PerformContext context = CreateContext();

        Assert.Throws<ArgumentNullException>(
            () => context.GetJobParameter<string>(null));
    }

    [Fact]
    public void GetJobParameter_ThrowsAnException_WhenParameterCouldNotBeDeserialized()
    {
        _connection.Setup(x => x.GetJobParameter(_backgroundJob.Id, "name")).Returns("value");
        PerformContext context = CreateContext();

        Assert.Throws<InvalidOperationException>(
            () => context.GetJobParameter<int>("name"));
    }

    [Fact]
    public void RetryCount_IsZero_WhenTheJobHasNotBeenRetried()
    {
        PerformContext context = CreateContext();

        Assert.Equal(0, context.RetryCount);
    }

    [Fact]
    public void RetryCount_ReadsTheParameterTheRetryFilterWrites()
    {
        _connection.Setup(x => x.GetJobParameter(_backgroundJob.Id, "RetryCount")).Returns("2");
        PerformContext context = CreateContext();

        Assert.Equal(2, context.RetryCount);
    }

    [Fact]
    public void ScheduledFor_ReadsTheParameterOfADelayedJob()
    {
        DateTime scheduledFor = new(2026, 11, 20, 9, 30, 0, 250, DateTimeKind.Utc);
        _backgroundJob.ParametersSnapshot = new Dictionary<string, string>
        {
            [PerformContext.ScheduledForParameter] = JobHelper.ToMillisecondTimestamp(scheduledFor).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        Assert.Equal(scheduledFor, CreateContext().ScheduledFor);
    }

    [Fact]
    public void ScheduledFor_ReadsTheOccurrenceOfARecurringJob()
    {
        DateTime occurrence = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        _backgroundJob.ParametersSnapshot = new Dictionary<string, string>
        {
            ["Time"] = JobHelper.ToTimestamp(occurrence).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["RecurringJobId"] = "\"nightly\"",
        };

        PerformContext context = CreateContext();

        Assert.Equal(occurrence, context.ScheduledFor);
        Assert.Equal("nightly", context.RecurringJobId);
    }

    [Fact]
    public void ScheduledFor_AndRecurringJobId_AreNull_ForAFireAndForgetJob()
    {
        _backgroundJob.ParametersSnapshot = new Dictionary<string, string>();

        PerformContext context = CreateContext();

        Assert.Null(context.ScheduledFor);
        Assert.Null(context.RecurringJobId);
        _connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void Lateness_IsMeasuredFromScheduledFor()
    {
        DateTime scheduledFor = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        _backgroundJob.CreatedAt = scheduledFor.AddDays(-1);
        _backgroundJob.ParametersSnapshot = new Dictionary<string, string>
        {
            [PerformContext.ScheduledForParameter] = JobHelper.ToMillisecondTimestamp(scheduledFor).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        PerformContext context = WithClock(scheduledFor.AddMinutes(7), CreateContext);

        Assert.Equal(scheduledFor.AddMinutes(7), context.StartedAt);
        Assert.Equal(TimeSpan.FromMinutes(7), context.Lateness);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void Lateness_IsMeasuredFromCreation_WithoutScheduledFor()
    {
        DateTime createdAt = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        _backgroundJob.CreatedAt = createdAt;
        _backgroundJob.ParametersSnapshot = new Dictionary<string, string>();

        PerformContext context = WithClock(createdAt.AddSeconds(90), CreateContext);

        Assert.Equal(TimeSpan.FromSeconds(90), context.Lateness);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void Lateness_IsNeverNegative()
    {
        DateTime createdAt = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        _backgroundJob.CreatedAt = createdAt;
        _backgroundJob.ParametersSnapshot = new Dictionary<string, string>();

        PerformContext context = WithClock(createdAt.AddSeconds(-5), CreateContext);

        Assert.Equal(TimeSpan.Zero, context.Lateness);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void CopyCtor_KeepsTheStartTime()
    {
        DateTime startedAt = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        PerformContext context = WithClock(startedAt, CreateContext);

        PerformContext copy = WithClock(startedAt.AddHours(1), () => new PerformContext(context));

        Assert.Equal(startedAt, copy.StartedAt);
    }

    private static T WithClock<T>(DateTime now, Func<T> action)
    {
        TimeProvider previous = JobClock.TimeProvider;
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(now));

        try
        {
            return action();
        }
        finally
        {
            JobClock.TimeProvider = previous;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private PerformContext CreateContext()
    {
        return new PerformContext(
            _storage.Object, _connection.Object, _backgroundJob.Object, _cancellationToken.Object);
    }
}
