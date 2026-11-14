#if NET10_0_OR_GREATER
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TickerQ.DependencyInjection;
using TickerQ.EntityFrameworkCore.DbContextFactory;
using TickerQ.EntityFrameworkCore.DependencyInjection;
using TickerQ.Utilities;
using TickerQ.Utilities.Base;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Enums;
using TickerQ.Utilities.Interfaces.Managers;
using TickerQ.Utilities.Models;

namespace NightShiftBenchmarks;

/// <summary>
/// The end-to-end run through TickerQ 10.4.0 (the last release under MIT OR Apache-2.0) with its EF Core store on
/// SQL Server. A fire-and-forget job is a time ticker that is due now.
/// <para>
/// TickerQ runs a due job in the process that added it when that process hosts the scheduler. So jobs added
/// before the server starts go through a host without background services, as a client-only app would add
/// them, and the server then finds them overdue. Jobs added while the server runs take TickerQ's usual path.
/// </para>
/// </summary>
internal sealed class TickerQSide(int workerCount, bool bulk = false, bool live = false) : ILibrary
{
    public const string DatabaseName = "NightShiftBench_TickerQ";

    private const int BatchSize = 250;
    private IHost? _client;
    private ITimeTickerManager<TimeTickerEntity>? _manager;

    public string Name => bulk ? "TickerQ 10.4.0 AddBatchAsync" : "TickerQ 10.4.0";

    public string Driver => SqlClientFactory.Instance.GetType().FullName ?? "default";

    public void OpenDatabase()
    {
        Database.EnsureCreated(DatabaseName);
        DbContextOptions<TickerQDbContext> options = new DbContextOptionsBuilder<TickerQDbContext>()
            .UseSqlServer(Database.ConnectionString(DatabaseName))
            .Options;
        using (TickerQDbContext context = new(options))
        {
            // The database exists but is empty the first time, and EnsureCreated only creates a missing database
            if (!TableExists())
            {
                context.Database.ExecuteSqlRaw(context.Database.GenerateCreateScript().Replace("\nGO", "\n", StringComparison.Ordinal));
            }
        }

        _client = BuildHost(server: false);
        TickerFunctionProvider.Build();
        _manager = _client.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
    }

    public void Reset() => Execute("DELETE FROM [ticker].[TimeTickers]");

    public void EnqueueAllCounted(int count)
    {
        if (!bulk)
        {
            for (int i = 0; i < count; i++)
            {
                EnqueueCounted();
            }

            return;
        }

        for (int done = 0; done < count; done += BatchSize)
        {
            List<TimeTickerEntity> batch = [];
            for (int i = 0; i < Math.Min(BatchSize, count - done); i++)
            {
                batch.Add(NewTicker());
            }

            Check(_manager!.AddBatchAsync(batch).GetAwaiter().GetResult()); // Set by OpenDatabase or StartServer
        }
    }

    public void EnqueueCountedInParallel(int count, int threads) => Callers.Run(count, threads, EnqueueCounted);

    public IDisposable StartServer(int workers)
    {
        IHost server = BuildHost(server: true);
        server.UseTickerQ();
        server.StartAsync().GetAwaiter().GetResult();
        ITimeTickerManager<TimeTickerEntity> client = _manager!; // Set by OpenDatabase
        _manager = server.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        return new Stop(server, () => _manager = client);
    }

    // Idle and Queued are the states of a job that has not started; InProgress is one that is running
    public bool HasFinished(int jobCount) => RemainingCount() == 0;

    private static long RemainingCount()
    {
        using SqlConnection connection = new(Database.ConnectionString(DatabaseName));
        using SqlCommand command = new("SELECT COUNT(*) FROM [ticker].[TimeTickers] WHERE [Status] IN (0, 1, 2)", connection);
        connection.Open();
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static TimeTickerEntity NewTicker() => new() { Function = CountedTickerJob.FunctionName, ExecutionTime = DateTime.UtcNow };

    private static void Check<T>(TickerResult<T> result) where T : class
    {
        if (!result.IsSucceeded)
        {
            throw new InvalidOperationException("TickerQ did not add the job.", result.Exception);
        }
    }

    private static bool TableExists()
    {
        using SqlConnection connection = new(Database.ConnectionString(DatabaseName));
        using SqlCommand command = new("SELECT OBJECT_ID(N'[ticker].[TimeTickers]')", connection);
        connection.Open();
        return command.ExecuteScalar() is not (null or DBNull);
    }

    private static void Execute(string sql)
    {
        using SqlConnection connection = new(Database.ConnectionString(DatabaseName));
        using SqlCommand command = new(sql, connection);
        connection.Open();
        command.ExecuteNonQuery();
    }

    private void EnqueueCounted() => Check(_manager!.AddAsync(NewTicker()).GetAwaiter().GetResult()); // Set by OpenDatabase or StartServer

    private IHost BuildHost(bool server)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.ClearProviders();
        builder.Services.AddTickerQ(options =>
        {
            options.ConfigureScheduler(scheduler =>
            {
                scheduler.MaxConcurrency = workerCount;
                // Jobs another process added are found by a sweep that runs every 30 seconds by default. That
                // wait is latency, not throughput, so the sweep runs every 100 ms when the jobs are added before
                // the server starts. With the server running, jobs don't need the sweep and it keeps its default.
                if (!live)
                {
                    scheduler.FallbackIntervalChecker = TimeSpan.FromMilliseconds(100);
                }
            });
            options.AddOperationalStore(store => store.UseTickerQDbContext<TickerQDbContext>(
                db => db.UseSqlServer(Database.ConnectionString(DatabaseName))));
            if (!server)
            {
                options.DisableBackgroundServices();
            }
        });
        return builder.Build();
    }

    private sealed class Stop(IHost server, Action stopped) : IDisposable
    {
        public void Dispose()
        {
            stopped();
            server.StopAsync().GetAwaiter().GetResult();
            server.Dispose();
        }
    }
}

/// <summary>The same empty job for TickerQ.</summary>
public sealed class CountedTickerJob
{
    public const string FunctionName = "Counted";

    [TickerFunction(FunctionName)]
    public Task Counted(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        Completed.Add();
        return Task.CompletedTask;
    }
}
#endif
