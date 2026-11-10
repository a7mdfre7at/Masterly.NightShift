namespace Masterly.NightShift.Core.Tests;

public class DeadlineAttributeFacts : IDisposable
{
    private readonly DateTime _now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    public DeadlineAttributeFacts()
    {
        DeadlineAttribute.Clock = () => _now;
    }

    public void Dispose()
    {
        DeadlineAttribute.Clock = () => DateTime.UtcNow;
    }

    [Fact]
    public void Ctor_ParsesTimeSpans_AndSeconds()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), new DeadlineAttribute("00:10:00").Timeout);
        Assert.Equal(TimeSpan.FromSeconds(90), new DeadlineAttribute(90).Timeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeadlineAttribute(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeadlineAttribute("-00:01:00"));
    }

    [Fact]
    public void OnCreating_SetsTheDeadlineParameter_FromNow()
    {
        CreateContextMock context = new();
        DeadlineAttribute attribute = new("00:10:00");

        attribute.OnCreating(context.GetCreatingContext());

        object value = context.Object.Parameters[DeadlineAttribute.DeadlineAtParameter];
        DateTime deadline = value is string serialized ? SerializationHelper.Deserialize<DateTime>(serialized, SerializationOption.User) : (DateTime)value;
        Assert.Equal(_now.AddMinutes(10), deadline);
    }

    [Fact]
    public void OnStateElection_DoesNotThrow_WhenTheMissCannotBeRecorded()
    {
        ElectStateContextMock context = new();
        context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);
        context.ApplyContext.Connection
            .Setup(x => x.GetJobParameter("JobId", DeadlineAttribute.DeadlineAtParameter))
            .Returns(SerializationHelper.Serialize(_now.AddMinutes(-5), SerializationOption.User));
        context.ApplyContext.Connection
            .Setup(x => x.SetJobParameter("JobId", DeadlineAttribute.DeadlineMissedParameter, It.IsAny<string>()))
            .Throws(new InvalidOperationException("storage is down"));

        new DeadlineAttribute(60).OnStateElection(context.Object);

        Assert.IsType<SucceededState>(context.Object.CandidateState);
    }

    [Fact]
    public void OnStateElection_RecordsTheMiss_WhenSucceedingAfterTheDeadline()
    {
        ElectStateContextMock context = new();
        context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);
        context.ApplyContext.Connection
            .Setup(x => x.GetJobParameter("JobId", DeadlineAttribute.DeadlineAtParameter))
            .Returns(SerializationHelper.Serialize(_now.AddMinutes(-5), SerializationOption.User));

        new DeadlineAttribute(60).OnStateElection(context.Object);

        context.ApplyContext.Connection.Verify(x => x.SetJobParameter("JobId", DeadlineAttribute.DeadlineMissedParameter,
            It.Is<string>(v => v.Contains("00:05:00"))));
    }

    [Fact, StaticLock]
    public void OnStateElection_RaisesTheDeadlineMissedAlert_AfterTheCommit()
    {
        RecordingSink sink = new();
        Alerts.AddSink(sink);
        try
        {
            ElectStateContextMock context = new();
            context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);
            context.ApplyContext.Connection
                .Setup(x => x.GetJobParameter("JobId", DeadlineAttribute.DeadlineAtParameter))
                .Returns(SerializationHelper.Serialize(_now.AddMinutes(-5), SerializationOption.User));

            // Last, because reading Object builds the context from the setup above
            PostCommitActions afterCommit = new();
            context.ApplyContext.Object.AfterCommit = afterCommit;

            new DeadlineAttribute(60).OnStateElection(context.Object);
            Assert.Empty(sink.Received);

            afterCommit.Run();
            Alert alert = Assert.Single(sink.Received);
            Assert.Equal(AlertKind.DeadlineMissed, alert.Kind);
            Assert.Equal("JobId", alert.JobId);
        }
        finally
        {
            Alerts.Clear();
        }
    }

    [Fact]
    public void OnStateElection_DoesNothing_WhenOnTime()
    {
        ElectStateContextMock context = new();
        context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);
        context.ApplyContext.Connection
            .Setup(x => x.GetJobParameter("JobId", DeadlineAttribute.DeadlineAtParameter))
            .Returns(SerializationHelper.Serialize(_now.AddMinutes(5), SerializationOption.User));

        new DeadlineAttribute(60).OnStateElection(context.Object);

        context.ApplyContext.Connection.Verify(x => x.SetJobParameter("JobId", DeadlineAttribute.DeadlineMissedParameter, It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_IgnoresOtherStates()
    {
        ElectStateContextMock context = new();
        context.ApplyContext.NewStateObject = new FailedState(new Exception());

        new DeadlineAttribute(60).OnStateElection(context.Object);

        context.ApplyContext.Connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ReadDeadline_ReturnsNull_WithoutParameter()
    {
        PerformContextMock context = new();

        Assert.Null(DeadlineAttribute.ReadDeadline(context.Connection.Object, "JobId"));
    }

    [Fact]
    public void ReadDeadline_ThrowsAnException_WhenArgumentsAreNull()
    {
        PerformContextMock context = new();

        Assert.Equal("connection", Assert.Throws<ArgumentNullException>(() => DeadlineAttribute.ReadDeadline(null, "JobId")).ParamName);
        Assert.Equal("jobId", Assert.Throws<ArgumentNullException>(() => DeadlineAttribute.ReadDeadline(context.Connection.Object, null)).ParamName);
    }

    [Fact]
    public void ReadDeadline_ReturnsNull_WhenTheParameterIsMalformed()
    {
        PerformContextMock context = new();
        context.Connection.Setup(x => x.GetJobParameter("JobId", DeadlineAttribute.DeadlineAtParameter)).Returns("{not a date");

        Assert.Null(DeadlineAttribute.ReadDeadline(context.Connection.Object, "JobId"));
    }

    [Fact]
    public void ReadDeadline_ReturnsNull_WhenTheStorageThrows()
    {
        PerformContextMock context = new();
        context.Connection.Setup(x => x.GetJobParameter("JobId", DeadlineAttribute.DeadlineAtParameter)).Throws(new InvalidOperationException("storage is down"));

        Assert.Null(DeadlineAttribute.ReadDeadline(context.Connection.Object, "JobId"));
    }

    [Fact]
    public void OnStateElection_DoesNothing_WhenTheDeadlineCannotBeRead()
    {
        ElectStateContextMock context = new();
        context.ApplyContext.NewStateObject = new SucceededState(null, 0, 0);
        context.ApplyContext.Connection
            .Setup(x => x.GetJobParameter("JobId", DeadlineAttribute.DeadlineAtParameter))
            .Throws(new InvalidOperationException("storage is down"));

        new DeadlineAttribute(60).OnStateElection(context.Object);

        context.ApplyContext.Connection.Verify(x => x.SetJobParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OnCreated_LeavesTheJobParametersAlone()
    {
        CreateContextMock context = new();

        new DeadlineAttribute(60).OnCreated(context.GetCreatedContext("JobId"));

        Assert.False(context.Object.Parameters.ContainsKey(DeadlineAttribute.DeadlineAtParameter));
    }

    private sealed class RecordingSink : IAlertSink
    {
        public List<Alert> Received { get; } = [];

        public void Publish(Alert alert) => Received.Add(alert);
    }
}
