# Masterly.NightShift.Redis

Redis storage for Masterly.NightShift background jobs, through StackExchange.Redis. Each transaction runs as one atomic Lua script, workers are woken through pub/sub as soon as a job is enqueued, and every key shares one hash tag so the storage works on a cluster.

This is a package of [Masterly.NightShift](https://www.nuget.org/packages/Masterly.NightShift), reliable background jobs for .NET.

[![NuGet](https://img.shields.io/nuget/v/Masterly.NightShift.Redis?style=flat-square)](https://www.nuget.org/packages/Masterly.NightShift.Redis)

## Install

```bash
dotnet add package Masterly.NightShift.Redis
```

- **Target frameworks:** `net8.0`, `net10.0`
- **Dependencies:** `Masterly.NightShift.Core`, [StackExchange.Redis](https://www.nuget.org/packages/StackExchange.Redis)

## Example

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

Pass an `IConnectionMultiplexer` instead of a configuration string to share the application's connection. Turn on Redis persistence (AOF or snapshots), or jobs are lost when Redis restarts.

## Documentation

Options, how the storage works and what to know before running it in production: <https://a7mdfre7at.github.io/Masterly.NightShift/docs/storage/redis>

## License

`MIT AND LGPL-3.0-or-later`: code written for Masterly.NightShift is MIT, code derived from Hangfire stays under the LGPL. The package contains both license texts.
