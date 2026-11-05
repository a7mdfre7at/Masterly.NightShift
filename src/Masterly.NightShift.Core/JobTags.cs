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
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
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
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class TagAttribute : JobFilterAttribute, IClientFilter
    {
        /// <summary>Tags every job of the method with a word or a template over its arguments.</summary>
        public TagAttribute([NotNull] string template)
        {
            if (String.IsNullOrWhiteSpace(template)) throw new ArgumentException("A tag is required.", nameof(template));

            Template = template;
            Order = 30;
        }

        /// <summary>The word or template.</summary>
        public string Template { get; }

        /// <summary>Resolves the tag for a job.</summary>
        public string GetTag([NotNull] Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            return JobTags.Normalize(ConcurrencyControl.FormatResource(Template, job));
        }

        /// <inheritdoc />
        public void OnCreating(CreatingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            // Before creation the parameters are still objects: an earlier TagAttribute leaves a string[]
            var tags = context.Parameters.TryGetValue(JobTags.ParameterName, out var existing)
                ? existing as string[] ?? JobTags.Parse(existing as string).ToArray()
                : Array.Empty<string>();
            var tag = GetTag(context.Job);

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

            JobTags.Index(context.Connection, context.BackgroundJob.Id, new[] { GetTag(context.Job) }, JobTags.Clock());
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

        /// <summary>How many job ids the tag lists. Expired jobs are counted until the tag is viewed.</summary>
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

        /// <summary>The longest tag accepted.</summary>
        public const int MaxLength = 100;

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>The set holding the ids of the jobs carrying a tag.</summary>
        public static string GetKey([NotNull] string tag)
        {
            if (tag == null) throw new ArgumentNullException(nameof(tag));
            return $"tags:{tag}";
        }

        /// <summary>Trims, lower-cases and validates a tag.</summary>
        public static string Normalize([NotNull] string tag)
        {
            if (tag == null) throw new ArgumentNullException(nameof(tag));

            var normalized = String.Join(" ", tag.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

            if (normalized.Length == 0) throw new ArgumentException("A tag can't be empty.", nameof(tag));
            if (normalized.Length > MaxLength) throw new ArgumentException($"A tag can't be longer than {MaxLength} characters.", nameof(tag));

            return normalized;
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

            var existing = Parse(connection.GetJobParameter(jobId, ParameterName));
            var added = normalized.Where(tag => !existing.Contains(tag, StringComparer.Ordinal)).ToList();
            if (added.Count == 0) return;

            existing.AddRange(added);
            connection.SetJobParameter(jobId, ParameterName, SerializationHelper.Serialize(existing.OrderBy(static x => x, StringComparer.Ordinal).ToArray(), SerializationOption.User));

            Index(connection, jobId, added, Clock());
        }

        /// <summary>Removes a tag from a job.</summary>
        public static void Remove([NotNull] IStorageConnection connection, [NotNull] string jobId, [NotNull] string tag)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            var normalized = Normalize(tag);
            var existing = Parse(connection.GetJobParameter(jobId, ParameterName));

            if (existing.Remove(normalized))
            {
                connection.SetJobParameter(jobId, ParameterName, SerializationHelper.Serialize(existing.ToArray(), SerializationOption.User));
            }

            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.RemoveFromSet(GetKey(normalized), jobId);
                transaction.Commit();
            }
        }

        /// <summary>Reads a job's tags.</summary>
        public static IReadOnlyList<string> GetTags([NotNull] IStorageConnection connection, [NotNull] string jobId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            return Parse(connection.GetJobParameter(jobId, ParameterName));
        }

        /// <summary>Lists every tag with its job count, most used first.</summary>
        public static IReadOnlyList<JobTagSummary> GetAll([NotNull] IStorageConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var storageConnection = connection as JobStorageConnection;
            var result = new List<JobTagSummary>();

            foreach (var tag in connection.GetAllItemsFromSet(IndexKey) ?? new HashSet<string>())
            {
                var count = storageConnection != null
                    ? storageConnection.GetSetCount(GetKey(tag))
                    : (connection.GetAllItemsFromSet(GetKey(tag))?.Count ?? 0);

                result.Add(new JobTagSummary(tag, count));
            }

            return result.OrderByDescending(static x => x.Count).ThenBy(static x => x.Tag, StringComparer.Ordinal).ToList();
        }

        /// <summary>Counts the jobs carrying a tag.</summary>
        public static long Count([NotNull] IStorageConnection connection, [NotNull] string tag)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var key = GetKey(Normalize(tag));
            return connection is JobStorageConnection storageConnection
                ? storageConnection.GetSetCount(key)
                : (connection.GetAllItemsFromSet(key)?.Count ?? 0);
        }

        /// <summary>Lists the ids of the jobs carrying a tag, newest first.</summary>
        public static IReadOnlyList<string> GetJobIds([NotNull] IStorageConnection connection, [NotNull] string tag, int from, int count)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (from < 0) throw new ArgumentOutOfRangeException(nameof(from));
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

            var key = GetKey(Normalize(tag));

            if (connection is JobStorageConnection storageConnection)
            {
                // Sets come back oldest first; the page wants the newest, so read from the end
                var total = storageConnection.GetSetCount(key);
                var endingAt = total - 1 - from;
                if (endingAt < 0) return Array.Empty<string>();

                var startingFrom = Math.Max(0, endingAt - count + 1);
                var range = storageConnection.GetRangeFromSet(key, (int)startingFrom, (int)endingAt) ?? new List<string>();
                range.Reverse();
                return range;
            }

            return (connection.GetAllItemsFromSet(key) ?? new HashSet<string>()).Reverse().Skip(from).Take(count).ToList();
        }

        /// <summary>Forgets job ids that no longer exist, so a tag doesn't keep listing expired jobs.</summary>
        public static void Forget([NotNull] IStorageConnection connection, [NotNull] string tag, [NotNull] IEnumerable<string> jobIds)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobIds == null) throw new ArgumentNullException(nameof(jobIds));

            var key = GetKey(Normalize(tag));
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

            var normalized = Normalize(tag);
            var key = GetKey(normalized);

            using (var transaction = connection.CreateWriteTransaction())
            {
                if (transaction is JobStorageTransaction storageTransaction)
                {
                    storageTransaction.RemoveSet(key);
                }
                else
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

        internal static List<string> Parse(string? json)
        {
            if (String.IsNullOrEmpty(json)) return new List<string>();

            try
            {
                return SerializationHelper.Deserialize<string[]>(json, SerializationOption.User)?.ToList() ?? new List<string>();
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
