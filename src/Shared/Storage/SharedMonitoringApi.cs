using Masterly.NightShift.States;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Storage.Shared;

/// <summary>
/// The monitoring API built on a few queries each storage answers: jobs by state (ordered by id), jobs by id,
/// queue contents, servers, counters and set sizes. It fills the dashboard's lists the way SQL Server does.
/// </summary>
internal abstract class SharedMonitoringApi : JobStorageMonitor
{
    /// <summary>The number of jobs whose current state is <paramref name="stateName"/>.</summary>
    protected abstract long CountJobs(string stateName);

    /// <summary>A page of the jobs in a state, ordered by id, with their current state.</summary>
    protected abstract IReadOnlyList<StoredJob> GetJobs(string stateName, int from, int count, bool descending);

    /// <summary>The jobs with these ids and their current states; a job that doesn't exist is left out.</summary>
    protected abstract IReadOnlyList<StoredJob> GetJobs(IReadOnlyCollection<long> ids);

    /// <summary>A job with its parameters and its whole history, newest state first.</summary>
    protected abstract StoredJob? GetJobWithHistory(long id);

    protected abstract IReadOnlyList<string> GetQueues();

    protected abstract IReadOnlyList<long> GetEnqueuedJobIds(string queue, int from, int count);

    protected abstract IReadOnlyList<long> GetFetchedJobIds(string queue, int from, int count);

    protected abstract (long Enqueued, long Fetched) GetQueueCounts(string queue);

    protected abstract IReadOnlyList<ServerDto> GetServers();

    /// <summary>The values of these counters; a counter that doesn't exist is 0 or left out.</summary>
    protected abstract IReadOnlyDictionary<string, long> GetCounters(IReadOnlyCollection<string> keys);

    protected abstract long GetSetCount(string key);

    public override IList<QueueWithTopEnqueuedJobsDto> Queues() => ListQueues(withFirstJobs: true);

    public override IList<QueueWithTopEnqueuedJobsDto> QueueCounts() => ListQueues(withFirstJobs: false);

    public override IList<ServerDto> Servers() => [.. GetServers()];

    public override JobDetailsDto? JobDetails(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        if (!StorageValues.TryParseJobId(jobId, out long id)) return null;

        StoredJob? job = GetJobWithHistory(id);
        if (job == null) return null;

        Dictionary<string, string?> parameters = new(job.Parameters, StringComparer.Ordinal);
        Job? deserialized = job.Deserialize(out InvocationData payload, out JobLoadException? exception);
        if (deserialized == null)
        {
            parameters["DBG_Type"] = payload.Type;
            parameters["DBG_Method"] = payload.Method;
            parameters["DBG_Args"] = payload.Arguments;
        }

        if (exception != null)
        {
            parameters["DBG_Exception"] = (exception.InnerException ?? exception).Message;
        }

        return new JobDetailsDto
        {
            CreatedAt = job.CreatedAt,
            ExpireAt = job.ExpireAt,
            Job = deserialized,
            InvocationData = payload,
            LoadException = exception,
            History = [.. (job.History ?? []).Select(static x => new StateHistoryDto
            {
                StateName = x.Name,
                CreatedAt = x.CreatedAt,
                Reason = x.Reason,
                Data = new SafeDictionary(x.ReadData()),
            })],
            Properties = parameters!, // A parameter set to null reads back as null
        };
    }

    public override StatisticsDto GetStatistics()
    {
        IReadOnlyDictionary<string, long> counters = GetCounters(["stats:succeeded", "stats:deleted"]);

        return new StatisticsDto
        {
            Enqueued = CountJobs(EnqueuedState.StateName),
            Failed = CountJobs(FailedState.StateName),
            Processing = CountJobs(ProcessingState.StateName),
            Scheduled = CountJobs(ScheduledState.StateName),
            Awaiting = CountJobs(AwaitingState.StateName),
            Servers = GetServers().Count,
            Succeeded = counters.GetValueOrDefault("stats:succeeded"),
            Deleted = counters.GetValueOrDefault("stats:deleted"),
            Recurring = GetSetCount("recurring-jobs"),
            Retries = GetSetCount("retries"),
            Queues = GetQueues().Count,
        };
    }

    public override JobList<EnqueuedJobDto?> EnqueuedJobs(string queue, int from, int perPage)
    {
        ArgumentNullException.ThrowIfNull(queue);
        IReadOnlyList<long> ids = GetEnqueuedJobIds(queue, from, perPage);
        return EnqueuedJobs(ids);
    }

    public override JobList<FetchedJobDto?> FetchedJobs(string queue, int from, int perPage)
    {
        ArgumentNullException.ThrowIfNull(queue);
        IReadOnlyList<long> ids = GetFetchedJobIds(queue, from, perPage);
        Dictionary<long, StoredJob> jobs = GetJobs(ids).ToDictionary(static x => x.Id);

        return new JobList<FetchedJobDto?>(ids
            .Where(jobs.ContainsKey)
            .Select(id => new KeyValuePair<string, FetchedJobDto?>(
                StorageValues.FormatJobId(id),
                new FetchedJobDto
                {
                    Job = jobs[id].Deserialize(out _, out _),
                    State = jobs[id].StateName,
                })));
    }

    public override JobList<ProcessingJobDto?> ProcessingJobs(int from, int count)
    {
        return ListJobs(ProcessingState.StateName, from, count, descending: false, static (stored, job, data, exception, state) => new ProcessingJobDto
        {
            Job = job,
            LoadException = exception,
            InvocationData = data,
            InProcessingState = IsIn(stored, ProcessingState.StateName),
            ServerId = state.TryGetValue("ServerId", out string? serverId) ? serverId : state["ServerName"],
            StartedAt = stored.State?.CreatedAt,
            StateData = state,
        });
    }

    public override JobList<ScheduledJobDto?> ScheduledJobs(int from, int count)
    {
        return ListJobs(ScheduledState.StateName, from, count, descending: false, static (stored, job, data, exception, state) => new ScheduledJobDto
        {
            Job = job,
            LoadException = exception,
            InvocationData = data,
            InScheduledState = IsIn(stored, ScheduledState.StateName),
            EnqueueAt = JobHelper.DeserializeNullableDateTime(state["EnqueueAt"]) ?? DateTime.MinValue,
            ScheduledAt = stored.State?.CreatedAt,
            StateData = state,
        });
    }

    public override JobList<SucceededJobDto?> SucceededJobs(int from, int count)
    {
        return ListJobs(SucceededState.StateName, from, count, descending: true, static (stored, job, data, exception, state) => new SucceededJobDto
        {
            Job = job,
            LoadException = exception,
            InvocationData = data,
            InSucceededState = IsIn(stored, SucceededState.StateName),
            Result = state["Result"],
            TotalDuration = state.TryGetValue("PerformanceDuration", out string? duration) && state.TryGetValue("Latency", out string? latency)
                ? long.Parse(duration, CultureInfo.InvariantCulture) + long.Parse(latency, CultureInfo.InvariantCulture)
                : null,
            SucceededAt = stored.State?.CreatedAt,
            StateData = state,
        });
    }

    public override JobList<FailedJobDto?> FailedJobs(int from, int count)
    {
        return ListJobs(FailedState.StateName, from, count, descending: true, static (stored, job, data, exception, state) => new FailedJobDto
        {
            Job = job,
            LoadException = exception,
            InvocationData = data,
            InFailedState = IsIn(stored, FailedState.StateName),
            Reason = stored.State?.Reason,
            ExceptionDetails = state["ExceptionDetails"],
            ExceptionMessage = state["ExceptionMessage"],
            ExceptionType = state["ExceptionType"],
            FailedAt = stored.State?.CreatedAt,
            StateData = state,
        });
    }

    public override JobList<DeletedJobDto?> DeletedJobs(int from, int count)
    {
        return ListJobs(DeletedState.StateName, from, count, descending: true, static (stored, job, data, exception, state) => new DeletedJobDto
        {
            Job = job,
            LoadException = exception,
            InvocationData = data,
            InDeletedState = IsIn(stored, DeletedState.StateName),
            DeletedAt = stored.State?.CreatedAt,
            StateData = state,
        });
    }

    public override JobList<AwaitingJobDto?> AwaitingJobs(int from, int count)
    {
        JobList<AwaitingJobDto?> jobs = ListJobs(AwaitingState.StateName, from, count, descending: false, static (stored, job, data, exception, state) => new AwaitingJobDto
        {
            Job = job,
            LoadException = exception,
            InvocationData = data,
            InAwaitingState = IsIn(stored, AwaitingState.StateName),
            AwaitingAt = stored.State?.CreatedAt,
            StateData = state,
        });

        List<long> parentIds = [];
        foreach (KeyValuePair<string, AwaitingJobDto?> pair in jobs)
        {
            if (TryGetParentId(pair.Value, out long parentId))
            {
                parentIds.Add(parentId);
            }
        }

        Dictionary<long, string?> parentStates = GetJobs(parentIds).ToDictionary(static x => x.Id, static x => x.StateName);
        foreach (KeyValuePair<string, AwaitingJobDto?> pair in jobs)
        {
            if (TryGetParentId(pair.Value, out long parentId) && parentStates.TryGetValue(parentId, out string? parentState))
            {
                pair.Value!.ParentStateName = parentState; // TryGetParentId returns false for null
            }
        }

        return jobs;
    }

    public override long ScheduledCount() => CountJobs(ScheduledState.StateName);

    public override long EnqueuedCount(string queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return GetQueueCounts(queue).Enqueued;
    }

    public override long FetchedCount(string queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return GetQueueCounts(queue).Fetched;
    }

    public override long FailedCount() => CountJobs(FailedState.StateName);

    public override long ProcessingCount() => CountJobs(ProcessingState.StateName);

    public override long SucceededListCount() => CountJobs(SucceededState.StateName);

    public override long DeletedListCount() => CountJobs(DeletedState.StateName);

    public override long AwaitingCount() => CountJobs(AwaitingState.StateName);

    public override IDictionary<DateTime, long> SucceededByDatesCount() => Daily("succeeded");

    public override IDictionary<DateTime, long> FailedByDatesCount() => Daily("failed");

    public override IDictionary<DateTime, long> DeletedByDatesCount() => Daily("deleted");

    public override IDictionary<DateTime, long> HourlySucceededJobs() => Hourly("succeeded");

    public override IDictionary<DateTime, long> HourlyFailedJobs() => Hourly("failed");

    public override IDictionary<DateTime, long> HourlyDeletedJobs() => Hourly("deleted");

    private List<QueueWithTopEnqueuedJobsDto> ListQueues(bool withFirstJobs)
    {
        List<QueueWithTopEnqueuedJobsDto> result = [];
        foreach (string queue in GetQueues().Order(StringComparer.Ordinal))
        {
            (long enqueued, long fetched) = GetQueueCounts(queue);
            result.Add(new QueueWithTopEnqueuedJobsDto
            {
                Name = queue,
                Length = enqueued,
                Fetched = fetched,
                FirstJobs = withFirstJobs ? EnqueuedJobs(GetEnqueuedJobIds(queue, 0, 5)) : new JobList<EnqueuedJobDto?>([]),
            });
        }

        return result;
    }

    private JobList<EnqueuedJobDto?> EnqueuedJobs(IReadOnlyList<long> ids)
    {
        Dictionary<long, StoredJob> jobs = GetJobs(ids).ToDictionary(static x => x.Id);

        return new JobList<EnqueuedJobDto?>(ids.Select(id =>
        {
            EnqueuedJobDto? dto = null;
            if (jobs.TryGetValue(id, out StoredJob? stored))
            {
                bool enqueued = IsIn(stored, EnqueuedState.StateName);
                dto = new EnqueuedJobDto
                {
                    Job = stored.Deserialize(out InvocationData data, out JobLoadException? exception),
                    LoadException = exception,
                    InvocationData = data,
                    State = stored.StateName,
                    InEnqueuedState = enqueued,
                    EnqueuedAt = enqueued ? stored.State?.CreatedAt : null,
                    StateData = stored.State != null ? new SafeDictionary(stored.State.ReadData()) : null,
                };
            }

            return new KeyValuePair<string, EnqueuedJobDto?>(StorageValues.FormatJobId(id), dto);
        }));
    }

    private JobList<TDto?> ListJobs<TDto>(
        string stateName,
        int from,
        int count,
        bool descending,
        Func<StoredJob, Job?, InvocationData, JobLoadException?, SafeDictionary, TDto> selector)
    {
        IReadOnlyList<StoredJob> jobs = GetJobs(stateName, from, count, descending);
        List<KeyValuePair<string, TDto?>> result = new(jobs.Count);

        foreach (StoredJob stored in jobs)
        {
            Job? job = stored.Deserialize(out InvocationData data, out JobLoadException? exception);
            SafeDictionary state = new(stored.State?.ReadData() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            result.Add(new KeyValuePair<string, TDto?>(StorageValues.FormatJobId(stored.Id), selector(stored, job, data, exception, state)));
        }

        return new JobList<TDto?>(result);
    }

    private Dictionary<DateTime, long> Daily(string type)
    {
        DateTime day = JobClock.UtcNow.Date;
        Dictionary<string, DateTime> keys = [];
        for (int i = 0; i < 7; i++)
        {
            keys[$"stats:{type}:{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"] = day;
            day = day.AddDays(-1);
        }

        return ReadTimeline(keys);
    }

    private Dictionary<DateTime, long> Hourly(string type)
    {
        DateTime hour = JobClock.UtcNow;
        Dictionary<string, DateTime> keys = [];
        for (int i = 0; i < 24; i++)
        {
            keys[$"stats:{type}:{hour.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture)}"] = hour;
            hour = hour.AddHours(-1);
        }

        return ReadTimeline(keys);
    }

    private Dictionary<DateTime, long> ReadTimeline(Dictionary<string, DateTime> keys)
    {
        IReadOnlyDictionary<string, long> values = GetCounters(keys.Keys);
        Dictionary<DateTime, long> result = [];
        foreach (KeyValuePair<string, DateTime> pair in keys)
        {
            result[pair.Value] = values.GetValueOrDefault(pair.Key);
        }

        return result;
    }

    private static bool IsIn(StoredJob job, string stateName)
    {
        return stateName.Equals(job.StateName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetParentId(AwaitingJobDto? job, out long parentId)
    {
        parentId = 0;
        return job != null
            && job.InAwaitingState
            && job.StateData != null
            && job.StateData.TryGetValue("ParentId", out string? value)
            && StorageValues.TryParseJobId(value, out parentId);
    }

    /// <summary>State data whose indexer answers <c>null</c> for a missing key, as the dashboard expects.</summary>
    private sealed class SafeDictionary(IDictionary<string, string> dictionary) : Dictionary<string, string>(dictionary, StringComparer.OrdinalIgnoreCase)
    {
        public new string? this[string key]
        {
            get => TryGetValue(key, out string? value) ? value : null;
            set => base[key] = value!; // Written as given, as the base class does
        }
    }
}
