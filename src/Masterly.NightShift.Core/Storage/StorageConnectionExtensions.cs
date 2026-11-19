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

using System.Globalization;

namespace Masterly.NightShift.Storage;

/// <summary>
/// Reads recurring jobs and takes job locks through a storage connection.
/// </summary>
#pragma warning disable CA1708 // False positive: the analyzer compares the types the compiler generates for extension blocks
public static class StorageConnectionExtensions
#pragma warning restore CA1708
{
    extension([NotNull] IStorageConnection connection)
    {
        public IDisposable AcquireDistributedJobLock(
            [NotNull] string jobId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(jobId);

            return connection.AcquireDistributedLock(
                $"job:{jobId}:state-lock",
                timeout);
        }
    }

    extension([NotNull] JobStorageTransaction transaction)
    {
        public void AcquireDistributedJobLock(
            [NotNull] string jobId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(jobId);

            transaction.AcquireDistributedLock($"job:{jobId}:state-lock", timeout);
        }
    }

    extension([NotNull] JobStorageConnection connection)
    {
        public long GetRecurringJobCount()
        {
            ArgumentNullException.ThrowIfNull(connection);
            return connection.GetSetCount("recurring-jobs");
        }

        public List<string> GetRecurringJobIds(
            int startingFrom,
            int endingAt)
        {
            ArgumentNullException.ThrowIfNull(connection);
            return connection.GetRangeFromSet("recurring-jobs", startingFrom, endingAt);
        }

        public List<RecurringJobDto> GetRecurringJobs(
            int startingFrom,
            int endingAt)
        {
            ArgumentNullException.ThrowIfNull(connection);

            List<string> ids = connection.GetRecurringJobIds(startingFrom, endingAt);
            return GetRecurringJobDtos(connection, ids);
        }
    }

    extension([NotNull] IStorageConnection connection)
    {
        public List<RecurringJobDto> GetRecurringJobs()
        {
            ArgumentNullException.ThrowIfNull(connection);

            HashSet<string> ids = connection.GetAllItemsFromSet("recurring-jobs");
            return GetRecurringJobDtos(connection, ids);
        }

        public List<RecurringJobDto> GetRecurringJobs(IEnumerable<string> ids)
        {
            ArgumentNullException.ThrowIfNull(connection);
            return GetRecurringJobDtos(connection, ids);
        }
    }

    private static List<RecurringJobDto> GetRecurringJobDtos(IStorageConnection connection, IEnumerable<string> ids)
    {
        List<RecurringJobDto> result = [];
        foreach (string id in ids)
        {
            Dictionary<string, string>? hash = connection.GetAllEntriesFromHash($"recurring-job:{id}");

            // TODO: Remove this in 2.0 (breaking change)
            if (hash == null)
            {
                result.Add(new RecurringJobDto { Id = id, Removed = true });
                continue;
            }

            RecurringJobDto dto = new() { Id = id };

            if (hash.TryGetValue("Cron", out string? cron) && !string.IsNullOrWhiteSpace(cron))
            {
                dto.Cron = cron;
            }

            try
            {
                if (hash.TryGetValue("Job", out string? payload) && !string.IsNullOrWhiteSpace(payload))
                {
                    InvocationData invocationData = InvocationData.DeserializePayload(payload);
                    dto.Job = invocationData.DeserializeJob();
                }
            }
            catch (JobLoadException ex)
            {
                dto.LoadException = ex;
            }

            if (hash.TryGetValue("NextExecution", out string? nextExecution))
            {
                dto.NextExecution = JobHelper.DeserializeNullableDateTime(nextExecution);
            }

            if (hash.TryGetValue("LastJobId", out string? lastJobId) && !string.IsNullOrWhiteSpace(lastJobId))
            {
                dto.LastJobId = lastJobId;

                StateData? stateData = connection.GetStateData(dto.LastJobId);
                if (stateData != null)
                {
                    dto.LastJobState = stateData.Name;
                }
            }

            if (hash.TryGetValue("Queue", out string? queue))
            {
                dto.Queue = queue;
            }

            if (hash.TryGetValue("LastExecution", out string? lastExecution))
            {
                dto.LastExecution = JobHelper.DeserializeNullableDateTime(lastExecution);
            }

            if (hash.TryGetValue("TimeZoneId", out string? timeZoneId))
            {
                dto.TimeZoneId = timeZoneId;
            }

            if (hash.TryGetValue("CreatedAt", out string? createdAt))
            {
                dto.CreatedAt = JobHelper.DeserializeNullableDateTime(createdAt);
            }

            if (hash.TryGetValue("Error", out string? error) && !string.IsNullOrEmpty(error))
            {
                dto.Error = error;
            }

            dto.RetryAttempt = hash.TryGetValue("RetryAttempt", out string? attemptString) &&
                int.TryParse(attemptString, out int retryAttempt)
                    ? retryAttempt
                    : 0;

            if (hash.TryGetValue("Paused", out string? paused) && paused == "1")
            {
                dto.Paused = true;
            }

            if (hash.TryGetValue("Misfire", out string? misfire) && int.TryParse(misfire, out int misfireValue) &&
                Enum.IsDefined(typeof(MisfireHandlingMode), misfireValue))
            {
                dto.MisfireHandling = (MisfireHandlingMode)misfireValue;
            }

            dto.StartAt = GetWindowValue(hash, RecurringJobEntity.StartAtField);
            dto.EndAt = GetWindowValue(hash, RecurringJobEntity.EndAtField);

            if (hash.TryGetValue(RecurringJobEntity.SkipAfterField, out string? skipAfter) &&
                long.TryParse(skipAfter, NumberStyles.Integer, CultureInfo.InvariantCulture, out long skipAfterSeconds) &&
                skipAfterSeconds > 0)
            {
                dto.SkipAfter = TimeSpan.FromSeconds(skipAfterSeconds);
            }

            // The name of the set that declared it, or the mark of an earlier build, which names no set
            if (hash.TryGetValue(RecurringJobSet.DeclaredField, out string? declared) && !string.IsNullOrEmpty(declared))
            {
                dto.Declared = true;
                dto.DeclaredBy = declared == RecurringJobSet.UnnamedMark ? null : declared;
            }

            if (hash.TryGetValue(RecurringJobEntity.PolicyField, out string? policy) && !string.IsNullOrWhiteSpace(policy))
            {
                dto.Policy = policy;
            }
            dto.Retired = hash.TryGetValue(RecurringJobSet.RetiredField, out string? retired) && retired == "1";

            result.Add(dto);
        }

        return result;
    }

    // A damaged value reads as no value here: the list must still show the job, and the scheduler reports it
    private static DateTime? GetWindowValue(Dictionary<string, string> hash, string field)
    {
        if (!hash.TryGetValue(field, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

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
