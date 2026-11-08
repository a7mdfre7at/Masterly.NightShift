using System;
using System.Net;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>POST /maintenance/clear-jobs</c> (keeps recurring jobs, servers, the audit log and drain mode) and
    /// <c>POST /maintenance/reset</c> (keeps only the audit log): purge the storage through
    /// <see cref="JobStorage.Purge"/>. Neither wipes the audit log, so the record of who cleared or reset the
    /// storage keeps its history. Answers <c>204</c>, <c>422 {error}</c> when the storage doesn't support purging, or
    /// <c>500 {error}</c> with a generic message when the purge fails (the error itself is logged). Every outcome is
    /// recorded in the audit log.
    /// </summary>
    internal sealed class MaintenanceDispatcher : IDashboardDispatcher
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(MaintenanceDispatcher));

        private readonly bool _keepRecurringJobsAndServers;

        public MaintenanceDispatcher(bool keepRecurringJobsAndServers)
        {
            _keepRecurringJobsAndServers = keepRecurringJobsAndServers;
        }

        public Task Dispatch(DashboardContext context)
        {
            if (context.IsReadOnly)
            {
                context.Response.StatusCode = 401;
                return Task.CompletedTask;
            }

            if (!"POST".Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return Task.CompletedTask;
            }

            if (!context.Storage.HasFeature(JobStorageFeatures.Purge))
            {
                AuditLog.Record(context, null, "not supported by the storage", 422);
                return DashboardJson.WriteAsync(context, 422, new { error = JobStorageFeatures.GetNotSupportedException(JobStorageFeatures.Purge).Message });
            }

            try
            {
                context.Storage.Purge(new StoragePurgeOptions
                {
                    KeepRecurringJobs = _keepRecurringJobsAndServers,
                    KeepServers = _keepRecurringJobsAndServers,
                    KeepAuditLog = true,
                    KeepDrainMode = _keepRecurringJobsAndServers,
                });

                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                AuditLog.Record(context);
                return Task.CompletedTask;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // The storage's message can name servers, databases or tables: it goes to the log, not to the browser
                Logger.ErrorException($"Dashboard action {context.Request.Path}: the storage could not be purged.", ex);
                AuditLog.Record(context, null, $"failed: {ex.GetType().Name}", 500);
                return DashboardJson.WriteAsync(context, 500, new { error = "The storage could not be purged. The error is in the server's log." });
            }
        }
    }
}
