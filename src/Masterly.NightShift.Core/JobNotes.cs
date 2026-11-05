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
    /// being looked at.
    /// </summary>
    public static class JobNotes
    {
        /// <summary>The job parameter holding the notes as a JSON array.</summary>
        public const string ParameterName = "Notes";

        /// <summary>The most notes kept per job; older ones are dropped.</summary>
        public const int MaxNotes = 50;

        /// <summary>The longest note accepted.</summary>
        public const int MaxLength = 2000;

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>Adds a note. Returns the note as stored.</summary>
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

            var notes = Read(connection, jobId).ToList();
            notes.Add(note);
            if (notes.Count > MaxNotes) notes.RemoveRange(0, notes.Count - MaxNotes);

            connection.SetJobParameter(jobId, ParameterName, SerializationHelper.Serialize(notes, SerializationOption.User));
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
            if (String.IsNullOrEmpty(serialized)) return Array.Empty<JobNote>();

            try
            {
                return SerializationHelper.Deserialize<List<JobNote>>(serialized, SerializationOption.User) ?? new List<JobNote>();
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
