#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Server
{
    /// <summary>One line or checkpoint of a job console, as stored.</summary>
    public sealed class JobConsoleEntry
    {
        /// <summary>Gets or sets the Unix time in milliseconds the entry was written.</summary>
        [JsonInclude, JsonPropertyName("t")]
        public long Timestamp { get; set; }

        /// <summary>Gets or sets the order of the entry within one performance of the job.</summary>
        [JsonInclude, JsonPropertyName("s")]
        public int Sequence { get; set; }

        /// <summary>Gets or sets the severity, <c>null</c> for checkpoints.</summary>
        [JsonInclude, JsonPropertyName("l"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JobConsoleLevel? Level { get; set; }

        /// <summary>Gets or sets the line, <c>null</c> for checkpoints.</summary>
        [JsonInclude, JsonPropertyName("m"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Message { get; set; }

        /// <summary>Gets or sets the checkpoint name, <c>null</c> for lines.</summary>
        [JsonInclude, JsonPropertyName("c"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Checkpoint { get; set; }
    }

    /// <summary>The last progress a job reported, stored in the <c>Progress</c> job parameter.</summary>
    public sealed class JobProgress
    {
        /// <summary>Gets or sets the percentage, 0 to 100.</summary>
        [JsonInclude, JsonPropertyName("p")]
        public double Percent { get; set; }

        /// <summary>Gets or sets the step description.</summary>
        [JsonInclude, JsonPropertyName("m"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Message { get; set; }

        /// <summary>Gets or sets the Unix time in milliseconds it was reported.</summary>
        [JsonInclude, JsonPropertyName("t")]
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// The <see cref="IJobConsole"/> a running job gets, plus the storage conventions the dashboard reads.
    /// </summary>
    /// <remarks>
    /// Calls from several threads are safe: they run one at a time, and each one uses a short-lived connection
    /// of its own instead of the worker's connection, which may be pinned to a distributed lock. Writes made
    /// after the performance ended, or after the job's cancellation token found the job aborted (deleted or
    /// requeued while it runs), are dropped, so they don't land in lists that are already expiring.
    /// <para>
    /// Progress and lines are best-effort: when the storage fails to write them (a timeout, a lost connection),
    /// <see cref="SetProgress"/> and <see cref="WriteLine(JobConsoleLevel, string)"/> log a warning and drop the
    /// write instead of failing the job, which would make it run again. The warning is logged at most once a
    /// minute per performance, with the number of writes dropped. Checkpoints are different:
    /// <see cref="Checkpoint"/>, <see cref="Checkpoints"/> and <see cref="HasCheckpoint"/> throw storage errors,
    /// because <see cref="JobConsoleExtensions.Resume"/> decides from them which steps to skip, and a checkpoint
    /// that was silently lost, or read as missing, would make a retry redo finished work.
    /// </para>
    /// </remarks>
    public sealed class JobConsole : IJobConsole
    {
        /// <summary>Name of the job parameter holding the <see cref="JobProgress"/>.</summary>
        public const string ProgressParameter = "Progress";

        /// <summary>
        /// Name of the job parameter that marks a job whose console wrote lines or checkpoints to storage.
        /// <see cref="JobConsoleStateFilter"/> only touches the console lists of jobs that carry it.
        /// </summary>
        public const string MarkerParameter = "Console";

        private const string MarkerValue = "1";

        private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan DroppedWriteWarningInterval = TimeSpan.FromMinutes(1);
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobConsole));

        private readonly object _syncRoot = new();
        private readonly PerformContext _context;
        private int _sequence;
        private JobProgress? _lastProgress;
        private List<string>? _checkpoints;
        private List<JobConsoleEntry>? _legacyCheckpoints;
        private bool _marked;
        private int _droppedWrites;
        private DateTime? _nextDroppedWriteWarning;

        /// <summary>
        /// Gets or sets how many console entries a job keeps. Older entries are dropped. Default is 500.
        /// The checkpoints <see cref="JobConsoleExtensions.Resume"/> reads are kept separately and never dropped.
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
            _marked = context.BackgroundJob.ParametersSnapshot?.ContainsKey(MarkerParameter) == true;
        }

        /// <summary>Whether the job carries the <see cref="MarkerParameter"/>, as far as this performance knows.</summary>
        internal bool HasStorageData
        {
            get
            {
                lock (_syncRoot)
                {
                    return _marked;
                }
            }
        }

        /// <summary>Gets the storage list key holding a job's console entries.</summary>
        public static string GetConsoleKey([NotNull] string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            return $"job:{jobId}:console";
        }

        /// <summary>
        /// Gets the storage list key holding the checkpoints of a job's current run, one entry per name. Unlike
        /// the console list it isn't trimmed. Requeuing a succeeded or deleted job empties it.
        /// </summary>
        public static string GetCheckpointsKey([NotNull] string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            return $"job:{jobId}:checkpoints";
        }

        /// <inheritdoc />
        public void SetProgress(double percent, string? message = null)
        {
            if (Double.IsNaN(percent)) percent = 0;
            percent = Math.Max(0, Math.Min(100, Math.Round(percent, 1)));
            message = Truncate(message);

            lock (_syncRoot)
            {
                var last = _lastProgress;
                if (last != null && Math.Abs(last.Percent - percent) < 0.05 && String.Equals(last.Message, message, StringComparison.Ordinal))
                {
                    return;
                }

                if (!IsRunning()) return;

                var progress = new JobProgress { Percent = percent, Message = message, Timestamp = Now() };
                var value = SerializationHelper.Serialize(progress, SerializationOption.Internal);

                try
                {
                    UseConnection(connection => connection.SetJobParameter(_context.BackgroundJob.Id, ProgressParameter, value));
                    _lastProgress = progress;
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    DropWrite(ex);
                }
            }
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

            lock (_syncRoot)
            {
                if (!IsRunning()) return;

                try
                {
                    Append(new JobConsoleEntry { Level = level, Message = Truncate(message) }, newCheckpoint: false);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    DropWrite(ex);
                }
            }
        }

        /// <inheritdoc />
        public void Checkpoint(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));

            var checkpoint = Truncate(name)!;

            lock (_syncRoot)
            {
                var checkpoints = LoadCheckpoints();
                var isNew = !checkpoints.Contains(checkpoint, StringComparer.Ordinal);

                if (IsRunning())
                {
                    Append(new JobConsoleEntry { Checkpoint = checkpoint }, isNew);
                }

                if (isNew) checkpoints.Add(checkpoint);
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<string> Checkpoints
        {
            get
            {
                lock (_syncRoot)
                {
                    return LoadCheckpoints().ToArray();
                }
            }
        }

        /// <inheritdoc />
        public bool HasCheckpoint(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));

            lock (_syncRoot)
            {
                return LoadCheckpoints().Contains(Truncate(name)!, StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// Reads a job's console entries in the order they were written, oldest first. Returns an empty list
        /// when the storage can't read lists (a <see cref="JobStorageConnection"/> with the extended API is required).
        /// </summary>
        public static IReadOnlyList<JobConsoleEntry> ReadEntries([NotNull] IStorageConnection connection, [NotNull] string jobId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            return connection is JobStorageConnection storageConnection
                ? ReadList(storageConnection, GetConsoleKey(jobId))
                : [];
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

        /// <summary>
        /// The entry <see cref="JobConsoleStateFilter"/> leaves in the checkpoints list when a finished job is
        /// requeued. It has no name; it only keeps loading from falling back to the console list.
        /// </summary>
        internal static string CreateRestartMarker()
        {
            return SerializationHelper.Serialize(new JobConsoleEntry { Timestamp = Now() }, SerializationOption.Internal);
        }

        /// <summary>
        /// Reads the checkpoints of the job's current run once, on first use: from the checkpoints list or, when
        /// that was never written, from the checkpoint entries older builds kept in the console list only. A storage
        /// that can't read lists has no checkpoints (<see cref="ReadList"/>), but any other storage error is thrown
        /// and nothing is kept: treating a timeout as "no checkpoints" would make the job redo finished steps.
        /// </summary>
        private List<string> LoadCheckpoints()
        {
            if (_checkpoints != null) return _checkpoints;

            var loaded = new List<string>();

            foreach (var entry in UseConnection(ReadCheckpointEntries))
            {
                if (!loaded.Contains(entry.Checkpoint!, StringComparer.Ordinal)) loaded.Add(entry.Checkpoint!);
            }

            _checkpoints = loaded;
            return loaded;
        }

        private List<JobConsoleEntry> ReadCheckpointEntries(IStorageConnection connection)
        {
            if (connection is not JobStorageConnection storageConnection) return [];

            var jobId = _context.BackgroundJob.Id;
            var recorded = ReadList(storageConnection, GetCheckpointsKey(jobId));

            if (recorded.Count > 0)
            {
                // Entries without a name are restart markers
                return recorded.Where(static x => x.Checkpoint != null).ToList();
            }

            var consoleKey = GetConsoleKey(jobId);
            var legacy = ReadList(storageConnection, consoleKey)
                .Where(static x => x.Checkpoint != null)
                .GroupBy(static x => x.Checkpoint, StringComparer.Ordinal)
                .Select(static x => x.First())
                .ToList();

            // An expiring console list belongs to a run that finished, and the job was requeued since: start over
            if (legacy.Count == 0 || IsExpiring(storageConnection, consoleKey)) return [];

            // Copied to the checkpoints list together with the first checkpoint this run records
            _legacyCheckpoints = legacy;
            return legacy;
        }

        private static bool IsExpiring(JobStorageConnection connection, string key)
        {
            try
            {
                return connection.GetListTtl(key) > TimeSpan.Zero;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static List<JobConsoleEntry> ReadList(JobStorageConnection connection, string key)
        {
            List<string> items;

            try
            {
                items = connection.GetAllItemsFromList(key);
            }
            catch (NotSupportedException)
            {
                // The storage doesn't implement the extended API
                return [];
            }

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

        private void Append(JobConsoleEntry entry, bool newCheckpoint)
        {
            entry.Timestamp = Now();
            entry.Sequence = ++_sequence;

            var jobId = _context.BackgroundJob.Id;
            var serialized = SerializationHelper.Serialize(entry, SerializationOption.Internal);
            var earlier = newCheckpoint ? _legacyCheckpoints : null;

            UseConnection(connection =>
            {
                if (!_marked)
                {
                    // Written before the lists, so the state filter knows they need expiring with the job
                    connection.SetJobParameter(jobId, MarkerParameter, MarkerValue);
                    _marked = true;
                }

                using (var transaction = connection.CreateWriteTransaction())
                {
                    var consoleKey = GetConsoleKey(jobId);

                    transaction.InsertToList(consoleKey, serialized);

                    if (DefaultMaxLines > 0)
                    {
                        transaction.TrimList(consoleKey, 0, DefaultMaxLines - 1);
                    }

                    if (newCheckpoint)
                    {
                        var checkpointsKey = GetCheckpointsKey(jobId);

                        if (earlier != null)
                        {
                            foreach (var legacy in earlier)
                            {
                                transaction.InsertToList(checkpointsKey, SerializationHelper.Serialize(legacy, SerializationOption.Internal));
                            }
                        }

                        transaction.InsertToList(checkpointsKey, serialized);
                    }

                    transaction.Commit();
                }
            });

            if (newCheckpoint) _legacyCheckpoints = null;
        }

        /// <summary>
        /// Counts a progress report or line the storage failed to write, and logs a warning for the first one and
        /// then at most once a minute, so a long storage outage doesn't flood the log from a chatty job.
        /// </summary>
        private void DropWrite(Exception exception)
        {
            _droppedWrites++;

            var now = Clock();
            if (_nextDroppedWriteWarning.HasValue && now < _nextDroppedWriteWarning.Value) return;

            _nextDroppedWriteWarning = now + DroppedWriteWarningInterval;
            var dropped = _droppedWrites;
            _droppedWrites = 0;

            Logger.WarnException(
                $"Job console of background job '{_context.BackgroundJob.Id}': the storage failed to write, so {dropped} progress report(s) or line(s) were dropped. The job carries on. Further failures of this performance are logged at most once a minute.",
                exception);
        }

        /// <summary>
        /// Whether the job still runs here. An aborted job (deleted or requeued meanwhile) and a performance that
        /// is over (its token is disposed) must not write, because the job's lists may already be expiring.
        /// </summary>
        private bool IsRunning()
        {
            try
            {
                return !_context.CancellationToken.IsAborted();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        private void UseConnection(Action<IStorageConnection> action)
        {
            UseConnection(connection =>
            {
                action(connection);
                return true;
            });
        }

        private T UseConnection<T>(Func<IStorageConnection, T> func)
        {
            // The worker's connection may be pinned to a distributed lock, where calls from the job's other
            // threads would run on one database connection at once, so each call gets a connection of its own
            var owned = _context.Storage?.GetConnection();

            try
            {
                return func(owned ?? _context.Connection);
            }
            finally
            {
                owned?.Dispose();
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
