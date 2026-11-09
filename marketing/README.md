# Masterly.NightShift marketing kit

Ready-to-use copy for announcing and describing Masterly.NightShift: taglines, descriptions in every length, store and repository texts, launch posts and the brand basics. Every claim here maps to a documented feature (links in [Proof points](#proof-points)), so it can be used as is.

## Contents

- [Positioning](#positioning)
- [Taglines](#taglines)
- [Descriptions](#descriptions)
- [Store and repository texts](#store-and-repository-texts)
- [Key messages](#key-messages)
- [Proof points](#proof-points)
- [For Hangfire users](#for-hangfire-users)
- [Launch announcement](#launch-announcement)
- [Social posts](#social-posts)
- [FAQ](#faq)
- [Brand basics](#brand-basics)
- [Rules for claims](#rules-for-claims)

## Positioning

**For** .NET teams that need work done outside the request: e-mails, reports, imports, syncs, clean-ups and calls to slow or flaky services.

**Masterly.NightShift is** a background job library for .NET 8 and later. It stores jobs in the SQL Server you already run, runs them on background workers, retries them when they fail, and shows everything in a built-in dashboard.

**Unlike** a hand-rolled `IHostedService` loop or a cron box, jobs survive restarts and deploys, failures retry themselves, and operators can see and act on every job.

**Unlike** a message broker, there is no new infrastructure to run: the database you already back up is the queue.

**The story in one line:** your app works the day shift and answers requests; NightShift works the night shift and gets the rest done, reliably, while nobody watches.

## Taglines

Pick one per surface; the first is the primary.

1. **Background jobs that work the night shift.** (primary)
2. Reliable background jobs for .NET.
3. Hand it off. Sleep well.
4. Your requests answer. NightShift does the rest.
5. Fire, forget, and actually get it done.
6. Every job done, even when nobody's watching.
7. Background jobs with a night watch built in.
8. Schedule it in plain English. Watch it on the dashboard.
9. The queue is the database you already back up.
10. From Hangfire to NightShift: same data, more tools.

## Descriptions

### One line (up to 80 characters)

> Reliable background jobs for .NET, stored in SQL Server, with a dashboard.

### Short (about 25 words)

> Fire-and-forget, delayed, recurring and continuation jobs for .NET 8+. Stored in SQL Server, retried automatically, watched in a built-in dashboard. No extra infrastructure.

### Medium (about 50 words)

> Masterly.NightShift runs background jobs in .NET 8+ apps: fire-and-forget, delayed, recurring and continuation jobs, stored in the SQL Server you already run. Failures retry with back-off, limits and schedules are attributes, and a live dashboard shows every job. Existing Hangfire SQL Server data keeps working.

### Long (about 150 words)

> Your web request should answer in milliseconds. The PDF report, the welcome e-mail, the nightly clean-up and the call to that slow partner API shouldn't hold it up. Masterly.NightShift takes that work off the request: you write a lambda, it stores the call in your database, and a background worker runs it as soon as one is free, at the time you chose, or on a schedule written as CRON or plain English ("every 10 minutes from 9am to 5pm on weekdays").
>
> Jobs survive restarts, deploys and crashed servers. A job that throws is retried with growing delays; circuit breakers, throttles, rate limits, duplicate prevention, timeouts and run windows are one attribute away. Operators get a live dashboard with search, insights, failure clusters and a "why isn't it running?" diagnosis, plus OpenTelemetry, alerts and health checks. It targets .NET 8 and later, and started from Hangfire 1.8, so existing Hangfire SQL Server data keeps working.

## Store and repository texts

### GitHub "About" (up to 350 characters)

> Reliable background jobs for .NET 8+: fire-and-forget, delayed, recurring and continuation jobs stored in SQL Server, with automatic retries, concurrency control, plain-English schedules, OpenTelemetry and a live dashboard. Your existing Hangfire SQL Server data keeps working.

**Website:** `https://a7mdfre7at.github.io/Masterly.NightShift/`

**Topics:** `dotnet`, `csharp`, `background-jobs`, `job-scheduler`, `task-scheduler`, `cron`, `recurring-jobs`, `queue`, `sql-server`, `dashboard`, `aspnetcore`, `opentelemetry`, `hangfire`, `worker-service`

### NuGet package descriptions

The `<Description>` of each project, as nuget.org shows it under the package name.

| Package | Description |
|---|---|
| `Masterly.NightShift` | Background jobs that work the night shift: fire-and-forget, delayed, recurring and continuation jobs for .NET, stored in SQL Server, with a dashboard. Installs the core library, SQL Server storage and the ASP.NET Core integration. |
| `Masterly.NightShift.Core` | The core of Masterly.NightShift: background jobs, the job server, retries and other filters, and the dashboard. Add a storage package such as Masterly.NightShift.SqlServer. |
| `Masterly.NightShift.SqlServer` | SQL Server storage for Masterly.NightShift background jobs, on Microsoft.Data.SqlClient. Creates and upgrades its own tables; Hangfire databases keep working. |
| `Masterly.NightShift.NetCore` | Masterly.NightShift for the generic host: dependency injection, ILogger logging and the job server as a hosted service, for Worker Services and console apps. |
| `Masterly.NightShift.AspNetCore` | Masterly.NightShift for ASP.NET Core: the dashboard as an endpoint or middleware, health checks, and /health and Prometheus /metrics endpoints. |

**NuGet tags** (in `src/Directory.Build.props`): `Background`, `Jobs`, `Scheduler`, `Recurring`, `Delayed`, `FireAndForget`, `Queue`, `Dashboard`, `Cron`, `SqlServer`, `Worker`, `OpenTelemetry`, `Hangfire`

## Key messages

1. **It gets done.** Jobs are stored before they run, so a restart, a deploy or a crashed server doesn't lose them. Another server picks them up.
2. **Failures fix themselves.** Retries with growing, jittered delays are on by default. Retry-later, in-process retries and circuit breakers handle the dependency that is down.
3. **One attribute, one rule.** `[Throttle]`, `[RateLimit]`, `[PreventDuplicates]`, `[JobTimeout]`, `[Deadline]`, `[RunWindow]`: production rules without writing infrastructure.
4. **Schedules people can read.** `"tomorrow at 9am"`, `"every 10 minutes from 9am to 5pm on weekdays"`, and `Cron.Describe` to explain any CRON expression back.
5. **Operators see everything.** A live dashboard, OpenTelemetry traces and metrics, alerts to Slack, Teams, Discord or any webhook, health checks and Prometheus metrics.
6. **Nothing new to run.** The queue is the SQL Server you already back up.
7. **Modern .NET.** .NET 8 and 10, System.Text.Json, source-generated job invokers instead of reflection, nullable annotations on the main APIs.

## Proof points

Link these when a claim needs backing. The site is `https://a7mdfre7at.github.io/Masterly.NightShift/docs/` followed by the path.

| Claim | Feature | Page |
|---|---|---|
| Survives restarts and crashes | Jobs stored before they run; sliding invisibility hands a dead server's jobs to another | `getting-started/core-concepts` |
| Retries itself | `AutomaticRetry` (10 retries, jittered back-off), `RetryLaterException`, `RetryInProcess`, `CircuitBreaker` | `filters/retries` |
| Concurrency under control | `DisableConcurrentExecution`, `Throttle`, `RateLimit`, `PreventDuplicates` | `filters/concurrency` |
| Time rules | `JobTimeout`, `Deadline`, `RunWindow`, `Blackout`, failed-job retention | `filters/timing` |
| Plain-English schedules | `Cron.FromText`, `Cron.Describe`, `Schedule(..., "tomorrow at 9am")` | `recurring-jobs/cron-and-schedules`, `jobs/delayed-jobs` |
| Workflows | Continuations, fan-in (`ContinueJobWithAll`/`Any`), batches | `jobs/continuations`, `jobs/fan-in-and-batches` |
| Live progress | Job console, progress and checkpoints | `jobs/progress-and-console` |
| Dashboard | Search, insights, failure clusters, lineage, wallboard, diagnosis | `dashboard/pages` |
| Safe deploys | Drain mode for the system, a queue or one server | `server/drain-mode` |
| Observability | `UseTelemetry()`, alerts and webhooks, health checks, Prometheus | `monitoring` |
| No reflection | Source-generated invokers | `jobs/source-generated-invokers` |
| Hangfire data keeps working | Same SQL schema, type-name mapping, payloads stay readable | `migrating-from-hangfire` |

## For Hangfire users

Masterly.NightShift started from the source of Hangfire 1.8.25 and kept its shape, so a Hangfire user feels at home in minutes. What it adds:

- **Your data comes along.** The SQL Server schema is the same, and stored type names from Hangfire are mapped to the new types.
- **More built in.** A job console with progress, throttling, rate limits, batches, fan-in continuations, run windows, drain mode, alerts, an audit log, OpenTelemetry and health checks, without extra packages.
- **A redesigned dashboard** with live monitoring, insights, failure clusters, lineage and a job diagnosis.
- **Modern .NET only.** .NET 8 and 10, System.Text.Json instead of Newtonsoft.Json, source-generated invokers instead of reflection.

The step-by-step guide is [Migrating from Hangfire](https://a7mdfre7at.github.io/Masterly.NightShift/docs/migrating-from-hangfire). Say "started from the source of Hangfire 1.8" or "a fork of Hangfire 1.8"; never suggest that Hangfire OÜ endorses or maintains it.

## Launch announcement

A blog post or release note. About 400 words.

> ### Meet Masterly.NightShift: background jobs that work the night shift
>
> Every app has work that shouldn't happen inside a request. Sending the welcome e-mail. Building the monthly report. Syncing with a partner API that answers in four seconds on a good day. Cleaning up at 2 a.m.
>
> Today we're releasing **Masterly.NightShift 1.0**, a background job library for .NET 8 and later. You hand it a method call; it stores the call in your SQL Server, runs it on a background worker, and makes sure it gets done.
>
> ```csharp
> jobs.Enqueue<EmailService>(x => x.SendWelcome(userId));
> jobs.Schedule<ReminderService>(x => x.SendTrialEnding(accountId), "tomorrow at 9am");
> recurringJobs.AddOrUpdate<ReportService>("daily-report", x => x.BuildDaily(), Cron.Daily(6));
> ```
>
> **It gets done.** Jobs are stored before they run. Restart the app, deploy a new version, lose a server: the work is still there, and another server picks it up.
>
> **Failures fix themselves.** A job that throws is retried with growing delays. When a dependency is down, a circuit breaker stops hammering it, and the jobs wait until it's back.
>
> **Rules are attributes.** At most two exports per tenant at a time? `[Throttle("tenant-{0}", MaxConcurrency = 2)]`. Only at night? `[RunWindow]`. Never twice for the same order? `[PreventDuplicates]`.
>
> **Schedules read like English.** `"every 10 minutes from 9am to 5pm on weekdays"` is a valid schedule, and the dashboard explains any CRON expression back to you.
>
> **You can see everything.** The built-in dashboard shows every job live, explains why a job isn't running, groups failures by cause, and draws a job's lineage. OpenTelemetry traces connect a background job to the request that created it, and alerts reach Slack, Teams or any webhook.
>
> **No new infrastructure.** There's no broker to run and no extra service to deploy. The queue is the database you already back up.
>
> NightShift started from the source of Hangfire 1.8, so if you know Hangfire you already know most of the API, and your existing Hangfire SQL Server data keeps working. The [migration guide](https://a7mdfre7at.github.io/Masterly.NightShift/docs/migrating-from-hangfire) walks you through the switch.
>
> Get started:
>
> ```text
> dotnet add package Masterly.NightShift
> ```
>
> Then follow the five-minute [quick start](https://a7mdfre7at.github.io/Masterly.NightShift/docs/getting-started/quick-start-aspnetcore). Questions, ideas and bugs are welcome on [GitHub](https://github.com/a7mdfre7at/Masterly.NightShift).

## Social posts

### X / Bluesky / Mastodon (under 280 characters)

> 🌙 Masterly.NightShift 1.0 is out: background jobs for .NET 8+. Fire-and-forget, delayed, recurring and continuation jobs, stored in SQL Server, retried for you, watched in a live dashboard. `dotnet add package Masterly.NightShift` https://a7mdfre7at.github.io/Masterly.NightShift/

> Schedules that read like English: `"every 10 minutes from 9am to 5pm on weekdays"`. That's a real schedule in Masterly.NightShift. 🌙 https://a7mdfre7at.github.io/Masterly.NightShift/docs/recurring-jobs/cron-and-schedules

> Your app works the day shift. NightShift does the rest: retries, throttles, rate limits, run windows and a dashboard that tells you why a job isn't running. For .NET 8+. https://a7mdfre7at.github.io/Masterly.NightShift/

### LinkedIn

> Every .NET app eventually needs background jobs: e-mails, reports, imports, syncs. Doing them inside the request makes users wait; doing them in a hand-rolled loop loses them on the next deploy.
>
> I've released Masterly.NightShift 1.0, an open-source background job library for .NET 8 and later. It stores jobs in the SQL Server you already run, retries failures with back-off, and gives operators a live dashboard with insights and a "why isn't it running?" diagnosis. Throttles, rate limits, duplicate prevention, deadlines and run windows are one attribute each, and schedules can be written in plain English.
>
> It started from the source of Hangfire 1.8, so existing Hangfire SQL Server data keeps working.
>
> Docs and quick start: https://a7mdfre7at.github.io/Masterly.NightShift/
>
> #dotnet #csharp #aspnetcore #opensource

### Reddit (r/dotnet, r/csharp)

**Title:** Masterly.NightShift 1.0: background jobs for .NET 8+ (started from Hangfire 1.8, with throttling, batches, OpenTelemetry and a new dashboard built in)

> I've been working on Masterly.NightShift, an open-source background job library for .NET 8 and 10. It started from the source of Hangfire 1.8, kept the API shape and the SQL Server schema (your Hangfire data keeps working), and adds things I kept needing:
>
> - throttles, rate limits, duplicate prevention, deadlines, run windows and circuit breakers as attributes
> - fan-in continuations and batches
> - a job console with progress and checkpoints
> - plain-English schedules (`"tomorrow at 9am"`, `"every 10 minutes from 9am to 5pm on weekdays"`)
> - a redesigned dashboard with insights, failure clusters, lineage and a "why isn't it running?" diagnosis
> - OpenTelemetry, alerts (Slack, Teams, Discord, webhooks), health checks, Prometheus metrics
> - System.Text.Json, and source-generated invokers instead of reflection
>
> Docs: https://a7mdfre7at.github.io/Masterly.NightShift/ · Code: https://github.com/a7mdfre7at/Masterly.NightShift
>
> Feedback very welcome, especially from people running Hangfire in production.

## FAQ

**What does it need?** .NET 8 or later, and SQL Server (LocalDB, Express, full, Azure SQL). The storage creates its own tables.

**Do I need a message broker or a separate service?** No. Jobs run inside your app, or in a separate worker app if you prefer, and the queue is the database.

**Does it work with my existing Hangfire database?** Yes, the SQL Server schema is the same. Stop the Hangfire servers before NightShift servers start, and follow the migration guide.

**Is it free?** Yes. Code written for NightShift is MIT; code that came from Hangfire stays LGPL v3, so the packages are `MIT AND LGPL-3.0-or-later`.

**Does it run on .NET Framework?** No. Use Hangfire there, or move the app to .NET 8 first.

**Other databases?** SQL Server ships in the box. The storage contract is open, so other storages can be written; see Custom storage in the docs.

## Brand basics

| | |
|---|---|
| Name | **Masterly.NightShift** in full; **NightShift** for short in running text once the full name has appeared |
| Mark | A cream crescent moon inside a mint orbit ring, with one ember dot riding the orbit, on a deep-teal tile: `assets/logo.svg` |
| Banner | `assets/readme-banner.png` (1200 × 300) |
| Social card | `website/static/img/social-card.png` (1200 × 630) |
| Colors | Abyss `#0A2530`, deep teal `#145A6B`, orbit mint `#5FE0C8`, moonlight `#F6E9CF`, ember `#FF7A45` (accent only) |
| Fonts | IBM Plex Sans for text, JetBrains Mono for code |
| Voice | Calm, plain and concrete. Show a line of code rather than an adjective. A little night-time wordplay is welcome; one pun per piece is plenty. |

Sources for the images and how to re-render them: [assets/branding/README.md](../assets/branding/README.md).

## Rules for claims

- **No numbers without a benchmark.** Say "no reflection" and "source-generated invokers", not "10× faster".
- **Hangfire is someone else's trademark.** Say "started from the source of Hangfire 1.8" or "a fork of Hangfire 1.8"; don't use the Hangfire logo, and don't suggest an endorsement.
- **License, precisely.** `MIT AND LGPL-3.0-or-later`, not just "MIT".
- **Platforms, precisely.** .NET 8 and later; SQL Server storage in the box.
- **Before 1.0 is on nuget.org**, don't say "is out" or "released"; use "coming" or "preview".
