# Benchmarks

These benchmarks compare Hangfire 1.8.25, from NuGet, with NightShift. Both ran in the same process with the same settings and the same SQL client (`Microsoft.Data.SqlClient`), each against its own database.

For the differences in features, see [NightShift compared with Hangfire](compared-with-hangfire.md).

## End to end on SQL Server

Empty jobs on SQL Server 2022 and .NET 10, with 20 workers. Each number is the median of 3 rounds. Higher is better.

| Jobs per second | Hangfire | NightShift | NightShift with `AllowDelayedDurability` |
|---|---|---|---|
| `Enqueue` in a loop, one caller | 174 | 218 | 332 |
| `Enqueue` from 64 callers at once | 1,523 | 3,179 | 3,063 |
| [`EnqueueMany`](jobs/fire-and-forget.md#many-jobs-at-once) | No free equivalent | 4,116 | 3,738 |
| Processing | 562 to 648 | 606 to 794 | 974 to 1,000 |

The database ran in a Docker container, where a round trip takes about 0.7 ms and a commit about 2 ms, so the absolute numbers are low for both libraries. Results vary by 10% to 20% between runs; the processing row gives the range seen in two runs.

## In-process operations

These measure the library alone, with no database. Lower is better.

| Operation (.NET 10) | Hangfire | NightShift | Difference |
|---|---|---|---|
| Call the job method | 17.0 ns | 3.5 ns | 79% less time |
| Serialize a job with an object argument | 2.38 µs, 10.97 KB | 1.59 µs, 1.84 KB | 33% less time, 83% less memory |
| Deserialize that job | 3.34 µs, 17.32 KB | 3.04 µs, 2.08 KB | 9% less time, 88% less memory |
| Create a job from a lambda | 0.70 µs, 2.08 KB | 0.74 µs, 2.08 KB | About the same |
| Run a sync job through all filters | 0.58 µs, 1.62 KB | 0.27 µs, 0.63 KB | 53% less time, 61% less memory |
| Run an async job through all filters | 1.82 µs, 1.69 KB | 1.47 µs, 0.70 KB | 19% less time, 58% less memory |

| Operation (.NET 8) | Hangfire | NightShift | Difference |
|---|---|---|---|
| Call the job method | 16.6 ns | 4.2 ns | 75% less time |
| Serialize a job with an object argument | 2.60 µs, 10.95 KB | 1.84 µs, 1.84 KB | 29% less time, 83% less memory |
| Deserialize that job | 3.61 µs, 17.30 KB | 3.07 µs, 2.08 KB | 15% less time, 88% less memory |
| Create a job from a lambda | 0.89 µs, 2.20 KB | 0.89 µs, 2.20 KB | About the same |
| Run a sync job through all filters | 0.55 µs, 1.70 KB | 0.37 µs, 0.75 KB | 33% less time, 56% less memory |
| Run an async job through all filters | 1.96 µs, 1.77 KB | 1.63 µs, 0.82 KB | 17% less time, 54% less memory |

## What the numbers mean

- **A loop of `Enqueue` calls is about 1.25 times faster, and about twice as fast with `AllowDelayedDurability`.** NightShift sends a commit as one round trip, so creating a job takes two round trips where Hangfire takes four. A caller still waits for the database to store each job before it gets the job's ID, which is why a loop can't be many times faster.
- **Many callers at once are about twice as fast.** Up to 8 callers write at the same time; the callers beyond them are written in groups, one round trip and one commit per group.
- **Creating many jobs at once is over 20 times faster.** `EnqueueMany` writes a group of jobs in a few round trips, where a loop pays several per job.
- **Processing is about the same by default, and about 1.7 times faster with `AllowDelayedDurability`.** With the option, fetching a job and moving it to Processing don't wait for the disk, and only the commit of the final state does.
- **`AllowDelayedDurability` is a trade-off you choose.** A job's creation and its final state are always stored before the call returns. If SQL Server stops in the few milliseconds after a job was fetched, the job runs again. See [SQL Server options](storage/sql-server-options.md).
- **NightShift's clearest gain is memory.** Serializing and deserializing a job allocates 83% to 88% less, because it uses System.Text.Json, and running a job allocates over 50% less. That means less garbage collection on busy servers.
- **The source generator speeds up the call, not the whole job.** The call itself is about 4 times faster, but that saves about 13 ns in a job run that takes 0.7 to 2 µs before any database work. A generated call and NightShift's compiled fallback are close to each other, and both are faster than reflection.
- **The generator's other gain is the first call of each method.** NightShift's fallback compiles a delegate on first use, which took 138 µs to 173 µs, against about 0.5 µs for a generated invoker. It happens once per method, and Hangfire has no such step, because it uses reflection.
- **A full job run is faster and lighter.** Running a job through its filters takes 17% to 53% less time and allocates 54% to 61% less memory, because NightShift keeps each job method's filter list instead of building and sorting it on every run. The saving is a fraction of a microsecond, so it shows in memory use more than in throughput.

## How they were measured

| | |
|---|---|
| Date | 2026-11-09 for the in-process tables; the two "run a job" rows and the end-to-end table on 2026-11-10 |
| Hardware | Intel Core Ultra 7 255H, 16 cores, Windows 11 |
| Database | SQL Server 2022 Developer (16.0.4295) in a Docker container on the same machine |
| Tools | BenchmarkDotNet 0.15.8, and a timed end-to-end run |
| Runtimes | .NET 8.0 and .NET 10.0 |

The in-process benchmarks use an empty job and a storage connection that does nothing, so they show library overhead alone. Results on other hardware will differ, so run them against your own database before relying on them. Use a full SQL Server: LocalDB is the Express engine and hides what a server does under load.

## Run them yourself

The code is in [benchmarks/Masterly.NightShift.Benchmarks](../benchmarks/Masterly.NightShift.Benchmarks). It isn't part of the solution. To run it:

```bash
cd benchmarks/Masterly.NightShift.Benchmarks
dotnet run -c Release -f net10.0 -- --filter '*'             # all BenchmarkDotNet benchmarks, on .NET 8 and .NET 10
dotnet run -c Release -f net10.0 -- throughput 3000 4 20     # end to end: jobs, rounds, workers
dotnet run -c Release -f net10.0 -- throughput 6000 4 20 64  # the same, enqueueing from 64 callers at once
```

Set `NightShift_SqlServer_ConnectionStringTemplate` to a connection string with a `{0}` placeholder for the database name. The end-to-end run creates the databases `NightShiftBench_Hangfire` and `NightShiftBench_NightShift`; `NS_DD=1` turns on `AllowDelayedDurability` for NightShift.

## See also

- [NightShift compared with Hangfire](compared-with-hangfire.md)
- [SQL Server options](storage/sql-server-options.md)
- [Many jobs at once](jobs/fire-and-forget.md#many-jobs-at-once)
- [Source-generated invokers](jobs/source-generated-invokers.md)
