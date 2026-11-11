using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Masterly.NightShift.AspNetCore.Tests.DependencyInjection;

public sealed class NightShiftJobDeclarationServiceCollectionExtensionsFacts : IDisposable
{
    private const string Hourly = "0 * * * *";

    private readonly Mock<IRecurringJobManager> _manager = new();
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IStorageConnection> _connection = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly ServiceCollection _services = new();

    public NightShiftJobDeclarationServiceCollectionExtensionsFacts()
    {
        JobHandlers.Clear();

        // Applying a set marks each declared job in the storage
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);

        _services.AddLogging();
        _services.AddSingleton(_manager.Object);
        _services.AddSingleton(_storage.Object);
    }

    public void Dispose()
    {
        JobHandlers.Clear();
    }

    [Fact]
    public void AddNightShiftJobHandler_RegistersTheHandler_ForItsArguments()
    {
        _services.AddNightShiftJobHandler<SampleHandler>();

        Assert.Equal([typeof(SampleHandler)], JobHandlers.GetHandlerTypes(typeof(SampleArgs)));
    }

    [Fact]
    public void AddNightShiftJobHandler_AddsAJobClassToTheContainer()
    {
        _services.AddNightShiftJobHandler<SampleJob>();

        Assert.Contains(_services, static x => x.ServiceType == typeof(SampleJob));
    }

    [Fact]
    public void AddNightShiftJobHandler_AddsTheHandlerToTheContainer_AsScoped()
    {
        _services.AddNightShiftJobHandler<SampleHandler>();

        ServiceDescriptor descriptor = Assert.Single(_services, static x => x.ServiceType == typeof(SampleHandler));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddNightShiftJobHandler_KeepsARegistrationTheAppAlreadyMade()
    {
        _services.AddSingleton<SampleHandler>();

        _services.AddNightShiftJobHandler<SampleHandler>();

        ServiceDescriptor descriptor = Assert.Single(_services, static x => x.ServiceType == typeof(SampleHandler));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddNightShiftJobHandlers_RegistersTheHandlersOfTheAssembly()
    {
        _services.AddNightShiftJobHandlers(typeof(SampleHandler).Assembly);

        Assert.Equal([typeof(SampleHandler)], JobHandlers.GetHandlerTypes(typeof(SampleArgs)));
        Assert.Contains(_services, static x => x.ServiceType == typeof(SampleHandler));
        Assert.Contains(_services, static x => x.ServiceType == typeof(SampleJob));
    }

    [Fact]
    public async Task AddNightShiftRecurringJobs_AppliesTheJobs_WhenTheHostStarts()
    {
        _services.AddNightShiftRecurringJobs(static jobs => jobs.Add("report", () => Run(), Hourly));

        await StartHostedServicesAsync();

        _manager.Verify(x => x.AddOrUpdate("report", It.IsAny<Job>(), Hourly, It.IsAny<RecurringJobOptions>()));
    }

    [Fact]
    public void AddNightShiftRecurringJobs_DoesNotApplyAnything_BeforeTheHostStarts()
    {
        _services.AddNightShiftRecurringJobs(static jobs => jobs.Add("report", () => Run(), Hourly));

        _ = _services.BuildServiceProvider().GetServices<IHostedService>().ToList();

        _manager.Verify(x => x.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()), Times.Never);
    }

    [Fact]
    public void AddNightShiftRecurringJobs_AddsOneHostedService_WhenCalledSeveralTimes()
    {
        _services.AddNightShiftRecurringJobs(static jobs => jobs.Add("first", () => Run(), Hourly));
        _services.AddNightShiftRecurringJobs(static jobs => jobs.Add("second", () => Run(), Hourly));

        Assert.Single(_services, static x => x.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public async Task AddNightShiftRecurringJobs_CombinesEveryCall_AndEveryProvider()
    {
        _services.AddNightShiftRecurringJobs(static jobs => jobs.Add("first", () => Run(), Hourly));
        _services.AddNightShiftRecurringJobs<SampleProvider>();

        await StartHostedServicesAsync();

        _manager.Verify(x => x.AddOrUpdate("first", It.IsAny<Job>(), Hourly, It.IsAny<RecurringJobOptions>()));
        _manager.Verify(x => x.AddOrUpdate(SampleProvider.Id, It.IsAny<Job>(), Hourly, It.IsAny<RecurringJobOptions>()));
    }

    [Fact]
    public async Task AddNightShiftRecurringJobs_AddsAProviderOnce_WhenCalledTwice()
    {
        _services.AddNightShiftRecurringJobs<SampleProvider>();
        _services.AddNightShiftRecurringJobs<SampleProvider>();

        await StartHostedServicesAsync();

        _manager.Verify(x => x.AddOrUpdate(SampleProvider.Id, It.IsAny<Job>(), Hourly, It.IsAny<RecurringJobOptions>()), Times.Once);
    }

    [Fact]
    public async Task HostedService_StopsTheStart_WhenADeclarationIsInvalid()
    {
        _services.AddNightShiftRecurringJobs(static jobs => jobs.Add("report", () => Run(), "not a cron"));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(StartHostedServicesAsync);

        Assert.Contains("report", exception.Message);
    }

    [Fact]
    public async Task HostedService_StopsTheStart_WhenTwoProvidersDeclareTheSameId()
    {
        _services.AddNightShiftRecurringJobs(static jobs => jobs.Add(SampleProvider.Id, () => Run(), Hourly));
        _services.AddNightShiftRecurringJobs<SampleProvider>();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(StartHostedServicesAsync);

        Assert.Contains(SampleProvider.Id, exception.Message);
    }

    [Fact]
    public async Task HostedService_StopsTheStart_WhenTheStorageRefusesAJob()
    {
        _manager.Setup(x => x.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Throws(new TimeoutException("The storage is unreachable."));
        _services.AddNightShiftRecurringJobs(static jobs => jobs.Add("report", () => Run(), Hourly));

        await Assert.ThrowsAsync<TimeoutException>(StartHostedServicesAsync);
    }

    [Fact]
    public async Task HostedService_ReadsAPlaceholderSchedule_FromTheConfiguration()
    {
        _services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Billing:Cron"] = Hourly }).Build());
        _services.AddNightShiftRecurringJobs(static jobs => jobs.Add("report", () => Run(), "%Billing:Cron%"));

        await StartHostedServicesAsync();

        _manager.Verify(x => x.AddOrUpdate("report", It.IsAny<Job>(), Hourly, It.IsAny<RecurringJobOptions>()));
    }

    [Fact]
    public async Task HostedService_StopsTheStart_WhenAPlaceholderHasNoValue()
    {
        _services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        _services.AddNightShiftRecurringJobs(static jobs => jobs.Add("report", () => Run(), "%Billing:Cron%"));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(StartHostedServicesAsync);

        Assert.Contains("Billing:Cron", exception.Message);
    }

    [Fact]
    public async Task HostedService_StopsTheStart_WhenAPlaceholderIsUsedWithoutAConfiguration()
    {
        _services.AddNightShiftRecurringJobs(static jobs => jobs.Add("report", () => Run(), "%Billing:Cron%"));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(StartHostedServicesAsync);

        Assert.Contains("report", exception.Message);
    }

    [Fact]
    public void HostedService_IsCreated_FromWhatAddNightShiftRegisters()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(_storage.Object);
        services.AddNightShift(static _ => { });
        services.AddNightShiftRecurringJobs(static jobs => jobs.Add("report", () => Run(), Hourly));

        IHostedService[] hostedServices = [.. services.BuildServiceProvider().GetServices<IHostedService>()];

        Assert.Single(hostedServices);
    }

    private async Task StartHostedServicesAsync()
    {
        foreach (IHostedService service in _services.BuildServiceProvider().GetServices<IHostedService>())
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Run()
    {
    }

    public sealed record SampleArgs(int Value);

    public sealed class SampleHandler : IJobHandler<SampleArgs>
    {
        public Task HandleAsync(SampleArgs args, PerformContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class SampleJob : IJob
    {
        public Task RunAsync(PerformContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class SampleProvider : IRecurringJobProvider
    {
        public const string Id = "from-provider";

        public void DeclareJobs(RecurringJobSet jobs)
        {
            jobs.Add(Id, () => Run(), Hourly);
        }
    }
}
