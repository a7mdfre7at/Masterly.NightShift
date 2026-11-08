#nullable enable

using System;
using System.Collections.Generic;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift
{
    /// <summary>What an alert is about.</summary>
    [Flags]
    public enum AlertKind
    {
        /// <summary>No alert.</summary>
        None = 0,

        /// <summary>A job reached the Failed state, that is, its retries are exhausted or it has none.</summary>
        JobFailed = 1,

        /// <summary>A job with a <see cref="DeadlineAttribute"/> finished after its deadline.</summary>
        DeadlineMissed = 2,

        /// <summary>A <see cref="CircuitBreakerAttribute"/> circuit opened.</summary>
        CircuitOpened = 4,

        /// <summary>Every kind.</summary>
        All = JobFailed | DeadlineMissed | CircuitOpened,
    }

    /// <summary>Something an operator should know about.</summary>
    public sealed class Alert
    {
        /// <summary>Creates an alert.</summary>
        public Alert(AlertKind kind, [NotNull] string title, [NotNull] string message)
        {
            Kind = kind;
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Message = message ?? throw new ArgumentNullException(nameof(message));
            OccurredAt = DateTime.UtcNow;
        }

        /// <summary>The kind.</summary>
        public AlertKind Kind { get; }

        /// <summary>A one-line title.</summary>
        public string Title { get; }

        /// <summary>The details.</summary>
        public string Message { get; }

        /// <summary>When it happened, UTC.</summary>
        public DateTime OccurredAt { get; set; }

        /// <summary>The job it is about, if any.</summary>
        public string? JobId { get; set; }

        /// <summary><c>Type.Method</c> of the job, if known.</summary>
        public string? Method { get; set; }

        /// <summary>The resource (circuit) it is about, if any.</summary>
        public string? Resource { get; set; }

        /// <summary>The server that reported it, if known.</summary>
        public string? ServerId { get; set; }
    }

    /// <summary>Receives alerts. Implementations must not throw and should return quickly.</summary>
    public interface IAlertSink
    {
        /// <summary>Handles one alert.</summary>
        void Publish([NotNull] Alert alert);
    }

    /// <summary>
    /// The alert bus: built-in filters raise alerts here, registered sinks (<see cref="WebhookAlertSink"/> or your
    /// own) deliver them. Without sinks, raising is free.
    /// </summary>
    public static class Alerts
    {
        private static readonly ILog Logger = LogProvider.For<Alert>();
        private static readonly object SyncRoot = new object();
        private static IAlertSink[] _sinks = Array.Empty<IAlertSink>();

        /// <summary>Whether at least one sink is registered.</summary>
        public static bool HasSinks => _sinks.Length > 0;

        /// <summary>The registered sinks.</summary>
        public static IReadOnlyList<IAlertSink> Sinks => _sinks;

        /// <summary>Registers a sink.</summary>
        public static void AddSink([NotNull] IAlertSink sink)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));

            lock (SyncRoot)
            {
                var sinks = new List<IAlertSink>(_sinks) { sink };
                _sinks = sinks.ToArray();
            }
        }

        /// <summary>Removes every sink.</summary>
        public static void Clear()
        {
            lock (SyncRoot)
            {
                _sinks = Array.Empty<IAlertSink>();
            }
        }

        /// <summary>Delivers an alert to every sink. A failing sink is logged and skipped.</summary>
        public static void Raise([NotNull] Alert alert)
        {
            if (alert == null) throw new ArgumentNullException(nameof(alert));

            foreach (var sink in _sinks)
            {
                try
                {
                    sink.Publish(alert);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    Logger.WarnException($"Alert sink {sink.GetType().Name} failed to publish '{alert.Title}'.", ex);
                }
            }
        }
    }

    /// <summary>
    /// Raises <see cref="AlertKind.JobFailed"/> when a job enters the Failed state. Added to the global filters by
    /// <c>UseWebhookAlerts</c> / <c>UseAlerts</c>.
    /// </summary>
    /// <remarks>
    /// The alert is raised after the state change is committed when the change goes through the state changer
    /// (workers, <see cref="IBackgroundJobClient.ChangeState"/>), so a state change that fails and is retried
    /// alerts once. Other callers of the state machine get it while the state is applied.
    /// </remarks>
    public sealed class AlertsFilter : JobFilterAttribute, IApplyStateFilter
    {
        /// <inheritdoc />
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!Alerts.HasSinks) return;
            if (context.NewState is not FailedState failed) return;

            var job = context.BackgroundJob.Job;
            var method = job != null ? $"{job.Type.Name}.{job.Method.Name}" : null;

            var alert = new Alert(
                AlertKind.JobFailed,
                $"Job {context.BackgroundJob.Id} failed{(method != null ? $": {method}" : String.Empty)}",
                $"{failed.Exception.GetType().Name}: {failed.Exception.Message}")
            {
                JobId = context.BackgroundJob.Id,
                Method = method,
                ServerId = failed.ServerId,
            };

            context.RunAfterCommit(() => Alerts.Raise(alert));
        }

        /// <inheritdoc />
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
        }
    }
}
