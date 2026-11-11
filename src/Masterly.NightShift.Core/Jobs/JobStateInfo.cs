namespace Masterly.NightShift;

/// <summary>
/// A job's current state, as <see cref="JobStorageExtensions.GetJobState(JobStorage, string)"/> reads it.
/// </summary>
/// <param name="Name">The state's name, such as <c>Enqueued</c>, <c>Succeeded</c> or <c>Failed</c>.</param>
/// <param name="Reason">The reason given for the state, or <c>null</c>.</param>
/// <param name="Data">
/// The state's data, by name (ignoring case), as the state serialized it: for example <c>SucceededAt</c> and
/// <c>Result</c> for a succeeded job, <c>ExceptionType</c> and <c>ExceptionMessage</c> for a failed one.
/// </param>
/// <param name="JobCreatedAt">When the job was created, in UTC.</param>
public sealed record JobStateInfo(
    [NotNull] string Name,
    [CanBeNull] string? Reason,
    [NotNull] IReadOnlyDictionary<string, string> Data,
    DateTime JobCreatedAt)
{
    /// <summary>The state's name, such as <c>Enqueued</c>, <c>Succeeded</c> or <c>Failed</c>.</summary>
    [NotNull]
    public string Name { get; } = Name ?? throw new ArgumentNullException(nameof(Name));

    /// <summary>
    /// The state's data, by name (ignoring case), as the state serialized it: for example <c>SucceededAt</c> and
    /// <c>Result</c> for a succeeded job, <c>ExceptionType</c> and <c>ExceptionMessage</c> for a failed one.
    /// </summary>
    [NotNull]
    public IReadOnlyDictionary<string, string> Data { get; } = Data ?? throw new ArgumentNullException(nameof(Data));

    /// <summary>
    /// Gets whether the job is done: <c>Succeeded</c>, <c>Failed</c> (its retries are used up, since a job that will
    /// be retried is <c>Scheduled</c>) or <c>Deleted</c> (canceled, skipped or deleted). A failed job can still be
    /// requeued by hand. Custom states are never completed.
    /// </summary>
    public bool IsCompleted =>
        SucceededState.StateName.Equals(Name, StringComparison.OrdinalIgnoreCase) ||
        FailedState.StateName.Equals(Name, StringComparison.OrdinalIgnoreCase) ||
        DeletedState.StateName.Equals(Name, StringComparison.OrdinalIgnoreCase);
}
