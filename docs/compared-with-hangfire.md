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
| **Creating many jobs at once** | Through paid batches | Included: `EnqueueMany` |
| **Round trips to SQL Server** | Four to create a job, three for every commit | Two to create a job, one for every commit; concurrent callers share them |
| **Waiting for a set of jobs (fan-in)** | Through paid batches | Included: `ContinueJobWithAll`, `ContinueJobWithAny` |
| **Concurrency limits and rate limits** | `[DisableConcurrentExecution]`. Semaphores and rate limiters are paid (Hangfire Ace) | Included: `[Throttle]`, `[RateLimit]` |
| **Circuit breaker** | Not included | Included: `[CircuitBreaker]` |
| **Duplicate prevention** | Not included | Included: `[PreventDuplicates]` |
| **Timing rules** | `[LatencyTimeout]` | `[LatencyTimeout]`, `[JobTimeout]`, `[Deadline]`, `[RunWindow]`, `[Blackout]`, `[SkipIfLate]` |
| **Retries** | `[AutomaticRetry]` | `[AutomaticRetry]`, `RetryLaterException`, `TerminateJobException`, `[RetryInProcess]` |
| **Wrapping the method call** | Filters only | Filters and interceptors |
| **Progress, console, resume after failure** | Not included (community packages) | Included: `IJobConsole`, checkpoints |
| **Plain-English times** | Not included | `"tomorrow at 9am"`, `"every 10 minutes from 9am to 5pm on weekdays"` |
| **Monitoring** | Dashboard (community packages for the rest) | Dashboard, OpenTelemetry, Prometheus, health checks, alerts, audit log |
| **Dashboard** | Jobs, queues, servers, retries, recurring jobs | The same, plus insights, failure clusters, lineage, job diagnosis, job catalog, timeline, wallboard, exports |
| **Stopping work before a deploy** | Not included | Drain mode, for the system, a queue or a server |
| **Storage** | SQL Server. Redis is paid (Hangfire Pro). Many community storages | SQL Server, Entity Framework Core (PostgreSQL, MySQL, SQLite and other providers), Redis, MongoDB and in memory, all passing one conformance suite. Other storages have to be written or ported |
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

Measured against Hangfire 1.8.25 on .NET 10 and SQL Server 2022:

- **Creating jobs:** a loop of `Enqueue` calls is about 1.25 times faster, 64 callers at once about twice as fast, and `EnqueueMany` creates jobs over 20 times faster than a loop.
- **Processing:** about the same by default, and about 1.7 times faster with `AllowDelayedDurability`.
- **Memory:** running a job allocates about 60% less, and serializing one over 80% less.
- **Calling the job method:** about 4 times faster, which saves a few nanoseconds per job.

[Benchmarks](benchmarks.md) has every number, what it means, and how to run the benchmarks yourself.

## See also

- [Benchmarks](benchmarks.md)
- [Migrating from Hangfire](migrating-from-hangfire.md)
- [What's new](whats-new-next.md)
- [Source-generated invokers](jobs/source-generated-invokers.md)
- [Changes from Hangfire](changes-from-hangfire.md)
