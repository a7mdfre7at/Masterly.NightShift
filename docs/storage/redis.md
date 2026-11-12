# Redis storage

`Masterly.NightShift.Redis` keeps jobs in Redis 5.0 or later (standalone, Sentinel or cluster) through StackExchange.Redis. Every transaction runs as one Lua script, so it is atomic and takes one round trip, and workers are woken through pub/sub as soon as a job is enqueued.

## Quick example

```
dotnet add package Masterly.NightShift.Redis
```

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseRedisStorage(builder.Configuration.GetConnectionString("Redis")!)); // "localhost:6379"
builder.Services.AddNightShiftServer();

var app = builder.Build();
app.MapNightShiftDashboard();
app.Run();
```

To share the application's connection, pass an `IConnectionMultiplexer`. The storage then leaves it open when disposed:

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect("localhost:6379"));
builder.Services.AddNightShift((provider, config) => config
    .UseRedisStorage(provider.GetRequiredService<IConnectionMultiplexer>()));
```

## How it works

Every key starts with `Prefix`, `{nightshift}:` by default. The braces make it a hash tag: all keys live in one hash slot, so a transaction's script can touch any of them on a cluster.

| Data | Redis keys |
|---|---|
| Jobs | hash `job:{id}` (payload, arguments, current state), hash `job:{id}:params`, list `job:{id}:history`; ids from `job-id` |
| Jobs by state | sorted set `state:{name}`, scored by id, for the dashboard's lists and counts |
| Queues | list `queue:{name}` (entries `entry:job`), sorted set `queue:{name}:fetched` (fetch time), hash `queue:{name}:tokens` |
| Sets, lists, hashes, counters | sorted set `set:{key}`, list `list:{key}`, hash `hash:{key}`, string `counter:{key}` |
| Servers | hash `servers`, sorted set `servers:heartbeat` |
| Locks | string `lock:{resource}` holding its owner, with a time to live |

Fetching runs a script that first puts back the entries whose workers stopped keeping them alive for longer than `InvisibilityTimeout`, then takes the oldest entry of the first queue that has one. Expiration uses Redis's own key expiry; a storage-wide process removes expired jobs from the state lists every `ExpirationCheckInterval`. A lock is a key set only if absent, with a time to live that its holder renews every third of `DistributedLockLifetime`, so a process that dies loses its locks once they run out.

## Options / API

| `RedisStorageOptions` | Default | What it does |
|---|---|---|
| `Prefix` | `{nightshift}:` | The prefix of every key. It must hold a hash tag, a non-empty name in braces, or the constructor throws `ArgumentException`. Use another prefix to keep applications apart in one database. |
| `AllowPrefixWithoutHashTag` | `false` | Accepts a `Prefix` without a hash tag. Only a standalone server or Sentinel works with one: on a cluster, a transaction's keys would land in different slots. |
| `Database` | -1 | The database number, -1 for the connection's default. |
| `InvisibilityTimeout` | 30 minutes | How long a fetched job stays invisible after its worker stopped keeping it alive. |
| `FetchPollInterval` | 15 seconds | How long a waiting worker sleeps if no pub/sub message wakes it. |
| `ExpirationCheckInterval` | 1 minute | How often expired jobs leave the state lists. |
| `DistributedLockLifetime` | 30 seconds | How long a lock outlives a process that died holding it. |

| API | What it does |
|---|---|
| `UseRedisStorage(string configuration, options?)` | A storage on a new connection, which it owns and closes when disposed. |
| `UseRedisStorage(IConnectionMultiplexer, options?)` | A storage on the application's connection. |
| `RedisStorage.Options` | A copy of the options the storage was created with. The options are copied and checked when the storage is created, before it connects, so changing them afterwards changes nothing. |
| `RedisStorage.Dispose()` | Stops listening for enqueued jobs; closes the connection if the storage opened it, or removes only its own subscription from a connection you passed. Calling it again does nothing. |

## Things to know

- **Persistence is Redis's.** Turn on AOF (`appendonly yes`) or snapshots, or jobs are lost when Redis restarts.
- **Memory.** Finished jobs stay for `JobStorage.JobExpirationTimeout` (one day by default). Don't set an eviction policy that evicts keys without a time to live (`allkeys-*`): it would drop queues and recurring jobs.
- **Clock.** The times that decide something (when a fetched job goes back to its queue, when a job expires, server heartbeats and when a server timed out) are read from the Redis server's clock (`TIME`) inside the scripts, and clients pass durations only, so client clocks don't need to agree with each other or with the server. `GetUtcDateTime` returns the server's time too.
- **One hash slot.** On a cluster, all of the storage's keys live on one node. Run several prefixes to spread several applications.

## See also

- [Storage](README.md)
- [Custom storage](custom-storage.md)
- [Server options](../server/server-options.md)
