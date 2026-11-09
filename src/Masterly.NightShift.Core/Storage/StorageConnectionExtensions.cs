// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

namespace Masterly.NightShift.Storage;

/// <summary>
/// Reads recurring jobs and takes job locks through a storage connection.
/// </summary>
public static class StorageConnectionExtensions
{
    public static IDisposable AcquireDistributedJobLock(
        [NotNull] this IStorageConnection connection,
        [NotNull] string jobId,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(jobId);

        return connection.AcquireDistributedLock(
            $"job:{jobId}:state-lock",
            timeout);
    }

    public static void AcquireDistributedJobLock(
        [NotNull] this JobStorageTransaction transaction,
        [NotNull] string jobId,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(jobId);

        transaction.AcquireDistributedLock($"job:{jobId}:state-lock", timeout);
    }

    public static long GetRecurringJobCount([NotNull] this JobStorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.GetSetCount("recurring-jobs");
    }

    public static List<string> GetRecurringJobIds(
        [NotNull] this JobStorageConnection connection,
        int startingFrom,
        int endingAt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.GetRangeFromSet("recurring-jobs", startingFrom, endingAt);
    }

    public static List<RecurringJobDto> GetRecurringJobs(
        [NotNull] this JobStorageConnection connection,
        int startingFrom,
        int endingAt)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var ids = connection.GetRecurringJobIds(startingFrom, endingAt);
        return GetRecurringJobDtos(connection, ids);
    }

    public static List<RecurringJobDto> GetRecurringJobs([NotNull] this IStorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var ids = connection.GetAllItemsFromSet("recurring-jobs");
        return GetRecurringJobDtos(connection, ids);
    }

    public static List<RecurringJobDto> GetRecurringJobs([NotNull] this IStorageConnection connection, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return GetRecurringJobDtos(connection, ids);
    }

    private static List<RecurringJobDto> GetRecurringJobDtos(IStorageConnection connection, IEnumerable<string> ids)
    {
        var result = new List<RecurringJobDto>();
        foreach (var id in ids)
        {
            var hash = connection.GetAllEntriesFromHash($"recurring-job:{id}");

            // TODO: Remove this in 2.0 (breaking change)
            if (hash == null)
            {
                result.Add(new RecurringJobDto { Id = id, Removed = true });
                continue;
            }

            var dto = new RecurringJobDto { Id = id };

            if (hash.TryGetValue("Cron", out var cron) && !string.IsNullOrWhiteSpace(cron))
            {
                dto.Cron = cron;
            }

            try
            {
                if (hash.TryGetValue("Job", out var payload) && !string.IsNullOrWhiteSpace(payload))
                {
                    var invocationData = InvocationData.DeserializePayload(payload);
                    dto.Job = invocationData.DeserializeJob();
                }
            }
            catch (JobLoadException ex)
            {
                dto.LoadException = ex;
            }

            if (hash.TryGetValue("NextExecution", out var nextExecution))
            {
                dto.NextExecution = JobHelper.DeserializeNullableDateTime(nextExecution);
            }

            if (hash.TryGetValue("LastJobId", out var lastJobId) && !string.IsNullOrWhiteSpace(lastJobId))
            {
                dto.LastJobId = lastJobId;

                var stateData = connection.GetStateData(dto.LastJobId);
                if (stateData != null)
                {
                    dto.LastJobState = stateData.Name;
                }
            }

            if (hash.TryGetValue("Queue", out var queue))
            {
                dto.Queue = queue;
            }

            if (hash.TryGetValue("LastExecution", out var lastExecution))
            {
                dto.LastExecution = JobHelper.DeserializeNullableDateTime(lastExecution);
            }

            if (hash.TryGetValue("TimeZoneId", out var timeZoneId))
            {
                dto.TimeZoneId = timeZoneId;
            }

            if (hash.TryGetValue("CreatedAt", out var createdAt))
            {
                dto.CreatedAt = JobHelper.DeserializeNullableDateTime(createdAt);
            }

            if (hash.TryGetValue("Error", out var error) && !string.IsNullOrEmpty(error))
            {
                dto.Error = error;
            }

            if (hash.TryGetValue("RetryAttempt", out var attemptString) &&
                int.TryParse(attemptString, out var retryAttempt))
            {
                dto.RetryAttempt = retryAttempt;
            }
            else
            {
                dto.RetryAttempt = 0;
            }

            if (hash.TryGetValue("Paused", out var paused) && paused == "1")
            {
                dto.Paused = true;
            }

            if (hash.TryGetValue("Misfire", out var misfire) && int.TryParse(misfire, out var misfireValue) &&
                Enum.IsDefined(typeof(MisfireHandlingMode), misfireValue))
            {
                dto.MisfireHandling = (MisfireHandlingMode)misfireValue;
            }

            dto.StartAt = GetWindowValue(hash, RecurringJobEntity.StartAtField);
            dto.EndAt = GetWindowValue(hash, RecurringJobEntity.EndAtField);

            result.Add(dto);
        }

        return result;
    }

    // A damaged value reads as no value here: the list must still show the job, and the scheduler reports it
    private static DateTime? GetWindowValue(Dictionary<string, string> hash, string field)
    {
        if (!hash.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value)) return null;

        try
        {
            return JobHelper.DeserializeDateTime(value);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
