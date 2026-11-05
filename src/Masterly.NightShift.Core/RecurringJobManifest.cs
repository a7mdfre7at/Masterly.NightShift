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
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.Json;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>One recurring job in a <see cref="RecurringJobManifest"/>.</summary>
    public sealed class RecurringJobManifestEntry
    {
        /// <summary>The recurring job identifier.</summary>
        [JsonInclude, JsonPropertyName("id")]
        public string Id { get; set; } = String.Empty;

        /// <summary>The CRON expression.</summary>
        [JsonInclude, JsonPropertyName("cron")]
        public string Cron { get; set; } = String.Empty;

        /// <summary>The time zone identifier; UTC when omitted.</summary>
        [JsonInclude, JsonPropertyName("timeZone"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TimeZone { get; set; }

        /// <summary>The queue the job is enqueued to; the default queue when omitted.</summary>
        [JsonInclude, JsonPropertyName("queue"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Queue { get; set; }

        /// <summary>Whether the job is paused.</summary>
        [JsonInclude, JsonPropertyName("paused"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Paused { get; set; }

        /// <summary>How missed runs are handled.</summary>
        [JsonInclude, JsonPropertyName("misfireHandling"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public MisfireHandlingMode MisfireHandling { get; set; }

        /// <summary>The method to call: type, method name, parameter types and arguments as the storage keeps them.</summary>
        [JsonInclude, JsonPropertyName("job")]
        public RecurringJobManifestJob Job { get; set; } = new RecurringJobManifestJob();
    }

    /// <summary>The method of a manifest entry, in the storage's own portable format.</summary>
    public sealed class RecurringJobManifestJob
    {
        /// <summary>The type, as the configured type serializer writes it.</summary>
        [JsonInclude, JsonPropertyName("type")]
        public string Type { get; set; } = String.Empty;

        /// <summary>The method name.</summary>
        [JsonInclude, JsonPropertyName("method")]
        public string Method { get; set; } = String.Empty;

        /// <summary>The parameter types, a JSON array of type names.</summary>
        [JsonInclude, JsonPropertyName("parameterTypes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ParameterTypes { get; set; }

        /// <summary>The arguments, a JSON array of serialized values.</summary>
        [JsonInclude, JsonPropertyName("arguments"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Arguments { get; set; }
    }

    /// <summary>A recurring job the manifest could not apply.</summary>
    public sealed class RecurringJobManifestError
    {
        internal RecurringJobManifestError(string id, string message)
        {
            Id = id;
            Message = message;
        }

        /// <summary>The recurring job identifier, or <c>?</c> when the entry had none.</summary>
        public string Id { get; }

        /// <summary>What went wrong.</summary>
        public string Message { get; }
    }

    /// <summary>What <see cref="RecurringJobManifest.Apply(IRecurringJobManager, IStorageConnection, string, bool)"/> did.</summary>
    public sealed class RecurringJobManifestResult
    {
        internal RecurringJobManifestResult(IReadOnlyList<string> added, IReadOnlyList<string> updated, IReadOnlyList<string> removed, IReadOnlyList<RecurringJobManifestError> errors)
        {
            Added = added;
            Updated = updated;
            Removed = removed;
            Errors = errors;
        }

        /// <summary>Recurring jobs that did not exist before.</summary>
        public IReadOnlyList<string> Added { get; }

        /// <summary>Recurring jobs that existed and were re-registered from the manifest.</summary>
        public IReadOnlyList<string> Updated { get; }

        /// <summary>Recurring jobs removed because they were not in the manifest (pruning only).</summary>
        public IReadOnlyList<string> Removed { get; }

        /// <summary>Entries that could not be applied.</summary>
        public IReadOnlyList<RecurringJobManifestError> Errors { get; }
    }

    /// <summary>
    /// Exports every recurring job to a JSON document and applies such a document back, so schedules can live in
    /// source control and be promoted between environments like any other configuration. The dashboard's
    /// Recurring Jobs page has Export and Import buttons for the same document.
    /// </summary>
    /// <remarks>
    /// <code>
    /// {
    ///   "version": 1,
    ///   "recurringJobs": [
    ///     { "id": "nightly-report", "cron": "30 2 * * *", "timeZone": "UTC",
    ///       "job": { "type": "App.Jobs, App", "method": "Report", "parameterTypes": "[\"System.String\"]", "arguments": "[\"\\\"nightly\\\"\"]" } }
    ///   ]
    /// }
    /// </code>
    /// Jobs are written in the storage's own format, so a manifest exported from one environment applies to
    /// another running the same code. Applying is idempotent: an unchanged entry keeps its next run.
    /// </remarks>
    public static class RecurringJobManifest
    {
        /// <summary>The document format version written by <see cref="Export(IStorageConnection)"/>.</summary>
        public const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions Settings = CreateSettings();

        private static JsonSerializerOptions CreateSettings()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            options.Converters.Add(new LenientEnumConverterFactory());
            return options;
        }

        /// <summary>Exports the recurring jobs of <see cref="JobStorage.Current"/>.</summary>
        public static string Export()
        {
            using (var connection = JobStorage.Current.GetReadOnlyConnection())
            {
                return Export(connection);
            }
        }

        /// <summary>Exports every recurring job as a JSON document.</summary>
        public static string Export([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var entries = connection.GetRecurringJobs()
                .Where(static dto => !dto.Removed)
                .OrderBy(static dto => dto.Id, StringComparer.Ordinal)
                .Select(ToEntry)
                .Where(static entry => entry != null)
                .ToList();

            return JsonSerializer.Serialize(new ManifestDocument { Version = CurrentVersion, RecurringJobs = entries! }, Settings);
        }

        /// <summary>Reads a manifest document without applying it.</summary>
        public static IReadOnlyList<RecurringJobManifestEntry> Parse([NotNull] string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));

            ManifestDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<ManifestDocument>(json, Settings);
            }
            catch (JsonException ex)
            {
                throw new FormatException($"The manifest is not valid JSON: {ex.Message}", ex);
            }

            if (document == null || document.RecurringJobs == null)
            {
                throw new FormatException("The manifest must be an object with a \"recurringJobs\" array.");
            }

            if (document.Version > CurrentVersion)
            {
                throw new FormatException($"The manifest version {document.Version} is newer than this Masterly.NightShift understands ({CurrentVersion}).");
            }

            return document.RecurringJobs;
        }

        /// <summary>Applies a manifest to <see cref="JobStorage.Current"/> through a new <see cref="RecurringJobManager"/>.</summary>
        public static RecurringJobManifestResult Apply([NotNull] string json, bool prune = false)
        {
            using (var connection = JobStorage.Current.GetConnection())
            {
                return Apply(new RecurringJobManager(JobStorage.Current), connection, json, prune);
            }
        }

        /// <summary>
        /// Registers every entry of the manifest, pausing or resuming it as the manifest says, and, with
        /// <paramref name="prune"/>, removes the recurring jobs the manifest doesn't mention. Entries that
        /// fail (unknown type, bad CRON, unknown time zone) are reported and don't stop the others.
        /// </summary>
        public static RecurringJobManifestResult Apply([NotNull] IRecurringJobManager manager, [NotNull] IStorageConnection connection, [NotNull] string json, bool prune = false)
        {
            if (manager == null) throw new ArgumentNullException(nameof(manager));
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var entries = Parse(json);
            var existing = new HashSet<string>(connection.GetRecurringJobs().Where(static dto => !dto.Removed).Select(static dto => dto.Id), StringComparer.Ordinal);
            var resolver = new DefaultTimeZoneResolver();

            var added = new List<string>();
            var updated = new List<string>();
            var removed = new List<string>();
            var errors = new List<RecurringJobManifestError>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                if (entry == null) continue;

                if (String.IsNullOrWhiteSpace(entry.Id))
                {
                    errors.Add(new RecurringJobManifestError("?", "An entry has no \"id\"."));
                    continue;
                }

                var id = entry.Id.Trim();

                if (!seen.Add(id))
                {
                    errors.Add(new RecurringJobManifestError(id, "The id appears more than once."));
                    continue;
                }

                try
                {
                    var job = ToJob(entry);
                    var options = new RecurringJobOptions
                    {
                        TimeZone = String.IsNullOrWhiteSpace(entry.TimeZone) ? TimeZoneInfo.Utc : resolver.GetTimeZoneById(entry.TimeZone!.Trim()),
                        MisfireHandling = entry.MisfireHandling,
                    };

                    manager.AddOrUpdate(id, job, entry.Cron, options);

                    if (manager is IRecurringJobManagerV3 managerV3)
                    {
                        if (entry.Paused) managerV3.Pause(id);
                        else managerV3.Resume(id);
                    }

                    if (existing.Contains(id)) updated.Add(id);
                    else added.Add(id);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    errors.Add(new RecurringJobManifestError(id, ex.InnerException != null ? $"{ex.Message} {ex.InnerException.Message}" : ex.Message));
                }
            }

            if (prune)
            {
                foreach (var id in existing.Where(id => !seen.Contains(id)).OrderBy(static id => id, StringComparer.Ordinal))
                {
                    manager.RemoveIfExists(id);
                    removed.Add(id);
                }
            }

            return new RecurringJobManifestResult(added, updated, removed, errors);
        }

        internal static RecurringJobManifestEntry? ToEntry(RecurringJobDto dto)
        {
            var job = dto.Job;
            if (job == null) return null;

            var invocationData = InvocationData.SerializeJob(job);

            return new RecurringJobManifestEntry
            {
                Id = dto.Id,
                Cron = dto.Cron,
                TimeZone = String.IsNullOrEmpty(dto.TimeZoneId) || dto.TimeZoneId == TimeZoneInfo.Utc.Id ? null : dto.TimeZoneId,
                Queue = job.Queue ?? (String.IsNullOrEmpty(dto.Queue) ? null : dto.Queue),
                Paused = dto.Paused,
                MisfireHandling = dto.MisfireHandling,
                Job = new RecurringJobManifestJob
                {
                    Type = invocationData.Type,
                    Method = invocationData.Method,
                    ParameterTypes = invocationData.ParameterTypes,
                    Arguments = invocationData.Arguments,
                },
            };
        }

        internal static Job ToJob(RecurringJobManifestEntry entry)
        {
            if (entry.Job == null || String.IsNullOrWhiteSpace(entry.Job.Type) || String.IsNullOrWhiteSpace(entry.Job.Method))
            {
                throw new FormatException("The entry needs a \"job\" with \"type\" and \"method\".");
            }

            if (String.IsNullOrWhiteSpace(entry.Cron))
            {
                throw new FormatException("The entry needs a \"cron\" expression.");
            }

            var queue = String.IsNullOrWhiteSpace(entry.Queue) ? null : entry.Queue!.Trim();
            var invocationData = new InvocationData(entry.Job.Type, entry.Job.Method, entry.Job.ParameterTypes ?? "[]", entry.Job.Arguments ?? "[]", queue);

            return invocationData.DeserializeJob();
        }

        private sealed class ManifestDocument
        {
            [JsonInclude, JsonPropertyName("version")]
            public int Version { get; set; } = CurrentVersion;

            [JsonInclude, JsonPropertyName("recurringJobs")]
            public List<RecurringJobManifestEntry>? RecurringJobs { get; set; }
        }
    }
}
