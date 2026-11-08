# Testing

Code that uses Masterly.NightShift is tested at three levels. Unit tests check that your code creates the right jobs, without a storage. Job classes are plain classes, tested by calling their methods. Integration tests run real jobs against a real SQL Server database, LocalDB on a developer machine, to check the whole path: serialization, filters, retries and the job's own work.

The examples use xUnit and Moq; any test framework and mocking library works the same way.

## Quick example

The code under test creates a job with the `Enqueue` extension method:

```csharp
using Masterly.NightShift;

public class SignupService
{
    private readonly IBackgroundJobClient _jobs;

    public SignupService(IBackgroundJobClient jobs)
    {
        _jobs = jobs;
    }

    public void Register(int userId)
    {
        // ... save the user ...
        _jobs.Enqueue<EmailSender>(sender => sender.SendWelcome(userId));
    }
}

public class EmailSender
{
    public void SendWelcome(int userId)
    {
        // ... send the email ...
    }
}
```

The test mocks `IBackgroundJobClient` and checks the job it received:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Moq;
using Xunit;

public class SignupServiceTests
{
    [Fact]
    public void Register_EnqueuesTheWelcomeEmail()
    {
        var jobs = new Mock<IBackgroundJobClient>();
        var service = new SignupService(jobs.Object);

        service.Register(42);

        jobs.Verify(x => x.Create(
            It.Is<Job>(job => JobMatch.Is<EmailSender>(job, sender => sender.SendWelcome(42))),
            It.IsAny<EnqueuedState>()));
    }
}
```

`JobMatch` is a small helper you add to your test project; it is shown in the next section.

## How it works

### Creating jobs goes through one method

`Enqueue`, `Schedule`, `ContinueJobWith` and the other creation methods of `IBackgroundJobClient` are extension methods. Moq can't verify an extension method, but it doesn't need to: they all turn the lambda into a `Job` (type, method and arguments) and call the interface's only creation method, `Create(Job job, IState state)`. The state says which kind of job it is:

| Your code calls | The mock receives |
|---|---|
| `Enqueue(...)` | `Create(job, EnqueuedState)`; the queue is in `EnqueuedState.Queue` or `job.Queue` |
| `Schedule(..., delay)` or `Schedule(..., enqueueAt)` | `Create(job, ScheduledState)` with `EnqueueAt` |
| `ContinueJobWith(parentId, ...)` | `Create(job, AwaitingState)` with `ParentId` |
| `Requeue(jobId)`, `Delete(jobId)` | `ChangeState(jobId, EnqueuedState or DeletedState, expectedState)` |

So verify `Create` and look at the `Job` and the state. `Create` returns the new job's id; a loose mock returns `null`, which is what a real client returns when a filter cancels the creation. Set up a return value when your code uses the id:

```csharp
jobs.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("1");
```

### A matcher for jobs

Comparing a `Job` field by field in every test is noisy. This helper builds the expected job from a lambda, like your code does, and compares the type, the method and the arguments:

```csharp
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Masterly.NightShift.Common;

public static class JobMatch
{
    /// <summary>Whether the job calls the same method of T, with equal arguments, as the expression.</summary>
    public static bool Is<T>(Job job, Expression<Action<T>> expected)
    {
        return Matches(job, Job.FromExpression(expected));
    }

    /// <summary>The same, for async job methods.</summary>
    public static bool Is<T>(Job job, Expression<Func<T, Task>> expected)
    {
        return Matches(job, Job.FromExpression(expected));
    }

    private static bool Matches(Job actual, Job expected)
    {
        return actual.Type == expected.Type
               && actual.Method == expected.Method
               && actual.Args.SequenceEqual(expected.Args);
    }
}
```

Arguments are compared with `Equals`. Numbers, strings, dates, enums and records compare by value; a class compares by reference unless it overrides `Equals`. For such an argument, match the `Job` yourself and check its properties: `It.Is<Job>(job => ((Order)job.Args[0]).Id == 7)`.

A `CancellationToken` argument is stored as a placeholder and replaced by the server's token when the job runs, so the code that creates a job should pass `CancellationToken.None` (or `default`). Write `CancellationToken.None` in the expected lambda too: it matches when the code under test passed `None` or `default`. If the code passes another token (one of its own, for example the request's), the `Job` holds that token and the match fails; compare the other arguments yourself, or better, change the code to pass `CancellationToken.None`.

### Testing the job classes

A job class is a plain class: create it with fakes for its dependencies and call the method, as the server would.

```csharp
using System.Threading;
using System.Threading.Tasks;
using Xunit;

public interface IReportStore
{
    Task SaveAsync(string name, CancellationToken cancellationToken);
}

public class ReportJob
{
    private readonly IReportStore _store;

    public ReportJob(IReportStore store)
    {
        _store = store;
    }

    public Task BuildAsync(string name, CancellationToken cancellationToken)
    {
        return _store.SaveAsync(name, cancellationToken);
    }
}

public class ReportJobTests
{
    [Fact]
    public async Task BuildAsync_SavesTheReport()
    {
        var store = new Moq.Mock<IReportStore>();

        await new ReportJob(store.Object).BuildAsync("daily", CancellationToken.None);

        store.Verify(x => x.SaveAsync("daily", CancellationToken.None));
    }
}
```

What a unit test of the class doesn't cover: that the arguments survive serialization (keep them small and serializable, see [Job arguments](job-arguments.md)), that the server can create the class (see [Dependency injection](../server/dependency-injection.md)), and what the filters do. The integration test below covers those.

## More examples

### Scheduled jobs and queues

```csharp
[Fact]
public void Remind_SchedulesTheReminder_InOneDay()
{
    var jobs = new Mock<IBackgroundJobClient>();

    jobs.Object.Schedule<EmailSender>(sender => sender.SendWelcome(42), TimeSpan.FromDays(1));

    jobs.Verify(x => x.Create(
        It.Is<Job>(job => JobMatch.Is<EmailSender>(job, sender => sender.SendWelcome(42))),
        It.Is<ScheduledState>(state => state.EnqueueAt > DateTime.UtcNow.AddHours(23))));
}

[Fact]
public void Urgent_GoesToTheCriticalQueue()
{
    var jobs = new Mock<IBackgroundJobClient>();

    jobs.Object.Enqueue<EmailSender>("critical", sender => sender.SendWelcome(42));

    jobs.Verify(x => x.Create(It.Is<Job>(job => job.Queue == "critical"), It.IsAny<EnqueuedState>()));
}
```

### Recurring jobs

`IRecurringJobManager.AddOrUpdate` overloads are extension methods too. They all call `AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options)`:

```csharp
public class ReportSchedule
{
    private readonly IRecurringJobManager _recurringJobs;

    public ReportSchedule(IRecurringJobManager recurringJobs)
    {
        _recurringJobs = recurringJobs;
    }

    public void Register()
    {
        _recurringJobs.AddOrUpdate<ReportJob>("daily-report", job => job.BuildAsync("daily", CancellationToken.None), Cron.Daily(6));
    }
}

public class ReportScheduleTests
{
    [Fact]
    public void Register_AddsTheDailyReport_AtSix()
    {
        var recurringJobs = new Mock<IRecurringJobManager>();

        new ReportSchedule(recurringJobs.Object).Register();

        recurringJobs.Verify(x => x.AddOrUpdate(
            "daily-report",
            It.Is<Job>(job => JobMatch.Is<ReportJob>(job, j => j.BuildAsync("daily", CancellationToken.None))),
            "0 6 * * *",
            It.IsAny<RecurringJobOptions>()));
    }
}
```

### Filters

A filter's methods take context objects with public constructors. Build them with a mocked storage and call the method:

```csharp
using Masterly.NightShift.Client;
using Masterly.NightShift.Storage;

public sealed class TenantAttribute : JobFilterAttribute, IClientFilter
{
    public void OnCreating(CreatingContext context)
    {
        context.SetJobParameter("TenantId", "contoso");
    }

    public void OnCreated(CreatedContext context)
    {
    }
}

public class TenantAttributeTests
{
    [Fact]
    public void OnCreating_StoresTheTenant()
    {
        var job = Job.FromExpression<EmailSender>(sender => sender.SendWelcome(42));
        var context = new CreatingContext(new CreateContext(
            new Mock<JobStorage>().Object,
            new Mock<IStorageConnection>().Object,
            job,
            new EnqueuedState()));

        new TenantAttribute().OnCreating(context);

        Assert.Equal("contoso", context.GetJobParameter<string>("TenantId"));
    }
}
```

Server filters work the same with `new PerformingContext(new PerformContext(storage, connection, new BackgroundJob("1", job, DateTime.UtcNow), new JobCancellationToken(false)))`; the token can't be `null`. To see a filter work with the real state machine, retries included, use an integration test.

### An integration test against LocalDB

There is no in-memory storage: run integration tests against SQL Server. LocalDB comes with Visual Studio; on a build agent, use SQL Server Express or a SQL Server container. Create the database once (the storage creates its tables, but not the database), and empty it before each test with `Purge`:

```csharp
using System.Diagnostics;
using Masterly.NightShift.SqlServer;
using Masterly.NightShift.Storage;

public class EmailSenderIntegrationTests
{
    // CREATE DATABASE MyApp_JobTests; once, or let your test setup do it
    private const string ConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=MyApp_JobTests;Integrated Security=True;TrustServerCertificate=True;";

    private readonly SqlServerStorage _storage;

    public EmailSenderIntegrationTests()
    {
        _storage = new SqlServerStorage(ConnectionString, new SqlServerStorageOptions
        {
            QueuePollInterval = TimeSpan.FromMilliseconds(200),
        });

        _storage.Purge(new StoragePurgeOptions { KeepRecurringJobs = false, KeepServers = false });
    }

    [Fact]
    public async Task AnEnqueuedJob_Succeeds()
    {
        var client = new BackgroundJobClient(_storage);
        var jobId = client.Enqueue<EmailSender>(sender => sender.SendWelcome(42));

        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1 }, _storage))
        {
            Assert.Equal(SucceededState.StateName, await WaitForFinalStateAsync(jobId!, TimeSpan.FromSeconds(30)));
        }
    }

    private async Task<string?> WaitForFinalStateAsync(string jobId, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        string? state = null;

        while (stopwatch.Elapsed < timeout)
        {
            using (var connection = _storage.GetConnection())
            {
                state = connection.GetJobData(jobId)?.State;
            }

            if (state == SucceededState.StateName || state == FailedState.StateName || state == DeletedState.StateName) return state;
            await Task.Delay(100);
        }

        return state;
    }
}
```

The server creates `EmailSender` with its `JobActivator`; by default that needs a public parameterless constructor. To give jobs their dependencies, set `BackgroundJobServerOptions.Activator`, or host the server with `AddNightShiftServer` in a test host (see [Dependency injection](../server/dependency-injection.md)). A job that fails is retried with growing delays by `AutomaticRetry`; put `[AutomaticRetry(Attempts = 0)]` on a job class you test for failures, so it reaches the Failed state at once.

Run integration tests one at a time against one database, or give each test class its own database: `Purge` empties the whole storage, and a server takes any job in the queues it listens to.

## Things to know

- **Mock `IBackgroundJobClient`, not the static `BackgroundJob` class.** The static class uses `JobStorage.Current` and can't be replaced in a test. Inject the interface; `AddNightShift` registers it.
- **The state passed to `Create` is a new object.** Match it by type (`It.IsAny<EnqueuedState>()`) or by its properties, not by reference.
- **Arguments are evaluated when the job is created.** `Job.FromExpression` runs the argument expressions, so `JobMatch` compares values, not the code that computed them.
- **Global configuration is static.** Tests that change `GlobalConfiguration`, `GlobalJobFilters` or `JobStorage.Current` affect each other; restore what you change, and don't run such tests in parallel.

## See also

- [Fire-and-forget jobs](fire-and-forget.md)
- [Job arguments](job-arguments.md)
- [Custom filters](../filters/custom-filters.md)
- [Dependency injection](../server/dependency-injection.md)
- [SQL Server storage](../storage/sql-server.md)
