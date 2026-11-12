namespace Masterly.NightShift.Server;

/// <summary>
/// Keeps a job's console lists (<see cref="JobConsole.GetConsoleKey"/> and <see cref="JobConsole.GetCheckpointsKey"/>)
/// alive exactly as long as the job: they expire with the job when it reaches a final state (or a failed state
/// with a retention period, see <see cref="ExpireAfterAttribute.IncludeFailed"/>) and are persisted
/// again when it is requeued. Requeuing a succeeded or deleted job also clears its checkpoints, so
/// <see cref="JobConsoleExtensions.Resume"/> runs every step again; a failed job keeps them. Registered globally
/// by default.
/// </summary>
/// <remarks>
/// Only jobs whose console wrote to storage are touched (they carry the <see cref="JobConsole.MarkerParameter"/>
/// job parameter), so other jobs' state changes cost nothing extra. Storages without the
/// <see cref="JobStorageFeatures.ExtendedApi"/> feature keep the lists forever; nothing else breaks.
/// </remarks>
public sealed class JobConsoleStateFilter : JobFilterAttribute, IApplyStateFilter
{
    /// <inheritdoc />
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transaction);

        // ExpireList and PersistList throw NotSupportedException without the extended API, which would fail the transition
        if (transaction is not JobStorageTransaction storageTransaction || !context.Storage.HasFeature(JobStorageFeatures.ExtendedApi))
        {
            return;
        }

        bool hasConsole = HasConsole(context);

        // Consoles written by builds before the marker existed still go when the job is deleted
        if (!hasConsole && !DeletedState.StateName.Equals(context.NewState.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string jobId = context.BackgroundJob.Id;
        string consoleKey = JobConsole.GetConsoleKey(jobId);
        string checkpointsKey = JobConsole.GetCheckpointsKey(jobId);

        // Failed job retention expires a failed job too: its console goes with it
        if (context.JobExpires)
        {
            storageTransaction.ExpireList(consoleKey, context.JobExpirationTimeout);
            if (hasConsole)
            {
                storageTransaction.ExpireList(checkpointsKey, context.JobExpirationTimeout);
            }

            return;
        }

        if (IsFinished(context.OldStateName))
        {
            // A finished job runs again from the start: its steps are not done yet
            storageTransaction.InsertToList(checkpointsKey, JobConsole.CreateRestartMarker());
            storageTransaction.TrimList(checkpointsKey, 0, 0);
        }

        storageTransaction.PersistList(consoleKey);
        storageTransaction.PersistList(checkpointsKey);
    }

    /// <inheritdoc />
    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }

    private static bool HasConsole(ApplyStateContext context)
    {
        IReadOnlyDictionary<string, string>? parameters = context.BackgroundJob.ParametersSnapshot;

        // Without a snapshot there is no telling, so the lists follow the job as they always did
        if (parameters == null || parameters.ContainsKey(JobConsole.MarkerParameter))
        {
            return true;
        }

        // The performance that just ended wrote to its console
        return context.CustomData != null &&
               context.CustomData.TryGetValue(PerformContext.ConsoleItemKey, out object? console) &&
               console is JobConsole { HasStorageData: true };
    }

    private static bool IsFinished(string? stateName)
    {
        return SucceededState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase) ||
               DeletedState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase);
    }
}
