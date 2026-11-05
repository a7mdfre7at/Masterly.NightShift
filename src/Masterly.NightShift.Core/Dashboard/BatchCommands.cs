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
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>The batch-wide actions behind <c>/batches/requeue-failed/{id}</c> and <c>/batches/delete/{id}</c>.</summary>
    internal static class BatchCommands
    {
        private const string Reason = "Triggered via Dashboard UI (batch)";

        /// <summary>Requeues every failed job of the batch. Returns <c>false</c> when the batch is unknown.</summary>
        public static bool RequeueFailed(DashboardContext context, string batchId)
        {
            return Apply(context, batchId, static state => state == FailedState.StateName, static (client, jobId) => client.ChangeState(jobId, new EnqueuedState { Reason = Reason }, FailedState.StateName));
        }

        /// <summary>Deletes every job of the batch that has not reached a final state. Returns <c>false</c> when the batch is unknown.</summary>
        public static bool DeletePending(DashboardContext context, string batchId)
        {
            return Apply(context, batchId, static state => state != SucceededState.StateName && state != DeletedState.StateName, static (client, jobId) => client.ChangeState(jobId, new DeletedState { Reason = Reason }, null));
        }

        private static bool Apply(DashboardContext context, string batchId, Func<string?, bool> select, Action<IBackgroundJobClient, string> action)
        {
            batchId = Uri.UnescapeDataString(batchId);
            var selected = new List<string>();

            using (var connection = context.Storage.GetReadOnlyConnection())
            {
                if (BatchJob.Get(connection, batchId) == null) return false;

                foreach (var jobId in BatchJob.GetJobIds(connection, batchId))
                {
                    var data = connection.GetJobData(jobId);
                    if (data != null && select(data.State)) selected.Add(jobId);
                }
            }

            var client = context.GetBackgroundJobClient();
            foreach (var jobId in selected)
            {
                action(client, jobId);
            }

            return true;
        }
    }
}
