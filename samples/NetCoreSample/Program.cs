// A generic host (no web server) that runs a Masterly.NightShift server with custom factory, performer and state
// changer decorators, and adds recurring jobs from a hosted service. It uses a SQL Server database, created on first
// run if it doesn't exist (with its tables): by default the LocalDB database named below. To use another one, set
// ConnectionStrings:NightShift, for example
//
//   dotnet run --project samples/NetCoreSample -- --ConnectionStrings:NightShift="Server=.\;Database=Masterly.NightShift.Sample;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;"
//
// or the ConnectionStrings__NightShift environment variable. Press Ctrl+C to stop it.

using Masterly.NightShift;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.SqlServer;
using Masterly.NightShift.States;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightShiftSamples;

const string DefaultConnectionString =
    @"Server=(localdb)\MSSQLLocalDB;Database=Masterly.NightShift.Sample;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;";

IHost host = new HostBuilder()
    .ConfigureAppConfiguration(x => x.AddEnvironmentVariables().AddCommandLine(args))
    .ConfigureLogging(x => x.AddConsole().SetMinimumLevel(LogLevel.Information))
    .ConfigureServices((hostContext, services) =>
    {
        services.Configure<HostOptions>(option =>
        {
            option.ShutdownTimeout = TimeSpan.FromSeconds(60);
        });

        services.TryAddSingleton<SqlServerStorageOptions>(new SqlServerStorageOptions
        {
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            QueuePollInterval = TimeSpan.FromTicks(1),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(1)
        });

        services.TryAddSingleton<IBackgroundJobFactory>(x => new CustomBackgroundJobFactory(
            new BackgroundJobFactory(x.GetRequiredService<IJobFilterProvider>())));

        services.TryAddSingleton<IBackgroundJobPerformer>(x => new CustomBackgroundJobPerformer(
            new BackgroundJobPerformer(
                x.GetRequiredService<IJobFilterProvider>(),
                x.GetRequiredService<JobActivator>(),
                TaskScheduler.Default)));

        services.TryAddSingleton<IBackgroundJobStateChanger>(x => new CustomBackgroundJobStateChanger(
                new BackgroundJobStateChanger(x.GetRequiredService<IJobFilterProvider>())));

        string connectionString = hostContext.Configuration.GetConnectionString("NightShift") ?? DefaultConnectionString;

        // The storage creates its tables but not the database; the sample creates it on a fresh machine
        SampleDatabase.EnsureExists(connectionString);

        services.AddNightShift((provider, configuration) => configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseTelemetry()
            .UseSqlServerStorage(connectionString, provider.GetRequiredService<SqlServerStorageOptions>()));

        services.AddHostedService<RecurringJobsService>();
        services.AddNightShiftServer(options =>
        {
            options.StopTimeout = TimeSpan.FromSeconds(15);
            options.ShutdownTimeout = TimeSpan.FromSeconds(30);
        });
    })
    .Build();

await host.RunAsync();

internal sealed class CustomBackgroundJobFactory([NotNull] IBackgroundJobFactory inner) : IBackgroundJobFactory
{
    private readonly IBackgroundJobFactory _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public IStateMachine StateMachine => _inner.StateMachine;

    public BackgroundJob? Create(CreateContext context)
    {
        Console.WriteLine($"Create: {context.Job.Type.FullName}.{context.Job.Method.Name} in {context.InitialState?.Name} state");
        return _inner.Create(context);
    }
}

internal sealed class CustomBackgroundJobPerformer([NotNull] IBackgroundJobPerformer inner) : IBackgroundJobPerformer
{
    private readonly IBackgroundJobPerformer _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public object? Perform(PerformContext context)
    {
        Console.WriteLine($"Perform {context.BackgroundJob.Id} ({context.BackgroundJob.Job!.Type.FullName}.{context.BackgroundJob.Job.Method.Name})");
        return _inner.Perform(context);
    }
}

internal sealed class CustomBackgroundJobStateChanger([NotNull] IBackgroundJobStateChanger inner) : IBackgroundJobStateChanger
{
    private readonly IBackgroundJobStateChanger _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public IState? ChangeState(StateChangeContext context)
    {
        Console.WriteLine($"ChangeState {context.BackgroundJobId} to {context.NewState}");
        return _inner.ChangeState(context);
    }
}

internal sealed class RecurringJobsService(
    [NotNull] IRecurringJobManager recurringJobs,
    [NotNull] ILogger<RecurringJobScheduler> logger) : BackgroundService
{
    private readonly IRecurringJobManager _recurringJobs = recurringJobs ?? throw new ArgumentNullException(nameof(recurringJobs));
    private readonly ILogger<RecurringJobScheduler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _recurringJobs.AddOrUpdate("seconds", () => Console.WriteLine("Hello, seconds!"), "*/15 * * * * *");
            _recurringJobs.AddOrUpdate("minutely", () => Console.WriteLine("Hello, world!"), Cron.Minutely);
            _recurringJobs.AddOrUpdate("hourly", () => Console.WriteLine("Hello"), "25 15 * * *");
            _recurringJobs.AddOrUpdate("neverfires", () => Console.WriteLine("Can only be triggered"), "0 0 31 2 *");

            _recurringJobs.AddOrUpdate("Hawaiian", () => Console.WriteLine("Hawaiian"), "15 08 * * *", new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Hawaiian Standard Time")
            });
            _recurringJobs.AddOrUpdate("UTC", () => Console.WriteLine("UTC"), "15 18 * * *");
            _recurringJobs.AddOrUpdate("Russian", () => Console.WriteLine("Russian"), "15 21 * * *", new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Local
            });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "An exception occurred while creating recurring jobs.");
        }

        return Task.CompletedTask;
    }
}
