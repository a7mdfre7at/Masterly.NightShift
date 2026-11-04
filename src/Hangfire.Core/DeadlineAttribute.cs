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
using System.Globalization;
using Hangfire.Annotations;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;

namespace Hangfire
{
    /// <summary>
    /// Gives a job a deadline, counted from its creation: the <c>DeadlineAt</c> job parameter. The dashboard
    /// highlights jobs that are still waiting or running past their deadline, and a job that finishes late
    /// gets the <c>DeadlineMissed</c> parameter, so the miss stays visible on the job details page. Nothing is
    /// canceled: a deadline is a service-level expectation, not a timeout (see <see cref="JobTimeoutAttribute"/>).
    /// </summary>
    public sealed class DeadlineAttribute : JobFilterAttribute, IClientFilter, IElectStateFilter
    {
        /// <summary>Job parameter holding the UTC deadline.</summary>
        public const string DeadlineAtParameter = "DeadlineAt";

        /// <summary>Job parameter set when the job finished after its deadline, holding how late it was.</summary>
        public const string DeadlineMissedParameter = "DeadlineMissed";

        /// <summary>Replaceable for tests.</summary>
        internal static Func<DateTime> Clock { get; set; } = static () => DateTime.UtcNow;

        /// <summary>
        /// Initializes the attribute.
        /// </summary>
        /// <param name="timeout">Time after creation by which the job should be done, like <c>"00:10:00"</c>.</param>
        public DeadlineAttribute([NotNull] string timeout)
        {
            if (timeout == null) throw new ArgumentNullException(nameof(timeout));

            Timeout = TimeSpan.Parse(timeout, CultureInfo.InvariantCulture);
            if (Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "The deadline should be greater than zero.");
        }

        /// <summary>
        /// Initializes the attribute.
        /// </summary>
        /// <param name="timeoutInSeconds">Seconds after creation by which the job should be done.</param>
        public DeadlineAttribute(int timeoutInSeconds)
        {
            if (timeoutInSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutInSeconds), "The deadline should be greater than zero.");
            Timeout = TimeSpan.FromSeconds(timeoutInSeconds);
        }

        /// <summary>Gets the time after creation by which the job should be done.</summary>
        public TimeSpan Timeout { get; }

        /// <inheritdoc />
        public void OnCreating(CreatingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            context.SetJobParameter(DeadlineAtParameter, Clock().Add(Timeout));
        }

        /// <inheritdoc />
        public void OnCreated(CreatedContext context)
        {
        }

        /// <inheritdoc />
        public void OnStateElection(ElectStateContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.CandidateState is not SucceededState) return;

            var deadline = ReadDeadline(context.Connection, context.BackgroundJob.Id);
            if (deadline == null) return;

            var now = Clock();
            if (now > deadline.Value)
            {
                context.SetJobParameter(DeadlineMissedParameter, now - deadline.Value);
            }
        }

        /// <summary>Reads a job's deadline, or <c>null</c> when it has none.</summary>
        public static DateTime? ReadDeadline([NotNull] IStorageConnection connection, [NotNull] string jobId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            try
            {
                var value = connection.GetJobParameter(jobId, DeadlineAtParameter);
                return value == null ? null : SerializationHelper.Deserialize<DateTime?>(value, SerializationOption.User);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return null;
            }
        }
    }
}
