namespace Masterly.NightShift.Storage.Shared;

/// <summary>The optional features the storages built on this layer support, and what a purge keeps.</summary>
internal static class StorageRules
{
    /// <summary>Every feature SQL Server supports, plus parameters set in a transaction and transactional acknowledgement.</summary>
    public static Dictionary<string, bool> Features(Type fetchedJobType)
    {
        return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [JobStorageFeatures.ExtendedApi] = true,
            [JobStorageFeatures.JobQueueProperty] = true,
            [JobStorageFeatures.Connection.BatchedGetFirstByLowest] = true,
            [JobStorageFeatures.Connection.GetUtcDateTime] = true,
            [JobStorageFeatures.Connection.GetSetContains] = true,
            [JobStorageFeatures.Connection.LimitedGetSetCount] = true,
            [JobStorageFeatures.Connection.GetQueueEntryId] = true,
            [JobStorageFeatures.Transaction.AcquireDistributedLock] = true,
            [JobStorageFeatures.Transaction.CreateJob] = false,
            [JobStorageFeatures.Transaction.SetJobParameter] = true,
            [JobStorageFeatures.Transaction.RemoveFromQueue(fetchedJobType)] = true,
            [JobStorageFeatures.Monitoring.DeletedStateGraphs] = true,
            [JobStorageFeatures.Monitoring.AwaitingJobs] = true,
            [JobStorageFeatures.Monitoring.QueueCounts] = true,
            [JobStorageFeatures.Purge] = true,
        };
    }

    /// <summary>The recurring job list survives a purge that keeps recurring jobs.</summary>
    public static bool PurgeKeepsSet(string key, StoragePurgeOptions options)
    {
        return options.KeepRecurringJobs && key == "recurring-jobs";
    }

    /// <summary>Recurring job definitions and drain mode survive a purge that keeps them.</summary>
    public static bool PurgeKeepsHash(string key, StoragePurgeOptions options)
    {
        return (options.KeepRecurringJobs && key.StartsWith("recurring-job:", StringComparison.Ordinal))
            || (options.KeepDrainMode && key == DrainMode.HashKey);
    }

    /// <summary>The audit log survives a purge that keeps it.</summary>
    public static bool PurgeKeepsList(string key, StoragePurgeOptions options)
    {
        return options.KeepAuditLog && key == AuditLog.ListKey;
    }
}
