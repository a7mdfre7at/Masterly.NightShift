using Masterly.NightShift.Storage;

namespace Masterly.NightShift.AspNetCore.Tests.DependencyInjection;

public class NightShiftScheduleDateProviderServiceCollectionExtensionsFacts
{
    private static readonly DateTime Holiday = new(2026, 12, 25, 10, 0, 0, DateTimeKind.Utc);

    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IStorageConnection> _connection = new();

    public NightShiftScheduleDateProviderServiceCollectionExtensionsFacts()
    {
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
    }

    [Fact]
    public void AddNightShiftScheduleDateProvider_RegistersTheProvider_AsASingleton()
    {
        IServiceCollection services = new ServiceCollection().AddNightShiftScheduleDateProvider<Holidays>("holidays");

        ServiceDescriptor descriptor = Assert.Single(services, x => x.ServiceType == typeof(Holidays));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddNightShiftScheduleDateProvider_ThrowsAnException_ForAnInvalidName_OrNullServices()
    {
        Assert.Throws<ArgumentException>("name", () => new ServiceCollection().AddNightShiftScheduleDateProvider<Holidays>("not valid"));
        Assert.Throws<ArgumentNullException>("name", () => new ServiceCollection().AddNightShiftScheduleDateProvider<Holidays>(null));
        Assert.Throws<ArgumentNullException>("services", () => NightShiftScheduleDateProviderServiceCollectionExtensions.AddNightShiftScheduleDateProvider<Holidays>(null, "holidays"));
    }

    [Fact]
    public void Configuration_RegistersTheProvider_FromTheContainer()
    {
        // A name no other test uses, as the providers are global
        _connection.Setup(x => x.GetAllEntriesFromHash("schedule-policy:closing-days"))
            .Returns(new Dictionary<string, string> { ["DateProviders"] = "di-holidays" });

        SchedulePolicyDecision before = _storage.Object.EvaluateSchedulePolicy("closing-days", Holiday);

        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton(_storage.Object)
            .AddNightShiftScheduleDateProvider<Holidays>("DI-Holidays")
            .AddNightShift(_ => { })
            .BuildServiceProvider();
        provider.GetRequiredService<IGlobalConfiguration>();

        SchedulePolicyDecision after = _storage.Object.EvaluateSchedulePolicy("closing-days", Holiday);

        Assert.Equal("policy 'closing-days': date provider 'di-holidays' isn't registered on this server", before.Reason);
        Assert.Equal("policy 'closing-days': 2026-12-25 is excluded by date provider 'di-holidays'", after.Reason);
        Assert.True(_storage.Object.EvaluateSchedulePolicy("closing-days", Holiday.AddDays(1)).Allowed);
        Assert.Same(provider.GetRequiredService<Holidays>(), provider.GetRequiredService<Holidays>());
    }

    public sealed class Holidays : IScheduleDateProvider
    {
        public bool Contains(DateOnly day) => day is { Month: 12, Day: 25 };
    }
}
