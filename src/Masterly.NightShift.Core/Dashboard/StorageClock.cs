namespace Masterly.NightShift.Storage;

/// <summary>
/// The clock that tells a live server from a stopped one. A storage with a clock of its own
/// (<see cref="JobStorageFeatures.Connection.GetUtcDateTime"/>) writes server heartbeats with it, so whoever
/// compares a heartbeat with a time must take the time from the same clock, or a process whose clock runs ahead
/// of the storage's finds every server silent, and one that runs behind doesn't notice a server that stopped.
/// </summary>
/// <remarks>
/// For the dashboard's <c>/health</c> and <c>/metrics</c>. The health check of Masterly.NightShift.AspNetCore
/// can't see this class, and reads the storage's clock the same way through the public API.
/// </remarks>
internal static class StorageClock
{
    /// <summary>
    /// The storage's time where the storage has a clock, read once on a read-only connection; the time of
    /// <paramref name="processClock"/> otherwise, without asking the storage.
    /// </summary>
    /// <param name="storage">The storage whose servers are judged.</param>
    /// <param name="processClock">This process's clock, for a storage without one.</param>
    /// <exception cref="ArgumentNullException">An argument is <c>null</c>.</exception>
    /// <exception cref="Exception">The storage has a clock, and reading it failed.</exception>
    public static DateTime UtcNow(JobStorage storage, Func<DateTime> processClock)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(processClock);

        if (storage.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime))
        {
            using IStorageConnection connection = storage.GetReadOnlyConnection();
            if (connection is JobStorageConnection storageConnection)
            {
                return storageConnection.GetUtcDateTime();
            }
        }

        return processClock();
    }
}
