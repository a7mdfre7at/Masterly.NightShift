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

A transaction runs in a MongoDB transaction, which the driver retries on transient errors. It reads from one snapshot (read concern `snapshot`) and commits with write concern `majority`, whatever the client's or database's defaults, so a committed write survives a failover. State changes only insert counter increments, which a storage-wide process merges every `CountersAggregateInterval`, so busy servers never conflict on a counter. Everything that expires has an `ExpireAt` field with a TTL index, and MongoDB deletes it (its TTL monitor runs about every minute). Fetching claims the oldest visible entry with an atomic `findAndModify`, and only when the queue has none, the oldest entry whose worker stopped keeping it alive for longer than `InvisibilityTimeout`. Fetched jobs and lock leases time out by the server's clock (`$$NOW`), so processes whose clocks differ agree on when a worker or a lock holder is gone.

## Options / API

| `MongoStorageOptions` | Default | What it does |
|---|---|---|
| `CollectionPrefix` | `nightshift.` | The prefix of the collection names, to keep applications apart in one database. |
| `InvisibilityTimeout` | 30 minutes | How long a fetched job stays invisible after its worker stopped keeping it alive. |
| `QueuePollInterval` | 1 second | How long a waiting worker sleeps. Jobs enqueued through the same storage object wake workers at once; other processes' jobs are seen at the next poll. |
| `CountersAggregateInterval` | 5 minutes | How often counter increments are merged. |
| `DistributedLockLifetime` | 30 seconds | How long a lock outlives a process that died holding it. |

| API | What it does |
|---|---|
| `UseMongoStorage(connectionString, databaseName, options?)` | A storage on a new client. |
| `UseMongoStorage(IMongoDatabase, options?)` | A storage on a database of the application's client. |

## Things to know

- **Replica set required**, even for development (see above).
- **Write concern.** Transactions always commit with `majority` and read a snapshot, whatever the client's settings.
- **List and queue order across processes** follows MongoDB object ids, which order by second and then by process: two processes writing in the same second may interleave.
- **Times** are stored with millisecond precision.

## See also

- [Storage](README.md)
- [Custom storage](custom-storage.md)
- [Server options](../server/server-options.md)
