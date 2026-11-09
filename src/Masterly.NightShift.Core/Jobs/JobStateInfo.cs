#nullable enable

namespace Masterly.NightShift;

/// <summary>
/// A job's current state, as <see cref="JobStorageExtensions.GetJobState(JobStorage, string)"/> reads it.
/// </summary>
/// <param name="name">The state's name, such as <c>Succeeded</c>.</param>
/// <param name="reason">The reason given for the state, or <c>null</c>.</param>
/// <param name="data">The state's data.</param>
/// <param name="jobCreatedAt">When the job was created, in UTC.</param>
public sealed class JobStateInfo([NotNull] string name, [CanBeNull] string? reason, [NotNull] IReadOnlyDictionary<string, string> data, DateTime jobCreatedAt)
{

    /// <summary>The state's name, such as <c>Enqueued</c>, <c>Succeeded</c> or <c>Failed</c>.</summary>
    [NotNull]
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>The reason given for the state, or <c>null</c>.</summary>
    [CanBeNull]
    public string? Reason { get; } = reason;

    /// <summary>
    /// The state's data, by name (ignoring case), as the state serialized it: for example <c>SucceededAt</c> and
    /// <c>Result</c> for a succeeded job, <c>ExceptionType</c> and <c>ExceptionMessage</c> for a failed one.
    /// </summary>
    [NotNull]
    public IReadOnlyDictionary<string, string> Data { get; } = data ?? throw new ArgumentNullException(nameof(data));

    /// <summary>When the job was created, in UTC.</summary>
    public DateTime JobCreatedAt { get; } = jobCreatedAt;
}
