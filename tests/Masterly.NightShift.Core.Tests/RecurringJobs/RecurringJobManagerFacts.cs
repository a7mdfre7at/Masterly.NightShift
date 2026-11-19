using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Client;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests;

public class RecurringJobManagerFacts
{
    private readonly Mock<JobStorage> _storage;
    private readonly string _id;
    private Job _job;
    private readonly string _cronExpression;
    private readonly Mock<IStorageConnection> _connection;
    private readonly Mock<IWriteOnlyTransaction> _transaction;
    private readonly Mock<IBackgroundJobFactory> _factory;
    private readonly Mock<IStateMachine> _stateMachine;
    private readonly DateTime _now = new(2017, 03, 30, 15, 30, 0, DateTimeKind.Utc);
    private readonly Func<DateTime> _nowFactory;
    private readonly BackgroundJob _backgroundJob;
    private readonly Mock<ITimeZoneResolver> _timeZoneResolver;

    public RecurringJobManagerFacts()
    {
        _id = "recurring-job-id";
        _job = Job.FromExpression(() => Method());
        _backgroundJob = new BackgroundJob("my-id", _job, _now);
        _cronExpression = Cron.Minutely();
        _storage = new Mock<JobStorage>();
        _factory = new Mock<IBackgroundJobFactory>();
        _stateMachine = new Mock<IStateMachine>();
        _factory.SetupGet(x => x.StateMachine).Returns(_stateMachine.Object);
        _nowFactory = () => _now;

        _timeZoneResolver = new Mock<ITimeZoneResolver>();
        _timeZoneResolver.Setup(x => x.GetTimeZoneById(It.IsAny<string>()))
            .Returns<string>(TimeZoneInfo.FindSystemTimeZoneById);

        _connection = new Mock<IStorageConnection>();
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);

        _transaction = new Mock<IWriteOnlyTransaction>();
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);

        _factory.Setup(x => x.Create(It.Is<CreateContext>(ctx =>
                ctx.Storage == _storage.Object &&
                ReferenceEquals(ctx.Connection, _connection.Object) &&
                ctx.InitialState == null)))
            .Returns(_backgroundJob);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenStorageIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new RecurringJobManager(null, _factory.Object));

        Assert.Equal("storage", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenFactoryIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new RecurringJobManager(_storage.Object, (IBackgroundJobFactory)null));

        Assert.Equal("factory", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenTimeZoneResolverIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new RecurringJobManager(_storage.Object, _factory.Object, null, _nowFactory));

        Assert.Equal("timeZoneResolver", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenNowFactoryIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new RecurringJobManager(_storage.Object, _factory.Object, _timeZoneResolver.Object, null));

        Assert.Equal("nowFactory", exception.ParamName);
    }

    [Fact]
    public void AddOrUpdate_ThrowsAnException_WhenIdIsNull()
    {
        RecurringJobManager manager = CreateManager();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => manager.AddOrUpdate(null, _job, Cron.Daily()));

        Assert.Equal("recurringJobId", exception.ParamName);
    }

    [Fact]
    public void AddOrUpdate_ThrowsAnException_WhenJobIsNull()
    {
        RecurringJobManager manager = CreateManager();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => manager.AddOrUpdate(_id, default(Job), Cron.Daily()));

        Assert.Equal("job", exception.ParamName);
    }

    [Fact]
    public void AddOrUpdate_ThrowsAnException_WhenCronExpressionIsNull()
    {
        RecurringJobManager manager = CreateManager();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => manager.AddOrUpdate(_id, _job, null));

        Assert.Equal("cronExpression", exception.ParamName);
    }

    [Fact]
    public void AddOrUpdate_ThrowsAnException_WhenCronExpressionIsInvalid()
    {
        RecurringJobManager manager = CreateManager();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => manager.AddOrUpdate(_id, _job, "* * *"));

        Assert.Equal("cronExpression", exception.ParamName);
    }

    [Fact]
    public void AddOrUpdate_ThrowsAnException_WhenCronExpression_HaveInvalidParts()
    {
        RecurringJobManager manager = CreateManager();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => manager.AddOrUpdate(_id, _job, "* * * * 9999"));

        Assert.Equal("cronExpression", exception.ParamName);
    }

    [Fact]
    public void AddOrUpdate_ThrowsAnException_WhenOptionsArgumentIsNull()
    {
        RecurringJobManager manager = CreateManager();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => manager.AddOrUpdate(_id, _job, _cronExpression, null));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void AddOrUpdate_ThrowsAnException_WhenMisfireHandlingIsNotDefined()
    {
        RecurringJobManager manager = CreateManager();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => manager.AddOrUpdate(_id, _job, _cronExpression, new RecurringJobOptions { MisfireHandling = (MisfireHandlingMode)7 }));

        Assert.Equal("options", exception.ParamName);
        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact]
    public void UpdateSchedule_ThrowsAnException_WhenMisfireHandlingIsNotDefined()
    {
        RecurringJobManager manager = CreateManager();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => manager.UpdateSchedule(_id, null, misfireHandling: (MisfireHandlingMode)7));

        Assert.Equal("misfireHandling", exception.ParamName);
        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact]
    public void AddOrUpdate_ThrowsAnException_WhenSkipAfterIsShorterThanASecond()
    {
        RecurringJobManager manager = CreateManager();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => manager.AddOrUpdate(_id, _job, _cronExpression, new RecurringJobOptions { SkipAfter = TimeSpan.FromMilliseconds(500) }));

        Assert.Equal("options", exception.ParamName);
        _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
    }

    [Fact]
    public void AddOrUpdate_ThrowsAnException_WhenRunLatestHasNoSkipAfter()
    {
        RecurringJobManager manager = CreateManager();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => manager.AddOrUpdate(_id, _job, _cronExpression, new RecurringJobOptions { MisfireHandling = MisfireHandlingMode.RunLatest }));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void AddOrUpdate_StoresSkipAfter_AndRunLatest()
    {
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(_id, _job, _cronExpression, new RecurringJobOptions
        {
            MisfireHandling = MisfireHandlingMode.RunLatest,
            SkipAfter = TimeSpan.FromMinutes(15),
        });

        _transaction.Verify(x => x.SetRangeInHash(
            $"recurring-job:{_id}",
            It.Is<Dictionary<string, string>>(rj => rj["SkipAfter"] == "900" && rj["Misfire"] == "3")));
    }

    [Fact]
    public void AddOrUpdate_ThrowsAnException_WhenThePolicyIsNotStored()
    {
        RecurringJobManager manager = CreateManager();

        ArgumentException exception = Assert.Throws<ArgumentException>("options",
            () => manager.AddOrUpdate(_id, _job, _cronExpression, new RecurringJobOptions { Policy = "weekdays" }));

        Assert.Contains("'weekdays'", exception.Message, StringComparison.Ordinal);
        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact]
    public void AddOrUpdate_StoresThePolicy_AndComputesTheNextRunItAllows()
    {
        Dictionary<string, string> policy = new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Monday).ToFields();
        _connection.Setup(x => x.GetAllEntriesFromHash("schedule-policy:weekdays")).Returns(policy);
        RecurringJobManager manager = CreateManager();

        // _now is Thursday 2017-03-30; the next Monday is April 3rd
        manager.AddOrUpdate(_id, _job, "0 12 * * *", new RecurringJobOptions { Policy = "Weekdays" });

        _transaction.Verify(x => x.SetRangeInHash(
            $"recurring-job:{_id}",
            It.Is<Dictionary<string, string>>(rj =>
                rj["Policy"] == "weekdays" &&
                rj["NextExecution"] == JobHelper.SerializeDateTime(new DateTime(2017, 4, 3, 12, 0, 0, DateTimeKind.Utc)))));
    }

    [Fact]
    public void AddOrUpdate_TreatsANewPolicy_AsAScheduleChange()
    {
        _connection.Setup(x => x.GetAllEntriesFromHash("schedule-policy:weekdays")).Returns(new SchedulePolicy("weekdays").ToFields());
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}"))
            .Returns(new Dictionary<string, string>
            {
                { "Job", TestJson.ToJson(InvocationData.SerializeJob(_job)) },
                { "Cron", _cronExpression },
                { "TimeZoneId", "UTC" },
                { "CreatedAt", JobHelper.SerializeDateTime(_now.AddDays(-1)) },
                { "NextExecution", JobHelper.SerializeDateTime(_now.AddMinutes(1)) },
                { "V", "2" },
            });
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(_id, _job, _cronExpression, new RecurringJobOptions { Policy = "weekdays" });

        _transaction.Verify(x => x.SetRangeInHash(
            $"recurring-job:{_id}",
            It.Is<Dictionary<string, string>>(rj => rj["Policy"] == "weekdays" && rj.ContainsKey("ScheduleChangedAt"))));
    }

    [Fact]
    public void RecurringJobOptions_Policy_IsLowerCased_AndValidated()
    {
        RecurringJobOptions options = new() { Policy = "Business-Hours" };

        Assert.Equal("business-hours", options.Policy);
        Assert.Throws<ArgumentException>("value", () => options.Policy = "not valid");
        options.Policy = null;
        Assert.Null(options.Policy);
    }

    [Fact]
    public void UpdateSchedule_ThrowsAnException_WhenRunLatestIsChosen_ForAJobWithoutSkipAfter()
    {
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}"))
            .Returns(new Dictionary<string, string>
            {
                { "Job", TestJson.ToJson(InvocationData.SerializeJob(_job)) },
                { "Cron", _cronExpression },
            });
        _connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
        RecurringJobManager manager = CreateManager();

        Assert.Throws<ArgumentException>(() => manager.UpdateSchedule(_id, null, misfireHandling: MisfireHandlingMode.RunLatest));
    }

    [Fact]
    public void Trigger_CopiesSkipAfter_ToTheRun()
    {
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}"))
            .Returns(new Dictionary<string, string>
            {
                { "Job", TestJson.ToJson(InvocationData.SerializeJob(_job)) },
                { "Cron", _cronExpression },
                { "SkipAfter", "120" },
            });
        RecurringJobManager manager = CreateManager();

        manager.Trigger(_id);

        _factory.Verify(x => x.Create(It.Is<CreateContext>(ctx =>
            (long)ctx.Parameters[SkipIfLateAttribute.WindowParameter] == 120L &&
            (string)ctx.Parameters["RecurringJobId"] == _id)));
    }

    [Fact]
    public void Trigger_RecordsTheRun_InTheRecurringJobsHistory()
    {
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}"))
            .Returns(new Dictionary<string, string>
            {
                { "Job", TestJson.ToJson(InvocationData.SerializeJob(_job)) },
                { "Cron", _cronExpression },
            });
        RecurringJobManager manager = CreateManager();

        manager.Trigger(_id);

        _transaction.Verify(x => x.InsertToList($"recurring-job:{_id}:runs", "my-id"));
        _transaction.Verify(x => x.TrimList($"recurring-job:{_id}:runs", 0, RecurringJobHistory.MaxRuns - 1));
    }

    [Fact]
    public void AddOrUpdate_ThrowsAnException_WhenJobQueueIsSet_ButStorageDoesNotSupportIt()
    {
        // Arrange
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.JobQueueProperty)).Returns(false);
        _job = Job.FromExpression(() => Method(), "some-queue");

        RecurringJobManager manager = CreateManager();

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => manager.AddOrUpdate(_id, _job, _cronExpression));
    }

    [Fact]
    public void AddOrUpdate_AddsAJob_ToTheRecurringJobsSet()
    {
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(_id, _job, _cronExpression);

        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now)));
    }

    [Fact]
    public void AddOrUpdate_SetsTheRecurringJobEntry()
    {
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(_id, _job, _cronExpression);

        _transaction.Verify(x => x.SetRangeInHash(
            $"recurring-job:{_id}",
            It.Is<Dictionary<string, string>>(rj =>
                rj["Cron"] == "* * * * *"
                && !string.IsNullOrEmpty(rj["Job"])
                && JobHelper.DeserializeDateTime(rj["CreatedAt"]) > _now.AddMinutes(-1))));
    }

    [Fact]
    public void AddOrUpdate_CommitsTransaction()
    {
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(_id, _job, _cronExpression);

        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_DoesNotUpdateCreatedAtValue_OfExistingJobs()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}"))
            .Returns(new Dictionary<string, string> { { "CreatedAt", JobHelper.SerializeDateTime(_now) } });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, _cronExpression);

        // Assert
        _transaction.Verify(
            x => x.SetRangeInHash(
                $"recurring-job:{_id}",
                It.Is<Dictionary<string, string>>(rj => rj.ContainsKey("CreatedAt"))),
            Times.Never);
    }

    [Fact]
    public void AddOrUpdate_IsAbleToScheduleSecondBasedCronExpression()
    {
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(_id, _job, "15 * * * * *");

        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now.AddSeconds(15))));
    }

    [Fact]
    public void AddOrUpdate_IsAbleToScheduleMacroBasedCronExpression()
    {
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(_id, _job, "@hourly");

        _transaction.Verify(x => x.AddToSet(
            "recurring-jobs",
            _id,
            JobHelper.ToTimestamp(_now.AddMinutes(30))));
    }

    [Fact]
    public void AddOrUpdate_EnsuresExistingOldJobsAreUpdated()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Cron", _cronExpression },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "CreatedAt", JobHelper.SerializeDateTime(_now) },
            { "NextExecution", JobHelper.SerializeDateTime(_now) },
            { "Queue", "default" },
            { "TimeZoneId", "UTC" },
            { "LastJobId", "1384" }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, _cronExpression);

        // Assert
        _transaction.Verify(x => x.SetRangeInHash(
            $"recurring-job:{_id}",
            It.Is<Dictionary<string, string>>(dict => dict.Count == 1 && dict["V"] == "2")));

        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now)));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_CanAddRecurringJob_WithCronThatNeverFires()
    {
        // Arrange
        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "0 0 31 2 *");

        // Assert
        _transaction.Verify(x => x.SetRangeInHash(
            $"recurring-job:{_id}",
            It.Is<Dictionary<string, string>>(dict =>
                dict.GetValueOrDefault("Cron") == "0 0 31 2 *" &&
                !dict.ContainsKey("NextExecution"))));

        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, -1.0D));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_CanResumeRecurringJob_ThatNeverFires()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Cron", "0 0 31 2 *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "CreatedAt", JobHelper.SerializeDateTime(_now.AddDays(-1)) },
            { "NextExecution", string.Empty },
            { "TimeZoneId", "UTC" },
            { "LastJobId", "1384" }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "* * * * *");

        // Assert
        _transaction.Verify(x => x.SetRangeInHash(
            $"recurring-job:{_id}",
            It.Is<Dictionary<string, string>>(dict =>
                dict.GetValueOrDefault("Cron") == "* * * * *" &&
                JobHelper.DeserializeNullableDateTime(dict.GetValueOrDefault("NextExecution")) == _now)));

        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now)));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_DoesNotReScheduleJob_WhenUpdatingIt()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Cron", "* * * * *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "CreatedAt", JobHelper.SerializeDateTime(_now.AddMinutes(-3)) },
            { "LastExecution", JobHelper.SerializeDateTime(_now.AddMinutes(-2)) },
            { "NextExecution", JobHelper.SerializeDateTime(_now.AddMinutes(-1)) }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "* * * * *");

        // Assert
        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(dict =>
            IsMissingOrEqual(dict, "NextExecution", JobHelper.SerializeDateTime(_now.AddMinutes(-1))))));
        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now.AddMinutes(-1))));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_CanUpdateRecurringJobs_WhoseMethodCouldNotBeFound()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Cron", "* * * * *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload().Replace("Masterly.NightShift", "Test") },
            { "CreatedAt", JobHelper.SerializeDateTime(_now.AddMinutes(-2)) },
            { "LastExecution", JobHelper.SerializeDateTime(_now.AddMinutes(-1)) },
            { "NextExecution", JobHelper.SerializeDateTime(_now) }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "* * * * *");

        // Assert
        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(
            dict => dict.GetValueOrDefault("Job", string.Empty).Contains("Masterly.NightShift.Core.Tests"))));
        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now)));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_CanUpdateRecurringJobs_WhoseJobPropertyCanNotBeDeserialized()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Cron", "* * * * *" },
            { "Job", "some garbage" },
            { "CreatedAt", JobHelper.SerializeDateTime(_now.AddMinutes(-2)) },
            { "LastExecution", JobHelper.SerializeDateTime(_now.AddMinutes(-1)) },
            { "NextExecution", JobHelper.SerializeDateTime(_now) }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "* * * * *");

        // Assert
        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(
            dict => dict.GetValueOrDefault("Job", string.Empty).Contains("Masterly.NightShift.Core.Tests"))));
        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now)));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_ResetsRetryAttemptNumber_WhenUpdatingARecurringJob()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Cron", "* * * * *" },
            { "Job", "some garbage" },
            { "RetryAttempt", "10" }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "* * * * *");

        // Assert
        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(
            dict => dict.GetValueOrDefault("RetryAttempt") == "0")));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_UsesCurrentTime_InsteadOfLastExecution_ToCalculateNextExecution_WhenChangingCronExpression()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Cron", "30 12 * * *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "LastExecution", JobHelper.SerializeDateTime(_now.AddHours(-3)) }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "30 13 * * *");

        // Assert
        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(dict =>
            JobHelper.DeserializeDateTime(dict["NextExecution"]) == _now.AddHours(22))));
        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now.AddHours(22))));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_CanRecoverARecurringJob_FromErrorState_WithoutSchedulingToThePast()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Cron", "* * * * *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "CreatedAt", JobHelper.SerializeDateTime(_now.AddDays(-1)) },
            { "Error", "Some error that's gone" },
            { "NextExecution", string.Empty },
            { "TimeZoneId", "UTC" },
            { "LastJobId", "1384" }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "* * * * *");

        // Assert
        _transaction.Verify(x => x.SetRangeInHash(
            $"recurring-job:{_id}",
            It.Is<Dictionary<string, string>>(dict =>
                dict.GetValueOrDefault("Error") == string.Empty &&
                JobHelper.DeserializeNullableDateTime(dict.GetValueOrDefault("NextExecution")) == _now)));

        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now)));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_CanNotTriggerRecurringJob_WhenNextExecutionTimeIsInFuture()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Cron", "* * * * *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "CreatedAt", JobHelper.SerializeDateTime(_now.AddDays(-1)) },
            { "LastExecution", JobHelper.SerializeDateTime(_now.AddMinutes(-5)) },
            { "NextExecution", JobHelper.SerializeDateTime(_now.AddMinutes(1)) },
            { "TimeZoneId", "UTC" }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "* * * * *");

        // Assert
        _transaction.Verify(x => x.SetRangeInHash(
            $"recurring-job:{_id}",
            It.Is<Dictionary<string, string>>(dict =>
                IsMissingOrEqual(dict, "NextExecution", JobHelper.SerializeDateTime(_now.AddMinutes(1))))));

        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now.AddMinutes(1))));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_DoesNotUpdate_UnchangedRecurringJob()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Queue", "default" },
            { "Cron", "* * * * *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "TimeZoneId", "UTC" },
            { "CreatedAt", JobHelper.SerializeDateTime(_now.AddDays(-1)) },
            { "V", "2" }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "* * * * *");

        // Assert
        _transaction.Verify(
            x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()),
            Times.Never);
        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact]
    public void AddOrUpdate_DoesNotUpdateRecurringJob_WhenErrorFieldIsSetToEmptyString()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Queue", "default" },
            { "Cron", "* * * * *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "TimeZoneId", "UTC" },
            { "CreatedAt", JobHelper.SerializeDateTime(_now.AddDays(-1)) },
            { "V", "2" },
            { "Error", "" }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "* * * * *");

        // Assert
        _transaction.Verify(
            x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()),
            Times.Never);
        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact]
    public void AddOrUpdate_DoesNotUpdateRecurringJob_WhenLastJobIdFieldIsSetToEmptyString()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Queue", "default" },
            { "Cron", "* * * * *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "TimeZoneId", "UTC" },
            { "CreatedAt", JobHelper.SerializeDateTime(_now.AddDays(-1)) },
            { "V", "2" },
            { "LastJobId", "" }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "* * * * *");

        // Assert
        _transaction.Verify(
            x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()),
            Times.Never);
        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact]
    public void Trigger_ThrowsAnException_WhenIdIsNull()
    {
        RecurringJobManager manager = CreateManager();

        Assert.Throws<ArgumentNullException>(() => manager.Trigger(null));
    }

    [Fact]
    public void Trigger_EnqueuesScheduledJob()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}"))
            .Returns(new Dictionary<string, string>
            {
                { "Job", TestJson.ToJson(InvocationData.SerializeJob(Job.FromExpression(() => Console.WriteLine()))) },
                { "Cron", Cron.Minutely() }
            });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.Trigger(_id);

        // Assert
        _stateMachine.Verify(x => x.ApplyState(
            It.Is<ApplyStateContext>(context => context.NewState is EnqueuedState)));
    }

    [Fact]
    public void Trigger_EnqueuedJobToTheSpecificQueue_IfSpecified()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}"))
            .Returns(new Dictionary<string, string>
            {
                { "Job", TestJson.ToJson(InvocationData.SerializeJob(_job)) },
                { "Cron", _cronExpression },
                { "Queue", "my_queue" }
            });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.Trigger(_id);

        // Assert
        _stateMachine.Verify(x => x.ApplyState(It.Is<ApplyStateContext>(context =>
            ((EnqueuedState)context.NewState).Queue == "my_queue")));
    }

    [Fact]
    public void Trigger_DoesNotThrowIfJobDoesNotExist()
    {
        RecurringJobManager manager = CreateManager();

        manager.Trigger(_id);

        _factory.Verify(x => x.Create(It.IsAny<CreateContext>()), Times.Never);
    }

    [Fact]
    public void TriggerJob_ReturnsNull_WhenRecurringJobDoesNotExist()
    {
        RecurringJobManager manager = CreateManager();

        string? jobId = manager.TriggerJob(_id);

        Assert.Null(jobId);
        _factory.Verify(x => x.Create(It.IsAny<CreateContext>()), Times.Never);
    }

    [Fact]
    public void Trigger_CanTriggerRecurringJob_WithCronThatNeverFires()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}"))
            .Returns(new Dictionary<string, string>
            {
                { "Job", TestJson.ToJson(InvocationData.SerializeJob(_job)) },
                { "Cron", "0 0 31 2 *" },
            });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.Trigger(_id);

        // Assert
        _stateMachine.Verify(x => x.ApplyState(It.IsAny<ApplyStateContext>()));

        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(dict =>
            dict.GetValueOrDefault("LastExecution") == JobHelper.SerializeDateTime(_now) &&
            !dict.ContainsKey("NextExecution"))));

        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, -1.0D));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void Trigger_SchedulesNextExecution_DependingOnCurrentTime_ToTheFuture()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Cron", "* * * * *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "CreatedAt", JobHelper.SerializeDateTime(_now.AddMinutes(-3)) },
            { "LastExecution", JobHelper.SerializeDateTime(_now.AddMinutes(-2)) },
            { "NextExecution", JobHelper.SerializeDateTime(_now.AddMinutes(-1)) }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.Trigger(_id);

        // Assert
        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(dict =>
            dict["NextExecution"] == JobHelper.SerializeDateTime(_now.AddMinutes(1)))));
        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now.AddMinutes(1))));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void Trigger_DoesNotMoveTheLastExecutionBack_WhenItIsLaterThanNow()
    {
        // The occurrence of the next minute ran, by a scheduler whose clock is ahead of this process's
        DateTime ran = _now.AddMinutes(1);
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Cron", "* * * * *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "CreatedAt", JobHelper.SerializeDateTime(_now.AddMinutes(-3)) },
            { "LastExecution", JobHelper.SerializeDateTime(ran) },
            { "NextExecution", JobHelper.SerializeDateTime(_now) }
        });

        RecurringJobManager manager = CreateManager();

        manager.Trigger(_id);

        // Counted from the last execution, not from now: the occurrence that ran isn't due again
        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(dict =>
            !dict.ContainsKey("LastExecution") &&
            dict["NextExecution"] == JobHelper.SerializeDateTime(_now.AddMinutes(2)))));
        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now.AddMinutes(2))));
    }

    [Fact]
    public void Trigger_ThrowsAnException_WhenRecurringJobCanNotBeTriggered_AndDoesNotCreateBackgroundJob()
    {
        // Arrange
        _timeZoneResolver.Setup(x => x.GetTimeZoneById(It.IsAny<string>())).Throws<Exception>();
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}"))
            .Returns(new Dictionary<string, string>
            {
                { "Job", TestJson.ToJson(InvocationData.SerializeJob(Job.FromExpression(() => Console.WriteLine()))) },
                { "Cron", Cron.Minutely() },
                { "TimeZoneId", "UnexistingID" }
            });

        RecurringJobManager manager = CreateManager();

        // Act
        Assert.Throws<AggregateException>(() => manager.Trigger(_id));

        // Assert
        _stateMachine.Verify(x => x.ApplyState(It.IsAny<ApplyStateContext>()), Times.Never);
    }

    [Fact]
    public void RemoveIfExists_ThrowsAnException_WhenIdIsNull()
    {
        RecurringJobManager manager = CreateManager();

        Assert.Throws<ArgumentNullException>(
            () => manager.RemoveIfExists(null));
    }

    [Fact]
    public void RemoveIfExists_RemovesEntriesAndCommitsTheTransaction()
    {
        RecurringJobManager manager = CreateManager();

        manager.RemoveIfExists(_id);

        _transaction.Verify(x => x.RemoveFromSet("recurring-jobs", _id));
        _transaction.Verify(x => x.RemoveHash($"recurring-job:{_id}"));
        _transaction.Verify(x => x.TrimList($"recurring-job:{_id}:runs", 1, 0));
        _transaction.Verify(x => x.Commit());
    }

    [Fact, CleanSerializerSettings]
    public void HandlesChangingProcessOfInvocationDataSerialization()
    {
        SerializationHelper.SetUserSerializerSettings(SerializerSettingsHelper.DangerousSettings);

        Job initialJob = Job.FromExpression(() => Console.WriteLine());
        InvocationData invocationData = InvocationData.SerializeJob(initialJob);

        string serializedInvocationData = SerializationHelper.Serialize(invocationData, SerializationOption.User);

        InvocationData? deserializedInvocationData = SerializationHelper.Deserialize<InvocationData>(serializedInvocationData);
        Job deserializedJob = deserializedInvocationData.DeserializeJob();

        Assert.Equal(initialJob.Args, deserializedJob.Args);
        Assert.Equal(initialJob.Method, deserializedJob.Method);
        Assert.Equal(initialJob.Type, deserializedJob.Type);
    }

    [Fact]
    public void TriggerJob_RunsThePostCommitActionsOfTheEnqueuedState_OnlyAfterItsCommit()
    {
        // Arrange
        SetupRecurringJobForTrigger();
        List<string> calls = [];
        _stateMachine.Setup(x => x.ApplyState(It.IsAny<ApplyStateContext>()))
            .Callback<ApplyStateContext>(ctx =>
            {
                Assert.NotNull(ctx.AfterCommit);
                ctx.RunAfterCommit(() => calls.Add("action"));
            })
            .Returns<ApplyStateContext>(ctx => ctx.NewState);
        _transaction.Setup(x => x.Commit()).Callback(() => calls.Add("commit"));
        RecurringJobManager manager = CreateManager();

        // Act
        string? jobId = manager.TriggerJob(_id);

        // Assert
        Assert.Equal(_backgroundJob.Id, jobId);
        Assert.Equal(new[] { "commit", "action" }, calls);
    }

    [Fact]
    public void TriggerJob_DropsThePostCommitActionsOfTheEnqueuedState_WhenTheCommitFails()
    {
        // Arrange
        SetupRecurringJobForTrigger();
        List<string> calls = [];
        _stateMachine.Setup(x => x.ApplyState(It.IsAny<ApplyStateContext>()))
            .Callback<ApplyStateContext>(ctx =>
            {
                ctx.RunAfterCommit(() => calls.Add("action"));
                ctx.AfterCommit.AddRegardless(() => calls.Add("regardless"));
            })
            .Returns<ApplyStateContext>(ctx => ctx.NewState);
        _transaction.Setup(x => x.Commit()).Throws(new TimeoutException("commit failed"));
        RecurringJobManager manager = CreateManager();

        // Act
        Assert.Throws<TimeoutException>(() => manager.TriggerJob(_id));

        // Assert
        Assert.Equal(new[] { "regardless" }, calls);
    }

    private void SetupRecurringJobForTrigger()
    {
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}"))
            .Returns(new Dictionary<string, string>
            {
                { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
                { "Cron", _cronExpression }
            });
    }

    private RecurringJobManager CreateManager()
    {
        return new RecurringJobManager(_storage.Object, _factory.Object, _timeZoneResolver.Object, _nowFactory);
    }

    private static bool IsMissingOrEqual(Dictionary<string, string> fields, string name, string expected)
    {
        return !fields.TryGetValue(name, out string? value) || value == expected;
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static void Method() { }

    [Fact]
    public void AddOrUpdate_UsesTimeZoneResolver_WhenCalculatingNextExecution()
    {
        // Arrange
        TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(PlatformHelper.IsRunningOnWindows()
            ? "Hawaiian Standard Time"
            : "Pacific/Honolulu");

        _timeZoneResolver.Setup(x => x.GetTimeZoneById(It.IsAny<string>())).Throws<InvalidOperationException>();
        _timeZoneResolver
            .Setup(x => x.GetTimeZoneById(It.Is<string>(id => id == "Hawaiian Standard Time" || id == "Pacific/Honolulu")))
            .Returns(timeZone);

        // We are returning IANA time zone on Windows and Windows time zone on Linux.
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Cron", "0 0 * * *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "CreatedAt", JobHelper.SerializeDateTime(_now) },
            { "TimeZoneId", PlatformHelper.IsRunningOnWindows() ? "Pacific/Honolulu" : "Hawaiian Standard Time" },
            { "NextExecution", JobHelper.SerializeDateTime(_now.AddHours(18).AddMinutes(30)) },
            { "Queue", "default" },
            { "V", "2" }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "0 0 * * *", new RecurringJobOptions { TimeZone = timeZone });

        // Assert
        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(dict =>
            dict.ContainsKey("TimeZoneId") && !dict.ContainsKey("NextExecution"))));
        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now.AddHours(18).AddMinutes(30))));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_UsesCurrentTime_InsteadOfLastExecution_ToCalculateNextExecution_WhenChangingTimeZone()
    {
        // Arrange
        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(new Dictionary<string, string>
        {
            { "Cron", "30 13 * * *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "TimeZoneId", PlatformHelper.IsRunningOnWindows() ? "Pacific/Honolulu" : "Hawaiian Standard Time" },
            { "LastExecution", JobHelper.SerializeDateTime(_now.AddDays(-3)) }
        });

        RecurringJobManager manager = CreateManager();

        // Act
        manager.AddOrUpdate(_id, _job, "30 13 * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // Assert
        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(dict =>
            JobHelper.DeserializeDateTime(dict["NextExecution"]) == _now.AddHours(22))));
        _transaction.Verify(x => x.AddToSet("recurring-jobs", _id, JobHelper.ToTimestamp(_now.AddHours(22))));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_KeepsThePendingOccurrence_OfAnUnchangedJob_ThatWaitsForItsPolicy()
    {
        // The occurrences since 15:20 wait for the policy, and the scheduler is about to look at the job again
        StoreJobThatWaitsForItsPolicy();
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(_id, _job, "* * * * *", new RecurringJobOptions { Policy = "holidays" });

        _transaction.Verify(
            x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()),
            Times.Never);
        _transaction.Verify(x => x.Commit(), Times.Never);
    }

    [Fact]
    public void AddOrUpdate_KeepsThePendingOccurrence_WhenOnlyTheQueueOrTheMisfireHandlingChanges()
    {
        StoreJobThatWaitsForItsPolicy();
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(_id, _job, "* * * * *", new RecurringJobOptions
        {
            Policy = "holidays",
            QueueName = "critical",
            MisfireHandling = MisfireHandlingMode.Strict,
        });

        // The scheduler applies the new misfire handling to the occurrences missed, from the last execution
        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(dict =>
            dict["Queue"] == "critical" &&
            dict["Misfire"] == "1" &&
            !dict.ContainsKey("NextExecution") &&
            !dict.ContainsKey("Error"))));
        _transaction.Verify(x => x.Commit());
    }

    [Theory]
    [InlineData("*/5 * * * *", "holidays")]
    [InlineData("* * * * *", "other")]
    [InlineData("* * * * *", null)]
    public void AddOrUpdate_ReschedulesAJobThatWaitsForItsPolicy_WhenItsScheduleChanges(string cronExpression, string policy)
    {
        StoreJobThatWaitsForItsPolicy();
        _connection.Setup(x => x.GetAllEntriesFromHash("schedule-policy:other")).Returns(new SchedulePolicy("other").ToFields());
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(_id, _job, cronExpression, new RecurringJobOptions { Policy = policy });

        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(dict =>
            JobHelper.DeserializeDateTime(dict["NextExecution"]) == _now &&
            dict["Error"] == string.Empty)));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_ReschedulesAJobThatWaitsForItsPolicy_WhenItsTimeZoneChanges()
    {
        StoreJobThatWaitsForItsPolicy();
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(_id, _job, "* * * * *", new RecurringJobOptions
        {
            Policy = "holidays",
            TimeZone = TimeZoneInfo.FindSystemTimeZoneById(PlatformHelper.IsRunningOnWindows() ? "Hawaiian Standard Time" : "Pacific/Honolulu"),
        });

        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(dict =>
            dict.ContainsKey("TimeZoneId") &&
            JobHelper.DeserializeDateTime(dict["NextExecution"]) == _now &&
            dict["Error"] == string.Empty)));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_StillRecoversFromAnotherError_OfAJobThatFollowsAPolicy()
    {
        Dictionary<string, string> stored = StoreJobThatWaitsForItsPolicy();
        stored["Error"] = "Some error that's gone";
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(_id, _job, "* * * * *", new RecurringJobOptions { Policy = "holidays" });

        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(dict =>
            JobHelper.DeserializeDateTime(dict["NextExecution"]) == _now &&
            dict["Error"] == string.Empty)));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void AddOrUpdate_StillRecoversFromAPolicyError_OfAJobThatLeftTheSchedule()
    {
        // Without a next execution the scheduler never looks at the job again, so registering it schedules it
        Dictionary<string, string> stored = StoreJobThatWaitsForItsPolicy();
        stored["NextExecution"] = string.Empty;
        RecurringJobManager manager = CreateManager();

        manager.AddOrUpdate(_id, _job, "* * * * *", new RecurringJobOptions { Policy = "holidays" });

        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(dict =>
            JobHelper.DeserializeDateTime(dict["NextExecution"]) == _now &&
            dict["Error"] == string.Empty)));
    }

    [Fact]
    public void UpdateSchedule_KeepsThePendingOccurrence_OfAJobThatWaitsForItsPolicy_WhenItsScheduleIsTheSame()
    {
        StoreJobThatWaitsForItsPolicy();
        RecurringJobManager manager = CreateManager();

        bool updated = manager.UpdateSchedule(_id, "* * * * *", TimeZoneInfo.Utc);

        Assert.True(updated);
        _transaction.Verify(
            x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()),
            Times.Never);
    }

    [Fact]
    public void UpdateSchedule_ReschedulesAJobThatWaitsForItsPolicy_WhenItsScheduleChanges()
    {
        StoreJobThatWaitsForItsPolicy();
        RecurringJobManager manager = CreateManager();

        manager.UpdateSchedule(_id, "*/5 * * * *");

        _transaction.Verify(x => x.SetRangeInHash($"recurring-job:{_id}", It.Is<Dictionary<string, string>>(dict =>
            JobHelper.DeserializeDateTime(dict["NextExecution"]) == _now &&
            dict["Error"] == string.Empty)));
    }

    // As the scheduler left it at 15:28:45, when the policy couldn't be evaluated: the last run was at 15:20, and
    // the job is looked at again at 15:29:45, so it is due. The policy can be evaluated by now, and allows every time
    private Dictionary<string, string> StoreJobThatWaitsForItsPolicy()
    {
        Dictionary<string, string> stored = new()
        {
            { "Queue", "default" },
            { "Cron", "* * * * *" },
            { "Job", InvocationData.SerializeJob(_job).SerializePayload() },
            { "TimeZoneId", "UTC" },
            { "CreatedAt", JobHelper.SerializeDateTime(_now.AddDays(-1)) },
            { "V", "2" },
            { "Policy", "holidays" },
            { "LastExecution", JobHelper.SerializeDateTime(_now.AddMinutes(-10)) },
            { "NextExecution", JobHelper.SerializeDateTime(_now.AddSeconds(-15)) },
            { "Error", "Schedule policy 'holidays' can't be evaluated, so the job waits and tries again: policy 'holidays': date provider 'public-holidays' failed: database down." },
        };

        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{_id}")).Returns(stored);
        _connection.Setup(x => x.GetAllEntriesFromHash("schedule-policy:holidays")).Returns(new SchedulePolicy("holidays").ToFields());
        return stored;
    }
}
