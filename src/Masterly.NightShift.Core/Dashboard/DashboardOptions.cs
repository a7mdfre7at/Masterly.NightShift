// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift
{
    /// <summary>
    /// Options of the dashboard: who may open it and change data, its links, title and appearance, and how
    /// often it refreshes. Pass them to <c>UseNightShiftDashboard</c> or <c>MapNightShiftDashboard</c>, or
    /// register them in the service container.
    /// </summary>
    public class DashboardOptions
    {
        private static readonly IDashboardAuthorizationFilter[] DefaultAuthorization =
            new[] { new LocalRequestsOnlyAuthorizationFilter() };

        private IEnumerable<IDashboardAsyncAuthorizationFilter> _asyncAuthorization;

        /// <summary>
        /// Initializes the options with their defaults: only local requests are authorized, and the dashboard
        /// isn't read-only.
        /// </summary>
        public DashboardOptions()
        {
            AppPath = "/";
            PrefixPath = string.Empty;
            _asyncAuthorization = [];
            Authorization = DefaultAuthorization;
            IsReadOnlyFunc = static _ => false;
            StatsPollingInterval = 2000;
            DisplayStorageConnectionString = true;
            DashboardTitle = "Masterly.NightShift Dashboard";
            DisplayNameFunc = null;
            DefaultRecordsPerPage = 20;
            DarkModeEnabled = true;
        }

        /// <summary>
        /// The path for the Back To Site link. Set to <see langword="null" /> in order to hide the Back To Site link.
        /// </summary>
        public string AppPath { get; set; }
        
        /// <summary>
        /// The path for the first url prefix link, eg. set "/admin", then url is "{domain}/{PrefixPath}/{nightshift}"
        /// </summary>
        public string PrefixPath { get; set; }

        /// <summary>
        /// Gets or sets the synchronous authorization filters. Every filter must return <c>true</c>, or the request
        /// gets 401 (anonymous) or 403 (signed in). Default is one <see cref="LocalRequestsOnlyAuthorizationFilter"/>.
        /// </summary>
        public IEnumerable<IDashboardAuthorizationFilter> Authorization { get; set; }

        /// <summary>
        /// Gets or sets the asynchronous authorization filters, run after <see cref="Authorization"/>. Default is
        /// none. Setting it while <see cref="Authorization"/> still holds the default filter also clears
        /// <see cref="Authorization"/>.
        /// </summary>
        public IEnumerable<IDashboardAsyncAuthorizationFilter> AsyncAuthorization
        {
            get => _asyncAuthorization;
            set
            {
                _asyncAuthorization = value;

                if (ReferenceEquals(Authorization, DefaultAuthorization))
                {
                    Authorization = [];
                }
            }
        }

        /// <summary>
        /// Gets or sets a function called once per request; when it returns <c>true</c>, the buttons that change
        /// data are hidden and every command route answers 401. Default always returns <c>false</c>.
        /// </summary>
        public Func<DashboardContext, bool> IsReadOnlyFunc { get; set; }
        
        /// <summary>
        /// The interval the /stats endpoint should be polled with.
        /// </summary>
        public int StatsPollingInterval { get; set; }

        /// <summary>
        /// Gets or sets whether the footer shows the storage's description (<see cref="JobStorage"/>'s
        /// <c>ToString()</c>). Default is <c>true</c>.
        /// </summary>
        public bool DisplayStorageConnectionString { get; set; }

        /// <summary>
        /// The Title displayed on the dashboard, optionally modify to describe this dashboards purpose.
        /// </summary>
        public string DashboardTitle { get; set; }

        /// <summary>
        /// Display name provider for jobs
        /// </summary>
        public Func<DashboardContext, Job, string> DisplayNameFunc { get; set; }

        /// <summary>
        /// Gets or sets whether the antiforgery check of <c>POST</c> requests is turned off, and the pages stop
        /// emitting the token. Default is <c>false</c>.
        /// </summary>
        public bool IgnoreAntiforgeryToken { get; set; }

        /// <summary>
        /// Gets or sets what resolves time zone IDs for the recurring job schedule editor, the schedule preview and
        /// the Forecast page. Default is <c>null</c>, which uses the global resolver; the ASP.NET Core extensions
        /// fill it from the service container.
        /// </summary>
        public ITimeZoneResolver TimeZoneResolver { get; set; }

        /// <summary>
        /// Gets or sets the default number of records per page.
        /// </summary>
        public int DefaultRecordsPerPage { get; set; }

        /// <summary>
        /// Gets or sets whether dark mode support is enabled by including
        /// or excluding the corresponding CSS files.
        /// </summary>
        public bool DarkModeEnabled { get; set; }

        /// <summary>
        /// Optional favicon path
        /// </summary>
        public string FaviconPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the time threshold after which a warning icon will be shown near a job or a
        /// server, depending on its last reported heartbeat.
        /// </summary>
        /// <remarks>
        /// It should be larger than a configured <see cref="BackgroundJobServerOptions.HeartbeatInterval"/>
        /// value, to give servers a chance to report it, but is expected to be lower than a configured
        /// <see cref="BackgroundJobServerOptions.ServerTimeout"/> value, since this is a heuristic anyway.
        /// </remarks>
        public TimeSpan ServerPossiblyAbortedThreshold { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets or sets whether the Import button of the Recurring Jobs page accepts a manifest entry for any public
        /// method the application can load. <c>false</c> by default: only the methods the application already uses as
        /// jobs (the method of an existing recurring job, a <see cref="JobCatalog"/> entry, or a method with a
        /// generated job invoker) are accepted, and other entries are reported as errors, so an imported manifest
        /// can't turn an arbitrary method into a job. See <see cref="RecurringJobManifest"/>.
        /// </summary>
        public bool AllowAnyMethodInManifestImport { get; set; }

        /// <summary>
        /// Gets or sets whether read-only users see the IP address recorded with each entry of the Audit page.
        /// <c>false</c> by default: they see who did what, and only users who can change things (see
        /// <see cref="IsReadOnlyFunc"/>) see the addresses, which are personal data.
        /// </summary>
        public bool DisplayAuditIpAddressesToReadOnlyUsers { get; set; }
    }
}
