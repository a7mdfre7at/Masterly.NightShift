namespace Masterly.NightShift.Core.Tests;

public class BackgroundJobClientScheduleOptionsExtensionsFacts
{
    private readonly Mock<IBackgroundJobClientV2> _client = new();
    private readonly Mock<JobStorage> _storage = new();
    private Job _job;
    private IState _state;
    private IDictionary<string, object> _parameters;

    public BackgroundJobClientScheduleOptionsExtensionsFacts()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.JobQueueProperty)).Returns(true);
        _client.Setup(x => x.Storage).Returns(_storage.Object);
        _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()))
            .Callback((Job job, IState state, IDictionary<string, object> parameters) =>
            {
                _job = job;
                _state = state;
                _parameters = parameters;
            })
            .Returns("1");
    }

    [Fact]
    public void Enqueue_CreatesAnEnqueuedJob_WithThePolicyParameters()
    {
        string id = _client.Object.Enqueue(() => Run(), new ScheduleOptions { Policy = "Business-Hours", WhenBlocked = WhenBlocked.Skip });

        Assert.Equal("1", id);
        Assert.IsType<EnqueuedState>(_state);
        Assert.Equal("business-hours", _parameters[ScheduleOptions.PolicyParameter]);
        Assert.Equal("Skip", _parameters[ScheduleOptions.WhenBlockedParameter]);
        Assert.False(_parameters.ContainsKey(SkipIfLateAttribute.WindowParameter));
    }

    [Fact]
    public void Schedule_WithADelay_CreatesAScheduledJob_WithTheLateStartLimit()
    {
        DateTime before = DateTime.UtcNow;

        _client.Object.Schedule(() => Run(), TimeSpan.FromHours(1), new ScheduleOptions { SkipAfter = TimeSpan.FromMinutes(30) });

        ScheduledState state = Assert.IsType<ScheduledState>(_state);
        Assert.InRange(state.EnqueueAt, before.AddHours(1), DateTime.UtcNow.AddHours(1));
        Assert.Equal(1800L, _parameters[SkipIfLateAttribute.WindowParameter]);
        Assert.False(_parameters.ContainsKey(ScheduleOptions.PolicyParameter));
    }

    [Fact]
    public void Schedule_AtAMoment_CreatesAScheduledJob_InTheQueueOfTheOptions()
    {
        DateTimeOffset at = new(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(1));

        _client.Object.Schedule<Service>(x => x.Work(), at, new ScheduleOptions { Queue = "critical" });

        Assert.Equal(at.UtcDateTime, Assert.IsType<ScheduledState>(_state).EnqueueAt);
        Assert.Equal("critical", _job.Queue);
        Assert.Equal(typeof(Service), _job.Type);
    }

    [Fact]
    public void Overloads_ForAsyncMethods_CreateTheJob()
    {
        ScheduleOptions options = new() { Policy = "p" };

        _client.Object.Enqueue(() => RunAsync(), options);
        Assert.Equal(nameof(RunAsync), _job.Method.Name);

        _client.Object.Enqueue<Service>(x => x.WorkAsync(), options);
        Assert.Equal(nameof(Service.WorkAsync), _job.Method.Name);

        _client.Object.Schedule(() => RunAsync(), TimeSpan.FromMinutes(1), options);
        Assert.IsType<ScheduledState>(_state);

        _client.Object.Schedule<Service>(x => x.WorkAsync(), DateTimeOffset.UtcNow, options);
        Assert.Equal(typeof(Service), _job.Type);

        _client.Object.Schedule<Service>(x => x.Work(), TimeSpan.FromMinutes(1), options);
        _client.Object.Schedule(() => Run(), DateTimeOffset.UtcNow, options);
        _client.Object.Schedule(() => RunAsync(), DateTimeOffset.UtcNow, options);
        _client.Object.Schedule<Service>(x => x.WorkAsync(), TimeSpan.FromMinutes(1), options);
        _client.Object.Enqueue<Service>(x => x.Work(), options);

        _client.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>(), It.IsAny<IDictionary<string, object>>()), Times.Exactly(9));
    }

    [Fact]
    public void Methods_ThrowAnException_WhenTheClientCannotStoreParameters()
    {
        IBackgroundJobClient client = new Mock<IBackgroundJobClient>().Object;

        Assert.Throws<NotSupportedException>(() => client.Enqueue(() => Run(), new ScheduleOptions()));
    }

    [Fact]
    public void Methods_ThrowAnException_ForAQueue_WhenTheStorageCannotKeepIt()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.JobQueueProperty)).Returns(false);

        Assert.Throws<NotSupportedException>(() => _client.Object.Enqueue(() => Run(), new ScheduleOptions { Queue = "critical" }));
        Assert.Equal("1", _client.Object.Enqueue(() => Run(), new ScheduleOptions()));
    }

    [Fact]
    public void Methods_ThrowAnException_WhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>("options", () => _client.Object.Enqueue(() => Run(), null));
        Assert.Throws<ArgumentNullException>("options", () => _client.Object.Schedule(() => Run(), TimeSpan.Zero, null));
        Assert.Throws<ArgumentNullException>("client", () => BackgroundJobClientScheduleOptionsExtensions.Enqueue(null, () => Run(), new ScheduleOptions()));
    }

    [Fact]
    public void ScheduleOptions_HasTheDefaults()
    {
        ScheduleOptions options = new();

        Assert.Null(options.Policy);
        Assert.Equal(WhenBlocked.MoveForward, options.WhenBlocked);
        Assert.Null(options.SkipAfter);
        Assert.Null(options.Queue);
        Assert.Empty(options.ToParameters());
    }

    [Fact]
    public void ScheduleOptions_ValidatesItsValues()
    {
        ScheduleOptions options = new();

        Assert.Throws<ArgumentException>("value", () => options.Policy = "not valid");
        Assert.Throws<ArgumentOutOfRangeException>("value", () => options.WhenBlocked = (WhenBlocked)9);
        Assert.Throws<ArgumentOutOfRangeException>("value", () => options.SkipAfter = TimeSpan.FromMilliseconds(500));
        Assert.ThrowsAny<ArgumentException>(() => options.Queue = "Not A Queue");

        options.Policy = null;
        options.SkipAfter = null;
        options.Queue = null;
        Assert.Empty(options.ToParameters());
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Run()
    {
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static Task RunAsync() => Task.CompletedTask;

    public sealed class Service
    {
        public void Work()
        {
        }

        public Task WorkAsync() => Task.CompletedTask;
    }
}
