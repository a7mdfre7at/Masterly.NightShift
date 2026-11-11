using Masterly.NightShift.Common;

namespace Masterly.NightShift.AspNetCore.Tests.DependencyInjection;

public class NightShiftFunctionServiceCollectionExtensionsFacts
{
    [Fact]
    public async Task AddNightShiftFunction_RegistersADelegate_ThatRunsWithTheJobsServicesAndPayload()
    {
        Recorder recorder = new();
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton(recorder)
            .AddNightShiftFunction<Order>("di-tests.ship", static (services, order, _) =>
            {
                services.GetRequiredService<Recorder>().Lines.Add($"ship {order.Id} to {order.City}");
                return Task.CompletedTask;
            })
            .BuildServiceProvider();

        Job job = JobFunctions.Find("di-tests.ship")!.CreateJob(new { Id = 7, City = "Oslo" }, null);

        Assert.Equal(typeof(DelegateJobFunctionRunner), job.Type);
        await new DelegateJobFunctionRunner(provider).RunAsync((string)job.Args[0], (string)job.Args[1], CancellationToken.None);

        Assert.Equal(["ship 7 to Oslo"], recorder.Lines);
        Assert.Equal(typeof(Order), JobFunctions.Find("di-tests.ship")!.PayloadType);
    }

    [Fact]
    public async Task AddNightShiftFunction_WithoutPayload_RunsTheDelegate_AndRefusesAPayload()
    {
        int runs = 0;
        new ServiceCollection().AddNightShiftFunction("di-tests.tick", (_, _) =>
        {
            runs++;
            return Task.CompletedTask;
        });

        Job job = JobFunctions.Find("di-tests.tick")!.CreateJob(null, "critical");
        await new DelegateJobFunctionRunner(new ServiceCollection().BuildServiceProvider()).RunAsync("di-tests.tick", null, CancellationToken.None);

        Assert.Equal(1, runs);
        Assert.Equal("critical", job.Queue);
        Assert.Throws<ArgumentException>("payload", () => JobFunctions.Find("di-tests.tick")!.CreateJob(5, null));
    }

    [Fact]
    public void AddNightShiftFunction_ChecksThePayload_WhenTheJobIsCreated()
    {
        new ServiceCollection().AddNightShiftFunction<Order>("di-tests.checked", static (_, _, _) => Task.CompletedTask);

        Assert.Throws<ArgumentException>("payload", () => JobFunctions.Find("di-tests.checked")!.CreateJob("not an order", null));
    }

    [Fact]
    public async Task Runner_ThrowsAnException_ForANameWithoutADelegate()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DelegateJobFunctionRunner(new ServiceCollection().BuildServiceProvider()).RunAsync("di-tests.missing", null, CancellationToken.None));
    }

    [Fact]
    public void AddNightShiftFunction_ThrowsAnException_ForInvalidArguments()
    {
        Assert.Throws<ArgumentNullException>("handler", () => new ServiceCollection().AddNightShiftFunction("x", null));
        Assert.Throws<ArgumentException>("name", () => new ServiceCollection().AddNightShiftFunction("bad name", static (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>("services", () => NightShiftFunctionServiceCollectionExtensions.AddNightShiftFunction(null, "x", static (_, _) => Task.CompletedTask));
    }

    [Fact]
    public void AddNightShiftFunctions_RegistersTheMarkedMethods_AndTheirClassesAsScoped()
    {
        IServiceCollection services = new ServiceCollection().AddNightShiftFunctions(typeof(NightShiftFunctionServiceCollectionExtensionsFacts).Assembly);

        Assert.NotNull(JobFunctions.Find("di-tests.reindex"));
        ServiceDescriptor descriptor = Assert.Single(services, static x => x.ServiceType == typeof(SearchIndex));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    public sealed record Order(int Id, string City);

    public sealed class Recorder
    {
        public List<string> Lines { get; } = [];
    }

    public sealed class SearchIndex
    {
        [JobFunction("di-tests.reindex")]
        public void Reindex()
        {
        }
    }
}
