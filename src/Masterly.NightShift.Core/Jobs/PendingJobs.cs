using System.Reflection;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift;

/// <summary>A job that hasn't started: enqueued, scheduled or waiting for other jobs.</summary>
/// <param name="Id">The job's id.</param>
/// <param name="State">Its state: <c>Enqueued</c>, <c>Scheduled</c> or <c>Awaiting</c>.</param>
/// <param name="Job">The method call, or <c>null</c> when it can't be loaded.</param>
/// <param name="Queue">The queue of an enqueued job, or <c>null</c>.</param>
/// <param name="EnqueueAt">When a scheduled job is due, UTC, or <c>null</c>.</param>
public sealed record PendingJob(string Id, string State, Job? Job, string? Queue, DateTime? EnqueueAt);

/// <summary>
/// Deletes jobs that haven't started, by method or by any condition: <c>storage.DeletePendingJobs&lt;Mailer&gt;()</c>
/// cancels every pending e-mail, <c>storage.DeletePendingJobs(x => x.Queue == "imports")</c> clears a queue's backlog.
/// </summary>
public static class PendingJobs
{
    private const int PageSize = 500;

    // The jobs of a delegate registered with AddNightShiftFunction (Masterly.NightShift.NetCore) run this method,
    // with the function's name first
    private const string DelegateRunnerTypeName = "Masterly.NightShift.DelegateJobFunctionRunner";
    private const string DelegateRunnerMethodName = "RunAsync";

    /// <param name="storage">The storage.</param>
    extension([NotNull] JobStorage storage)
    {
        /// <summary>
        /// Lists the jobs that haven't started: every queue's enqueued jobs, the scheduled jobs and, on storages that
        /// list them, the waiting continuations.
        /// </summary>
        [NotNull]
        public IReadOnlyList<PendingJob> GetPendingJobs()
        {
            ArgumentNullException.ThrowIfNull(storage);

            IMonitoringApi monitoring = storage.GetMonitoringApi();
            List<PendingJob> jobs = [];

            foreach (QueueWithTopEnqueuedJobsDto queue in monitoring.Queues())
            {
                if (queue.Name == null) continue;

                for (int from = 0; ; from += PageSize)
                {
                    JobList<EnqueuedJobDto?> page = monitoring.EnqueuedJobs(queue.Name, from, PageSize);
                    jobs.AddRange(page.Where(static x => x.Value is { InEnqueuedState: true })
                        .Select(x => new PendingJob(x.Key, EnqueuedState.StateName, x.Value!.Job, queue.Name, null))); // Filtered above
                    if (page.Count < PageSize) break;
                }
            }

            for (int from = 0; ; from += PageSize)
            {
                JobList<ScheduledJobDto?> page = monitoring.ScheduledJobs(from, PageSize);
                jobs.AddRange(page.Where(static x => x.Value is { InScheduledState: true })
                    .Select(static x => new PendingJob(x.Key, ScheduledState.StateName, x.Value!.Job, null, x.Value.EnqueueAt))); // Filtered above
                if (page.Count < PageSize) break;
            }

            if (monitoring is JobStorageMonitor monitor && storage.HasFeature(JobStorageFeatures.Monitoring.AwaitingJobs))
            {
                for (int from = 0; ; from += PageSize)
                {
                    JobList<AwaitingJobDto?> page = monitor.AwaitingJobs(from, PageSize);
                    jobs.AddRange(page.Where(static x => x.Value is { InAwaitingState: true })
                        .Select(static x => new PendingJob(x.Key, AwaitingState.StateName, x.Value!.Job, null, null))); // Filtered above
                    if (page.Count < PageSize) break;
                }
            }

            return jobs;
        }

        /// <summary>
        /// Deletes the pending jobs a condition selects, each only from the state it was listed in, so a job a worker
        /// takes meanwhile isn't touched. Returns the number deleted.
        /// </summary>
        /// <param name="filter">Selects the jobs to delete.</param>
        /// <param name="reason">The reason recorded with the deletion.</param>
        public int DeletePendingJobs([NotNull, InstantHandle] Func<PendingJob, bool> filter, [CanBeNull] string? reason = null)
        {
            ArgumentNullException.ThrowIfNull(storage);
            ArgumentNullException.ThrowIfNull(filter);

            List<PendingJob> selected = [.. storage.GetPendingJobs().Where(filter)];
            BackgroundJobClient client = new(storage);
            int deleted = 0;

            // Continuations first, so deleting a parent doesn't start them
            foreach (PendingJob job in selected.OrderBy(static x => x.State == AwaitingState.StateName ? 0 : 1))
            {
                if (client.ChangeState(job.Id, new DeletedState { Reason = reason ?? "Pending job deleted" }, job.State)) deleted++;
            }

            return deleted;
        }

        /// <summary>Deletes the pending jobs of a type's methods, or of one of them. Returns the number deleted.</summary>
        /// <typeparam name="T">The type whose jobs to delete.</typeparam>
        /// <param name="methodName">The method's name, or <c>null</c> for every method.</param>
        public int DeletePendingJobs<T>([CanBeNull] string? methodName = null)
        {
            return storage.DeletePendingJobs(job => Matches(job.Job, typeof(T), methodName), $"Pending {typeof(T).Name} jobs deleted");
        }

        /// <summary>
        /// Deletes the pending jobs of a <see cref="JobFunction"/>: a method marked with <see cref="JobFunctionAttribute"/>,
        /// or a delegate registered with <c>AddNightShiftFunction</c>. Returns the number deleted.
        /// </summary>
        /// <param name="functionName">The function's name, compared without case.</param>
        public int DeletePendingFunctionJobs([NotNull] string functionName)
        {
            ArgumentNullException.ThrowIfNull(functionName);

            string name = functionName.Trim();
            return storage.DeletePendingJobs(job => IsFunctionJob(job.Job, name), $"Pending '{name}' jobs deleted");
        }
    }

    /// <summary>Whether a job runs a function: its method is marked with the name, or it runs the delegate of that name.</summary>
    internal static bool IsFunctionJob(Job? job, string functionName)
    {
        if (job == null) return false;

        if (job.Method.GetCustomAttribute<JobFunctionAttribute>() is { } attribute)
        {
            return string.Equals(attribute.Name.Trim(), functionName, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(job.Type.FullName, DelegateRunnerTypeName, StringComparison.Ordinal) &&
               string.Equals(job.Method.Name, DelegateRunnerMethodName, StringComparison.Ordinal) &&
               job.Args.Count > 0 && job.Args[0] is string name &&
               string.Equals(name.Trim(), functionName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(Job? job, Type type, string? methodName)
    {
        return job != null && type.IsAssignableFrom(job.Type) &&
               (methodName == null || string.Equals(job.Method.Name, methodName, StringComparison.Ordinal));
    }
}
