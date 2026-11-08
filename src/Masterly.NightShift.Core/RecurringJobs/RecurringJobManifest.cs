#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.Json;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.States;
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

        /// <summary>
        /// The queue the job is enqueued to; the default queue when omitted. Export omits the default queue.
        /// </summary>
        [JsonInclude, JsonPropertyName("queue"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Queue { get; set; }

        /// <summary>
        /// <c>true</c> to pause the job, <c>false</c> to resume it, <c>null</c> (omitted) to leave its paused
        /// state as it is. Export always writes it.
        /// </summary>
        [JsonInclude, JsonPropertyName("paused"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Paused { get; set; }

        /// <summary>How missed runs are handled.</summary>
        [JsonInclude, JsonPropertyName("misfireHandling"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public MisfireHandlingMode MisfireHandling { get; set; }

        /// <summary>
        /// The instant before which the job doesn't run (<see cref="RecurringJobOptions.StartAt"/>); no start when
        /// omitted. Written in UTC (<c>2026-12-01T00:00:00Z</c>); read as ISO 8601, a value without an offset is UTC.
        /// </summary>
        [JsonInclude, JsonPropertyName("startAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? StartAt { get; set; }

        /// <summary>
        /// The instant after which the job doesn't run (<see cref="RecurringJobOptions.EndAt"/>); no end when
        /// omitted. Written and read like <see cref="StartAt"/>.
        /// </summary>
        [JsonInclude, JsonPropertyName("endAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? EndAt { get; set; }

        /// <summary>The method to call: type, method name, parameter types and arguments as the storage keeps them.</summary>
        [JsonInclude, JsonPropertyName("job")]
        public RecurringJobManifestJob Job { get; set; } = new RecurringJobManifestJob();
    }

    /// <summary>The method of a manifest entry, in the storage's own portable format.</summary>
    public sealed class RecurringJobManifestJob
    {
        /// <summary>
        /// The type, as the configured type serializer writes it. Jobs whose type or method can't be loaded
        /// where the export runs are written as stored.
        /// </summary>
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
    ///     { "id": "nightly-report", "cron": "30 2 * * *", "timeZone": "Europe/Berlin", "paused": false,
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
            options.Converters.Add(new UtcDateTimeOffsetConverter());
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

        /// <summary>
        /// Exports every recurring job as a JSON document. A job whose type or method can't be loaded here
        /// (a dashboard-only application, for example) is written as stored. A job without any stored method
        /// call is left out, with a warning in the log.
        /// </summary>
        public static string Export([NotNull] IStorageConnection connection)
        {
            return Export(connection, maskRedacted: false);
        }

        /// <summary>
        /// Exports every recurring job as a JSON document, like <see cref="Export(IStorageConnection)"/>. With
        /// <paramref name="maskRedacted"/>, the values of <see cref="RedactedAttribute"/> parameters are written
        /// as <c>null</c>, and a job whose method can't be loaded here (so its redacted parameters are unknown) is
        /// written without arguments. The dashboard does this for read-only users. Such a document is for reading:
        /// applying it would register the jobs without those values.
        /// </summary>
        public static string Export([NotNull] IStorageConnection connection, bool maskRedacted)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var logger = LogProvider.GetLogger(typeof(RecurringJobManifest));
            var entries = new List<RecurringJobManifestEntry>();

            foreach (var dto in connection.GetRecurringJobs().Where(static dto => !dto.Removed).OrderBy(static dto => dto.Id, StringComparer.Ordinal))
            {
                var entry = ToEntry(connection, dto, logger, maskRedacted);
                if (entry != null) entries.Add(entry);
            }

            return JsonSerializer.Serialize(new ManifestDocument { Version = CurrentVersion, RecurringJobs = entries }, Settings);
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

        /// <summary>
        /// Applies a manifest to <see cref="JobStorage.Current"/> through a new <see cref="RecurringJobManager"/>,
        /// accepting only the methods the application already uses as jobs (see
        /// <see cref="Apply(IRecurringJobManager, IStorageConnection, string, bool, bool)"/>).
        /// </summary>
        public static RecurringJobManifestResult Apply([NotNull] string json, bool prune = false)
        {
            return Apply(json, prune, allowAnyMethod: false);
        }

        /// <summary>Applies a manifest to <see cref="JobStorage.Current"/> through a new <see cref="RecurringJobManager"/>.</summary>
        /// <param name="json">The manifest document.</param>
        /// <param name="prune"><c>true</c> to remove the recurring jobs the manifest doesn't mention.</param>
        /// <param name="allowAnyMethod">
        /// <c>true</c> to accept an entry for any public method the application can load, not only the methods it
        /// already uses as jobs.
        /// </param>
        public static RecurringJobManifestResult Apply([NotNull] string json, bool prune, bool allowAnyMethod)
        {
            using (var connection = JobStorage.Current.GetConnection())
            {
                return Apply(new RecurringJobManager(JobStorage.Current), connection, json, prune, allowAnyMethod);
            }
        }

        /// <summary>
        /// Registers every entry of the manifest, as <see cref="Apply(IRecurringJobManager, IStorageConnection, string, bool, bool)"/>
        /// does, accepting only the methods the application already uses as jobs.
        /// </summary>
        public static RecurringJobManifestResult Apply([NotNull] IRecurringJobManager manager, [NotNull] IStorageConnection connection, [NotNull] string json, bool prune = false)
        {
            return Apply(manager, connection, json, prune, allowAnyMethod: false);
        }

        /// <summary>
        /// Registers every entry of the manifest, pausing or resuming it when the entry has a <c>paused</c>
        /// field (an entry without one keeps the job's paused state), and, with <paramref name="prune"/>,
        /// removes the recurring jobs the manifest doesn't mention. Entries that fail (unknown type, a method the
        /// application doesn't use as a job, bad CRON, unknown time zone, unknown misfire handling) are reported
        /// and don't stop the others.
        /// </summary>
        /// <param name="manager">Registers, pauses, resumes and removes the jobs.</param>
        /// <param name="connection">A connection to the manager's storage, used to list the existing jobs.</param>
        /// <param name="json">The manifest document.</param>
        /// <param name="prune"><c>true</c> to remove the recurring jobs the manifest doesn't mention.</param>
        /// <param name="allowAnyMethod">
        /// <c>false</c> (the default of the other overloads) to accept an entry only when its type and method are
        /// already used as a job by the application: the method of an existing recurring job, a
        /// <see cref="JobCatalog"/> entry, or a method with a generated job invoker (any method used in an
        /// <c>Enqueue</c>, <c>Schedule</c> or <c>AddOrUpdate</c> call compiled with the source generator, and every
        /// public method of a type marked with <see cref="GenerateJobInvokersAttribute"/>). Other entries are
        /// reported as errors, so a manifest can't turn an arbitrary public method into a job. <c>true</c>
        /// accepts any public method the application can load.
        /// </param>
        /// <remarks>
        /// Time zones are resolved with the manager's <see cref="ITimeZoneResolver"/> when it is a
        /// <see cref="RecurringJobManager"/>, otherwise with the one set by
        /// <see cref="GlobalConfigurationExtensions.UseTimeZoneResolver"/>. A queue other than the default one
        /// becomes part of the job, or, when the manager's storage doesn't support per-job queues, the
        /// recurring job's own queue.
        /// </remarks>
        public static RecurringJobManifestResult Apply([NotNull] IRecurringJobManager manager, [NotNull] IStorageConnection connection, [NotNull] string json, bool prune, bool allowAnyMethod)
        {
            if (manager == null) throw new ArgumentNullException(nameof(manager));
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var entries = Parse(json);
            var recurringJobs = connection.GetRecurringJobs().Where(static dto => !dto.Removed).ToList();
            var existing = new HashSet<string>(recurringJobs.Select(static dto => dto.Id), StringComparer.Ordinal);
            var knownJobMethods = allowAnyMethod ? null : new KnownJobMethods(recurringJobs);
            var resolver = (manager as RecurringJobManager)?.TimeZoneResolver ?? GlobalTimeZoneResolver.Current;
            var storage = (manager as IRecurringJobManagerV2)?.Storage;
            var jobQueues = storage == null || storage.HasFeature(JobStorageFeatures.JobQueueProperty);

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

                if (!Enum.IsDefined(typeof(MisfireHandlingMode), entry.MisfireHandling))
                {
                    errors.Add(new RecurringJobManifestError(id, $"\"misfireHandling\" {(int)entry.MisfireHandling} is not defined. Use 0 (Relaxed), 1 (Strict) or 2 (Ignorable)."));
                    continue;
                }

                try
                {
                    var queue = GetExplicitQueue(entry.Queue);
                    var job = ToJob(entry, jobQueues ? queue : null);

                    if (knownJobMethods != null && !knownJobMethods.Contains(job))
                    {
                        errors.Add(new RecurringJobManifestError(id,
                            $"{job.Type.ToGenericTypeString()}.{job.Method.Name} is not a method this application uses as a job: no recurring job, Job Catalog entry or generated job invoker has it. " +
                            "To accept any method, apply the manifest with allowAnyMethod: true (DashboardOptions.AllowAnyMethodInManifestImport for the dashboard's Import)."));
                        continue;
                    }

                    var options = new RecurringJobOptions
                    {
                        TimeZone = String.IsNullOrWhiteSpace(entry.TimeZone) ? TimeZoneInfo.Utc : resolver.GetTimeZoneById(entry.TimeZone!.Trim()),
                        MisfireHandling = entry.MisfireHandling,
                        StartAt = entry.StartAt,
                        EndAt = entry.EndAt,
                    };

                    if (queue != null && !jobQueues)
                    {
                        // The recurring job's own queue works with every storage
#pragma warning disable 618
                        options.QueueName = queue;
#pragma warning restore 618
                    }

                    manager.AddOrUpdate(id, job, entry.Cron, options);

                    if (entry.Paused.HasValue && manager is IRecurringJobManagerV3 managerV3)
                    {
                        if (entry.Paused.Value) managerV3.Pause(id);
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

        private static RecurringJobManifestEntry? ToEntry(IStorageConnection connection, RecurringJobDto dto, ILog logger, bool maskRedacted)
        {
            InvocationData? invocationData;

            if (dto.Job != null)
            {
                invocationData = InvocationData.SerializeJob(maskRedacted ? MaskRedacted(dto.Job) : dto.Job);
            }
            else
            {
                // The type or method can't be loaded here. Leaving the job out would also get it pruned when
                // the manifest is applied with pruning, so the method call is written as stored instead.
                invocationData = GetStoredInvocationData(connection, dto.Id, logger);

                if (invocationData == null)
                {
                    logger.Warn($"Recurring job '{dto.Id}' is left out of the manifest, because it has no method call that can be read.");
                    return null;
                }

                logger.Debug($"Recurring job '{dto.Id}' is exported as stored, because its type or method can't be loaded: {dto.LoadException?.Message}");
            }

            // The queue the runs go to: the recurring job's own queue wins over the job's, unless it is the default one
            var queue = GetExplicitQueue(dto.Queue) ?? GetExplicitQueue(invocationData.Queue);

            return new RecurringJobManifestEntry
            {
                Id = dto.Id,
                Cron = dto.Cron,
                TimeZone = String.IsNullOrEmpty(dto.TimeZoneId) || dto.TimeZoneId == TimeZoneInfo.Utc.Id ? null : dto.TimeZoneId,
                Queue = queue,
                Paused = dto.Paused,
                MisfireHandling = dto.MisfireHandling,
                StartAt = ToUtcOffset(dto.StartAt),
                EndAt = ToUtcOffset(dto.EndAt),
                Job = new RecurringJobManifestJob
                {
                    Type = invocationData.Type,
                    Method = invocationData.Method,
                    ParameterTypes = invocationData.ParameterTypes,
                    Arguments = maskRedacted && dto.Job == null ? null : invocationData.Arguments,
                },
            };
        }

        private static DateTimeOffset? ToUtcOffset(DateTime? utc)
        {
            return utc.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc)) : (DateTimeOffset?)null;
        }

        private static Job MaskRedacted(Job job)
        {
            var parameters = job.Method.GetParameters();
            var arguments = job.Args.ToArray();

            for (var i = 0; i < parameters.Length && i < arguments.Length; i++)
            {
                if (RedactedAttribute.IsRedacted(parameters[i]))
                {
                    var type = parameters[i].ParameterType;
                    arguments[i] = type.GetTypeInfo().IsValueType ? Activator.CreateInstance(type) : null;
                }
            }

            return new Job(job.Type, job.Method, arguments, job.Queue);
        }

        private static InvocationData? GetStoredInvocationData(IStorageConnection connection, string recurringJobId, ILog logger)
        {
            try
            {
                var hash = connection.GetAllEntriesFromHash($"recurring-job:{recurringJobId}");
                if (hash == null || !hash.TryGetValue("Job", out var payload) || String.IsNullOrWhiteSpace(payload)) return null;

                var stored = InvocationData.DeserializePayload(payload);
                if (String.IsNullOrWhiteSpace(stored.Type) || String.IsNullOrWhiteSpace(stored.Method)) return null;

                // A compact payload of a method without parameters has no arrays, which read back as "null"
                return new InvocationData(stored.Type, stored.Method, ArrayOrEmpty(stored.ParameterTypes), ArrayOrEmpty(stored.Arguments), stored.Queue);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                logger.DebugException($"The stored method call of recurring job '{recurringJobId}' can't be read.", ex);
                return null;
            }
        }

        private static string ArrayOrEmpty(string? json)
        {
            return String.IsNullOrWhiteSpace(json) || json == "null" ? "[]" : json!;
        }

        /// <summary>The queue name, or <c>null</c> when it is empty or the default queue.</summary>
        private static string? GetExplicitQueue(string? queue)
        {
            if (String.IsNullOrWhiteSpace(queue)) return null;

            var trimmed = queue!.Trim();
            return EnqueuedState.DefaultQueue.Equals(trimmed, StringComparison.OrdinalIgnoreCase) ? null : trimmed;
        }

        internal static Job ToJob(RecurringJobManifestEntry entry, string? queue)
        {
            if (entry.Job == null || String.IsNullOrWhiteSpace(entry.Job.Type) || String.IsNullOrWhiteSpace(entry.Job.Method))
            {
                throw new FormatException("The entry needs a \"job\" with \"type\" and \"method\".");
            }

            if (String.IsNullOrWhiteSpace(entry.Cron))
            {
                throw new FormatException("The entry needs a \"cron\" expression.");
            }

            var invocationData = new InvocationData(entry.Job.Type, entry.Job.Method, entry.Job.ParameterTypes ?? "[]", entry.Job.Arguments ?? "[]", queue);

            return invocationData.DeserializeJob();
        }

        /// <summary>
        /// The methods the application already uses as jobs: those of the existing recurring jobs, the
        /// <see cref="JobCatalog"/> entries and the methods with a generated invoker.
        /// </summary>
        private sealed class KnownJobMethods
        {
            private readonly List<RecurringJobDto> _recurringJobs;
            private IReadOnlyList<JobCatalogEntry>? _catalog;

            public KnownJobMethods(List<RecurringJobDto> recurringJobs)
            {
                _recurringJobs = recurringJobs;
            }

            public bool Contains(Job job)
            {
                // A generated invoker is registered for the type that declares the method
                if (job.Type == job.Method.DeclaringType && JobInvokerRegistry.TryGetGeneratedInvoker(job.Method, out _))
                {
                    return true;
                }

                foreach (var recurringJob in _recurringJobs)
                {
                    if (recurringJob.Job != null && recurringJob.Job.Type == job.Type && IsSameMethod(recurringJob.Job.Method, job.Method))
                    {
                        return true;
                    }
                }

                // Read once, and only when needed: it reflects over every catalog type
                _catalog ??= JobCatalog.GetEntries();

                foreach (var entry in _catalog)
                {
                    if (entry.Type == job.Type && IsSameMethod(entry.Method, job.Method)) return true;
                }

                return false;
            }

            private static bool IsSameMethod(MethodInfo left, MethodInfo right)
            {
                if (left.Equals(right)) return true;

                if (left.DeclaringType != right.DeclaringType || !String.Equals(left.Name, right.Name, StringComparison.Ordinal))
                {
                    return false;
                }

                var leftTypes = left.GetParameters().Select(static p => p.ParameterType);
                var rightTypes = right.GetParameters().Select(static p => p.ParameterType);
                var leftArguments = left.IsGenericMethod ? left.GetGenericArguments() : Type.EmptyTypes;
                var rightArguments = right.IsGenericMethod ? right.GetGenericArguments() : Type.EmptyTypes;

                return leftTypes.SequenceEqual(rightTypes) && leftArguments.SequenceEqual(rightArguments);
            }
        }

        // Dates are written in UTC, and a hand-written date without an offset means UTC, not the local time of
        // whichever machine applies the manifest
        private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
        {
            private const string Format = "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFF'Z'";

            public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

                if (String.IsNullOrWhiteSpace(text) ||
                    !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
                {
                    throw new JsonException($"'{text}' is not a date. Use ISO 8601, for example \"2026-12-31T23:00:00Z\".");
                }

                return value;
            }

            public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
            }
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
