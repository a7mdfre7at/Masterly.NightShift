using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Masterly.NightShift.AspNetCore.Tests;

public class JobLogCaptureFacts
{
    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IStorageConnection> _connection = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly List<string> _console = [];
    private readonly BackgroundJobServerOptions _options = new() { WorkerCount = 1 };

    public JobLogCaptureFacts()
    {
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _transaction.Setup(x => x.InsertToList("job:1:console", It.IsAny<string>()))
            .Callback((string _, string value) => _console.Add(value));
    }

    [Fact]
    public void AddNightShiftJobLogs_WritesTheJobsLogLines_IntoItsConsole()
    {
        using ServiceProvider provider = BuildProvider(o =>
        {
            o.MaxLinesPerJob = 2;
            o.Redact = static line => line.Replace("s3cret", "***", StringComparison.Ordinal);
        });
        ILoggerFactory loggers = provider.GetRequiredService<ILoggerFactory>();
        ILogger orders = loggers.CreateLogger("MyApp.Orders");
        (IServerFilter capture, PerformContext context) = StartCapture(provider);

        orders.LogInformation("Charging with token {Token}", "s3cret");
        orders.LogDebug("Too detailed");
        loggers.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogInformation("select 1");
        orders.LogWarning("Second");
        orders.LogWarning("Third");
        capture.OnPerformed(new PerformedContext(context, null, false, null));
        orders.LogInformation("After the run");

        Assert.Collection(
            _console,
            x => Assert.Contains("[nightshift] Started", x, StringComparison.Ordinal),
            x => Assert.Contains("[Orders] Charging with token ***", x, StringComparison.Ordinal),
            x => Assert.Contains("[Orders] Second", x, StringComparison.Ordinal),
            x => Assert.Contains("[nightshift] Finished in", x, StringComparison.Ordinal),
            x => Assert.Contains("[nightshift] 1 more log lines were left out", x, StringComparison.Ordinal));
    }

    [Fact]
    public void AddNightShiftJobLogs_ReportsAFailure_AndCanLeaveOutLifecycleLines()
    {
        using ServiceProvider provider = BuildProvider(static o => o.LifecycleLines = false);
        (IServerFilter capture, PerformContext context) = StartCapture(provider);

        provider.GetRequiredService<ILoggerFactory>().CreateLogger("MyApp.Orders").LogError(new InvalidOperationException("boom"), "Could not charge");
        capture.OnPerformed(new PerformedContext(context, null, false, new InvalidOperationException("boom")));

        string line = Assert.Single(_console);
        Assert.Contains("[Orders] Could not charge (InvalidOperationException: boom)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_HasNoCaptureFilter_WithoutAddNightShiftJobLogs()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(_storage.Object)
            .AddSingleton(_options)
            .AddNightShift(_ => { })
            .AddNightShiftServer()
            .BuildServiceProvider();
        _ = provider.GetServices<IHostedService>().ToList();

        Assert.DoesNotContain(_options.FilterProvider!.GetFilters(Job.FromExpression(() => Work())), static x => x.Instance.GetType().Name == "CaptureFilter");
    }

    [Fact]
    public void Options_RefuseLimitsOutOfRange()
    {
        NightShiftJobLogOptions options = new();

        Assert.Throws<ArgumentOutOfRangeException>("value", () => options.MaxLinesPerJob = 0);
        Assert.Throws<ArgumentOutOfRangeException>("value", () => options.MaxMessageLength = 50);
        Assert.Contains("Masterly.NightShift", options.ExcludedCategories);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Work()
    {
    }

    private ServiceProvider BuildProvider(Action<NightShiftJobLogOptions> configure)
    {
        ServiceProvider provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(_storage.Object)
            .AddSingleton(_options)
            .AddNightShift(_ => { })
            .AddNightShiftJobLogs(configure)
            .AddNightShiftServer()
            .BuildServiceProvider();

        // Creating the hosted server adds the capture filter to the options' filter provider
        _ = provider.GetServices<IHostedService>().ToList();
        return provider;
    }

    private (IServerFilter Capture, PerformContext Context) StartCapture(ServiceProvider provider)
    {
        Job job = Job.FromExpression(() => Work());
        IServerFilter capture = _options.FilterProvider!.GetFilters(job)
            .Select(static x => x.Instance)
            .OfType<IServerFilter>()
            .Single(static x => x.GetType().Name == "CaptureFilter");

        PerformContext context = new(_storage.Object, _connection.Object, new BackgroundJob("1", job, DateTime.UtcNow), new Mock<IJobCancellationToken>().Object);
        capture.OnPerforming(new PerformingContext(context));
        return (capture, context);
    }
}
