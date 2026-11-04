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

#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;

namespace Hangfire.Diagnostics
{
    /// <summary>
    /// Emits traces and metrics for background jobs, see <see cref="HangfireTelemetry"/>.
    /// The trace context of the code that creates a job is stored in the job's parameters,
    /// so the span of its execution joins the same trace.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class TelemetryFilter : JobFilterAttribute, IClientFilter, IServerFilter
    {
        internal const string TraceParentParameter = "TraceParent";
        internal const string TraceStateParameter = "TraceState";

        internal const string OutcomeSucceeded = "succeeded";
        internal const string OutcomeFailed = "failed";
        internal const string OutcomeCanceled = "canceled";

        private const string CreateActivityKey = "Hangfire.Telemetry.CreateActivity";
        private const string ProcessActivityKey = "Hangfire.Telemetry.ProcessActivity";
        private const string ProcessStartedKey = "Hangfire.Telemetry.ProcessStarted";
        private const string ActiveCountedKey = "Hangfire.Telemetry.ActiveCounted";

        public void OnCreating(CreatingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var jobName = GetJobName(context.Job);
            var activity = HangfireTelemetry.ActivitySource.StartActivity($"create {jobName}", ActivityKind.Producer);

            if (activity != null)
            {
                context.Items[CreateActivityKey] = activity;

                if (activity.IsAllDataRequested)
                {
                    SetCommonTags(activity, "create", context.Job, GetQueue(context.Job, context.InitialState));
                    activity.SetTag("hangfire.job.state", context.InitialState?.Name);
                }
            }

            // Propagate even when nobody listens to Hangfire, so the job still joins the caller's trace
            var traceContext = activity ?? Activity.Current;
            if (traceContext != null && traceContext.IdFormat == ActivityIdFormat.W3C)
            {
                context.SetJobParameter(TraceParentParameter, traceContext.Id);

                if (!String.IsNullOrEmpty(traceContext.TraceStateString))
                {
                    context.SetJobParameter(TraceStateParameter, traceContext.TraceStateString);
                }
            }
        }

        public void OnCreated(CreatedContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var failed = context.Exception != null && !context.ExceptionHandled;

            if (HangfireTelemetry.JobsCreated.Enabled && !context.Canceled)
            {
                HangfireTelemetry.JobsCreated.Add(
                    1,
                    Tag("hangfire.job.name", GetJobName(context.Job)),
                    Tag("hangfire.job.state", context.InitialState?.Name),
                    Tag("error.type", failed ? GetErrorType(context.Exception) : null));
            }

            if (context.Items.TryGetValue(CreateActivityKey, out var value) && value is Activity activity)
            {
                if (activity.IsAllDataRequested && context.BackgroundJob != null)
                {
                    activity.SetTag("messaging.message.id", context.BackgroundJob.Id);
                }

                if (failed)
                {
                    SetError(activity, context.Exception);
                }

                activity.Stop();
            }
        }

        public void OnPerforming(PerformingContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var job = context.BackgroundJob.Job;
            var jobName = GetJobName(job);

            if (HangfireTelemetry.ActivitySource.HasListeners())
            {
                // Reading parameters may query the storage, so do it only when someone listens
                var traceParent = context.GetJobParameter<string>(TraceParentParameter, allowStale: true);
                var traceState = context.GetJobParameter<string>(TraceStateParameter, allowStale: true);

                ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var parentContext);

                var activity = HangfireTelemetry.ActivitySource.StartActivity(
                    $"process {jobName}",
                    ActivityKind.Consumer,
                    parentContext);

                if (activity != null)
                {
                    context.Items[ProcessActivityKey] = activity;

                    if (activity.IsAllDataRequested)
                    {
                        SetCommonTags(activity, "process", job, job?.Queue);
                        activity.SetTag("messaging.message.id", context.BackgroundJob.Id);
                    }
                }
            }

            if (HangfireTelemetry.ActiveJobs.Enabled)
            {
                HangfireTelemetry.ActiveJobs.Add(1, Tag("hangfire.job.name", jobName));

                // Decrement only what was incremented, even if a listener attaches in between
                context.Items[ActiveCountedKey] = true;
            }

            if (HangfireTelemetry.JobDuration.Enabled)
            {
                context.Items[ProcessStartedKey] = Stopwatch.GetTimestamp();
            }
        }

        public void OnPerformed(PerformedContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var outcome = GetOutcome(context);
            var errorType = outcome == OutcomeFailed ? GetErrorType(context.Exception) : null;

            var jobName = GetJobName(context.BackgroundJob.Job);

            if (context.Items.ContainsKey(ActiveCountedKey))
            {
                HangfireTelemetry.ActiveJobs.Add(-1, Tag("hangfire.job.name", jobName));
            }

            if (context.Items.TryGetValue(ProcessStartedKey, out var started) && started is long startedAt)
            {
                HangfireTelemetry.JobDuration.Record(
                    Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                    Tag("hangfire.job.name", jobName),
                    Tag("hangfire.job.outcome", outcome),
                    Tag("error.type", errorType));
            }

            if (context.Items.TryGetValue(ProcessActivityKey, out var value) && value is Activity activity)
            {
                if (activity.IsAllDataRequested)
                {
                    activity.SetTag("hangfire.job.outcome", outcome);
                }

                if (outcome == OutcomeFailed)
                {
                    SetError(activity, context.Exception);
                }

                activity.Stop();
            }
        }

        internal static string GetOutcome(PerformedContext context)
        {
            if (context.Canceled) return OutcomeCanceled;
            if (context.Exception == null || context.ExceptionHandled) return OutcomeSucceeded;

            // JobAbortedException is an OperationCanceledException too
            return context.Exception is OperationCanceledException ? OutcomeCanceled : OutcomeFailed;
        }

        private static void SetCommonTags(Activity activity, string operation, Job job, string queue)
        {
            activity.SetTag("messaging.system", "hangfire");
            activity.SetTag("messaging.operation.name", operation);
            activity.SetTag("messaging.operation.type", operation);
            activity.SetTag("messaging.destination.name", queue);
            activity.SetTag("hangfire.job.name", GetJobName(job));

            if (job?.Type != null)
            {
                activity.SetTag("code.function.name", $"{job.Type.FullName}.{job.Method?.Name}");
            }
        }

        private static void SetError(Activity activity, Exception exception)
        {
            var error = Unwrap(exception);
            activity.SetStatus(ActivityStatusCode.Error, error.Message);

            if (!activity.IsAllDataRequested) return;

            activity.SetTag("error.type", error.GetType().FullName);
#if NET9_0_OR_GREATER
            activity.AddException(error);
#else
            activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", error.GetType().FullName },
                { "exception.message", error.Message },
                { "exception.stacktrace", error.ToString() }
            }));
#endif
        }

        private static string GetErrorType(Exception exception)
        {
            return exception != null ? Unwrap(exception).GetType().FullName : null;
        }

        private static Exception Unwrap(Exception exception)
        {
            // Job method exceptions reach server filters wrapped, the original one is more useful
            return exception is JobPerformanceException && exception.InnerException != null
                ? exception.InnerException
                : exception;
        }

        private static KeyValuePair<string, object> Tag(string key, object value)
        {
            return new KeyValuePair<string, object>(key, value);
        }

        private static string GetJobName(Job job)
        {
            return job?.Type != null ? $"{job.Type.Name}.{job.Method?.Name}" : null;
        }

        private static string GetQueue(Job job, IState initialState)
        {
            return job?.Queue ?? (initialState as EnqueuedState)?.Queue;
        }
    }
}
#endif
