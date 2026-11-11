using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public sealed class RecurringJobSetFacts : IDisposable
{
    private const string Cron = "0 * * * *";

    private readonly Mock<IRecurringJobManager> _manager = new();
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly Dictionary<string, RecurringJobDto> _stored = new(StringComparer.Ordinal);

    public RecurringJobSetFacts()
    {
        JobHandlers.Clear();

        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _connection.Setup(x => x.GetAllItemsFromSet("recurring-jobs")).Returns(() => [.. _stored.Keys]);
        _connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>())).Returns((string key) =>
            new Dictionary<string, string> { ["Cron"] = Cron, ["Job"] = InvocationData.SerializeJob(Job.FromExpression(() => Run())).SerializePayload() });
    }

    public void Dispose()
    {
        JobHandlers.Clear();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Add_ThrowsAnException_WhenTheIdIsMissing(string id)
    {
        RecurringJobSet jobs = new();

        Assert.ThrowsAny<ArgumentException>(() => jobs.Add(id, () => Run(), Cron));
    }

    [Fact]
    public void Add_ThrowsAnException_NamingTheJob_WhenTheCronIsInvalid()
    {
        RecurringJobSet jobs = new();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => jobs.Add("report", () => Run(), "not a cron"));

        Assert.Contains("report", exception.Message);
        Assert.Contains("not a cron", exception.Message);
    }

    [Fact]
    public void Add_ThrowsAnException_NamingTheJob_WhenTheIdIsDeclaredTwice()
    {
        RecurringJobSet jobs = new();
        jobs.Add("report", () => Run(), Cron);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => jobs.Add("report", () => Run(), Cron));

        Assert.Contains("report", exception.Message);
    }

    [Fact]
    public void Add_KeepsTheIdAvailable_WhenTheDeclarationWasRejected()
    {
        RecurringJobSet jobs = new();
        Assert.Throws<InvalidOperationException>(() => jobs.Add("report", () => Run(), "not a cron"));

        jobs.Add("report", () => Run(), Cron);

        Assert.Equal(["report"], jobs.DeclaredIds);
    }

    [Fact]
    public void AddHandler_ThrowsAnException_NamingTheJob_WhenNoHandlerIsRegistered()
    {
        RecurringJobSet jobs = new();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => jobs.AddHandler("purge", new PurgeArgs(30), Cron));

        Assert.Contains("purge", exception.Message);
        Assert.Contains(nameof(PurgeArgs), exception.Message);
    }

    [Fact]
    public void Apply_RegistersEachDeclaredJob_WithItsScheduleAndOptions()
    {
        RecurringJobOptions options = new() { QueueName = "reports" };
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), Cron, options);

        RecurringJobSetResult result = jobs.Apply(_manager.Object, _storage.Object);

        _manager.Verify(x => x.AddOrUpdate("report", It.Is<Job>(job => job.Method.Name == nameof(Run)), Cron, options));
        Assert.Equal(["report"], result.Applied);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Apply_UsesDefaultOptions_WhenNoneAreGiven()
    {
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), Cron);

        jobs.Apply(_manager.Object, _storage.Object);

        _manager.Verify(x => x.AddOrUpdate("report", It.IsAny<Job>(), Cron, It.IsNotNull<RecurringJobOptions>()));
    }

    [Fact]
    public void Apply_RegistersATypedHandler_AgainstTheHandlerClass()
    {
        JobHandlers.Add<PurgeHandler>();
        RecurringJobSet jobs = new RecurringJobSet().AddHandler("purge", new PurgeArgs(30), Cron);

        jobs.Apply(_manager.Object, _storage.Object);

        _manager.Verify(x => x.AddOrUpdate("purge", It.Is<Job>(job => job.Type == typeof(PurgeHandler)), Cron, It.IsAny<RecurringJobOptions>()));
    }

    [Fact]
    public void Apply_RemovesNothing_ByDefault()
    {
        _stored["old"] = new RecurringJobDto();
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), Cron);

        jobs.Apply(_manager.Object, _storage.Object);

        _manager.Verify(x => x.RemoveIfExists(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Apply_RemovesUndeclaredJobs_WhenAskedTo()
    {
        _stored["report"] = new RecurringJobDto();
        _stored["old"] = new RecurringJobDto();
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), Cron).RemoveUndeclared();

        RecurringJobSetResult result = jobs.Apply(_manager.Object, _storage.Object);

        _manager.Verify(x => x.RemoveIfExists("old"));
        _manager.Verify(x => x.RemoveIfExists("report"), Times.Never);
        Assert.Equal(["old"], result.Removed);
    }

    [Fact]
    public void Apply_LeavesJobsOutsideTheScope_WhenRemovingUndeclaredOnes()
    {
        _stored["app:old"] = new RecurringJobDto();
        _stored["manual"] = new RecurringJobDto();
        RecurringJobSet jobs = new RecurringJobSet()
            .Add("app:report", () => Run(), Cron)
            .RemoveUndeclared(static id => id.StartsWith("app:", StringComparison.Ordinal));

        RecurringJobSetResult result = jobs.Apply(_manager.Object, _storage.Object);

        _manager.Verify(x => x.RemoveIfExists("manual"), Times.Never);
        Assert.Equal(["app:old"], result.Removed);
    }

    [Fact]
    public void Apply_RemovesNothing_WhenNoJobWasDeclared()
    {
        _stored["old"] = new RecurringJobDto();
        RecurringJobSet jobs = new RecurringJobSet().RemoveUndeclared();

        RecurringJobSetResult result = jobs.Apply(_manager.Object, _storage.Object);

        _manager.Verify(x => x.RemoveIfExists(It.IsAny<string>()), Times.Never);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Apply_RemovesNothing_WhenApplyingAJobFails()
    {
        _stored["old"] = new RecurringJobDto();
        _manager.Setup(x => x.AddOrUpdate("report", It.IsAny<Job>(), Cron, It.IsAny<RecurringJobOptions>())).Throws<InvalidOperationException>();
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), Cron).RemoveUndeclared();

        Assert.Throws<InvalidOperationException>(() => jobs.Apply(_manager.Object, _storage.Object));

        _manager.Verify(x => x.RemoveIfExists(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Add_ForAJobClass_UsesItsNameAsTheId()
    {
        RecurringJobSet jobs = new RecurringJobSet().Add<NightlyJob>(Cron);

        jobs.Apply(_manager.Object, _storage.Object);

        _manager.Verify(x => x.AddOrUpdate("RecurringJobSetFacts.NightlyJob", It.Is<Job>(job => job.Type == typeof(NightlyJob)), Cron, It.IsAny<RecurringJobOptions>()));
    }

    [Fact]
    public void Add_ForAJobClass_UsesTheGivenId()
    {
        RecurringJobSet jobs = new RecurringJobSet().Add<NightlyJob>("nightly", Cron);

        Assert.Equal(["nightly"], jobs.DeclaredIds);
    }

    [Fact]
    public void Add_ForAJobClass_ThrowsAnException_WhenItIsDeclaredTwiceWithoutAnId()
    {
        RecurringJobSet jobs = new RecurringJobSet().Add<NightlyJob>(Cron);

        Assert.Throws<InvalidOperationException>(() => jobs.Add<NightlyJob>(Cron));
    }

    [Fact]
    public void AddHandler_WithoutAnId_UsesTheNameOfTheHandler()
    {
        JobHandlers.Add<PurgeHandler>();

        RecurringJobSet jobs = new RecurringJobSet().AddHandler(new PurgeArgs(30), Cron);

        Assert.Equal(["RecurringJobSetFacts.PurgeHandler"], jobs.DeclaredIds);
    }

    [Fact]
    public void AddHandler_WithAChosenHandler_TargetsIt_WithoutARegistration()
    {
        RecurringJobSet jobs = new RecurringJobSet().AddHandler<PurgeHandler, PurgeArgs>("purge", new PurgeArgs(30), Cron);

        jobs.Apply(_manager.Object, _storage.Object);

        _manager.Verify(x => x.AddOrUpdate("purge", It.Is<Job>(job => job.Type == typeof(PurgeHandler)), Cron, It.IsAny<RecurringJobOptions>()));
    }

    [Fact]
    public void AddHandler_WithAChosenHandler_AndNoId_UsesItsName()
    {
        RecurringJobSet jobs = new RecurringJobSet().AddHandler<PurgeHandler, PurgeArgs>(new PurgeArgs(30), Cron);

        Assert.Equal(["RecurringJobSetFacts.PurgeHandler"], jobs.DeclaredIds);
    }

    [Fact]
    public void Apply_ThrowsAnException_NamingTheJob_AndAppliesNothing_WhenAPlaceholderIsUnresolved()
    {
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), Cron).Add("purge", () => Run(), "%Billing:Cron%");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => jobs.Apply(_manager.Object, _storage.Object));

        Assert.Contains("purge", exception.Message);
        _manager.Verify(x => x.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()), Times.Never);
    }

    [Fact]
    public void ResolvePlaceholders_AppliesTheValueOfTheKey()
    {
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), "%Billing:Cron%");

        jobs.ResolvePlaceholders(static key => key == "Billing:Cron" ? "*/5 * * * *" : null).Apply(_manager.Object, _storage.Object);

        _manager.Verify(x => x.AddOrUpdate("report", It.IsAny<Job>(), "*/5 * * * *", It.IsAny<RecurringJobOptions>()));
    }

    [Fact]
    public void ResolvePlaceholders_LeavesAPlainScheduleAlone()
    {
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), Cron);

        jobs.ResolvePlaceholders(static _ => "*/5 * * * *").Apply(_manager.Object, _storage.Object);

        _manager.Verify(x => x.AddOrUpdate("report", It.IsAny<Job>(), Cron, It.IsAny<RecurringJobOptions>()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ResolvePlaceholders_ThrowsAnException_NamingTheJobAndKey_WhenTheKeyHasNoValue(string value)
    {
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), "%Billing:Cron%");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => jobs.ResolvePlaceholders(_ => value));

        Assert.Contains("report", exception.Message);
        Assert.Contains("Billing:Cron", exception.Message);
    }

    [Fact]
    public void ResolvePlaceholders_ThrowsAnException_NamingTheJob_WhenTheValueIsNotACron()
    {
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), "%Billing:Cron%");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => jobs.ResolvePlaceholders(static _ => "not a cron"));

        Assert.Contains("report", exception.Message);
        Assert.Contains("not a cron", exception.Message);
    }

    [Fact]
    public void ResolvePlaceholders_ThrowsAnException_WhenTheLookupIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new RecurringJobSet().ResolvePlaceholders(null));
    }

    [Fact]
    public void Apply_ThrowsAnException_WhenManagerIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => new RecurringJobSet().Apply(null, _storage.Object));

        Assert.Equal("manager", exception.ParamName);
    }

    [Fact]
    public void Apply_MarksADeclaredJob_TheFirstTime()
    {
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), Cron);

        jobs.Apply(_manager.Object, _storage.Object);

        _transaction.Verify(x => x.SetRangeInHash("recurring-job:report", It.Is<IEnumerable<KeyValuePair<string, string>>>(
            fields => fields.Contains(new KeyValuePair<string, string>("Declared", "1")))));
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void Apply_WritesNothingMore_ForAJobAlreadyMarked()
    {
        StoreHash("report", declared: true);
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), Cron);

        jobs.Apply(_manager.Object, _storage.Object);

        _transaction.Verify(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()), Times.Never);
    }

    [Fact]
    public void Apply_ResumesARetiredJob_ThatIsDeclaredAgain()
    {
        Mock<IRecurringJobManagerV3> manager = new();
        StoreHash("report", declared: true, retired: true);
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), Cron);

        jobs.Apply(manager.Object, _storage.Object);

        manager.Verify(x => x.Resume("report"));
        _transaction.Verify(x => x.SetRangeInHash("recurring-job:report", It.Is<IEnumerable<KeyValuePair<string, string>>>(
            fields => fields.Contains(new KeyValuePair<string, string>("Retired", string.Empty)))));
    }

    [Fact]
    public void Apply_RetiresUndeclaredJobs_ThatASetDeclared_AndOnlyThem()
    {
        Mock<IRecurringJobManagerV3> manager = new();
        StoreHash("report", declared: true);
        StoreHash("old", declared: true);
        StoreHash("made-on-the-dashboard", declared: false);
        StoreHash("already-retired", declared: true, retired: true);
        RecurringJobSet jobs = new RecurringJobSet().Add("report", () => Run(), Cron).RetireUndeclared();

        RecurringJobSetResult result = jobs.Apply(manager.Object, _storage.Object);

        Assert.Equal(["old"], result.Retired);
        manager.Verify(x => x.Pause("old"));
        manager.Verify(x => x.Pause("made-on-the-dashboard"), Times.Never);
        manager.Verify(x => x.Pause("report"), Times.Never);
        manager.Verify(x => x.Pause("already-retired"), Times.Never);
        manager.Verify(x => x.RemoveIfExists(It.IsAny<string>()), Times.Never);
        _transaction.Verify(x => x.SetRangeInHash("recurring-job:old", It.Is<IEnumerable<KeyValuePair<string, string>>>(
            fields => fields.Contains(new KeyValuePair<string, string>("Retired", "1")))));
    }

    [Fact]
    public void Apply_RetiresOnlyJobsInTheScope()
    {
        Mock<IRecurringJobManagerV3> manager = new();
        StoreHash("app:old", declared: true);
        StoreHash("other:old", declared: true);
        RecurringJobSet jobs = new RecurringJobSet()
            .Add("app:report", () => Run(), Cron)
            .RetireUndeclared(static id => id.StartsWith("app:", StringComparison.Ordinal));

        RecurringJobSetResult result = jobs.Apply(manager.Object, _storage.Object);

        Assert.Equal(["app:old"], result.Retired);
    }

    [Fact]
    public void Apply_RetiresNothing_WhenNoJobWasDeclared_OrTheManagerCannotPause()
    {
        StoreHash("old", declared: true);

        Assert.Empty(new RecurringJobSet().RetireUndeclared().Apply(new Mock<IRecurringJobManagerV3>().Object, _storage.Object).Retired);
        Assert.Empty(new RecurringJobSet().Add("report", () => Run(), Cron).RetireUndeclared().Apply(_manager.Object, _storage.Object).Retired);
    }

    [Fact]
    public void RetireUndeclared_AndRemoveUndeclared_CannotBeCombined()
    {
        Assert.Throws<InvalidOperationException>(() => new RecurringJobSet().RemoveUndeclared().RetireUndeclared());
        Assert.Throws<InvalidOperationException>(() => new RecurringJobSet().RetireUndeclared().RemoveUndeclared());
    }

    [Fact]
    public void Apply_CreatesAOnceJob_AndRecordsItsKey()
    {
        SetupJobCreation();
        RecurringJobSet jobs = new RecurringJobSet().Once("seed-roles", () => Run());

        RecurringJobSetResult result = jobs.Apply(_manager.Object, _storage.Object);

        Assert.Equal(["seed-roles"], result.CreatedOnce);
        _connection.Verify(x => x.AcquireDistributedLock("recurring-job-set:once:lock", It.IsAny<TimeSpan>()));
        _transaction.Verify(x => x.SetRangeInHash(RecurringJobSet.OnceHashKey, It.Is<IEnumerable<KeyValuePair<string, string>>>(
            fields => fields.Contains(new KeyValuePair<string, string>("seed-roles", "42")))));
    }

    [Fact]
    public void Apply_SkipsAOnceJob_WhoseKeyWasRecorded()
    {
        SetupJobCreation();
        _connection.Setup(x => x.GetAllEntriesFromHash(RecurringJobSet.OnceHashKey)).Returns(new Dictionary<string, string> { ["seed-roles"] = "7" });
        RecurringJobSet jobs = new RecurringJobSet().Once("seed-roles", () => Run()).Once("seed-users", () => Run());

        RecurringJobSetResult result = jobs.Apply(_manager.Object, _storage.Object);

        Assert.Equal(["seed-users"], result.CreatedOnce);
        _connection.Verify(x => x.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public void Apply_SchedulesAOnceJob_WithADelay()
    {
        SetupJobCreation();
        RecurringJobSet jobs = new RecurringJobSet().Once<RecurringJobSetFacts>("warm-cache", x => x.Instance(), TimeSpan.FromMinutes(5));

        jobs.Apply(_manager.Object, _storage.Object);

        _transaction.Verify(x => x.SetJobState("42", It.Is<IState>(state => state is ScheduledState)));
    }

    [Fact]
    public void Apply_DoesNotTakeTheLock_WithoutOnceJobs()
    {
        new RecurringJobSet().Add("report", () => Run(), Cron).Apply(_manager.Object, _storage.Object);

        _connection.Verify(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Once_ThrowsAnException_WhenTheKeyIsMissing(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => new RecurringJobSet().Once(key, () => Run()));
    }

    [Fact]
    public void Once_ThrowsAnException_WhenTheKeyIsTooLong_OrTheDelayNegative_OrTheKeyRepeats()
    {
        RecurringJobSet jobs = new RecurringJobSet().Once("seed", () => Run());

        Assert.Throws<ArgumentException>(() => jobs.Once(new string('k', 101), () => Run()));
        Assert.Throws<ArgumentOutOfRangeException>(() => jobs.Once("later", () => Run(), TimeSpan.FromSeconds(-1)));
        Assert.Throws<InvalidOperationException>(() => jobs.Once("seed", () => Run()));
    }

    private void StoreHash(string id, bool declared, bool retired = false)
    {
        _stored[id] = new RecurringJobDto();
        Dictionary<string, string> hash = new()
        {
            ["Cron"] = Cron,
            ["Job"] = InvocationData.SerializeJob(Job.FromExpression(() => Run())).SerializePayload(),
        };

        if (declared) hash["Declared"] = "1";
        if (retired) hash["Retired"] = "1";

        _connection.Setup(x => x.GetAllEntriesFromHash($"recurring-job:{id}")).Returns(hash);
    }

    private void SetupJobCreation()
    {
        _connection.Setup(x => x.GetAllEntriesFromHash(RecurringJobSet.OnceHashKey)).Returns((Dictionary<string, string>)null);
        _connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
        _connection.Setup(x => x.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>())).Returns("42");
        _connection.Setup(x => x.GetJobData("42")).Returns(new JobData());
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public void Instance()
    {
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Run()
    {
    }

    public sealed record PurgeArgs(int Days);

    public sealed class NightlyJob : IJob
    {
        public Task RunAsync(PerformContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class PurgeHandler : IJobHandler<PurgeArgs>
    {
        public Task HandleAsync(PurgeArgs args, PerformContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
