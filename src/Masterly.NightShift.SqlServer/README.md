# Masterly.NightShift.SqlServer

SQL Server storage for Masterly.NightShift background jobs. Jobs, their state history, queues, recurring jobs and statistics are kept in a set of tables that the storage creates and upgrades itself. Databases created by Hangfire keep working.

This is a package of [Masterly.NightShift](https://www.nuget.org/packages/Masterly.NightShift), reliable background jobs for .NET.

[![NuGet](https://img.shields.io/nuget/v/Masterly.NightShift.SqlServer?style=flat-square)](https://www.nuget.org/packages/Masterly.NightShift.SqlServer)

## Install

```bash
dotnet add package Masterly.NightShift.SqlServer
```

- **Target frameworks:** `net8.0`, `net10.0`
- **Dependencies:** `Masterly.NightShift.Core`, [Dapper](https://www.nuget.org/packages/Dapper), [Microsoft.Data.SqlClient](https://www.nuget.org/packages/Microsoft.Data.SqlClient)

## Example

Add a connection string to `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "NightShift": "Server=.\\sqlexpress;Database=MyApp;Integrated Security=True;TrustServerCertificate=True;"
  }
}
```

Then register the storage (here in an ASP.NET Core app with the `Masterly.NightShift` meta-package):

```csharp
using Masterly.NightShift;
using Masterly.NightShift.SqlServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNightShift(config => config
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("NightShift")!));
builder.Services.AddNightShiftServer();

var app = builder.Build();

app.MapNightShiftDashboard();

app.Run();
```

On the first start the storage creates its tables in the `[HangFire]` schema of the `MyApp` database. The database itself must already exist.

## Documentation

Connection strings, database permissions, every `SqlServerStorageOptions` property and schema upgrades are covered in the full documentation: <https://a7mdfre7at.github.io/Masterly.NightShift/docs/storage>

## License

`MIT AND LGPL-3.0-or-later`: code written for Masterly.NightShift is MIT, code derived from Hangfire stays under the LGPL. The package contains both license texts.
