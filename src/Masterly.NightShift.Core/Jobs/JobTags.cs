#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>
    /// Tags a job when it is created, so it can be found later on the dashboard's Tags page or through the
    /// command palette. The tag is a fixed word (<c>[Tag("billing")]</c>) or a template over the job's
    /// arguments (<c>[Tag("customer:{0}")]</c>, <c>{0}</c> being the first argument). Apply several to tag
    /// a job several times. Tags are lower-cased.
    /// </summary>
    /// <remarks>
    /// Storage: set <c>tags</c> lists the tag names; set <c>tags:{tag}</c> holds the job ids, scored by creation
    /// time; the job parameter <c>Tags</c> holds the job's tags as a JSON array. Jobs added with
    /// <see cref="JobTags.Add(string, string[])"/> are tagged the same way after creation.
    /// A template whose arguments give an empty text adds no tag, and a text longer than
    /// <see cref="JobTags.MaxLength"/> is shortened, so arguments never make job creation fail. The job's tags are
    /// stored with the job; the sets are written once it exists, and when that write fails it is logged as a warning
    /// and the job is created anyway, so it is only missing from the tag's list.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class TagAttribute : JobFilterAttribute, IClientFilter
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(TagAttribute));

        /// <summary>Tags every job of the method with a word or a template over its arguments.</summary>
        public TagAttribute([NotNull] string template)
        {
            if (String.IsNullOrWhiteSpace(template)) throw new ArgumentException("A tag is required.", nameof(template));

            Template = template;
            Order = 30;
        }

        /// <summary>The word or template.</summary>
        public string Template { get; }

        /// <summary>
        /// Resolves the tag for a job: the template formatted with the job's arguments and normalized like
        /// <see cref="JobTags.Normalize"/>. Returns <c>null</c> when nothing is left (for example <c>{0}</c> with a
        /// <c>null</c> or blank argument). A text longer than <see cref="JobTags.MaxLength"/> is cut and ends with
        /// <c>~</c> and 16 hexadecimal digits of a hash of the whole text, so different long values stay different tags.
        /// </summary>
        /// <exception cref="FormatException">The template refers to an argument the method doesn't have.</exception>
        [CanBeNull]
        public string? GetTag([NotNull] Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            return JobTags.FromText(ConcurrencyControl.FormatResource(Template, job));
        }

        /// <inheritdoc />
        public void OnCreating(CreatingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var tag = GetTag(context.Job);
            if (tag == null) return;

            // Before creation the parameters are still objects: an earlier TagAttribute leaves a string[]
            var tags = context.Parameters.TryGetValue(JobTags.ParameterName, out var existing)
                ? existing as string[] ?? JobTags.Parse(existing as string).ToArray()
                : Array.Empty<string>();

            if (!tags.Contains(tag, StringComparer.Ordinal))
            {
                context.SetJobParameter(JobTags.ParameterName, tags.Concat(new[] { tag }).OrderBy(static x => x, StringComparer.Ordinal).ToArray());
            }
        }

        /// <inheritdoc />
        public void OnCreated(CreatedContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.Canceled || context.Exception != null || context.BackgroundJob == null) return;

            var tag = GetTag(context.Job);
            if (tag == null) return;

            try
            {
                JobTags.Index(context.Connection, context.BackgroundJob.Id, new[] { tag }, JobTags.Clock());
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // The job exists already: failing here would make the caller think it wasn't created and create it again
                Logger.WarnException($"Could not add job '{context.BackgroundJob.Id}' to the list of tag '{tag}'. The job is created and keeps the tag in its parameters.", ex);
            }
        }
    }

    /// <summary>A tag with the number of jobs carrying it.</summary>
    public sealed class JobTagSummary
    {
        internal JobTagSummary(string tag, long count)
        {
            Tag = tag;
            Count = count;
        }

        /// <summary>The tag.</summary>
        public string Tag { get; }

        /// <summary>How many job ids the tag lists. Expired jobs are counted until they are removed from the tag, with the tag page's button or <see cref="JobTags.Forget"/>.</summary>
        public long Count { get; }
    }

    /// <summary>
    /// Finds and manages job tags, see <see cref="TagAttribute"/>. Tags can also be attached after creation:
    /// <c>JobTags.Add(jobId, "customer:contoso", "import")</c>.
    /// </summary>
    public static class JobTags
    {
        /// <summary>The job parameter holding a job's tags as a JSON array.</summary>
        public const string ParameterName = "Tags";

        /// <summary>The set listing every tag.</summary>
        public const string IndexKey = "tags";

        /// <summary>
        /// The longest tag accepted. The set key <c>tags:{tag}</c> must fit the 100 characters storages such as
        /// SQL Server allow for keys.
        /// </summary>
        public const int MaxLength = 95;

        private const int HashLength = 16;

        /// <summary>
        /// How long <see cref="Add(IStorageConnection, string, IEnumerable{string})"/> and <see cref="Remove"/> wait
        /// for another change to the same job's tags to finish.
        /// </summary>
        internal static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>The set holding the ids of the jobs carrying a tag.</summary>
        public static string GetKey([NotNull] string tag)
        {
            if (tag == null) throw new ArgumentNullException(nameof(tag));
            return $"tags:{tag}";
        }

        /// <summary>Trims, collapses runs of whitespace, lower-cases and validates a tag.</summary>
        /// <exception cref="ArgumentException">The tag is empty or longer than <see cref="MaxLength"/>.</exception>
        public static string Normalize([NotNull] string tag)
        {
            var normalized = NormalizeName(tag);
            if (normalized.Length > MaxLength) throw new ArgumentException($"A tag can't be longer than {MaxLength} characters.", nameof(tag));

            return normalized;
        }

        /// <summary>
        /// Normalizes the text a template produced: <c>null</c> when nothing is left, and a text longer than
        /// <see cref="MaxLength"/> cut to a readable prefix followed by <c>~</c> and a hash of the whole text.
        /// </summary>
        internal static string? FromText([NotNull] string text)
        {
            var normalized = Collapse(text);
            if (normalized.Length == 0) return null;
            if (normalized.Length <= MaxLength) return normalized;

            var prefixLength = MaxLength - HashLength - 1;
            if (Char.IsHighSurrogate(normalized[prefixLength - 1])) prefixLength--;

            return $"{normalized.Substring(0, prefixLength).TrimEnd()}~{RedactedAttribute.Hash(normalized)}";
        }

        /// <summary>
        /// Normalizes a tag that is looked up or removed. Longer tags than <see cref="MaxLength"/> are accepted:
        /// they simply aren't found, and ones stored by earlier builds can still be removed.
        /// </summary>
        private static string NormalizeName([NotNull] string tag)
        {
            if (tag == null) throw new ArgumentNullException(nameof(tag));

            var normalized = Collapse(tag);
            if (normalized.Length == 0) throw new ArgumentException("A tag can't be empty.", nameof(tag));

            return normalized;
        }

        private static string Collapse(string text)
        {
            return String.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        }

        /// <summary>Tags an existing job in <see cref="JobStorage.Current"/>.</summary>
        public static void Add([NotNull] string jobId, [NotNull] params string[] tags)
        {
            using (var connection = JobStorage.Current.GetConnection())
            {
                Add(connection, jobId, tags);
            }
        }

        /// <summary>Tags an existing job.</summary>
        public static void Add([NotNull] IStorageConnection connection, [NotNull] string jobId, [NotNull] IEnumerable<string> tags)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            if (tags == null) throw new ArgumentNullException(nameof(tags));

            var normalized = tags.Select(Normalize).Distinct(StringComparer.Ordinal).ToList();
            if (normalized.Count == 0) return;

            // Read, change and write under a lock, so two changes at once don't lose each other's tags
            using (AcquireLock(connection, jobId))
            {
                var existing = Parse(connection.GetJobParameter(jobId, ParameterName));
                var added = normalized.Where(tag => !existing.Contains(tag, StringComparer.Ordinal)).ToList();
                if (added.Count == 0) return;

                existing.AddRange(added);
                connection.SetJobParameter(jobId, ParameterName, Serialize(existing.OrderBy(static x => x, StringComparer.Ordinal).ToArray()));

                Index(connection, jobId, added, Clock());
            }
        }

        /// <summary>Removes a tag from a job.</summary>
        public static void Remove([NotNull] IStorageConnection connection, [NotNull] string jobId, [NotNull] string tag)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            var normalized = NormalizeName(tag);

            using (AcquireLock(connection, jobId))
            {
                var existing = Parse(connection.GetJobParameter(jobId, ParameterName));

                if (existing.Remove(normalized))
                {
                    connection.SetJobParameter(jobId, ParameterName, Serialize(existing.ToArray()));
                }

                using (var transaction = connection.CreateWriteTransaction())
                {
                    transaction.RemoveFromSet(GetKey(normalized), jobId);
                    transaction.Commit();
                }
            }
        }

        /// <summary>
        /// Takes the lock on a job's tags: a lock of its own, not the job's state lock, so tagging from a state filter
        /// or while the job changes state doesn't wait. Throws <see cref="DistributedLockTimeoutException"/> after
        /// <see cref="LockTimeout"/>.
        /// </summary>
        private static IDisposable AcquireLock(IStorageConnection connection, string jobId)
        {
            return connection.AcquireDistributedLock($"job:{jobId}:tags-lock", LockTimeout);
        }

        /// <summary>Reads a job's tags.</summary>
        public static IReadOnlyList<string> GetTags([NotNull] IStorageConnection connection, [NotNull] string jobId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            return Parse(connection.GetJobParameter(jobId, ParameterName));
        }

        /// <summary>
        /// Lists every tag with its job count, most used first. It counts the jobs of every tag (one storage
        /// query per tag); with many tags, use <see cref="GetNames"/> and <see cref="Count"/> for the ones you need.
        /// </summary>
        public static IReadOnlyList<JobTagSummary> GetAll([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            return GetNames(connection)
                .Select(tag => new JobTagSummary(tag, CountMembers(connection, GetKey(tag))))
                .OrderByDescending(static x => x.Count)
                .ThenBy(static x => x.Tag, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Lists every tag name in ordinal order, without counting their jobs: one storage query.</summary>
        public static IReadOnlyList<string> GetNames([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            return (connection.GetAllItemsFromSet(IndexKey) ?? new HashSet<string>())
                .OrderBy(static x => x, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Counts the jobs carrying a tag.</summary>
        public static long Count([NotNull] IStorageConnection connection, [NotNull] string tag)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            return CountMembers(connection, GetKey(NormalizeName(tag)));
        }

        /// <summary>Lists the ids of the jobs carrying a tag, newest first.</summary>
        public static IReadOnlyList<string> GetJobIds([NotNull] IStorageConnection connection, [NotNull] string tag, int from, int count)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (from < 0) throw new ArgumentOutOfRangeException(nameof(from));
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

            var key = GetKey(NormalizeName(tag));

            return ExtendedStorage.Read<IReadOnlyList<string>>(
                connection,
                storageConnection =>
                {
                    // Sets come back oldest first; the page wants the newest, so read from the end
                    var total = storageConnection.GetSetCount(key);
                    var endingAt = total - 1 - from;
                    if (endingAt < 0) return Array.Empty<string>();

                    var startingFrom = Math.Max(0, endingAt - count + 1);
                    var range = storageConnection.GetRangeFromSet(key, (int)startingFrom, (int)endingAt) ?? new List<string>();
                    range.Reverse();
                    return range;
                },
                basicConnection => (basicConnection.GetAllItemsFromSet(key) ?? new HashSet<string>()).Reverse().Skip(from).Take(count).ToList());
        }

        private static long CountMembers(IStorageConnection connection, string key)
        {
            return ExtendedStorage.Read(
                connection,
                storageConnection => storageConnection.GetSetCount(key),
                basicConnection => (long)(basicConnection.GetAllItemsFromSet(key)?.Count ?? 0));
        }

        /// <summary>Forgets job ids that no longer exist, so a tag doesn't keep listing expired jobs.</summary>
        public static void Forget([NotNull] IStorageConnection connection, [NotNull] string tag, [NotNull] IEnumerable<string> jobIds)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobIds == null) throw new ArgumentNullException(nameof(jobIds));

            var key = GetKey(NormalizeName(tag));
            var ids = jobIds.ToList();
            if (ids.Count == 0) return;

            using (var transaction = connection.CreateWriteTransaction())
            {
                foreach (var jobId in ids)
                {
                    transaction.RemoveFromSet(key, jobId);
                }

                transaction.Commit();
            }
        }

        /// <summary>Deletes a tag and its list of jobs. The jobs themselves are untouched.</summary>
        public static void Delete([NotNull] IStorageConnection connection, [NotNull] string tag)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var normalized = NormalizeName(tag);
            var key = GetKey(normalized);

            using (var transaction = connection.CreateWriteTransaction())
            {
                if (!TryRemoveSet(transaction, key))
                {
                    foreach (var jobId in connection.GetAllItemsFromSet(key) ?? new HashSet<string>())
                    {
                        transaction.RemoveFromSet(key, jobId);
                    }
                }

                transaction.RemoveFromSet(IndexKey, normalized);
                transaction.Commit();
            }
        }

        private static bool TryRemoveSet(IWriteOnlyTransaction transaction, string key)
        {
            if (transaction is not JobStorageTransaction storageTransaction) return false;

            try
            {
                storageTransaction.RemoveSet(key);
                return true;
            }
            catch (NotSupportedException)
            {
                // Without the extended API RemoveSet throws before queuing anything: remove the members one by one
                return false;
            }
        }

        /// <summary>
        /// Writes the <c>Tags</c> parameter with the internal options, so the application's serializer settings (a
        /// resolver that doesn't know <c>string[]</c>, for example) can't break tagging.
        /// </summary>
        internal static string Serialize(string[] tags)
        {
            return SerializationHelper.SerializeInternal(tags);
        }

        /// <summary>Reads the <c>Tags</c> parameter, also when it was written with the application's settings.</summary>
        internal static List<string> Parse(string? json)
        {
            if (String.IsNullOrEmpty(json)) return new List<string>();

            try
            {
                return SerializationHelper.Deserialize<string[]>(json)?.ToList() ?? new List<string>();
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return new List<string>();
            }
        }

        internal static void Index(IStorageConnection connection, string jobId, IEnumerable<string> tags, DateTime now)
        {
            var score = JobHelper.ToTimestamp(now);

            using (var transaction = connection.CreateWriteTransaction())
            {
                foreach (var tag in tags)
                {
                    transaction.AddToSet(IndexKey, tag);
                    transaction.AddToSet(GetKey(tag), jobId, score);
                }

                transaction.Commit();
            }
        }
    }
}
