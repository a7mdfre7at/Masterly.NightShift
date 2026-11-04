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
using System.Globalization;
using System.Linq;
using Hangfire.Annotations;
using Hangfire.Common;
using Hangfire.States;

namespace Hangfire.Server
{
    /// <summary>
    /// What the concurrency filters share: resource key formatting, timestamps, members of the form
    /// <c>{unix seconds}:{job id}</c> kept in sets, and moving a job back to the Scheduled state.
    /// </summary>
    internal static class ConcurrencyControl
    {
        private static readonly Random Jitter = new();

        /// <summary>Replaceable for tests. The worker's own changer isn't reachable from a filter.</summary>
        internal static IBackgroundJobStateChanger StateChanger { get; set; } = new BackgroundJobStateChanger();

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>
        /// Formats a resource template like <see cref="DisableConcurrentExecutionAttribute"/> does: <c>{0}</c>..<c>{n}</c>
        /// are the job's arguments, and a missing template means <c>Type.Method</c>.
        /// </summary>
        public static string FormatResource([CanBeNull] string? template, [NotNull] Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            if (!String.IsNullOrWhiteSpace(template))
            {
                try
                {
                    return String.Format(CultureInfo.InvariantCulture, template!, job.Args.ToArray()).ToLowerInvariant();
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    throw new FormatException($"Unable to obtain resource identifier from template `{template}`: {ex.Message}", ex);
                }
            }

            return $"{job.Type.ToGenericTypeString()}.{job.Method.Name}".ToLowerInvariant();
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
        /// Moves the job from Processing back to Scheduled and cancels the current performance. The worker's
        /// own Processing→Succeeded/Deleted transition then finds the job in another state and does nothing,
        /// which is the documented behaviour of <see cref="Worker"/>.
        /// </summary>
        public static void Reschedule([NotNull] PerformingContext context, DateTime enqueueAt, [NotNull] string reason)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (reason == null) throw new ArgumentNullException(nameof(reason));

            var storage = context.Storage ?? JobStorage.Current;

            var stateChangeContext = new StateChangeContext(
                storage,
                context.Connection,
                context.BackgroundJob.Id,
                new ScheduledState(enqueueAt) { Reason = reason },
                ProcessingState.StateName);

            StateChanger.ChangeState(stateChangeContext);
            context.Canceled = true;
        }

        /// <summary>Cancels the current performance: the worker moves the job to the Deleted state.</summary>
        public static void Skip([NotNull] PerformingContext context, [NotNull] string reason)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            context.Items[BackgroundJobPerformer.ContextCanceledKey] = reason;
            context.Canceled = true;
        }

        /// <summary>Blocks the worker thread until the delay passes or shutdown is requested.</summary>
        public static void Wait(PerformContext context, TimeSpan delay)
        {
            context.CancellationToken.ShutdownToken.WaitHandle.WaitOne(delay);
            context.CancellationToken.ShutdownToken.ThrowIfCancellationRequested();
        }
    }
}
