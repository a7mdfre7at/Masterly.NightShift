# Masterly.NightShift.EntityFrameworkCore

Entity Framework Core storage for Masterly.NightShift background jobs: SQL Server, PostgreSQL, SQLite, MySQL and any other relational provider. With your own context, jobs can be staged so they are created only if your `SaveChanges` commits.

This is a package of [Masterly.NightShift](https://www.nuget.org/packages/Masterly.NightShift), reliable background jobs for .NET.

[![NuGet](https://img.shields.io/nuget/v/Masterly.NightShift.EntityFrameworkCore?style=flat-square)](https://www.nuget.org/packages/Masterly.NightShift.EntityFrameworkCore)

## Install

```bash
dotnet add package Masterly.NightShift.EntityFrameworkCore
```

- **Target frameworks:** `net8.0`, `net10.0`
- **Dependencies:** `Masterly.NightShift.Core`, [Microsoft.EntityFrameworkCore.Relational](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Relational) (8.x on `net8.0`, 10.x on `net10.0`), plus the provider you choose

## Example

```csharp
using Masterly.NightShift;

// Your context includes the storage's tables, so your migrations create them
public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyNightShiftModel();
}

builder.Services.AddDbContextFactory<ShopContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddNightShift((provider, config) => config.UseEntityFrameworkCoreStorage(
    () => provider.GetRequiredService<IDbContextFactory<ShopContext>>().CreateDbContext()));
builder.Services.AddNightShiftServer();

// The order and its confirmation email, or neither
db.Orders.Add(order);
db.NightShift().Enqueue<IEmailSender>(x => x.SendConfirmation(order.Id));
await db.SaveChangesAsync();
```

Without a context of your own, `UseEntityFrameworkCoreStorage(o => o.UseSqlServer(connectionString))` gives the storage one, and it creates its tables in the `nightshift` schema when they are missing.

## Documentation

Options, how the storage works and what to know before running it in production: <https://a7mdfre7at.github.io/Masterly.NightShift/docs/storage/entity-framework-core>

## License

`MIT AND LGPL-3.0-or-later`: code written for Masterly.NightShift is MIT, code derived from Hangfire stays under the LGPL. The package contains both license texts.
