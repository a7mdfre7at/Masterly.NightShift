using System.Diagnostics;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift;

/// <summary>
/// Changes the state of many jobs, a page at a time, until the jobs run out or a work or time limit is reached.
/// The dashboard's "Delete all", "Requeue all" and batch actions use it with the limits of one request; the
/// public bulk operations (<see cref="BatchJob.Cancel(string, TimeSpan?)"/>,
/// <see cref="JobStorageExtensions.RequeueFailedJobs(JobStorage, Func{FailedJobDto, bool}, TimeSpan?)"/>)
/// use it without a work limit.
/// </summary>
internal static class BulkStateChange
{
    /// <summary>How many jobs one page holds.</summary>
    internal const int PageSize = 500;

    /// <summary>No limit on the pages read.</summary>
    internal const int Unlimited = int.MaxValue;

    /// <summary>
    /// Changes every job of a list. <paramref name="page"/> returns the ids of a page, each with whether the job
    /// should be changed (still in the list's state, and selected); the others are skipped. Changed jobs usually
    /// leave the list, so the same page is read again until nothing on it changes, then the next one.
    /// </summary>
    /// <param name="change">Changes a job's state, and returns whether it did.</param>
    /// <param name="page">Reads a page: the index of its first job and the page size.</param>
    /// <param name="count">Counts the list when the limits stop the work early, or <c>null</c>.</param>
    /// <param name="maxReads">How many pages may be read at most, or <see cref="Unlimited"/>.</param>
    /// <param name="timeBudget">How long the work may take, or <c>null</c> for no limit.</param>
    public static BulkJobResult RunOnPages(
        Func<string, bool> change,
        Func<int, int, IEnumerable<KeyValuePair<string, bool>>?> page,
        Func<long>? count,
        int maxReads,
        TimeSpan? timeBudget)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        int from = 0;
        int processed = 0;
        bool complete = false;
        bool outOfTime = false;
        Stopwatch elapsed = Stopwatch.StartNew();

        for (int read = 0; read < maxReads && !outOfTime; read++)
        {
            List<KeyValuePair<string, bool>> items = [.. (page(from, PageSize) ?? []).Where(static item => item.Key != null)];

            if (items.Count == 0)
            {
                complete = true;
                break;
            }

            int changedOnThisPage = 0;

            foreach (KeyValuePair<string, bool> item in items)
            {
                // Seen before: changed by this call, or skipped because it wasn't in the list's state
                if (!seen.Add(item.Key) || !item.Value)
                {
                    continue;
                }

                if (change(item.Key))
                {
                    changedOnThisPage++;
                }

                if (IsOutOfTime(elapsed, timeBudget))
                {
                    outOfTime = true;
                    break;
                }
            }

            processed += changedOnThisPage;
            if (outOfTime)
            {
                break;
            }

            if (changedOnThisPage > 0)
            {
                // Jobs that changed state usually leave the list, so the same page now holds new ones
                continue;
            }

            // Nothing on this page left the list. On SQL Server, for example, an enqueued job keeps its queue
            // row until a worker fetches it, even after it is deleted. Move past these jobs.
            if (items.Count < PageSize)
            {
                complete = true;
                break;
            }

            from += items.Count;
        }

        long? remaining = complete ? 0 : null;
        if (!complete && count != null)
        {
            remaining = Math.Max(0, count());
        }

        return new BulkJobResult(processed, remaining, complete);
    }

    /// <summary>
    /// Changes each id of a list read beforehand, until the list ends or a limit is reached.
    /// <see cref="BulkJobResult.Remaining"/> is then the number of ids not reached.
    /// </summary>
    public static BulkJobResult RunOnIds(IReadOnlyList<string> ids, Func<string, bool> change, int maxReads, TimeSpan? timeBudget)
    {
        int processed = 0;
        long limit = MaxJobs(maxReads);
        Stopwatch elapsed = Stopwatch.StartNew();

        for (int i = 0; i < ids.Count; i++)
        {
            if (change(ids[i]))
            {
                processed++;
            }

            int done = i + 1;
            if (done < ids.Count && (done >= limit || IsOutOfTime(elapsed, timeBudget)))
            {
                return new BulkJobResult(processed, ids.Count - done, Complete: false);
            }
        }

        return new BulkJobResult(processed, 0, Complete: true);
    }

    /// <summary>
    /// Changes the jobs of a batch whose current state <paramref name="select"/> accepts, reading their states
    /// a page at a time. <paramref name="action"/> gets the state that was read, so it can expect it.
    /// </summary>
    /// <returns>The result, or <c>null</c> when the batch doesn't exist or expired.</returns>
    public static BulkJobResult? RunOnBatch(
        JobStorage storage,
        IBackgroundJobClient client,
        string batchId,
        Func<string?, bool> select,
        Func<IBackgroundJobClient, string, string?, bool> action,
        int maxReads,
        TimeSpan? timeBudget)
    {
        IReadOnlyList<string> jobIds;

        using (IStorageConnection connection = storage.GetReadOnlyConnection())
        {
            if (connection.GetAllEntriesFromHash(BatchJob.GetHashKey(batchId)) == null)
            {
                return null;
            }

            jobIds = BatchJob.GetJobIds(connection, batchId);
        }

        long limit = MaxJobs(maxReads);
        Stopwatch elapsed = Stopwatch.StartNew();
        int processed = 0;

        for (int from = 0; from < jobIds.Count; from += PageSize)
        {
            List<string> chunk = [.. jobIds.Skip(from).Take(PageSize)];
            List<KeyValuePair<string, string?>?> states = new(chunk.Count);

            using (IStorageConnection connection = storage.GetReadOnlyConnection())
            {
                foreach (string jobId in chunk)
                {
                    JobData? data = connection.GetJobData(jobId);
                    states.Add(data != null && select(data.State) ? new KeyValuePair<string, string?>(jobId, data.State) : null);
                }
            }

            for (int i = 0; i < chunk.Count; i++)
            {
                KeyValuePair<string, string?>? job = states[i];
                if (job.HasValue && action(client, job.Value.Key, job.Value.Value))
                {
                    processed++;
                }

                // Jobs already handled aren't selected again, so another call goes on where this one stopped
                int done = from + i + 1;
                if (done < jobIds.Count && (done >= limit || IsOutOfTime(elapsed, timeBudget)))
                {
                    return new BulkJobResult(processed, jobIds.Count - done, Complete: false);
                }
            }
        }

        return new BulkJobResult(processed, 0, Complete: true);
    }

    private static long MaxJobs(int maxReads)
    {
        return maxReads == Unlimited ? long.MaxValue : (long)PageSize * maxReads;
    }

    private static bool IsOutOfTime(Stopwatch elapsed, TimeSpan? timeBudget)
    {
        return timeBudget.HasValue && elapsed.Elapsed >= timeBudget.Value;
    }
}
