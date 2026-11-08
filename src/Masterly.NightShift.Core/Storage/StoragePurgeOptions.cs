namespace Masterly.NightShift.Storage
{
    /// <summary>
    /// What <see cref="JobStorage.Purge"/> keeps. Everything else, background jobs with their states,
    /// parameters, queues, counters, lists, sets and hashes, is removed. By default recurring job definitions,
    /// servers, the audit log and the drain-mode switches are kept, which is what the dashboard's "Clear all
    /// jobs" does; "Reset storage" keeps only the audit log.
    /// </summary>
    public sealed class StoragePurgeOptions
    {
        /// <summary>
        /// Gets or sets whether recurring job definitions are kept, so they don't have to be registered again
        /// by the application. Default is <c>true</c>.
        /// </summary>
        public bool KeepRecurringJobs { get; set; } = true;

        /// <summary>
        /// Gets or sets whether server registrations and heartbeats are kept. Default is <c>true</c>; running
        /// servers re-announce themselves anyway, but keeping them avoids a gap on the Servers page.
        /// </summary>
        public bool KeepServers { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the audit log (the <see cref="AuditLog.ListKey"/> list) is kept, so the record of
        /// who did what in the dashboard survives the purge. Default is <c>true</c>.
        /// </summary>
        public bool KeepAuditLog { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the processing switches are kept: drain mode, paused queues and paused servers
        /// (the <see cref="DrainMode.HashKey"/> hash). Default is <c>true</c>, so processing that was paused
        /// before the purge stays paused after it.
        /// </summary>
        public bool KeepDrainMode { get; set; } = true;
    }
}
