// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
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

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>One action somebody took on the dashboard.</summary>
    public sealed class AuditEntry
    {
        /// <summary>When, as a Unix timestamp in seconds.</summary>
        [JsonInclude, JsonPropertyName("t")]
        public long Timestamp { get; set; }

        /// <summary>The authenticated user, when the host knows one.</summary>
        [JsonInclude, JsonPropertyName("u"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? User { get; set; }

        /// <summary>The client address.</summary>
        [JsonInclude, JsonPropertyName("ip"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RemoteIpAddress { get; set; }

        /// <summary>The dashboard path that was posted to, for example <c>/jobs/failed/requeue</c>.</summary>
        [JsonInclude, JsonPropertyName("a")]
        public string Action { get; set; } = String.Empty;

        /// <summary>What the action was about: job ids, recurring job ids, a queue, a tag. At most <see cref="AuditLog.MaxTargets"/> are kept.</summary>
        [JsonInclude, JsonPropertyName("g"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? Targets { get; set; }

        /// <summary>How many targets there were, when more than <see cref="AuditLog.MaxTargets"/>.</summary>
        [JsonInclude, JsonPropertyName("n"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? TargetCount { get; set; }

        /// <summary>A free-text detail: the reason given, the job created, the manifest outcome.</summary>
        [JsonInclude, JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Detail { get; set; }

        /// <summary>The HTTP status the action answered with.</summary>
        [JsonInclude, JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Status { get; set; }

        /// <summary>When, as UTC.</summary>
        [JsonIgnore]
        public DateTime OccurredAt => JobHelper.FromTimestamp(Timestamp);
    }

    /// <summary>
    /// Records who did what on the dashboard: requeues, deletes, pauses, imports, jobs run from the Catalog, in
    /// the storage list <c>audit</c> (the newest <see cref="MaxEntries"/>). The Audit page shows it. The user
    /// comes from <see cref="DashboardContext.GetUserName"/>, which the ASP.NET Core adapter answers from
    /// <c>HttpContext.User</c>; the address from the request. <see cref="Enabled"/> turns recording off.
    /// </summary>
    public static class AuditLog
    {
        /// <summary>The storage list the entries live in.</summary>
        public const string ListKey = "audit";

        /// <summary>How many targets of one action are kept by id.</summary>
        public const int MaxTargets = 25;

        /// <summary>How many entries are kept. Default: 1000.</summary>
        public static int MaxEntries { get; set; } = 1000;

        /// <summary>Whether dashboard actions are recorded. Default: on.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>Records an action taken through the given dashboard request. Never throws: a failing storage must not break the action.</summary>
        public static void Record([NotNull] DashboardContext context, [CanBeNull] IEnumerable<string>? targets = null, [CanBeNull] string? detail = null, int status = 204)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!Enabled) return;

            var targetList = targets?.Where(static t => !String.IsNullOrEmpty(t)).ToList();

            var entry = new AuditEntry
            {
                Timestamp = JobHelper.ToTimestamp(Clock()),
                User = Truncate(context.GetUserName(), 200),
                RemoteIpAddress = Truncate(SafeRemoteIp(context), 64),
                Action = context.Request.Path ?? String.Empty,
                Targets = targetList == null || targetList.Count == 0 ? null : targetList.Take(MaxTargets).ToList(),
                TargetCount = targetList != null && targetList.Count > MaxTargets ? targetList.Count : (int?)null,
                Detail = Truncate(detail, 500),
                Status = status,
            };

            try
            {
                using (var connection = context.Storage.GetConnection())
                using (var transaction = connection.CreateWriteTransaction())
                {
                    transaction.InsertToList(ListKey, SerializationHelper.Serialize(entry, SerializationOption.User));
                    transaction.TrimList(ListKey, 0, Math.Max(0, MaxEntries - 1));
                    transaction.Commit();
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // The action already happened; losing its audit line is better than failing the request
            }
        }

        /// <summary>Reads the newest entries, newest first. Needs a <see cref="JobStorageConnection"/>.</summary>
        public static IReadOnlyList<AuditEntry> Read([NotNull] IStorageConnection connection, int count)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

            if (connection is not JobStorageConnection storageConnection) return Array.Empty<AuditEntry>();

            var items = storageConnection.GetRangeFromList(ListKey, 0, count - 1) ?? new List<string>();
            var result = new List<AuditEntry>(items.Count);

            foreach (var item in items)
            {
                try
                {
                    var entry = SerializationHelper.Deserialize<AuditEntry>(item, SerializationOption.User);
                    if (entry != null) result.Add(entry);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    // Skip lines written in a format we don't read
                }
            }

            return result;
        }

        /// <summary>How many entries are kept right now.</summary>
        public static long Count([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            return connection is JobStorageConnection storageConnection ? storageConnection.GetListCount(ListKey) : 0;
        }

        /// <summary>A short English description of a dashboard action path, for example "Requeue failed jobs".</summary>
        public static string Describe([NotNull] string action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            var path = action.Trim('/');
            foreach (var pair in Descriptions)
            {
                if (path.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            }

            return action;
        }

        private static readonly KeyValuePair<string, string>[] Descriptions =
        {
            new KeyValuePair<string, string>("jobs/enqueued/delete-all", "Delete every enqueued job"),
            new KeyValuePair<string, string>("jobs/enqueued/delete", "Delete enqueued jobs"),
            new KeyValuePair<string, string>("jobs/enqueued/requeue", "Requeue enqueued jobs"),
            new KeyValuePair<string, string>("jobs/scheduled/delete-all", "Delete every scheduled job"),
            new KeyValuePair<string, string>("jobs/scheduled/enqueue", "Enqueue scheduled jobs now"),
            new KeyValuePair<string, string>("jobs/scheduled/delete", "Delete scheduled jobs"),
            new KeyValuePair<string, string>("jobs/processing/delete-all", "Delete every processing job"),
            new KeyValuePair<string, string>("jobs/processing/delete", "Delete processing jobs"),
            new KeyValuePair<string, string>("jobs/processing/requeue", "Requeue processing jobs"),
            new KeyValuePair<string, string>("jobs/succeeded/delete-all", "Delete every succeeded job"),
            new KeyValuePair<string, string>("jobs/succeeded/requeue", "Requeue succeeded jobs"),
            new KeyValuePair<string, string>("jobs/failed/delete-all", "Delete every failed job"),
            new KeyValuePair<string, string>("jobs/failed/requeue", "Requeue failed jobs"),
            new KeyValuePair<string, string>("jobs/failed/delete", "Delete failed jobs"),
            new KeyValuePair<string, string>("jobs/deleted/requeue", "Requeue deleted jobs"),
            new KeyValuePair<string, string>("jobs/awaiting/delete-all", "Delete every awaiting job"),
            new KeyValuePair<string, string>("jobs/awaiting/enqueue", "Enqueue awaiting jobs now"),
            new KeyValuePair<string, string>("jobs/awaiting/delete", "Delete awaiting jobs"),
            new KeyValuePair<string, string>("jobs/actions/requeue", "Requeue a job"),
            new KeyValuePair<string, string>("jobs/actions/delete", "Delete a job"),
            new KeyValuePair<string, string>("retries/delete-all", "Delete every retry"),
            new KeyValuePair<string, string>("recurring/remove-all", "Remove every recurring job"),
            new KeyValuePair<string, string>("recurring/remove", "Remove recurring jobs"),
            new KeyValuePair<string, string>("recurring/trigger", "Trigger recurring jobs"),
            new KeyValuePair<string, string>("recurring/pause", "Pause recurring jobs"),
            new KeyValuePair<string, string>("recurring/resume", "Resume recurring jobs"),
            new KeyValuePair<string, string>("recurring/update", "Edit a recurring job"),
            new KeyValuePair<string, string>("recurring/import", "Import recurring jobs"),
            new KeyValuePair<string, string>("maintenance/clear-jobs", "Clear all jobs"),
            new KeyValuePair<string, string>("maintenance/reset", "Reset the storage"),
            new KeyValuePair<string, string>("maintenance/pause", "Pause processing"),
            new KeyValuePair<string, string>("maintenance/resume", "Resume processing"),
            new KeyValuePair<string, string>("queues/pause", "Pause a queue"),
            new KeyValuePair<string, string>("queues/resume", "Resume a queue"),
            new KeyValuePair<string, string>("catalog/run", "Run a job from the catalog"),
            new KeyValuePair<string, string>("insights/circuits/reset", "Close a circuit"),
            new KeyValuePair<string, string>("tags/forget", "Forget a tag"),
            new KeyValuePair<string, string>("batches/requeue-failed", "Requeue a batch's failed jobs"),
            new KeyValuePair<string, string>("batches/delete", "Delete a batch's pending jobs"),
        };

        private static string? SafeRemoteIp(DashboardContext context)
        {
            try
            {
                return context.Request.RemoteIpAddress;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return null;
            }
        }

        private static string? Truncate(string? value, int max)
        {
            if (String.IsNullOrEmpty(value)) return null;
            return value!.Length <= max ? value : value.Substring(0, max);
        }
    }
}
