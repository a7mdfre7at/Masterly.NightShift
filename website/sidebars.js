// @ts-check

/**
 * A sidebar category whose label opens the section's README.md, which lists the section's pages.
 * Page ids are paths under docs/ without the extension.
 * @param {string} label
 * @param {string} folder
 * @param {string[]} pages
 * @param {boolean} [collapsed]
 * @returns {import('@docusaurus/plugin-content-docs').SidebarItemCategory}
 */
function section(label, folder, pages, collapsed = true) {
  return {
    type: 'category',
    label,
    collapsed,
    link: {type: 'doc', id: `${folder}/README`},
    items: pages.map((page) => `${folder}/${page}`),
  };
}

/** @type {import('@docusaurus/plugin-content-docs').SidebarsConfig} */
const sidebars = {
  docs: [
    {type: 'doc', id: 'README', label: 'Introduction'},
    section(
      'Getting started',
      'getting-started',
      ['installation', 'quick-start-aspnetcore', 'quick-start-worker', 'core-concepts'],
      false,
    ),
    section('Jobs', 'jobs', [
      'fire-and-forget',
      'typed-handlers',
      'job-functions',
      'delayed-jobs',
      'schedule-policies',
      'continuations',
      'fan-in-and-batches',
      'chains',
      'job-arguments',
      'cancellation',
      'progress-and-console',
      'tags-and-notes',
      'job-catalog',
      'managing-jobs',
      'source-generated-invokers',
      'testing',
    ]),
    section('Recurring jobs', 'recurring-jobs', [
      'creating-recurring-jobs',
      'cron-and-schedules',
      'pausing-and-updating',
      'recurring-jobs-in-code',
      'time-zones-and-misfires',
      'recurring-job-manifest',
    ]),
    section('Filters', 'filters', ['retries', 'concurrency', 'timing', 'behavior', 'custom-filters']),
    section('Server', 'server', [
      'server-options',
      'queues-and-workers',
      'drain-mode',
      'reliability',
      'keep-alive',
      'dependency-injection',
      'background-processes',
    ]),
    section('Storage', 'storage', ['sql-server', 'sql-server-options', 'schema-and-upgrades', 'entity-framework-core', 'redis', 'mongodb', 'in-memory', 'custom-storage']),
    section('Dashboard', 'dashboard', ['setup', 'authorization', 'pages', 'customization']),
    section('Monitoring', 'monitoring', [
      'opentelemetry',
      'alerts-and-webhooks',
      'job-monitors',
      'exception-handlers',
      'audit-log-and-activity',
      'insights',
      'health-checks',
      'logging',
    ]),
    section('Configuration', 'configuration', ['global-configuration', 'serialization', 'compatibility-levels']),
    {
      type: 'category',
      label: 'Help and reference',
      collapsed: true,
      link: {
        type: 'generated-index',
        slug: '/category/help-and-reference',
        title: 'Help and reference',
        description: 'Fixes for common problems, benchmarks, moving from Hangfire, what changed, and what is planned.',
      },
      items: ['troubleshooting', 'compared-with-hangfire', 'benchmarks', 'migrating-from-hangfire', 'whats-new-next', 'changes-from-hangfire', 'roadmap'],
    },
    section('Contributing', 'contributing', ['architecture']),
  ],
};

export default sidebars;
