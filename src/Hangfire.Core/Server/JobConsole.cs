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
using System.Threading;
using Hangfire.Annotations;
using Hangfire.Common;
using Hangfire.Storage;
using Newtonsoft.Json;

namespace Hangfire.Server
{
    /// <summary>One line or checkpoint of a job console, as stored.</summary>
    public sealed class JobConsoleEntry
    {
        /// <summary>Gets or sets the Unix time in milliseconds the entry was written.</summary>
        [JsonProperty("t")]
        public long Timestamp { get; set; }

        /// <summary>Gets or sets the order of the entry within one performance of the job.</summary>
        [JsonProperty("s")]
        public int Sequence { get; set; }

        /// <summary>Gets or sets the severity, <c>null</c> for checkpoints.</summary>
        [JsonProperty("l", NullValueHandling = NullValueHandling.Ignore)]
        public JobConsoleLevel? Level { get; set; }

        /// <summary>Gets or sets the line, <c>null</c> for checkpoints.</summary>
        [JsonProperty("m", NullValueHandling = NullValueHandling.Ignore)]
        public string? Message { get; set; }

        /// <summary>Gets or sets the checkpoint name, <c>null</c> for lines.</summary>
        [JsonProperty("c", NullValueHandling = NullValueHandling.Ignore)]
        public string? Checkpoint { get; set; }
    }

    /// <summary>The last progress a job reported, stored in the <c>Progress</c> job parameter.</summary>
    public sealed class JobProgress
    {
        /// <summary>Gets or sets the percentage, 0 to 100.</summary>
        [JsonProperty("p")]
        public double Percent { get; set; }

        /// <summary>Gets or sets the step description.</summary>
        [JsonProperty("m", NullValueHandling = NullValueHandling.Ignore)]
        public string? Message { get; set; }

        /// <summary>Gets or sets the Unix time in milliseconds it was reported.</summary>
        [JsonProperty("t")]
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// The <see cref="IJobConsole"/> a running job gets, plus the storage conventions the dashboard reads.
    /// </summary>
    public sealed class JobConsole : IJobConsole
    {
        /// <summary>Name of the job parameter holding the <see cref="JobProgress"/>.</summary>
        public const string ProgressParameter = "Progress";

        private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly PerformContext _context;
        private int _sequence;
        private JobProgress? _lastProgress;

        /// <summary>
        /// Gets or sets how many console entries a job keeps. Older entries are dropped. Default is 500.
        /// </summary>
        public static int DefaultMaxLines { get; set; } = 500;

        /// <summary>
        /// Gets or sets the longest message kept; longer ones are cut. Default is 4000 characters.
        /// </summary>
        public static int MaxMessageLength { get; set; } = 4000;

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        internal JobConsole([NotNull] PerformContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>Gets the storage list key holding a job's console entries.</summary>
        public static string GetConsoleKey([NotNull] string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            return $"job:{jobId}:console";
        }

        /// <inheritdoc />
        public void SetProgress(double percent, string? message = null)
        {
            if (Double.IsNaN(percent)) percent = 0;
            percent = Math.Max(0, Math.Min(100, Math.Round(percent, 1)));
            message = Truncate(message);

            var last = _lastProgress;
            if (last != null && Math.Abs(last.Percent - percent) < 0.05 && String.Equals(last.Message, message, StringComparison.Ordinal))
            {
                return;
            }

            var progress = new JobProgress { Percent = percent, Message = message, Timestamp = Now() };
            _context.Connection.SetJobParameter(_context.BackgroundJob.Id, ProgressParameter, SerializationHelper.Serialize(progress, SerializationOption.Internal));
            _lastProgress = progress;
        }

        /// <inheritdoc />
        public void WriteLine(string message)
        {
            WriteLine(JobConsoleLevel.Info, message);
        }

        /// <inheritdoc />
        public void WriteLine(JobConsoleLevel level, string message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            Append(new JobConsoleEntry { Level = level, Message = Truncate(message) });
        }

        /// <inheritdoc />
        public void Checkpoint(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));

            Append(new JobConsoleEntry { Checkpoint = Truncate(name) });
        }

        /// <summary>
        /// Reads a job's console entries in the order they were written, oldest first. Returns an empty list
        /// when the storage can't read lists (<see cref="JobStorageConnection"/> is required).
        /// </summary>
        public static IReadOnlyList<JobConsoleEntry> ReadEntries([NotNull] IStorageConnection connection, [NotNull] string jobId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            if (connection is not JobStorageConnection storageConnection)
            {
                return [];
            }

            var items = storageConnection.GetAllItemsFromList(GetConsoleKey(jobId));
            var entries = new List<JobConsoleEntry>(items.Count);

            foreach (var item in items)
            {
                try
                {
                    var entry = SerializationHelper.Deserialize<JobConsoleEntry>(item, SerializationOption.Internal);
                    if (entry != null) entries.Add(entry);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    // A line another version wrote in a format we don't know: skip it rather than hide the console
                }
            }

            return entries.OrderBy(static x => x.Timestamp).ThenBy(static x => x.Sequence).ToList();
        }

        /// <summary>Reads the last progress a job reported, or <c>null</c>.</summary>
        public static JobProgress? ReadProgress([NotNull] IStorageConnection connection, [NotNull] string jobId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            try
            {
                var value = connection.GetJobParameter(jobId, ProgressParameter);
                return value == null ? null : SerializationHelper.Deserialize<JobProgress>(value, SerializationOption.Internal);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return null;
            }
        }

        /// <summary>Converts an entry's Unix millisecond timestamp to UTC.</summary>
        public static DateTime FromTimestamp(long milliseconds) => UnixEpoch.AddMilliseconds(milliseconds);

        private void Append(JobConsoleEntry entry)
        {
            entry.Timestamp = Now();
            entry.Sequence = Interlocked.Increment(ref _sequence);

            var key = GetConsoleKey(_context.BackgroundJob.Id);

            using (var transaction = _context.Connection.CreateWriteTransaction())
            {
                transaction.InsertToList(key, SerializationHelper.Serialize(entry, SerializationOption.Internal));

                if (DefaultMaxLines > 0)
                {
                    transaction.TrimList(key, 0, DefaultMaxLines - 1);
                }

                transaction.Commit();
            }
        }

        private static long Now() => (long)(Clock() - UnixEpoch).TotalMilliseconds;

        private static string? Truncate(string? value)
        {
            if (value == null || MaxMessageLength <= 0 || value.Length <= MaxMessageLength) return value;
            return value.Substring(0, MaxMessageLength);
        }
    }
}
