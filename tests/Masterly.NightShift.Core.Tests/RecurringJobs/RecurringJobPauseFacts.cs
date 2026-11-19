using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;

namespace Masterly.NightShift.Core.Tests;

/// <summary>
/// Pause, resume and schedule changes: the entity rules and the manager operations built on them.
/// </summary>
public class RecurringJobPauseFacts
{
    private const string RecurringJobId = "recurring-job-id";

    private readonly DateTime _now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IStorageConnection> _connection = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly Mock<IBackgroundJobFactory> _factory = new();
    private readonly Dictionary<string, string> _hash;
    private readonly List<Dictionary<string, string>> _writes = [];
    private readonly List<double> _scores = [];

    public RecurringJobPauseFacts()
    {
        _hash = new Dictionary<string, string>
        {
            { "Cron", "0 9 * * *" },
            { "TimeZoneId", "UTC" },
            { "Job", InvocationData.SerializeJob(Job.FromExpression(() => Sample())).SerializePayload() },
            { "NextExecution", JobHelper.SerializeDateTime(_now.AddHours(21)) },
            { "LastExecution", JobHelper.SerializeDateTime(_now.AddHours(-3)) },
            { "CreatedAt", JobHelper.SerializeDateTime(_now.AddDays(-10)) },
            { "V", "2" },
        };

        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{RecurringJobId}")).Returns(() => _hash);
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _transaction
            .Setup(x => x.SetRangeInHash($"recurring-job:{RecurringJobId}", It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) => _writes.Add(pairs.ToDictionary(p => p.Key, p => p.Value)));
        _transaction
            .Setup(x => x.AddToSet("recurring-jobs", RecurringJobId, It.IsAny<double>()))
            .Callback<string, string, double>((_, _, score) => _scores.Add(score));
    }

    [Fact]
    public void Entity_ReadsThePausedFlag()
    {
        _hash["Paused"] = "1";

        RecurringJobEntity entity = new(RecurringJobId, _hash);

        Assert.True(entity.Paused);
    }

    [Fact]
    public void Entity_IsNotPaused_ByDefault()
    {
        RecurringJobEntity entity = new(RecurringJobId, _hash);

        Assert.False(entity.Paused);
    }

    [Fact]
    public void Entity_ScheduleNext_YieldsNothing_WhilePaused()
    {
        RecurringJobEntity entity = new(RecurringJobId, _hash);
        entity.Pause();

        IEnumerable<DateTime> executions = entity.ScheduleNext(new DefaultTimeZoneResolver(), _now.AddDays(-2), _now, TimeSpan.Zero);

        Assert.Empty(executions);
        Assert.Null(entity.NextExecution);
    }

    [Fact]
    public void Entity_Pause_IsPersisted_AndRemovesTheJobFromTheSchedule()
    {
        RecurringJobEntity entity = new(RecurringJobId, _hash);
        entity.Pause();

        Assert.True(entity.IsChanged(_now, out IReadOnlyDictionary<string, string>? changed));
        Assert.Equal("1", changed["Paused"]);
        Assert.Equal(string.Empty, changed["NextExecution"]);
    }

    [Fact]
    public void Entity_Resume_SchedulesFromNow_WithoutCatchingUp()
    {
        _hash["Paused"] = "1";
        _hash["NextExecution"] = string.Empty;
        _hash["Misfire"] = ((int)MisfireHandlingMode.Strict).ToString();
        RecurringJobEntity entity = new(RecurringJobId, _hash);

        entity.Resume(new DefaultTimeZoneResolver(), _now);

        Assert.False(entity.Paused);
        Assert.Equal(_now.AddHours(21), entity.NextExecution);
        Assert.Equal(_now, entity.ResumedAt);
        Assert.Equal(_now.AddHours(-3), entity.LastExecution);
        Assert.True(entity.IsChanged(_now, out IReadOnlyDictionary<string, string>? changed));
        Assert.Equal("0", changed["Paused"]);
        Assert.Equal(JobHelper.SerializeDateTime(_now), changed["ResumedAt"]);

        // The next scheduler pass computes from the resume time, so the 09:00 runs missed while paused aren't replayed
        Assert.Equal(_now, entity.ScheduleFrom);
        IEnumerable<DateTime> executions = entity.ScheduleNext(new DefaultTimeZoneResolver(), entity.ScheduleFrom.Value, _now.AddMinutes(1), TimeSpan.Zero);
        Assert.Empty(executions);
    }

    [Fact]
    public void Entity_ScheduleFrom_PrefersTheLaterOfLastExecutionAndResumedAt()
    {
        _hash["ResumedAt"] = JobHelper.SerializeDateTime(_now.AddHours(-1));
        RecurringJobEntity entity = new(RecurringJobId, _hash);
        Assert.Equal(_now.AddHours(-1), entity.ScheduleFrom);

        entity.LastExecution = _now;
        Assert.Equal(_now, entity.ScheduleFrom);
    }

    [Fact]
    public void Entity_ScheduleFrom_StartsASecondBeforeTheScheduleChange_WhenThatIsTheLatest()
    {
        _hash["ResumedAt"] = JobHelper.SerializeDateTime(_now.AddHours(-2));
        _hash["ScheduleChangedAt"] = JobHelper.SerializeDateTime(_now.AddHours(-1));
        RecurringJobEntity entity = new(RecurringJobId, _hash);
        Assert.Equal(_now.AddHours(-1).AddSeconds(-1), entity.ScheduleFrom);

        entity.LastExecution = _now;
        Assert.Equal(_now, entity.ScheduleFrom);
    }

    [Fact]
    public void Strict_AfterACronChange_CountsMissedRunsFromTheChange_NotFromTheLastExecution()
    {
        // Hourly, last run at 11:00; changed to every 5 minutes at 11:28; the scheduler wakes up at 11:35
        DateTime changedAt = _now.AddMinutes(-32);
        _hash["Cron"] = "0 * * * *";
        _hash["LastExecution"] = JobHelper.SerializeDateTime(_now.AddHours(-1));
        _hash["NextExecution"] = JobHelper.SerializeDateTime(_now);
        _hash["Misfire"] = ((int)MisfireHandlingMode.Strict).ToString();
        RecurringJobManager manager = new(_storage.Object, _factory.Object, new DefaultTimeZoneResolver(), () => changedAt);

        manager.AddOrUpdate(RecurringJobId, Job.FromExpression(() => Sample()), "*/5 * * * *", new RecurringJobOptions { MisfireHandling = MisfireHandlingMode.Strict });
        MergeWritesIntoHash();
        RecurringJobEntity entity = new(RecurringJobId, _hash);
        IEnumerable<DateTime> executions = entity.ScheduleNext(new DefaultTimeZoneResolver(), entity.ScheduleFrom.Value, _now.AddMinutes(-25), TimeSpan.Zero);

        Assert.Equal([_now.AddMinutes(-30), _now.AddMinutes(-25)], executions);
    }

    [Fact]
    public void Manager_AddOrUpdate_RecordsTheScheduleChange_WhenTheCronChanges()
    {
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(RecurringJobId, Job.FromExpression(() => Sample()), "0 10 * * *", new RecurringJobOptions());

        Assert.Equal(JobHelper.SerializeDateTime(_now), _writes.Single()["ScheduleChangedAt"]);
    }

    [Fact]
    public void Manager_AddOrUpdate_DoesNotRecordAScheduleChange_WhenTheCronIsRewrittenWithTheSameMeaning()
    {
        _hash["Cron"] = "0 0 * * *";
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(RecurringJobId, Job.FromExpression(() => Sample()), "@daily", new RecurringJobOptions());

        Dictionary<string, string> written = _writes.Single();
        Assert.Equal("@daily", written["Cron"]);
        Assert.False(written.ContainsKey("ScheduleChangedAt"));
    }

    [Fact]
    public void Manager_AddOrUpdate_DoesNotRecordAScheduleChange_WhenTheSameTimeZoneHasAnotherId()
    {
        // A Windows and a Linux server registering TimeZoneInfo.Local
        _hash["TimeZoneId"] = "W. Europe Standard Time";
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(RecurringJobId, Job.FromExpression(() => Sample()), "0 9 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin") });

        Dictionary<string, string> written = _writes.Single();
        Assert.Equal("Europe/Berlin", written["TimeZoneId"]);
        Assert.False(written.ContainsKey("ScheduleChangedAt"));
    }

    [Fact]
    public void Manager_AddOrUpdate_DoesNotRecordAScheduleChange_WhenOnlyTheMisfireHandlingChanges()
    {
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(RecurringJobId, Job.FromExpression(() => Sample()), "0 9 * * *", new RecurringJobOptions { MisfireHandling = MisfireHandlingMode.Strict });

        Dictionary<string, string> written = _writes.Single();
        Assert.Equal(((int)MisfireHandlingMode.Strict).ToString(), written["Misfire"]);
        Assert.False(written.ContainsKey("ScheduleChangedAt"));
    }

    [Fact]
    public void Manager_UpdateSchedule_RecordsTheScheduleChange_WhenTheTimeZoneChanges()
    {
        TimeZoneInfo plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus-two", TimeSpan.FromHours(2), "plus two", "plus two");
        Mock<ITimeZoneResolver> resolver = new();
        resolver.Setup(x => x.GetTimeZoneById("plus-two")).Returns(plusTwo);
        RecurringJobManager manager = new(_storage.Object, _factory.Object, resolver.Object, () => _now);

        manager.UpdateSchedule(RecurringJobId, null, plusTwo);

        Assert.Equal(JobHelper.SerializeDateTime(_now), _writes.Single()["ScheduleChangedAt"]);
    }

    [Fact]
    public void Manager_UpdateSchedule_DoesNotRecordAScheduleChange_WhenOnlyTheQueueChanges()
    {
        RecurringJobManager manager = CreateManager();

        manager.UpdateSchedule(RecurringJobId, null, queue: "critical");

        Dictionary<string, string> written = _writes.Single();
        Assert.Equal("critical", written["Queue"]);
        Assert.False(written.ContainsKey("ScheduleChangedAt"));
    }

    [Fact]
    public void Manager_Pause_WritesThePausedFlag_AndScoresTheJobOutOfTheSchedule()
    {
        RecurringJobManager manager = CreateManager();

        bool result = manager.Pause(RecurringJobId);

        Assert.True(result);
        Assert.Equal("1", _writes.Single()["Paused"]);
        Assert.Equal(-1.0D, _scores.Single());
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void Manager_Pause_ReturnsFalse_ForUnknownJobs()
    {
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{RecurringJobId}")).Returns(default(Dictionary<string, string>));
        RecurringJobManager manager = CreateManager();

        Assert.False(manager.Pause(RecurringJobId));
        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact]
    public void Manager_Resume_PutsTheJobBackOnTheSchedule()
    {
        _hash["Paused"] = "1";
        _hash["NextExecution"] = string.Empty;
        RecurringJobManager manager = CreateManager();

        bool result = manager.Resume(RecurringJobId);

        Assert.True(result);
        Assert.Equal("0", _writes.Single()["Paused"]);
        Assert.Equal(JobHelper.ToTimestamp(_now.AddHours(21)), _scores.Single());
    }

    [Fact]
    public void Manager_Resume_DoesNothing_ForRunningJobs()
    {
        RecurringJobManager manager = CreateManager();

        Assert.True(manager.Resume(RecurringJobId));
        Assert.Empty(_writes);
    }

    [Fact]
    public void Manager_AddOrUpdate_KeepsAPausedJobPaused()
    {
        _hash["Paused"] = "1";
        _hash["NextExecution"] = string.Empty;
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(RecurringJobId, Job.FromExpression(() => Sample()), "0 10 * * *", new RecurringJobOptions());

        Dictionary<string, string> written = _writes.Single();
        Assert.Equal("0 10 * * *", written["Cron"]);
        Assert.False(written.ContainsKey("Paused"));
        Assert.True(!written.TryGetValue("NextExecution", out string? nextExecution) || nextExecution == string.Empty);
        Assert.Equal(-1.0D, _scores.Single());
    }

    [Fact]
    public void Manager_UpdateSchedule_ChangesCronAndReschedules()
    {
        RecurringJobManager manager = CreateManager();

        bool result = manager.UpdateSchedule(RecurringJobId, "0 18 * * *");

        Assert.True(result);
        Dictionary<string, string> written = _writes.Single();
        Assert.Equal("0 18 * * *", written["Cron"]);
        Assert.Equal(JobHelper.SerializeDateTime(_now.AddHours(6)), written["NextExecution"]);
        Assert.False(written.ContainsKey("Job"));
    }

    [Fact]
    public void Manager_UpdateSchedule_ChangesTimeZoneQueueAndMisfire()
    {
        TimeZoneInfo plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus-two", TimeSpan.FromHours(2), "plus two", "plus two");
        Mock<ITimeZoneResolver> resolver = new();
        resolver.Setup(x => x.GetTimeZoneById("plus-two")).Returns(plusTwo);
        resolver.Setup(x => x.GetTimeZoneById("UTC")).Returns(TimeZoneInfo.Utc);
        RecurringJobManager manager = new(_storage.Object, _factory.Object, resolver.Object, () => _now);

        manager.UpdateSchedule(RecurringJobId, null, plusTwo, "critical", MisfireHandlingMode.Strict);

        Dictionary<string, string> written = _writes.Single();
        Assert.Equal("plus-two", written["TimeZoneId"]);
        Assert.Equal("critical", written["Queue"]);
        Assert.Equal(((int)MisfireHandlingMode.Strict).ToString(), written["Misfire"]);
        Assert.Equal(JobHelper.SerializeDateTime(_now.AddHours(19)), written["NextExecution"]);
    }

    [Fact]
    public void Manager_UpdateSchedule_PutsTheQueueIntoTheJob_WhenTheStorageKeepsJobQueues()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.JobQueueProperty)).Returns(true);
        _hash["Job"] = InvocationData.SerializeJob(Job.FromExpression(() => SampleWithArgument("report", 42))).SerializePayload();
        RecurringJobManager manager = CreateManager();

        manager.UpdateSchedule(RecurringJobId, null, queue: "low");

        Dictionary<string, string> written = _writes.Single();
        Job job = InvocationData.DeserializePayload(written["Job"]).DeserializeJob();
        Assert.Equal("low", written["Queue"]);
        Assert.Equal("low", job.Queue);
        Assert.Equal(nameof(SampleWithArgument), job.Method.Name);
        Assert.Equal(new object[] { "report", 42 }, [.. job.Args]);
        Assert.False(written.ContainsKey("NextExecution"));
    }

    [Fact]
    public void Manager_UpdateSchedule_ReplacesTheQueueTheJobWasRegisteredWith()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.JobQueueProperty)).Returns(true);
        _hash["Job"] = InvocationData.SerializeJob(Job.FromExpression(() => Sample(), "critical")).SerializePayload();
        _hash["Queue"] = "default";
        RecurringJobManager manager = CreateManager();

        manager.UpdateSchedule(RecurringJobId, null, queue: "low");

        Assert.Equal("low", InvocationData.DeserializePayload(_writes.Single()["Job"]).Queue);
    }

    [Fact]
    public void Manager_UpdateSchedule_KeepsTheJob_WhenTheStorageDoesNotKeepJobQueues()
    {
        RecurringJobManager manager = CreateManager();

        manager.UpdateSchedule(RecurringJobId, null, queue: "low");

        Dictionary<string, string> written = _writes.Single();
        Assert.Equal("low", written["Queue"]);
        Assert.False(written.ContainsKey("Job"));
    }

    [Fact]
    public void Manager_UpdateSchedule_ClearsTheJobsQueue_WhenMovedBackToTheDefaultQueue()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.JobQueueProperty)).Returns(true);
        _hash["Job"] = InvocationData.SerializeJob(Job.FromExpression(() => Sample(), "low")).SerializePayload();
        _hash["Queue"] = "low";
        RecurringJobManager manager = CreateManager();

        manager.UpdateSchedule(RecurringJobId, null, queue: "default");

        Dictionary<string, string> written = _writes.Single();
        Assert.Equal("default", written["Queue"]);
        Assert.Null(InvocationData.DeserializePayload(written["Job"]).Queue);
    }

    [Fact]
    public void Manager_UpdateSchedule_KeepsTheQueueTheJobWasRegisteredWith_WhenTheDefaultQueueIsPassed()
    {
        // The dashboard's Edit dialog sends the recurring job's own queue, "default" here, with every change
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.JobQueueProperty)).Returns(true);
        _hash["Job"] = InvocationData.SerializeJob(Job.FromExpression(() => Sample(), "critical")).SerializePayload();
        _hash["Queue"] = "default";
        RecurringJobManager manager = CreateManager();

        manager.UpdateSchedule(RecurringJobId, "0 18 * * *", queue: "default");

        Dictionary<string, string> written = _writes.Single();
        Assert.Equal("0 18 * * *", written["Cron"]);
        Assert.False(written.ContainsKey("Job"));
        Assert.False(written.ContainsKey("Queue"));
    }

    [Fact]
    public void Manager_UpdateSchedule_ChangesTheQueue_WhenTheJobCanNotBeRead()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.JobQueueProperty)).Returns(true);
        _hash["Job"] = "{ not json";
        RecurringJobManager manager = CreateManager();

        bool result = manager.UpdateSchedule(RecurringJobId, null, queue: "low");

        Dictionary<string, string> written = _writes.Single();
        Assert.True(result);
        Assert.Equal("low", written["Queue"]);
        Assert.False(written.ContainsKey("Job"));
    }

    [Fact]
    public void Manager_UpdateSchedule_ThrowsAnException_ForInvalidCron()
    {
        RecurringJobManager manager = CreateManager();

        Assert.Throws<ArgumentException>(() => manager.UpdateSchedule(RecurringJobId, "not a cron"));
        Assert.Empty(_writes);
    }

    [Fact]
    public void Manager_UpdateSchedule_ThrowsAnException_ForInvalidQueue()
    {
        RecurringJobManager manager = CreateManager();

        Assert.Throws<ArgumentException>(() => manager.UpdateSchedule(RecurringJobId, null, queue: "Not Valid"));
    }

    [Fact]
    public void Manager_UpdateSchedule_ReturnsFalse_ForUnknownJobs()
    {
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{RecurringJobId}")).Returns(default(Dictionary<string, string>));
        RecurringJobManager manager = CreateManager();

        Assert.False(manager.UpdateSchedule(RecurringJobId, "0 1 * * *"));
    }

    [Fact]
    public void Manager_UpdateSchedule_KeepsAPausedJobOutOfTheSchedule()
    {
        _hash["Paused"] = "1";
        _hash["NextExecution"] = string.Empty;
        RecurringJobManager manager = CreateManager();

        manager.UpdateSchedule(RecurringJobId, "0 18 * * *");

        Assert.Equal(-1.0D, _scores.Single());
    }

    [Fact]
    public void Extensions_ThrowNotSupported_ForManagersWithoutV3()
    {
        IRecurringJobManager manager = new Mock<IRecurringJobManager>().Object;

        Assert.Throws<NotSupportedException>(() => manager.Pause(RecurringJobId));
        Assert.Throws<NotSupportedException>(() => manager.Resume(RecurringJobId));
        Assert.Throws<NotSupportedException>(() => manager.UpdateSchedule(RecurringJobId, "* * * * *"));
    }

    [Fact]
    public void Extensions_ForwardToV3Managers()
    {
        Mock<IRecurringJobManagerV3> v3 = new();
        v3.Setup(x => x.Pause(RecurringJobId)).Returns(true);
        IRecurringJobManager manager = v3.Object;

        Assert.True(manager.Pause(RecurringJobId));
        v3.Verify(x => x.Pause(RecurringJobId));
    }

    [Fact]
    public void Dto_ExposesThePausedFlag()
    {
        _hash["Paused"] = "1";
        _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns([RecurringJobId]);

        List<RecurringJobDto> dtos = _connection.Object.GetRecurringJobs();

        Assert.True(dtos.Single().Paused);
    }

    private RecurringJobManager CreateManager()
    {
        return new RecurringJobManager(_storage.Object, _factory.Object, new DefaultTimeZoneResolver(), () => _now);
    }

    private void MergeWritesIntoHash()
    {
        foreach (KeyValuePair<string, string> pair in _writes.SelectMany(static write => write))
        {
            _hash[pair.Key] = pair.Value;
        }
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void Sample()
    {
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void SampleWithArgument(string name, int count)
    {
    }
}
