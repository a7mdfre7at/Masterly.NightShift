#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.Json;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>One state transition, as kept in the activity feed.</summary>
    public sealed class ActivityEntry
    {
        /// <summary>Unix time in milliseconds.</summary>
        [JsonInclude, JsonPropertyName("t")]
        public long Timestamp { get; set; }

        /// <summary>The job identifier.</summary>
        [JsonInclude, JsonPropertyName("j")]
        public string? JobId { get; set; }

        /// <summary>The state the job entered.</summary>
        [JsonInclude, JsonPropertyName("s")]
        public string? State { get; set; }

        /// <summary>The state the job left, if any.</summary>
        [JsonInclude, JsonPropertyName("o"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OldState { get; set; }

        /// <summary><c>Type.Method</c> of the job, when it could be loaded.</summary>
        [JsonInclude, JsonPropertyName("m"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Method { get; set; }

        /// <summary>The reason attached to the new state, if any.</summary>
        [JsonInclude, JsonPropertyName("r"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; set; }

        /// <summary>The server that performs or performed the job, when the state knows it.</summary>
        [JsonInclude, JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ServerId { get; set; }

        /// <summary>The worker that took the job, for Processing transitions.</summary>
        [JsonInclude, JsonPropertyName("w"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? WorkerId { get; set; }
    }

    /// <summary>
    /// A rolling log of the latest state transitions across the whole cluster: the storage list <c>activity</c>,
    /// about <see cref="MaxEntries"/> long, written by <see cref="ActivityFeedFilter"/> and shown live on the
    /// dashboard's home page and Timeline. Off by default: turn it on with
    /// <c>GlobalConfiguration.Configuration.UseActivityFeed()</c>, or by setting <see cref="Enabled"/>, in every
    /// process that changes job states.
    /// </summary>
    /// <remarks>
    /// Entries are written after the state change commits, in batches and in transactions of their own, so the
    /// feed never slows down or fails a state change. The price: an entry can be missing when its write fails or
    /// the process stops before writing it, and the list can hold a few more than <see cref="MaxEntries"/> entries
    /// between two trims.
    /// </remarks>
    public static class ActivityFeed
    {
        /// <summary>The storage list key.</summary>
        public const string ListKey = "activity";

        /// <summary>
        /// Whether transitions are recorded. Default <c>false</c>; <c>UseActivityFeed()</c> sets it to <c>true</c>.
        /// </summary>
        public static bool Enabled { get; set; }

        /// <summary>How many entries the list keeps. Default 1000.</summary>
        public static int MaxEntries { get; set; } = 1000;

        private static readonly ILog Logger = LogProvider.GetLogger(typeof(ActivityFeed));

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>
        /// Reads the newest entries, newest first. Returns nothing when the connection isn't a
        /// <see cref="JobStorageConnection"/> or the storage can't read lists.
        /// </summary>
        public static IReadOnlyList<ActivityEntry> Read([NotNull] IStorageConnection connection, int count)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (count <= 0) return Array.Empty<ActivityEntry>();

            if (connection is not JobStorageConnection storageConnection) return Array.Empty<ActivityEntry>();

            List<string> items;
            try
            {
                items = storageConnection.GetRangeFromList(ListKey, 0, count - 1) ?? new List<string>();
            }
            catch (NotSupportedException)
            {
                // A storage without the extended API (JobStorageFeatures.ExtendedApi) can't read lists
                return Array.Empty<ActivityEntry>();
            }

            var result = new List<ActivityEntry>(items.Count);

            foreach (var serialized in items)
            {
                ActivityEntry? entry;
                try
                {
                    entry = MonitoringJson.Deserialize<ActivityEntry>(serialized);
                }
                catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
                {
                    continue;
                }

                if (entry != null) result.Add(entry);
            }

            return result;
        }

        /// <summary>Queues an entry for the storage. Never throws.</summary>
        internal static void Record(JobStorage storage, ActivityEntry entry)
        {
            try
            {
                ActivityFeedWriter.For(storage).Add(MonitoringJson.Serialize(entry));
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Could not record the transition of job '{entry.JobId}' to {entry.State} in the activity feed.", ex);
            }
        }

        internal static ActivityEntry Create(BackgroundJob backgroundJob, IState newState, string? oldStateName)
        {
            var entry = new ActivityEntry
            {
                Timestamp = (long)(Clock() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
                JobId = backgroundJob.Id,
                State = newState.Name,
                OldState = oldStateName,
                Reason = String.IsNullOrEmpty(newState.Reason) ? null : newState.Reason,
            };

            if (backgroundJob.Job != null)
            {
                entry.Method = $"{backgroundJob.Job.Type.Name}.{backgroundJob.Job.Method.Name}";
            }

            if (newState is ProcessingState processing)
            {
                entry.ServerId = processing.ServerId;
                entry.WorkerId = processing.WorkerId;
            }

            return entry;
        }
    }

    /// <summary>
    /// Records every applied state in <see cref="ActivityFeed"/> while <see cref="ActivityFeed.Enabled"/> is
    /// <c>true</c>. It writes nothing into the state change's transaction: the entry is queued once the state change
    /// commits and written in the background. Registered globally by default; it does nothing until the feed is
    /// turned on.
    /// </summary>
    public sealed class ActivityFeedFilter : JobFilterAttribute, IApplyStateFilter
    {
        /// <inheritdoc />
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (!ActivityFeed.Enabled) return;

            var entry = ActivityFeed.Create(context.BackgroundJob, context.NewState, context.OldStateName);
            var storage = context.Storage;

            context.RunAfterCommit(() => ActivityFeed.Record(storage, entry));
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }
    }
}
