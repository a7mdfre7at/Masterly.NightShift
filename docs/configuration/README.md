# Configuration

Process-wide settings live on `GlobalConfiguration.Configuration`, a chain of `Use*`/`Set*` extension methods. In ASP.NET Core and Worker Service apps you reach the same object through the callback of `services.AddNightShift(...)`. This section covers every method on that chain, how job data is turned into JSON, and how to choose the stored data format when servers of different versions share one storage.

| Page | What it covers |
|---|---|
| [Global configuration](global-configuration.md) | `GlobalConfiguration` and every `Use*`/`Set*` extension method, grouped by purpose |
| [Serialization](serialization.md) | What is stored as JSON, System.Text.Json options, `UseSerializerSettings`, `UseRecommendedSerializerSettings`, reading data written by older versions, rules for argument types |
| [Compatibility levels](compatibility-levels.md) | `SetDataCompatibilityLevel`, what each `CompatibilityLevel` changes in stored data, and rolling upgrades |

## The most common setup

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")!));

builder.Services.AddNightShiftServer();

var app = builder.Build();
app.Run();
```

These three lines before the storage are the settings a new application should start with:

- `SetDataCompatibilityLevel(CompatibilityLevel.Version_180)` writes the newest, most compact data format. The default is `Version_110`, the oldest one, so existing deployments keep working. See [Compatibility levels](compatibility-levels.md).
- `UseSimpleAssemblyNameTypeSerializer()` stores type names without assembly versions, so jobs survive a new build of your assembly. See [Global configuration](global-configuration.md#serialization-and-type-names).
- `UseRecommendedSerializerSettings()` serializes your job arguments with the same options as the library's own data, so arguments declared as `object` keep their type. See [Serialization](serialization.md).

## See also

- [Quick start: ASP.NET Core](../getting-started/quick-start-aspnetcore.md)
- [Quick start: console app or Worker Service](../getting-started/quick-start-worker.md)
- [Dependency injection](../server/dependency-injection.md)
- [SQL Server storage](../storage/sql-server.md)
