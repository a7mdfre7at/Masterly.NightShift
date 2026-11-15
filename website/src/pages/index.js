import React from 'react';
import clsx from 'clsx';
import Link from '@docusaurus/Link';
import useBaseUrl from '@docusaurus/useBaseUrl';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import Layout from '@theme/Layout';
import Heading from '@theme/Heading';
import CodeBlock from '@theme/CodeBlock';
import Tabs from '@theme/Tabs';
import TabItem from '@theme/TabItem';
import Icon from '@site/src/components/home/Icon';
import CopyCommand from '@site/src/components/home/CopyCommand';
import JobsDemo from '@site/src/components/home/JobsDemo';
import styles from './index.module.css';

// Every sample below is copied from the docs page it links to. Keep them in sync.
const jobKinds = [
  {
    value: 'fire-and-forget',
    label: 'Fire-and-forget',
    caption: 'Runs once, as soon as a worker is free. If it throws, it is retried automatically.',
    to: '/docs/jobs/fire-and-forget',
    code: `app.MapPost("/users/{id:int}/welcome", (int id, IBackgroundJobClient jobs) =>
{
    string? jobId = jobs.Enqueue<EmailService>(x => x.SendWelcome(id));
    return Results.Accepted(value: new { jobId });
});`,
  },
  {
    value: 'delayed',
    label: 'Delayed',
    caption: 'Runs once, later: after a delay, at a moment you choose, or at a time written in plain English.',
    to: '/docs/jobs/delayed-jobs',
    code: `jobs.Schedule<ReminderService>(x => x.SendTrialEnding(id), TimeSpan.FromDays(11));

jobs.Schedule<ReminderService>(x => x.SendTrialEnding(accountId), "tomorrow at 9am");
jobs.Schedule<ReportService>(x => x.BuildDaily(), "end of month");`,
  },
  {
    value: 'recurring',
    label: 'Recurring',
    caption: 'Runs on a CRON schedule, written by hand, with the Cron helpers, or as an English phrase.',
    to: '/docs/recurring-jobs/creating-recurring-jobs',
    code: `recurringJobs.AddOrUpdate<ReportService>(
    "daily-report",                  // the recurring job ID
    service => service.BuildDaily(), // what to run
    Cron.Daily(6));                  // when: every day at 06:00 UTC

recurringJobs.AddOrUpdate<CleanupService>("purge-sessions", x => x.PurgeExpiredSessions(),
    Cron.FromText("every 10 minutes"));`,
  },
  {
    value: 'continuations',
    label: 'Continuations',
    caption: 'Runs after another job finishes. Creating a job returns null when a filter cancels it.',
    to: '/docs/jobs/continuations',
    code: `string? chargeId = jobs.Enqueue<OrderService>(x => x.ChargeAsync(orderId));
if (chargeId != null)
{
    jobs.ContinueJobWith<EmailService>(chargeId, x => x.SendReceipt(orderId));
}`,
  },
  {
    value: 'fan-in',
    label: 'Fan-in',
    caption: 'Runs once a whole set of jobs has finished, such as merging exports made in parallel.',
    to: '/docs/jobs/fan-in-and-batches',
    code: `string?[] exports =
{
    jobs.Enqueue<ExportService>(x => x.ExportRegionAsync("eu")),
    jobs.Enqueue<ExportService>(x => x.ExportRegionAsync("us")),
    jobs.Enqueue<ExportService>(x => x.ExportRegionAsync("apac")),
};

jobs.ContinueJobWithAll<ExportService>(exports, x => x.MergeAsync());`,
  },
];

// The grid has three columns and a wide card spans two, so order the cards to fill each row: wide + 1, then 1 + 1 + 1.
const features = [
  {
    icon: 'shield',
    title: 'Retries that back off on their own',
    text: 'A job that throws is retried up to ten times with growing, jittered delays. Add retry-later, in-process retries and circuit breakers when a dependency is down.',
    to: '/docs/filters/retries',
    size: 'wide',
    visual: 'retries',
  },
  {
    icon: 'lock',
    title: 'Concurrency control',
    text: 'One at a time, throttling, rate limits and duplicate prevention, across every server.',
    to: '/docs/filters/concurrency',
  },
  {
    icon: 'timer',
    title: 'Timeouts and run windows',
    text: 'Job timeouts, deadlines, run windows and blackouts, and how long finished jobs are kept.',
    to: '/docs/filters/timing',
  },
  {
    icon: 'layout',
    title: 'A dashboard for operators',
    text: 'Live monitoring, search, insights, failure clusters, lineage, a wallboard and a "why isn\'t it running?" diagnosis.',
    to: '/docs/dashboard',
  },
  {
    icon: 'zap',
    title: 'No reflection',
    text: 'A source generator emits direct calls to your job methods, so they are not invoked through reflection.',
    to: '/docs/jobs/source-generated-invokers',
  },
  {
    icon: 'repeat',
    title: 'Schedules in plain English',
    text: 'Cron.FromText turns a phrase into a CRON expression, and Cron.Describe explains one back.',
    to: '/docs/recurring-jobs/cron-and-schedules',
    size: 'wide',
    visual: 'schedules',
  },
  {
    icon: 'moon',
    title: 'Drain mode',
    text: 'Stop starting new jobs for the whole system, a queue or one server, then deploy.',
    to: '/docs/server/drain-mode',
  },
  {
    icon: 'bell',
    title: 'Alerts and webhooks',
    text: 'Failures and missed deadlines sent to Slack, Microsoft Teams, Discord or any URL.',
    to: '/docs/monitoring/alerts-and-webhooks',
  },
  {
    icon: 'activity',
    title: 'OpenTelemetry',
    text: 'Traces and metrics for every job through UseTelemetry(), cheap when nobody listens.',
    to: '/docs/monitoring/opentelemetry',
  },
  {
    icon: 'gauge',
    title: 'Health checks and metrics',
    text: 'ASP.NET Core health checks, plus /health and Prometheus /metrics endpoints for probes and scrapers.',
    to: '/docs/monitoring/health-checks',
  },
];

const packages = [
  {name: 'Masterly.NightShift', role: 'Meta-package', text: 'Core, SQL Server storage and the ASP.NET Core integration in one install.', target: 'net8.0 · net10.0', deps: 'The three below', to: '/docs/getting-started/installation'},
  {name: 'Masterly.NightShift.Core', role: 'Core', text: 'Jobs, the server, filters and the dashboard. Add a storage package next to it.', target: 'net8.0 · net10.0', deps: 'Cronos', to: '/docs/getting-started/core-concepts'},
  {name: 'Masterly.NightShift.SqlServer', role: 'Storage', text: 'SQL Server storage on Microsoft.Data.SqlClient, with the schema created and upgraded for you.', target: 'net8.0 · net10.0', deps: 'Dapper, Microsoft.Data.SqlClient', to: '/docs/storage/sql-server'},
  {name: 'Masterly.NightShift.NetCore', role: 'Hosting', text: 'Dependency injection, logging and hosted services for Generic Host apps and Worker Services.', target: 'net8.0 · net10.0', deps: 'Microsoft.Extensions.*', to: '/docs/server/dependency-injection'},
  {name: 'Masterly.NightShift.AspNetCore', role: 'Web', text: 'Dashboard middleware and endpoint mapping, health checks and the metrics endpoint.', target: 'net8.0 · net10.0', deps: 'ASP.NET Core', to: '/docs/dashboard/setup'},
];

// Retry delays follow docs/filters/retries.md: retry n waits (n - 1)^4 + 15 + r * n seconds, r from 0 to 29.
const retrySteps = [
  {label: 'Attempt 1', detail: 'Failed: HttpRequestException', tone: 'fail'},
  {label: 'Retry 1', detail: 'Scheduled in 34 s', tone: 'sched'},
  {label: 'Attempt 2', detail: 'Failed: timeout', tone: 'fail'},
  {label: 'Retry 2', detail: 'Scheduled in 1 min 2 s', tone: 'sched'},
  {label: 'Attempt 3', detail: 'Succeeded', tone: 'pass'},
];

// Phrases and results from the tables in docs/recurring-jobs/cron-and-schedules.md.
const schedulePhrases = [
  {phrase: 'every 15 minutes', cron: '*/15 * * * *'},
  {phrase: 'weekdays at 9', cron: '0 9 * * 1-5'},
  {phrase: 'every 10 minutes from 9am to 5pm on weekdays', cron: '*/10 9-17 * * 1-5', highlight: true},
  {phrase: 'last day of month at 23:00', cron: '0 23 L * *'},
];

function Hero() {
  const {siteConfig} = useDocusaurusContext();
  const version = siteConfig.customFields.packageVersion;
  return (
    <header className={styles.hero}>
      <div className={styles.heroBackdrop} aria-hidden="true" />
      <div className={clsx('container', styles.heroGrid)}>
        <div className={styles.heroCopy}>
          <Link className={styles.eyebrow} to="/docs/whats-new-next">
            <span className={styles.eyebrowVersion}>v{version}</span>
            <span>Open source · .NET 8 · .NET 10</span>
            <Icon name="arrowRight" size={14} />
          </Link>
          <Heading as="h1" className={styles.heroTitle}>
            Background jobs that <span className={styles.heroAccent}>work the night shift.</span>
          </Heading>
          <p className={styles.heroLead}>
            Fire-and-forget, delayed, recurring and continuation jobs for .NET, stored in a database you already run.
            Jobs survive restarts and deploys, failures are retried for you, and a built-in dashboard shows what is
            happening. No Windows Service or Task Scheduler needed. It is a remastered fork of Hangfire that calls your
            jobs through source-generated code instead of reflection.{' '}
            <Link to="/docs/compared-with-hangfire">See how they compare</Link>.
          </p>
          <div className={styles.heroActions}>
            <Link
              className={clsx('button button--primary button--lg', styles.primaryButton)}
              to="/docs/getting-started/quick-start-aspnetcore">
              Get started
              <Icon name="arrowRight" size={18} />
            </Link>
            <Link className={clsx('button button--outline button--lg', styles.secondaryButton)} to="/docs">
              Read the docs
            </Link>
          </div>
          <CopyCommand command="dotnet add package Masterly.NightShift" className={styles.heroInstall} />
        </div>
        <div className={styles.heroDemo}>
          <JobsDemo />
        </div>
      </div>
    </header>
  );
}

function Stats() {
  const stats = [
    {value: '4 job types', label: 'fire-and-forget, delayed, recurring, continuations'},
    {value: '5 packages', label: 'one shared version'},
    {value: '0 reflection', label: 'job methods are called through generated code'},
    {value: 'SQL Server', label: 'storage in the box; others plug in'},
  ];
  return (
    <section className={styles.stats} aria-label="At a glance">
      <div className={clsx('container', styles.statsGrid)}>
        {stats.map((stat) => (
          <div key={stat.label} className={styles.stat}>
            <div className={styles.statValue}>{stat.value}</div>
            <div className={styles.statLabel}>{stat.label}</div>
          </div>
        ))}
      </div>
    </section>
  );
}

function SectionHeader({eyebrow, title, children}) {
  return (
    <div className={styles.sectionHeader}>
      <div className={styles.sectionEyebrow}>{eyebrow}</div>
      <Heading as="h2" className={styles.sectionTitle}>
        {title}
      </Heading>
      {children && <p className={styles.sectionLead}>{children}</p>}
    </div>
  );
}

function JobKinds() {
  return (
    <section className={styles.section}>
      <div className="container">
        <SectionHeader eyebrow="Create" title="One line to run it later">
          A job is a call to one of your methods. Its arguments are stored, so the job runs even if the process that
          created it has gone away.
        </SectionHeader>
        <div className={styles.tabsCard}>
          <Tabs groupId="job-kind" className={styles.tabs}>
            {jobKinds.map((kind) => (
              <TabItem key={kind.value} value={kind.value} label={kind.label}>
                <p className={styles.tabCaption}>
                  {kind.caption}{' '}
                  <Link to={kind.to} className={styles.inlineLink}>
                    Learn more
                    <Icon name="arrowRight" size={14} />
                  </Link>
                </p>
                <CodeBlock language="csharp">{kind.code}</CodeBlock>
              </TabItem>
            ))}
          </Tabs>
        </div>
      </div>
    </section>
  );
}

function RetryVisual() {
  return (
    <ol className={styles.ladder} aria-label="A job retried twice">
      {retrySteps.map((step, index) => (
        <li key={step.label} className={clsx(styles.ladderStep, styles[`ladder_${step.tone}`])}>
          <span className={styles.ladderIndex}>{index + 1}</span>
          <span className={styles.ladderLabel}>{step.label}</span>
          <span className={styles.ladderDetail}>{step.detail}</span>
        </li>
      ))}
    </ol>
  );
}

function ScheduleVisual() {
  return (
    <table className={styles.phrases}>
      <caption className={styles.srOnly}>English phrases and the CRON expressions they become</caption>
      <thead>
        <tr>
          <th scope="col">Cron.FromText(...)</th>
          <th scope="col">CRON</th>
        </tr>
      </thead>
      <tbody>
        {schedulePhrases.map((row) => (
          <tr key={row.phrase} className={row.highlight ? styles.phraseHighlight : undefined}>
            <td>"{row.phrase}"</td>
            <td>{row.cron}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function Features() {
  return (
    <section className={clsx(styles.section, styles.sectionAlt)}>
      <div className="container">
        <SectionHeader eyebrow="Run" title="Everything a job needs in production">
          Start with one job. Add retries, limits, schedules and monitoring when you need them, with attributes and
          configuration rather than new infrastructure.
        </SectionHeader>
        <div className={styles.bento}>
          {features.map((feature) => (
            <Link
              key={feature.title}
              to={feature.to}
              className={clsx(styles.featureCard, feature.size === 'wide' && styles.featureWide)}>
              <div className={styles.featureBody}>
                <span className={styles.featureIcon}>
                  <Icon name={feature.icon} size={20} />
                </span>
                <Heading as="h3" className={styles.featureTitle}>
                  {feature.title}
                </Heading>
                <p className={styles.featureText}>{feature.text}</p>
                <span className={styles.featureMore}>
                  Read the guide
                  <Icon name="arrowRight" size={14} />
                </span>
              </div>
              {feature.visual === 'retries' && <RetryVisual />}
              {feature.visual === 'schedules' && <ScheduleVisual />}
            </Link>
          ))}
        </div>
      </div>
    </section>
  );
}

function Packages() {
  return (
    <section className={styles.section}>
      <div className="container">
        <SectionHeader eyebrow="Packages" title="One install for web apps. Pick and mix for the rest.">
          All five packages ship together with the same version number. ASP.NET Core apps install the meta-package;
          Worker Services and console apps take only the parts they use.
        </SectionHeader>
        <div className={styles.packages}>
          {packages.map((pkg) => (
            <Link key={pkg.name} to={pkg.to} className={styles.packageCard}>
              <div className={styles.packageTop}>
                <span className={styles.packageIcon}>
                  <Icon name="box" size={18} />
                </span>
                <span className={styles.packageRole}>{pkg.role}</span>
              </div>
              <code className={styles.packageName}>{pkg.name}</code>
              <p className={styles.packageText}>{pkg.text}</p>
              <div className={styles.packageMeta}>
                <span>{pkg.target}</span>
                <span>{pkg.deps}</span>
              </div>
            </Link>
          ))}
        </div>
      </div>
    </section>
  );
}

function CallToAction() {
  return (
    <section className={styles.cta}>
      <div className={clsx('container', styles.ctaInner)}>
        <img className={styles.ctaMark} src={useBaseUrl('/img/logo.svg')} alt="" width="72" height="72" />
        <Heading as="h2" className={styles.ctaTitle}>
          Your first job, running in five minutes.
        </Heading>
        <p className={styles.ctaText}>
          The quick start goes from an empty web app to a job you can watch in the dashboard.
        </p>
        <div className={styles.heroActions}>
          <Link
            className={clsx('button button--primary button--lg', styles.primaryButton)}
            to="/docs/getting-started/quick-start-aspnetcore">
            Open the quick start
            <Icon name="arrowRight" size={18} />
          </Link>
          <Link className={clsx('button button--outline button--lg', styles.secondaryButton)} to="/docs/migrating-from-hangfire">
            Migrate from Hangfire
          </Link>
        </div>
      </div>
    </section>
  );
}

export default function Home() {
  return (
    <Layout description="Reliable background jobs for .NET: fire-and-forget, delayed, recurring and continuation jobs, persisted to SQL Server, with retries, concurrency control and a built-in dashboard.">
      <Hero />
      <main>
        <Stats />
        <JobKinds />
        <Features />
        <Packages />
        <CallToAction />
      </main>
    </Layout>
  );
}
