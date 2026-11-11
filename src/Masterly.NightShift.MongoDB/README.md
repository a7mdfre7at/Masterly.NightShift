# Masterly.NightShift.MongoDB

MongoDB storage for Masterly.NightShift background jobs, through the official driver. It needs a replica set or a sharded cluster (a single server can run as a one-member replica set), because its transactions span several documents. TTL indexes remove what expires.

This is a package of [Masterly.NightShift](https://www.nuget.org/packages/Masterly.NightShift), reliable background jobs for .NET.

[![NuGet](https://img.shields.io/nuget/v/Masterly.NightShift.MongoDB?style=flat-square)](https://www.nuget.org/packages/Masterly.NightShift.MongoDB)

## Install

```bash
dotnet add package Masterly.NightShift.MongoDB
```

- **Target frameworks:** `net8.0`, `net10.0`
- **Dependencies:** `Masterly.NightShift.Core`, [MongoDB.Driver](https://www.nuget.org/packages/MongoDB.Driver)

## Example

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

The collections and their indexes are created when the storage starts. Pass an `IMongoDatabase` instead to share the application's client.

## Documentation

Options, how the storage works and what to know before running it in production: <https://a7mdfre7at.github.io/Masterly.NightShift/docs/storage/mongodb>

## License

`MIT AND LGPL-3.0-or-later`: code written for Masterly.NightShift is MIT, code derived from Hangfire stays under the LGPL. The package contains both license texts.
