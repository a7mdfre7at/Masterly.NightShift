using Masterly.NightShift.States;

namespace Masterly.NightShift.Storage.Shared;

/// <summary>A state as a storage keeps it: its data as JSON, written the way SQL Server writes it.</summary>
internal sealed record StoredState(string Name, string? Reason, DateTime CreatedAt, string? Data)
{
    public static StoredState From(IState state)
    {
        return new StoredState(state.Name, state.Reason, JobClock.UtcNow, SerializationHelper.Serialize(state.SerializeData()));
    }

    public Dictionary<string, string> ReadData()
    {
        Dictionary<string, string>? data = Data != null ? SerializationHelper.Deserialize<Dictionary<string, string>>(Data) : null;
        return data != null
            ? new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// A job as a storage keeps it: its payload without the arguments, the arguments, its parameters and its current
/// state. <see cref="History"/> is filled only when a reader asks for it (job details), newest state first.
/// </summary>
internal sealed class StoredJob
{
    public long Id { get; set; }

    public string InvocationData { get; set; } = null!;

    public string? Arguments { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ExpireAt { get; set; }

    public string? StateName { get; set; }

    public StoredState? State { get; set; }

    public Dictionary<string, string?> Parameters { get; set; } = new(StringComparer.Ordinal);

    public List<StoredState>? History { get; set; }

    /// <summary>A new job, serialized as SQL Server serializes it: the payload without the arguments, then the arguments.</summary>
    public static StoredJob Create(long id, Job job, IDictionary<string, string?> parameters, DateTime createdAt, DateTime expireAt)
    {
        InvocationData invocationData = Storage.InvocationData.SerializeJob(job);

        return new StoredJob
        {
            Id = id,
            InvocationData = invocationData.SerializePayload(excludeArguments: true)!, // Never null for a job
            Arguments = invocationData.Arguments,
            CreatedAt = createdAt,
            ExpireAt = expireAt,
            Parameters = new Dictionary<string, string?>(parameters, StringComparer.Ordinal),
        };
    }

    public Job? Deserialize(out InvocationData data, out JobLoadException? exception)
    {
        data = Storage.InvocationData.DeserializePayload(InvocationData);
        if (!string.IsNullOrEmpty(Arguments))
        {
            data.Arguments = Arguments;
        }

        try
        {
            exception = null;
            return data.DeserializeJob();
        }
        catch (JobLoadException ex)
        {
            exception = ex;
            return null;
        }
    }

    public JobData ToJobData()
    {
        Job? job = Deserialize(out InvocationData data, out JobLoadException? exception);

        Dictionary<string, string> parameters = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string?> parameter in Parameters)
        {
            parameters[parameter.Key] = parameter.Value!; // A parameter set to null reads back as null, as on SQL Server
        }

        return new JobData
        {
            Job = job,
            InvocationData = data,
            State = StateName,
            CreatedAt = CreatedAt,
            LoadException = exception,
            ParametersSnapshot = parameters,
        };
    }

    public StateData? ToStateData()
    {
        return State == null
            ? null
            : new StateData { Name = State.Name, Reason = State.Reason, Data = State.ReadData() };
    }
}

/// <summary>Job ids and expiration times, shared by the storages.</summary>
internal static class StorageValues
{
    /// <summary>
    /// Parses a job id. Ids are numbers, as on SQL Server, so an id that isn't one names no job: reads return
    /// <c>null</c> for it and writes do nothing.
    /// </summary>
    public static bool TryParseJobId(string jobId, out long id)
    {
        return long.TryParse(jobId, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    public static string FormatJobId(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The time something expires, clamped so a long period, or a long negative one, doesn't overflow.</summary>
    public static DateTime ExpireAt(DateTime from, TimeSpan expireIn)
    {
        if (expireIn >= DateTime.MaxValue - from)
        {
            return DateTime.SpecifyKind(DateTime.MaxValue, from.Kind);
        }

        if (expireIn <= DateTime.MinValue - from)
        {
            return DateTime.SpecifyKind(DateTime.MinValue, from.Kind);
        }

        return from.Add(expireIn);
    }

    /// <summary>The time left before <paramref name="expireAt"/>, or -1 second when it doesn't expire, as SQL Server answers.</summary>
    public static TimeSpan TimeToLive(DateTime? expireAt)
    {
        return expireAt.HasValue ? expireAt.Value - JobClock.UtcNow : TimeSpan.FromSeconds(-1);
    }

    /// <summary>The key of a server's data: its worker count, queues and start time, as JSON.</summary>
    public static string SerializeServer(ServerContext context)
    {
        return SerializationHelper.Serialize(new StoredServerData
        {
            WorkerCount = context.WorkerCount,
            Queues = context.Queues,
            StartedAt = JobClock.UtcNow,
        });
    }

    public static StoredServerData DeserializeServer(string? data)
    {
        return (data != null ? SerializationHelper.Deserialize<StoredServerData>(data) : null) ?? new StoredServerData();
    }
}

/// <summary>What a server announces, stored as JSON.</summary>
internal sealed class StoredServerData
{
    public int WorkerCount { get; set; }

    public string[]? Queues { get; set; }

    public DateTime? StartedAt { get; set; }
}
