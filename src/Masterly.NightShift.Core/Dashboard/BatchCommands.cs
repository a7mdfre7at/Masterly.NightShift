#nullable enable

using System;
using Masterly.NightShift.States;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// The batch-wide actions behind <c>/batches/requeue-failed/{id}</c> and <c>/batches/delete/{id}</c>. They read
    /// the batch's jobs <see cref="JobListCommands.BatchSize"/> at a time and stop at the work and time limits of one
    /// request (<see cref="JobListCommands.TimeBudget"/>), like "Delete all" on the job lists.
    /// </summary>
    internal static class BatchCommands
    {
        private const string Reason = "Triggered via Dashboard UI (batch)";

        /// <summary>Requeues every failed job of the batch. Returns <c>null</c> when the batch is unknown.</summary>
        public static BulkJobResult? RequeueFailed(DashboardContext context, string batchId)
        {
            return Apply(context, batchId, BatchJob.IsFailed, static (client, jobId, _) => client.ChangeState(jobId, new EnqueuedState { Reason = Reason }, FailedState.StateName));
        }

        /// <summary>Deletes every job of the batch that has not reached a final state. Returns <c>null</c> when the batch is unknown.</summary>
        public static BulkJobResult? DeletePending(DashboardContext context, string batchId)
        {
            // Expects the state that was read, so a job that finished meanwhile isn't deleted
            return Apply(context, batchId, BatchJob.IsUnfinished, static (client, jobId, state) => client.ChangeState(jobId, new DeletedState { Reason = Reason }, state));
        }

        private static BulkJobResult? Apply(DashboardContext context, string batchId, Func<string?, bool> select, Func<IBackgroundJobClient, string, string?, bool> action)
        {
            return BulkStateChange.RunOnBatch(
                context.Storage,
                context.GetBackgroundJobClient(),
                RouteValues.Decode(batchId),
                select,
                action,
                JobListCommands.MaxReads,
                JobListCommands.TimeBudget);
        }
    }
}
