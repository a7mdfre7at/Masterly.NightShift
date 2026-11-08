#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>A note left on a job from the dashboard.</summary>
    public sealed class JobNote
    {
        /// <summary>When, as a Unix timestamp in seconds.</summary>
        [JsonInclude, JsonPropertyName("t")]
        public long Timestamp { get; set; }

        /// <summary>Who wrote it, when the host knows.</summary>
        [JsonInclude, JsonPropertyName("u"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? User { get; set; }

        /// <summary>The text.</summary>
        [JsonInclude, JsonPropertyName("m")]
        public string Text { get; set; } = String.Empty;

        /// <summary>Whether the note acknowledges the job's failure.</summary>
        [JsonInclude, JsonPropertyName("a"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Acknowledges { get; set; }

        /// <summary>When, as UTC.</summary>
        [JsonIgnore]
        public DateTime WrittenAt => JobHelper.FromTimestamp(Timestamp);
    }

    /// <summary>
    /// Notes and acknowledgements on jobs, written from the job details page and kept in the <c>Notes</c> job
    /// parameter: "known issue, partner API down, retry after 15:00", "acknowledged, ticket OPS-412". A failed job
    /// with an acknowledging note shows as acknowledged on the Failed jobs page, so the team sees what is already
    /// being looked at. The JSON is written with options of the library's own, never with the application's
    /// serializer settings.
    /// </summary>
    public static class JobNotes
    {
        /// <summary>The job parameter holding the notes as a JSON array.</summary>
        public const string ParameterName = "Notes";

        /// <summary>The most notes kept per job; older ones are dropped.</summary>
        public const int MaxNotes = 50;

        /// <summary>The longest note accepted.</summary>
        public const int MaxLength = 2000;

        /// <summary>How long <see cref="Add"/> waits for another note on the same job to be written.</summary>
        internal static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>
        /// Adds a note. Returns the note as stored. Notes added to the same job at the same time are written one
        /// after the other, under a lock of their own; <see cref="DistributedLockTimeoutException"/> is thrown when
        /// the lock can't be taken within 15 seconds.
        /// </summary>
        public static JobNote Add([NotNull] IStorageConnection connection, [NotNull] string jobId, [NotNull] string text, [CanBeNull] string? user = null, bool acknowledges = false)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            if (text == null) throw new ArgumentNullException(nameof(text));

            text = text.Trim();
            if (text.Length == 0 && !acknowledges) throw new ArgumentException("A note needs some text.", nameof(text));
            if (text.Length > MaxLength) text = text.Substring(0, MaxLength);

            var note = new JobNote
            {
                Timestamp = JobHelper.ToTimestamp(Clock()),
                User = String.IsNullOrWhiteSpace(user) ? null : user!.Trim(),
                Text = text,
                Acknowledges = acknowledges,
            };

            // Read, change and write under a lock, so two notes written at once don't lose one of them
            using (connection.AcquireDistributedLock($"job:{jobId}:notes-lock", LockTimeout))
            {
                var notes = Read(connection, jobId).ToList();
                notes.Add(note);
                if (notes.Count > MaxNotes) notes.RemoveRange(0, notes.Count - MaxNotes);

                // Written with options of its own, like the audit log: a source-generated resolver an application sets
                // with UseSerializerSettings doesn't know JobNote
                connection.SetJobParameter(jobId, ParameterName, MonitoringJson.Serialize(notes));
            }

            return note;
        }

        /// <summary>Reads a job's notes, oldest first.</summary>
        public static IReadOnlyList<JobNote> Read([NotNull] IStorageConnection connection, [NotNull] string jobId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            return Parse(connection.GetJobParameter(jobId, ParameterName));
        }

        /// <summary>Parses the serialized parameter value.</summary>
        public static IReadOnlyList<JobNote> Parse([CanBeNull] string? serialized)
        {
            if (serialized == null || serialized.Length == 0) return Array.Empty<JobNote>();

            try
            {
                // Notes written before with the application's settings are still read
                return MonitoringJson.Deserialize<List<JobNote>>(serialized) ?? new List<JobNote>();
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return Array.Empty<JobNote>();
            }
        }

        /// <summary>The acknowledging note, when there is one.</summary>
        public static JobNote? GetAcknowledgement([NotNull] IReadOnlyList<JobNote> notes)
        {
            if (notes == null) throw new ArgumentNullException(nameof(notes));
            return notes.LastOrDefault(static n => n.Acknowledges);
        }
    }
}
