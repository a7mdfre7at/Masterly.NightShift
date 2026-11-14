namespace Masterly.NightShift;

/// <summary>
/// The time an election filter judges a due job by. The delayed job scheduler decides that a job is due with the
/// storage's clock where the storage has one (<see cref="JobStorageFeatures.Connection.GetUtcDateTime"/>), so a
/// filter that puts the job back to the Scheduled state for a later time must judge with the same clock: with a
/// process clock behind the storage's, the time it chose would already be due, and the scheduler would take the
/// job again at once, over and over.
/// </summary>
internal static class StorageTime
{
    /// <summary>
    /// The storage's time, read on <paramref name="connection"/>, where the storage has a clock;
    /// <see cref="JobClock.UtcNow"/> otherwise, without asking the storage.
    /// </summary>
    /// <exception cref="Exception">The storage has a clock, and reading it failed.</exception>
    public static DateTime UtcNow([NotNull] JobStorage storage, [NotNull] IStorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(connection);

        return connection is JobStorageConnection storageConnection &&
            storage.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime)
            ? DateTime.SpecifyKind(storageConnection.GetUtcDateTime(), DateTimeKind.Utc)
            : JobClock.UtcNow;
    }
}
