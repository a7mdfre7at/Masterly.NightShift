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
using System.Text.Json.Serialization;
using System.Text.Json;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
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
    /// trimmed to <see cref="MaxEntries"/>, written by <see cref="ActivityFeedFilter"/> and shown live on the
    /// dashboard's home page. Set <see cref="Enabled"/> to <c>false</c> to stop recording.
    /// </summary>
    public static class ActivityFeed
    {
        /// <summary>The storage list key.</summary>
        public const string ListKey = "activity";

        /// <summary>Whether transitions are recorded. Default <c>true</c>.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>How many entries the list keeps. Default 200.</summary>
        public static int MaxEntries { get; set; } = 1000;

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>Reads the newest entries, newest first.</summary>
        public static IReadOnlyList<ActivityEntry> Read([NotNull] IStorageConnection connection, int count)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (count <= 0) return Array.Empty<ActivityEntry>();

            if (connection is not JobStorageConnection storageConnection) return Array.Empty<ActivityEntry>();

            var result = new List<ActivityEntry>(count);

            foreach (var serialized in storageConnection.GetRangeFromList(ListKey, 0, count - 1))
            {
                ActivityEntry? entry;
                try
                {
                    entry = SerializationHelper.Deserialize<ActivityEntry>(serialized, SerializationOption.User);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (entry != null) result.Add(entry);
            }

            return result;
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
    /// Records every applied state in <see cref="ActivityFeed"/>: two list operations inside the state transaction.
    /// Registered globally by default.
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

            transaction.InsertToList(ActivityFeed.ListKey, SerializationHelper.Serialize(entry, SerializationOption.User));
            transaction.TrimList(ActivityFeed.ListKey, 0, Math.Max(0, ActivityFeed.MaxEntries - 1));
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }
    }
}
