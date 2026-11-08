#nullable enable

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;

namespace Masterly.NightShift
{
    /// <summary>
    /// Marks a job parameter whose value must not be shown: the dashboard renders it as
    /// <c>"••••••"</c> on every page, "Copy as code" leaves it out and "Run again with changes" asks for it
    /// anew. Use it for passwords, tokens, personal data. The value itself is still stored with the job,
    /// so redaction protects dashboard viewers, not the storage.
    /// </summary>
    /// <remarks>
    /// <see cref="JobDisplayNameAttribute"/> shows <see cref="Mask"/> for a redacted argument. Templates whose
    /// result ends up in storage keys, lock names or tags (<see cref="ThrottleAttribute"/>,
    /// <see cref="RateLimitAttribute"/>, <see cref="CircuitBreakerAttribute"/>,
    /// <see cref="PreventDuplicatesAttribute"/>, <see cref="DisableConcurrentExecutionAttribute"/> and
    /// <see cref="TagAttribute"/>) get a hash of the value instead: the first 16 hexadecimal digits of the SHA-256
    /// of its text, so different values still give different keys. A hash of a short or guessable secret can be
    /// guessed back, so don't build keys from secrets at all when the storage or the dashboard is shared with people
    /// who must not know them. A <see cref="QueueAttribute"/> template uses the real value, because the queue name
    /// must match the queues servers listen to, so don't build queue names from secrets either.
    /// </remarks>
    /// <example>
    /// <code>
    /// public Task Connect(string host, [Redacted] string password) { ... }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
    public sealed class RedactedAttribute : Attribute
    {
        /// <summary>What the dashboard shows instead of a redacted value.</summary>
        public const string Mask = "••••••";

        private const int HashBytes = 8;

        private static readonly ConcurrentDictionary<MethodInfo, int[]> RedactedPositions = new();

        /// <summary>Whether a parameter is marked with <see cref="RedactedAttribute"/>.</summary>
        public static bool IsRedacted([NotNull] ParameterInfo parameter)
        {
            if (parameter == null) throw new ArgumentNullException(nameof(parameter));
            return parameter.GetCustomAttribute<RedactedAttribute>(inherit: true) != null;
        }

        /// <summary>The job's arguments, with <see cref="Mask"/> in place of each redacted one. For text people read.</summary>
        internal static object?[] GetDisplayArguments([NotNull] Job job)
        {
            return ReplaceRedacted(job, static _ => Mask);
        }

        /// <summary>
        /// The job's arguments, with a stable hash in place of each redacted one. For templates that end up in
        /// storage keys, lock names or tags (not queue names), where a constant would merge the keys of different values.
        /// </summary>
        internal static object?[] GetKeyArguments([NotNull] Job job)
        {
            return ReplaceRedacted(job, Hash);
        }

        /// <summary>The first 16 hexadecimal digits of the SHA-256 of the value's invariant text.</summary>
        internal static string Hash([CanBeNull] object? value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var builder = new StringBuilder(HashBytes * 2);
                for (var i = 0; i < HashBytes; i++)
                {
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private static object?[] ReplaceRedacted(Job job, Func<object?, string> replace)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            var arguments = job.Args.ToArray<object?>();
            var positions = RedactedPositions.GetOrAdd(
                job.Method,
                static method => method.GetParameters().Where(IsRedacted).Select(static parameter => parameter.Position).ToArray());

            foreach (var position in positions)
            {
                if (position < arguments.Length)
                {
                    arguments[position] = replace(arguments[position]);
                }
            }

            return arguments;
        }
    }
}
