# In-memory storage

`Masterly.NightShift.InMemory` keeps every job, queue and recurring job in the process's memory. Use it for development, tests and demos: it needs no database, starts instantly and behaves like the SQL Server storage. Nothing survives the process, and only servers in the same process share its jobs.

## Quick example

```
dotnet add package Masterly.NightShift.InMemory
```

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config.UseInMemoryStorage());
builder.Services.AddNightShiftServer();

var app = builder.Build();
app.MapNightShiftDashboard();
app.Run();
```

In a test, create a storage per test so tests don't share jobs:

```csharp
var storage = new InMemoryStorage();
var client = new BackgroundJobClient(storage);
using var server = new BackgroundJobServer(new BackgroundJobServerOptions(), storage);

string? id = client.Enqueue(() => Console.WriteLine("Hello"));
```

## How it works

Everything is behind one lock. A transaction is applied as a whole under the lock, and reads return copies, so a reader never sees half a transaction and every read sees every commit (`LinearizableReads` is `true`). Enqueuing a job wakes the workers waiting on its queue at once. Disposing a storage connection releases the locks taken through it whose handles weren't disposed, as SQL Server does when a connection closes. Expired jobs and keys are removed every `ExpirationCheckInterval`, when the storage is next used.

The storage supports every optional feature: the extended API, transactional acknowledgement, job parameters and locks in transactions, awaiting jobs on the dashboard and purging.

## Options / API

| `InMemoryStorageOptions` | Default | What it does |
|---|---|---|
| `QueuePollInterval` | 1 second | How long a waiting worker sleeps before it looks at its queues again. Enqueuing wakes workers at once, so this rarely matters. |
| `InvisibilityTimeout` | 30 minutes | How long a fetched job stays invisible after its worker stopped keeping it alive. A worker keeps its job alive while it runs. |
| `ExpirationCheckInterval` | 1 minute | How often expired jobs and keys are removed. |
| `MaxExpirationTime` | none | The longest anything is kept after it is set to expire, to bound memory in a busy process. `TimeSpan.FromHours(1)` keeps finished jobs for an hour at most. Must be positive when set. |

`UseInMemoryStorage()` and `UseInMemoryStorage(options)` set the storage as the current one. `new InMemoryStorage(options)` creates one for direct use. The options are copied and checked when the storage is created: an interval that isn't positive throws `ArgumentException`, and changing the options afterwards changes nothing. `InMemoryStorage.Options` returns a copy of them.

## Things to know

- **Not for production.** Jobs are lost when the process stops, and servers in other processes can't see them.
- **Memory grows with jobs.** Finished jobs are kept for `JobStorage.JobExpirationTimeout` (one day by default); set `MaxExpirationTime` when a process creates many jobs.
- **Ids restart at 1** in every process.
- **Two clocks.** The times that are stored and shown, and that decide when something expires, comes due or stopped sending heartbeats, come from the library's clock, which `UseTimeProvider` can replace. Whether a lock's lease or a fetched job's invisibility has lapsed is judged by the machine's monotonic clock, at the pace of real time. So a test that moves its clock forward expires jobs, makes scheduled jobs due and times out servers, but never hands a held lock or a running job to a second owner, and a clock that stands still keeps neither for good.

## See also

- [Storage](README.md)
- [Testing](../jobs/testing.md)
- [Custom storage](custom-storage.md)
