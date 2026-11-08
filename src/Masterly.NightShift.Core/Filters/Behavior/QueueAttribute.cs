// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
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

using System;
using System.Globalization;
using System.Linq;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;

namespace Masterly.NightShift
{
    /// <summary>
    /// Represents attribute, that is used to determine queue name
    /// for background jobs. It can be applied to the methods and classes. 
    /// If the attribute is not applied neither to the method, nor the class, 
    /// then default queue will be used.
    /// </summary>
    /// 
    /// <example><![CDATA[
    /// 
    /// [Queue("high")]
    /// public class ErrorService
    /// {
    ///     public void ReportError(string message) { }
    /// 
    ///     [Queue("critical")]
    ///     public void ReportFatal(string message) { }
    /// }
    /// 
    /// // Background job will be placed on the 'high' queue.
    /// BackgroundJob.Enqueue<ErrorService>(x => x.ReportError("Something bad happened"));
    /// 
    /// // Background job will be placed on the 'critical' queue.
    /// BackgroundJob.Enqueue<ErrorService>(x => x.ReportFatal("Really bad thing!"));
    /// 
    /// ]]></example>
    public sealed class QueueAttribute : JobFilterAttribute, IElectStateFilter
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="QueueAttribute"/> class
        /// using the specified queue name.
        /// </summary>
        /// <param name="queue">
        /// Queue name: lowercase letters, digits, underscores and dashes. Or a template where <c>{0}</c>,
        /// <c>{1}</c>... are the job's arguments; its result is checked when the job is enqueued.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="queue"/> is <c>null</c> or empty.</exception>
        /// <exception cref="ArgumentException"><paramref name="queue"/> is neither a valid queue name nor a template.</exception>
        public QueueAttribute(string queue)
        {
            if (!IsTemplate(queue))
            {
                EnqueuedState.ValidateQueueName(nameof(queue), queue);
            }

            Queue = queue;
            Order = Int32.MaxValue;
        }

        /// <summary>
        /// Gets the queue name that will be used for background jobs.
        /// </summary>
        public string Queue { get; }

        /// <inheritdoc />
        /// <exception cref="FormatException">The template refers to an argument the job doesn't have.</exception>
        /// <exception cref="InvalidOperationException">The template doesn't produce a valid queue name.</exception>
        public void OnStateElection(ElectStateContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.CandidateState is not EnqueuedState enqueuedState) return;

            if (!IsTemplate(Queue))
            {
                enqueuedState.Queue = Queue;
                return;
            }

            var job = context.BackgroundJob.Job;
            if (job == null) return; // The arguments can't be read, so the template can't be applied

            string queue;
            try
            {
                // The real values, [Redacted] ones included: a queue name must match the queues servers listen to,
                // which a hash of the value never would
                queue = String.Format(CultureInfo.InvariantCulture, Queue, job.Args.ToArray());
            }
            catch (FormatException ex)
            {
                throw new FormatException($"Unable to build a queue name from the template '{Queue}': {ex.Message}", ex);
            }

            if (String.IsNullOrWhiteSpace(queue) || !EnqueuedState.TryValidateQueueName(queue))
            {
                throw new InvalidOperationException(
                    $"The queue template '{Queue}' produced '{queue}' for background job '{context.BackgroundJob.Id}', which is not a valid queue name. A queue name must consist of lowercase letters, digits, underscore, and dash characters only.");
            }

            enqueuedState.Queue = queue;
        }

        private static bool IsTemplate(string queue)
        {
            return queue != null && queue.IndexOf('{') >= 0;
        }
    }
}
