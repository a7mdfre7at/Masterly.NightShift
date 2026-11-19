// This file is part of Hangfire. Copyright © 2019 Hangfire OÜ.
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

using Masterly.NightShift.Client;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Profiling;

namespace Masterly.NightShift;

internal static class RecurringJobExtensions
{
    extension([NotNull] IStorageConnection connection)
    {
        public IDisposable AcquireDistributedRecurringJobLock(
            [NotNull] string recurringJobId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(recurringJobId);

            return connection.AcquireDistributedLock($"lock:recurring-job:{recurringJobId}", timeout);
        }

        public RecurringJobEntity? GetRecurringJob(
            [NotNull] string recurringJobId)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(recurringJobId);

            Dictionary<string, string>? recurringJob = connection.GetAllEntriesFromHash($"recurring-job:{recurringJobId}");
            if (recurringJob == null || recurringJob.Count == 0)
            {
                return null;
            }

            return new RecurringJobEntity(recurringJobId, recurringJob) { PolicyCalendars = new SchedulePolicyCalendars(connection) };
        }

        public RecurringJobEntity GetOrCreateRecurringJob(
            [NotNull] string recurringJobId)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(recurringJobId);

            Dictionary<string, string>? recurringJob = connection.GetAllEntriesFromHash($"recurring-job:{recurringJobId}");
            if (recurringJob == null || recurringJob.Count == 0)
            {
                recurringJob = [];
            }

            return new RecurringJobEntity(recurringJobId, recurringJob) { PolicyCalendars = new SchedulePolicyCalendars(connection) };
        }
    }

    // A classic extension method rather than an extension block member: CodeQL maps the arguments of a call to
    // an extension block member to the wrong parameters, and took a null logger for null changed fields
    public static void UpdateRecurringJob(
        [NotNull] this IWriteOnlyTransaction transaction,
        [NotNull] RecurringJobEntity recurringJob,
        [NotNull] IReadOnlyDictionary<string, string> changedFields,
        [CanBeNull] ILog? logger)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(recurringJob);
        ArgumentNullException.ThrowIfNull(changedFields);

        if (changedFields.Count > 0)
        {
            transaction.SetRangeInHash($"recurring-job:{recurringJob.RecurringJobId}", changedFields);
        }

        double score = recurringJob.NextExecution.HasValue ? JobHelper.ToTimestamp(recurringJob.NextExecution.Value) : -1.0D;

        if (logger != null && logger.IsTraceEnabled())
        {
            logger.Trace($"Recurring job '{recurringJob.RecurringJobId}' is being updated. RecurringJob: ({recurringJob}), Changes: ({string.Join(";", changedFields.Select(static x => $"{x.Key}:{x.Value}"))})");
        }

        transaction.AddToSet(
            "recurring-jobs",
            recurringJob.RecurringJobId,
            score);
    }

    extension([NotNull] IBackgroundJobFactory factory)
    {
        public BackgroundJob? TriggerRecurringJob(
            [NotNull] JobStorage storage,
            [NotNull] IStorageConnection connection,
            [NotNull] IProfiler profiler,
            [NotNull] RecurringJobEntity recurringJob,
            DateTime now)
        {
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(profiler);
            ArgumentNullException.ThrowIfNull(recurringJob);

            if (recurringJob.Job == null)
            {
                throw new InvalidOperationException("The 'Job' field has a null or empty value");
            }

            Job job = InvocationData.DeserializePayload(recurringJob.Job).DeserializeJob();

            CreateContext context = new(storage, connection, job, null, null, profiler, null);
            context.Parameters["RecurringJobId"] = recurringJob.RecurringJobId;
            context.Parameters["Time"] = JobHelper.ToTimestamp(now);

            if (recurringJob.SkipAfter.HasValue)
            {
                context.Parameters[SkipIfLateAttribute.WindowParameter] = (long)recurringJob.SkipAfter.Value.TotalSeconds;
            }

            // The occurrence was due while the recurring job waited for a policy that couldn't be evaluated: when
            // the run is skipped as late, its reason names the policy. Created with the job, so it costs no round trip
            if (recurringJob.GetPolicyWaitedFor(now) is { } waitedFor)
            {
                context.Parameters[SkipIfLateAttribute.WaitedForPolicyParameter] = waitedFor;
            }

            BackgroundJob? backgroundJob = factory.Create(context);

            // Never moved back: the scheduler counts the occurrences from it, so a trigger by a process whose clock
            // is behind the scheduler's would make it run an occurrence that has run already
            if (recurringJob.LastExecution is not { } lastExecution || lastExecution < now)
            {
                recurringJob.LastExecution = now;
            }

            recurringJob.LastJobId = backgroundJob?.Id;

            return backgroundJob;
        }
    }

    extension([NotNull] IStateMachine stateMachine)
    {
        public void EnqueueBackgroundJob(
            [NotNull] JobStorage storage,
            [NotNull] IStorageConnection connection,
            [NotNull] IWriteOnlyTransaction transaction,
            [NotNull] RecurringJobEntity recurringJob,
            [NotNull] BackgroundJob backgroundJob,
            [CanBeNull] string? reason,
            [NotNull] IProfiler profiler)
        {
            EnqueueBackgroundJob(stateMachine, storage, connection, transaction, recurringJob, backgroundJob, reason, profiler, afterCommit: null);
        }

        // With afterCommit, work that must wait for the caller's commit is collected there for the caller to run
        internal void EnqueueBackgroundJob(
            [NotNull] JobStorage storage,
            [NotNull] IStorageConnection connection,
            [NotNull] IWriteOnlyTransaction transaction,
            [NotNull] RecurringJobEntity recurringJob,
            [NotNull] BackgroundJob backgroundJob,
            [CanBeNull] string? reason,
            [NotNull] IProfiler profiler,
            [CanBeNull] PostCommitActions? afterCommit)
        {
            ArgumentNullException.ThrowIfNull(stateMachine);
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(recurringJob);
            ArgumentNullException.ThrowIfNull(backgroundJob);
            ArgumentNullException.ThrowIfNull(profiler);

            EnqueuedState state = new() { Reason = reason };

            if (recurringJob.Queue != null)
            {
                state.Queue = recurringJob.Queue;
            }

            stateMachine.ApplyState(new ApplyStateContext(
                storage,
                connection,
                transaction,
                backgroundJob,
                state,
                null,
                profiler,
                stateMachine)
            {
                AfterCommit = afterCommit
            });

            RecurringJobHistory.RecordRun(transaction, recurringJob.RecurringJobId, backgroundJob.Id);
        }
    }
}