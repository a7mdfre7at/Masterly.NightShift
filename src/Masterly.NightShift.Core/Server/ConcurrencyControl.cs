#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Server
{
    /// <summary>
    /// What the concurrency filters share: resource key formatting, timestamps, members of the form
    /// <c>{unix seconds}:{job id}</c> kept in sets, and moving a job back to the Scheduled state.
    /// </summary>
    internal static class ConcurrencyControl
    {
        /// <summary>
        /// The longest set or hash key SQL Server stores. A resource is shortened by <see cref="FormatResourceKey"/> only
        /// when a key built from it would be longer, so every resource that fits keeps its exact key.
        /// </summary>
        internal const int MaxKeyLength = 100;

        /// <summary>
        /// <see cref="PerformContext.Items"/> entry where the worker leaves its <see cref="IBackgroundJobStateChanger"/>,
        /// so filters change states with the server's filter provider.
        /// </summary>
        internal const string StateChangerItem = "X_NS_StateChanger";

        /// <summary>
        /// <see cref="PerformContext.Items"/> entry set by <see cref="Reschedule"/>: the job already left the Processing
        /// state, so the worker doesn't change its state again.
        /// </summary>
        internal const string RescheduledItem = "X_NS_Rescheduled";

        /// <summary>
        /// <see cref="PerformContext.Items"/> entry set by the performer just before it calls the job method, so a
        /// filter's <c>OnPerformed</c> can tell a method that ran (and maybe threw) from a later filter that threw
        /// before the method was called.
        /// </summary>
        internal const string MethodInvokedItem = "X_NS_MethodInvoked";

        private const int HashLength = 32;
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(ConcurrencyControl));
        private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(Int32.MaxValue);

        /// <summary>
        /// The pauses between the attempts of <see cref="WithRetries"/> after a storage error. Replaceable for tests.
        /// </summary>
        internal static TimeSpan[] StorageRetryDelays { get; set; } = { TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1) };
        private static readonly Random Jitter = new();

        /// <summary>
        /// Replaceable for tests. Used when the worker's own changer isn't in <see cref="PerformContext.Items"/>
        /// (a performer called directly, for example).
        /// </summary>
        internal static IBackgroundJobStateChanger StateChanger { get; set; } = new BackgroundJobStateChanger();

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>
        /// Formats a resource template like <see cref="DisableConcurrentExecutionAttribute"/> does: <c>{0}</c>..<c>{n}</c>
        /// are the job's arguments (a <see cref="RedactedAttribute"/> argument is replaced with a hash of its value, see
        /// <see cref="RedactedAttribute.GetKeyArguments"/>), and a missing template means the job's method as
        /// <see cref="GetMethodIdentity"/> names it. The result is lower-cased.
        /// </summary>
        public static string FormatResource([CanBeNull] string? template, [NotNull] Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            if (!String.IsNullOrWhiteSpace(template))
            {
                try
                {
                    return String.Format(CultureInfo.InvariantCulture, template!, RedactedAttribute.GetKeyArguments(job)).ToLowerInvariant();
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    throw new FormatException($"Unable to obtain resource identifier from template `{template}`: {ex.Message}", ex);
                }
            }

            return GetMethodIdentity(job).ToLowerInvariant();
        }

        /// <summary>
        /// Names the job's method when no template is given: the full name of the job's type (namespace and generic
        /// arguments included), the method's name (with its generic arguments) and its parameter types, such as
        /// <c>Orders.CleanupJob.Execute(System.String,System.Int32)</c>. Types of the same name in other namespaces,
        /// and overloads, get different names.
        /// </summary>
        public static string GetMethodIdentity([NotNull] Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            var builder = new StringBuilder();
            AppendTypeName(builder, job.Type);
            builder.Append('.').Append(job.Method.Name);

            if (job.Method.IsGenericMethod)
            {
                var arguments = job.Method.GetGenericArguments();
                AppendTypeArguments(builder, arguments, 0, arguments.Length);
            }

            builder.Append('(');

            var parameters = job.Method.GetParameters();
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i > 0) builder.Append(',');
                AppendTypeName(builder, parameters[i].ParameterType);
            }

            return builder.Append(')').ToString();
        }

        private static void AppendTypeName(StringBuilder builder, Type type)
        {
            if (type.IsGenericParameter)
            {
                builder.Append(type.Name);
                return;
            }

            if (type.HasElementType)
            {
                AppendTypeName(builder, type.GetElementType()!);

                if (type.IsArray)
                {
                    builder.Append('[').Append(',', type.GetArrayRank() - 1).Append(']');
                }
                else
                {
                    builder.Append(type.IsByRef ? '&' : '*');
                }

                return;
            }

            if (!type.IsGenericType)
            {
                builder.Append((type.FullName ?? type.Name).Replace('+', '.'));
                return;
            }

            // "Namespace.Outer`1+Inner`2": each part's arguments take the place of its "`n"
            var definition = type.GetGenericTypeDefinition();
            var name = definition.FullName ?? definition.Name;
            var arguments = type.GetGenericArguments();
            var position = 0;

            for (var i = 0; i < name.Length; i++)
            {
                if (name[i] != '`')
                {
                    builder.Append(name[i] == '+' ? '.' : name[i]);
                    continue;
                }

                var count = 0;
                while (i + 1 < name.Length && name[i + 1] >= '0' && name[i + 1] <= '9')
                {
                    count = (count * 10) + (name[++i] - '0');
                }

                count = Math.Min(count, arguments.Length - position);
                AppendTypeArguments(builder, arguments, position, count);
                position += count;
            }
        }

        private static void AppendTypeArguments(StringBuilder builder, Type[] arguments, int start, int count)
        {
            builder.Append('<');

            for (var i = 0; i < count; i++)
            {
                if (i > 0) builder.Append(',');
                AppendTypeName(builder, arguments[start + i]);
            }

            builder.Append('>');
        }

        /// <summary>
        /// The longest resource whose keys fit the storage, when the longest key built from it adds
        /// <paramref name="keyOverhead"/> characters (<c>"throttle:".Length</c>, for example).
        /// </summary>
        public static int GetMaxResourceLength(int keyOverhead) => MaxKeyLength - keyOverhead;

        /// <summary>
        /// Formats a resource like <see cref="FormatResource"/> for use in storage keys and lock names. A resource longer
        /// than <paramref name="maxLength"/> characters (see <see cref="GetMaxResourceLength"/>) keeps its first
        /// characters and ends with <c>~</c> and a SHA-256 hash of the whole text, so it still names one resource, and
        /// the keys built from it fit the storage. Resources that fit are returned unchanged.
        /// </summary>
        public static string FormatResourceKey([CanBeNull] string? template, [NotNull] Job job, int maxLength)
        {
            return ShortenResource(FormatResource(template, job), maxLength);
        }

        /// <summary>Shortens a resource longer than <paramref name="maxLength"/>, see <see cref="FormatResourceKey"/>.</summary>
        public static string ShortenResource([NotNull] string resource, int maxLength)
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            if (maxLength <= HashLength + 2) throw new ArgumentOutOfRangeException(nameof(maxLength));
            if (resource.Length <= maxLength) return resource;

            // "{prefix}~{hash}" is exactly maxLength characters long; a surrogate pair is never cut in two
            var prefixLength = maxLength - HashLength - 1;
            if (Char.IsHighSurrogate(resource[prefixLength - 1])) prefixLength--;

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(resource));
                var builder = new StringBuilder(maxLength);
                builder.Append(resource, 0, prefixLength).Append('~');

                for (var i = 0; i < HashLength / 2; i++)
                {
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        /// <summary>
        /// The state changer of the worker that runs the job, which uses the server's filter provider, or
        /// <see cref="StateChanger"/> when the job runs outside a worker.
        /// </summary>
        public static IBackgroundJobStateChanger GetStateChanger([NotNull] PerformContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            return context.Items.TryGetValue(StateChangerItem, out var value) && value is IBackgroundJobStateChanger stateChanger
                ? stateChanger
                : StateChanger;
        }

        /// <summary>
        /// The transaction as a <see cref="JobStorageTransaction"/> when sets and hashes can be given an expiration in
        /// it (the storage supports <see cref="JobStorageFeatures.ExtendedApi"/>), otherwise <c>null</c>.
        /// </summary>
        public static JobStorageTransaction? GetExpiringTransaction([CanBeNull] JobStorage? storage, [NotNull] IWriteOnlyTransaction transaction)
        {
            return storage != null && storage.HasFeature(JobStorageFeatures.ExtendedApi)
                ? transaction as JobStorageTransaction
                : null;
        }

        public static long ToUnixSeconds(DateTime utc) => JobHelper.ToTimestamp(utc);

        public static DateTime FromUnixSeconds(long seconds) => JobHelper.FromTimestamp(seconds);

        public static string CreateMember(long timestamp, string jobId) => $"{timestamp.ToString(CultureInfo.InvariantCulture)}:{jobId}";

        /// <summary>
        /// Parses a <c>{timestamp}:{jobId}</c> member. Members in another format are treated as stale.
        /// </summary>
        public static bool TryParseMember(string member, out long timestamp, out string jobId)
        {
            var separator = member.IndexOf(':');

            if (separator > 0 && Int64.TryParse(member.Substring(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out timestamp))
            {
                jobId = member.Substring(separator + 1);
                return true;
            }

            timestamp = 0;
            jobId = String.Empty;
            return false;
        }

        /// <summary>
        /// Splits set members into live ones (newer than <paramref name="staleBefore"/>, and not belonging to
        /// <paramref name="ownJobId"/>, whose previous attempt may have crashed) and the ones to remove.
        /// </summary>
        public static void Partition(
            IEnumerable<string> members,
            long staleBefore,
            string? ownJobId,
            out List<(long Timestamp, string JobId)> live,
            out List<string> stale)
        {
            live = new List<(long, string)>();
            stale = new List<string>();

            foreach (var member in members)
            {
                if (!TryParseMember(member, out var timestamp, out var jobId)
                    || timestamp < staleBefore
                    || (ownJobId != null && String.Equals(jobId, ownJobId, StringComparison.Ordinal)))
                {
                    stale.Add(member);
                }
                else
                {
                    live.Add((timestamp, jobId));
                }
            }
        }

        /// <summary>A random delay of up to a second, so rescheduled jobs don't all wake up together.</summary>
        public static TimeSpan GetJitter()
        {
            lock (Jitter)
            {
                return TimeSpan.FromMilliseconds(Jitter.Next(0, 1000));
            }
        }

        /// <summary>
        /// Moves the job from Processing back to Scheduled and cancels the current performance. The state change uses
        /// the worker's state changer (see <see cref="GetStateChanger"/>), and the worker then leaves the job's state
        /// alone: it doesn't send its usual "Canceled by filter" Deleted state, which could delete the job after
        /// another worker had already picked it up again.
        /// </summary>
        /// <remarks>
        /// A storage error is retried (see <see cref="WithRetries"/>), so a transient one doesn't fail a job whose
        /// method never ran. A retry is safe: the state change expects the Processing state, so when an attempt that
        /// reported an error had committed after all, the next one changes nothing.
        /// </remarks>
        public static void Reschedule([NotNull] PerformingContext context, DateTime enqueueAt, [NotNull] string reason)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (reason == null) throw new ArgumentNullException(nameof(reason));

            var storage = context.Storage ?? JobStorage.Current;

            WithRetries(context, $"reschedule background job '{context.BackgroundJob.Id}'", () =>
            {
                // The job comes back with a new Enqueued state for the default queue: keep a queue given only by its
                // state
                HeldQueue.Remember(context);

                var stateChangeContext = new StateChangeContext(
                    storage,
                    context.Connection,
                    context.BackgroundJob.Id,
                    new ScheduledState(enqueueAt) { Reason = reason },
                    ProcessingState.StateName);

                GetStateChanger(context).ChangeState(stateChangeContext);
            });

            context.Items[RescheduledItem] = true;
            context.Canceled = true;
        }

        /// <summary>
        /// Runs a storage write that must be safe to repeat, trying it again twice after an error (see
        /// <see cref="StorageRetryDelays"/>), so a transient one (a deadlock victim, a dropped connection) doesn't fail a
        /// job whose method never ran. When every attempt fails, the last error is thrown: it fails the run, and the
        /// job's retry policy decides. A shutdown while waiting throws <see cref="OperationCanceledException"/>, and the
        /// worker requeues the job.
        /// </summary>
        public static void WithRetries([NotNull] PerformContext context, [NotNull] string description, [NotNull] Action write)
        {
            var delays = StorageRetryDelays;

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    write();
                    return;
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType() && attempt < delays.Length)
                {
                    Logger.WarnException($"Could not {description} (attempt {attempt + 1} of {delays.Length + 1}), trying again.", ex);
                    Wait(context, delays[attempt]);
                }
            }
        }

        /// <summary>Cancels the current performance: the worker moves the job to the Deleted state.</summary>
        public static void Skip([NotNull] PerformingContext context, [NotNull] string reason)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            context.Items[BackgroundJobPerformer.ContextCanceledKey] = reason;
            context.Canceled = true;
        }

        /// <summary>
        /// Blocks the worker thread until the delay passes or shutdown is requested. A delay longer than a wait handle
        /// accepts (about 24 days) is shortened to that; callers wait in a loop.
        /// </summary>
        public static void Wait(PerformContext context, TimeSpan delay)
        {
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (delay > MaxWait) delay = MaxWait;

            context.CancellationToken.ShutdownToken.WaitHandle.WaitOne(delay);
            context.CancellationToken.ShutdownToken.ThrowIfCancellationRequested();
        }
    }
}
