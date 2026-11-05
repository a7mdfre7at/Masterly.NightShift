# Masterly.NightShift

Reliable background jobs for .NET: fire-and-forget, delayed, recurring and continuation jobs, persisted to a storage you already run, with a built-in dashboard.

```csharp
services.AddNightShift(config => config.UseSqlServerStorage(connectionString));
services.AddNightShiftServer();

BackgroundJob.Enqueue(() => Console.WriteLine("Hello from a background job"));
RecurringJob.AddOrUpdate("nightly-cleanup", () => Cleanup(), Cron.Daily());
```

For more details, please take a look at the [documentation](https://a7mdfre7at.github.io/Masterly.NightShift/) and the [README](https://github.com/a7mdfre7at/Masterly.NightShift#readme) on GitHub.
