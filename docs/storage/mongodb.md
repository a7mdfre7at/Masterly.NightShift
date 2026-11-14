# MongoDB storage

`Masterly.NightShift.MongoDB` keeps jobs in MongoDB 5.0 or later through the official driver. It needs a replica set or a sharded cluster, because its transactions span several documents; a single server can run as a one-member replica set.

## Quick example

```
dotnet add package Masterly.NightShift.MongoDB
```

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseMongoStorage(builder.Configuration.GetConnectionString("Mongo")!, "myapp"));
builder.Services.AddNightShiftServer();

var app = builder.Build();
app.MapNightShiftDashboard();
app.Run();
```

To share the application's client, pass a database of it:

```csharp
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient("mongodb://localhost:27017/?replicaSet=rs0"));
builder.Services.AddNightShift((provider, config) => config
    .UseMongoStorage(provider.GetRequiredService<IMongoClient>().GetDatabase("myapp")));
```

For development, start a one-member replica set:

```
docker run -d -p 27017:27017 mongo:7 --replSet rs0 --bind_ip_all
docker exec <container> mongosh --eval "rs.initiate()"
```

## How it works

The constructor connects to the server at once: it reads the server's version and topology with `hello`, and refuses a standalone server, or one older than MongoDB 5.0 (wire version 13), with `NotSupportedException`. Then it creates its collections' indexes; creating an index that exists does nothing. Create the storage where a network call is acceptable, not on a hot path.

| Collection (with `CollectionPrefix`) | What it holds |
|---|---|
| `jobs` | One document per job: payload, arguments, parameters, current state and history. Ids are numbers from `sequences`. |
| `jobQueue` | Queue entries, with `FetchedAt` and `Token` while a worker holds them |
| `sets`, `lists`, `hashes` | One document per item, with `Key` |
| `counters`, `aggregatedCounters` | Counter increments, and their merged values |
| `servers`, `locks`, `sequences` | Server heartbeats, lock leases, the job id counter |

A transaction runs in a MongoDB transaction, which the driver retries on transient errors. It reads from one snapshot (read concern `snapshot`) and commits with write concern `majority`. Everything else (creating a job, fetching, removing or requeuing a fetched job, taking and renewing locks, heartbeats, job parameters) writes with write concern `majority` too, and every read goes to the primary with read concern `majority`, whatever the client's or database's defaults: a job whose id was returned, a claimed queue entry or a taken lock survives a failover, and nothing is decided on a write that a failover could roll back. State changes only insert counter increments, which a storage-wide process merges every `CountersAggregateInterval`, so busy servers never conflict on a counter. Everything that expires has an `ExpireAt` field with a TTL index, and MongoDB deletes it (its TTL monitor runs about every minute). Fetching claims the oldest visible entry with an atomic `findAndModify`, and only when the queue has none, the oldest entry whose worker stopped keeping it alive for longer than `InvisibilityTimeout`. A claim isn't canceled once sent (a server stopping waits for it), so it never hides a job that no worker got; the driver's retryable writes apply a claim retried after a network error once. Fetched jobs, lock leases and server heartbeats are stamped and timed out by the server's clock (`$$NOW`), so processes whose clocks differ agree on when a worker, a lock holder or a server is gone. `GetUtcDateTime` returns the server's time too, and the dashboard and the [health checks](../monitoring/health-checks.md#which-clock) compare heartbeats with it.

## Options / API

| `MongoStorageOptions` | Default | What it does |
|---|---|---|
| `CollectionPrefix` | `nightshift.` | The prefix of the collection names, to keep applications apart in one database. |
| `InvisibilityTimeout` | 30 minutes | How long a fetched job stays invisible after its worker stopped keeping it alive. |
| `QueuePollInterval` | 1 second | How long a waiting worker sleeps. Jobs enqueued through the same storage object wake the workers of their queue at once; other processes' jobs are seen at the next poll. |
| `CountersAggregateInterval` | 5 minutes | How often counter increments are merged. |
| `DistributedLockLifetime` | 30 seconds | How long a lock outlives a process that died holding it. |

| API | What it does |
|---|---|
| `UseMongoStorage(connectionString, databaseName, options?)` | A storage on a new client. |
| `UseMongoStorage(IMongoDatabase, options?)` | A storage on a database of the application's client. |
| `MongoStorage.Options` | A copy of the options the storage was created with. The options are copied and checked when the storage is created, so changing them afterwards changes nothing. |

## Things to know

- **Replica set required**, even for development (see above).
- **Write and read concern.** The storage uses the database you give it with settings of its own: transactions commit with `majority` and read a snapshot, every other write uses `majority`, and every other read goes to the primary with read concern `majority`, whatever the client's or the database's settings. A database set to `w: 1`, unacknowledged writes or secondary reads changes nothing for the storage.
- **The client.** A storage created from a connection string makes a `MongoClient`. The driver shares the connections of clients with the same settings and keeps them for the life of the process, so there is nothing for the storage to dispose; pass a database of your own client to control its lifetime.
- **Locks and connections.** Disposing a storage connection releases the locks taken through it whose handles weren't disposed, as SQL Server does when a connection closes.
- **List and queue order across processes** follows MongoDB object ids, which order by second and then by process: two processes writing in the same second may interleave.
- **Times** are stored with millisecond precision.

## See also

- [Storage](README.md)
- [Custom storage](custom-storage.md)
- [Server options](../server/server-options.md)
