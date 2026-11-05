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
using Masterly.NightShift.Common;
using Masterly.NightShift.States;

namespace Masterly.NightShift.Server
{
    /// <summary>
    /// Enforces <see cref="DrainMode"/>: when a worker elects the Processing state for a job while the switch is
    /// on, the job goes to the Scheduled state for <see cref="DrainMode.HoldInterval"/> instead, and the worker
    /// moves on. Registered globally by default.
    /// </summary>
    public sealed class DrainModeFilter : JobFilterAttribute, IElectStateFilter
    {
        /// <summary>The reason put on held jobs.</summary>
        public const string HeldReason = "Held: processing is paused";

        /// <summary>The reason put on jobs held because their queue is paused; <c>{0}</c> is the queue.</summary>
        public const string QueueHeldReasonFormat = "Held: queue '{0}' is paused";

        /// <summary>The reason put on jobs a paused server hands back; <c>{0}</c> is the server.</summary>
        public const string ServerHeldReasonFormat = "Held: server '{0}' is paused";

        /// <inheritdoc />
        public void OnStateElection(ElectStateContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.CandidateState is not ProcessingState) return;

            var status = DrainMode.GetStatus(context.Connection);

            if (status.Active)
            {
                context.CandidateState = new ScheduledState(DrainMode.HoldInterval) { Reason = HeldReason };
                return;
            }

            if (status.PausedServers.Count > 0)
            {
                var serverId = ((ProcessingState)context.CandidateState).ServerId;
                if (serverId != null && status.PausedServers.ContainsKey(serverId))
                {
                    context.CandidateState = new ScheduledState(DrainMode.HoldInterval)
                    {
                        Reason = String.Format(System.Globalization.CultureInfo.InvariantCulture, ServerHeldReasonFormat, serverId),
                    };
                    return;
                }
            }

            if (status.PausedQueues.Count == 0) return;

            // Only when some queue is paused: find the queue the job was fetched from
            var queue = GetQueue(context);
            if (queue != null && status.PausedQueues.ContainsKey(queue))
            {
                context.CandidateState = new ScheduledState(DrainMode.HoldInterval)
                {
                    Reason = String.Format(System.Globalization.CultureInfo.InvariantCulture, QueueHeldReasonFormat, queue),
                };
            }
        }

        private static string? GetQueue(ElectStateContext context)
        {
            try
            {
                var state = context.Connection.GetStateData(context.BackgroundJob.Id);
                if (state?.Data != null && state.Data.TryGetValue("Queue", out var queue) && !String.IsNullOrEmpty(queue)) return queue;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // A storage hiccup must not hold the job
            }

            return context.BackgroundJob.Job?.Queue;
        }
    }
}
