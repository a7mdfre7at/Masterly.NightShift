using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// "Delete all" and "Requeue all" for the job lists: pages through a list with the monitoring API and changes
    /// the state of every job that is still in the list's state, until the list ends or the work or time limit of
    /// one request is reached. "Remove all" recurring jobs and the batch actions (<see cref="BatchCommands"/>)
    /// keep to the same limits.
    /// </summary>
    internal static class JobListCommands
    {
        internal const int BatchSize = BulkStateChange.PageSize;

        // Pages read by one request. Each read either handles new jobs or moves past jobs handled before,
        // so one click changes at most BatchSize * MaxReads jobs and the rest is left for the next click.
        internal const int MaxReads = 200;

        /// <summary>
        /// How long one request may change states. Each change is a storage round trip (a few milliseconds), so a long
        /// list would take minutes: a proxy would time out while the work went on, and the summary would be lost.
        /// The request stops after this time and reports that the list isn't done. Replaceable for tests.
        /// </summary>
        internal static TimeSpan TimeBudget { get; set; } = TimeSpan.FromSeconds(20);

        private const string DeleteReason = "Deleted via Dashboard UI (delete all)";
        private const string RequeueReason = "Requeued via Dashboard UI (requeue all)";

        /// <summary>
        /// Deletes every job of a list. <paramref name="page"/> returns the ids of a page, each with whether the job
        /// is still in the list's state; jobs that aren't are skipped.
        /// </summary>
        public static BulkJobResult DeleteAll(
            DashboardContext context,
            Func<IMonitoringApi, int, int, IEnumerable<KeyValuePair<string, bool>>> page,
            Func<IMonitoringApi, long> count,
            string expectedState)
        {
            var client = context.GetBackgroundJobClient();
            var monitor = context.Storage.GetMonitoringApi();

            return Run(
                id => client.ChangeState(id, new DeletedState { Reason = DeleteReason }, expectedState),
                (from, size) => page(monitor, from, size),
                count != null ? () => count(monitor) : null);
        }

        /// <summary>Requeues every job of a list, like <see cref="DeleteAll"/>.</summary>
        public static BulkJobResult RequeueAll(
            DashboardContext context,
            Func<IMonitoringApi, int, int, IEnumerable<KeyValuePair<string, bool>>> page,
            Func<IMonitoringApi, long> count,
            string expectedState)
        {
            var client = context.GetBackgroundJobClient();
            var monitor = context.Storage.GetMonitoringApi();

            return Run(
                id => client.ChangeState(id, new EnqueuedState { Reason = RequeueReason }, expectedState),
                (from, size) => page(monitor, from, size),
                count != null ? () => count(monitor) : null);
        }

        /// <summary>Deletes every job whose id is in a sorted set, such as <c>retries</c>.</summary>
        /// <returns>
        /// The result, or <c>null</c> when the storage can't page through a set (no <see cref="JobStorageFeatures.ExtendedApi"/>).
        /// </returns>
        public static BulkJobResult DeleteAllFromSet(DashboardContext context, string setKey, string expectedState)
        {
            if (!context.Storage.HasFeature(JobStorageFeatures.ExtendedApi))
            {
                return null;
            }

            var client = context.GetBackgroundJobClient();

            using (var connection = context.Storage.GetConnection())
            {
                if (connection is not JobStorageConnection storageConnection)
                {
                    return null;
                }

                return Run(
                    id => client.ChangeState(id, new DeletedState { Reason = DeleteReason }, expectedState),
                    (from, size) => storageConnection.GetRangeFromSet(setKey, from, from + size - 1).Select(static id => new KeyValuePair<string, bool>(id, true)),
                    () => storageConnection.GetSetCount(setKey));
            }
        }

        /// <summary>
        /// Removes every recurring job, within the work and time limits of one request like the job lists:
        /// <c>complete</c> is <c>false</c> when some are left for another click.
        /// </summary>
        public static BulkJobResult RemoveAllRecurringJobs(DashboardContext context)
        {
            var manager = context.GetRecurringJobManager();
            List<string> ids;

            using (var connection = context.Storage.GetConnection())
            {
                ids = (connection.GetAllItemsFromSet("recurring-jobs") ?? new HashSet<string>())
                    .OrderBy(static id => id, StringComparer.Ordinal)
                    .ToList();
            }

            return RunOnIds(ids, id =>
            {
                manager.RemoveIfExists(id);
                return true;
            });
        }

        /// <summary>
        /// Changes each id of a list read beforehand, until the list ends or the work or time limit of one request is
        /// reached. <c>remaining</c> is then the number of ids not reached.
        /// </summary>
        internal static BulkJobResult RunOnIds(IReadOnlyList<string> ids, Func<string, bool> change)
        {
            return BulkStateChange.RunOnIds(ids, change, MaxReads, TimeBudget);
        }

        internal static BulkJobResult Run(
            Func<string, bool> change,
            Func<int, int, IEnumerable<KeyValuePair<string, bool>>> page,
            Func<long> count)
        {
            return BulkStateChange.RunOnPages(change, page, count, MaxReads, TimeBudget);
        }
    }

    /// <summary>
    /// Runs a "Delete all" or "Requeue all" command: <c>POST</c> only, <c>401</c> for read-only users, answers
    /// <c>200</c> with <c>{ processed, remaining, complete }</c>, or <c>422</c> when the storage can't do it,
    /// and records the action in the audit log.
    /// </summary>
    internal sealed class JobListCommandDispatcher : IDashboardCommandDispatcher
    {
        private readonly Func<DashboardContext, BulkJobResult> _command;

        public JobListCommandDispatcher(Func<DashboardContext, BulkJobResult> command)
        {
            _command = command ?? throw new ArgumentNullException(nameof(command));
        }

        public async Task Dispatch(DashboardContext context)
        {
            if (context.IsReadOnly)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return;
            }

            if (!"POST".Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            BulkJobResult result;
            try
            {
                result = _command(context);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // Some jobs may have changed already: keep a record of the attempt, then let it answer 500
                AuditLog.Record(context, Targets(context), $"failed: {ex.GetType().Name}", (int)HttpStatusCode.InternalServerError);
                throw;
            }

            if (result == null)
            {
                context.Response.StatusCode = 422;
                AuditLog.Record(context, Targets(context), null, 422);
                return;
            }

            AuditLog.Record(context, Targets(context), $"{result.Processed} jobs", (int)HttpStatusCode.OK);

            await DashboardJson.WriteAsync(context, (int)HttpStatusCode.OK, new
            {
                processed = result.Processed,
                remaining = result.Remaining,
                complete = result.Complete,
            }).ConfigureAwait(false);
        }

        private static List<string> Targets(DashboardContext context)
        {
            var targets = new List<string>();
            if (context.UriMatch == null) return targets;

            for (var i = 1; i < context.UriMatch.Groups.Count; i++)
            {
                var value = context.UriMatch.Groups[i].Value;
                if (!String.IsNullOrEmpty(value)) targets.Add(RouteValues.Decode(value));
            }

            return targets;
        }
    }
}
