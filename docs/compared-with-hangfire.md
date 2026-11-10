# NightShift compared with Hangfire

Masterly.NightShift is a remastered fork of [Hangfire](https://www.hangfire.io/) 1.8.25. It keeps Hangfire's programming model and can read your existing Hangfire SQL Server data. It was refactored for .NET 8 and later, and a source generator writes direct calls to your job methods instead of calling them through reflection. Features that Hangfire leaves to paid add-ons or community packages are built in.

NightShift is an independent project. It isn't affiliated with, endorsed by or supported by Hangfire OÜ. The origin of the code and its licenses are described in [NOTICE.md](../NOTICE.md).

This page helps you choose between them. To move an existing app, read [Migrating from Hangfire](migrating-from-hangfire.md).

## At a glance

| | Hangfire 1.8.25 | NightShift 1.0 |
|---|---|---|
| **Platforms** | .NET Framework 4.5.1 and later, .NET Standard 1.3 and 2.0 | .NET 8 and later only |
| **Calling your job method** | `MethodInfo.Invoke` | Source-generated direct call. A compiled delegate when the generator can't see the method, reflection as a last resort |
| **JSON** | Newtonsoft.Json | System.Text.Json, with no Newtonsoft.Json dependency |
| **Batches** | Paid (Hangfire Pro) | Included: `BatchJob` |
| **Waiting for a set of jobs (fan-in)** | Through paid batches | Included: `ContinueJobWithAll`, `ContinueJobWithAny` |
| **Concurrency limits and rate limits** | `[DisableConcurrentExecution]`. Semaphores and rate limiters are paid (Hangfire Ace) | Included: `[Throttle]`, `[RateLimit]` |
| **Circuit breaker** | Not included | Included: `[CircuitBreaker]` |
| **Duplicate prevention** | Not included | Included: `[PreventDuplicates]` |
| **Timing rules** | `[LatencyTimeout]` | `[LatencyTimeout]`, `[JobTimeout]`, `[Deadline]`, `[RunWindow]`, `[Blackout]` |
| **Retries** | `[AutomaticRetry]` | `[AutomaticRetry]`, `RetryLaterException`, `[RetryInProcess]` |
| **Wrapping the method call** | Filters only | Filters and interceptors |
| **Progress, console, resume after failure** | Not included (community packages) | Included: `IJobConsole`, checkpoints |
| **Plain-English times** | Not included | `"tomorrow at 9am"`, `"every 10 minutes from 9am to 5pm on weekdays"` |
| **Monitoring** | Dashboard (community packages for the rest) | Dashboard, OpenTelemetry, Prometheus, health checks, alerts, audit log |
| **Dashboard** | Jobs, queues, servers, retries, recurring jobs | The same, plus insights, failure clusters, lineage, job diagnosis, job catalog, timeline, wallboard, exports |
| **Stopping work before a deploy** | Not included | Drain mode, for the system, a queue or a server |
| **Storage** | SQL Server. Redis is paid (Hangfire Pro). Many community storages | SQL Server. Other storages have to be written or ported |
| **Support** | Community, or paid plans with email support | Community, through GitHub |
| **License** | LGPL v3, with paid plans for the add-ons | `MIT AND LGPL-3.0-or-later`, with every feature above included |
| **Track record** | Used in production for more than a decade | New: 1.0 is the first release |

Hangfire's paid features are described on its [pricing](https://www.hangfire.io/pricing/), [Pro](https://www.hangfire.io/pro/) and [Ace](https://www.hangfire.io/ace/) pages. The "not included" entries were checked against the Hangfire.Core 1.8.25 package.

## Why choose NightShift

- **You'd otherwise pay for batches or throttling.** Batches, fan-in continuations, semaphores and rate limits are in the box, with no subscription.
- **You run many kinds of job and need to protect what they call.** Circuit breakers, rate limits, duplicate prevention, deadlines and run windows are attributes on the job method.
- **You want to see what your jobs are doing.** Live progress and console output, plus "why isn't it running?" diagnosis, failure clusters, OpenTelemetry and Prometheus, come without extra packages.
- **Your app is on .NET 8 or later and you want a smaller dependency set.** There's no Newtonsoft.Json, and no code for .NET Framework hosts.
- **You already run Hangfire on SQL Server.** The API is close to the same, and your existing jobs and recurring jobs keep working after [the migration](migrating-from-hangfire.md).

## When to stay on Hangfire

- Your app runs on .NET Framework, or on .NET 7 or earlier.
- You use Redis or another storage that NightShift doesn't have.
- You depend on third-party Hangfire extensions. They're built against Hangfire's namespaces and would have to be ported.
- You need a vendor with paid support, or a library with a long production record.

## Benchmarks

The benchmarks compare Hangfire 1.8.25, from NuGet, with NightShift. Both ran in the same process with the same settings and the same SQL client (`Microsoft.Data.SqlClient`), each against its own database.

### Results

Lower is better in the first two tables.

| Operation (.NET 10) | Hangfire | NightShift | Difference |
|---|---|---|---|
| Call the job method | 17.0 ns | 3.5 ns | 79% less time |
| Serialize a job with an object argument | 2.38 µs, 10.97 KB | 1.59 µs, 1.84 KB | 33% less time, 83% less memory |
| Deserialize that job | 3.34 µs, 17.32 KB | 3.04 µs, 2.08 KB | 9% less time, 88% less memory |
| Create a job from a lambda | 0.70 µs, 2.08 KB | 0.74 µs, 2.08 KB | About the same |
| Run a sync job through all filters | 0.58 µs, 1.62 KB | 0.27 µs, 0.63 KB | 53% less time, 61% less memory |
| Run an async job through all filters | 1.82 µs, 1.69 KB | 1.47 µs, 0.70 KB | 19% less time, 58% less memory |
| Enqueue into SQL Server | 808 µs, 31.89 KB | 865 µs, 25.78 KB | 19% less memory, time within the noise |
| Read a job from SQL Server | 178 µs, 21.64 KB | 183 µs, 7.73 KB | 64% less memory, time about the same |

| Operation (.NET 8) | Hangfire | NightShift | Difference |
|---|---|---|---|
| Call the job method | 16.6 ns | 4.2 ns | 75% less time |
| Serialize a job with an object argument | 2.60 µs, 10.95 KB | 1.84 µs, 1.84 KB | 29% less time, 83% less memory |
| Deserialize that job | 3.61 µs, 17.30 KB | 3.07 µs, 2.08 KB | 15% less time, 88% less memory |
| Create a job from a lambda | 0.89 µs, 2.20 KB | 0.89 µs, 2.20 KB | About the same |
| Run a sync job through all filters | 0.55 µs, 1.70 KB | 0.37 µs, 0.75 KB | 33% less time, 56% less memory |
| Run an async job through all filters | 1.96 µs, 1.77 KB | 1.63 µs, 0.82 KB | 17% less time, 54% less memory |
| Enqueue into SQL Server | 861 µs, 31.95 KB | 818 µs, 25.91 KB | 19% less memory, time within the noise |
| Read a job from SQL Server | 207 µs, 22.25 KB | 165 µs, 8.34 KB | 21% less time, 63% less memory |

End to end on SQL Server: 5,000 empty jobs, 20 workers, the median of 5 rounds with the range in brackets. Higher is better.

| Jobs per second | Hangfire | NightShift |
|---|---|---|
| Enqueue, .NET 10 | 1,300 (1,174 to 1,466) | 1,383 (874 to 1,434) |
| Process, .NET 10 | 1,710 (1,460 to 1,909) | 1,583 (1,348 to 1,875) |
| Enqueue, .NET 8 | 1,187 (967 to 1,399) | 1,359 (980 to 1,445) |
| Process, .NET 8 | 1,520 (1,206 to 1,820) | 1,301 (1,256 to 1,785) |

### What the numbers mean

- **Expect about the same throughput.** A job spends its time in the database and in your own code. NightShift's median was 6% to 14% higher for enqueueing and 7% to 14% lower for processing, and the ranges overlap in all four rows.
- **NightShift's clearest gain is memory.** Serializing and deserializing a job allocates 83% to 88% less, because it uses System.Text.Json, running a job allocates over 50% less, and reading a job from SQL Server allocates over 60% less. That means less garbage collection on busy servers.
- **The source generator speeds up the call, not the whole job.** The call itself is about 4 times faster, but that saves about 13 ns in a job run that takes 0.7 to 2 µs before any database work. A generated call and NightShift's compiled fallback are close to each other, and both are faster than reflection.
- **The generator's other gain is the first call of each method.** NightShift's fallback compiles a delegate on first use, which took 138 µs to 173 µs, against about 0.5 µs for a generated invoker. It happens once per method, and Hangfire has no such step, because it uses reflection.
- **A full job run is faster and lighter.** Running a job through its filters takes 17% to 53% less time and allocates 54% to 61% less memory, because NightShift keeps each job method's filter list instead of building and sorting it on every run. The saving is a fraction of a microsecond, so it shows in memory use more than in throughput.

### How they were measured

| | |
|---|---|
| Date | 2026-11-09; the two "run a job" rows on 2026-11-10 |
| Hardware | Intel Core Ultra 7 255H, 16 cores, Windows 11 |
| Database | SQL Server LocalDB 17.0, a separate instance on the same machine |
| Tools | BenchmarkDotNet 0.15.8, and a timed end-to-end run |
| Runtimes | .NET 8.0 and .NET 10.0 |

The in-memory benchmarks use an empty job and a storage connection that does nothing, so they show library overhead alone. Results on other hardware will differ, and LocalDB isn't a production database, so run them against your own setup before relying on them.

The code is in [benchmarks/Masterly.NightShift.Benchmarks](../benchmarks/Masterly.NightShift.Benchmarks). It isn't part of the solution. To run it:

```bash
cd benchmarks/Masterly.NightShift.Benchmarks
dotnet run -c Release -f net10.0 -- --filter '*'             # all BenchmarkDotNet benchmarks, on .NET 8 and .NET 10
dotnet run -c Release -f net10.0 -- throughput 5000 6 20     # end to end: jobs, rounds, workers
```

The SQL Server benchmarks use LocalDB unless `NightShift_SqlServer_ConnectionStringTemplate` is set, and create the databases `NightShiftBench_Hangfire` and `NightShiftBench_NightShift`.

## See also

- [Migrating from Hangfire](migrating-from-hangfire.md)
- [What's new](whats-new-next.md)
- [Source-generated invokers](jobs/source-generated-invokers.md)
- [Breaking changes](breaking-changes.md)
