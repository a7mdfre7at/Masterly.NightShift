using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests;

public sealed class RecurringJobSetFacts : IDisposable
{
    private const string Cron = "0 * * * *";

    private readonly Mock<IRecurringJobManager> _manager = new();
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly Dictionary<string, RecurringJobDto> _stored = new(StringComparer.Ordinal);

    public RecurringJobSetFacts()
    {
        JobHandlers.Clear();

        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
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
