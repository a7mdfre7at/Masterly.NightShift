#nullable enable

using System;
using System.Globalization;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
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

        private static readonly ILog Logger = LogProvider.GetLogger(typeof(DeadlineAttribute));

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
                var missedBy = now - deadline.Value;

                // Only a record: a storage error must not fail the state change of a job that succeeded
                try
                {
                    context.SetJobParameter(DeadlineMissedParameter, missedBy);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    Logger.WarnException(
                        $"Background job '{context.BackgroundJob.Id}' missed its deadline, but the '{DeadlineMissedParameter}' parameter could not be set.",
                        ex);
                }

                if (Alerts.HasSinks)
                {
                    var job = context.BackgroundJob.Job;
                    var method = job != null ? $"{job.Type.Name}.{job.Method.Name}" : null;

                    var alert = new Alert(
                        AlertKind.DeadlineMissed,
                        $"Job {context.BackgroundJob.Id} missed its deadline{(method != null ? $": {method}" : String.Empty)}",
                        $"Finished {(int)missedBy.TotalSeconds} seconds after the deadline of {Timeout}.")
                    {
                        JobId = context.BackgroundJob.Id,
                        Method = method,
                    };

                    // Sent once the state change is stored, so a retried state change doesn't alert twice
                    context.RunAfterCommit(() => Alerts.Raise(alert));
                }
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
