# Masterly.NightShift.InMemory

In-memory storage for Masterly.NightShift background jobs, for development, tests and demos. It needs no database and behaves like the SQL Server storage; nothing survives the process.

This is a package of [Masterly.NightShift](https://www.nuget.org/packages/Masterly.NightShift), reliable background jobs for .NET.

[![NuGet](https://img.shields.io/nuget/v/Masterly.NightShift.InMemory?style=flat-square)](https://www.nuget.org/packages/Masterly.NightShift.InMemory)

## Install

```bash
dotnet add package Masterly.NightShift.InMemory
```

- **Target frameworks:** `net8.0`, `net10.0`
- **Dependencies:** `Masterly.NightShift.Core`

## Example

```csharp
using Masterly.NightShift;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config.UseInMemoryStorage());
builder.Services.AddNightShiftServer();

var app = builder.Build();
app.MapNightShiftDashboard();
app.Run();
```

In a test, create an `InMemoryStorage` per test and pass it to `BackgroundJobClient` and `BackgroundJobServer`.

## Documentation

Options, how the storage works and what to know before running it in production: <https://a7mdfre7at.github.io/Masterly.NightShift/docs/storage/in-memory>

## License

`MIT AND LGPL-3.0-or-later`: code written for Masterly.NightShift is MIT, code derived from Hangfire stays under the LGPL. The package contains both license texts.
