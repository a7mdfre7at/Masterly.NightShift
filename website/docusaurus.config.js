// @ts-check
// Docusaurus configuration for the Masterly.NightShift documentation site.
// See: https://docusaurus.io/docs/api/docusaurus-config

import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {themes as prismThemes} from 'prism-react-renderer';
import repoLinks from './src/remark/repo-links.mjs';

const repositoryUrl = 'https://github.com/a7mdfre7at/Masterly.NightShift';
const branch = 'master';

const siteDirectory = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(siteDirectory, '..');

// The pages live in the repository's docs/ folder, so they read the same on GitHub and here. The design notes in
// docs/architecture and the run log in docs/runs are not published; links to them go to GitHub (repo-links.mjs).
const docsDir = path.join(repoRoot, 'docs');
const unpublished = ['architecture', 'runs'];

// The package version has one source of truth: <Version> in src/Directory.Build.props.
const buildProps = fs.readFileSync(path.join(repoRoot, 'src', 'Directory.Build.props'), 'utf8');
const versionMatch = /<Version>([^<]+)<\/Version>/.exec(buildProps);
if (!versionMatch) {
  throw new Error('src/Directory.Build.props has no <Version> element.');
}
const packageVersion = versionMatch[1].trim();

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'Masterly.NightShift',
  tagline: 'Reliable background jobs for .NET.',
  favicon: 'img/favicon.svg',

  future: {
    v4: true,
  },

  url: 'https://a7mdfre7at.github.io',
  baseUrl: '/Masterly.NightShift/',
  organizationName: 'a7mdfre7at',
  projectName: 'Masterly.NightShift',
  trailingSlash: false,

  onBrokenLinks: 'throw',
  onBrokenAnchors: 'throw',

  customFields: {
    packageVersion,
    repositoryUrl,
  },

  markdown: {
    // .md files are plain CommonMark, so C# generics such as IBackgroundJobClient<T> need no escaping.
    format: 'detect',
    hooks: {
      onBrokenMarkdownLinks: 'throw',
    },
  },

  headTags: [
    {tagName: 'link', attributes: {rel: 'preconnect', href: 'https://fonts.googleapis.com'}},
    {tagName: 'link', attributes: {rel: 'preconnect', href: 'https://fonts.gstatic.com', crossorigin: 'anonymous'}},
  ],

  stylesheets: [
    'https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:ital,wght@0,400;0,500;0,600;0,700;1,400&family=JetBrains+Mono:wght@400;500;600&display=swap',
  ],

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          path: docsDir,
          exclude: unpublished.map((folder) => `${folder}/**`),
          sidebarPath: './sidebars.js',
          editUrl: ({docPath}) => `${repositoryUrl}/edit/${branch}/docs/${docPath}`,
          showLastUpdateTime: true,
          beforeDefaultRemarkPlugins: [[repoLinks, {docsDir, repoRoot, repositoryUrl, branch, unpublished}]],
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      }),
    ],
  ],

  themes: [
    [
      '@easyops-cn/docusaurus-search-local',
      /** @type {import('@easyops-cn/docusaurus-search-local').PluginOptions} */
      ({
        hashed: true,
        indexBlog: false,
        docsRouteBasePath: '/docs',
        docsDir,
        highlightSearchTermsOnTargetPage: true,
        explicitSearchResultPath: true,
        searchResultLimits: 10,
        searchBarShortcutHint: true,
      }),
    ],
  ],

  themeConfig:
    /** @type {import('@docusaurus/preset-classic').ThemeConfig} */
    ({
      image: 'img/social-card.png',
      metadata: [
        {
          name: 'keywords',
          content: '.NET, C#, background jobs, job scheduler, recurring jobs, cron, queue, SQL Server, dashboard, Hangfire',
        },
      ],
      colorMode: {
        defaultMode: 'dark',
        respectPrefersColorScheme: true,
      },
      announcementBar: {
        id: 'migrating-from-hangfire',
        content:
          '<strong>Coming from Hangfire?</strong> Your SQL Server data keeps working. <a href="/Masterly.NightShift/docs/migrating-from-hangfire">Read the migration guide →</a>',
        isCloseable: true,
      },
      docs: {
        sidebar: {
          hideable: true,
          autoCollapseCategories: false,
        },
      },
      tableOfContents: {
        minHeadingLevel: 2,
        maxHeadingLevel: 3,
      },
      navbar: {
        title: 'Masterly.NightShift',
        hideOnScroll: false,
        logo: {
          alt: '',
          src: 'img/logo.svg',
          width: 28,
          height: 28,
        },
        items: [
          {type: 'docSidebar', sidebarId: 'docs', position: 'left', label: 'Docs'},
          {to: '/docs/dashboard', label: 'Dashboard', position: 'left'},
          {to: '/docs/whats-new-next', label: "What's new", position: 'left'},
          {
            type: 'html',
            position: 'right',
            value: `<a class="navbar-version" href="${repositoryUrl}/blob/${branch}/CHANGELOG.md" title="Changelog">v${packageVersion}</a>`,
          },
          {
            href: 'https://www.nuget.org/packages/Masterly.NightShift',
            position: 'right',
            className: 'navbar-icon-link navbar-icon-link--nuget',
            'aria-label': 'NuGet package',
          },
          {
            href: repositoryUrl,
            position: 'right',
            className: 'navbar-icon-link navbar-icon-link--github',
            'aria-label': 'GitHub repository',
          },
        ],
      },
      footer: {
        style: 'dark',
        links: [
          {
            title: 'Learn',
            items: [
              {label: 'Quick start: ASP.NET Core', to: '/docs/getting-started/quick-start-aspnetcore'},
              {label: 'Quick start: Worker Service', to: '/docs/getting-started/quick-start-worker'},
              {label: 'Core concepts', to: '/docs/getting-started/core-concepts'},
            ],
          },
          {
            title: 'Reference',
            items: [
              {label: 'Troubleshooting', to: '/docs/troubleshooting'},
              {label: 'Migrating from Hangfire', to: '/docs/migrating-from-hangfire'},
              {label: 'Breaking changes', to: '/docs/breaking-changes'},
              {label: 'Changelog', href: `${repositoryUrl}/blob/${branch}/CHANGELOG.md`},
            ],
          },
          {
            title: 'Project',
            items: [
              {label: 'GitHub', href: repositoryUrl},
              {label: 'Issues', href: `${repositoryUrl}/issues`},
              {label: 'Security policy', href: `${repositoryUrl}/blob/${branch}/SECURITY.md`},
              {label: 'NuGet', href: 'https://www.nuget.org/packages?q=Masterly.NightShift'},
            ],
          },
        ],
        copyright: `MIT AND LGPL-3.0-or-later · © ${new Date().getFullYear()} Ahmad Omar Al-Freihat`,
      },
      prism: {
        theme: prismThemes.oneLight,
        darkTheme: prismThemes.oneDark,
        additionalLanguages: ['csharp', 'bash', 'json', 'powershell', 'sql'],
      },
    }),
};

export default config;
